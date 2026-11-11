param([Parameter(Mandatory)][string]$PayloadDirectory)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $env:CI -ne 'true') { throw 'Network isolation checks run only in disposable Windows CI.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$PayloadDirectory = [IO.Path]::GetFullPath($PayloadDirectory)
. (Join-Path $PSScriptRoot 'conversion-payload.ps1')
$files = @(Get-ConversionPayloadFiles $PayloadDirectory)
if ($files.Count -lt 10) { throw 'Incomplete component inventory.' }
$test = Join-Path $root 'tests/DailyToolkit.Desktop.Tests/bin/Release/net10.0-windows10.0.26100.0/DailyToolkit.Desktop.Tests.exe'
if (-not (Test-Path -LiteralPath $test)) { throw 'Build the desktop fixture runner before this check.' }
$savedPath = $env:PATH
$rules = [Collections.Generic.List[string]]::new()
try {
    # Only these test-owned processes are denied outbound traffic, never the
    # user's machine or the runner's package/download/artifact services.
    if (@(Get-NetFirewallProfile | Where-Object Enabled -eq $false).Count) { throw 'The disposable CI firewall must be enabled for an offline check.' }
    $programs = @($test, (Join-Path $PayloadDirectory 'DailyToolkit.exe'),
        (Join-Path $PayloadDirectory 'tools/ffmpeg/ffmpeg.exe'),
        (Join-Path $PayloadDirectory 'tools/libreoffice/program/soffice.com'),
        (Join-Path $PayloadDirectory 'tools/libreoffice/program/soffice.exe'),
        (Join-Path $PayloadDirectory 'tools/libreoffice/program/soffice.bin'))
    foreach ($program in $programs) {
        $id = 'DailyToolkit.Conversion.Test.' + [guid]::NewGuid().ToString('N')
        New-NetFirewallRule -Name $id -DisplayName $id -Direction Outbound -Action Block -Program $program -Profile Any | Out-Null
        $rules.Add($id)
    }
    $env:PATH = "$env:SystemRoot\System32;$env:SystemRoot"
    & $test --clipboard-native-bundled-conversion-checks $PayloadDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Offline packaged conversion checks failed.' }
} finally {
    $env:PATH = $savedPath
    foreach ($id in $rules) { Remove-NetFirewallRule -Name $id }
}
