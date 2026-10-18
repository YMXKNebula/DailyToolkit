param([switch]$IsolatedCi, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($IsolatedCi -and $env:CI -ne 'true') { throw 'Full native checks require a disposable CI runner. Use the default safe local checks here.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot '.local/quality-checks' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$fixtureTemp = Join-Path $output ('fixture-temp-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureTemp | Out-Null
$previousTemp = [System.Environment]::GetEnvironmentVariable('TEMP', 'Process')
$previousTmp = [System.Environment]::GetEnvironmentVariable('TMP', 'Process')
$results = [Collections.Generic.List[object]]::new()
$desktop = Join-Path $projectRoot 'tests/DailyToolkit.Desktop.Tests/bin/Release/net10.0-windows10.0.26100.0/DailyToolkit.Desktop.Tests.exe'
$core = Join-Path $projectRoot 'tests/DailyToolkit.Tests/bin/Release/net10.0/DailyToolkit.Tests.exe'
$buildPassed = $false
$runCompleted = $false
$infrastructureFailure = $false
$overallFailed = $true
$plannedChecks = @('reporting-self-check', 'build', 'core', 'desktop', 'diagnostics', 'background-resources', 'clipboard-media',
    'clipboard-native', 'clipboard-visuals', 'palettes', 'filepack-recovery', 'interface-visuals',
    'mixed-monitor-dpi', 'gpu-device-loss', 'sleep-lock-game', 'physical-disk-full', 'real-upgrade-data', 'code-signing')
$requiredChecks = @('reporting-self-check', 'build', 'core', 'desktop', 'diagnostics', 'background-resources', 'clipboard-media')
if ($IsolatedCi) { $requiredChecks += @('clipboard-native', 'clipboard-visuals', 'palettes', 'filepack-recovery', 'interface-visuals') }
function Invoke-QualityCheck([string]$name, [scriptblock]$action, [string]$skipReason = '') {
    $entry = [ordered]@{ name = $name; status = 'skipped'; durationMs = 0; reason = $skipReason; log = "$name.log" }
    if ($skipReason) { $results.Add([pscustomobject]$entry); return }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $action *>&1 | Tee-Object -FilePath (Join-Path $output "$name.log") | ForEach-Object { Write-Host $_ }
        $entry.status = 'passed'
    } catch {
        # Classify failure before attempting diagnostics: the same disk/permission
        # problem may prevent both the check log and its error append from writing.
        $entry.status = 'failed'; $entry.reason = 'See the retained check log.'
        $failureRecord = $_
        try {
            $diagnostic = $failureRecord | Out-String
            if ($diagnostic.Length -gt 8192) { $diagnostic = $diagnostic.Substring(0, 8192) + "`n[diagnostic truncated]" }
            Add-Content -LiteralPath (Join-Path $output "$name.log") -Value $diagnostic
        } catch { $entry.reason = 'The check failed and its diagnostic log could not be written.' }
        Write-Warning "Quality check $name failed; remaining independent checks will still run."
    } finally {
        $entry.durationMs = $watch.ElapsedMilliseconds
        $results.Add([pscustomobject]$entry)
    }
}
function New-QualitySummary([object[]]$checkResults, [string[]]$requiredNames, [string[]]$plannedNames,
    [bool]$completed, [bool]$infrastructureFailed, [string]$scope) {
    $entries = [Collections.Generic.List[object]]::new()
    foreach ($check in $checkResults) { $entries.Add($check) }
    foreach ($name in $plannedNames) {
        if (-not @($entries | Where-Object name -eq $name).Count) {
            $entries.Add([pscustomobject]@{ name = $name; status = 'skipped'; durationMs = 0;
                reason = 'The script did not reach this check.'; log = "$name.log" })
        }
    }
    $missingRequired = @($requiredNames | Where-Object {
        @($entries | Where-Object name -eq $_ | Where-Object status -eq 'passed').Count -ne 1
    })
    $failed = @($entries | Where-Object status -eq 'failed').Count
    $passed = $completed -and -not $infrastructureFailed -and $failed -eq 0 -and $missingRequired.Count -eq 0
    return [ordered]@{
        schemaVersion = 1; status = $(if ($passed) { 'passed' } else { 'failed' }); scope = $scope;
        infrastructureFailure = $infrastructureFailed -or -not $completed;
        incompleteRequiredChecks = @($missingRequired);
        counts = @{ passed = @($entries | Where-Object status -eq 'passed').Count; failed = $failed;
            skipped = @($entries | Where-Object status -eq 'skipped').Count };
        checks = @($entries)
    }
}
function Invoke-DesktopCheck([string[]]$checkArguments) {
    & $desktop @checkArguments
    if ($LASTEXITCODE -ne 0) { throw "Desktop check exited with code $LASTEXITCODE." }
}
Push-Location -LiteralPath $projectRoot
try {
    # Only this PowerShell process and its children use the isolated fixture root.
    # Never change the user's persistent TEMP/TMP or recursively remove unknown paths.
    $env:TEMP = $fixtureTemp
    $env:TMP = $fixtureTemp
    Invoke-QualityCheck 'reporting-self-check' { & (Join-Path $PSScriptRoot 'test-quality-reporting.ps1') }
    Invoke-QualityCheck 'build' { & (Join-Path $PSScriptRoot 'build.ps1') -SkipTests }
    $buildPassed = $results[-1].status -eq 'passed'
    $dependency = if ($buildPassed) { '' } else { 'Build failed; executable checks were not run.' }
    Invoke-QualityCheck 'core' { & $core; if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' } } $dependency
    Invoke-QualityCheck 'desktop' {
        if ($IsolatedCi) { Invoke-DesktopCheck -CheckArguments @() } else { Invoke-DesktopCheck -CheckArguments @('--safe-regression-checks') }
    } $dependency
    Invoke-QualityCheck 'diagnostics' { Invoke-DesktopCheck -CheckArguments @('--diagnostic-checks') } $dependency
    Invoke-QualityCheck 'background-resources' { Invoke-DesktopCheck -CheckArguments @('--background-resource-checks', (Join-Path $output 'background-resources.json')) } $dependency
    Invoke-QualityCheck 'clipboard-media' { Invoke-DesktopCheck -CheckArguments @('--clipboard-media-checks') } $dependency
    $nativeReason = if ($dependency) { $dependency } elseif (-not $IsolatedCi) { 'Requires an isolated CI desktop; not run against the user desktop.' } else { '' }
    Invoke-QualityCheck 'clipboard-native' { Invoke-DesktopCheck -CheckArguments @('--clipboard-native-checks') } $nativeReason
    Invoke-QualityCheck 'clipboard-visuals' { Invoke-DesktopCheck -CheckArguments @('--clipboard-native-visuals', (Join-Path $output 'clipboard-panel-checks')) } $nativeReason
    Invoke-QualityCheck 'palettes' { Invoke-DesktopCheck -CheckArguments @('--clipboard-native-palette-checks', (Join-Path $output 'palette-checks')) } $nativeReason
    Invoke-QualityCheck 'filepack-recovery' { Invoke-DesktopCheck -CheckArguments @('--clipboard-native-filepack-checks', (Join-Path $output 'filepack-checks')) } $nativeReason
    Invoke-QualityCheck 'interface-visuals' { Invoke-DesktopCheck -CheckArguments @('--interface-checks', (Join-Path $output 'interface-checks')) } $nativeReason
    foreach ($manual in @('mixed-monitor-dpi', 'gpu-device-loss', 'sleep-lock-game', 'physical-disk-full', 'real-upgrade-data', 'code-signing')) {
        Invoke-QualityCheck $manual { } 'Manual acceptance or external signing identity required; see docs/QUALITY_084.md.'
    }
    $runCompleted = $true
} catch {
    $infrastructureFailure = $true
    throw
} finally {
    try { [System.Environment]::SetEnvironmentVariable('TEMP', $previousTemp, 'Process') }
    catch { $infrastructureFailure = $true }
    try { [System.Environment]::SetEnvironmentVariable('TMP', $previousTmp, 'Process') }
    catch { $infrastructureFailure = $true }
    try { Pop-Location } catch { $infrastructureFailure = $true }
    $scope = if ($IsolatedCi) { 'disposable-ci' } else { 'safe-local' }
    $summary = New-QualitySummary -CheckResults $results.ToArray() -RequiredNames $requiredChecks -PlannedNames $plannedChecks `
        -Completed $runCompleted -InfrastructureFailed $infrastructureFailure -Scope $scope
    $overallFailed = $summary.status -ne 'passed'
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
}
if ($overallFailed) { throw 'Quality checks failed. See summary.json and the retained logs.' }
