param([ValidateRange(1, 300)][int]$DurationMinutes = 60, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot '.local/background-stability' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$fixtureTemp = Join-Path $output ('fixture-temp-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureTemp | Out-Null
$previousTemp = [System.Environment]::GetEnvironmentVariable('TEMP', 'Process')
$previousTmp = [System.Environment]::GetEnvironmentVariable('TMP', 'Process')
$executable = Join-Path $projectRoot 'tests/DailyToolkit.Desktop.Tests/bin/Release/net10.0-windows10.0.26100.0/DailyToolkit.Desktop.Tests.exe'
$reportPath = Join-Path $output 'resource-soak.json'
$summaryPath = Join-Path $output 'summary.json'
$status = 'failed'
$reason = 'Check did not finish.'
try {
    # Set process-scoped variables before starting the disposable test child.
    # Retain remaining fixtures for inspection; do not recursively delete unknown paths.
    $env:TEMP = $fixtureTemp
    $env:TMP = $fixtureTemp
    if (-not (Test-Path -LiteralPath $executable)) { throw 'Build the test executable before running this check.' }
    # This entry uses temporary stores and fake clipboard/capture/native adapters;
    # it does not connect to the user clipboard, inject input or elevate/install.
    $process = Start-Process -FilePath $executable -ArgumentList @('--background-resource-soak', ('"' + $reportPath + '"'), $DurationMinutes) -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $output 'stdout.log') -RedirectStandardError (Join-Path $output 'stderr.log')
    try {
        # The test has its own bounded fixture timeouts. No unrelated application
        # is stopped; this watchdog only owns the disposable test child it started.
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (-not $process.WaitForExit(30000)) {
            if ($watch.Elapsed.TotalMinutes -gt ($DurationMinutes + 5)) {
                $process.Kill($true); $process.WaitForExit();
                throw 'The isolated stability test exceeded its timeout.'
            }
        }
        if ($process.ExitCode -ne 0) { throw "The isolated stability test failed (exit $($process.ExitCode))." }
    } finally { $process.Dispose() }
    if (-not (Test-Path -LiteralPath $reportPath)) { throw 'Stability executable did not create its report.' }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($report.status -ne 'passed') { throw 'Stability report did not confirm a completed pass.' }
    $status = 'passed'; $reason = ''
} catch {
    $reason = 'See stdout/stderr and the resource report when present.'
    throw
} finally {
    [System.Environment]::SetEnvironmentVariable('TEMP', $previousTemp, 'Process')
    [System.Environment]::SetEnvironmentVariable('TMP', $previousTmp, 'Process')
    [ordered]@{ schemaVersion = 1; status = $status; durationMinutes = $DurationMinutes; reason = $reason;
        scope = 'isolated-fixtures-no-user-input'; report = 'resource-soak.json';
        unverified = @('mixed-monitor-dpi', 'gpu-device-loss', 'sleep-lock-game', 'real-user-long-run') } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding utf8
}
