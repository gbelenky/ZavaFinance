#Requires -Version 7.0
Set-StrictMode -Version Latest

function Assert-EstateSeparatePath {
    param([string]$Path, [string]$PackagePath)
    $package = [IO.Path]::GetFullPath($PackagePath).TrimEnd('\','/')
    $candidate = [IO.Path]::GetFullPath($Path)
    if ($candidate -eq $package -or $candidate.StartsWith($package + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Receipts and readbacks must be outside the immutable export package.'
    }
}

function Read-EstateSource {
    param([string]$Path)
    $config = Read-EstateJson $Path
    if ($config.version -ne 1) { throw 'Unsupported source configuration version.' }
    Assert-EstateGuid $config.tenantId 'source tenantId'
    if (!$config.workspaces -or $config.workspaces.Count -eq 0) { throw 'Select at least one source workspace.' }
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($workspace in $config.workspaces) {
        Assert-EstateGuid $workspace.id 'source workspace id'
        if (!$ids.Add($workspace.id)) { throw 'Duplicate source workspace.' }
        foreach ($lakehouse in $workspace.lakehouses) {
            Assert-EstateGuid $lakehouse.id 'source lakehouse id'
            if (!$ids.Add($lakehouse.id)) { throw 'Duplicate source lakehouse.' }
            Assert-EstateSqlEndpoint $lakehouse
        }
    }
    return $config
}

function Assert-EstateSqlEndpoint {
    param($Lakehouse)
    if ($Lakehouse.sqlServer -notmatch '^[a-zA-Z0-9.-]+\.datawarehouse\.fabric\.microsoft\.com$' -or
        [string]::IsNullOrWhiteSpace($Lakehouse.sqlDatabase)) {
        throw 'Supply the actual Fabric SQL hostname and database for every lakehouse.'
    }
    if (!$Lakehouse.Contains('requireZavaContract') -or $Lakehouse.requireZavaContract -isnot [bool]) {
        throw 'Each lakehouse must explicitly set requireZavaContract to true or false.'
    }
}

function Get-EstateDefaultSchema {
    param($Properties)
    if ($Properties.Contains('defaultSchema') -and $Properties.defaultSchema) { return [string]$Properties.defaultSchema }
    return ''
}

function Assert-EstateLakehouseEndpoint {
    param($Item, $Settings)
    $sql = $Item.properties.sqlEndpointProperties
    if ($sql.provisioningStatus -ne 'Success' -or $sql.connectionString -ne $Settings.sqlServer -or
        $Settings.sqlDatabase -notin $Item.displayName,$sql.id) {
        throw 'Configured SQL hostname/database does not match the provisioned Lakehouse endpoint; use its actual database name or SQL endpoint ID.'
    }
    if ($Settings.requireZavaContract -and (Get-EstateDefaultSchema $Item.properties)) {
        throw 'Zava currently requires schema-disabled Lakehouses (Tables/<name>); schema-enabled targets need an application/publisher migration first.'
    }
}

function Assert-EstateDeltaTables {
    param($Lakehouse)
    $enabled = [bool](Get-EstateDefaultSchema $Lakehouse.properties)
    $prefixes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $Lakehouse.areas.Tables) {
        if ($entry.kind -ne 'directory' -or !$entry.path.EndsWith('/_delta_log')) { continue }
        $prefix = $entry.path.Substring(0, $entry.path.Length - '/_delta_log'.Length)
        $segments = $prefix.Split('/')
        if ($segments.Count -ne $(if ($enabled) {2} else {1})) { throw 'Unexpected Delta table directory layout for the Lakehouse schema mode.' }
        [void]$prefixes.Add($prefix)
    }
    foreach ($table in $Lakehouse.sqlSnapshot.tables) {
        if (!$enabled -and $table.schemaName -ne 'dbo') { throw 'Non-dbo SQL table in a schema-disabled Lakehouse requires explicit migration handling.' }
        $prefix = if ($enabled) {"$($table.schemaName)/$($table.tableName)"} else {[string]$table.tableName}
        if (!$prefixes.Remove($prefix)) { throw "SQL table has no matching physical Delta log: $prefix" }
    }
    if ($prefixes.Count) { throw 'Physical Delta tables are missing from the SQL snapshot. Wait for synchronization and verify full metadata visibility before exporting.' }
}

function Add-EstateSqlBindings {
    param([System.Collections.IDictionary]$Bindings, $Source, $Target, $Settings)
    $sourceSqlId = $Source.lakehouse.properties.sqlEndpointProperties.id
    $targetSqlId = $Target.properties.sqlEndpointProperties.id
    Assert-EstateGuid $sourceSqlId 'source SQL endpoint ID'
    Assert-EstateGuid $targetSqlId 'target SQL endpoint ID'
    if ($sourceSqlId -eq $targetSqlId -or $Source.lakehouse.sqlServer -eq $Settings.sqlServer) {
        throw 'Target SQL endpoint must be independent of the source.'
    }
    foreach ($pair in @(@{source=$sourceSqlId;target=$targetSqlId},@{source=$Source.lakehouse.sqlServer;target=$Settings.sqlServer})) {
        if ($Bindings.Contains($pair.source) -and $Bindings[$pair.source] -ne $pair.target) { throw 'Conflicting SQL endpoint binding.' }
        $Bindings[$pair.source] = $pair.target
    }
}

function Read-EstatePackage {
    param([string]$PackagePath, [string]$ManifestSha256)
    $manifestPath = Join-Path $PackagePath 'manifest.json'
    if ($ManifestSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Provide the SHA-256 of manifest.json from the approved export record.' }
    if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ne $ManifestSha256) {
        throw 'Manifest hash differs from the approved export record.'
    }
    $manifest = Read-EstateJson $manifestPath
    if ($manifest.version -ne 1 -or $manifest.status -ne 'complete') { throw 'This export is not a completed, supported migration package.' }
    if ($manifest.blockers.Count) { throw 'This export has unresolved inventory blockers.' }
    Assert-EstateGuid $manifest.sourceTenantId 'manifest source tenant'
    Assert-EstateFiles (Join-Path $PackagePath 'payload') $manifest.files
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $manifest.items) {
        Assert-EstateGuid $item.id 'manifest item'
        Assert-EstateGuid $item.workspaceId 'manifest workspace'
        if (!$ids.Add($item.id)) { throw 'Duplicate manifest item ID.' }
        if ($item.type -notin 'Lakehouse','Notebook','DataAgent') { throw "Unsupported manifest item type: $($item.type)" }
        [void](Resolve-EstatePath (Join-Path $PackagePath 'payload') $item.definitionPath)
        if (!(Test-Path -LiteralPath (Resolve-EstatePath (Join-Path $PackagePath 'payload') "$($item.definitionPath)/definition.json"))) {
            throw "Definition missing for $($item.id)."
        }
    }
    [void]@(Get-EstateItemOrder $manifest.items)
    return $manifest
}

