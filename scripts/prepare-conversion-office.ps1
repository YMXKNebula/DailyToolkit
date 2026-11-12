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
# Stable mirror entries can disappear when upstream publishes the next patch.
# Use the fixed official archive first; every candidate must match the same pin.
$archive = 'https://downloadarchive.documentfoundation.org/libreoffice/old/26.2.6.3/win/x86_64/'
$sources = @(($archive + 'LibreOffice_26.2.6.3_Win_x86-64.msi'), $url)
$downloadCode = @'
import concurrent.futures, pathlib, shutil, sys, time, urllib.request
url, destination = sys.argv[1:]
target = pathlib.Path(destination)
size = 373252096
chunk = (size + 7) // 8
class HTTPS(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if not newurl.startswith('https://'): raise OSError('Refused a non-HTTPS redirect')
        return super().redirect_request(req, fp, code, msg, headers, newurl)
def fetch(index):
    first, end = index * chunk, min(size, (index + 1) * chunk) - 1
    expected = end - first + 1
    part = target.with_name(target.name + '.part-' + str(index))
    deadline = time.monotonic() + 900
    for attempt in range(3):
        received = part.stat().st_size if part.exists() else 0
        if received > expected: raise OSError('Oversized existing source range')
        if received == expected: return part
        start = first + received
        request = urllib.request.Request(url, headers={'Range':f'bytes={start}-{end}', 'Accept-Encoding':'identity', 'User-Agent':'DailyToolkit-pinned-source'})
        try:
            with urllib.request.build_opener(HTTPS()).open(request, timeout=45) as response:
                if response.status != 206 or response.headers.get('Content-Range', '').strip() != f'bytes {start}-{end}/{size}':
                    raise RuntimeError('The official server did not return the exact requested range')
                with part.open('ab') as output:
                    while block := response.read(65536):
                        if time.monotonic() > deadline: raise TimeoutError('Official source range exceeded its deadline')
                        received += len(block)
                        if received > expected: raise RuntimeError('Oversized official source range')
                        output.write(block)
            if part.stat().st_size != expected: raise OSError('Incomplete official source range')
            print('Received complete source range', index + 1, 'of 8', flush=True)
            return part
        except OSError as error:
            if (isinstance(error, urllib.error.HTTPError) and 400 <= error.code < 500) or attempt == 2 or time.monotonic() >= deadline:
                raise
            print('Resuming source range', index + 1, flush=True)
with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
    parts = list(pool.map(fetch, range(8)))
with target.open('wb') as output:
    for part in parts:
        with part.open('rb') as source: shutil.copyfileobj(source, output, 1048576)
if target.stat().st_size != size: raise OSError('Incomplete official source archive')
'@
$downloaded = $false
foreach ($candidate in $sources) {
    & python -c $downloadCode $candidate $msi
    if ($LASTEXITCODE -eq 0 -and (Get-FileHash -LiteralPath $msi).Hash.ToLowerInvariant() -eq $hash) {
        $downloaded = $true; $downloadUrl = $candidate; break
    }
}
if (-not $downloaded) { throw 'No official source returned the checksum-verified archive in time.' }
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
$fontRevision = '523d033d6cb47f4a80c58a35753646f5c3608a78'
$fontDirectory = Join-Path $OutputDirectory 'share/fonts/truetype'
New-Item -ItemType Directory -Path $fontDirectory -Force | Out-Null
$font = Join-Path $fontDirectory 'NotoSansCJKsc-Regular.otf'
& curl.exe --fail --silent --show-error --location --connect-timeout 15 --max-time 90 --output $font "https://raw.githubusercontent.com/notofonts/noto-cjk/$fontRevision/Sans/OTF/SimplifiedChinese/NotoSansCJKsc-Regular.otf"
if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $font).Hash.ToLowerInvariant() -ne '2c76254f6fc379fddfce0a7e84fb5385bb135d3e399294f6eeb6680d0365b74b') { throw 'Bundled Chinese font checksum mismatch.' }
$fontLicense = Join-Path $OutputDirectory 'LICENSE-NotoSansCJK.txt'
& curl.exe --fail --silent --show-error --location --connect-timeout 15 --max-time 60 --output $fontLicense "https://raw.githubusercontent.com/notofonts/noto-cjk/$fontRevision/LICENSE"
if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $fontLicense).Hash.ToLowerInvariant() -ne '6a73f9541c2de74158c0e7cf6b0a58ef774f5a780bf191f2d7ec9cc53efe2bf2') { throw 'Bundled Chinese font license checksum mismatch.' }
$runtimeLicense = Join-Path $OutputDirectory 'LICENSE-Microsoft-VC-Runtime.docx'
$runtimeLicenseUrl = 'https://visualstudio.microsoft.com/wp-content/uploads/2025/10/Visual-C-V14-License-Redistributable_and_Runtime_ENU.docx'
& curl.exe --fail --silent --show-error --location --connect-timeout 15 --max-time 60 --output $runtimeLicense $runtimeLicenseUrl
if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $runtimeLicense).Hash.ToLowerInvariant() -ne '08651651a7602fc7c0e2763de0fde1ff9f868df2780597cd1775ee9d6441c783') { throw 'The pinned Microsoft runtime license could not be preserved.' }
$licenseArchive = [IO.Compression.ZipFile]::OpenRead($runtimeLicense)
try {
    $document = $licenseArchive.GetEntry('word/document.xml')
    if (-not $document) { throw 'The Microsoft runtime license is not a valid source document.' }
    $reader = [IO.StreamReader]::new($document.Open())
    try { [xml]$licenseXml = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $paragraphs = @($licenseXml.SelectNodes('//*[local-name()="p"]') | ForEach-Object { ($_.SelectNodes('.//*[local-name()="t"]') | ForEach-Object InnerText) -join '' })
    Write-Host ('Microsoft runtime license SHA-256: ' + (Get-FileHash -LiteralPath $runtimeLicense).Hash.ToLowerInvariant())
    $paragraphs | Write-Host
} finally { $licenseArchive.Dispose() }
foreach ($relative in $summary.microsoftRuntime) {
    $file = Join-Path $OutputDirectory $relative
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'The selected CRT is not an unchanged, signed Microsoft redistributable.' }
    Write-Host ("CRT {0}: {1}; SHA-256 {2}" -f $relative, (Get-Item -LiteralPath $file).VersionInfo.FileVersion, (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant())
}
@"
LibreOffice 26.2.6.3, The Document Foundation and contributors.
Unmodified upstream runtime files selected for Writer, Calc and Impress conversion.
MPL-2.0 and third-party licenses: license.txt, LICENSE.html and NOTICE in this folder.
Official binary archive: $downloadUrl
SHA-256: $hash
Corresponding source and external dependency manifests:
https://download.documentfoundation.org/libreoffice/src/26.2.6/libreoffice-26.2.6.3.tar.xz
https://github.com/LibreOffice/core/tree/libreoffice-26.2.6.3
The source archive includes download.lst and external/ with versions, checksums and build recipes.
This selection does not change upstream binaries. Selection recipe: scripts/stage-conversion-office.py in DailyToolkit source.
Microsoft Visual C++ CRT files are included app-local; no runtime installer or system registration is used.
Copyright (C) Microsoft Corporation. All rights reserved.
Runtime terms: LICENSE-Microsoft-VC-Runtime.docx
https://visualstudio.microsoft.com/license-terms/vs2026-ga-visualcpp-v14-redist-runtime/
Redistribution is governed by the Visual Studio 2026 Distributable Code terms for the licensed build environment:
https://visualstudio.microsoft.com/license-terms/vs2026-ga-pro-enterprise/
Noto Sans CJK SC 2.004 is included as a private document font, with no system font registration.
Copyright 2014-2021 Adobe and Noto contributors; SIL Open Font License 1.1 in LICENSE-NotoSansCJK.txt.
Font source: https://github.com/notofonts/noto-cjk/tree/$fontRevision
"@ | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SOURCE.txt') -Encoding utf8
$probeProfile = Join-Path ([IO.Path]::GetTempPath()) ('dtk-lo-' + [guid]::NewGuid().ToString('N'))
$probeInput = Join-Path $work '离线检查.txt'
[IO.File]::WriteAllText($probeInput, '格式转换离线检查', [Text.UTF8Encoding]::new($false))
try {
    New-Item -ItemType Directory -Path (Join-Path $probeProfile 'user') -Force | Out-Null
    @'
<?xml version="1.0" encoding="UTF-8"?>
<oor:items xmlns:oor="http://openoffice.org/2001/registry">
<item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop></item>
<item oor:path="/org.openoffice.Office.Jobs/Jobs/UpdateCheck/Arguments"><prop oor:name="AutoCheckEnabled" oor:op="fuse"><value>false</value></prop></item>
</oor:items>
'@ | Set-Content -LiteralPath (Join-Path $probeProfile 'user/registrymodifications.xcu') -Encoding utf8
    $probeLog = Join-Path $work 'probe.log'
    $probeError = Join-Path $work 'probe-error.log'
    $arguments = @(('-env:UserInstallation=' + ([uri]($probeProfile + '\')).AbsoluteUri), '--headless', '--nologo', '--nodefault', '--nofirststartwizard', '--norestore', '--convert-to', 'pdf', '--outdir', ('"' + $work + '"'), ('"' + $probeInput + '"'))
    $probe = Start-Process -FilePath (Join-Path $OutputDirectory 'program/soffice.com') -ArgumentList $arguments -RedirectStandardOutput $probeLog -RedirectStandardError $probeError -PassThru
    if (-not $probe.WaitForExit(60000)) {
        if ($env:CI -eq 'true') { & (Join-Path $PSScriptRoot 'inspect-office-probe.ps1') -ProgramDirectory (Join-Path $OutputDirectory 'program') }
        $probe.Kill($true); $probe.WaitForExit()
        Get-Content -LiteralPath $probeLog, $probeError -ErrorAction SilentlyContinue | Write-Host
        throw 'The selected app-local office probe exceeded one minute.'
    }
    Get-Content -LiteralPath $probeLog, $probeError -ErrorAction SilentlyContinue | Write-Host
    if ($probe.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $work '离线检查.pdf'))) {
        Write-Host "Probe source exists: $(Test-Path -LiteralPath $probeInput); bytes: $((Get-Item -LiteralPath $probeInput).Length)"
        $arguments | ForEach-Object { Write-Host "Probe argument: [$_]" }
        $controlOutput = Join-Path $work 'untrimmed-control'
        New-Item -ItemType Directory -Path $controlOutput -Force | Out-Null
        $controlProfile = Join-Path ([IO.Path]::GetTempPath()) ('dtk-lo-' + [guid]::NewGuid().ToString('N'))
        try {
            $controlArguments = @(('-env:UserInstallation=' + ([uri]($controlProfile + '\')).AbsoluteUri), '--headless', '--nologo', '--nodefault', '--nofirststartwizard', '--norestore', '--convert-to', 'pdf', '--outdir', ('"' + $controlOutput + '"'), ('"' + $probeInput + '"'))
            $control = Start-Process -FilePath (Join-Path $source 'program/soffice.com') -ArgumentList $controlArguments -RedirectStandardOutput (Join-Path $work 'control.log') -RedirectStandardError (Join-Path $work 'control-error.log') -PassThru
            if (-not $control.WaitForExit(60000)) { $control.Kill($true); $control.WaitForExit() }
            Get-Content -LiteralPath (Join-Path $work 'control.log'), (Join-Path $work 'control-error.log') -ErrorAction SilentlyContinue | Write-Host
            Write-Host "Untrimmed control produced PDF: $(Test-Path -LiteralPath (Join-Path $controlOutput '离线检查.pdf'))"
        } finally { if (Test-Path -LiteralPath $controlProfile) { Remove-Item -LiteralPath $controlProfile -Recurse -Force } }
        throw 'Selected app-local office runtime did not convert Unicode text to PDF.'
    }
} finally { if (Test-Path -LiteralPath $probeProfile) { Remove-Item -LiteralPath $probeProfile -Recurse -Force } }
Write-Host 'PASS Selected app-local LibreOffice: Unicode TXT to PDF without installation.'
& python (Join-Path $PSScriptRoot 'conversion-manifest.py') create $OutputDirectory libreoffice $PSCommandPath (Join-Path $PSScriptRoot 'stage-conversion-office.py')
if ($LASTEXITCODE -ne 0) { throw 'Office component manifest creation failed.' }
