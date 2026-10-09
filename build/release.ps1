#Requires -Version 7.3
<#
.SYNOPSIS
    Builds the Hush installer: a self-contained Release publish packed with Velopack, optionally uploaded to GitHub Releases.

.DESCRIPTION
    1. Restores the repo-local tools (.config/dotnet-tools.json pins vpk).
    2. Publishes src/HushMusic.App for win-<Arch>, self-contained (.NET and the Windows App SDK runtime are inside,
       so users install nothing else) into artifacts/publish/<rid>.
    3. Runs `vpk pack` into artifacts/releases/<rid>: Setup.exe, a portable zip, the full (and delta) nupkg and the
       release feed files that installed copies read to update themselves.
    4. With -Upload: downloads the latest published release first (for delta updates), then uploads to GitHub Releases.

    The version is the one <Version> in Directory.Build.props. Everything goes to artifacts/ (git-ignored); the
    project's bin/ folder is never touched.

.PARAMETER Arch
    x64 (default, channel "win") or arm64 (channel "win-arm64").

.PARAMETER RepoUrl
    The GitHub repository installed copies check for updates and -Upload publishes to. Defaults to
    Updates:GitHubRepository in src/HushMusic.App/appsettings.json; when given, it is written into the published
    appsettings.json as well.

.PARAMETER Upload
    Upload the release with `vpk upload github`. Needs a token with write access to the repository's contents in
    $env:VPK_TOKEN or $env:GITHUB_TOKEN; it is passed to vpk through the environment only, never stored.

.PARAMETER Tag
    Git tag of the GitHub release (default v<Version>). Must match the version.

.PARAMETER Draft
    Leave the GitHub release as a draft instead of publishing it.

.PARAMETER ReleaseNotes
    Markdown file with the release notes, shown on the GitHub release.

.EXAMPLE
    ./build/release.ps1

.EXAMPLE
    $env:VPK_TOKEN = '<token>'; ./build/release.ps1 -Upload
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',
    [string]$RepoUrl,
    [switch]$Upload,
    [string]$Tag,
    [switch]$Draft,
    [string]$ReleaseNotes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Fixed for the life of the app: Velopack installs to %LOCALAPPDATA%\<PackId>, and installed copies only update from
# packages with the same id. It must differ from the data folder (%LOCALAPPDATA%\HushMusic), which uninstall keeps.
$PackId = 'HushMusic.App'
$Title = 'Hush'
$MainExe = 'HushMusic.exe'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\HushMusic.App\HushMusic.App.csproj'
$icon = Join-Path $root 'src\HushMusic.App\Assets\AppIcon.ico'
$rid = "win-$Arch"
$platform = if ($Arch -eq 'arm64') { 'ARM64' } else { 'x64' }
$channel = if ($Arch -eq 'arm64') { 'win-arm64' } else { 'win' }
$artifacts = Join-Path $root 'artifacts'
$buildDir = Join-Path $artifacts "build\$rid"
$publishDir = Join-Path $artifacts "publish\$rid"
$releasesDir = Join-Path $artifacts "releases\$rid"

function Invoke-Checked([string]$What, [scriptblock]$Command) {
    Write-Host "==> $What" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$What failed (exit code $LASTEXITCODE)."
    }
}

# ----- Version and repository -----
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).SelectSingleNode('/Project/PropertyGroup/Version')?.InnerText
if (-not $version) {
    throw 'No <Version> in Directory.Build.props.'
}
if (-not $Tag) {
    $Tag = "v$version"
}
if ($Tag -ne "v$version") {
    throw "Tag $Tag doesn't match the version $version in Directory.Build.props. Update <Version> (or the tag) so they agree."
}

$appSettings = Get-Content (Join-Path $root 'src\HushMusic.App\appsettings.json') -Raw | ConvertFrom-Json -AsHashtable
if (-not $RepoUrl) {
    $RepoUrl = $appSettings['Updates']?['GitHubRepository']
}
$RepoUrl = "$RepoUrl".Trim().TrimEnd('/')
if ($Upload -and -not $RepoUrl) {
    throw '-Upload needs a repository: pass -RepoUrl or set Updates:GitHubRepository in appsettings.json.'
}

