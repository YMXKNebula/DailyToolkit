# Disposable Windows CI. Use precisely the prepared portable engines; no installs.
$ErrorActionPreference = 'Stop'
if ($env:CI -ne 'true' -or -not $IsWindows) { throw 'Conversion engine checks require disposable Windows CI.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bundle = Join-Path $root '.local/conversion-tools'
. (Join-Path $PSScriptRoot 'conversion-payload.ps1')
$files = @(Get-ConversionPayloadFiles $bundle)
$ffmpeg = Join-Path $bundle 'tools/ffmpeg/ffmpeg.exe'
$office = Join-Path $bundle 'tools/libreoffice/program/soffice.com'
"DAILYTOOLKIT_TEST_FFMPEG=$ffmpeg" | Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8
"DAILYTOOLKIT_TEST_LIBREOFFICE=$office" | Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8
& $ffmpeg -version | Select-Object -First 1
if ($LASTEXITCODE -ne 0) { throw 'Bundled FFmpeg could not start.' }
$protocols = (& $ffmpeg -hide_banner -protocols 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0 -or $protocols -match '(?m)^\s+(https?|ftp|tcp|udp|tls|crypto)\s*$') { throw 'The converter must not contain network protocols.' }
Write-Host $protocols
Write-Host "Verified $($files.Count) prepared conversion component files; no program was installed."
