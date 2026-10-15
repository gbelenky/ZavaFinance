#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\scripts\Fabric-Estate.ps1')
. (Join-Path $PSScriptRoot '..\scripts\Fabric-EstatePackage.ps1')
$script:passed = 0
function Test-Case([string]$Name, [scriptblock]$Action) {
    & $Action
    $script:passed++
    Write-Host "PASS $Name"
}
function Assert-True([bool]$Condition, [string]$Message = 'Assertion failed') {
    if (!$Condition) { throw $Message }
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    $caught = $false
    try { & $Action } catch {
        $caught = $true
        if ($_.Exception.Message -notmatch $Pattern) { throw "Wrong error: $($_.Exception.Message)" }
    }
    if (!$caught) { throw "Expected failure: $Pattern" }
}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("zava-estate-tests-" + [guid]::NewGuid())
[void](New-Item -ItemType Directory -Path $testRoot)
try {
    Test-Case 'GUIDs fail closed' {
        Assert-Throws { Assert-EstateGuid 'not-an-id' 'source' } 'GUID'
        Assert-Throws { Assert-EstateGuid ([guid]::Empty.ToString()) 'source' } 'GUID'
    }
    Test-Case 'Package path traversal, Windows aliases and wildcards are rejected' {
        foreach ($path in @('..\secret', '/absolute', 'C:\secret', 'data//x', 'a/../b', 'a/CON.txt', 'a/file.', 'a/file ', 'a/b:c', 'a/*')) {
            Assert-Throws { Resolve-EstatePath $testRoot $path } 'relative|portable'
        }
    }
    Test-Case 'Normal nested package path is accepted' {
        Assert-True ((Resolve-EstatePath $testRoot 'data/Tables/name/_delta_log/0.json') -eq (Join-Path $testRoot 'data\Tables\name\_delta_log\0.json'))
    }
    Test-Case 'Package JSON retains timestamp strings without automatic date coercion' {
        $path = Join-Path $testRoot 'timestamps.json'
        $timestamp = '2026-09-30T19:00:00.1234567Z'
        Write-EstateJson $path @{timestamp=$timestamp;rows=@(@{timestamp=$timestamp})}
        $value = Read-EstateJson $path
        Assert-True ($value.timestamp -is [string] -and $value.timestamp -ceq $timestamp)
        Assert-True ($value.rows -is [array] -and $value.rows[0].timestamp -ceq $timestamp)
    }
    Test-Case 'File digests retain singleton arrays and detect byte tampering' {
        $data = Join-Path $testRoot 'data'
        [void](New-Item -ItemType Directory -Path $data)
        [IO.File]::WriteAllText((Join-Path $data 'table.parquet'), 'fixture')
        $expected = @(Get-EstateFiles $data)
        Assert-True ($expected.Count -eq 1 -and $expected[0].bytes -eq '7')
        Assert-EstateFiles $data $expected
        [IO.File]::WriteAllText((Join-Path $data 'table.parquet'), 'changed')
        Assert-Throws { Assert-EstateFiles $data $expected } 'integrity mismatch'
    }
    Test-Case 'Unexpected package files and duplicate manifest paths fail' {
        $data = Join-Path $testRoot 'data'
        $expected = @(Get-EstateFiles $data)
        Assert-Throws { Assert-EstateFiles $data @($expected[0], $expected[0]) } 'Duplicate'
        [IO.File]::WriteAllText((Join-Path $data 'extra'), 'x')
        Assert-Throws { Assert-EstateFiles $data $expected } 'count differs'
    }
    Test-Case 'Inventories compare exact case and sizes' {
        $expected = @(@{path='table/a';bytes='5';modified='2026-01-01'})
        Assert-EstateListingMatches $expected $expected -IncludeModified
        Assert-Throws { Assert-EstateListingMatches $expected @(@{path='table/a';bytes='6';modified='2026-01-01'}) } 'mismatch'
        Assert-Throws { Assert-EstateListingMatches $expected @(@{path='Table/a';bytes='5';modified='2026-01-01'}) } 'mismatch'
        Assert-Throws { Assert-EstateListingMatches $expected @(@{path='table/a';bytes='5';modified='2026-01-02'}) -IncludeModified } 'mismatch'
    }
    Test-Case 'OneLake URLs cannot redirect credentials' {
        $id = '11111111-1111-1111-1111-111111111111'
        Assert-True ((Get-EstateOneLakeUri $id $id Tables) -eq "https://onelake.dfs.fabric.microsoft.com/$id/$id/Tables")
        Assert-Throws { Get-EstateOneLakeUri 'evil.example' $id Tables } 'GUID'
    }
    Test-Case 'Topological import puts dependencies before their consumers' {
        $items = @(
            @{id='agent';type='DataAgent';dependencies=@('notebook','lakehouse')},
            @{id='notebook';type='Notebook';dependencies=@('lakehouse')},
            @{id='lakehouse';type='Lakehouse';dependencies=@()}
        )
        Assert-True ((@(Get-EstateItemOrder $items).id -join ',') -eq 'lakehouse,notebook,agent')
        $items[2].dependencies = @('agent')
        Assert-Throws { Get-EstateItemOrder $items } 'Cyclic|out-of-scope'
    }
    Test-Case 'Remapping is single-pass and rejects unresolved source bindings' {
        $mapped = Convert-EstateDefinitionText 'source-01 source-02' @{'source-01'='source-02';'source-02'='target-02'} @()
        Assert-True ($mapped -eq 'source-02 target-02')
        Assert-Throws { Convert-EstateDefinitionText 'source-01' @{} @('source-01') } 'Unmapped'
        Assert-Throws { Convert-EstateDefinitionText '{"password":"test-secret"}' @{} @() } 'credentials'
    }
    Test-Case 'Manifest digest is independently required' {
        $package = Join-Path $testRoot 'package'
        [void](New-Item -ItemType Directory -Path (Join-Path $package 'payload') -Force)
        Write-EstateJson (Join-Path $package 'manifest.json') @{version=1;status='blocked';blockers=@('unsupported type')}
        $digest = (Get-FileHash -LiteralPath (Join-Path $package 'manifest.json')).Hash
        Assert-Throws { Read-EstatePackage $package ('0' * 64) } 'hash differs'
        Assert-Throws { Read-EstatePackage $package $digest } 'not a completed'
    }
    Test-Case 'Decoded definitions reject binary or non-self-contained payloads' {
        $directory = Join-Path $testRoot 'definition'
        [void](New-Item -ItemType Directory -Path $directory)
        Write-EstateJson (Join-Path $directory 'definition.json') @{parts=@(@{path='binary';payloadType='InlineBase64';payload='/w=='})}
        Assert-Throws { Get-EstateDefinitionParts $directory } 'Binary definition'
        Write-EstateJson (Join-Path $directory 'definition.json') @{parts=@(@{path='payload';payloadType='External';payload='https://example.invalid'})}
        Assert-Throws { Get-EstateDefinitionParts $directory } 'self-contained'
    }
    Test-Case 'Empty directory records and case-insensitive GUID rewriting survive roundtrip' {
        $directory = Join-Path $testRoot 'empty-directories'
        [void](New-Item -ItemType Directory -Path (Join-Path $directory 'empty') -Force)
        $entries = @(Get-EstateFiles $directory)
        Assert-True ($entries.Count -eq 1 -and $entries[0].kind -eq 'directory')
        Assert-EstateFiles $directory $entries
        Assert-True ((Convert-EstateDefinitionText 'ABCDEF01' @{'abcdef01'='target-id'} @('abcdef01')) -eq 'target-id')
    }
    Test-Case 'JSON definition comparison preserves exact numeric tokens' {
        $first = ConvertTo-EstateCanonicalText '{"a":1.12345678901234567890,"b":[true]}'
        $reordered = ConvertTo-EstateCanonicalText '{ "b": [true], "a": 1.12345678901234567890 }'
        $changed = ConvertTo-EstateCanonicalText '{"a":1.12345678901234567891,"b":[true]}'
        Assert-True ($first -ceq $reordered -and $first -cne $changed)
    }
    Test-Case 'JSON-escaped references are discovered and remapped without numeric precision loss' {
        $directory = Join-Path $testRoot 'escaped-definition'
        [void](New-Item -ItemType Directory -Path $directory)
        $text = '{"id":"\u0061bcdef01","amount":1.12345678901234567890}'
        Write-EstateJson (Join-Path $directory 'definition.json') @{parts=@(@{
            path='notebook.ipynb';payloadType='InlineBase64';payload=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text))
        })}
        Assert-True ((Get-EstateDefinitionText $directory).Contains('abcdef01'))
        $mappedDirectory = Join-Path $testRoot 'mapped-definition'
        Copy-EstateDefinitionWithBindings $directory $mappedDirectory @{'abcdef01'='target-id'} @('abcdef01')
        Assert-True ((Get-EstateDefinitionText $mappedDirectory) -ceq '{"amount":1.12345678901234567890,"id":"target-id"}')
        $invalidDirectory = Join-Path $testRoot 'invalid-definition'
        Assert-Throws {
            Copy-EstateDefinitionWithBindings $directory $invalidDirectory @{'abcdef01'='invalid"quote'} @('abcdef01')
        } 'JSON|invalid|expected'
        Assert-True (!(Test-Path -LiteralPath $invalidDirectory))
    }
    Test-Case 'Escaped credential property names and SDK token names are rejected' {
        foreach ($name in 'client_secret','access_token','api-key','\u0070assword') {
            $directory = Join-Path $testRoot 'credential-definition'
            [void](New-Item -ItemType Directory -Path $directory -Force)
            $text = '{"' + $name + '":"fixture-secret"}'
            Write-EstateJson (Join-Path $directory 'definition.json') @{parts=@(@{
                path='config.json';payloadType='InlineBase64';payload=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text))
            })}
            Assert-Throws { Get-EstateDefinitionParts $directory } 'credentials'
        }
    }
    Test-Case 'Physical Delta table visibility must agree with the SQL snapshot' {
        $lakehouse = @{properties=@{};areas=@{Tables=@(@{kind='directory';path='fact/_delta_log'})}
            sqlSnapshot=@{tables=@(@{schemaName='dbo';tableName='fact'})}}
        Assert-EstateDeltaTables $lakehouse
        $lakehouse.sqlSnapshot.tables=@()
        Assert-Throws { Assert-EstateDeltaTables $lakehouse } 'missing from the SQL'
        $lakehouse.areas.Tables=@()
        $lakehouse.sqlSnapshot.tables=@(@{schemaName='dbo';tableName='fact'})
        Assert-Throws { Assert-EstateDeltaTables $lakehouse } 'no matching physical'
    }
    Test-Case 'Migration mutable artifacts cannot be written inside immutable package or Git root' {
        Assert-Throws { Assert-EstateSeparatePath (Join-Path $testRoot 'package\receipt.json') (Join-Path $testRoot 'package') } 'immutable'
        Assert-Throws { Assert-EstateLocalPath (Join-Path $PSScriptRoot '..\export') -OutsideRepository } 'outside any Git'
    }
    Test-Case 'Subprocess arguments remain separate including Windows command shim' {
        $shim = Join-Path $testRoot 'estate-args.cmd'
        $receiver = Join-Path $testRoot 'args receiver.ps1'
        [IO.File]::WriteAllText($receiver, 'ConvertTo-Json -InputObject @($args) -Compress')
        $pwshPath = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
        [IO.File]::WriteAllText($shim, "@echo off`r`n`"$pwshPath`" -NoProfile -File `"$receiver`" %*`r`n")
        $output = Invoke-EstateCommand $shim @('space value','second=value')
        $received = $output | ConvertFrom-Json
        Assert-True ($received.Count -eq 2 -and $received[0] -eq 'space value' -and $received[1] -eq 'second=value') "Native argument output differed: $output"
        Assert-True ((Invoke-EstateCommand $shim @()).Trim() -eq '[]')
        [IO.File]::WriteAllText($shim, "@echo off`r`nexit /b 7`r`n")
        Assert-Throws { Invoke-EstateCommand $shim @('x') } 'exit 7'
    }
    Test-Case 'Native process timeouts fail rather than returning partial output' {
        Assert-Throws {
            Invoke-EstateCommand pwsh @('-NoProfile','-Command','Start-Sleep -Seconds 30') -TimeoutSeconds 1
        } 'timed out'
    }
    function Invoke-EstateAzCopy {
        param([string[]]$Arguments, [string]$TenantId)
        return $script:azResponse
    }
    Test-Case 'AzCopy JSON listing contract' {
        $script:azResponse = @(
            (@{MessageType='Info';MessageContent='Preparing'} | ConvertTo-Json -Compress)
            (@{MessageType='ListObject';MessageContent=(@{Path='/';ContentLength='0';LastModifiedTime='2026-01-01T00:00:00Z'} | ConvertTo-Json -Compress)} | ConvertTo-Json -Compress)
            (@{MessageType='ListObject';MessageContent=(@{Path='table/a';ContentLength='42';LastModifiedTime='2026-01-01T00:00:00Z'} | ConvertTo-Json -Compress)} | ConvertTo-Json -Compress)
            (@{MessageType='ListSummary';MessageContent='{}'} | ConvertTo-Json -Compress)
        ) -join "`n"
        $files = @(Get-EstateRemoteFiles 'https://onelake.dfs.fabric.microsoft.com/a/b/Tables' 'tenant')
        Assert-True ($files.Count -eq 1 -and $files[0].path -eq 'table/a' -and $files[0].bytes -eq '42')
        $script:azResponse = '{"MessageType":"Info","MessageContent":"Incomplete output"}'
        Assert-Throws { Get-EstateRemoteFiles 'url' 'tenant' } 'completed listing'
    }
    Test-Case 'Partial, failed or skipped copies cannot succeed' {
        $destination = Join-Path $testRoot 'copy-target'
        foreach ($status in @('Failed', 'CompletedWithErrors', 'Cancelled', 'InProgress')) {
            $script:azResponse = @{MessageType='EndOfJob';MessageContent=(@{JobStatus=$status;TransfersFailed=0;TransfersSkipped=0}|ConvertTo-Json -Compress)} | ConvertTo-Json -Compress
            Assert-Throws { Copy-EstateData 'source' $destination 'tenant' } 'every transfer'
        }
        $summary = @{JobStatus='CompletedWithSkipped';TransfersFailed='0';FoldersFailed='0';TransfersSkipped='1';FoldersSkipped='0';TransfersCompleted='1';TotalTransfers='2'}
        $script:azResponse = @{MessageType='EndOfJob';MessageContent=($summary|ConvertTo-Json -Compress)} | ConvertTo-Json -Compress
        Assert-Throws { Copy-EstateData 'source' $destination 'tenant' } 'every transfer'
        $summary.JobStatus='Completed';$summary.TransfersSkipped='0';$summary.TotalTransfers='1'
        $script:azResponse = @{MessageType='EndOfJob';MessageContent=($summary|ConvertTo-Json -Compress)} | ConvertTo-Json -Compress
        Copy-EstateData 'source' $destination 'tenant'
        $summary.JobStatus='CompletedWithSkipped';$summary.TransfersSkipped='1';$summary.FoldersSkipped='1';$summary.TotalTransfers='2'
        $script:azResponse = @{MessageType='EndOfJob';MessageContent=($summary|ConvertTo-Json -Compress)} | ConvertTo-Json -Compress
        Copy-EstateData 'source' $destination 'tenant'
        $summary.TransfersSkipped='2';$summary.FoldersSkipped='2';$summary.TotalTransfers='3'
        $script:azResponse = @{MessageType='EndOfJob';MessageContent=($summary|ConvertTo-Json -Compress)} | ConvertTo-Json -Compress
        Assert-Throws { Copy-EstateData 'source' $destination 'tenant' } 'every transfer'
    }
    Write-Host "$script:passed tests passed."
} finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
