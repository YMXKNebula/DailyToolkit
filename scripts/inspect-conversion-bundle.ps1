$ErrorActionPreference = 'Stop'
if ($env:CI -ne 'true' -or -not $IsWindows) { throw 'Disposable Windows CI only.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$evidence = Join-Path $root '.local/conversion-bundle-inspection'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$version = '26.2.6'
$url = "https://download.documentfoundation.org/libreoffice/stable/$version/win/x86_64/LibreOffice_${version}_Win_x86-64.msi"
[xml]$meta = (Invoke-WebRequest ($url + '.meta4') -TimeoutSec 60).Content
$hash = @($meta.metalink.file.hash | Where-Object type -eq 'sha-256')[0].InnerText.ToLowerInvariant()
if ($hash -notmatch '^[a-f0-9]{64}$') { throw 'Missing official SHA-256.' }
Write-Host "Official LibreOffice MSI SHA-256: $hash"
$msi = Join-Path $env:RUNNER_TEMP 'DailyToolkit-LibreOffice.msi'
$downloaded = $false
foreach ($mirror in @($meta.metalink.file.url | Where-Object { $_.InnerText.StartsWith('https://') } | Sort-Object { [int]$_.priority } | Select-Object -First 4)) {
    & curl.exe --fail --silent --show-error --location --connect-timeout 15 --max-time 90 --output $msi $mirror.InnerText
    if ($LASTEXITCODE -eq 0 -and (Get-FileHash $msi).Hash.ToLowerInvariant() -eq $hash) { $downloaded = $true; break }
}
if (-not $downloaded) { throw 'Official download failed.' }
$extract = Join-Path $env:RUNNER_TEMP 'DailyToolkit-LibreOffice-admin'
$log = Join-Path $evidence 'extract.log'
$p = Start-Process msiexec.exe -ArgumentList @('/a', ('"' + $msi + '"'), '/qn', '/norestart', ('TARGETDIR="' + $extract + '"'), '/L*v', ('"' + $log + '"')) -PassThru
if (-not $p.WaitForExit(240000)) { throw 'Administrative extraction timed out.' }
if ($p.ExitCode -notin @(0,3010)) { throw "Administrative extraction failed: $($p.ExitCode)" }
$office = @(Get-ChildItem $extract -Recurse -File -Filter soffice.com)
if ($office.Count -ne 1) { throw 'Expected one extracted LibreOffice console entry.' }
$install = $office[0].Directory.Parent.FullName
$files = @(Get-ChildItem $install -Recurse -File | ForEach-Object {
    @{ path = [IO.Path]::GetRelativePath($install,$_.FullName).Replace('\','/'); bytes = $_.Length }
})
@{ version=$version; sha256=$hash; files=$files } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $evidence 'files.json')
$files | Group-Object { ($_.path -split '/')[0..([Math]::Min(1,($_.path -split '/').Length-1))] -join '/' } | ForEach-Object {
    [pscustomobject]@{ directory=$_.Name; files=$_.Count; bytes=($_.Group | Measure-Object bytes -Sum).Sum }
} | Sort-Object bytes -Descending | Select-Object -First 60 | Format-Table -AutoSize | Out-String -Width 180 | Write-Host
Write-Host 'PROGRAM FILES'
$files | Where-Object path -Like 'program/*' | ForEach-Object { Write-Host "$($_.path) $($_.bytes)" }
Write-Host 'ROOT FILES'
$files | Where-Object { $_.path -notlike '*/*' } | ForEach-Object { Write-Host "$($_.path) $($_.bytes)" }
& $office[0].FullName --version
if ($LASTEXITCODE -ne 0) { throw 'Extracted LibreOffice could not start.' }
