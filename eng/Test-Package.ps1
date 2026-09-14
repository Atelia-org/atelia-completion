#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')][string]$Version,
    [Parameter(Mandatory)][string]$FeedDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path $PSScriptRoot -Parent
$feed = (Resolve-Path -LiteralPath $FeedDirectory).Path
$work = [IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $work) { throw 'WorkDirectory must be a new directory; previous evidence is never overwritten.' }
if ($work.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $work -eq $repoRoot) {
    throw 'WorkDirectory must be outside the source repository.'
}
$ids = @('Atelia.Diagnostics', 'Atelia.Completion.Abstractions', 'Atelia.Completion', 'Atelia.Completion.Tools')
$packageEvidence = @{}
foreach ($id in $ids) {
    $files = @(Get-ChildItem -LiteralPath $feed -File | Where-Object Name -IEQ "$id.$Version.nupkg")
    if ($files.Count -ne 1) { throw "Expected exactly one frozen package $id.$Version.nupkg." }
    $file = $files[0]
    $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        $specs = @($zip.Entries | Where-Object FullName -Like '*.nuspec')
        if ($specs.Count -ne 1) { throw "Package $id must have one nuspec." }
        $reader = [IO.StreamReader]::new($specs[0].Open())
        try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $spec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        $actualId = $metadata.SelectSingleNode('*[local-name()="id"]').InnerText
        $actualVersion = $metadata.SelectSingleNode('*[local-name()="version"]').InnerText
        if ($actualId -cne $id -or $actualVersion -cne $Version) { throw "Wrong nuspec identity in $($file.Name)." }
    } finally { $zip.Dispose() }
    $packageEvidence[$id] = @{ id = $id; version = $Version; file = $file.FullName; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
}
[void][IO.Directory]::CreateDirectory($work)
$utf8 = [Text.UTF8Encoding]::new($false)
function Write-Utf8([string]$Path, [string]$Content) { [IO.File]::WriteAllText($Path, $Content, $utf8) }
# Stop ancestor MSBuild/central-package configuration discovery at the work root.
Write-Utf8 "$work/Directory.Build.props" '<Project />'
Write-Utf8 "$work/Directory.Build.targets" '<Project />'
Write-Utf8 "$work/Directory.Packages.props" '<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>'
Copy-Item -LiteralPath "$repoRoot/global.json" -Destination "$work/global.json"
$escapedFeed = [Security.SecurityElement]::Escape($feed)
$maps = ($ids | ForEach-Object { '<package pattern="' + $_ + '" />' }) -join ''
Write-Utf8 "$work/NuGet.Config" @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /><add key="candidate" value="$escapedFeed" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <fallbackPackageFolders><clear /></fallbackPackageFolders>
  <disabledPackageSources><clear /></disabledPackageSources>
  <packageSourceMapping><clear /><packageSource key="candidate">$maps</packageSource><packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping>
