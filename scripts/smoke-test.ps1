param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$SkipBuild) { & "$PSScriptRoot\build.ps1" -Release }

function Stop-ProcessTree([System.Diagnostics.Process]$p) {
    if (!$p) { return }
    try {
        if ($p.HasExited) { return }
    } catch { return }

    $killed = $false
    try {
        # Process.Kill(bool entireProcessTree) is supported in .NET Core 3.0+ / PowerShell 7+
        $p.Kill($true)
        $killed = $true
    } catch { }

    if (!$killed) {
        try {
            # Fallback on Windows PowerShell 5.1: taskkill /T /F terminates the full process tree
            & taskkill.exe /PID $p.Id /T /F 2>$null | Out-Null
        } catch {
            try { $p.Kill() } catch { }
        }
    }
}

$exe = Join-Path $repo 'Slate\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Slate.exe'
$output = Join-Path $repo ('artifacts\smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
$process = Start-Process -FilePath $exe -ArgumentList ('--smoke-test="' + $output + '"') -WindowStyle Hidden -PassThru

$timeoutSeconds = 120
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$lastReport = 0

Write-Output "Started Slate (PID $($process.Id)). Running GUI smoke test..."

try {
    while (!$process.WaitForExit(1000)) {
        $elapsed = [int]$stopwatch.Elapsed.TotalSeconds
        if ($elapsed - $lastReport -ge 5) {
            Write-Output "Running GUI smoke test... (${elapsed}s elapsed)"
            $lastReport = $elapsed
        }
        if ($elapsed -ge $timeoutSeconds) {
            Stop-ProcessTree $process
            throw "Smoke test exceeded $timeoutSeconds seconds."
        }
    }

    if (!(Test-Path -LiteralPath $output)) {
        Stop-ProcessTree $process
        throw 'Smoke test exited without a result.'
    }

    $result = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    foreach ($check in $result.checks) { Write-Output "PASS  $($check.name)" }
    if (!$result.passed) { throw $result.error }
    if ($process.ExitCode -ne 0) { throw "Application exited with code $($process.ExitCode) after running the checks." }
    Write-Output "$($result.checks.Count) native integration checks passed in $([int]$stopwatch.Elapsed.TotalSeconds)s. Results: $output"
} catch {
    Stop-ProcessTree $process
    throw
}

