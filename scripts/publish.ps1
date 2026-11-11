param([ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64', [switch]$LocalBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetPath = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnetPath)) { throw 'Install the .NET 10 SDK before publishing.' }
if ($Runtime -ne 'win-x64') { throw 'The offline conversion payload is validated for Windows x64 only.' }
. (Join-Path $PSScriptRoot 'conversion-payload.ps1')
$conversionDirectory = Join-Path $projectRoot '.local/conversion-tools'
foreach ($engine in @('ffmpeg','libreoffice')) {
    $recipes = if ($engine -eq 'ffmpeg') { @('build-conversion-ffmpeg.sh','conversion-sources.json') } else { @('prepare-conversion-office.ps1','stage-conversion-office.py') }
    $recipePaths = @($recipes | ForEach-Object { Join-Path $PSScriptRoot $_ })
    & python (Join-Path $PSScriptRoot 'conversion-manifest.py') verify (Join-Path $conversionDirectory "tools/$engine") $engine @recipePaths
    if ($LASTEXITCODE -ne 0) { throw "Prepare the matching $engine bundle before publishing." }
}
$conversionFiles = @(Get-ConversionPayloadFiles $conversionDirectory)
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.local\dotnet'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.local\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_NOLOGO = '1'
[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$releaseVersion = [string]$buildProperties.Project.PropertyGroup.Version
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'Expected a valid release version in Directory.Build.props.' }
$packageName = 'DailyToolkit-' + $releaseVersion + '-' + $Runtime
$outputDirectory = Join-Path $projectRoot ('artifacts\' + $packageName)
$versionArguments = @()
if ($LocalBuild) { $versionArguments = @(('-p:InformationalVersion=' + $releaseVersion + '-local'), '-p:IncludeSourceRevisionInInformationalVersion=false') }
Push-Location -LiteralPath $projectRoot
try {
    & $dotnetPath publish src/DailyToolkit.Desktop/DailyToolkit.Desktop.csproj -c Release -r $Runtime --self-contained true --disable-build-servers -m:1 --nologo -o $outputDirectory -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false @versionArguments
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    foreach ($relative in $conversionFiles) {
        $destination = Join-Path $outputDirectory $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $conversionDirectory $relative) -Destination $destination
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $outputDirectory 'LICENSE-DailyToolkit.txt')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\PORTABLE_README.txt') -Destination (Join-Path $outputDirectory 'README.txt')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.txt') -Destination (Join-Path $outputDirectory 'THIRD_PARTY_NOTICES.txt')
    $licenseDirectory = Join-Path $outputDirectory 'licenses'
    New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses\Windows-SDK-License.rtf') -Destination (Join-Path $licenseDirectory 'Windows-SDK-License.rtf')
    foreach ($packName in @('microsoft.netcore.app.runtime.', 'microsoft.windowsdesktop.app.runtime.')) {
        $packRoot = Join-Path $env:NUGET_PACKAGES ($packName + $Runtime)
        $versionDirectory = Get-ChildItem -LiteralPath $packRoot -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
        foreach ($notice in @('LICENSE.TXT', 'LICENSE', 'THIRD-PARTY-NOTICES.TXT')) {
            $noticePath = Join-Path $versionDirectory.FullName $notice
            if (Test-Path -LiteralPath $noticePath) {
                Copy-Item -LiteralPath $noticePath -Destination (Join-Path $outputDirectory (($packName.TrimEnd('.')) + '-' + $notice))
            }
        }
    }
    $expected = @(Get-ApplicationPayloadFiles $outputDirectory)
    $actual = @(Get-ChildItem -LiteralPath $outputDirectory -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($outputDirectory, $_.FullName) })
    if (@(Compare-Object $expected $actual).Count) { throw 'Unexpected or missing payload files; refuse to archive user data or development output.' }
    $zipPath = Join-Path $projectRoot ('artifacts\' + $packageName + '-portable.zip')
    Compress-Archive -Path (Join-Path $outputDirectory '*') -DestinationPath $zipPath -Force
    & (Join-Path $PSScriptRoot 'refresh-explorer-icon.ps1') -Executable (Join-Path $outputDirectory 'DailyToolkit.exe') | Out-Null
    Write-Output $zipPath
} finally { Pop-Location }
