#requires -Version 7.0
param([switch]$RestoreOnly, [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($RestoreOnly -and $NoRestore) { throw 'RestoreOnly and NoRestore cannot be combined.' }

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$reportRoot = Join-Path $projectRoot '.local/cloud-core-checks'
$null = [IO.Directory]::CreateDirectory($reportRoot)
$project = Join-Path $projectRoot 'tests/DailyToolkit.Tests/DailyToolkit.Tests.csproj'
$assembly = Join-Path $projectRoot 'tests/DailyToolkit.Tests/bin/Release/net10.0/DailyToolkit.Tests.dll'
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$environmentValues = @{
    DOTNET_CLI_HOME = (Join-Path $projectRoot '.local/dotnet')
    NUGET_PACKAGES = (Join-Path $projectRoot '.local/nuget')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_NOLOGO = '1'
}
$previousEnvironment = @{}
$summary = [ordered]@{
    schemaVersion = 1; scope = 'core-only'; status = 'failed'
    mode = $(if ($RestoreOnly) { 'restore-only' } else { 'build-and-test' })
    checkedAt = [DateTime]::UtcNow.ToString('O')
    hostOS = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    sdk = $null; buildSucceeded = $false; passed = $null; total = $null
    skippedChecks = @(); windowsDesktopValidated = $false; stage = 'sdk'
}

function Invoke-CoreDotnet([string[]]$Arguments, [string]$LogName) {
    & $dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $reportRoot $LogName) | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "Core check failed at $($summary.stage); exit code $LASTEXITCODE. See $LogName." }
}

Push-Location -LiteralPath $projectRoot
try {
    foreach ($name in $environmentValues.Keys) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environmentValues[$name], 'Process')
    }
    $sdk = @(& $dotnet --version)
    if ($LASTEXITCODE -ne 0 -or $sdk.Count -ne 1 -or $sdk[0] -notmatch '^10\.0\.\d+(?:[-+].*)?$') {
        throw 'Select a .NET 10 SDK for this environment before running core checks.'
    }
    $summary.sdk = $sdk[0]
    if (-not $NoRestore) {
        $summary.stage = 'restore'
        Invoke-CoreDotnet -Arguments @('restore', $project, '--disable-parallel', '--nologo') -LogName 'restore.log'
    }
    if ($RestoreOnly) {
        $summary.status = 'restored'
        $summary.stage = 'complete'
        Write-Host 'Core dependencies restored. Build, tests and Windows desktop checks have not run.'
    } else {
        $summary.stage = 'build'
        Invoke-CoreDotnet -Arguments @('build', $project, '-c', 'Release', '--no-restore', '--disable-build-servers', '-m:1', '-p:UseSharedCompilation=false', '--nologo') -LogName 'build.log'
        $summary.buildSucceeded = $true
        $summary.stage = 'tests'
        Invoke-CoreDotnet -Arguments @($assembly) -LogName 'core.log'
        $testLines = @(Get-Content -LiteralPath (Join-Path $reportRoot 'core.log'))
        $summary.skippedChecks = @($testLines | Where-Object { $_ -match '^SKIP ' })
        $result = @($testLines | Where-Object { $_ -match '^\d+/\d+ passed$' } | Select-Object -Last 1)
        if ($result.Count -ne 1 -or $result[0] -notmatch '^(\d+)/(\d+) passed$') { throw 'The core runner did not return its result summary.' }
        $summary.passed = [int]$Matches[1]
        $summary.total = [int]$Matches[2]
        if ($summary.total -lt 1 -or $summary.passed -ne $summary.total) { throw 'Core checks are incomplete.' }
        $summary.status = 'passed'
        $summary.stage = 'complete'
        Write-Host 'Core checks passed. Read skippedChecks in summary.json; Windows desktop behavior is outside this check.'
    }
} finally {
    foreach ($name in $previousEnvironment.Keys) {
        if ($null -eq $previousEnvironment[$name]) { Remove-Item -LiteralPath ('Env:' + $name) -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
    }
    Pop-Location
    $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $reportRoot 'summary.json') -Encoding utf8
}
