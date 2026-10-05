param([ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetPath = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnetPath)) { throw 'Install the .NET 10 SDK before publishing.' }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.local\dotnet'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.local\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_NOLOGO = '1'
$outputDirectory = Join-Path $projectRoot ('artifacts\DailyToolkit-' + $Runtime)
Push-Location -LiteralPath $projectRoot
try {
    & $dotnetPath publish src/DailyToolkit.Desktop/DailyToolkit.Desktop.csproj -c Release -r $Runtime --self-contained true --disable-build-servers -m:1 --nologo -o $outputDirectory -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
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
    $zipPath = Join-Path $projectRoot ('artifacts\DailyToolkit-' + $Runtime + '.zip')
    Compress-Archive -Path (Join-Path $outputDirectory '*') -DestinationPath $zipPath -Force
    Write-Output $zipPath
} finally { Pop-Location }
