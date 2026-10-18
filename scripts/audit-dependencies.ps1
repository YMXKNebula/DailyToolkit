param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot '.local/dependency-audit' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.local/dotnet'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.local/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$checks = [Collections.Generic.List[object]]::new()
$overall = 'failed'
Push-Location -LiteralPath $projectRoot
try {
    # .NET 10 already audits direct and transitive dependencies at restore. This
    # independent report retains both inventory and advisories for the exact build.
    foreach ($kind in @('inventory', 'vulnerabilities')) {
        $arguments = @('package', 'list', '--project', 'DailyToolkit.slnx', '--no-restore', '--include-transitive', '--format', 'json', '--output-version', '1')
        if ($kind -eq 'vulnerabilities') { $arguments += '--vulnerable' }
        $report = & dotnet @arguments 2> (Join-Path $output "$kind.stderr.log")
        $exitCode = $LASTEXITCODE
        $report | Set-Content -LiteralPath (Join-Path $output "$kind.json") -Encoding utf8
        if ($exitCode -ne 0) { throw "Dependency $kind command failed (exit $exitCode)." }
        $parsed = ($report -join "`n") | ConvertFrom-Json
        if ($parsed.version -ne 1 -or $null -eq $parsed.projects -or @($parsed.projects).Count -eq 0 -or
            ($null -ne $parsed.problems -and @($parsed.problems).Count -gt 0) -or
            ($null -ne $parsed.errors -and @($parsed.errors).Count -gt 0)) {
            throw "Dependency $kind report is incomplete or contains errors."
        }
        if ((Get-Item -LiteralPath (Join-Path $output "$kind.stderr.log")).Length -gt 0) {
            throw "Dependency $kind wrote diagnostics; inspect the retained stderr log."
        }
        $findings = @($parsed.projects | ForEach-Object { $_.frameworks } | ForEach-Object { @($_.topLevelPackages) + @($_.transitivePackages) } |
            Where-Object { $null -ne $_ -and $null -ne $_.vulnerabilities -and @($_.vulnerabilities).Count -gt 0 })
        $checks.Add(@{ name = "nuget-$kind"; status = $(if ($findings.Count) { 'failed' } else { 'passed' }); vulnerablePackages = $findings.Count })
        if ($findings.Count) { throw 'Known NuGet advisories were found; inspect vulnerabilities.json.' }
    }
    $thirdParty = @(
        @{ path = 'src/DailyToolkit.Desktop/Tools/7zip/win-x64/7za.exe'; sha256 = '15d4c788c148e3677e2fc1c4a01f191fb6669eba4f6acf8a465f0f1a6f1e1260' },
        @{ path = 'src/DailyToolkit.Desktop/Tools/7zip/win-arm64/7za.exe'; sha256 = '88aea89faa900ea6c5f3723d5b62924328f079ae5ddcbca32248a08aeceea230' },
        @{ path = 'src/DailyToolkit.Desktop/Tools/7zip/7z2604-src.7z'; sha256 = '6bcfd2679d178174df06d09d50147dd7b5980d7beac896136689dfc9d51e0fd9' }
    )
    $inventory = foreach ($component in $thirdParty) {
        $actual = (Get-FileHash -LiteralPath (Join-Path $projectRoot $component.path) -Algorithm SHA256).Hash.ToLowerInvariant()
        [ordered]@{ path = $component.path; version = '26.04'; expectedSha256 = $component.sha256; actualSha256 = $actual;
            status = $(if ($actual -eq $component.sha256) { 'passed' } else { 'failed' }); check = 'integrity-only' }
    }
    [ordered]@{ schemaVersion = 1; components = @($inventory); advisoryReview = 'manual-required';
        source = 'https://github.com/ip7z/7zip/releases/tag/26.04' } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'third-party.json') -Encoding utf8
    if (@($inventory | Where-Object status -eq 'failed').Count) { throw 'Bundled 7-Zip integrity check failed.' }
    $checks.Add(@{ name = '7zip-integrity'; status = 'passed' })
    $checks.Add(@{ name = '7zip-advisories'; status = 'skipped'; reason = 'Manual upstream security review required; hashes do not prove absence of vulnerabilities.' })
    $overall = 'passed'
} finally {
    Pop-Location
    [ordered]@{ schemaVersion = 1; status = $overall; checks = @($checks) } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
}
