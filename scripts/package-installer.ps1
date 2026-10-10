param([string]$PayloadDirectory, [string]$Compiler)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$properties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'The Windows installer requires a three-part release version.' }
if (-not $PayloadDirectory) { $PayloadDirectory = Join-Path $projectRoot "artifacts\DailyToolkit-$version-win-x64" }
$PayloadDirectory = [IO.Path]::GetFullPath($PayloadDirectory)
$executable = Join-Path $PayloadDirectory 'DailyToolkit.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Run scripts/publish.ps1 before packaging the installer.' }
if ((Get-Item -LiteralPath $executable).VersionInfo.ProductVersion -notmatch ('^' + [regex]::Escape($version) + '(?:\+|$)')) {
    throw 'The application and installer versions must match.'
}
$expected = @('DailyToolkit.exe','LICENSE-DailyToolkit.txt','README.txt','THIRD_PARTY_NOTICES.txt',
    'microsoft.netcore.app.runtime-LICENSE.TXT','microsoft.netcore.app.runtime-THIRD-PARTY-NOTICES.TXT',
    'microsoft.windowsdesktop.app.runtime-LICENSE','licenses\Windows-SDK-License.rtf')
$actual = @(Get-ChildItem -LiteralPath $PayloadDirectory -Recurse -File | ForEach-Object {
    [IO.Path]::GetRelativePath($PayloadDirectory, $_.FullName)
})
if (@(Compare-Object $expected $actual).Count -ne 0) { throw 'The portable payload has unexpected or missing files; refuse to package user data or development output.' }
$output = Join-Path $projectRoot 'artifacts'
$zip = Join-Path $output "DailyToolkit-$version-win-x64-portable.zip"
if (-not (Test-Path -LiteralPath $zip)) { throw 'The matching portable ZIP is required alongside the installer.' }
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $zipFiles = @($archive.Entries | Where-Object Name | ForEach-Object { $_.FullName.Replace('/', '\') })
    if (@(Compare-Object $expected $zipFiles).Count -ne 0) { throw 'The portable ZIP contains unexpected or missing files.' }
    $stream = $archive.GetEntry('DailyToolkit.exe').Open()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $zipExeHash = [Convert]::ToHexString($sha.ComputeHash($stream)) }
    finally { $sha.Dispose(); $stream.Dispose() }
    if ($zipExeHash -ne (Get-FileHash -LiteralPath $executable).Hash) { throw 'The ZIP and installer payload must contain the exact same application.' }
} finally { $archive.Dispose() }
if (-not $Compiler) {
    $Compiler = Join-Path $projectRoot '.local\inno-setup\7.1.0\ISCC.exe'
    if (-not (Test-Path -LiteralPath $Compiler)) {
        $available = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($available) { $Compiler = $available.Source }
        else {
            $installed = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
            if (Test-Path -LiteralPath $installed) { $Compiler = $installed }
            else { throw 'Install Inno Setup from jrsoftware.org and pass its ISCC.exe path with -Compiler.' }
        }
    }
}
& $Compiler "/DAppVersion=$version" "/DPayloadDir=$PayloadDirectory" "/DOutputDir=$output" (Join-Path $projectRoot 'installer\DailyToolkit.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$setup = Join-Path $output "DailyToolkit-$version-win-x64-setup.exe"
$sums = @($setup, $zip) | ForEach-Object {
    (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_)
}
[IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'), $sums, [Text.UTF8Encoding]::new($false))
Write-Output $setup
