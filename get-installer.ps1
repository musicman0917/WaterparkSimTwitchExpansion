#Requires -Version 5.1
<#
.SYNOPSIS
    Downloads the latest WaterparkSimTwitchExpansion installer from GitHub Releases and runs it.

.DESCRIPTION
    A one-command alternative to visiting the Releases page by hand: this always fetches whatever
    the current latest release's Setup .exe is (via the GitHub API), so it never goes stale the way
    a script hardcoding a version number would. Meant for people who'd rather paste one command
    than navigate to a download page - e.g. a short instruction on a storefront listing.

    Doesn't need to be saved to disk first - can be run directly as:
        irm https://raw.githubusercontent.com/musicman0917/WaterparkSimTwitchExpansion/main/get-installer.ps1 | iex
    from a PowerShell prompt (Win+X > Terminal on Windows 11/10). That also sidesteps script
    execution policy entirely, since it's an inline command rather than a saved .ps1 file.

.PARAMETER DownloadDir
    Where to save the installer before running it. Defaults to a folder under %TEMP%.
#>
[CmdletBinding()]
param(
    [string]$DownloadDir = (Join-Path $env:TEMP 'WaterparkSimTwitchExpansion')
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$ReleasesApiUrl = 'https://api.github.com/repos/musicman0917/WaterparkSimTwitchExpansion/releases/latest'
$ReleasesPageUrl = 'https://github.com/musicman0917/WaterparkSimTwitchExpansion/releases/latest'

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

Write-Step "Checking the latest release"

try {
    $release = Invoke-RestMethod -Uri $ReleasesApiUrl -Headers @{ 'User-Agent' = 'WaterparkSimTwitchExpansion-get-installer' }
}
catch {
    Fail "Couldn't reach GitHub to check for the latest release: $($_.Exception.Message)`nYou can always download it by hand from:`n    $ReleasesPageUrl"
}

$asset = $release.assets | Where-Object { $_.name -like 'WaterparkSimTwitchExpansion-Setup-*.exe' } | Select-Object -First 1
if (-not $asset) {
    Fail "No installer .exe was found in the latest release ($($release.tag_name)). Download it by hand from:`n    $ReleasesPageUrl"
}

Write-Info "Found $($asset.name) (release $($release.tag_name))"

New-Item -ItemType Directory -Path $DownloadDir -Force | Out-Null
$InstallerPath = Join-Path $DownloadDir $asset.name

Write-Step "Downloading $($asset.name)"

try {
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $InstallerPath -UseBasicParsing
}
catch {
    Fail "Download failed: $($_.Exception.Message)"
}

Write-Step "Launching the installer"
Write-Info "Follow the setup wizard - it'll ask for your Twitch details and offer to launch the game when done."

Start-Process -FilePath $InstallerPath -Wait

Write-Host "`nDone. If the wizard finished successfully, Waterpark Simulator is ready to go." -ForegroundColor Green
