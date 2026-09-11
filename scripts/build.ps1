param([switch]$Release, [switch]$Publish)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$configuration = if ($Release -or $Publish) { 'Release' } else { 'Debug' }
Push-Location $repo
try {
    & $dotnet restore Slate/Slate.csproj --packages .tools/packages
    if ($LASTEXITCODE) { throw 'Dependency restore failed.' }
    & $dotnet build Slate/Slate.csproj --no-restore -c $configuration -p:Platform=x64
    if ($LASTEXITCODE) { throw 'Build failed.' }
    & $dotnet run --project Slate.Tests/Slate.Tests.csproj -c $configuration
    if ($LASTEXITCODE) { throw 'Core checks failed.' }
    if ($Publish) {
        & $dotnet publish Slate/Slate.csproj --no-restore -c Release -p:Platform=x64 -o artifacts/Slate
        if ($LASTEXITCODE) { throw 'Publish failed.' }
        Write-Output "Ready: $repo\artifacts\Slate\Slate.exe"
    }
} finally { Pop-Location }
