[CmdletBinding()]
param(
    [switch]$Replace,
    [switch]$RegisterStartup,
    [switch]$InspectOnly
)

$ErrorActionPreference = 'Stop'
$runtimeRoot = Split-Path -Parent $PSScriptRoot
$overlayDir = Join-Path $runtimeRoot 'native-overlay'
$overlayExe = Join-Path $overlayDir 'CodexUsageOverlay.exe'
$buildScript = Join-Path $overlayDir 'build.ps1'

if (-not (Test-Path -LiteralPath $overlayExe -PathType Leaf)) {
    if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) {
        throw "Native overlay build script not found: $buildScript"
    }
    & $buildScript -OutputPath $overlayExe
}

function Test-NativeCompanionMutex {
    try {
        $m = [Threading.Mutex]::OpenExisting('Local\CodexUsageNativeOverlay')
        $m.Dispose()
        return $true
    } catch [Threading.WaitHandleCannotBeOpenedException] {
        return $false
    } catch {
        return $true
    }
}

function Test-CurrentProcessPackaged {
    if (-not ('CodexPkgCheck' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class CodexPkgCheck {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentPackageFullName(ref uint length, StringBuilder fullName);
    public static bool IsPackaged() {
        uint len = 0;
        return GetCurrentPackageFullName(ref len, null) != 15700;
    }
}
'@ -ErrorAction SilentlyContinue
    }
    try { return [CodexPkgCheck]::IsPackaged() } catch { return $false }
}

$stateRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CodexUsageMonitor'
$companionStateDir = Join-Path $stateRoot 'companion'
$running = Test-NativeCompanionMutex

if ($InspectOnly) {
    [pscustomobject]@{
        Running = $running
        OverlayExe = $overlayExe
        StateDir = $companionStateDir
    }
    return
}

if ($RegisterStartup) {
    $startupLnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'Codex Usage Companion.lnk'
    $wsh = New-Object -ComObject WScript.Shell
    $lnk = $wsh.CreateShortcut($startupLnk)
    $lnk.TargetPath = $overlayExe
    $lnk.Arguments = '--daemon'
    $lnk.WorkingDirectory = $overlayDir
    $lnk.IconLocation = "$overlayExe,0"
    $lnk.Description = 'Codex Usage Companion Overlay'
    $lnk.Save()
}

if ($running -and -not $Replace) {
    return [pscustomobject]@{ Started = $false; Reused = $true; StateDir = $companionStateDir }
}

if ($running -and $Replace) {
    & (Join-Path $PSScriptRoot 'stop-native-companion.ps1')
}

if (Test-CurrentProcessPackaged) {
    $cmdLine = "`"$overlayExe`" --daemon"
    $wmi = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = $cmdLine
        CurrentDirectory = $overlayDir
    }
    if ($wmi.ReturnValue -ne 0) { throw "Failed to start unpackaged companion via WMI (code $($wmi.ReturnValue))." }
    $pidStarted = [int]$wmi.ProcessId
} else {
    New-Item -ItemType Directory -Path $companionStateDir -Force | Out-Null
    $stopFile = Join-Path $companionStateDir 'stop'
    if (Test-Path -LiteralPath $stopFile) { Remove-Item -LiteralPath $stopFile -Force -ErrorAction SilentlyContinue }
    $proc = Start-Process -FilePath $overlayExe -ArgumentList '--daemon' -WorkingDirectory $overlayDir -PassThru
    $pidStarted = [int]$proc.Id
}

$deadline = (Get-Date).AddSeconds(8)
$verified = $false
while ((Get-Date) -lt $deadline) {
    if (Test-NativeCompanionMutex) { $verified = $true; break }
    Start-Sleep -Milliseconds 120
}

[pscustomobject]@{ Started = $true; Reused = $false; OverlayPid = $pidStarted; Verified = $verified; StateDir = $companionStateDir }
