[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[a-z0-9]+([.-][a-z0-9]+)*)?$')]
    [string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Diagnostics', 'Completion.Abstractions', 'Completion', 'Completion.Tools')][string]$Project,
    [hashtable]$DependencyVersions
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$feed = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$repositoryUrl = 'https://github.com/Atelia-org/atelia-completion'
$projects = @('Diagnostics', 'Completion.Abstractions', 'Completion', 'Completion.Tools')

function Get-GitValue([string[]]$Arguments) {
    $value = & git -C $repo @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git failed: $Arguments" }
    return ($value -join "`n").Trim()
}

function Assert-CleanSource {
    if (Get-GitValue @('status', '--porcelain', '--untracked-files=normal')) {
        throw 'Pack requires a clean committed source tree. Commit changes and choose an ignored or external output directory.'
    }
}

function Read-PackageMetadata([string]$Path, [string]$ExpectedId, [string]$ExpectedVersion, [bool]$RequireSignature) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $signatures = @($archive.Entries | Where-Object FullName -CEQ '.signature.p7s')
        if ($RequireSignature -and ($signatures.Count -ne 1 -or $signatures[0].Length -le 0)) { throw "Expected signed public package: $Path" }
        if (!$RequireSignature -and $signatures.Count -ne 0) { throw "New candidate is already signed: $Path" }
        $specs = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
        if ($specs.Count -ne 1) { throw "Expected one nuspec: $Path" }
        $reader = [IO.StreamReader]::new($specs[0].Open())
        try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $spec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        if ($metadata.SelectSingleNode('*[local-name()="id"]').InnerText -cne $ExpectedId -or
            $metadata.SelectSingleNode('*[local-name()="version"]').InnerText -cne $ExpectedVersion) {
            throw "Wrong package identity: $Path"
        }
        $repository = $metadata.SelectSingleNode('*[local-name()="repository"]')
        if (!$repository -or $repository.GetAttribute('url') -cne $repositoryUrl -or
            $repository.GetAttribute('type') -cne 'git' -or
            $repository.GetAttribute('commit') -cnotmatch '^[0-9a-f]{40}$') {
            throw "Missing repository provenance: $Path"
        }
        return [pscustomobject]@{ sourceRevision = $repository.GetAttribute('commit'); metadata = $metadata }
    } finally { $archive.Dispose() }
}

function Get-PublicPackage([string]$Id, [string]$PackageVersion, [string]$Destination) {
    $lowerId = $Id.ToLowerInvariant()
    $lowerVersion = $PackageVersion.ToLowerInvariant()
    $url = "https://api.nuget.org/v3-flatcontainer/$lowerId/$lowerVersion/$lowerId.$lowerVersion.nupkg"
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try { Invoke-WebRequest -Uri $url -OutFile $Destination -TimeoutSec 60 -ErrorAction Stop; return }
        catch {
            if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination }
            if ($_.Exception -is [Microsoft.PowerShell.Commands.HttpResponseException] -and
                $_.Exception.Response.StatusCode -eq [Net.HttpStatusCode]::NotFound) {
                throw "Published dependency does not exist: $Id $PackageVersion"
            }
            if ($attempt -eq 3) { throw }
            Start-Sleep -Seconds (2 * $attempt)
        }
    }
}

function Assert-UnusedVersion([string]$Id, [string]$PackageVersion) {
    $lowerId = $Id.ToLowerInvariant()
    $lowerVersion = $PackageVersion.ToLowerInvariant()
    $url = "https://api.nuget.org/v3-flatcontainer/$lowerId/$lowerVersion/$lowerId.$lowerVersion.nupkg"
    try {
        Invoke-WebRequest -Uri $url -Method Head -TimeoutSec 60 -ErrorAction Stop | Out-Null
        throw "$Id $PackageVersion already exists on nuget.org."
    } catch {
        if ($_.Exception -isnot [Microsoft.PowerShell.Commands.HttpResponseException] -or
            $_.Exception.Response.StatusCode -ne [Net.HttpStatusCode]::NotFound) { throw }
    }
}

