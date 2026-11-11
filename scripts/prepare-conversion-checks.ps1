# Disposable Windows CI only. These engines are test dependencies, not payload.
$ErrorActionPreference = 'Stop'
if ($env:CI -ne 'true' -or -not $IsWindows) { throw 'Conversion engine preparation requires disposable Windows CI.' }
$ffmpeg = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
if (-not $ffmpeg) {
    choco install ffmpeg --yes --no-progress --limit-output
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg test dependency installation failed.' }
    $ffmpeg = Get-Command ffmpeg.exe -ErrorAction Stop
}
$office = Join-Path $env:ProgramFiles 'LibreOffice/program/soffice.com'
if (-not (Test-Path -LiteralPath $office)) {
    choco install libreoffice-fresh --yes --no-progress --limit-output
    if ($LASTEXITCODE -notin @(0, 3010)) { throw 'LibreOffice test dependency installation failed.' }
}
if (-not (Test-Path -LiteralPath $office)) { throw 'LibreOffice console entry was not found.' }
"DAILYTOOLKIT_TEST_FFMPEG=$($ffmpeg.Source)" | Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8
"DAILYTOOLKIT_TEST_LIBREOFFICE=$office" | Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8
& $ffmpeg.Source -version | Select-Object -First 1
& $office --version
Get-FileHash -LiteralPath $ffmpeg.Source, $office -Algorithm SHA256 | Format-Table -AutoSize
