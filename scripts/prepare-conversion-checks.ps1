# Disposable Windows CI only. These engines are test dependencies, not payload.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($env:CI -ne 'true' -or -not $IsWindows) { throw 'Conversion engine preparation requires disposable Windows CI.' }
$ffmpeg = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
if (-not $ffmpeg) {
    choco install ffmpeg --version=9.0.2 --yes --no-progress --limit-output
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg test dependency installation failed.' }
    $ffmpeg = Get-Command ffmpeg.exe -ErrorAction Stop
}
$office = Join-Path $env:ProgramFiles 'LibreOffice/program/soffice.com'
if (-not (Test-Path -LiteralPath $office)) {
    $version = '26.2.6'
    $url = "https://download.documentfoundation.org/libreoffice/stable/$version/win/x86_64/LibreOffice_${version}_Win_x86-64.msi"
    $evidence = Join-Path $PSScriptRoot '../.local/conversion-test-engines'
    New-Item -ItemType Directory -Path $evidence -Force | Out-Null
    [xml]$metalink = (Invoke-WebRequest -Uri ($url + '.meta4') -TimeoutSec 60).Content
    $entry = $metalink.metalink.file
    $hash = @($entry.hash | Where-Object type -eq 'sha-256')[0].InnerText.ToLowerInvariant()
    if ($hash -notmatch '^[a-f0-9]{64}$') { throw 'Official LibreOffice SHA-256 metadata was not found.' }
    $urls = @($entry.url | Where-Object { $_.InnerText.StartsWith('https://') } | Sort-Object { [int]$_.priority } | Select-Object -First 4)
    if ($urls.Count -eq 0) { throw 'Official HTTPS mirrors were not found.' }
    $msi = Join-Path $env:RUNNER_TEMP "LibreOffice-$version.msi"
    $downloaded = $false
    foreach ($mirror in $urls) {
        Write-Host ('Trying official LibreOffice mirror: ' + ([uri]$mirror.InnerText).Host)
        & curl.exe --silent --show-error --fail --location --connect-timeout 15 --max-time 90 --output $msi $mirror.InnerText
        if ($LASTEXITCODE -eq 0 -and (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant() -eq $hash) {
            $downloaded = $true; break
        }
    }
    if (-not $downloaded) { throw 'No official mirror supplied the checksum-verified LibreOffice installer within its time limit.' }
    @{ version = $version; sha256 = $hash; mirror = $mirror.InnerText } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'libreoffice-download.json') -Encoding utf8
    $log = Join-Path ([IO.Path]::GetFullPath($evidence)) 'libreoffice-install.log'
    $install = Start-Process msiexec.exe -ArgumentList @('/i', $msi, '/qn', '/norestart', '/L*v', $log) -PassThru
    if (-not $install.WaitForExit(240000)) { Stop-Process -Id $install.Id; throw 'The disposable CI LibreOffice install exceeded four minutes.' }
    if ($install.ExitCode -notin @(0, 3010)) { throw "LibreOffice test dependency installation failed: $($install.ExitCode)." }
}
if (-not (Test-Path -LiteralPath $office)) { throw 'LibreOffice console entry was not found.' }
"DAILYTOOLKIT_TEST_FFMPEG=$($ffmpeg.Source)" | Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8
"DAILYTOOLKIT_TEST_LIBREOFFICE=$office" | Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8
& $ffmpeg.Source -version | Select-Object -First 1
& $office --version
Get-FileHash -LiteralPath $ffmpeg.Source, $office -Algorithm SHA256 | Format-Table -AutoSize