Push-Location $repo
try {
    Assert-CleanSource
    $revision = Get-GitValue @('rev-parse', 'HEAD')
    $origin = (Get-GitValue @('remote', 'get-url', 'origin')) -replace '\.git$', ''
    if ($origin -cne $repositoryUrl) { throw "origin must be $repositoryUrl (optional .git suffix) for Source Link." }
    if ($Project) {
        $requiredIds = @(if ($Project -in @('Completion', 'Completion.Tools')) { 'Atelia.Diagnostics'; 'Atelia.Completion.Abstractions' })
        if ($requiredIds.Count -gt 0 -and !$PSBoundParameters.ContainsKey('DependencyVersions')) { throw '-DependencyVersions is required for Completion and Completion.Tools.' }
        if (!$DependencyVersions) { $DependencyVersions = @{} }
        $providedIds = @($DependencyVersions.Keys | ForEach-Object { [string]$_ })
        if ($providedIds.Count -ne $requiredIds.Count -or @($providedIds | Where-Object { $requiredIds -cnotcontains $_ }).Count -ne 0) {
            throw "DependencyVersions for $Project must contain exactly: $($requiredIds -join ', ')."
        }
        foreach ($id in $requiredIds) {
            if ($DependencyVersions[$id] -isnot [string] -or
                $DependencyVersions[$id] -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[a-z0-9]+([.-][a-z0-9]+)*)?$') {
                throw "Invalid dependency version for $id."
            }
        }
        $id = "Atelia.$Project"
        $file = "$id.$Version.nupkg"
        $symbols = "$id.$Version.snupkg"
        $manifestPath = Join-Path $feed "manifest.$id.$Version.json"
        if (Test-Path -LiteralPath $feed) {
            if (!(Test-Path -LiteralPath $feed -PathType Container) -or @(Get-ChildItem -LiteralPath $feed -Force).Count -ne 0) {
                throw 'OutputDirectory must be absent or empty. Existing candidates are never overwritten.'
            }
        }
        $requiredSdk = (Get-Content -LiteralPath (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json).sdk.version
        $sdkVersion = (& dotnet --version | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $sdkVersion -cne $requiredSdk) { throw "Pack requires .NET SDK $requiredSdk; selected '$sdkVersion'." }
        Assert-UnusedVersion $id $Version
        [void][IO.Directory]::CreateDirectory($feed)
        $stage = Join-Path $feed ('.pack-' + [Guid]::NewGuid().ToString('N'))
        $dependencyFeed = Join-Path $stage 'public-dependencies'
        [void][IO.Directory]::CreateDirectory($dependencyFeed)
        $dependencies = @()
        foreach ($dependencyId in $requiredIds) {
            $dependencyVersion = [string]$DependencyVersions[$dependencyId]
            $dependencyFile = "$dependencyId.$dependencyVersion.nupkg"
            $downloaded = Join-Path $dependencyFeed $dependencyFile
            Get-PublicPackage $dependencyId $dependencyVersion $downloaded
            $metadata = Read-PackageMetadata $downloaded $dependencyId $dependencyVersion $true
            $dependencies += [ordered]@{
                id = $dependencyId; version = $dependencyVersion; file = $dependencyFile
                sha256 = (Get-FileHash -LiteralPath $downloaded -Algorithm SHA256).Hash.ToLowerInvariant()
                sourceRevision = $metadata.sourceRevision
            }
        }
        $escapedFeed = [Security.SecurityElement]::Escape($dependencyFeed)
        $maps = ($requiredIds | ForEach-Object { '<package pattern="' + $_ + '" />' }) -join ''
        $config = if ($requiredIds.Count -eq 0) { @'
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources><fallbackPackageFolders><clear /></fallbackPackageFolders></configuration>
'@ } else { @"
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /><add key="frozen" value="$escapedFeed" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources><fallbackPackageFolders><clear /></fallbackPackageFolders><packageSourceMapping><clear /><packageSource key="frozen">$maps</packageSource><packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping></configuration>
"@ }
        $configPath = Join-Path $stage 'NuGet.Config'
        [IO.File]::WriteAllText($configPath, $config, [Text.UTF8Encoding]::new($false))
        $obj = Join-Path $stage 'obj'
        $bin = Join-Path $stage 'bin'
        $cache = Join-Path $stage 'cache'
        $projectPath = Join-Path $repo "src/$Project/$Project.csproj"
        $props = @('-p:CompletionPackageMode=true', '-p:DefaultItemExcludesInProjectFolder=obj/**',
            "-p:PackageVersion=$Version", "-p:RepositoryCommit=$revision",
            '-p:RepositoryBranch=', '-p:ContinuousIntegrationBuild=true',
            "-p:BaseIntermediateOutputPath=$obj/", "-p:MSBuildProjectExtensionsPath=$obj/",
            "-p:BaseOutputPath=$bin/", "-p:RestorePackagesPath=$cache/")
        if ($requiredIds.Count -gt 0) {
            $props += "-p:CompletionDependencyVersionDiagnostics=$($DependencyVersions['Atelia.Diagnostics'])"
            $props += "-p:CompletionDependencyVersionAbstractions=$($DependencyVersions['Atelia.Completion.Abstractions'])"
        }
        & dotnet restore $projectPath --configfile $configPath --no-http-cache @props | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Isolated restore failed for $Project; stage retained at $stage." }
        $assets = Get-Content -LiteralPath (Join-Path $obj 'project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
        if (@($assets.libraries.Values | Where-Object { $_.type -eq 'project' }).Count -ne 0) { throw 'Package-mode restore unexpectedly used ProjectReference.' }
        foreach ($dependency in $dependencies) {
            $key = "$($dependency.id)/$($dependency.version)"
            if (!$assets.libraries.ContainsKey($key) -or
                @($assets.libraries.Keys | Where-Object { $_ -like "$($dependency.id)/*" }).Count -ne 1) {
                throw "Restored dependency differs from declared lower bound: $key"
            }
            $library = $assets.libraries[$key]
            $cached = Join-Path $cache "$($library.path)/$($dependency.id.ToLowerInvariant()).$($dependency.version.ToLowerInvariant()).nupkg"
            if ((Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash.ToLowerInvariant() -cne $dependency.sha256) {
                throw "Restored bytes differ from frozen public package: $key"
            }
        }
        & dotnet pack $projectPath -c Release -o $stage --no-restore @props | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $Project; stage retained at $stage." }
        $metadata = Read-PackageMetadata (Join-Path $stage $file) $id $Version $false
        if ($metadata.sourceRevision -cne $revision) { throw 'Candidate source revision differs from HEAD.' }
        $actualDependencies = @($metadata.metadata.SelectNodes("*[local-name()='dependencies']/*[local-name()='group']/*[local-name()='dependency']"))
        if ($actualDependencies.Count -ne $dependencies.Count) { throw 'Nuspec direct dependency count differs from declared inputs.' }
        foreach ($dependency in $dependencies) {
            $matches = @($actualDependencies | Where-Object { $_.GetAttribute('id') -ceq $dependency.id })
            if ($matches.Count -ne 1 -or $matches[0].GetAttribute('version') -cne $dependency.version) {
                throw "Nuspec lower bound differs from declared input: $($dependency.id)"
            }
        }
        Assert-CleanSource
        if ((Get-GitValue @('rev-parse', 'HEAD')) -cne $revision) { throw 'Source commit changed during Pack.' }
        foreach ($dependency in $dependencies) { [IO.File]::Move((Join-Path $dependencyFeed $dependency.file), (Join-Path $feed $dependency.file)) }
        [IO.File]::Move((Join-Path $stage $file), (Join-Path $feed $file))
        [IO.File]::Move((Join-Path $stage $symbols), (Join-Path $feed $symbols))
        $packages = @([ordered]@{
            id = $id; file = $file; sha256 = (Get-FileHash -LiteralPath (Join-Path $feed $file) -Algorithm SHA256).Hash.ToLowerInvariant()
            symbolsFile = $symbols; symbolsSha256 = (Get-FileHash -LiteralPath (Join-Path $feed $symbols) -Algorithm SHA256).Hash.ToLowerInvariant()
        })
        $manifest = [ordered]@{
            schemaVersion = 2; version = $Version; sourceRevision = $revision; repositoryUrl = $repositoryUrl
            sdkVersion = $sdkVersion; packages = $packages; dependencies = $dependencies
        } | ConvertTo-Json -Depth 5
        [IO.File]::WriteAllText($manifestPath, $manifest + "`n", [Text.UTF8Encoding]::new($false))
        Write-Host "Packed $id $Version against frozen public dependencies. No remote publication was performed."
        Write-Output $manifestPath
        return
    }
    if ($PSBoundParameters.ContainsKey('DependencyVersions')) { throw '-DependencyVersions requires -Project.' }
    if (Test-Path -LiteralPath $feed) {
        if (!(Test-Path -LiteralPath $feed -PathType Container) -or @(Get-ChildItem -LiteralPath $feed -Force).Count -ne 0) {
            throw 'OutputDirectory must be absent or empty. Existing packages are never overwritten; choose a new version and output directory.'
        }
    }
    $requiredSdk = (Get-Content -LiteralPath (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $sdkVersion = (& dotnet --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -cne $requiredSdk) {
        throw "Pack requires .NET SDK $requiredSdk; selected '$sdkVersion'."
    }
    [void][IO.Directory]::CreateDirectory($feed)
    $stage = Join-Path $feed ('.pack-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($stage)
    $packages = @()
    foreach ($name in $projects) {
        # PackageVersion keeps the existing default assembly/file version independent of package releases.
        & dotnet pack (Join-Path $repo "src/$name/$name.csproj") -c Release -o $stage `
            "-p:PackageVersion=$Version" "-p:RepositoryCommit=$revision" '-p:RepositoryBranch=' '-p:ContinuousIntegrationBuild=true' | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $name. Partial artifacts retained at $stage." }
        $file = "Atelia.$name.$Version.nupkg"
        $symbols = "Atelia.$name.$Version.snupkg"
        $packages += [ordered]@{
            id = "Atelia.$name"
            file = $file
            sha256 = (Get-FileHash -LiteralPath (Join-Path $stage $file) -Algorithm SHA256).Hash.ToLowerInvariant()
            symbolsFile = $symbols
            symbolsSha256 = (Get-FileHash -LiteralPath (Join-Path $stage $symbols) -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    Assert-CleanSource
    if ((Get-GitValue @('rev-parse', 'HEAD')) -cne $revision) { throw 'Source commit changed during Pack.' }
    foreach ($package in $packages) {
        foreach ($file in @($package.file, $package.symbolsFile)) {
            # File.Move without overwrite rejects races or unexpected existing artifacts.
            [IO.File]::Move((Join-Path $stage $file), (Join-Path $feed $file))
        }
    }
    [IO.Directory]::Delete($stage, $false)
    $manifestPath = Join-Path $feed "manifest.$Version.json"
    $manifest = [ordered]@{
        schemaVersion = 1
        version = $Version
        sourceRevision = $revision
        repositoryUrl = $repositoryUrl
        sdkVersion = $sdkVersion
        packages = $packages
    } | ConvertTo-Json -Depth 5
    # Manifest is written last. A failed run retains evidence and cannot be silently reused.
    $stream = [IO.File]::Open($manifestPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($manifest + "`n")
        $stream.Write($bytes, 0, $bytes.Length)
    }
    finally { $stream.Dispose() }
    Write-Host "Packed four libraries from $revision. No remote publication was performed."
    Write-Output $manifestPath
}
finally { Pop-Location }
