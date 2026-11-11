# Temporary controlled fixture diagnostics, never run on a user's installation.
$ErrorActionPreference = 'Stop'
if ($env:CI -ne 'true' -or -not $IsWindows) { throw 'Requires disposable Windows CI.' }
$root = Join-Path $env:RUNNER_TEMP ('DailyToolkit-office-probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$source = Join-Path $root 'sample.txt'
[IO.File]::WriteAllText($source, 'Offline conversion fixture.', [Text.UTF8Encoding]::new($false))
$jobCode = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/DailyToolkit.Core/FilePack/FilePackProcessJob.cs') -Raw
Add-Type -TypeDefinition ("using System;`nusing System.IO;`n#nullable enable`n" + $jobCode.Replace('internal sealed class', 'public sealed class'))
$word = Join-Path $root '文档 example.docx'
$zip = [IO.Compression.ZipFile]::Open($word, [IO.Compression.ZipArchiveMode]::Create)
try {
    $parts = @{
        '[Content_Types].xml' = '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>'
        '_rels/.rels' = '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>'
        'word/document.xml' = '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>格式转换离线检查</w:t></w:r></w:p></w:body></w:document>'
    }
    foreach ($part in $parts.GetEnumerator()) {
        $writer = [IO.StreamWriter]::new($zip.CreateEntry($part.Key).Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.Write($part.Value) } finally { $writer.Dispose() }
    }
} finally { $zip.Dispose() }
$installerLog = Join-Path $PSScriptRoot '../.local/conversion-test-engines/libreoffice-install.log'
if (Test-Path -LiteralPath $installerLog) {
    Select-String -LiteralPath $installerLog -Pattern 'Feature: gm_p_Wrt;', 'Feature: gm_p_Calc;', 'Feature: gm_p_Impress;' | Select-Object -Last 6 | ForEach-Object { Write-Output $_.Line }
}
foreach ($kind in @('shell-empty', 'redirect-empty', 'redirect-configured', 'redirect-job', 'redirect-word', 'redirect-word-job', 'redirect-long-profile')) {
    $output = Join-Path $root $kind
    $profile = Join-Path $output 'profile'
    if ($kind -eq 'redirect-long-profile') { $profile = Join-Path $profile ('long-' + ('p' * 90)) }
    New-Item -ItemType Directory -Path (Join-Path $profile 'user') -Force | Out-Null
    if ($kind -eq 'redirect-configured') {
        $code = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/DailyToolkit.Core/Conversion/ExternalConversionBackend.cs') -Raw
        $xml = $code.Split('private const string OfficeProfile = """')[1].Split('"""')[0].Trim()
        [IO.File]::WriteAllText((Join-Path $profile 'user/registrymodifications.xcu'), $xml, [Text.UTF8Encoding]::new($false))
    }
    $inputFile = if ($kind.Contains('word')) { $word } else { $source }
    $arguments = @(('-env:UserInstallation=' + ([uri]($profile + [IO.Path]::DirectorySeparatorChar)).AbsoluteUri),
        '--headless', '--nologo', '--nodefault', '--nofirststartwizard', '--norestore', '--convert-to', 'pdf', '--outdir', $output, $inputFile)
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
        $job = if ($kind.EndsWith('-job')) { [DailyToolkit.Core.FilePack.FilePackProcessJob]::Create() } else { $null }
        $process = [Diagnostics.Process]::Start($start)
        try {
            if ($job) { $job.Assign($process) }
            $process.StandardInput.Close()
            $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(45000)) { $process.Kill($true); throw 'Controlled office probe timeout.' }
            Write-Output ('OFFICE_PROBE exit=' + $process.ExitCode + ' stdout=' + $stdout.GetAwaiter().GetResult() + ' stderr=' + $stderr.GetAwaiter().GetResult())
        } finally { if ($job) { $job.Dispose() }; $process.Dispose() }
    }
    Write-Output ('OFFICE_PROBE files=' + ((Get-ChildItem -LiteralPath $output -File).Name -join ','))
}
