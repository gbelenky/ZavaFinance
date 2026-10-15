#Requires -Version 7.4
Set-StrictMode -Version Latest

function Invoke-EstateFab {
    param([string[]]$Arguments)
    $output = Invoke-EstateCommand fab ($Arguments + @('--output_format','json')) | ConvertFrom-Json -AsHashtable -Depth 100
    if ($output.status -ne 'Success' -or !$output.Contains('result') -or !$output.result.Contains('data')) {
        throw 'Fabric CLI did not return a successful structured result. No operation is considered complete.'
    }
    return ,@($output.result.data)
}

function Assert-EstateFabricAuth {
    param([string]$TenantId)
    $version = Invoke-EstateCommand fab @('--version')
    if ($version -notmatch '\b1\.7\.0\b') { throw 'These Fabric CLI contracts were reviewed against 1.7.0. Use that version or validate/update the adapter for your version.' }
    $status = Invoke-EstateFab @('auth','status')
    if ($status.Count -ne 1 -or !$status[0].logged_in -or $status[0].tenant_id -ne $TenantId) {
        throw 'Fabric CLI is not signed into the requested tenant. Run fab auth login --tenant explicitly; Azure CLI context is separate.'
    }
}

function Get-EstateApiResponse {
    param([string]$Endpoint, [ValidateSet('get','post')][string]$Method='get', [string]$InputFile, [string]$Parameters)
    if ($Endpoint -notmatch '^[a-zA-Z0-9][a-zA-Z0-9/-]*$' -or $Endpoint.Contains('//')) { throw 'Only relative, validated Fabric API resource paths are allowed.' }
    $arguments = @('api',$Endpoint,'-X',$Method,'--show_headers')
    if ($InputFile) { $arguments += @('-i',$InputFile) }
    if ($Parameters) { $arguments += @('-P',$Parameters) }
    $data = Invoke-EstateFab $arguments
    if ($data.Count -ne 1 -or !$data[0].Contains('status_code') -or !$data[0].Contains('headers') -or !$data[0].Contains('text')) {
        throw 'Unexpected Fabric CLI API envelope; expected one status_code/headers/text response.'
    }
    $response = $data[0]
    $attempt = 0
    while ($response.status_code -eq 429 -and $attempt -lt 3) {
        $attempt++
        $delay = 10
        if ($response.headers.Contains('Retry-After') -and $response.headers['Retry-After'] -match '^\d+$') {
            $delay = [Math]::Min(120, [Math]::Max(1, [int]$response.headers['Retry-After']))
        }
        Write-Warning "Fabric throttled the request; retry $attempt of 3 after $delay seconds."
        Start-Sleep -Seconds $delay
        $retry = Invoke-EstateFab $arguments
        if ($retry.Count -ne 1) { throw 'Unexpected retry response.' }
        $response = $retry[0]
    }
    if ($response.status_code -notin 200,201,202,204) {
        $code = 'unknown'
        if ($response.text -is [System.Collections.IDictionary] -and $response.text.Contains('errorCode') -and
            $response.text.errorCode -match '^[a-zA-Z0-9_.-]{1,100}$') { $code = $response.text.errorCode }
        throw "Fabric $Method $Endpoint returned HTTP $($response.status_code) ($code). For mutations inspect the target/operation before retrying; no success or empty inventory is assumed."
    }
    return $response
}

function Invoke-EstateApi {
    param([string]$Endpoint, [ValidateSet('get','post')][string]$Method='get', [string]$InputFile, [string]$Parameters, [int]$TimeoutSeconds=1800)
    $response = Get-EstateApiResponse $Endpoint $Method $InputFile $Parameters
    if ($response.status_code -ne 202) { return $response.text }
    $operationId = $response.headers['x-ms-operation-id']
    Assert-EstateGuid $operationId 'Fabric operation ID'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Seconds 5
        $operation = Get-EstateApiResponse "operations/$operationId"
        if ($operation.status_code -ne 200 -or $operation.text -isnot [System.Collections.IDictionary]) {
            throw "Unexpected operation status response for $operationId."
        }
        switch ($operation.text.status) {
            'Succeeded' {
                $result = Get-EstateApiResponse "operations/$operationId/result"
                if ($result.status_code -eq 202) { throw "Operation $operationId reported success but its result is not ready." }
                return $result.text
            }
            { $_ -in 'NotStarted','Running' } { continue }
            default { throw "Fabric operation $operationId did not succeed ($($operation.text.status)). Inspect the operation; do not repeat a create blindly." }
        }
    }
    throw "Fabric operation $operationId timed out and may still be running. Inspect it and the target before any retry."
}

