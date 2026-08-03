#Requires -Version 5.1
<#
.SYNOPSIS
    Builds a Release copy of WaterparkSimTwitchExpansion and zips it into a package end users
    can extract directly into their game folder - no .NET SDK, git, or PowerShell flags needed
    on their end.

.DESCRIPTION
    Unlike install.ps1 (which builds+deploys straight into a local game install for your own
    testing), this produces a standalone WaterparkSimTwitchExpansion-vX.Y.Z.zip shaped like:
        BepInEx\plugins\WaterparkSimTwitchExpansion\WaterparkSimTwitchExpansion.dll
        BepInEx\plugins\WaterparkSimTwitchExpansion\<dependency DLLs>
    ...so extracting it directly on top of a Waterpark Simulator install (that already has the
    BepInEx IL2CPP pack from https://www.nexusmods.com/waterparksimulator/mods/62 installed)
    just works.

.PARAMETER GameDir
    Path to a local Waterpark Simulator install with BepInEx (IL2CPP) already installed and run
    at least once - needed to build against the game's interop assemblies (see the main
    project's .csproj comments for why). This is YOUR build machine, not an end user's; the
    resulting zip has no dependency on GameDir once built.

.PARAMETER OutputDir
    Where to write the .zip. Defaults to a "release" folder at the repo root.

.EXAMPLE
    .\package.ps1 -GameDir "F:\SteamLibrary\steamapps\common\WaterPark Simulator"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GameDir,

    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

function Write-Step($message) {
    Write-Host "`n==> $message" -ForegroundColor Cyan
}

function Write-Info($message) {
    Write-Host "    $message" -ForegroundColor Gray
}

function Fail($message) {
    Write-Host "`nERROR: $message" -ForegroundColor Red
    exit 1
}

$GameDir = $GameDir.TrimEnd('\')
$RepoRoot = $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot 'WaterparkSimTwitchExpansion\WaterparkSimTwitchExpansion.csproj'

if (-not $OutputDir) {
    $OutputDir = Join-Path $RepoRoot 'release'
}

# --- Sanity checks ----------------------------------------------------------

Write-Step "Checking paths"

if (-not (Test-Path $ProjectPath)) {
    Fail "Project file not found at '$ProjectPath'. Run this script from the repo root."
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail ".NET SDK not found on PATH. Install the .NET 6 SDK: https://dotnet.microsoft.com/download/dotnet/6.0"
}

$InteropMarker = Join-Path $GameDir 'BepInEx\interop\UnityEngine.dll'
if (-not (Test-Path $InteropMarker)) {
    Fail "Interop assemblies not found at '$InteropMarker'. Run .\install.ps1 first (or launch the game once with BepInEx installed) so there's something to build against."
}

# --- Version (Plugin.cs is the single source of truth) ----------------------

$PluginCsPath = Join-Path $RepoRoot 'WaterparkSimTwitchExpansion\Plugin.cs'
$versionMatch = Select-String -Path $PluginCsPath -Pattern 'PluginVersion\s*=\s*"([^"]+)"' | Select-Object -First 1
if ($versionMatch) {
    $Version = $versionMatch.Matches[0].Groups[1].Value
}
else {
    $Version = '0.0.0'
    Write-Info "Couldn't find PluginVersion in Plugin.cs - defaulting to $Version"
}
Write-Info "Packaging version $Version"

# --- Build --------------------------------------------------------------

Write-Step "Building (Release)"

& dotnet build $ProjectPath -c Release -p:GameDir="$GameDir"
if ($LASTEXITCODE -ne 0) {
    Fail "Build failed (see errors above)."
}

$BuildOutputDir = Join-Path $RepoRoot 'WaterparkSimTwitchExpansion\bin\Release\net6.0'
$BuiltDll = Join-Path $BuildOutputDir 'WaterparkSimTwitchExpansion.dll'
if (-not (Test-Path $BuiltDll)) {
    Fail "Build succeeded but expected output not found at '$BuiltDll'."
}

# --- Package ---------------------------------------------------------------

Write-Step "Packaging"

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$StagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "waterpark-package-$([guid]::NewGuid())"
$PluginStagingDir = Join-Path $StagingDir 'BepInEx\plugins\WaterparkSimTwitchExpansion'
New-Item -ItemType Directory -Path $PluginStagingDir -Force | Out-Null

Copy-Item -Path (Join-Path $BuildOutputDir '*') -Destination $PluginStagingDir -Recurse -Force

# Debug symbols aren't useful to end users and just add size.
Get-ChildItem $PluginStagingDir -Filter '*.pdb' | Remove-Item -Force

# Bundle the plain-language setup guide at the zip root, so it's self-contained even for
# someone who downloaded it from Nexus/a Releases page and never saw the repo.
$SetupMdPath = Join-Path $RepoRoot 'SETUP.md'
if (Test-Path $SetupMdPath) {
    Copy-Item $SetupMdPath -Destination (Join-Path $StagingDir 'SETUP.md') -Force
}

$ZipPath = Join-Path $OutputDir "WaterparkSimTwitchExpansion-v$Version.zip"
if (Test-Path $ZipPath) {
    Remove-Item $ZipPath -Force
}
Compress-Archive -Path (Join-Path $StagingDir '*') -DestinationPath $ZipPath

Remove-Item $StagingDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "`nPackaged: $ZipPath" -ForegroundColor Green
Write-Info "Extract this directly into a Waterpark Simulator install folder that already has"
Write-Info "the BepInEx IL2CPP pack (https://www.nexusmods.com/waterparksimulator/mods/62) installed."
