[CmdletBinding()]
param(
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [string]$Version,
    [string]$DotnetPath = 'dotnet',
    [string]$OutputRoot,
    [string]$BuildRecordPath,
    [string]$SigningCertificateThumbprint,
    [string]$TimestampServer = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$properties = [xml](Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props') -Raw)
$configuredVersion = [string]$properties.Project.PropertyGroup.Version
if (-not $configuredVersion -or ($Version -and $Version -ne $configuredVersion)) { throw 'Version must match Directory.Build.props.' }
$Version = $configuredVersion
$licensePath = Join-Path $repository 'LICENSE'
if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf) -or (Get-Item -LiteralPath $licensePath).Length -lt 100) {
    throw 'The approved application LICENSE must exist before packaging.'
}
$sdk = Get-Content -LiteralPath (Join-Path $repository 'global.json') -Raw | ConvertFrom-Json
$actualSdk = (& $DotnetPath --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $sdk.sdk.version) { throw 'The SDK must match global.json exactly.' }
if (-not $OutputRoot) { $OutputRoot = Join-Path $repository 'artifacts' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$outputName = "WinCSVTool-$Version-portable"
$output = Join-Path $OutputRoot $outputName
$archive = Join-Path $OutputRoot "WinCSVTool-$Version-win-x64-portable.zip"
$hashFile = Join-Path $OutputRoot "WinCSVTool-$Version-SHA256SUMS.txt"
foreach ($path in @($output, $archive, $hashFile)) {
    if (Test-Path -LiteralPath $path) { throw "An existing artifact will not be overwritten: $path" }
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$staging = Join-Path $OutputRoot ('.staging-' + [Guid]::NewGuid().ToString('N'))
$guiStage = Join-Path $staging 'gui'
$cliStage = Join-Path $staging 'cli'
$packageStage = Join-Path $staging $outputName
New-Item -ItemType Directory -Path $guiStage, $cliStage, $packageStage | Out-Null
$projects = @('WinCSVTool', 'WinCSVTool.Cli')
foreach ($name in $projects) {
    $project = Join-Path $repository "src/$name/$name.csproj"
    $projectProperties = [xml](Get-Content -LiteralPath $project -Raw)
    if ([string]$projectProperties.Project.PropertyGroup.RuntimeIdentifier -ne $Runtime -or [string]$projectProperties.Project.PropertyGroup.SelfContained -ne 'true') { throw 'Project runtime/self-contained settings do not match the package target.' }
    # Keep class-library restore RID-agnostic; executable projects already pin their RID.
    $restoreArguments = @('restore', $project, '--locked-mode', '-p:NuGetAudit=true', '-p:NuGetAuditMode=all')
    & $DotnetPath @restoreArguments
    if ($LASTEXITCODE -ne 0) { throw "Locked restore and dependency audit failed: $name" }
    $destination = if ($name -eq 'WinCSVTool') { $guiStage } else { $cliStage }
    $publishArguments = @('publish', $project, '-c', 'Release', '--no-restore',
        '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishReadyToRun=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-p:UseSharedCompilation=false',
        '-p:Deterministic=true', '-m:1', '-o', $destination)
    & $DotnetPath @publishArguments
    if ($LASTEXITCODE -ne 0) { throw "Portable publish failed: $name" }
    $executable = Join-Path $destination "$name.exe"
    if ((Get-Item -LiteralPath $executable).Length -ge 100000000) { throw "EXE exceeds 100,000,000 bytes: $name" }
    Copy-Item -LiteralPath $executable -Destination $packageStage
}
foreach ($name in @('README.md', 'THIRD-PARTY-NOTICES.txt', 'LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $packageStage
}
New-Item -ItemType Directory -Path (Join-Path $packageStage 'docs/images') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repository 'docs/images/main-window.png') -Destination (Join-Path $packageStage 'docs/images/main-window.png')
$assetsPath = (& $DotnetPath msbuild (Join-Path $repository 'src/WinCSVTool/WinCSVTool.csproj') -getProperty:ProjectAssetsFile -nologo | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the package assets file.' }
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$noticesPath = Join-Path $packageStage 'THIRD-PARTY-NOTICES.txt'
foreach ($notice in @('SQLitePCLRaw-Apache-2.0.txt', 'MaxMind.Db-Apache-2.0.txt')) {
    Add-Content -LiteralPath $noticesPath -Value ([Environment]::NewLine + "===== $notice =====") -Encoding utf8
    Add-Content -LiteralPath $noticesPath -Value (Get-Content -LiteralPath (Join-Path $repository "licenses/$notice") -Raw) -Encoding utf8
}
$runtimePackages = @{}
foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
    foreach ($package in $framework.Value.downloadDependencies) {
        if ($package.name -match '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64$') {
            $packageVersion = [regex]::Match($package.version, '\d+\.\d+\.\d+[^,\]\s]*').Value
            $runtimePackages["$($package.name)/$packageVersion"] = "$($package.name.ToLowerInvariant())/$packageVersion"
        }
    }
}
foreach ($package in $assets.libraries.PSObject.Properties) {
    if ($package.Name -match '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64/') { $runtimePackages[$package.Name] = $package.Value.path }
}
if ($runtimePackages.Count -ne 2) { throw 'Both exact runtime license packages are required.' }
foreach ($package in $runtimePackages.GetEnumerator() | Sort-Object Key) {
    $packageDirectory = $null
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $folder $package.Value
        if (Test-Path -LiteralPath $candidate) { $packageDirectory = $candidate; break }
    }
    if (-not $packageDirectory) { throw "Missing runtime notices: $($package.Key)" }
    if ((-not (Test-Path -LiteralPath (Join-Path $packageDirectory 'LICENSE'))) -and (-not (Test-Path -LiteralPath (Join-Path $packageDirectory 'LICENSE.TXT')))) { throw "Missing runtime LICENSE: $($package.Key)" }
    # NETCore ships a third-party notice file. The pinned WindowsDesktop package
    # ships only LICENSE; do not require a notice file absent from that package.
    if ($package.Key -like 'Microsoft.NETCore.App.Runtime.*' -and -not (Test-Path -LiteralPath (Join-Path $packageDirectory 'THIRD-PARTY-NOTICES.TXT'))) { throw "Missing runtime third-party notices: $($package.Key)" }
    foreach ($name in @('LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
        $notice = Join-Path $packageDirectory $name
        if (Test-Path -LiteralPath $notice) {
            Add-Content -LiteralPath $noticesPath -Value ([Environment]::NewLine + "===== $($package.Key) / $name =====") -Encoding utf8
            Add-Content -LiteralPath $noticesPath -Value (Get-Content -LiteralPath $notice -Raw) -Encoding utf8
        }
    }
}
$packageFiles = [Collections.Generic.List[string]]::new()
$packageFiles.AddRange([string[]]@('WinCSVTool.exe', 'WinCSVTool.Cli.exe', 'README.md', 'THIRD-PARTY-NOTICES.txt', 'LICENSE', 'docs/images/main-window.png'))
$metadata = Get-Content -LiteralPath (Join-Path $repository 'data/ip/metadata.json') -Raw | ConvertFrom-Json
$ipDestination = Join-Path $packageStage 'data/ip'
New-Item -ItemType Directory -Path $ipDestination -Force | Out-Null
foreach ($name in @('metadata.json', 'README.md', 'LICENSE-CC-BY-4.0.txt', $metadata.CountryFile, $metadata.AsnFile, $metadata.AsnIndexFile)) {
    if (-not $name -or [IO.Path]::GetFileName($name) -ne $name -or $name -match '[/\\]') { throw 'Unsafe reference filename.' }
    Copy-Item -LiteralPath (Join-Path $repository "data/ip/$name") -Destination $ipDestination
    $packageFiles.Add("data/ip/$name")
}
foreach ($item in @(@($metadata.CountryFile, $metadata.CountrySha256), @($metadata.AsnFile, $metadata.AsnSha256), @($metadata.AsnIndexFile, $metadata.AsnIndexSha256))) {
    if ((Get-FileHash -LiteralPath (Join-Path $ipDestination $item[0]) -Algorithm SHA256).Hash -ne $item[1]) { throw 'Reference database hash mismatch.' }
}
$signed = $false
if ($SigningCertificateThumbprint) {
    $signTool = Get-Command signtool.exe -ErrorAction Stop
    foreach ($name in @('WinCSVTool.exe', 'WinCSVTool.Cli.exe')) {
        $file = Join-Path $packageStage $name
        & $signTool.Source sign /sha1 $SigningCertificateThumbprint /fd SHA256 /tr $TimestampServer /td SHA256 $file
        if ($LASTEXITCODE -ne 0) { throw "Code signing failed: $name" }
        & $signTool.Source verify /pa $file
        if ($LASTEXITCODE -ne 0 -or (Get-AuthenticodeSignature -LiteralPath $file).Status -ne 'Valid') { throw 'Signature verification failed.' }
    }
    $signed = $true
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$temporaryArchive = Join-Path $staging 'package.zip'
$stream = [IO.File]::Open($temporaryArchive, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in $packageFiles) {
        $entry = $zip.CreateEntry("$outputName/$name", [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $sourceStream = [IO.File]::OpenRead((Join-Path $packageStage $name))
        try {
            $entryStream = $entry.Open()
            try { $sourceStream.CopyTo($entryStream) } finally { $entryStream.Dispose() }
        } finally { $sourceStream.Dispose() }
    }
} finally { $zip.Dispose(); $stream.Dispose() }
$digest = (Get-FileHash -LiteralPath $temporaryArchive -Algorithm SHA256).Hash.ToLowerInvariant()
$hashLines = @("$digest  $([IO.Path]::GetFileName($archive))")
foreach ($name in @('WinCSVTool.exe', 'WinCSVTool.Cli.exe')) {
    $hashLines += "$((Get-FileHash -LiteralPath (Join-Path $packageStage $name) -Algorithm SHA256).Hash.ToLowerInvariant())  $outputName/$name"
}
$temporaryHashes = Join-Path $staging 'SHA256SUMS.txt'
[IO.File]::WriteAllLines($temporaryHashes, $hashLines, [Text.UTF8Encoding]::new($false))
$record = [ordered]@{ version = $Version; sdk = $actualSdk; runtime = '10.0.13'; signed = $signed; zip_sha256 = $digest; package_files = @($packageFiles); lock_sha256 = @{} }
foreach ($name in @('WinCSVTool.Core', 'WinCSVTool', 'WinCSVTool.Cli')) {
    $record.lock_sha256[$name] = (Get-FileHash -LiteralPath (Join-Path $repository "src/$name/packages.lock.json") -Algorithm SHA256).Hash
}
if ($BuildRecordPath) {
    if (Test-Path -LiteralPath $BuildRecordPath) { throw 'Build record already exists.' }
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($BuildRecordPath))) -Force | Out-Null
    $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $BuildRecordPath -Encoding utf8
}
[IO.Directory]::Move($packageStage, $output)
[IO.File]::Move($temporaryArchive, $archive)
[IO.File]::Move($temporaryHashes, $hashFile)
[PSCustomObject]@{ Version = $Version; Directory = $output; Archive = $archive; HashFile = $hashFile; SHA256 = $digest; Signed = $signed }
