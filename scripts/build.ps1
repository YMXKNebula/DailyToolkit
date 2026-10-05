param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetPath = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnetPath)) { throw 'Install the .NET 10 SDK before building.' }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.local\dotnet'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.local\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_NOLOGO = '1'
Push-Location -LiteralPath $projectRoot
try {
    & $dotnetPath build DailyUSE.slnx -c Release --disable-build-servers -m:1 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        & (Join-Path $projectRoot 'tests\DailyUSE.Tests\bin\Release\net10.0\DailyUSE.Tests.exe')
        if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
        & (Join-Path $projectRoot 'tests\DailyUSE.Desktop.Tests\bin\Release\net10.0-windows10.0.26100.0\DailyUSE.Desktop.Tests.exe')
        if ($LASTEXITCODE -ne 0) { throw 'Desktop checks failed.' }
    }
} finally { Pop-Location }
