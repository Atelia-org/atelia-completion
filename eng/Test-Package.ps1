#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('All', 'Diagnostics', 'Completion.Abstractions', 'Completion', 'Completion.Tools')][string]$Project = 'All',
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')][string]$Version,
    [Parameter(Mandatory)][string]$FeedDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-SelectivePackageSmoke {
    $repo = Split-Path $PSScriptRoot -Parent
    $feed = (Resolve-Path -LiteralPath $FeedDirectory).Path
    $work = [IO.Path]::GetFullPath($WorkDirectory)
    if (Test-Path -LiteralPath $work) { throw 'WorkDirectory must be a new directory; previous evidence is never overwritten.' }
    if ($work.Equals($repo, [StringComparison]::OrdinalIgnoreCase) -or
        $work.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'WorkDirectory must be outside the source repository.'
    }

    $id = "Atelia.$Project"
    $allIds = @('Atelia.Diagnostics', 'Atelia.Completion.Abstractions', 'Atelia.Completion', 'Atelia.Completion.Tools')
    $dependencyIds = @(if ($Project -in @('Completion', 'Completion.Tools')) {
        'Atelia.Diagnostics'; 'Atelia.Completion.Abstractions'
    })
    $manifestPath = Join-Path $feed "manifest.$id.$Version.json"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.schemaVersion -ne 2 -or $manifest.version -cne $Version -or
        $manifest.repositoryUrl -cne 'https://github.com/Atelia-org/atelia-completion' -or
        $manifest.sourceRevision -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Invalid selective package manifest identity, repository, or source revision.'
    }
    $requiredSdk = (Get-Content -LiteralPath (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json).sdk.version
    if ($manifest.sdkVersion -cne $requiredSdk) { throw 'Manifest SDK differs from this repository global.json.' }
    $sdk = (& dotnet --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdk -cne $requiredSdk) { throw "Expected .NET SDK $requiredSdk; selected $sdk." }
    $revision = (& git -C $repo rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $revision -cne $manifest.sourceRevision) { throw 'Checkout does not match candidate source revision.' }
    $status = & git -C $repo status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $status) { throw 'Package source verification requires a clean checkout.' }
    if (@($manifest.packages).Count -ne 1 -or $manifest.packages[0].id -cne $id -or
        @($manifest.dependencies).Count -ne $dependencyIds.Count) {
        throw 'Manifest candidate or frozen dependency set differs from selected project.'
    }
    $records = @{}
    $records[$id] = @{ version = $Version; entry = $manifest.packages[0] }
    foreach ($dependency in @($manifest.dependencies)) {
        if ($dependency.id -cnotin $dependencyIds -or $records.ContainsKey($dependency.id) -or
            $dependency.version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[a-z0-9]+([.-][a-z0-9]+)*)?$' -or
            $dependency.sourceRevision -cnotmatch '^[0-9a-f]{40}$') {
            throw "Invalid frozen dependency entry: $($dependency.id)."
        }
        $records[$dependency.id] = @{ version = $dependency.version; entry = $dependency }
    }
    foreach ($dependencyId in $dependencyIds) {
        if (!$records.ContainsKey($dependencyId)) { throw "Missing frozen dependency $dependencyId." }
    }

    function Assert-FeedFile([string]$Name, [string]$ExpectedHash) {
        if ($ExpectedHash -cnotmatch '^[0-9a-f]{64}$') { throw "Invalid SHA256 in manifest: $Name." }
        $path = Join-Path $feed $Name
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing frozen feed file: $Name." }
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -cne $ExpectedHash) { throw "Feed SHA256 mismatch: $Name." }
        return $path
    }
    function Read-PackageMetadata([string]$Path, [string]$ExpectedId, [string]$ExpectedVersion, [bool]$Signed) {
        $archive = [IO.Compression.ZipFile]::OpenRead($Path)
        try {
            $specs = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
            if ($specs.Count -ne 1) { throw "Expected one nuspec: $Path." }
            $signatures = @($archive.Entries | Where-Object FullName -CEQ '.signature.p7s')
            if (($Signed -and ($signatures.Count -ne 1 -or $signatures[0].Length -le 0)) -or
                (!$Signed -and $signatures.Count -ne 0)) { throw "Unexpected package signature state: $Path." }
            $reader = [IO.StreamReader]::new($specs[0].Open())
            try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $metadata = $spec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
            if ($null -eq $metadata -or $metadata.SelectSingleNode('*[local-name()="id"]').InnerText -cne $ExpectedId -or
                $metadata.SelectSingleNode('*[local-name()="version"]').InnerText -cne $ExpectedVersion) {
                throw "Wrong nuspec identity: $Path."
            }
            $repository = $metadata.SelectSingleNode('*[local-name()="repository"]')
            if ($null -eq $repository -or $repository.GetAttribute('type') -cne 'git' -or
                $repository.GetAttribute('url') -cne $manifest.repositoryUrl -or
                $repository.GetAttribute('commit') -cnotmatch '^[0-9a-f]{40}$') {
                throw "Invalid nuspec repository provenance: $Path."
            }
            $tfm = if ($ExpectedId -ceq 'Atelia.Diagnostics') { 'netstandard2.0' } else { 'net10.0' }
            if ($null -eq $archive.GetEntry("lib/$tfm/$ExpectedId.dll")) { throw "Missing package assembly: $Path." }
            return @{ metadata = $metadata; sourceRevision = $repository.GetAttribute('commit') }
        } finally { $archive.Dispose() }
    }

    foreach ($packageId in @($id) + $dependencyIds) {
        $record = $records[$packageId]
        $entry = $record.entry
        $packageVersion = $record.version
        $filename = "$packageId.$packageVersion.nupkg"
        if ($entry.file -cne $filename) { throw "Unexpected package filename for $packageId." }
        $path = Assert-FeedFile $filename $entry.sha256
        $metadata = Read-PackageMetadata $path $packageId $packageVersion ($packageId -cne $id)
        if ($packageId -ceq $id) {
            if ($metadata.sourceRevision -cne $manifest.sourceRevision) { throw 'Candidate nuspec commit differs from manifest.' }
            $symbolsName = "$packageId.$packageVersion.snupkg"
            if ($entry.symbolsFile -cne $symbolsName) { throw 'Unexpected candidate symbols filename.' }
            $symbolsPath = Assert-FeedFile $symbolsName $entry.symbolsSha256
            $symbols = [IO.Compression.ZipFile]::OpenRead($symbolsPath)
            try {
                $tfm = if ($Project -ceq 'Diagnostics') { 'netstandard2.0' } else { 'net10.0' }
                if ($null -eq $symbols.GetEntry("lib/$tfm/$id.pdb")) { throw 'Candidate symbols lack portable PDB.' }
            } finally { $symbols.Dispose() }
            $direct = @($metadata.metadata.SelectNodes('*[local-name()="dependencies"]/*[local-name()="group"]/*[local-name()="dependency"]') |
                Where-Object { $_.GetAttribute('id') -in $allIds })
            if ($direct.Count -ne $dependencyIds.Count) { throw 'Candidate nuspec direct Atelia dependency count differs from manifest.' }
            foreach ($dependencyId in $dependencyIds) {
                $matches = @($direct | Where-Object { $_.GetAttribute('id') -ceq $dependencyId })
                $lower = $records[$dependencyId].version
                if ($matches.Count -ne 1 -or $matches[0].GetAttribute('version') -cnotin @($lower, "[$lower, )", "[$lower,)")) {
                    throw "Candidate nuspec direct dependency lower bound differs from manifest: $dependencyId."
                }
            }
        } elseif ($metadata.sourceRevision -cne $entry.sourceRevision) {
            throw "Frozen public dependency source revision differs from manifest: $packageId."
        }
    }

    [void][IO.Directory]::CreateDirectory($work)
    $utf8 = [Text.UTF8Encoding]::new($false)
    function Write-Utf8([string]$Path, [string]$Content) { [IO.File]::WriteAllText($Path, $Content, $utf8) }
    foreach ($file in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
        Write-Utf8 (Join-Path $work $file) '<Project />'
    }
    Copy-Item -LiteralPath (Join-Path $repo 'global.json') -Destination $work
    $escapedFeed = [Security.SecurityElement]::Escape($feed)
    Write-Utf8 (Join-Path $work 'NuGet.Config') @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /><add key="frozen" value="$escapedFeed" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <fallbackPackageFolders><clear /></fallbackPackageFolders>
  <disabledPackageSources><clear /></disabledPackageSources>
  <packageSourceMapping><clear /><packageSource key="frozen"><package pattern="Atelia.*" /></packageSource><packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping>
