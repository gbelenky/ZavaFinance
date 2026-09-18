#requires -Version 7.2
<#
.SYNOPSIS
    Builds an isolated, unofficial Activity SDK package from pinned public MIT sources.
.DESCRIPTION
    Run only on activity-protocol. Requires an installed .NET 10 SDK and Git.
    WorkDirectory must be outside the application repository. No application project,
    user NuGet configuration, or existing global package cache is modified.
    OutputDirectory optionally places the feed subdirectory and distribution ZIP in
    a separate artifact directory, such as C:\src\ZavaFinance\.artifacts\one-sdk.
    Keep repository output ignored. Source, build state and caches stay under
    WorkDirectory; output defaults to WorkDirectory\artifacts.
    Upstream uses Core source beta.30 and M365 1.6.150. This compatibility build
    deliberately targets the application's Core beta.28 and M365 1.8.77; failure
    never triggers a dependency upgrade. Use -M365Version 1.6.150 to test the
    upstream-declared M365 floor in a separate working directory.
    Retain the feed bundle and pass its packages.lock.json as -DependencyLockFile
    to reproduce the resolved dependency graph in a fresh working directory.
    The ZIP is a flat NuGet feed including all resolved SDK dependency archives,
    license notices and SHA256SUMS.txt. Stage it in an approved remote build context,
    verify checksums, and restore with an explicit local source and separate package
    cache. A consuming application needs its own lock file for its complete graph.
    On Windows keep the cache path short: MSBuild can reject assembly paths over
    259 characters even when NuGet extracts the package successfully.
    Only net10.0 is packaged. Microsoft signing and the Azure SDK monorepo build
    pipeline are not reproduced; this is NOT an official Microsoft release.
.EXAMPLE
    .\scripts\build-experimental-activity-sdk.ps1 -WorkDirectory C:\scratch\activity-sdk
