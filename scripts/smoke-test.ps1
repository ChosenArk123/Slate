param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if (!$SkipBuild) {
    Write-Output "Building Slate in Release mode..."
    & "$PSScriptRoot\build.ps1" -Release
    if ($LASTEXITCODE) { throw "Build failed." }
}

$exe = Join-Path $repo 'Slate\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Slate.exe'
if (!(Test-Path -LiteralPath $exe)) {
    $exe = Join-Path $repo 'Slate\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Slate.exe'
}
if (!(Test-Path -LiteralPath $exe)) {
    throw "Slate.exe not found at $exe"
}

$artifacts = Join-Path $repo 'artifacts'
if (!(Test-Path -LiteralPath $artifacts)) { New-Item -ItemType Directory -Path $artifacts | Out-Null }
$output = Join-Path $artifacts ('smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')

function Stop-ProcessTree([System.Diagnostics.Process]$p) {
    if (!$p) { return }
    try {
        if ($p.HasExited) { return }
    } catch { return }

    $killed = $false
    try {
        $p.Kill($true)
        $killed = $true
    } catch { }

    if (!$killed) {
        try {
            & taskkill.exe /PID $p.Id /T /F 2>$null | Out-Null
        } catch {
            try { $p.Kill() } catch { }
        }
    }
}

Write-Output "Launching Slate GUI smoke test..."
$process = Start-Process -FilePath $exe -ArgumentList ('--smoke-test="' + $output + '"') -PassThru

$timeoutSeconds = 120
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$lastReport = 0

try {
    while (!$process.WaitForExit(1000)) {
        $elapsed = [int]$stopwatch.Elapsed.TotalSeconds
        if ($elapsed - $lastReport -ge 5) {
            Write-Output "Running GUI smoke test... (${elapsed}s elapsed)"
            $lastReport = $elapsed
        }
        if ($elapsed -ge $timeoutSeconds) {
            Stop-ProcessTree $process
            throw "Smoke test exceeded $timeoutSeconds seconds timeout."
        }
    }

    if (!(Test-Path -LiteralPath $output)) {
        Stop-ProcessTree $process
        throw "Smoke test exited without writing result JSON to $output"
    }

    $result = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    foreach ($check in $result.checks) { Write-Output "PASS  $($check.name)" }
    if (!$result.passed) { throw $result.error }
    if ($process.ExitCode -ne 0) { throw "Application exited with code $($process.ExitCode) after running the checks." }
    Write-Output ""
    Write-Output "All $($result.checks.Count) native integration checks passed in $([int]$stopwatch.Elapsed.TotalSeconds)s. Results: $output"
} catch {
    Stop-ProcessTree $process
    throw
}