</configuration>
"@
    $saved = @{}
    foreach ($name in @('NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'NUGET_FALLBACK_PACKAGES')) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    $env:NUGET_PACKAGES = Join-Path $work 'packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $work 'http-cache'
    $env:NUGET_FALLBACK_PACKAGES = ''
    try {
        # Compile the verifier outside the repository so the candidate checkout stays immutable.
        $checker = Join-Path $work 'SourceLinkCheck'
        [void][IO.Directory]::CreateDirectory($checker)
        $checkerPath = Join-Path $checker 'SourceLinkCheck.csproj'
        Write-Utf8 $checkerPath '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>'
        Write-Utf8 (Join-Path $checker 'Program.cs') @'
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 5) { throw new ArgumentException("Expected symbols, source root, package ID, repository URL, and commit."); }
string symbolsPath = Path.GetFullPath(args[0]);
string sourceRoot = Path.GetFullPath(args[1]).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
string project = args[2]["Atelia.".Length..];
string sourcePrefix = args[3].Replace("https://github.com/", "https://raw.githubusercontent.com/", StringComparison.Ordinal) + "/" + args[4] + "/";
string tfm = project == "Diagnostics" ? "netstandard2.0" : "net10.0";
using var archive = ZipFile.OpenRead(symbolsPath);
var pdbEntry = archive.GetEntry($"lib/{tfm}/{args[2]}.pdb") ?? throw new InvalidDataException("Candidate portable PDB is missing.");
using var pdbBytes = new MemoryStream();
using (var stream = pdbEntry.Open()) { stream.CopyTo(pdbBytes); }
pdbBytes.Position = 0;
using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbBytes);
var reader = provider.GetMetadataReader();
Guid sourceLinkKind = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");
var sourceLinks = reader.CustomDebugInformation.Select(handle => reader.GetCustomDebugInformation(handle))
    .Where(info => reader.GetGuid(info.Kind) == sourceLinkKind).ToArray();
if (sourceLinks.Length != 1) { throw new InvalidDataException("Expected one Source Link record in candidate PDB."); }
using var json = JsonDocument.Parse(reader.GetBlobBytes(sourceLinks[0].Value));
var mappings = json.RootElement.GetProperty("documents").EnumerateObject().ToArray();
int checkedDocuments = 0;
foreach (var handle in reader.Documents) {
    var document = reader.GetDocument(handle);
    string name = reader.GetString(document.Name).Replace('\\', '/');
    foreach (var mapping in mappings) {
        string pattern = mapping.Name.Replace('\\', '/');
        if (!pattern.EndsWith('*') || !name.StartsWith(pattern[..^1], StringComparison.Ordinal)) { continue; }
        string url = mapping.Value.GetString()!.Replace("*", name[(pattern.Length - 1)..], StringComparison.Ordinal);
        if (!url.StartsWith(sourcePrefix, StringComparison.Ordinal)) { continue; }
        string relative = Uri.UnescapeDataString(url[sourcePrefix.Length..]);
        if (!relative.StartsWith($"src/{project}/", StringComparison.Ordinal) ||
            relative.Contains("/obj/", StringComparison.Ordinal)) { continue; }
        string local = Path.GetFullPath(Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!local.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidDataException("Source Link path escapes the checkout.");
        }
        byte[] bytes = File.ReadAllBytes(local);
        Guid algorithm = reader.GetGuid(document.HashAlgorithm);
        byte[] hash = algorithm == new Guid("8829D00F-11B8-4213-878B-770E8597AC16") ? SHA256.HashData(bytes) :
            algorithm == new Guid("FF1816EC-AA5E-4D10-87F7-6F4963833460") ? SHA1.HashData(bytes) :
            throw new InvalidDataException("Unknown portable PDB source checksum algorithm.");
        if (!hash.AsSpan().SequenceEqual(reader.GetBlobBytes(document.Hash))) {
            throw new InvalidDataException($"Candidate source checksum mismatch: {relative}");
        }
        checkedDocuments++;
        break;
    }
}
if (checkedDocuments == 0) { throw new InvalidDataException("No project source documents were verified against candidate Source Link."); }
Console.WriteLine($"Verified {checkedDocuments} local source documents against candidate portable PDB and Source Link commit.");
'@
        & dotnet restore $checkerPath --configfile (Join-Path $work 'NuGet.Config') --packages $env:NUGET_PACKAGES --no-http-cache
        if ($LASTEXITCODE -ne 0) { throw 'Source Link checker restore failed.' }
        & dotnet run --project $checkerPath -c Release --no-restore -- (Join-Path $feed $manifest.packages[0].symbolsFile) $repo $id $manifest.repositoryUrl $manifest.sourceRevision
        if ($LASTEXITCODE -ne 0) { throw 'Candidate PDB / Source Link / local source checksum verification failed.' }

        $probe = Join-Path $work "${Project}Only"
        [void][IO.Directory]::CreateDirectory($probe)
        $projectPath = Join-Path $probe 'Probe.csproj'
        Write-Utf8 $projectPath "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><IsPackable>false</IsPackable></PropertyGroup><ItemGroup><PackageReference Include=`"$id`" Version=`"$Version`" /></ItemGroup></Project>"
        switch ($Project) {
            'Diagnostics' {
                $program = @'
using Atelia.Diagnostics;
DebugUtil.Warning("package-smoke", "public warning path");
if (typeof(DebugUtil).Assembly.GetName().Name != "Atelia.Diagnostics") { throw new Exception("Wrong diagnostics assembly."); }
'@
            }
            'Completion.Abstractions' {
                $program = @'
using Atelia.Completion.Abstractions;
var request = new CompletionRequest("smoke", new CompletionPromptPrefix("System", CompletionOutputContract.ProviderDefault([]), []), [new ObservationMessage("hello")]);
if (request.ModelId != "smoke" || request.TailMessages.Length != 1) { throw new Exception("Request contract lost."); }
try { _ = new CompletionRequest("", request.PromptPrefix, []); throw new Exception("Empty model accepted."); }
catch (ArgumentException) { }
'@
            }
            'Completion' {
                $program = @'
using System.Net;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.Completion.Transport;
using var http = new HttpClient(new Fixture()) { BaseAddress = new Uri("https://package-smoke.invalid/"), Timeout = Timeout.InfiniteTimeSpan };
var client = new OpenAIChatClient(null, http, OpenAIChatDialects.SgLangCompatible);
var request = new CompletionRequest("smoke", new CompletionPromptPrefix("System", CompletionOutputContract.ProviderDefault([]), []), [new ObservationMessage("hello")]);
var result = await client.StreamCompletionAsync(request, null);
if (result.Termination.Kind != CompletionTerminationKind.Completed || result.Message.GetFlattenedText() != "hello") { throw new Exception("Offline completion behavior failed."); }
sealed class Fixture : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hello\"},\"finish_reason\":\"stop\"}]}\n\n", Encoding.UTF8, "text/event-stream") });
}
'@
            }
            'Completion.Tools' {
                $program = @'
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
var tool = MethodToolWrapper.FromDelegate<EchoInput>(new EchoHost().EchoAsync);
var registry = new ToolRegistry([tool]);
if (registry.AllDefinitions.Length != 1 || registry.AllDefinitions[0].Name != "smoke.echo") { throw new Exception("Tool declaration failed."); }
var session = registry.CreateSession();
var result = await session.ExecuteAsync(new RawToolCall("smoke.echo", "call-1", "{\"text\":\"bound\"}"), default);
if (result.ExecuteResult.Status != ToolExecutionStatus.Success || result.ExecuteResult.GetFlattenedText() != "bound") { throw new Exception("Tool binding or execution failed."); }
public sealed record EchoInput([property: JsonPropertyName("text"), Required, Description("Text to echo.")] string Text);
public sealed class EchoHost {
    [Tool("smoke.echo", "Echo a bound string.")]
    public ValueTask<ToolExecuteResult> EchoAsync(EchoInput input, ToolExecutionContext context, CancellationToken ct) =>
        ValueTask.FromResult(ToolExecuteResult.FromText(ToolExecutionStatus.Success, input.Text));
}
'@
            }
        }
        Write-Utf8 (Join-Path $probe 'Program.cs') $program
        Push-Location $work
        try {
            & dotnet restore $projectPath --configfile (Join-Path $work 'NuGet.Config') --packages $env:NUGET_PACKAGES --no-http-cache '-p:RestoreFallbackFolders=' '-p:RestoreAdditionalProjectSources=' '-p:RestoreAdditionalProjectFallbackFolders='
            if ($LASTEXITCODE -ne 0) { throw 'Isolated selective PackageReference restore failed.' }
            $assetsPath = Join-Path $probe 'obj/project.assets.json'
            $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
            if ($assets.packageFolders.Count -ne 1 -or
                ![IO.Path]::GetFullPath(@($assets.packageFolders.Keys)[0]).TrimEnd('/','\').Equals(
                    [IO.Path]::GetFullPath($env:NUGET_PACKAGES).TrimEnd('/','\'), [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Restore did not use the private package cache.'
            }
            if (@($assets.libraries.Values | Where-Object { $_.type -eq 'project' }).Count -ne 0) { throw 'Probe contains a ProjectReference.' }
            $expected = @($id) + $dependencyIds | ForEach-Object { "$_/$($records[$_].version)" } | Sort-Object
            $actual = @($assets.libraries.Keys | Where-Object { $_ -like 'Atelia.*/*' } | Sort-Object)
            if (($actual -join '|') -cne ($expected -join '|')) { throw "Wrong Atelia package closure: $($actual -join ', ')." }
            $frameworks = @($assets.project.frameworks.Keys)
            if ($frameworks.Count -ne 1 -or
                (@($assets.project.frameworks[$frameworks[0]].dependencies.Keys | Where-Object { $_ -in $allIds }) -join '|') -cne $id) {
                throw "Probe must directly reference only $id."
            }
            $checks = @()
            foreach ($packageId in @($id) + $dependencyIds) {
                $record = $records[$packageId]
                $key = "$packageId/$($record.version)"
                if (!$assets.libraries.ContainsKey($key) -or $assets.libraries[$key].type -cne 'package') {
                    throw "Expected restored package $key."
                }
                $library = $assets.libraries[$key]
                $cached = Join-Path (Join-Path $env:NUGET_PACKAGES $library.path) "$($packageId.ToLowerInvariant()).$($record.version.ToLowerInvariant()).nupkg"
                $hash = (Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($hash -cne $record.entry.sha256) { throw "Private cache bytes differ from frozen feed: $key." }
                $stream = [IO.File]::OpenRead($cached)
                $sha = [Security.Cryptography.SHA512]::Create()
                try { $contentHash = [Convert]::ToBase64String($sha.ComputeHash($stream)) }
                finally { $stream.Dispose(); $sha.Dispose() }
                if ((Get-Content -LiteralPath "$cached.sha512" -Raw).Trim() -cne $contentHash -or
                    ($packageId -ceq $id -and $library.sha512 -cne $contentHash)) {
                    throw "NuGet content hash mismatch: $key."
                }
                $checks += @{ id = $packageId; version = $record.version; sha256 = $hash }
            }
            & dotnet build $projectPath -c Release --no-restore
            if ($LASTEXITCODE -ne 0) { throw "$Project independent probe build failed." }
            & dotnet run --project $projectPath -c Release --no-build --no-restore
            if ($LASTEXITCODE -ne 0) { throw "$Project independent public API smoke failed." }
            Write-Utf8 (Join-Path $work 'package-smoke-results.json') (@{ project = $Project; version = $Version; sourceRevision = $manifest.sourceRevision; manifest = $manifestPath; sourceLink = 'candidate PDB, commit mapping and local source checksums verified'; checks = $checks } | ConvertTo-Json -Depth 8)
            Write-Host "Selective package verification passed. Evidence: $work/package-smoke-results.json"
        } finally { Pop-Location }
    } finally {
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    }
}

if ($Project -ne 'All') { Invoke-SelectivePackageSmoke; return }

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
                $projectXml = (Get-Content -LiteralPath "$repoRoot/tests/PackageSmoke/PackageSmoke.csproj" -Raw).Replace('@VERSION@', $Version)
                Copy-Item -LiteralPath "$repoRoot/tests/PackageSmoke/Program.cs" -Destination "$directory/Program.cs"
                $expected = $ids
            } else {
                $id = if ($probe -eq 'DiagnosticsOnly') { 'Atelia.Diagnostics' } else { 'Atelia.Completion.Abstractions' }
                $type = if ($probe -eq 'DiagnosticsOnly') { 'Atelia.Diagnostics.DebugUtil' } else { 'Atelia.Completion.Abstractions.CompletionRequest' }
                $projectXml = "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup><ItemGroup><PackageReference Include=`"$id`" Version=`"$Version`" /></ItemGroup></Project>"
                if ($probe -eq 'DiagnosticsOnly') {
                    Write-Utf8 "$directory/Program.cs" @'
#nullable enable
using System;
using System.Reflection;
using System.Collections.Generic;

// Clean-process default-level probe: verify DebugUtil's private static fields
// resolve to File=Debug / Console=Warning when no environment variables are set.
Environment.SetEnvironmentVariable("ATELIA_DEBUG_FILE_LEVEL", null);
Environment.SetEnvironmentVariable("ATELIA_DEBUG_CONSOLE_LEVEL", null);
Type utilType = typeof(Atelia.Diagnostics.DebugUtil);
Type levelType = Assert.Single(utilType.GetNestedTypes(BindingFlags.NonPublic), static t => t.IsEnum);
object fileLevel = utilType.GetField("_fileLevel", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
object consoleLevel = utilType.GetField("_consoleLevel", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
if (!Equals(fileLevel, Enum.ToObject(levelType, 0))) { throw new InvalidOperationException($"Default file level is not Debug: {fileLevel}"); }
if (!Equals(consoleLevel, Enum.ToObject(levelType, 1))) { throw new InvalidOperationException($"Default console level is not Warning: {consoleLevel}"); }
Console.WriteLine(utilType.Assembly.GetName().Name);

static class Assert {
    public static T Single<T>(IEnumerable<T> source, Func<T, bool> predicate) {
        T? found = default; bool has = false;
        foreach (T item in source) { if (predicate(item)) { if (has) { throw new InvalidOperationException("More than one match."); } found = item; has = true; } }
        if (!has) { throw new InvalidOperationException("No match."); }
        return found!;
    }
}
'@
                } else {
                    Write-Utf8 "$directory/Program.cs" "System.Console.WriteLine(typeof($type).Assembly.GetName().Name);"
                }
                $expected = @($id)
            }
            $projectPath = Join-Path $directory "$probe.csproj"
            Write-Utf8 $projectPath $projectXml
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
