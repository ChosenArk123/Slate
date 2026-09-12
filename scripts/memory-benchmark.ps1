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

$output = Join-Path $repo ('artifacts\memory-benchmark-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
$dir = Split-Path -Parent $output
if (!(Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

Write-Output "Launching Slate Memory Benchmark..."
$process = Start-Process -FilePath $exe -ArgumentList ('--memory-benchmark="' + $output + '"') -PassThru

$timeoutSeconds = 120
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$lastReport = 0

try {
    while (!$process.WaitForExit(1000)) {
        $elapsed = [int]$stopwatch.Elapsed.TotalSeconds
        if ($elapsed - $lastReport -ge 5) {
            Write-Output "Running memory pressure & tab discard benchmark... (${elapsed}s elapsed)"
            $lastReport = $elapsed
        }
        if ($elapsed -ge $timeoutSeconds) {
            try { $process.Kill($true) } catch { }
            throw "Memory benchmark exceeded $timeoutSeconds seconds timeout."
        }
    }

    if (!(Test-Path -LiteralPath $output)) {
        throw "Benchmark finished but output JSON was not written to $output"
    }

    $result = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    Write-Output ""
    Write-Output "==============================================================================="
    Write-Output "                 SLATE REAL RAM RECLAMATION BENCHMARK RESULTS"
    Write-Output "==============================================================================="
    Write-Output "Tabs opened:                  $($result.tabsTotal)"
    Write-Output "Inactive tabs discarded:      $($result.tabsDiscarded)"
    Write-Output "Audio stream tab preserved:   $($result.audioTabPreserved)"
    Write-Output "Restoration succeeded:        $($result.restorationSucceeded)"
    Write-Output "-------------------------------------------------------------------------------"
    Write-Output "Baseline Total Working Set:   $($result.baseline.totalWorkingSetMB) MB ($($result.baseline.totalProcesses) processes)"
    Write-Output "Baseline Private Commit:      $($result.baseline.totalPrivateBytesMB) MB"
    Write-Output "Post-Discard Working Set:     $($result.postDiscard.totalWorkingSetMB) MB ($($result.postDiscard.totalProcesses) processes)"
    Write-Output "Post-Discard Private Commit:  $($result.postDiscard.totalPrivateBytesMB) MB"
    Write-Output "-------------------------------------------------------------------------------"
    Write-Output "Physical Working Set Saved:   $($result.savings.workingSetSavedMB) MB ($($result.savings.workingSetReductionPercent)%)"
    Write-Output "Private Commit Reclaimed:     $($result.savings.privateBytesSavedMB) MB ($($result.savings.privateBytesReductionPercent)%)"
    Write-Output "Processes Terminated:         $($result.savings.rendererProcessesTerminated)"
    Write-Output "Post-Restore Working Set:     $($result.postRestore.totalWorkingSetMB) MB ($($result.postRestore.totalProcesses) processes)"
    Write-Output "==============================================================================="
    Write-Output ""

    if (!$result.passed) {
        throw "Benchmark did not meet target savings criteria."
    }

    Write-Output "Benchmark verified: Slate successfully releases meaningful RAM under gaming pressure."
} catch {
    try { $process.Kill($true) } catch { }
    throw
}
