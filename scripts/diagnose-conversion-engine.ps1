# Temporary controlled fixture diagnostics, never run on a user's installation.
$ErrorActionPreference = 'Stop'
if ($env:CI -ne 'true' -or -not $IsWindows) { throw 'Requires disposable Windows CI.' }
$root = Join-Path $env:RUNNER_TEMP ('DailyToolkit-office-probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$source = Join-Path $root 'sample.txt'
[IO.File]::WriteAllText($source, 'Offline conversion fixture.', [Text.UTF8Encoding]::new($false))
$installerLog = Join-Path $PSScriptRoot '../.local/conversion-test-engines/libreoffice-install.log'
if (Test-Path -LiteralPath $installerLog) {
    Select-String -LiteralPath $installerLog -Pattern 'Feature: gm_p_Wrt;', 'Feature: gm_p_Calc;', 'Feature: gm_p_Impress;' | Select-Object -Last 6 | ForEach-Object { Write-Output $_.Line }
}
foreach ($kind in @('shell-empty', 'redirect-empty', 'redirect-configured')) {
    $output = Join-Path $root $kind
    $profile = Join-Path $output 'profile'
    New-Item -ItemType Directory -Path (Join-Path $profile 'user') -Force | Out-Null
    if ($kind -eq 'redirect-configured') {
        $code = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/DailyToolkit.Core/Conversion/ExternalConversionBackend.cs') -Raw
        $xml = $code.Split('private const string OfficeProfile = """')[1].Split('"""')[0].Trim()
        [IO.File]::WriteAllText((Join-Path $profile 'user/registrymodifications.xcu'), $xml, [Text.UTF8Encoding]::new($false))
    }
    $arguments = @(('-env:UserInstallation=' + ([uri]($profile + [IO.Path]::DirectorySeparatorChar)).AbsoluteUri),
        '--headless', '--nologo', '--nodefault', '--nofirststartwizard', '--norestore', '--convert-to', 'pdf', '--outdir', $output, $source)
    Write-Output ('OFFICE_PROBE ' + $kind + ' args=' + ($arguments | ConvertTo-Json -Compress))
    if ($kind -eq 'shell-empty') {
        & $env:DAILYTOOLKIT_TEST_LIBREOFFICE @arguments
        Write-Output ('OFFICE_PROBE exit=' + $LASTEXITCODE)
    } else {
        $start = [Diagnostics.ProcessStartInfo]::new($env:DAILYTOOLKIT_TEST_LIBREOFFICE)
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        $start.WorkingDirectory = Split-Path $env:DAILYTOOLKIT_TEST_LIBREOFFICE
        foreach ($arg in $arguments) { $start.ArgumentList.Add($arg) }
        $process = [Diagnostics.Process]::Start($start)
        try {
            $process.StandardInput.Close()
            $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(45000)) { $process.Kill($true); throw 'Controlled office probe timeout.' }
            Write-Output ('OFFICE_PROBE exit=' + $process.ExitCode + ' stdout=' + $stdout.GetAwaiter().GetResult() + ' stderr=' + $stderr.GetAwaiter().GetResult())
        } finally { $process.Dispose() }
    }
    Write-Output ('OFFICE_PROBE files=' + ((Get-ChildItem -LiteralPath $output -File).Name -join ','))
}
