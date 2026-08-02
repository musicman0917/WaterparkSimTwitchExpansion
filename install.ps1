#Requires -Version 5.1
<#
.SYNOPSIS
    Builds WaterparkSimTwitchExpansion and deploys it into a local Waterpark Simulator install
    for testing.

.DESCRIPTION
    Waterpark Simulator is an IL2CPP Unity build, so this targets BepInEx 6 (IL2CPP). Instead of
    BepInEx's own unversioned CI "bleeding edge" builds, this installs the "BepInEx IL2CPP for
    Waterpark Simulator" pack from Nexus Mods (game-specific, pre-built):
    https://www.nexusmods.com/waterparksimulator/mods/62

    Downloading it requires a Nexus Premium account (the API's download_link endpoint 403s for
    free accounts - see -NexusApiKey below). Without Premium, or without -NexusApiKey, this
    script falls back to printing manual instructions and opening the mod page.

    Once BepInEx is installed and the game has been run once (to generate the interop
    assemblies the project builds against), this script builds the plugin and copies it into
    BepInEx\plugins.

.PARAMETER GameDir
    Path to the Waterpark Simulator install folder (the one containing WaterparkSimulator.exe).

.PARAMETER Configuration
    Build configuration, Debug or Release. Defaults to Debug.

.PARAMETER NexusApiKey
    Your personal Nexus Mods API key (Account Settings > API Keys) - requires Nexus Premium to
    auto-download the BepInEx pack. Falls back to $env:NEXUS_API_KEY if not passed, so you can
    avoid putting it in shell history: $env:NEXUS_API_KEY = 'your-key-here'. Never commit this.

.PARAMETER TwitchChannel
.PARAMETER BotUsername
.PARAMETER OAuthToken
    Optional - pre-seed BepInEx's config file with your Twitch settings so you don't have to
    launch-quit-edit-relaunch. Safe to omit and fill in the generated .cfg by hand instead.

.PARAMETER LaunchGame
    If set, launches the game after a successful install.

.EXAMPLE
    .\install.ps1 -GameDir "F:\SteamLibrary\steamapps\common\WaterPark Simulator" -LaunchGame

