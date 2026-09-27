#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Diagnostics', 'Completion.Abstractions', 'Completion', 'Completion.Tools')][string]$Project,
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[a-z0-9]+([.-][a-z0-9]+)*)?$')][string]$Version,
    [Parameter(Mandatory)][string]$FeedDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$feed = (Resolve-Path -LiteralPath $FeedDirectory).Path
$work = [IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $work) { throw 'WorkDirectory must be new; use another directory for a read-only retry.' }
if ($work.Equals($repo, [StringComparison]::OrdinalIgnoreCase) -or
    $work.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'WorkDirectory must be outside the source repository.'
}

$packageId = "Atelia.$Project"
$manifestPath = Join-Path $feed "manifest.$packageId.$Version.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
if ($manifest.schemaVersion -ne 2 -or $manifest.version -cne $Version -or @($manifest.packages).Count -ne 1 -or
    $manifest.packages[0].id -cne $packageId -or $manifest.sourceRevision -cnotmatch '^[0-9a-f]{40}$' -or
    $manifest.repositoryUrl -cne 'https://github.com/Atelia-org/atelia-completion' -or !$manifest.sdkVersion -or
    !$manifest.ContainsKey('dependencies')) { throw "Expected one schema 2 $packageId candidate." }

$expectedDependencies = if ($Project -eq 'Completion' -or $Project -eq 'Completion.Tools') {
    @('Atelia.Diagnostics', 'Atelia.Completion.Abstractions')
} else { @() }
$dependencies = @($manifest.dependencies)
$dependencyIds = @($dependencies | ForEach-Object { $_.id } | Sort-Object)
if (($dependencyIds -join '|') -cne (($expectedDependencies | Sort-Object) -join '|')) {
    throw "Unexpected manifest dependency set: $($dependencyIds -join ', ')."
}
foreach ($dependency in $dependencies) {
    if ($dependency.version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[a-z0-9]+([.-][a-z0-9]+)*)?$' -or
        $dependency.file -cne "$($dependency.id).$($dependency.version).nupkg" -or
        $dependency.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $dependency.sourceRevision -cnotmatch '^[0-9a-f]{40}$') { throw "Invalid frozen dependency evidence: $($dependency.id)." }
    $frozenDependency = Join-Path $feed $dependency.file
    if ((Get-FileHash -LiteralPath $frozenDependency -Algorithm SHA256).Hash.ToLowerInvariant() -cne $dependency.sha256) {
        throw "Frozen dependency changed: $($dependency.id)."
    }
}