function Read-EstateTarget {
    param([string]$Path, $Manifest)
    $target = Read-EstateJson $Path
    if ($target.version -ne 1) { throw 'Unsupported target map version.' }
    Assert-EstateGuid $target.tenantId 'target tenantId'
    $workspaceMap = @{}
    $destinationIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $sourceWorkspaceIds = @($Manifest.workspaces | ForEach-Object { $_.id } | Sort-Object -Unique)
    foreach ($workspace in $target.workspaces) {
        Assert-EstateGuid $workspace.sourceId 'workspace sourceId'
        Assert-EstateGuid $workspace.targetId 'workspace targetId'
        if ($workspaceMap.ContainsKey($workspace.sourceId) -or !$destinationIds.Add($workspace.targetId)) {
            throw 'Workspace mapping must be one-to-one.'
        }
        if ($workspace.targetId -in $sourceWorkspaceIds) { throw 'A source workspace cannot be an import destination.' }
        $workspaceMap[$workspace.sourceId] = $workspace.targetId
    }
    if ($workspaceMap.Count -ne $sourceWorkspaceIds.Count -or @($sourceWorkspaceIds | Where-Object { !$workspaceMap.ContainsKey($_) }).Count) {
        throw 'Map every exported workspace exactly once, without extra workspace mappings.'
    }
    $itemMap = @{}
    foreach ($item in $target.items) {
        Assert-EstateGuid $item.sourceId 'item sourceId'
        if ($itemMap.ContainsKey($item.sourceId)) { throw 'Duplicate target item mapping.' }
        if ($item.displayName -notmatch '^[a-zA-Z0-9][a-zA-Z0-9 _.-]{0,127}$' -or $item.displayName -match '[. ]$') {
            throw 'Use simple target item names (letters, digits, spaces, underscores, dots and hyphens; no trailing dot/space).'
        }
        $source = @($Manifest.items | Where-Object { $_.id -eq $item.sourceId })
        if ($source.Count -ne 1) { throw "Unknown target sourceId: $($item.sourceId)" }
        if ($source[0].type -eq 'Lakehouse') {
            Assert-EstateGuid $item.targetId 'precreated empty lakehouse targetId'
            if ($item.targetId -in @($Manifest.items | ForEach-Object { $_.id }) -or !$destinationIds.Add($item.targetId)) {
                throw 'Lakehouse destinations must be unique and not source items.'
            }
            Assert-EstateSqlEndpoint $item
        } elseif ($item.Contains('targetId')) { throw 'Non-lakehouse target IDs are created by the importer, not supplied.' }
        $itemMap[$item.sourceId] = $item
    }
    if ($itemMap.Count -ne $Manifest.items.Count) { throw 'Map every exported item exactly once.' }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $Manifest.items) {
        if (!$names.Add("$($workspaceMap[$item.workspaceId])/$($itemMap[$item.id].displayName).$($item.type)")) {
            throw 'Two source items map to the same target item name.'
        }
    }
    $bindingSources = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($binding in $target.bindings) {
        if ([string]::IsNullOrWhiteSpace($binding.source) -or [string]::IsNullOrWhiteSpace($binding.target) -or
            $binding.source.Length -lt 8 -or $binding.source -eq $binding.target) {
            throw 'Bindings need distinct nonempty source/target strings, source at least eight characters.'
        }
        if (!$bindingSources.Add($binding.source)) { throw 'Duplicate manual binding source.' }
        if ($binding.source -in @($Manifest.items | ForEach-Object { $_.id }) -or $binding.source -in $sourceWorkspaceIds) {
            throw 'Workspace/item ID bindings are generated automatically; do not override them.'
        }
    }
    return @{ config = $target; workspaces = $workspaceMap; items = $itemMap }
}

