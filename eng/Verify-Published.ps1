#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('All', 'Diagnostics', 'Completion.Abstractions', 'Completion', 'Completion.Tools')][string]$Project = 'All',
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

$allIds = @('Atelia.Diagnostics', 'Atelia.Completion.Abstractions', 'Atelia.Completion', 'Atelia.Completion.Tools')
$publishIds = @(if ($Project -eq 'All') { $allIds } else { "Atelia.$Project" })
$manifestName = if ($Project -eq 'All') { "manifest.$Version.json" } else { "manifest.$($publishIds[0]).$Version.json" }
$manifestPath = Join-Path $feed $manifestName
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
$schema = if ($Project -eq 'All') { 1 } else { 2 }
if ($manifest.schemaVersion -ne $schema -or $manifest.version -cne $Version -or
    @($manifest.packages).Count -ne $publishIds.Count -or
    $manifest.sourceRevision -cnotmatch '^[0-9a-f]{40}$' -or
    $manifest.repositoryUrl -cne 'https://github.com/Atelia-org/atelia-completion' -or !$manifest.sdkVersion -or
    ($Project -ne 'All' -and !$manifest.ContainsKey('dependencies'))) { throw "Invalid $Project release manifest." }

$expectedDependencies = @(if ($Project -in @('Completion', 'Completion.Tools')) {
    'Atelia.Diagnostics'; 'Atelia.Completion.Abstractions'
})
$dependencies = @(if ($Project -ne 'All') { $manifest.dependencies })
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

