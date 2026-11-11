param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not $IsWindows) { throw 'LibreOffice staging requires Windows; it never installs LibreOffice.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root '.local/conversion-tools/tools/libreoffice' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Refuse to replace an existing staged office engine.' }
$version = '26.2.6'
$hash = 'f9877032fd908beb9c0ddf06df4af5c2e85f419c42e14876c4cce5aae5fb2660'
$url = "https://download.documentfoundation.org/libreoffice/stable/$version/win/x86_64/LibreOffice_${version}_Win_x86-64.msi"
$work = Join-Path $root ('.local/conversion-office/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$msi = Join-Path $work "LibreOffice-$version.msi"
[xml]$meta = (Invoke-WebRequest ($url + '.meta4') -TimeoutSec 60).Content
$officialHash = @($meta.metalink.file.hash | Where-Object type -eq 'sha-256')[0].InnerText.ToLowerInvariant()
if ($officialHash -ne $hash) { throw 'Official office metadata differs from the pinned source; review before updating.' }
$downloaded = $false
foreach ($mirror in @($meta.metalink.file.url | Where-Object { $_.InnerText.StartsWith('https://') } | Sort-Object { [int]$_.priority } | Select-Object -First 4)) {
    & curl.exe --fail --silent --show-error --location --connect-timeout 15 --max-time 90 --output $msi $mirror.InnerText
    if ($LASTEXITCODE -eq 0 -and (Get-FileHash -LiteralPath $msi).Hash.ToLowerInvariant() -eq $hash) { $downloaded = $true; break }
}
if (-not $downloaded) { throw 'No official mirror returned the checksum-verified archive in time.' }
$extract = Join-Path $work 'extracted'
$p = Start-Process msiexec.exe -ArgumentList @('/a', ('"' + $msi + '"'), '/qn', '/norestart', ('TARGETDIR="' + $extract + '"'), '/L*v', ('"' + (Join-Path $work 'extract.log') + '"')) -PassThru
if (-not $p.WaitForExit(240000)) { throw 'Administrative archive extraction timed out; no installed application was changed.' }
if ($p.ExitCode -notin @(0,3010)) { throw "Administrative archive extraction failed: $($p.ExitCode)" }
$entry = @(Get-ChildItem -LiteralPath $extract -Recurse -Filter soffice.com -File)
if ($entry.Count -ne 1) { throw 'Expected one upstream office console entry.' }
$source = $entry[0].Directory.Parent.FullName
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $vs) { throw 'A licensed Visual C++ app-local CRT is required when preparing the bundle.' }
$crt = Get-ChildItem -LiteralPath (Join-Path $vs 'VC/Redist/MSVC') -Recurse -Directory -Filter 'Microsoft.VC*.CRT' |
    Where-Object { $_.Parent.Name -eq 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $crt) { throw 'The Microsoft x64 app-local CRT was not found.' }
$metrics = & python (Join-Path $PSScriptRoot 'stage-conversion-office.py') $source $OutputDirectory $crt.FullName
if ($LASTEXITCODE -ne 0) { throw 'Office dependency staging failed.' }
$metrics | Set-Content -LiteralPath (Join-Path $work 'selection.json') -Encoding utf8
$summary = $metrics | ConvertFrom-Json
Write-Host ("Office: selected {0:N1} MiB / {1:N1} MiB; {2} files; {3} checked binaries" -f ($summary.selectedBytes/1MB),($summary.upstreamBytes/1MB),$summary.selectedFiles,$summary.checkedBinaries)
Write-Host ('App-local CRT: ' + ($summary.microsoftRuntime -join ', '))
@"
LibreOffice 26.2.6.3, The Document Foundation and contributors.
Unmodified upstream runtime files selected for Writer, Calc and Impress conversion.
MPL-2.0 and third-party licenses: license.txt, LICENSE.html and NOTICE in this folder.
Official binary archive: $url
SHA-256: $hash
Corresponding source and external dependency manifests:
https://download.documentfoundation.org/libreoffice/src/26.2.6/libreoffice-26.2.6.3.tar.xz
https://github.com/LibreOffice/core/tree/libreoffice-26.2.6.3
The source archive includes download.lst and external/ with versions, checksums and build recipes.
This selection does not change upstream binaries. Selection recipe: scripts/stage-conversion-office.py in DailyToolkit source.
Microsoft Visual C++ CRT files are included app-local; no runtime installer or system registration is used.
Copyright (C) Microsoft Corporation. All rights reserved.
Runtime terms: https://visualstudio.microsoft.com/license-terms/vs2022-cruntime/
"@ | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SOURCE.txt') -Encoding utf8
$profile = Join-Path $work 'probe-profile'
$input = Join-Path $work '离线检查.txt'
[IO.File]::WriteAllText($input, '格式转换离线检查', [Text.UTF8Encoding]::new($false))
& (Join-Path $OutputDirectory 'program/soffice.com') ('-env:UserInstallation=' + ([uri]($profile + '\')).AbsoluteUri) --headless --nologo --nodefault --nofirststartwizard --norestore --convert-to pdf --outdir $work $input
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $work '离线检查.pdf'))) { throw 'Selected app-local office runtime did not convert Unicode text to PDF.' }
Write-Host 'PASS Selected app-local LibreOffice: Unicode TXT to PDF without installation.'
