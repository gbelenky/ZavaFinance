#Requires -Version 7.6
Set-StrictMode -Version Latest

function Write-EstateJson {
    param([string]$Path, $Value)
    $json = ConvertTo-Json -InputObject $Value -Depth 100
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function Read-EstateJson {
    param([string]$Path)
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -Depth 100 -DateKind String
}

function Assert-EstateLocalPath {
    param([string]$Path, [switch]$OutsideRepository)
    $full = [IO.Path]::GetFullPath($Path)
    $current = $full
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Links/reparse points are not allowed in migration paths: $current"
            }
            if ($OutsideRepository -and (Test-Path -LiteralPath (Join-Path $current '.git'))) {
                throw 'Keep migration packages, receipts and readbacks outside any Git repository.'
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

function Assert-EstateGuid {
    param([string]$Value, [string]$Name)
    if ($Value -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' -or [guid]$Value -eq [guid]::Empty) {
        throw "$Name must be a nonempty GUID."
    }
}

function Resolve-EstatePath {
    param([string]$Root, [string]$Relative)
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative)) {
        throw "Expected a relative package path: $Relative"
    }
    $parts = $Relative.Replace('\', '/').Split('/')
    foreach ($part in $parts) {
        if (!$part -or $part -in '.', '..' -or $part -match '[<>:"|?*\x00-\x1f]' -or
            $part -match '[. ]$' -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)') {
            throw "Path is not portable to Windows: $Relative"
        }
    }
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    Assert-EstateLocalPath $fullRoot
    $path = $fullRoot
    foreach ($part in $parts) {
        $path = Join-Path $path $part
        if (Test-Path -LiteralPath $path) {
            if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Links/reparse points are not allowed in a migration package: $path"
            }
        }
    }
    return $path
}

function Get-EstateFiles {
    param([string]$Root)
    $rootPath = [IO.Path]::GetFullPath($Root)
    Assert-EstateLocalPath $rootPath
    $entries = @(Get-ChildItem -LiteralPath $rootPath -Recurse -Force)
    if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw "Migration data must not contain links/reparse points: $Root"
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @($entries | Sort-Object FullName -CaseSensitive)) {
        $relative = [IO.Path]::GetRelativePath($rootPath, $file.FullName).Replace('\', '/')
        [void](Resolve-EstatePath $rootPath $relative)
        if (!$seen.Add($relative)) { throw "Case-colliding path: $relative" }
        if ($file.PSIsContainer) {
            [ordered]@{ path=$relative; kind='directory'; bytes='0'; sha256=$null }
        } else {
            [ordered]@{ path=$relative; kind='file'; bytes=$file.Length.ToString([Globalization.CultureInfo]::InvariantCulture)
                sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
    }
}

function Assert-EstateFiles {
    param([string]$Root, [array]$Expected)
    $actual = @(Get-EstateFiles $Root)
    $lookup = @{}
    foreach ($entry in $Expected) {
        [void](Resolve-EstatePath $Root $entry.path)
        if ($lookup.ContainsKey($entry.path)) { throw "Duplicate manifest path: $($entry.path)" }
        $lookup[$entry.path] = $entry
    }
    if ($actual.Count -ne $Expected.Count) { throw "Package file count differs: expected $($Expected.Count), got $($actual.Count)." }
    foreach ($entry in $actual) {
        if (!$lookup.ContainsKey($entry.path) -or
            $entry.path -cne $lookup[$entry.path].path -or
            $entry.kind -ne $lookup[$entry.path].kind -or $entry.bytes -ne $lookup[$entry.path].bytes -or
            $entry.sha256 -cne $lookup[$entry.path].sha256) {
            throw "Package integrity mismatch: $($entry.path)"
        }
    }
}

function Invoke-EstateCommand {
    param([string]$Command, [string[]]$Arguments, [int]$TimeoutSeconds = 7200)
    $executable = Get-Command $Command -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $executable.Source
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if ([IO.Path]::GetExtension($executable.Source) -in '.cmd', '.bat') {
        $start.FileName = (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
        foreach ($argument in @('-NoProfile', '-CommandWithArgs', '$command=$args[0]; $forwarded=@($args | Select-Object -Skip 1); & $command @forwarded; exit $LASTEXITCODE', $executable.Source)) {
            $start.ArgumentList.Add($argument)
        }
    }
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            throw "$Command timed out. Inspect the existing Fabric/AzCopy job before retrying; it may still be running remotely."
        }
        $text = $output.GetAwaiter().GetResult()
        $errorText = $errorOutput.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            # CLI diagnostics can contain definition payloads or URLs. Do not echo them into transcripts.
            throw "$Command failed (exit $($process.ExitCode)). Output is suppressed because it may contain sensitive data. Inspect the tool's local logs, or diagnose in an approved local console; inspect remote mutations before retrying."
        }
        if ($errorText.Trim()) { Write-Warning "$Command emitted diagnostics on stderr (not echoed because they may contain sensitive content)." }
        return $text
    } finally { $process.Dispose() }
}

