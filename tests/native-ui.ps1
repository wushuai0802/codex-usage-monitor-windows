[CmdletBinding()]
param([string]$ArtifactsDirectory = '')
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('codex-native-ui-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $exe = Join-Path $testRoot 'NativeUiTests.exe'
    & (Join-Path $PSScriptRoot '..\native-overlay\build.ps1') -OutputPath $exe -Target exe -Main NativeUiTests -ExtraSource (Join-Path $PSScriptRoot 'NativeUiTests.cs')
    if ($ArtifactsDirectory) { New-Item -ItemType Directory -Path $ArtifactsDirectory -Force | Out-Null; & $exe $ArtifactsDirectory } else { & $exe }
    if ($LASTEXITCODE -ne 0) { throw 'Native UI regression checks failed.' }
} finally {
    if (([IO.Path]::GetFullPath($testRoot)).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
