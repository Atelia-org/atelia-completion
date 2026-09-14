[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[a-z0-9]+([.-][a-z0-9]+)*)?$')]
    [string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory
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

Push-Location $repo
try {
    Assert-CleanSource
    $revision = Get-GitValue @('rev-parse', 'HEAD')
    $origin = (Get-GitValue @('remote', 'get-url', 'origin')) -replace '\.git$', ''
    if ($origin -cne $repositoryUrl) { throw "origin must be $repositoryUrl (optional .git suffix) for Source Link." }
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