function Assert-EstateTenant {
    param([string]$TenantId)
    Assert-EstateGuid $TenantId 'TenantId'
    $account = Invoke-EstateCommand az @('account', 'show', '--output', 'json') | ConvertFrom-Json -AsHashtable
    if ($account.tenantId -ne $TenantId) { throw "Azure CLI tenant does not match the requested tenant $TenantId. Sign in explicitly first." }
}

function Get-EstateOneLakeUri {
    param([string]$WorkspaceId, [string]$LakehouseId, [ValidateSet('Tables','Files')][string]$Area)
    Assert-EstateGuid $WorkspaceId 'WorkspaceId'
    Assert-EstateGuid $LakehouseId 'LakehouseId'
    return "https://onelake.dfs.fabric.microsoft.com/$WorkspaceId/$LakehouseId/$Area"
}

function Invoke-EstateAzCopy {
    param([string[]]$Arguments, [string]$TenantId)
    $priorLogin = $env:AZCOPY_AUTO_LOGIN_TYPE
    $priorTenant = $env:AZCOPY_TENANT_ID
    try {
        $env:AZCOPY_AUTO_LOGIN_TYPE = 'AZCLI'
        $env:AZCOPY_TENANT_ID = $TenantId
        Invoke-EstateCommand azcopy ($Arguments + @('--trusted-microsoft-suffixes=fabric.microsoft.com', '--output-type=json'))
    } finally {
        $env:AZCOPY_AUTO_LOGIN_TYPE = $priorLogin
        $env:AZCOPY_TENANT_ID = $priorTenant
    }
}

function Get-EstateRemoteFiles {
    param([string]$Uri, [string]$TenantId)
    $text = Invoke-EstateAzCopy @('list', $Uri, '--machine-readable', '--properties=LastModifiedTime', '--running-tally') $TenantId
    $files = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $summarySeen = $false
    foreach ($line in $text.Split("`n") | Where-Object { $_.Trim() }) {
        $message = $line | ConvertFrom-Json -AsHashtable
        if ($message.MessageType -eq 'ListSummary') { $summarySeen = $true }
        if ($message.MessageType -eq 'Error') { throw 'AzCopy failed during OneLake enumeration.' }
        if ($message.MessageType -ne 'ListObject') { continue }
        $item = $message.MessageContent | ConvertFrom-Json -AsHashtable
        if ($item.Path -eq '/') { continue }
        $isDirectory = $item.Path.EndsWith('/')
        $path = $item.Path.TrimEnd('/')
        [void](Resolve-EstatePath ([IO.Path]::GetTempPath()) $path)
        if (!$seen.Add($path)) { throw "Nonportable case-colliding OneLake path: $path" }
        if ($item.ContentLength -notmatch '^\d+$' -or (!$isDirectory -and !$item.LastModifiedTime)) { throw 'Incomplete OneLake listing metadata.' }
        $files.Add([ordered]@{ path=$path; kind=$(if ($isDirectory) {'directory'} else {'file'})
            bytes=$(if ($isDirectory) {'0'} else {$item.ContentLength})
            modified=$(if ($isDirectory) {$null} else {$item.LastModifiedTime}) })
    }
    if (!$summarySeen) { throw 'AzCopy did not return a completed listing summary.' }
    return @($files | Sort-Object { $_.path } -CaseSensitive)
}