Write-Host "Hush $version ($rid, channel $channel), package id $PackId"
Write-Host "Update source: $(if ($RepoUrl) { $RepoUrl } else { 'none (update checks off)' })"

# ----- Clean -----
foreach ($dir in $buildDir, $publishDir, $releasesDir) {
    if (Test-Path $dir) {
        Remove-Item $dir -Recurse -Force
    }
}

# ----- Tools -----
Push-Location $root
try {
    Invoke-Checked 'Restoring local tools (vpk)' { dotnet tool restore }
}
finally {
    Pop-Location
}

# ----- Publish -----
# Release turns on WindowsAppSDKSelfContained, ReadyToRun and no trimming in the csproj; they are spelled out here too
# rather than taken from the publish profiles (Properties/PublishProfiles is git-ignored, so CI wouldn't have them).
# No trimming: WinUI, the DI container and reflection-based JSON aren't trim-safe. ReadyToRun for a faster start
# (checked after the publish).
# HushOutputDir moves the app's build output under artifacts/, away from bin/ (a running dev copy may lock files there).
Invoke-Checked "Publishing $rid (self-contained, Release)" {
    dotnet publish $project -c Release -r $rid --self-contained true -nologo `
        "-p:Platform=$platform" `
        -p:PublishReadyToRun=true `
        -p:PublishTrimmed=false `
        -p:PublishSingleFile=false `
        "-p:HushOutputDir=$buildDir" `
        -o $publishDir
}

# The point of a self-contained build: no .NET or Windows App Runtime install needed. Fail loudly if that regresses.
$required = @{
    'coreclr.dll'                     = '.NET runtime'
    'Microsoft.ui.xaml.dll'           = 'WinUI (Windows App SDK runtime)'
    'Microsoft.WindowsAppRuntime.dll' = 'Windows App SDK runtime'
    $MainExe                          = 'app'
    'Velopack.dll'                    = 'Velopack'
}
foreach ($file in $required.Keys) {
    if (-not (Test-Path (Join-Path $publishDir $file))) {
        throw "The publish folder has no $file ($($required[$file])): it isn't self-contained."
    }
}
# A framework-dependent Windows App SDK build compiles in the bootstrapper's initializer (it looks for an installed
# Windows App Runtime); a self-contained one the reg-free WinRT initializer. The bootstrapper DLLs are copied either way.
$appAssembly = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes((Join-Path $publishDir 'HushMusic.dll')))
if ($appAssembly.Contains('BootstrapCS') -or -not $appAssembly.Contains('UndockedRegFreeWinRTCS')) {
    throw 'HushMusic.dll initializes the Windows App SDK as framework-dependent: users would need the Windows App Runtime installed.'
}

# ReadyToRun: precompiled assemblies carry a managed native header. Without it every start JITs the app and the large
# WinRT projections (about a third slower to the first window).
foreach ($assembly in 'HushMusic.dll', 'HushMusic.Core.dll', 'Microsoft.WinUI.dll', 'Microsoft.Windows.SDK.NET.dll') {
    $stream = [IO.File]::OpenRead((Join-Path $publishDir $assembly))
    try {
        $headers = [Reflection.PortableExecutable.PEReader]::new($stream).PEHeaders
        if (-not $headers.CorHeader -or $headers.CorHeader.ManagedNativeHeaderDirectory.Size -eq 0) {
            throw "$assembly isn't ReadyToRun-compiled."
        }
    }
    finally {
        $stream.Dispose()
    }
}