.EXAMPLE
    $env:NEXUS_API_KEY = 'your-personal-api-key'
    .\install.ps1 -GameDir "F:\SteamLibrary\steamapps\common\WaterPark Simulator" `
        -TwitchChannel "mychannel" -BotUsername "mychannel" -OAuthToken "oauth:xxxxxxxx" -LaunchGame
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GameDir,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$NexusApiKey = $env:NEXUS_API_KEY,

    [string]$TwitchChannel,
    [string]$BotUsername,
    [string]$OAuthToken,

    [switch]$LaunchGame
)

$ErrorActionPreference = 'Stop'

# Some Windows PowerShell 5.1 setups don't default to TLS 1.2, which would otherwise silently
# break the HTTPS calls to the Nexus API below.
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

# Downloads and installs the "BepInEx IL2CPP for Waterpark Simulator" pack
# (https://www.nexusmods.com/waterparksimulator/mods/62) via the Nexus Mods API. Requires
# Premium: the download_link.json endpoint 403s for free accounts. Returns $true on success,
# $false on any failure (caller falls back to manual instructions).
function Install-BepInExFromNexus([string]$ApiKey, [string]$GameDir) {
    $GameDomain = 'waterparksimulator'
    $ModId = 62
    $headers = @{ apikey = $ApiKey; 'User-Agent' = 'WaterparkSimTwitchExpansion-install.ps1' }

    try {
        Write-Info "Looking up mod files via Nexus API..."
        $filesResponse = Invoke-RestMethod -Uri "https://api.nexusmods.com/v1/games/$GameDomain/mods/$ModId/files.json" -Headers $headers
    }
    catch {
        Write-Info "Nexus API file lookup failed: $($_.Exception.Message)"
        return $false
    }

    $file = $filesResponse.files | Where-Object { $_.category_name -eq 'MAIN' } | Sort-Object uploaded_timestamp -Descending | Select-Object -First 1
    if (-not $file) {
        $file = $filesResponse.files | Sort-Object uploaded_timestamp -Descending | Select-Object -First 1
    }
    if (-not $file) {
        Write-Info "No downloadable files found for mod $ModId."
        return $false
    }
    Write-Info "Selected file: $($file.file_name) (v$($file.version))"

    try {
        $downloadResponse = Invoke-RestMethod -Uri "https://api.nexusmods.com/v1/games/$GameDomain/mods/$ModId/files/$($file.file_id)/download_link.json" -Headers $headers
    }
    catch {
        Write-Info "Nexus API download-link request failed: $($_.Exception.Message)"
        Write-Info "(This endpoint requires Nexus Premium - free accounts get a 403 here.)"
        return $false
    }

    $downloadUrl = $downloadResponse | Select-Object -First 1 -ExpandProperty URI -ErrorAction SilentlyContinue
    if (-not $downloadUrl) {
        Write-Info "Nexus API didn't return a download URL."
        return $false
    }

    $tempZip = Join-Path ([System.IO.Path]::GetTempPath()) $file.file_name
    $tempExtract = Join-Path ([System.IO.Path]::GetTempPath()) "waterpark-bepinex-$([guid]::NewGuid())"

    try {
        Write-Info "Downloading $($file.file_name)..."
        $prevProgressPreference = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue' # Invoke-WebRequest's progress bar is extremely slow in Windows PowerShell.
        Invoke-WebRequest -Uri $downloadUrl -OutFile $tempZip
        $ProgressPreference = $prevProgressPreference

        Write-Info "Extracting..."
        Expand-Archive -Path $tempZip -DestinationPath $tempExtract -Force

        # Find winhttp.dll wherever it landed, then copy EVERYTHING alongside it (doorstop_config.ini,
        # changelog, the BepInEx folder, etc.) rather than cherry-picking specific names - the pack's
        # exact file list isn't something to hardcode assumptions about.
        $winhttp = Get-ChildItem -Path $tempExtract -Recurse -File -Filter 'winhttp*' | Select-Object -First 1
        if (-not $winhttp) {
            Write-Info "Couldn't find a winhttp file inside the downloaded pack. Extracted to: $tempExtract"
            return $false
        }

        $packRoot = $winhttp.Directory.FullName
        Write-Info "Copying pack contents from '$packRoot' into '$GameDir'..."
        Copy-Item -Path (Join-Path $packRoot '*') -Destination $GameDir -Recurse -Force

        Write-Host "BepInEx IL2CPP pack installed from Nexus." -ForegroundColor Green
        return $true
    }
    catch {
        Write-Info "Download/extract failed: $($_.Exception.Message)"
        return $false
    }
    finally {
        Remove-Item $tempZip -Force -ErrorAction SilentlyContinue
        Remove-Item $tempExtract -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$GameDir = $GameDir.TrimEnd('\')
$RepoRoot = $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot 'WaterparkSimTwitchExpansion\WaterparkSimTwitchExpansion.csproj'
$PluginGuid = 'com.musicman0917.waterparksimtwitchexpansion'

# --- Sanity checks ---------------------------------------------------------

Write-Step "Checking paths"

if (-not (Test-Path $ProjectPath)) {
    Fail "Project file not found at '$ProjectPath'. Run this script from the repo root."
}

$GameExe = Join-Path $GameDir 'WaterparkSimulator.exe'
if (-not (Test-Path $GameExe)) {
    Fail "'$GameExe' not found. Double-check -GameDir points at the folder containing WaterparkSimulator.exe."
}
Write-Info "Game found: $GameExe"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail ".NET SDK not found on PATH. Install the .NET 6 SDK: https://dotnet.microsoft.com/download/dotnet/6.0"
}

# --- BepInEx IL2CPP core -----------------------------------------------------

Write-Step "Checking for BepInEx (IL2CPP build)"

$BepInExCore = Join-Path $GameDir 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'
$DoorstopConfigPath = Join-Path $GameDir 'doorstop_config.ini'

# Check both files, not just BepInExCore: a partial/older install (e.g. from before
# doorstop_config.ini started getting copied) would otherwise look "installed" and skip the
# fix-up below forever.
function Test-BepInExInstalled {
    (Test-Path $BepInExCore) -and (Test-Path $DoorstopConfigPath)
}

if ((-not (Test-BepInExInstalled)) -and $NexusApiKey) {
    Write-Info "Not fully installed locally - attempting automated install via Nexus API (requires Premium)..."
    if (Install-BepInExFromNexus -ApiKey $NexusApiKey -GameDir $GameDir) {
        # Re-check; don't trust the pack's internal layout blindly.
        if (-not (Test-BepInExInstalled)) {
            Fail "Nexus download completed but installation still looks incomplete (missing '$BepInExCore' or '$DoorstopConfigPath') - check what was copied into $GameDir."
        }
    }
    else {
        Write-Host "Automated Nexus install failed - falling back to manual instructions." -ForegroundColor Yellow
    }
}

if (-not (Test-BepInExInstalled)) {
    Write-Host @"

BepInEx's IL2CPP build isn't installed in this game folder yet.

Use the "BepInEx IL2CPP for Waterpark Simulator" pack on Nexus Mods - it's a pre-built,
game-specific IL2CPP pack (simpler than pulling a raw BepInEx bleeding-edge CI build).

If you have Nexus Premium, pass -NexusApiKey (or set `$env:NEXUS_API_KEY` first) and re-run this
script to have it downloaded and installed automatically. Otherwise, do it manually:

  1. Open https://www.nexusmods.com/waterparksimulator/mods/62
  2. Download the pack from the Files tab (requires a free Nexus account to download manually).
  3. Extract the zip, then move/copy its "winhttp" file and "BepInEx" folder into:
     $GameDir
     (so you end up with $GameDir\BepInEx\core\... and $GameDir\winhttp.dll)
  4. Re-run this script - it'll launch the game once to generate the interop assemblies.

"@ -ForegroundColor Yellow

    try { Start-Process 'https://www.nexusmods.com/waterparksimulator/mods/62' } catch { }
    exit 1
}
Write-Info "Found: $BepInExCore"
Write-Info "Found: $DoorstopConfigPath"

# --- Interop assemblies ------------------------------------------------------

Write-Step "Checking for generated interop assemblies"

$InteropDir = Join-Path $GameDir 'BepInEx\interop'
$InteropMarker = Join-Path $InteropDir 'UnityEngine.dll'

if (-not (Test-Path $InteropMarker)) {
    Write-Host "Interop assemblies not found yet - launching the game once to generate them." -ForegroundColor Yellow
    Write-Info "This can take a few minutes on the very first run (BepInEx is dumping IL2CPP metadata)."

    $LogPath = Join-Path $GameDir 'BepInEx\LogOutput.log'
    $DoorstopConfig = Join-Path $GameDir 'doorstop_config.ini'
    if (-not (Test-Path $DoorstopConfig)) {
        Write-Info "Warning: no doorstop_config.ini found at $DoorstopConfig - BepInEx likely won't load at all."
    }

    $proc = Start-Process -FilePath $GameExe -PassThru
    $timeoutSeconds = 900
    $earlyWarnSeconds = 120
    $elapsed = 0
    $pollSeconds = 5
    $warned = $false

    while (-not (Test-Path $InteropMarker) -and $elapsed -lt $timeoutSeconds) {
        Start-Sleep -Seconds $pollSeconds
        $elapsed += $pollSeconds

        # Non-fatal heads-up only - real game boot time (first-run antivirus/SmartScreen scanning
        # the freshly-downloaded winhttp.dll included) can easily exceed a minute, so this must
        # NOT abort the wait; it did in an earlier version and produced a false failure on a run
        # that was actually working fine.
        if (-not $warned -and $elapsed -ge $earlyWarnSeconds -and -not (Test-Path $LogPath)) {
            $warned = $true
            Write-Host "    Note: $LogPath still doesn't exist after ${elapsed}s. If this keeps going, doorstop_config.ini might be missing/disabled, or a Windows Defender/SmartScreen prompt might be blocking winhttp.dll - check for a popup. Still waiting..." -ForegroundColor Yellow
        }

        if ($proc.HasExited) {
            Fail "Game process exited before interop assemblies were generated. Check $LogPath for errors, then re-run this script."
        }
    }

    if (-not (Test-Path $InteropMarker)) {
        Fail "Timed out waiting for interop assemblies after $timeoutSeconds seconds. Check $LogPath, close the game, and re-run this script."
    }

    Write-Host "Interop assemblies generated. You can close the game now (or let it keep running)." -ForegroundColor Green
    Write-Info "Waiting 10s for file writes to finish before building..."
    Start-Sleep -Seconds 10
}
else {
    Write-Info "Found: $InteropMarker"
}

# --- Build --------------------------------------------------------------

Write-Step "Building ($Configuration)"

& dotnet build $ProjectPath -c $Configuration -p:GameDir="$GameDir"
if ($LASTEXITCODE -ne 0) {
    Fail "Build failed (see errors above)."
}

$BuiltDll = Join-Path $RepoRoot "WaterparkSimTwitchExpansion\bin\$Configuration\net6.0\WaterparkSimTwitchExpansion.dll"
if (-not (Test-Path $BuiltDll)) {
    Fail "Build succeeded but expected output not found at '$BuiltDll'."
}

# --- Deploy ---------------------------------------------------------------

Write-Step "Deploying plugin"

$PluginDir = Join-Path $GameDir 'BepInEx\plugins\WaterparkSimTwitchExpansion'
New-Item -ItemType Directory -Path $PluginDir -Force | Out-Null
Copy-Item $BuiltDll -Destination $PluginDir -Force
Write-Info "Copied to $PluginDir\WaterparkSimTwitchExpansion.dll"

# --- Config -----------------------------------------------------------------

$ConfigDir = Join-Path $GameDir 'BepInEx\config'
$ConfigPath = Join-Path $ConfigDir "$PluginGuid.cfg"

function Set-IniValue([System.Collections.Generic.List[string]]$lines, [string]$section, [string]$key, [string]$value) {
    if (-not $value) { return }

    $sectionHeader = "[$section]"
    $sectionIndex = $lines.IndexOf($sectionHeader)

    if ($sectionIndex -lt 0) {
        $lines.Add($sectionHeader)
        $lines.Add("$key = $value")
        return
    }

    $sectionEnd = $lines.Count
    for ($i = $sectionIndex + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\[.*\]') { $sectionEnd = $i; break }
    }

    for ($i = $sectionIndex + 1; $i -lt $sectionEnd; $i++) {
        if ($lines[$i] -match "^$([regex]::Escape($key))\s*=") {
            $lines[$i] = "$key = $value"
            return
        }
    }

    # Key not present in the section yet - insert right after the header.
    $lines.Insert($sectionIndex + 1, "$key = $value")
}

if ($TwitchChannel -or $BotUsername -or $OAuthToken) {
    Write-Step "Seeding Twitch config"
    New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null

    $lines = [System.Collections.Generic.List[string]]::new()
    if (Test-Path $ConfigPath) {
        Write-Info "Config already exists at $ConfigPath - updating [Twitch] values only, other settings left as-is."
        $existing = Get-Content $ConfigPath
        if ($existing) { $lines.AddRange([string[]]$existing) }
    }
    else {
        Write-Info "No config yet - it'll be filled out fully once the game runs with the plugin installed; seeding just the [Twitch] section for now."
        $lines.Add('[Twitch]')
    }

    Set-IniValue $lines 'Twitch' 'ChannelName' $TwitchChannel
    Set-IniValue $lines 'Twitch' 'BotUsername' $BotUsername
    Set-IniValue $lines 'Twitch' 'OAuthToken' $OAuthToken

    Set-Content -Path $ConfigPath -Value $lines
    Write-Info "Wrote $ConfigPath"
}
elseif (-not (Test-Path $ConfigPath)) {
    Write-Info "No Twitch credentials passed - config will appear at:"
    Write-Info "  $ConfigPath"
    Write-Info "after you launch the game once with the plugin installed. Fill in ChannelName/BotUsername/OAuthToken and restart."
}

Write-Host "`nInstall complete." -ForegroundColor Green

if ($LaunchGame) {
    Write-Step "Launching game"
    Start-Process -FilePath $GameExe
}
