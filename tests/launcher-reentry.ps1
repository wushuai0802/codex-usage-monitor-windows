$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$mutex=[Threading.Mutex]::new($false,'Local\CodexUsageMonitorLauncher')
$acquired=$false
try {
  $acquired=$mutex.WaitOne(5000)
  if(-not $acquired){throw 'Launcher is busy; regression did not start a child.'}
  $shell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
  $arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $root 'scripts\launch-codex-monitor.ps1')+'"'
  $child=Start-Process -FilePath $shell -ArgumentList $arguments -WindowStyle Hidden -PassThru
  if(-not $child.WaitForExit(12000)){Stop-Process -Id $child.Id;throw 'A repeated launch did not return while the activation lock was held.'}
  $child.Refresh()
  if($child.ExitCode -ne 0){throw 'A repeated launch should exit normally without activating or starting Codex.'}
  Write-Host 'PASS: a concurrent launcher returns without activating Codex or starting a monitor.'
} finally {if($acquired){$mutex.ReleaseMutex()};$mutex.Dispose()}
