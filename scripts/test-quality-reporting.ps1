$ErrorActionPreference = 'Stop'
# Load only the two reporting function definitions from the production script.
# No build, test executable, package command or environment mutation is evaluated.
$tokens = $null; $parseErrors = $null
$sourcePath = Join-Path $PSScriptRoot 'check-quality.ps1'
$syntax = [Management.Automation.Language.Parser]::ParseFile($sourcePath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'The quality script did not parse.' }
foreach ($name in @('Invoke-QualityCheck', 'New-QualitySummary')) {
    $definition = $syntax.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $false)
    if ($null -eq $definition) { throw "Missing reporting function $name." }
    Invoke-Expression $definition.Extent.Text
}
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $projectRoot ('.local/quality-report-self-check/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()
$appendAttempted = $false
function Add-Content { $script:appendAttempted = $true; throw 'Simulated diagnostic log write failure.' }
try {
    Invoke-QualityCheck 'simulated-check' { throw 'Simulated check failure; no test process was started.' }
} finally { Remove-Item Function:\Add-Content }
if (-not $appendAttempted -or $results.Count -ne 1 -or $results[0].status -ne 'failed' -or
    $results[0].reason -ne 'The check failed and its diagnostic log could not be written.') {
    throw 'A check/log failure was not retained as failed.'
}
$logFailure = New-QualitySummary -CheckResults $results.ToArray() -RequiredNames @('simulated-check') `
    -PlannedNames @('simulated-check', 'not-reached') -Completed $true -InfrastructureFailed $false -Scope 'reporting-self-check'
if ($logFailure.status -ne 'failed' -or $logFailure.counts.failed -ne 1 -or $logFailure.counts.skipped -ne 1) {
    throw 'Log failure summary lost failed/skipped status.'
}
$passedFixture = @([pscustomobject]@{ name = 'required'; status = 'passed' })
$outerFailure = New-QualitySummary -CheckResults $passedFixture -RequiredNames @('required') `
    -PlannedNames @('required') -Completed $false -InfrastructureFailed $true -Scope 'reporting-self-check'
if ($outerFailure.status -ne 'failed') { throw 'An outer script failure was reported as passed.' }
$missing = New-QualitySummary -CheckResults $passedFixture -RequiredNames @('required', 'missing') `
    -PlannedNames @('required', 'missing') -Completed $true -InfrastructureFailed $false -Scope 'reporting-self-check'
if ($missing.status -ne 'failed' -or $missing.incompleteRequiredChecks -notcontains 'missing') {
    throw 'A missing mandatory check was reported as passed.'
}
$optionalSkipped = New-QualitySummary -CheckResults $passedFixture -RequiredNames @('required') `
    -PlannedNames @('required', 'manual') -Completed $true -InfrastructureFailed $false -Scope 'reporting-self-check'
if ($optionalSkipped.status -ne 'passed' -or $optionalSkipped.counts.skipped -ne 1) {
    throw 'An explicitly optional skipped check changed the required-check result.'
}
Write-Output 'PASS Quality reporting: log-write failure, outer failure, missing mandatory checks and optional skipped checks; no child processes started.'
