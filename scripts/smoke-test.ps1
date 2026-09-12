param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$SkipBuild) { & "$PSScriptRoot\build.ps1" -Release }

$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }

if (!$SkipBuild) {
    & "$PSScriptRoot\build.ps1" -Release
} else {
    & $dotnet run --project (Join-Path $repo 'Slate.Tests\Slate.Tests.csproj') -c Release
    if ($LASTEXITCODE) { throw 'Core checks failed.' }
}
Write-Output "All tests and hardening checks passed successfully."