</configuration>
"@
$savedEnvironment = @{}
foreach ($name in @('NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'NUGET_FALLBACK_PACKAGES')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$env:NUGET_PACKAGES = "$work/packages"
$env:NUGET_HTTP_CACHE_PATH = "$work/http-cache"
$env:NUGET_FALLBACK_PACKAGES = ''
$checks = [Collections.Generic.List[object]]::new()
function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}
function Verify-Assets([string]$ProjectDirectory, [string[]]$ExpectedIds) {
    $assetsPath = Join-Path $ProjectDirectory 'obj/project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
    if (@($assets.libraries.Values | Where-Object { $_.type -eq 'project' }).Count -ne 0) { throw "Unexpected ProjectReference in $assetsPath." }
    if ($assets.packageFolders.Count -ne 1 -or -not [IO.Path]::GetFullPath(@($assets.packageFolders.Keys)[0]).TrimEnd('/','\').Equals(
        [IO.Path]::GetFullPath($env:NUGET_PACKAGES).TrimEnd('/','\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected package cache in $assetsPath."
    }
    $actual = @($assets.libraries.Keys | Where-Object { $_ -like 'Atelia.*/*' } | ForEach-Object { ($_ -split '/')[0] } | Sort-Object)
    if (($actual -join '|') -cne (($ExpectedIds | Sort-Object) -join '|')) { throw "Wrong Atelia closure in $assetsPath : $($actual -join ', ')." }
    foreach ($id in $ExpectedIds) {
        $key = "$id/$Version"
        if (-not $assets.libraries.ContainsKey($key) -or $assets.libraries[$key].type -ne 'package') { throw "Expected package identity $key." }
        $library = $assets.libraries[$key]
        $cacheDirectory = Join-Path $env:NUGET_PACKAGES $library.path
        $packageName = "$($id.ToLowerInvariant()).$($Version.ToLowerInvariant()).nupkg"
        $cached = Join-Path $cacheDirectory $packageName
        $actualHash = (Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash
        if ($actualHash -cne $packageEvidence[$id].sha256) { throw "Cached package bytes differ from frozen feed: $id." }
        $stream = [IO.File]::OpenRead($cached)
        $sha = [Security.Cryptography.SHA512]::Create()
        try { $sha512 = [Convert]::ToBase64String($sha.ComputeHash($stream)) } finally { $stream.Dispose(); $sha.Dispose() }
        $recordedHash = (Get-Content -LiteralPath "$cached.sha512" -Raw).Trim()
        if ($recordedHash -cne $sha512 -or $library.sha512 -cne $sha512) { throw "NuGet content hash mismatch: $id." }
        $checks.Add(@{ project = (Split-Path $ProjectDirectory -Leaf); id = $id; version = $Version; cachedSha256 = $actualHash })
    }
}
try {
    Push-Location $work
    try {
        foreach ($probe in @('PackageSmoke', 'DiagnosticsOnly', 'AbstractionsOnly')) {
            $directory = Join-Path $work $probe
            [void][IO.Directory]::CreateDirectory($directory)
            if ($probe -eq 'PackageSmoke') {
                $project = (Get-Content -LiteralPath "$repoRoot/tests/PackageSmoke/PackageSmoke.csproj" -Raw).Replace('@VERSION@', $Version)
                Copy-Item -LiteralPath "$repoRoot/tests/PackageSmoke/Program.cs" -Destination "$directory/Program.cs"
                $expected = $ids
            } else {
                $id = if ($probe -eq 'DiagnosticsOnly') { 'Atelia.Diagnostics' } else { 'Atelia.Completion.Abstractions' }
                $type = if ($probe -eq 'DiagnosticsOnly') { 'Atelia.Diagnostics.DebugUtil' } else { 'Atelia.Completion.Abstractions.CompletionRequest' }
                $project = "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup><ItemGroup><PackageReference Include=`"$id`" Version=`"$Version`" /></ItemGroup></Project>"
                Write-Utf8 "$directory/Program.cs" "System.Console.WriteLine(typeof($type).Assembly.GetName().Name);"
                $expected = @($id)
            }
            $projectPath = Join-Path $directory "$probe.csproj"
            Write-Utf8 $projectPath $project
            Invoke-DotNet -Arguments @('restore', $projectPath, '--configfile', "$work/NuGet.Config", '--packages', $env:NUGET_PACKAGES,
                '--no-http-cache', '-p:RestoreFallbackFolders=', '-p:RestoreAdditionalProjectSources=', '-p:RestoreAdditionalProjectFallbackFolders=')
            Verify-Assets $directory $expected
            Invoke-DotNet -Arguments @('build', $projectPath, '-c', 'Release', '--no-restore')
            Invoke-DotNet -Arguments @('run', '--project', $projectPath, '-c', 'Release', '--no-build', '--no-restore')
        }
        $summary = @{ version = $Version; feed = $feed; packages = @($ids | ForEach-Object { $packageEvidence[$_] }); checks = $checks.ToArray(); coverage = 'Offline public-package HTTP and tools smoke; independent Diagnostics and Abstractions closures.' }
        Write-Utf8 "$work/package-smoke-results.json" ($summary | ConvertTo-Json -Depth 8)
        Write-Host "Package verification passed. Evidence: $work/package-smoke-results.json"
    } finally { Pop-Location }
} finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
}
