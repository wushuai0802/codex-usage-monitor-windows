[CmdletBinding()]
param(
    [switch]$UnregisterStartup
)

$ErrorActionPreference = 'Stop'
$companionStateDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CodexUsageMonitor\companion'
New-Item -ItemType Directory -Path $companionStateDir -Force | Out-Null
$stopFile = Join-Path $companionStateDir 'stop'
'stop' | Set-Content -LiteralPath $stopFile -Encoding ASCII

$deadline = (Get-Date).AddSeconds(5)
while ((Get-Date) -lt $deadline) {
    $alive = @(Get-Process -Name 'CodexUsageOverlay' -ErrorAction SilentlyContinue)
    if ($alive.Count -eq 0) { break }
    Start-Sleep -Milliseconds 120
}

$leftover = @(Get-Process -Name 'CodexUsageOverlay' -ErrorAction SilentlyContinue)
foreach ($p in $leftover) {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
if (Test-Path -LiteralPath $stopFile) {
    Remove-Item -LiteralPath $stopFile -Force -ErrorAction SilentlyContinue
}

if ($UnregisterStartup) {
    foreach ($name in @('Codex Usage Companion.lnk', 'Codex 用量伴随浮层.lnk')) {
        $lnk = Join-Path ([Environment]::GetFolderPath('Startup')) $name
        if (Test-Path -LiteralPath $lnk) {
            Remove-Item -LiteralPath $lnk -Force -ErrorAction SilentlyContinue
        }
    }
}
