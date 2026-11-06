[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$stopScript = Join-Path $PSScriptRoot 'scripts\stop-native-companion.ps1'
if (Test-Path -LiteralPath $stopScript -PathType Leaf) {
    & $stopScript -UnregisterStartup
}
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Codex Usage Monitor.lnk'
if (Test-Path -LiteralPath $desktopShortcut -PathType Leaf) {
    Remove-Item -LiteralPath $desktopShortcut -Force -ErrorAction SilentlyContinue
}
Write-Host 'Stopped Codex Usage Monitor and removed startup/desktop shortcuts. User settings and Token history under %LOCALAPPDATA%\CodexUsageMonitor were preserved.'