$candidates = @{}
for ($index = 0; $index -lt $publishIds.Count; $index++) {
    $id = $publishIds[$index]
    $candidate = $manifest.packages[$index]
    if ($candidate.id -cne $id -or $candidate.file -cne "$id.$Version.nupkg" -or
        $candidate.symbolsFile -cne "$id.$Version.snupkg" -or
        $candidate.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $candidate.symbolsSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "Invalid candidate file identity or hash in manifest: $id."
    }
    $candidatePath = Join-Path $feed $candidate.file
    $symbolsPath = Join-Path $feed $candidate.symbolsFile
    if ((Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $candidate.sha256 -or
        (Get-FileHash -LiteralPath $symbolsPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $candidate.symbolsSha256) {
        throw "Frozen candidate or symbols changed before public verification: $id."
    }
    $candidates[$id] = @{ entry = $candidate; path = $candidatePath }
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
$deadline = [DateTimeOffset]::UtcNow.AddMinutes(15)
$publicPackages = @{}
foreach ($packageId in $publishIds) {
    $publicPath = Join-Path $work "$packageId.$Version.nupkg"
    $normalizedId = $packageId.ToLowerInvariant()
    $normalizedVersion = $Version.ToLowerInvariant()
    $url = "https://api.nuget.org/v3-flatcontainer/$normalizedId/$normalizedVersion/$normalizedId.$normalizedVersion.nupkg"
    while ($true) {
        $remaining = ($deadline - [DateTimeOffset]::UtcNow).TotalSeconds
        if ($remaining -le 0) { throw "Public readback exceeded 15 minutes at $packageId. Rerun this read-only script with the archived feed and a new WorkDirectory." }
        try {
            Invoke-WebRequest -Uri $url -OutFile $publicPath -TimeoutSec ([Math]::Min(30, [Math]::Ceiling($remaining))) | Out-Null
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
    $candidateArchive = [IO.Compression.ZipFile]::OpenRead($candidates[$packageId].path)
    $publicArchive = [IO.Compression.ZipFile]::OpenRead($publicPath)
    try {
        if (!$publicArchive.GetEntry('.signature.p7s')) { throw "Downloaded public package has no NuGet signature: $packageId." }
        $candidateEntries = @($candidateArchive.Entries | ForEach-Object { $_.FullName } | Sort-Object)
        $publicEntries = @($publicArchive.Entries | Where-Object FullName -CNE '.signature.p7s' | ForEach-Object { $_.FullName } | Sort-Object)
        if (($candidateEntries -join '|') -cne ($publicEntries -join '|')) { throw "Published package asset list differs from candidate: $packageId." }
        foreach ($name in $candidateEntries) {
            if ((Get-EntryHash ($candidateArchive.GetEntry($name))) -cne (Get-EntryHash ($publicArchive.GetEntry($name)))) {
                throw "Published package asset differs from candidate: $packageId/$name"
            }
        }
        $metadata = Assert-NuspecIdentity (Read-Nuspec $publicArchive $packageId) $packageId $Version $manifest.sourceRevision $manifest.repositoryUrl
        $direct = @($metadata.SelectNodes('.//*[local-name()="dependency"]') | Where-Object { $_.GetAttribute('id') -like 'Atelia.*' })
        $expectedDirect = @(if ($Project -eq 'All') {
            if ($packageId -in @('Atelia.Completion', 'Atelia.Completion.Tools')) {
                [ordered]@{ id = 'Atelia.Diagnostics'; version = $Version }
                [ordered]@{ id = 'Atelia.Completion.Abstractions'; version = $Version }
            }
        } else { $dependencies })
        if ($direct.Count -ne $expectedDirect.Count) { throw "Published package direct dependency count differs: $packageId." }
        foreach ($dependency in $expectedDirect) {
            $match = @($direct | Where-Object { $_.GetAttribute('id') -ceq $dependency.id })
            if ($match.Count -ne 1 -or
                $match[0].GetAttribute('version') -cnotin @($dependency.version, "[$($dependency.version), )", "[$($dependency.version),)")) {
                throw "Published package direct dependency lower bound differs: $packageId -> $($dependency.id)."
            }
        }
    }
    finally { $candidateArchive.Dispose(); $publicArchive.Dispose() }
    $publicPackages[$packageId] = @{ path = $publicPath; sha256 = $publicHash; url = $url }
}

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
$savedEnvironment = @{}
foreach ($name in @('NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'NUGET_FALLBACK_PACKAGES')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$env:NUGET_PACKAGES = Join-Path $work 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $work 'http-cache'
$env:NUGET_FALLBACK_PACKAGES = ''
$checks = [Collections.Generic.List[object]]::new()
try {
    $publicPaths = @($publishIds | ForEach-Object { $publicPackages[$_].path })
    & dotnet nuget verify @publicPaths --all --configfile (Join-Path $work 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw 'NuGet signature verification failed.' }
    foreach ($packageId in $publishIds) {
        $probe = Join-Path $work "PublicProbe-$packageId"
        [void][IO.Directory]::CreateDirectory($probe)
        $projectPath = Join-Path $probe 'PublicProbe.csproj'
        Write-Utf8 $projectPath "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup><ItemGroup><PackageReference Include=`"$packageId`" Version=`"$Version`" /></ItemGroup></Project>"
        $type = switch ($packageId) {
            Atelia.Diagnostics { 'Atelia.Diagnostics.DebugUtil' }
            Atelia.Completion.Abstractions { 'Atelia.Completion.Abstractions.CompletionRequest' }
            Atelia.Completion { 'Atelia.Completion.OpenAI.OpenAIChatClient' }
            Atelia.Completion.Tools { 'Atelia.Completion.Tools.MethodToolWrapper' }
        }
        Write-Utf8 (Join-Path $probe 'Program.cs') "System.Console.WriteLine(typeof($type).Assembly.GetName().Name);"
        $expectedRecords = @([ordered]@{ id = $packageId; version = $Version; sha256 = $publicPackages[$packageId].sha256; sourceRevision = $manifest.sourceRevision })
        if ($packageId -in @('Atelia.Completion', 'Atelia.Completion.Tools')) {
            foreach ($dependencyId in @('Atelia.Diagnostics', 'Atelia.Completion.Abstractions')) {
                if ($Project -eq 'All') {
                    $expectedRecords += [ordered]@{ id = $dependencyId; version = $Version; sha256 = $publicPackages[$dependencyId].sha256; sourceRevision = $manifest.sourceRevision }
                } else {
                    $dependency = @($dependencies | Where-Object { $_.id -ceq $dependencyId })[0]
                    $expectedRecords += $dependency
                }
            }
        }
        & dotnet restore $projectPath --configfile (Join-Path $work 'NuGet.Config') --packages $env:NUGET_PACKAGES --no-http-cache '-p:RestoreFallbackFolders=' '-p:RestoreAdditionalProjectSources=' '-p:RestoreAdditionalProjectFallbackFolders='
        if ($LASTEXITCODE -ne 0) { throw "Public NuGet restore failed: $packageId." }
        $assets = Get-Content -LiteralPath (Join-Path $probe 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
        if (@($assets.libraries.Values | Where-Object { $_.type -eq 'project' }).Count -ne 0 -or $assets.packageFolders.Count -ne 1 -or
            ![IO.Path]::GetFullPath(@($assets.packageFolders.Keys)[0]).TrimEnd('/','\').Equals(
                [IO.Path]::GetFullPath($env:NUGET_PACKAGES).TrimEnd('/','\'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Public consumer resolved project references or an unexpected package cache: $packageId."
        }
        $expected = @($expectedRecords | ForEach-Object { "$($_.id)/$($_.version)" } | Sort-Object)
        $actual = @($assets.libraries.Keys | Where-Object { $_ -like 'Atelia.*/*' } | Sort-Object)
        if (($actual -join '|') -cne ($expected -join '|')) {
            throw "Public Atelia package closure differs for $packageId`: $($actual -join ', ')."
        }
        & dotnet run --project $projectPath -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Public package consumer failed: $packageId." }
        foreach ($record in $expectedRecords) {
            $id = ([string]$record.id).ToLowerInvariant()
            $resolvedVersion = ([string]$record.version).ToLowerInvariant()
            $cached = Join-Path $env:NUGET_PACKAGES "$id/$resolvedVersion/$id.$resolvedVersion.nupkg"
            if ((Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash.ToLowerInvariant() -cne $record.sha256) {
                throw "Restored package bytes differ from frozen public evidence: $($record.id)."
            }
            if (!$publicPackages.ContainsKey($record.id)) {
                $archive = [IO.Compression.ZipFile]::OpenRead($cached)
                try { [void](Assert-NuspecIdentity (Read-Nuspec $archive $record.id) $record.id $record.version $record.sourceRevision $manifest.repositoryUrl) }
                finally { $archive.Dispose() }
            }
        }
        $checks.Add([ordered]@{
            id = $packageId; version = $Version; candidateSha256 = $candidates[$packageId].entry.sha256
            candidateSymbolsSha256 = $candidates[$packageId].entry.symbolsSha256
            publishedSha256 = $publicPackages[$packageId].sha256; publicUrl = $publicPackages[$packageId].url
            resolvedPackages = $actual
        })
    }
}
finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
}

$result = [ordered]@{ project = $Project; version = $Version; sourceRevision = $manifest.sourceRevision; checks = $checks.ToArray() }
if ($Project -ne 'All') {
    $check = $checks[0]
    $result.id = $check.id
    $result.candidateSha256 = $check.candidateSha256
    $result.candidateSymbolsSha256 = $check.candidateSymbolsSha256
    $result.publishedSha256 = $check.publishedSha256
    $result.publicUrl = $check.publicUrl
    $result.resolvedPackages = $check.resolvedPackages
    $result.dependencies = @($dependencies | ForEach-Object { [ordered]@{ id = $_.id; version = $_.version; sha256 = $_.sha256; sourceRevision = $_.sourceRevision } })
}
Write-Utf8 (Join-Path $work 'published-check.json') ($result | ConvertTo-Json -Depth 5)
Write-Host "Public $Project/$Version verified from nuget.org: $($checks.Count) package consumer(s)."
