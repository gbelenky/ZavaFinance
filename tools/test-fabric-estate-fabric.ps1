#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
foreach ($helper in 'Fabric-Estate','Fabric-EstatePackage','Fabric-EstateFabric') {
    . (Join-Path $PSScriptRoot "..\scripts\$helper.ps1")
}
$script:passed = 0
$script:responses = [Collections.Generic.Queue[object]]::new()
$script:calls = [Collections.Generic.List[object]]::new()
$script:waits = 0
function Case([string]$Name, [scriptblock]$Action) {
    $script:responses.Clear(); $script:calls.Clear(); $script:waits=0
    & $Action
    if ($script:responses.Count) { throw 'Fixture responses left unused' }
    $script:passed++; Write-Host "PASS $Name"
}
function Assert([bool]$Condition) { if (!$Condition) { throw 'Assertion failed' } }
function Fails([scriptblock]$Action,[string]$Pattern) {
    try { & $Action } catch { if ($_.Exception.Message -notmatch $Pattern) { throw }; return }
    throw "Expected error: $Pattern"
}
function Response($Body,[int]$Status=200,$Headers=@{}) {
    $script:responses.Enqueue(@{status='Success';result=@{data=@(@{status_code=$Status;headers=$Headers;text=$Body})}})
}
function Invoke-EstateCommand($Command,[string[]]$Arguments) {
    Assert ($Command -eq 'fab')
    $script:calls.Add($Arguments)
    if ($Arguments[0] -eq '--version') { return 'fab version 1.7.0' }
    if (!$script:responses.Count) { throw 'Unexpected native call' }
    $script:responses.Dequeue() | ConvertTo-Json -Depth 100
}
function Start-Sleep([int]$Seconds) { $script:waits++ }
Case 'Fabric CLI tenant is verified separately from Azure CLI' {
    $script:responses.Enqueue(@{status='Success';result=@{data=@(@{logged_in=$true;tenant_id='expected'})}})
    Assert-EstateFabricAuth 'expected'
    Assert ($script:calls.Count -eq 2)
}
Case 'Incorrect Fabric CLI tenant fails closed' {
    $script:responses.Enqueue(@{status='Success';result=@{data=@(@{logged_in=$true;tenant_id='different'})}})
    Fails { Assert-EstateFabricAuth 'expected' } 'requested tenant'
}
Case 'Outer CLI failure is not an empty result' {
    $script:responses.Enqueue(@{status='Error';result=@{message='denied'}})
    Fails { Invoke-EstateFab @('auth','status') } 'successful structured'
}
Case 'HTTP 403 inside successful CLI envelope still fails' {
    Response @{errorCode='InsufficientPrivileges'} 403
    Fails { Invoke-EstateApi 'workspaces' } 'HTTP 403.*InsufficientPrivileges'
}
Case 'CLI response text is parsed without flattening singleton lists' {
    Response @{value=@(@{id='one'})}
    $items = @(Get-EstateApiList 'workspaces')
    Assert ($items.Count -eq 1 -and $items[0].id -eq 'one')
}
Case 'All pages are read including an empty intermediate page' {
    Response @{value=@();continuationToken='abc==';continuationUri='https://api.fabric.microsoft.com/v1/workspaces?continuationToken=abc=='}
    Response @{value=@(@{id='two'})}
    $items = @(Get-EstateApiList 'workspaces')
    Assert ($items.Count -eq 1 -and $script:calls.Count -eq 2)
    Assert ('continuationToken=abc==' -in $script:calls[1])
}
Case 'Repeated pagination tokens fail instead of truncating results' {
    Response @{value=@();continuationToken='repeat'}
    Response @{value=@();continuationToken='repeat'}
    Fails { Get-EstateApiList 'workspaces' } 'repeated'
}
Case 'External continuation URLs are never followed' {
    Response @{value=@();continuationToken='token';continuationUri='https://example.invalid/v1/workspaces'}
    Fails { Get-EstateApiList 'workspaces' } 'Untrusted'
    Assert ($script:calls.Count -eq 1)
}
Case 'Unrepresentable opaque tokens fail instead of changing CLI parameters' {
    Response @{value=@();continuationToken='bad,another=value'}
    Fails { Get-EstateApiList 'workspaces' } 'cannot be safely'
}
Case 'Missing value array is not success-empty' {
    Response @{}
    Fails { Get-EstateApiList 'workspaces' } 'value array'
}
Case 'HTTP throttling is retried boundedly with Retry-After' {
    Response @{errorCode='TooManyRequests'} 429 @{'Retry-After'='1'}
    Response @{id='ok'}
    Assert ((Invoke-EstateApi 'workspaces').id -eq 'ok')
    Assert ($script:waits -eq 1)
}
Case 'Ambiguous POST server failures are not retried automatically' {
    Response @{errorCode='Unknown'} 503
    Fails { Invoke-EstateApi 'workspaces' 'post' } 'HTTP 503'
    Assert ($script:calls.Count -eq 1)
}
Case 'Long-running create waits for success and fetches result' {
    $id='11111111-1111-1111-1111-111111111111'
    Response '(Empty)' 202 @{'x-ms-operation-id'=$id}
    Response @{status='Running'}
    Response @{status='Succeeded'}
    Response @{id='created'} 201
    Assert ((Invoke-EstateApi 'workspaces' 'post').id -eq 'created')
    Assert ($script:calls.Count -eq 4 -and $script:waits -eq 2)
}
Case 'Failed operations remain failures and are never recreated' {
    Response '(Empty)' 202 @{'x-ms-operation-id'='11111111-1111-1111-1111-111111111111'}
    Response @{status='Failed'}
    Fails { Invoke-EstateApi 'workspaces' 'post' } 'did not succeed'
}
Case 'Timed-out operations include inspectable IDs' {
    Response '(Empty)' 202 @{'x-ms-operation-id'='11111111-1111-1111-1111-111111111111'}
    Fails { Invoke-EstateApi 'workspaces' 'post' -TimeoutSeconds 0 } '11111111.*timed out'
}
Case 'Operation identifiers and endpoints cannot redirect requests' {
    Response '(Empty)' 202 @{'x-ms-operation-id'='../external'}
    Fails { Invoke-EstateApi 'workspaces' 'post' } 'GUID'
    Fails { Get-EstateApiResponse 'https://example.invalid' } 'relative'
}
Write-Host "$script:passed Fabric CLI contract tests passed (mocked, not a live-tenant certification)."