function Get-EstateItemOrder {
    param([array]$Items)
    $remaining = [Collections.Generic.List[object]]::new()
    foreach ($item in $Items) { $remaining.Add($item) }
    $done = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $allIds = @($Items | ForEach-Object { $_.id })
    while ($remaining.Count) {
        $next = @($remaining | Where-Object {
            @($_.dependencies | Where-Object { $_ -notin $allIds -or !$done.Contains($_) }).Count -eq 0
        } | Sort-Object @{Expression={ if ($_.type -eq 'Lakehouse') { 0 } else { 1 } }}, id | Select-Object -First 1)
        if (!$next.Count) { throw 'Cyclic or out-of-scope item dependencies require explicit migration handling.' }
        $item = $next[0]
        [void]$done.Add($item.id)
        [void]$remaining.Remove($item)
        $item
    }
}

function Assert-EstateDefinitionSafe {
    param([string]$Text)
    if ($Text -match '(?i)"(?:password|client[_-]?secret|account[_-]?key|api[_-]?key|access[_-]?token|refresh[_-]?token)"\s*:\s*"(?!\s*")[^"]+"' -or
        $Text -match '(?i)(?:AccountKey=|SharedAccessSignature=|[?&]sig=[a-zA-Z0-9%+/]{16,}|Bearer\s+eyJ)' -or
        $Text -match '-----BEGIN (?:RSA |EC )?PRIVATE KEY-----') {
        throw 'Definition may contain credentials. Remove embedded credentials in the source and approve the content before exporting.'
    }
}

