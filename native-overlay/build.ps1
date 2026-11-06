param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'CodexUsageOverlay.exe'),
    [string]$IconPath = ''
)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$source = Join-Path $PSScriptRoot 'NativeOverlay.cs'
if (-not $IconPath) {
    $pkg = Get-AppxPackage -Name OpenAI.Codex -ErrorAction SilentlyContinue | Sort-Object Version -Descending | Select-Object -First 1
    if ($pkg) {
        $candidate = Join-Path $pkg.InstallLocation 'app\resources\chatgpt-app-light.ico'
        if (Test-Path -LiteralPath $candidate) { $IconPath = $candidate }
    }
}
$iconArg = if ($IconPath -and (Test-Path -LiteralPath $IconPath)) { "/win32icon:$IconPath" } else { $null }
$cscArgs = @(
    '/nologo', '/codepage:65001', '/target:winexe', '/optimize+',
    "/out:$OutputPath",
    '/r:System.Core.dll', '/r:System.Web.Extensions.dll',
    "/r:$framework\System.Xaml.dll",
    "/r:$framework\WPF\WindowsBase.dll",
    "/r:$framework\WPF\PresentationCore.dll",
    "/r:$framework\WPF\PresentationFramework.dll",
    "/r:$framework\WPF\UIAutomationClient.dll",
    "/r:$framework\WPF\UIAutomationTypes.dll"
)
if ($iconArg) { $cscArgs += $iconArg }
$cscArgs += $source
& (Join-Path $framework 'csc.exe') @cscArgs
if ($LASTEXITCODE -ne 0) { throw 'CodexUsageOverlay build failed.' }
