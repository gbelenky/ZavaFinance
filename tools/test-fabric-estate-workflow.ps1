#Requires -Version 7.6
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
foreach ($helper in 'Fabric-Estate','Fabric-EstatePackage','Fabric-EstateFabric','Fabric-EstateSql','Fabric-EstateWorkflow') {
    . (Join-Path $PSScriptRoot "..\scripts\$helper.ps1")
}
$script:passed = 0
function Check([string]$Name, [scriptblock]$Action) { & $Action; $script:passed++; Write-Host "PASS $Name" }
function Assert([bool]$Condition) { if (!$Condition) { throw 'Assertion failed' } }
function Fails([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch { if ($_.Exception.Message -notmatch $Pattern) { throw }; return }
    throw "Expected error: $Pattern"
}
$root = Join-Path ([IO.Path]::GetTempPath()) ("zava-estate-flow-" + [guid]::NewGuid())
[void](New-Item -ItemType Directory -Path $root)
$tenant = '11111111-1111-1111-1111-111111111111'
$sourceWorkspace = '22222222-2222-2222-2222-222222222222'
$sourceLakehouse = '33333333-3333-3333-3333-333333333333'
$targetWorkspace = '44444444-4444-4444-4444-444444444444'
$targetLakehouse = '55555555-5555-5555-5555-555555555555'
$notebook = '66666666-6666-6666-6666-666666666666'
$createdNotebook = '77777777-7777-7777-7777-777777777777'
$script:mutations = 0
$script:sourceChanged = $false
$script:sqlChanged = $false
$script:failCopy = $false
$script:extraType = ''
$script:hasShortcut = $false
$script:hasSchedule = $false
$script:tenantMismatch = $false
$script:changeDuringDownload = $false
$script:changeSqlDuringDownload = $false
$script:schemaChanged = $false
$script:definitions = @{}
$script:created = @()
$sourceConfig = Join-Path $root 'source.json'
$targetMap = Join-Path $root 'target.json'
$package = Join-Path $root 'export'
$receiptPath = Join-Path $root 'receipt.json'
function Assert-EstateTenant($TenantId) { if ($script:tenantMismatch) { throw 'Tenant mismatch' }; Assert ($TenantId -eq $tenant) }
function Assert-EstateFabricAuth($TenantId) { Assert ($TenantId -eq $tenant) }
function Get-EstateWorkspaceInventory($WorkspaceId) {
    $source = $WorkspaceId -eq $sourceWorkspace
    $lh = if ($source) {$sourceLakehouse} else {$targetLakehouse}
    $sqlId = if ($source) {'99999999-9999-9999-9999-999999999999'} else {'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'}
    $items = @(@{id=$lh;type='Lakehouse';displayName='Finance';properties=@{defaultSchema=$(if (!$source -and $script:schemaChanged) {'dbo'} else {$null})
        sqlEndpointProperties=@{id=$sqlId;provisioningStatus='Success';connectionString="$(if ($source) {'source'} else {'target'}).datawarehouse.fabric.microsoft.com"}}})
    if ($source) { $items += @{id=$notebook;type='Notebook';displayName='Publisher'} } else { $items += $script:created }
    if ($source -and $script:extraType) { $items += @{id='88888888-8888-8888-8888-888888888888';type=$script:extraType;displayName='Unsupported'} }
    return [ordered]@{id=$WorkspaceId;items=$items;lakehouseSqlEndpointIds=@()
        shortcuts=@{$lh=$(if ($source -and $script:hasShortcut) {@(@{name='external'})} else {@()})}
        schedules=@{$lh=@();$notebook=$(if ($source -and $script:hasSchedule) {@(@{enabled=$true})} else {@()})}
        roleAssignments=@();folders=@()}
}
function Get-MockDataPath($Uri) {
    $isSource = $Uri.Contains($sourceWorkspace)
    Join-Path $root "$(if ($isSource) {'source-data'} else {'target-data'})\$($Uri.Split('/')[-1])"
}
function Get-EstateRemoteFiles($Uri, $TenantId) {
    $files = @(Get-EstateFiles (Get-MockDataPath $Uri))
    foreach ($entry in $files) {
        $entry['modified'] = if ($entry.kind -eq 'directory') {$null} elseif ($script:sourceChanged -and $Uri.Contains($sourceWorkspace)) {'changed'} else {'2026-09-30'}
        $entry
    }
}
function Copy-EstateData($Source, $Destination, $TenantId) {
    if ($Source.StartsWith('https:')) {
        $Source = Get-MockDataPath $Source
        if ($script:changeDuringDownload) { $script:sourceChanged=$true }
        if ($script:changeSqlDuringDownload) { $script:sqlChanged=$true }
    }
    if ($Destination.StartsWith('https:')) {
        $Destination = Get-MockDataPath $Destination
        $script:mutations++
        if ($script:failCopy) { throw 'Simulated partial transfer' }
    }
    foreach ($entry in @(Get-EstateFiles $Source)) {
        $target = Resolve-EstatePath $Destination $entry.path
        if ($entry.kind -eq 'directory') { [void](New-Item -ItemType Directory -Path $target -Force); continue }
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force)
        if (Test-Path -LiteralPath $target) { throw 'Mock refuses overwrite' }
        [IO.File]::Copy((Resolve-EstatePath $Source $entry.path), $target)
    }
}
function Export-EstateDefinition($WorkspaceId, $ItemId, $Type, $Directory) {
    [void](New-Item -ItemType Directory -Path $Directory)
    $text = if ($script:definitions.ContainsKey($ItemId)) {$script:definitions[$ItemId]} else {'{"parts":[]}'}
    [IO.File]::WriteAllText((Join-Path $Directory 'definition.json'),$text)
}
function Import-EstateDefinition($WorkspaceId, $DisplayName, $Type, $Directory, $Description) {
    $script:mutations++
    $script:definitions[$createdNotebook] = [IO.File]::ReadAllText((Join-Path $Directory 'definition.json'))
    $script:created += @{id=$createdNotebook;displayName=$DisplayName;type=$Type}
    $createdNotebook
}
function Get-FabricEstateSqlSnapshot {
    param($Server,$Database,$SqlClientDirectory,[switch]$RequireZavaContract)
    @{sqlObjects=@();tables=@(@{schemaName='dbo';tableName='fact';rowCount=$(if ($script:sqlChanged) {'2'} else {'1'})});columns=@();financial=@();resolverRelease=@();permissions=@();principals=@();policies=@()}
}
try {
    foreach ($location in 'source-data','target-data') {
        foreach ($area in 'Tables','Files') { [void](New-Item -ItemType Directory -Path (Join-Path $root "$location\$area") -Force) }
    }
    [void](New-Item -ItemType Directory -Path (Join-Path $root 'source-data\Tables\fact\_delta_log') -Force)
    [void](New-Item -ItemType Directory -Path (Join-Path $root 'source-data\Files\empty-folder') -Force)
    [IO.File]::WriteAllText((Join-Path $root 'source-data\Tables\fact\_delta_log\00000000000000000000.json'), '{"commitInfo":{"version":0}}')
    [IO.File]::WriteAllBytes((Join-Path $root 'source-data\Tables\fact\part.parquet'), [byte[]](0,1,2,3,255))
    $notebookText = "workspace=$sourceWorkspace lakehouse=$sourceLakehouse"
    $metadataText = '{"dependency":"\u0033' + $sourceLakehouse.Substring(1) + '","reference":"reference-source"}'
    $script:definitions[$notebook] = @{parts=@(
        @{path='notebook-content.py';payloadType='InlineBase64';payload=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($notebookText))}
        @{path='metadata.json';payloadType='InlineBase64';payload=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($metadataText))}
    )}|ConvertTo-Json -Depth 10
    Write-EstateJson $sourceConfig @{version=1;tenantId=$tenant;workspaces=@(@{id=$sourceWorkspace;lakehouses=@(@{
        id=$sourceLakehouse;sqlServer='source.datawarehouse.fabric.microsoft.com';sqlDatabase='finance';requireZavaContract=$false
    })})}
    Write-EstateJson $targetMap @{version=1;tenantId=$tenant;workspaces=@(@{sourceId=$sourceWorkspace;targetId=$targetWorkspace})
        items=@(@{sourceId=$sourceLakehouse;targetId=$targetLakehouse;displayName='Finance';sqlServer='target.datawarehouse.fabric.microsoft.com';sqlDatabase='finance';requireZavaContract=$false}
            @{sourceId=$notebook;displayName='Publisher'})
        bindings=@()}
    Check 'Inventory-only does not transfer or mutate; cannot import' {
        $result = Export-FabricEstate -SourceConfig $sourceConfig -OutputPath (Join-Path $root 'inventory') -InventoryOnly
        Assert ($result.status -eq 'inventoryOnly' -and $script:mutations -eq 0)
    }
    Check 'Unknown item, shortcut and schedule inventories block full export' {
        $script:extraType='Warehouse'
        Fails { Export-FabricEstate -SourceConfig $sourceConfig -OutputPath (Join-Path $root 'unsupported') -InventoryOnly } 'blockers'
        $script:extraType='';$script:hasShortcut=$true
        Fails { Export-FabricEstate -SourceConfig $sourceConfig -OutputPath (Join-Path $root 'shortcut') -InventoryOnly } 'blockers'
        $script:hasShortcut=$false;$script:hasSchedule=$true
        Fails { Export-FabricEstate -SourceConfig $sourceConfig -OutputPath (Join-Path $root 'schedule') -InventoryOnly } 'blockers'
        $script:hasSchedule=$false
        Assert ($script:mutations -eq 0)
    }
    Check 'Full export requires explicit quiescence and content approval' {
        Fails { Export-FabricEstate -SourceConfig $sourceConfig -OutputPath $package } 'Quiesced'
        Assert (!(Test-Path -LiteralPath $package))
    }
    Check 'Export captures binary data, Delta log, empty directories and decoded dependencies' {
        $script:exported = Export-FabricEstate -SourceConfig $sourceConfig -OutputPath $package -SourceQuiesced -DefinitionContentApproved
        Assert ($script:exported.status -eq 'complete')
        $script:digest = $script:exported.manifestSha256
        $manifest = Read-EstatePackage $package $script:digest
        Assert ($manifest.items[1].dependencies[0] -eq $sourceLakehouse)
        Assert (@($manifest.files | Where-Object { $_.path -like '*_delta_log*json' }).Count -eq 1)
        Assert (@($manifest.files | Where-Object { $_.path -like '*empty-folder' }).Count -eq 1)
        $sqlReadback = Read-EstateJson (Join-Path $package "readback-$sourceLakehouse-sql.json")
        Assert ($sqlReadback.differences -is [array] -and $sqlReadback.differences.Count -eq 0)
    }
    Check 'Offline validation requires no tenant access' {
        $script:tenantMismatch=$true
        Assert ((Test-FabricEstate -PackagePath $package -ManifestSha256 $script:digest -PackageOnly).status -eq 'packageValidated')
        $script:tenantMismatch=$false
    }
    Check 'Public package-only entrypoint returns valid JSON without mocked adapters or dependencies' {
        $entrypoint = Join-Path $PSScriptRoot '..\scripts\Test-FabricEstate.ps1'
        $output = Invoke-EstateCommand pwsh @('-NoProfile','-File',$entrypoint,'-PackagePath',$package,'-ManifestSha256',$script:digest,'-PackageOnly')
        Assert (($output | ConvertFrom-Json).status -eq 'packageValidated')
        Fails {
            Invoke-EstateCommand pwsh @('-NoProfile','-File',$entrypoint,'-PackagePath',$package,'-ManifestSha256',('0' * 64),'-PackageOnly')
        } 'exit 1'
    }
    Check 'Source drift during copy prevents a completed export' {
        $script:changeDuringDownload=$true
        Fails { Export-FabricEstate -SourceConfig $sourceConfig -OutputPath (Join-Path $root 'source-drift') -SourceQuiesced -DefinitionContentApproved } 'inventory mismatch'
        Assert ((Read-EstateJson (Join-Path $root 'source-drift\manifest.json')).status -eq 'incomplete')
        $script:changeDuringDownload=$false;$script:sourceChanged=$false
    }
    Check 'Real SQL comparator rejects source drift and retains flat difference evidence' {
        $script:changeSqlDuringDownload=$true
        $changedPackage = Join-Path $root 'source-sql-drift'
        Fails { Export-FabricEstate -SourceConfig $sourceConfig -OutputPath $changedPackage -SourceQuiesced -DefinitionContentApproved } 'Source SQL snapshot changed'
        Assert ((Read-EstateJson (Join-Path $changedPackage 'manifest.json')).status -eq 'incomplete')
        $evidence = Read-EstateJson (Join-Path $changedPackage "readback-$sourceLakehouse-sql.json")
        Assert ($evidence.differences.Count -eq 1 -and $evidence.differences[0].path -ceq '$.tables[0].rowCount')
        $script:changeSqlDuringDownload=$false;$script:sqlChanged=$false
    }
    Check 'Plan is read-only and leaves no receipt or staging files' {
        $result = Import-FabricEstate -PackagePath $package -ManifestSha256 $script:digest -TargetMap $targetMap -ReceiptPath $receiptPath
        Assert ($result.status -eq 'planned' -and $script:mutations -eq 0 -and !(Test-Path -LiteralPath $receiptPath))
    }
    Check 'Tenant mismatch and nonempty target fail without remote writes' {
        $script:tenantMismatch=$true
        Fails { Import-FabricEstate -PackagePath $package -ManifestSha256 $script:digest -TargetMap $targetMap -ReceiptPath $receiptPath -Apply -TargetIsolated } 'Tenant'
        $script:tenantMismatch=$false
        $collision = Join-Path $root 'target-data\Files\unexpected'
        [IO.File]::WriteAllText($collision,'x')
        Fails { Import-FabricEstate -PackagePath $package -ManifestSha256 $script:digest -TargetMap $targetMap -ReceiptPath $receiptPath -Apply -TargetIsolated } 'not empty'
        Remove-Item -LiteralPath $collision
        Assert ($script:mutations -eq 0)
    }
    Check 'Schema mode mismatch prevents all writes' {
        $script:schemaChanged=$true
        Fails { Import-FabricEstate -PackagePath $package -ManifestSha256 $script:digest -TargetMap $targetMap -ReceiptPath $receiptPath -Apply -TargetIsolated } 'schema mode'
        $script:schemaChanged=$false
        Assert ($script:mutations -eq 0)
    }
    Check 'A binding that creates invalid JSON is rejected before any remote write' {
        $original = [IO.File]::ReadAllText($targetMap)
        try {
            $mapping = Read-EstateJson $targetMap
            $mapping.bindings = @(@{source='reference-source';target='invalid"quote'})
            Write-EstateJson $targetMap $mapping
            Fails {
                Import-FabricEstate -PackagePath $package -ManifestSha256 $script:digest -TargetMap $targetMap -ReceiptPath $receiptPath -Apply -TargetIsolated
            } 'JSON|invalid|expected'
            Assert ($script:mutations -eq 0 -and !(Test-Path -LiteralPath $receiptPath))
        } finally { [IO.File]::WriteAllText($targetMap, $original) }
    }
    Check 'Apply imports in dependency order and rebinds decoded definitions' {
        $result = Import-FabricEstate -PackagePath $package -ManifestSha256 $script:digest -TargetMap $targetMap -ReceiptPath $receiptPath -Apply -TargetIsolated
        Assert ($result.status -eq 'imported' -and !$result.cutoverApproved)
        $definition = $script:definitions[$createdNotebook] | ConvertFrom-Json -AsHashtable
        $text = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($definition.parts[0].payload))
        Assert ($text -eq "workspace=$targetWorkspace lakehouse=$targetLakehouse")
        $metadata = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($definition.parts[1].payload)) | ConvertFrom-Json
        Assert ($metadata.dependency -eq $targetLakehouse)
    }
    Check 'Completed receipt retries are idempotent without writes' {
        $before=$script:mutations
        $result = Import-FabricEstate -PackagePath $package -ManifestSha256 $script:digest -TargetMap $targetMap -ReceiptPath $receiptPath -Apply -TargetIsolated
        Assert ($result.status -eq 'imported' -and $before -eq $script:mutations)
    }
    Check 'Full target validation checks bytes, SQL and definition readback, not cutover approval' {
        $result = Test-FabricEstate -PackagePath $package -ManifestSha256 $script:digest -TargetMap $targetMap -ReceiptPath $receiptPath -ReadbackPath (Join-Path $root 'readback')
        Assert ($result.status -eq 'dataAndDefinitionsValidated' -and !$result.cutoverApproved -and $result.items.Count -eq 2)
    }
    Check 'SQL drift never yields a successful target validation' {
        $script:sqlChanged=$true
        Fails { Test-FabricEstate -PackagePath $package -ManifestSha256 $script:digest -TargetMap $targetMap -ReceiptPath $receiptPath -ReadbackPath (Join-Path $root 'sql-drift') } 'SQL reconciliation'
        $script:sqlChanged=$false
    }
    Check 'Same-size byte corruption is detected by full readback' {
        [IO.File]::WriteAllBytes((Join-Path $root 'target-data\Tables\fact\part.parquet'),[byte[]](0,1,2,3,254))
        Fails { Test-FabricEstate -PackagePath $package -ManifestSha256 $script:digest -TargetMap $targetMap -ReceiptPath $receiptPath -ReadbackPath (Join-Path $root 'byte-drift') } 'integrity mismatch'
    }
    Check 'Definition drift is detected independently of byte verification' {
        $script:definitions[$createdNotebook] = '{"parts":[]}'
        $m = Read-EstatePackage $package $script:digest
        $mapping = Read-EstateTarget $targetMap $m
        $receipt = Read-EstateJson $receiptPath
        $definitionDirectory = Join-Path $root 'definition-drift'
        Export-EstateDefinition $targetWorkspace $createdNotebook Notebook $definitionDirectory
        Fails { Assert-EstateImportedDefinition $m.items[1] $m $mapping $receipt $package $definitionDirectory @{$targetWorkspace=(Get-EstateWorkspaceInventory $targetWorkspace)} } 'part count'
    }
    Check 'Interrupted receipts are not blindly resumed or overwritten' {
        $receipt = Read-EstateJson $receiptPath
        $receipt.status='inProgress'
        Write-EstateJson $receiptPath $receipt
        $before=$script:mutations
        Fails { Import-FabricEstate -PackagePath $package -ManifestSha256 $script:digest -TargetMap $targetMap -ReceiptPath $receiptPath -Apply -TargetIsolated } 'Interrupted'
        Assert ($before -eq $script:mutations)
    }
    Write-Host "$script:passed workflow tests passed."
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