function Convert-EstateDefinitionText {
    param([string]$Text, [System.Collections.IDictionary]$Bindings, [string[]]$Forbidden)
    Assert-EstateDefinitionSafe $Text
    # One pass prevents cascading replacements (A->B followed by B->C).
    $keys = @($Bindings.Keys | Sort-Object Length -Descending)
    if ($keys.Count) {
        $pattern = ($keys | ForEach-Object { [regex]::Escape($_) }) -join '|'
        $Text = [regex]::Replace($Text, $pattern, [Text.RegularExpressions.MatchEvaluator]{
            param($match)
            [string]$Bindings[$match.Value]
        }, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
    foreach ($value in $Forbidden) {
        if ($value -and $Text.Contains($value, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unmapped source reference remains in an item definition: $value"
        }
    }
    Assert-EstateDefinitionSafe $Text
    return $Text
}

function Get-EstateDefinitionParts {
    param([string]$Directory)
    $definition = Read-EstateJson (Join-Path $Directory 'definition.json')
    if (!$definition.Contains('parts') -or $definition.parts -isnot [array]) { throw 'Invalid Fabric definition: expected a parts array.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $parts = foreach ($part in $definition.parts) {
        [void](Resolve-EstatePath $Directory $part.path)
        if (!$seen.Add($part.path)) { throw 'Duplicate definition part path.' }
        if ($part.payloadType -ne 'InlineBase64') { throw 'Only self-contained InlineBase64 definitions can be migrated.' }
        try {
            $text = [Text.UTF8Encoding]::new($false,$true).GetString([Convert]::FromBase64String($part.payload))
        } catch [FormatException] { throw "Invalid Base64 definition part: $($part.path)" }
        catch [Text.DecoderFallbackException] { throw "Binary definition part needs a type-specific migration: $($part.path)" }
        Assert-EstateDefinitionSafe $text
        if ([IO.Path]::GetExtension($part.path) -in '.json','.ipynb') {
            $text = ConvertTo-EstateCanonicalText $text
            Assert-EstateDefinitionSafe $text
        }
        @{path=$part.path;text=$text}
    }
    return @($parts)
}

function Get-EstateDefinitionText {
    param([string]$Directory)
    return (@(Get-EstateDefinitionParts $Directory | ForEach-Object { $_.text }) -join "`n")
}

function Copy-EstateDefinitionWithBindings {
    param([string]$Source, [string]$Destination, [System.Collections.IDictionary]$Bindings, [string[]]$Forbidden)
    $parts = @(Get-EstateDefinitionParts $Source)
    $result = foreach ($part in $parts) {
        # .platform is source-control identity, not a runtime dependency. Fabric creates the new item identity.
        if ($part.path -eq '.platform') { continue }
        $text = Convert-EstateDefinitionText $part.text $Bindings $Forbidden
        if ([IO.Path]::GetExtension($part.path) -in '.json','.ipynb') { [void](ConvertTo-EstateCanonicalText $text) }
        @{path=$part.path;payloadType='InlineBase64';payload=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text))}
    }
    [void](New-Item -ItemType Directory -Path $Destination)
    Write-EstateJson (Join-Path $Destination 'definition.json') @{parts=@($result)}
}

function ConvertTo-EstateCanonicalJson {
    param($Value)
    function Sort-Value($InputValue) {
        if ($InputValue -is [System.Collections.IDictionary]) {
            $result = [ordered]@{}
            foreach ($key in @($InputValue.Keys | Sort-Object -CaseSensitive)) { $result[$key] = Sort-Value $InputValue[$key] }
            return $result
        }
        if ($InputValue -is [array]) {
            return ,@($InputValue | ForEach-Object { Sort-Value $_ })
        }
        return $InputValue
    }
    ConvertTo-Json -InputObject (Sort-Value $Value) -Depth 100 -Compress
}

function ConvertTo-EstateCanonicalText {
    param([string]$Text)
    function Convert-Element([System.Text.Json.JsonElement]$Element) {
        switch ($Element.ValueKind.ToString()) {
            'Object' {
                $parts = @($Element.EnumerateObject() | Sort-Object Name -CaseSensitive | ForEach-Object {
                    $name = [System.Text.Json.JsonSerializer]::Serialize([string]$_.Name, [System.Text.Json.JsonSerializerOptions]::new())
                    $name + ':' + (Convert-Element $_.Value)
                })
                return '{' + ($parts -join ',') + '}'
            }
            'Array' { return '[' + (@($Element.EnumerateArray() | ForEach-Object { Convert-Element $_ }) -join ',') + ']' }
            'String' { return [System.Text.Json.JsonSerializer]::Serialize($Element.GetString(), [System.Text.Json.JsonSerializerOptions]::new()) }
            default { return $Element.GetRawText() }
        }
    }
    $document = [System.Text.Json.JsonDocument]::Parse($Text)
    try { Convert-Element $document.RootElement } finally { $document.Dispose() }
}

function Assert-EstateImportedDefinition {
    param($Item, $Manifest, $Mapping, $Receipt, [string]$PackagePath, [string]$ActualDirectory, $TargetInventory)
    $bindings = @{}
    foreach ($key in $Mapping.workspaces.Keys) { $bindings[$key] = $Mapping.workspaces[$key] }
    foreach ($key in $Receipt.items.Keys) { $bindings[$key] = $Receipt.items[$key].targetId }
    foreach ($binding in $Mapping.config.bindings) { $bindings[$binding.source] = $binding.target }
    foreach ($lakehouse in @($Manifest.items | Where-Object { $_.type -eq 'Lakehouse' })) {
        $record = $Receipt.items[$lakehouse.id]
        $actualLakehouse = @($TargetInventory[$record.workspaceId].items | Where-Object { $_.id -eq $record.targetId })
        if ($actualLakehouse.Count -ne 1) { throw 'Target Lakehouse missing during definition reconciliation.' }
        Add-EstateSqlBindings $bindings $lakehouse $actualLakehouse[0] $Mapping.items[$lakehouse.id]
    }
    $source = Resolve-EstatePath (Join-Path $PackagePath 'payload') $Item.definitionPath
    $expected = @(Get-EstateDefinitionParts $source | Where-Object { $_.path -ne '.platform' })
    $actual = @(Get-EstateDefinitionParts $ActualDirectory | Where-Object { $_.path -ne '.platform' })
    if ($expected.Count -ne $actual.Count) { throw 'Imported definition part count differs.' }
    $forbidden = @($Manifest.items | ForEach-Object { $_.id }) + @($Mapping.workspaces.Keys) + @($Mapping.config.bindings | ForEach-Object { $_.source })
    foreach ($part in $expected) {
        $match = @($actual | Where-Object { $_.path -ceq $part.path })
        if ($match.Count -ne 1) { throw "Imported definition part missing: $($part.path)" }
        $expectedText = Convert-EstateDefinitionText $part.text $bindings $forbidden
        [void](Convert-EstateDefinitionText $match[0].text @{} $forbidden)
        $actualText = $match[0].text
        if ([IO.Path]::GetExtension($part.path) -in '.json','.ipynb') {
            $expectedText = ConvertTo-EstateCanonicalText $expectedText
            $actualText = ConvertTo-EstateCanonicalText $actualText
        }
        if ($expectedText -cne $actualText) { throw "Imported definition differs after rebinding: $($part.path). Review the service's normalization; no drift is silently accepted." }
    }
}