function Get-EstateApiList {
    param([string]$Endpoint)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $parameters = ''
    $all = [Collections.Generic.List[object]]::new()
    do {
        $page = Invoke-EstateApi -Endpoint $Endpoint -Parameters $parameters
        if ($page -isnot [System.Collections.IDictionary] -or !$page.Contains('value') -or $page.value -isnot [array]) {
            throw "Fabric list did not return a value array: $Endpoint"
        }
        foreach ($item in $page.value) { $all.Add($item) }
        $token = if ($page.Contains('continuationToken')) { [string]$page.continuationToken } else { '' }
        if ($page.Contains('continuationUri') -and $page.continuationUri) {
            $uri = [uri]$page.continuationUri
            if (!$uri.IsAbsoluteUri -or $uri.Scheme -ne 'https' -or $uri.Host -ne 'api.fabric.microsoft.com' -or
                !$uri.IsDefaultPort -or !$uri.AbsolutePath.StartsWith('/v1/')) { throw 'Untrusted Fabric continuation URI.' }
            if (!$token) { throw 'Continuation URI without a token requires an adapter update; enumeration is incomplete.' }
        }
        if ($token) {
            if (!$seen.Add($token)) { throw 'Fabric repeated a continuation token; enumeration is incomplete.' }
            if ($token -match '[,\r\n]') { throw 'Fabric continuation token cannot be safely represented by the CLI -P contract; update the adapter before proceeding.' }
            $parameters = "continuationToken=$token"
        }
    } while ($token)
    return @($all | Sort-Object { ConvertTo-EstateCanonicalJson $_ } -CaseSensitive)
}

function Get-EstateWorkspaceInventory {
    param([string]$WorkspaceId)
    Assert-EstateGuid $WorkspaceId 'workspace ID'
    $workspace = Invoke-EstateApi "workspaces/$WorkspaceId"
    if ($workspace.id -ne $WorkspaceId) { throw 'Workspace response identity mismatch.' }
    $items = @(Get-EstateApiList "workspaces/$WorkspaceId/items")
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $shortcuts = @{}
    $schedules = @{}
    $sqlIds = @()
    foreach ($item in $items) {
        Assert-EstateGuid $item.id 'item ID'
        if (!$seen.Add($item.id)) { throw 'Duplicate workspace item ID across pages.' }
        if ($item.type -eq 'Lakehouse') {
            $details = Invoke-EstateApi "workspaces/$WorkspaceId/lakehouses/$($item.id)"
            if ($details.id -ne $item.id) { throw 'Lakehouse response identity mismatch.' }
            $item['properties'] = $details.properties
            $sqlIds += $details.properties.sqlEndpointProperties.id
            $shortcuts[$item.id] = @(Get-EstateApiList "workspaces/$WorkspaceId/items/$($item.id)/shortcuts")
            $schedules[$item.id] = @(Get-EstateApiList "workspaces/$WorkspaceId/items/$($item.id)/jobs/TableMaintenance/schedules")
        }
        if ($item.type -eq 'Notebook') {
            $schedules[$item.id] = @(Get-EstateApiList "workspaces/$WorkspaceId/items/$($item.id)/jobs/RunNotebook/schedules")
        }
    }
    return [ordered]@{
        id=$WorkspaceId;displayName=$workspace.displayName;items=$items
        lakehouseSqlEndpointIds=$sqlIds;shortcuts=$shortcuts;schedules=$schedules
        roleAssignments=@(Get-EstateApiList "workspaces/$WorkspaceId/roleAssignments")
        folders=@(Get-EstateApiList "workspaces/$WorkspaceId/folders")
        securityBoundary='Workspace roles only; item sharing, sensitivity labels and full OneLake access controls require separate approved evidence and target recreation.'
    }
}

function Get-EstateTypeRoute {
    param([ValidateSet('Lakehouse','Notebook','DataAgent')][string]$Type)
    switch ($Type) {
        'Lakehouse' { 'lakehouses' }
        'Notebook' { 'notebooks' }
        'DataAgent' { 'dataAgents' }
    }
}

function Export-EstateDefinition {
    param([string]$WorkspaceId, [string]$ItemId, [string]$Type, [string]$Directory)
    Assert-EstateGuid $WorkspaceId 'workspace ID'
    Assert-EstateGuid $ItemId 'item ID'
    $route = Get-EstateTypeRoute $Type
    $body = Invoke-EstateApi "workspaces/$WorkspaceId/$route/$ItemId/getDefinition" 'post'
    if ($body -isnot [System.Collections.IDictionary] -or !$body.Contains('definition')) { throw "Definition result missing for $ItemId." }
    [void](New-Item -ItemType Directory -Path $Directory -Force)
    Write-EstateJson (Join-Path $Directory 'definition.json') $body.definition
    [void]@(Get-EstateDefinitionParts $Directory)
}

function Import-EstateDefinition {
    param([string]$WorkspaceId, [string]$DisplayName, [ValidateSet('Notebook','DataAgent')][string]$Type, [string]$Directory, [string]$Description='')
    Assert-EstateGuid $WorkspaceId 'workspace ID'
    $route = Get-EstateTypeRoute $Type
    $definition = Read-EstateJson (Join-Path $Directory 'definition.json')
    $inputFile = Join-Path $Directory 'create-request.json'
    Write-EstateJson $inputFile @{displayName=$DisplayName;description=$Description;definition=$definition}
    # ID-addressed create never uses native CLI import's implicit update-by-name behavior.
    $created = Invoke-EstateApi "workspaces/$WorkspaceId/$route" 'post' $inputFile
    Assert-EstateGuid $created.id 'created item ID'
    $readback = Invoke-EstateApi "workspaces/$WorkspaceId/items/$($created.id)"
    if ($readback.id -ne $created.id -or $readback.displayName -cne $DisplayName -or $readback.type -ne $Type) {
        throw 'Created item readback does not match the request. Inspect the target; do not create another copy.'
    }
    return $created.id
}