function Copy-EstateData {
    param([string]$Source, [string]$Destination, [string]$TenantId)
    if ($Destination.StartsWith('https:', [StringComparison]::OrdinalIgnoreCase)) {
        if (@(Get-EstateRemoteFiles $Destination $TenantId).Count) { throw 'Transfer destination must be empty before copy.' }
    } else {
        Assert-EstateLocalPath $Destination
        if (!(Test-Path -LiteralPath $Destination)) { [void](New-Item -ItemType Directory -Path $Destination -Force) }
        if (@(Get-EstateFiles $Destination).Count) { throw 'Transfer destination must be empty before copy.' }
    }
    $text = Invoke-EstateAzCopy @('copy', $Source, $Destination, '--recursive=true', '--as-subdir=false',
        '--overwrite=false', '--check-length=true') $TenantId
    $summary = $null
    foreach ($line in $text.Split("`n") | Where-Object { $_.Trim() }) {
        $message = $line | ConvertFrom-Json -AsHashtable
        if ($message.MessageType -eq 'EndOfJob') { $summary = $message.MessageContent | ConvertFrom-Json -AsHashtable }
        if ($message.MessageType -eq 'Error') { throw 'AzCopy copy failed.' }
    }
    if (!$summary -or $summary.JobStatus -notin 'Completed','CompletedWithSkipped') {
        throw 'AzCopy did not complete every transfer. Preserve the partial target and job logs; do not activate it.'
    }
    foreach ($name in 'TransfersFailed','TransfersSkipped','FoldersSkipped','FoldersFailed','TransfersCompleted','TotalTransfers') {
        if (!$summary.Contains($name) -or [string]$summary[$name] -notmatch '^\d+$') { throw "AzCopy summary is incomplete: $name" }
    }
    $skipped = [long]$summary.TransfersSkipped
    # The destination was empty immediately above: only its existing root may skip folder properties.
    if ([long]$summary.TransfersFailed -ne 0 -or [long]$summary.FoldersFailed -ne 0 -or
        $skipped -ne [long]$summary.FoldersSkipped -or $skipped -gt 1 -or
        [long]$summary.TotalTransfers -ne ([long]$summary.TransfersCompleted + $skipped) -or
        ($summary.JobStatus -eq 'Completed' -and $skipped -ne 0)) {
        throw 'AzCopy did not complete every transfer: skipped files, unexpected skipped directories or failed transfers are blockers.'
    }
}

function Assert-EstateListingMatches {
    param([array]$Expected, [array]$Actual, [switch]$IncludeModified)
    if ($Expected.Count -ne $Actual.Count) { throw 'OneLake file inventory changed or transfer omitted files.' }
    $lookup = @{}
    foreach ($entry in $Expected) {
        if ($lookup.ContainsKey($entry.path)) { throw 'Duplicate OneLake inventory entry.' }
        $lookup[$entry.path] = $entry
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Actual) {
        if (!$seen.Add($entry.path)) { throw 'Duplicate actual OneLake inventory entry.' }
        if (!$lookup.ContainsKey($entry.path) -or $entry.path -cne $lookup[$entry.path].path -or
            $entry.bytes -ne $lookup[$entry.path].bytes -or
            ($entry.Contains('kind') -and $lookup[$entry.path].Contains('kind') -and $entry.kind -ne $lookup[$entry.path].kind) -or
            ($IncludeModified -and $entry.modified -ne $lookup[$entry.path].modified)) {
            throw "OneLake inventory mismatch: $($entry.path)"
        }
    }

}