# Installed copies check the repository the release is uploaded to.
$publishedSettingsPath = Join-Path $publishDir 'appsettings.json'
$publishedSettings = Get-Content $publishedSettingsPath -Raw | ConvertFrom-Json -AsHashtable
if (-not $publishedSettings.Contains('Updates')) {
    $publishedSettings['Updates'] = [ordered]@{}
}
$publishedSettings['Updates']['GitHubRepository'] = $RepoUrl
$publishedSettings | ConvertTo-Json -Depth 10 | Set-Content $publishedSettingsPath -Encoding utf8NoBOM

# ----- Pack -----
New-Item -ItemType Directory -Force $releasesDir | Out-Null
$vpkToken = if ($env:VPK_TOKEN) { $env:VPK_TOKEN } else { $env:GITHUB_TOKEN }
$hadVpkToken = [bool]$env:VPK_TOKEN
$prerelease = $version.Contains('-')
try {
    if ($Upload) {
        if (-not $vpkToken) {
            throw '-Upload needs a GitHub token in $env:VPK_TOKEN or $env:GITHUB_TOKEN.'
        }

        # vpk reads the token from VPK_TOKEN: it never appears on a command line or in a file.
        $env:VPK_TOKEN = $vpkToken

        # The previous release lets vpk build a delta package, so installed copies download only what changed.
        Write-Host '==> Downloading the latest release for delta updates' -ForegroundColor Cyan
        $downloadArgs = @('vpk', '--skip-updates', 'download', 'github', '--repoUrl', $RepoUrl, '--channel', $channel, '--outputDir', $releasesDir)
        if ($prerelease) {
            $downloadArgs += '--pre'
        }
        Push-Location $root
        try {
            & dotnet @downloadArgs
        }
        finally {
            Pop-Location
        }
        if ($LASTEXITCODE -ne 0) {
            Write-Warning 'No earlier release could be downloaded (normal for the first release): this one has no delta package.'
        }
    }

    $packArgs = @(
        'vpk', '--skip-updates', 'pack',
        '--packId', $PackId,
        '--packVersion', $version,
        '--packTitle', $Title,
        '--packAuthors', 'HushMusic',
        '--packDir', $publishDir,
        '--mainExe', $MainExe,
        '--icon', $icon,
        '--channel', $channel,
        '--runtime', $rid,
        # Google sign-in uses WebView2; Setup installs it in the rare case it's missing (it ships with Windows 11).
        '--framework', 'webview2',
        '--outputDir', $releasesDir
    )
    if ($ReleaseNotes) {
        $packArgs += @('--releaseNotes', (Resolve-Path $ReleaseNotes).Path)
    }
    Push-Location $root
    try {
        Invoke-Checked 'Packing with Velopack' { dotnet @packArgs }
    }
    finally {
        Pop-Location
    }

    if ($Upload) {
        $uploadArgs = @(
            'vpk', '--skip-updates', 'upload', 'github',
            '--repoUrl', $RepoUrl,
            '--channel', $channel,
            '--outputDir', $releasesDir,
            '--tag', $Tag,
            '--releaseName', "$Title $version",
            # Several architectures go into the same GitHub release.
            '--merge'
        )
        if (-not $Draft) {
            $uploadArgs += '--publish'
        }
        if ($prerelease) {
            $uploadArgs += '--pre'
        }
        Push-Location $root
        try {
            Invoke-Checked "Uploading to $RepoUrl ($Tag)" { dotnet @uploadArgs }
        }
        finally {
            Pop-Location
        }
    }
}
finally {
    if (-not $hadVpkToken) {
        Remove-Item Env:VPK_TOKEN -ErrorAction SilentlyContinue
    }
}

# ----- Summary -----
Write-Host ''
Write-Host "Release files in $releasesDir" -ForegroundColor Green
Get-ChildItem $releasesDir -File | Sort-Object Name | ForEach-Object {
    '{0,-48} {1,10:N1} MB' -f $_.Name, ($_.Length / 1MB)
}
$publishSize = (Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum
'{0,-48} {1,10:N1} MB ({2} files)' -f 'Installed app (publish folder)', ($publishSize / 1MB), (Get-ChildItem $publishDir -Recurse -File).Count
