param([switch]$Release)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$configuration = if ($Release) { 'Release' } else { 'Debug' }
$exe = Join-Path $repo "Slate\bin\x64\$configuration\net8.0-windows10.0.19041.0\win-x64\Slate.exe"
if (!(Test-Path -LiteralPath $exe)) { & "$PSScriptRoot\build.ps1" -Release:$Release }
Start-Process -FilePath $exe