.EXAMPLE
    .\scripts\build-experimental-activity-sdk.ps1 -WorkDirectory C:\scratch\activity-sdk -OutputDirectory .\.artifacts\one-sdk
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $WorkDirectory,
    [string] $DependencyLockFile,
    [ValidateSet('1.6.150', '1.8.77')] [string] $M365Version = '1.8.77',
    [string] $SdkVersion = '10.0.303',
    [ValidateSet('https://api.nuget.org/v3/index.json', 'https://www.nuget.org/api/v2/')]
    [string] $NuGetSource = 'https://www.nuget.org/api/v2/',
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$commit = 'dc9cca2d1f1c9f42182a0f1d6cc2540acf5dc956'
$coreVersion = '1.0.0-beta.28'
$version = "1.0.0-beta.1.source.dc9cca2d1f1c.core28.m365$($M365Version.Replace('.', ''))"
$packageId = 'Azure.AI.AgentServer.Activity'
$repoRoot = Split-Path $PSScriptRoot -Parent
$branch = & git -C $repoRoot branch --show-current
if ($LASTEXITCODE -ne 0 -or $branch -ne 'activity-protocol') {
    throw 'Build this experimental SDK only from the activity-protocol branch.'
}
if ($SdkVersion -notmatch '^10\.0\.\d+$') {
    throw 'SdkVersion must identify an installed stable .NET 10 SDK (for example 10.0.303).'
}
$installed = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($installed | Where-Object { $_.StartsWith("$SdkVersion ") })) {
    throw ".NET SDK $SdkVersion is not installed. No SDK will be downloaded."
}
$work = [IO.Path]::GetFullPath($WorkDirectory)
$repository = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\', '/')
if ($work -eq $repository -or $work.StartsWith("$repository\", [StringComparison]::OrdinalIgnoreCase) `
    -or $work -eq [IO.Path]::GetPathRoot($work)) {
    throw 'WorkDirectory must be a dedicated directory outside the application repository, not a drive root.'
}

# Git blob IDs pin the actual bytes, not just a mutable URL or archive filename.
$sourceHashes = [ordered]@{
    'src\ActivityEnvironment.cs' = '84d54f55309c31da8f84db8bdc9902da2fddaaa0'
    'src\ActivityProtocolActivitySource.cs' = '5dfde25f66fd2bb8b295e7065662b3fe56cbb11c'
    'src\ActivityServerOptions.cs' = '7dc9608ae2d8fd5f202d0829d1bf0bf2af17ad5e'
    'src\Azure.AI.AgentServer.Activity.csproj' = '6533c72cbf044914eea5a3b107e969decfa818cf'
    'src\Hosting\ActivityBuilderExtensions.cs' = '8fa0b898682e905413a2236ffc269fbefab3e232'
    'src\Hosting\ActivityServer.cs' = '98ee86933fb3187bce519bddb8cf2873a8f5716a'
    'src\Hosting\ActivityServerServiceCollectionExtensions.cs' = 'c3990a18a0486e7edbdaf60d3b07f7ad1599e37d'
    'src\Hosting\FoundryActivityEndpointRouteBuilderExtensions.cs' = '77dd7d69c4e68b6444cd72d6b12fe7051d3b43a5'
    'src\Hosting\FoundryActivityHostingExtensions.cs' = '115bea1902cdad045d86d1bf94cb1497a357019a'
    'src\Internal\ActivityEndpointHandler.cs' = '7768bae128a7c73008b400fbde06e23bbbfefdcb'
    'src\Internal\ActivityErrorSourceFilter.cs' = 'b6b6ce0bfe3882ffc03eccc8628cebabbbff220b'
    'src\Internal\ActivityIdSanitizer.cs' = '227e8ace1dcb7d11cc35c3f8df1419f5023af4e6'
    'src\Internal\ActivitySessionIdResolver.cs' = '9ba6e174bc8effc7b689b56be54f1597d7d6123d'
    'src\Internal\ActivityStack.cs' = '6cbddeeb140195efafbaa32f941aca46f48238a9'
    'src\Internal\ActivityStartupLogger.cs' = '35c583cad926576056c486121c9cd82e1b46b56c'
    'src\Internal\ConnectionEnvironment.cs' = '40f094f49f1b05f31c06042ed3482a78f0362e33'
    'src\Internal\PlatformErrorMarker.cs' = 'aaa6c3325158432922c185e987d922328491d9eb'
    'src\Properties\AssemblyInfo.cs' = '03fc8af0a0c7c228e9917452b48c46d7e8fe640c'
    'README.md' = '2057bbbd3ca966082005fe4084ff380c94311c8a'
    'CHANGELOG.md' = 'c4bc07a6476d352674dbcd1e2fac5129b301e38c'
    'api\Azure.AI.AgentServer.Activity.net10.0.cs' = '261896b0b20ebddc37b278e08f24f9ac50c0926f'
    'docs\hosting-guide.md' = 'cd5f7e4065e9b9e0b85a365d36a0b51681098bc9'
}
$files = [ordered]@{
    'LICENSE.txt' = '3423e9584d153a326c1900a759d3b5c2330047d4'
    'NOTICE.txt' = '01a06a82faf98b3e5733e855aba50647ab2d6ca5'
    'eng\centralpackagemanagement\Directory.Packages.props' = '84070bf1101c6484b5ef9957b159caf345213ae3'
    'eng\centralpackagemanagement\overrides\Azure.AI.AgentServer.Activity.Packages.props' = '73ff38626482975f94f7510bbacf33c2aca59c61'
    'sdk\agentserver\Azure.AI.AgentServer.Core\src\Azure.AI.AgentServer.Core.csproj' = '719693540ae38bce4211adb9c0412f5f70f710b1'
}
foreach ($entry in $sourceHashes.GetEnumerator()) {
    $files["sdk\agentserver\$packageId\$($entry.Key)"] = $entry.Value
}

function Write-Utf8([string] $Path, [string] $Content) {
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Get-GitBlobHash([string] $Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $header = [Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0")
    return [Convert]::ToHexString([Security.Cryptography.SHA1]::HashData(
        [byte[]] ($header + $bytes))).ToLowerInvariant()
}

function Invoke-DotNet([string[]] $Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

$artifacts = Join-Path $work 'artifacts'
$distribution = if ($OutputDirectory) {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
}
else {
    $artifacts
}
if ($distribution.TrimEnd('\', '/') -eq $repository -or $distribution -eq [IO.Path]::GetPathRoot($distribution)) {
    throw 'OutputDirectory must be a dedicated artifact directory, not the repository or a drive root.'
}
$build = Join-Path $artifacts 'build'
$feed = Join-Path $distribution 'feed'
$cache = Join-Path $artifacts 'packages'
foreach ($manifestPath in @((Join-Path $build 'SOURCE.json'), (Join-Path $feed 'SOURCE.json'))) {
    if (Test-Path -LiteralPath $manifestPath) {
        $existing = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($existing.version -ne $version -or $existing.sdk -ne $SdkVersion) {
            throw 'Use separate work and output directories for a different dependency or SDK profile.'
        }
    }
}
if ($DependencyLockFile) {
    $DependencyLockFile = (Resolve-Path -LiteralPath $DependencyLockFile).Path
}
foreach ($directory in @($work, $artifacts, $build, $feed, $cache)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
Write-Utf8 (Join-Path $work '.gitignore') "*`n"
if ($DependencyLockFile) {
    $lockPath = Join-Path $build 'packages.lock.json'
    if (Test-Path -LiteralPath $lockPath) {
        if ((Get-FileHash -LiteralPath $lockPath).Hash -ne (Get-FileHash -LiteralPath $DependencyLockFile).Hash) {
            throw 'The supplied dependency lock differs from the existing build lock. Use a fresh WorkDirectory.'
        }
    }
    else {
        Copy-Item -LiteralPath $DependencyLockFile -Destination $lockPath
    }
}
$provenance = [Collections.Generic.List[object]]::new()
foreach ($entry in $files.GetEnumerator()) {
    $target = Join-Path $work "source\$($entry.Key)"
    $url = "https://raw.githubusercontent.com/Azure/azure-sdk-for-net/$commit/$($entry.Key.Replace('\', '/'))"
    if (-not (Test-Path -LiteralPath $target)) {
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Invoke-WebRequest -Uri $url -OutFile $target -MaximumRetryCount 3 -RetryIntervalSec 2
    }
    if ((Get-GitBlobHash $target) -ne $entry.Value) {
        throw "Source hash mismatch: $target. Refusing to overwrite or compile it."
    }
    $provenance.Add([ordered]@{
        path = $entry.Key
        gitBlobSha1 = $entry.Value
        sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        url = $url
    })
}

$source = Join-Path $work "source\sdk\agentserver\$packageId"
$compile = foreach ($entry in $sourceHashes.GetEnumerator()) {
    if ($entry.Key.StartsWith('src\') -and $entry.Key.EndsWith('.cs')) {
        $path = [Security.SecurityElement]::Escape((Join-Path $source $entry.Key))
        "    <Compile Include=`"$path`" />"
    }
}
$dependencies = [ordered]@{
    'Azure.AI.AgentServer.Core' = $coreVersion
    'Azure.Core' = '1.62.0'
    'Azure.Identity' = '1.21.0'
    'Microsoft.Agents.Authentication.Msal' = $M365Version
    'Microsoft.Agents.Connector' = $M365Version
    'Microsoft.Agents.Hosting.AspNetCore' = $M365Version
}
$references = foreach ($entry in $dependencies.GetEnumerator()) {
    "    <PackageReference Include=`"$($entry.Key)`" Version=`"[$($entry.Value)]`" />"
}
$sourceManifest = [ordered]@{
    repository = 'https://github.com/Azure/azure-sdk-for-net'
    commit = $commit
    package = $packageId
    version = $version
    upstreamCoreSourceVersion = '1.0.0-beta.30'
    upstreamM365Version = '1.6.150'
    targetFramework = 'net10.0'
    sdk = $SdkVersion
    unsignedCompatibilityBuild = $true
    dependencies = $dependencies
    files = $provenance
}
Write-Utf8 (Join-Path $build 'SOURCE.json') ($sourceManifest | ConvertTo-Json -Depth 8)
Copy-Item -LiteralPath (Join-Path $work 'source\LICENSE.txt') -Destination $build
Copy-Item -LiteralPath (Join-Path $work 'source\NOTICE.txt') -Destination $build
Copy-Item -LiteralPath (Join-Path $source 'README.md') -Destination $build
Write-Utf8 (Join-Path $artifacts 'global.json') (@{
    sdk = @{ version = $SdkVersion; rollForward = 'disable' }
} | ConvertTo-Json)
Write-Utf8 (Join-Path $artifacts 'Directory.Build.props') '<Project />'
Write-Utf8 (Join-Path $artifacts 'Directory.Build.targets') '<Project />'
Write-Utf8 (Join-Path $artifacts 'Directory.Packages.props') '<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>'
$config = Join-Path $artifacts 'NuGet.Config'
Write-Utf8 $config @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /><add key="nuget.org" value="$NuGetSource" /></packageSources>
  <fallbackPackageFolders><clear /></fallbackPackageFolders>
</configuration>
"@
$project = Join-Path $build "$packageId.csproj"
Write-Utf8 $project @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>$packageId</PackageId>
    <AssemblyName>$packageId</AssemblyName>
    <RootNamespace>$packageId</RootNamespace>
    <Version>$version</Version>
    <AssemblyVersion>1.0.0.0</AssemblyVersion>
    <FileVersion>1.0.0.0</FileVersion>
    <Authors>Experimental source build from Microsoft MIT-licensed sources</Authors>
    <Description>Unofficial unsigned net10.0 compatibility build of public Activity SDK commit $commit. Not an official Microsoft release.</Description>
    <RepositoryType>git</RepositoryType>
    <RepositoryUrl>https://github.com/Azure/azure-sdk-for-net</RepositoryUrl>
    <RepositoryCommit>$commit</RepositoryCommit>
    <PackageLicenseFile>LICENSE.txt</PackageLicenseFile>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <Deterministic>true</Deterministic>
    <PathMap>$([Security.SecurityElement]::Escape($work))=/_/experimental-activity-sdk</PathMap>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
    <WarningsAsErrors>NU1603;NU1605;NU1608</WarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
$($references -join "`n")
$($compile -join "`n")
    <None Include="LICENSE.txt;NOTICE.txt;SOURCE.json;README.md" Pack="true" PackagePath="" />
  </ItemGroup>
</Project>
"@

$environment = @{
    NUGET_PACKAGES = $cache
    NUGET_HTTP_CACHE_PATH = (Join-Path $artifacts 'http-cache')
    NUGET_PLUGINS_CACHE_PATH = (Join-Path $artifacts 'plugin-cache')
    DOTNET_CLI_HOME = (Join-Path $artifacts 'dotnet-home')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_NOLOGO = '1'
}
$previous = @{}
foreach ($key in $environment.Keys) {
    $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
    [Environment]::SetEnvironmentVariable($key, $environment[$key], 'Process')
}
Push-Location $build
try {
    $restoreArgs = @('restore', $project, '--configfile', $config, '--packages', $cache, '--verbosity', 'minimal')
    if (Test-Path -LiteralPath (Join-Path $build 'packages.lock.json')) {
        $restoreArgs += '--locked-mode'
    }
    Invoke-DotNet $restoreArgs
    Invoke-DotNet @('pack', $project, '--configuration', 'Release', '--no-restore', '--output', $feed, '--verbosity', 'minimal')
    $nupkg = Join-Path $feed "$packageId.$version.nupkg"
    if (-not (Test-Path -LiteralPath $nupkg)) {
        throw "Expected package was not produced: $nupkg"
    }
    $assets = Get-Content -LiteralPath (Join-Path $build 'obj\project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    $graph = foreach ($entry in $assets.libraries.GetEnumerator()) {
        if ($entry.Value.type -ne 'package') { continue }
        $parts = $entry.Key.Split('/')
        $id = $parts[0].ToLowerInvariant()
        $resolved = $parts[1]
        $dependencyPackage = Join-Path $cache "$id\$resolved\$id.$resolved.nupkg"
        if (-not (Test-Path -LiteralPath $dependencyPackage)) {
            throw "Resolved dependency archive missing: $dependencyPackage"
        }
        Copy-Item -LiteralPath $dependencyPackage -Destination $feed
        [pscustomobject]@{
            id = $parts[0]
            version = $resolved
            sha512 = $entry.Value.sha512
            dependencies = $assets.targets['net10.0'][$entry.Key]['dependencies']
        }
    }
    Copy-Item -LiteralPath (Join-Path $build 'SOURCE.json'), (Join-Path $build 'packages.lock.json'), `
        (Join-Path $build 'LICENSE.txt'), (Join-Path $build 'NOTICE.txt') -Destination $feed
    Write-Utf8 (Join-Path $feed 'dependency-graph.json') ($graph | Sort-Object id | ConvertTo-Json -Depth 6)
    $hashes = Get-ChildItem -LiteralPath $feed -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
        Sort-Object Name | ForEach-Object { "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" }
    Write-Utf8 (Join-Path $feed 'SHA256SUMS.txt') (($hashes -join "`n") + "`n")
    $archive = Join-Path $distribution "activity-sdk-$version.zip"
    Compress-Archive -Path (Join-Path $feed '*') -DestinationPath $archive -Force
    Write-Output "Built: $nupkg"
    Write-Output "Local feed: $feed"
    Write-Output "Distribution (includes dependency archives and their embedded licenses): $archive"
    Write-Output "Lock file: $(Join-Path $feed 'packages.lock.json')"
    Write-Output "SHA256: $((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash)"
}
finally {
    Pop-Location
    foreach ($key in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process')
    }
}