$candidate = $manifest.packages[0]
if ($candidate.file -cne "$packageId.$Version.nupkg" -or
    $candidate.symbolsFile -cne "$packageId.$Version.snupkg" -or
    $candidate.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $candidate.symbolsSha256 -cnotmatch '^[0-9a-f]{64}$') {
    throw 'Invalid candidate file identity or hash in manifest.'
}
$candidatePath = Join-Path $feed $candidate.file
$symbolsPath = Join-Path $feed $candidate.symbolsFile
$candidateHash = (Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($candidateHash -cne $candidate.sha256 -or
    (Get-FileHash -LiteralPath $symbolsPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $candidate.symbolsSha256) {
    throw 'Frozen candidate or symbols changed before public verification.'
}

function Read-Nuspec([IO.Compression.ZipArchive]$Archive, [string]$Id) {
    $entry = $Archive.GetEntry("$Id.nuspec")
    if (!$entry) { throw "Package has no $Id nuspec." }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { return [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Get-Metadata([xml]$Spec) {
    $metadata = $Spec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    if (!$metadata) { throw 'Package has no nuspec metadata.' }
    return $metadata
}

function Assert-NuspecIdentity([xml]$Spec, [string]$Id, [string]$PackageVersion, [string]$Revision, [string]$RepositoryUrl) {
    $metadata = Get-Metadata $Spec
    $idNode = $metadata.SelectSingleNode('*[local-name()="id"]')
    $versionNode = $metadata.SelectSingleNode('*[local-name()="version"]')
    $repository = $metadata.SelectSingleNode('*[local-name()="repository"]')
    if (!$idNode -or !$versionNode -or $idNode.InnerText -cne $Id -or $versionNode.InnerText -cne $PackageVersion -or
        !$repository -or $repository.GetAttribute('url') -cne $RepositoryUrl -or
        ($Revision -and $repository.GetAttribute('commit') -cne $Revision)) {
        throw "Wrong public nuspec identity or source: $Id/$PackageVersion."
    }
    return $metadata
}

function Get-EntryHash([IO.Compression.ZipArchiveEntry]$Entry) {
    $stream = $Entry.Open()
    try { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
    finally { $stream.Dispose() }
}

[void][IO.Directory]::CreateDirectory($work)
$publicPath = Join-Path $work "$packageId.$Version.nupkg"
$normalizedId = $packageId.ToLowerInvariant()
$normalizedVersion = $Version.ToLowerInvariant()
$url = "https://api.nuget.org/v3-flatcontainer/$normalizedId/$normalizedVersion/$normalizedId.$normalizedVersion.nupkg"
$deadline = [DateTimeOffset]::UtcNow.AddMinutes(15)
while ($true) {
    try {
        Invoke-WebRequest -Uri $url -OutFile $publicPath -TimeoutSec 30 | Out-Null
        break
    }
    catch {
        if (Test-Path -LiteralPath $publicPath) { Remove-Item -LiteralPath $publicPath }
        $remaining = ($deadline - [DateTimeOffset]::UtcNow).TotalSeconds
        if ($remaining -le 0) { throw "Published package was not downloadable within 15 minutes: $url. Rerun this read-only script with the archived feed and a new WorkDirectory." }
        Start-Sleep -Seconds ([Math]::Min(15, [Math]::Ceiling($remaining)))
    }
}
$publicHash = (Get-FileHash -LiteralPath $publicPath -Algorithm SHA256).Hash.ToLowerInvariant()
$candidateArchive = [IO.Compression.ZipFile]::OpenRead($candidatePath)
$publicArchive = [IO.Compression.ZipFile]::OpenRead($publicPath)
try {
    if (!$publicArchive.GetEntry('.signature.p7s')) { throw 'Downloaded public package has no NuGet signature.' }
    $candidateEntries = @($candidateArchive.Entries | ForEach-Object { $_.FullName } | Sort-Object)
    $publicEntries = @($publicArchive.Entries | Where-Object FullName -CNE '.signature.p7s' | ForEach-Object { $_.FullName } | Sort-Object)
    if (($candidateEntries -join '|') -cne ($publicEntries -join '|')) { throw 'Published package asset list differs from candidate.' }
    foreach ($name in $candidateEntries) {
        if ((Get-EntryHash ($candidateArchive.GetEntry($name))) -cne (Get-EntryHash ($publicArchive.GetEntry($name)))) {
            throw "Published package asset differs from candidate: $name"
        }
    }
    $metadata = Assert-NuspecIdentity (Read-Nuspec $publicArchive $packageId) $packageId $Version $manifest.sourceRevision $manifest.repositoryUrl
    $direct = @($metadata.SelectNodes('.//*[local-name()="dependency"]') | Where-Object { $_.GetAttribute('id') -like 'Atelia.*' })
    if ($direct.Count -ne $dependencies.Count) { throw 'Published package direct dependency count differs from manifest.' }
    foreach ($dependency in $dependencies) {
        $match = @($direct | Where-Object { $_.GetAttribute('id') -ceq $dependency.id })
        if ($match.Count -ne 1 -or
            ($match[0].GetAttribute('version') -cne "[$($dependency.version), )" -and
             $match[0].GetAttribute('version') -cne "[$($dependency.version),)" -and
             $match[0].GetAttribute('version') -cne $dependency.version)) {
            throw "Published package direct dependency lower bound differs: $($dependency.id)."
        }
    }
}
finally { $candidateArchive.Dispose(); $publicArchive.Dispose() }

$utf8 = [Text.UTF8Encoding]::new($false)
function Write-Utf8([string]$Path, [string]$Content) { [IO.File]::WriteAllText($Path, $Content, $utf8) }
Write-Utf8 (Join-Path $work 'Directory.Build.props') '<Project />'
Write-Utf8 (Join-Path $work 'Directory.Build.targets') '<Project />'
Write-Utf8 (Join-Path $work 'Directory.Packages.props') '<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>'
Copy-Item -LiteralPath (Join-Path $repo 'global.json') -Destination $work
Write-Utf8 (Join-Path $work 'NuGet.Config') @'
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources><fallbackPackageFolders><clear /></fallbackPackageFolders></configuration>
'@
$probe = Join-Path $work 'PublicProbe'
[void][IO.Directory]::CreateDirectory($probe)
Write-Utf8 (Join-Path $probe 'PublicProbe.csproj') "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup><ItemGroup><PackageReference Include=`"$packageId`" Version=`"$Version`" /></ItemGroup></Project>"
$type = switch ($Project) {
    Diagnostics { 'Atelia.Diagnostics.DebugUtil' }
    Completion.Abstractions { 'Atelia.Completion.Abstractions.CompletionRequest' }
    Completion { 'Atelia.Completion.OpenAI.OpenAIChatClient' }
    Completion.Tools { 'Atelia.Completion.Tools.MethodToolWrapper' }
}
Write-Utf8 (Join-Path $probe 'Program.cs') "System.Console.WriteLine(typeof($type).Assembly.GetName().Name);"

$savedEnvironment = @{}
foreach ($name in @('NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'NUGET_FALLBACK_PACKAGES')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$env:NUGET_PACKAGES = Join-Path $work 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $work 'http-cache'
$env:NUGET_FALLBACK_PACKAGES = ''
try {
    & dotnet nuget verify $publicPath --all --configfile (Join-Path $work 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw 'NuGet signature verification failed.' }
    & dotnet restore (Join-Path $probe 'PublicProbe.csproj') --configfile (Join-Path $work 'NuGet.Config') --packages $env:NUGET_PACKAGES --no-http-cache '-p:RestoreFallbackFolders=' '-p:RestoreAdditionalProjectSources=' '-p:RestoreAdditionalProjectFallbackFolders='
    if ($LASTEXITCODE -ne 0) { throw 'Public NuGet restore failed.' }
    $assets = Get-Content -LiteralPath (Join-Path $probe 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    if (@($assets.libraries.Values | Where-Object { $_.type -eq 'project' }).Count -ne 0 -or $assets.packageFolders.Count -ne 1 -or
        ![IO.Path]::GetFullPath(@($assets.packageFolders.Keys)[0]).TrimEnd('/','\').Equals(
            [IO.Path]::GetFullPath($env:NUGET_PACKAGES).TrimEnd('/','\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Public consumer resolved project references or an unexpected package cache.'
    }
    $expected = @("$packageId/$Version") + @($dependencies | ForEach-Object { "$($_.id)/$($_.version)" })
    $actual = @($assets.libraries.Keys | Where-Object { $_ -like 'Atelia.*/*' } | Sort-Object)
    if (($actual -join '|') -cne (($expected | Sort-Object) -join '|')) {
        throw "Public Atelia package closure differs: $($actual -join ', ')."
    }
    & dotnet run --project (Join-Path $probe 'PublicProbe.csproj') -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Public package consumer failed.' }

    $cachedNew = Join-Path $env:NUGET_PACKAGES "$normalizedId/$normalizedVersion/$normalizedId.$normalizedVersion.nupkg"
    if ((Get-FileHash -LiteralPath $cachedNew -Algorithm SHA256).Hash.ToLowerInvariant() -cne $publicHash) {
        throw 'Restored new package bytes differ from public download.'
    }
    foreach ($dependency in $dependencies) {
        $id = ([string]$dependency.id).ToLowerInvariant()
        $dependencyVersion = ([string]$dependency.version).ToLowerInvariant()
        $cached = Join-Path $env:NUGET_PACKAGES "$id/$dependencyVersion/$id.$dependencyVersion.nupkg"
        if ((Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash.ToLowerInvariant() -cne $dependency.sha256) {
            throw "Restored dependency bytes differ from frozen public package: $($dependency.id)."
        }
        if ($dependency.sourceRevision) {
            $archive = [IO.Compression.ZipFile]::OpenRead($cached)
            try { [void](Assert-NuspecIdentity (Read-Nuspec $archive $dependency.id) $dependency.id $dependency.version $dependency.sourceRevision $manifest.repositoryUrl) }
            finally { $archive.Dispose() }
        }
    }
}
finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
}

$result = [ordered]@{
    id = $packageId
    version = $Version
    sourceRevision = $manifest.sourceRevision
    candidateSha256 = $candidateHash
    candidateSymbolsSha256 = $candidate.symbolsSha256
    publishedSha256 = $publicHash
    publicUrl = $url
    resolvedPackages = $actual
    dependencies = @($dependencies | ForEach-Object { [ordered]@{ id = $_.id; version = $_.version; sha256 = $_.sha256; sourceRevision = $_.sourceRevision } })
}
Write-Utf8 (Join-Path $work 'published-check.json') ($result | ConvertTo-Json -Depth 5)
Write-Host "Public $packageId/$Version verified from nuget.org: $publicHash"
