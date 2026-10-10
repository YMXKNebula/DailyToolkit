param([Parameter(Mandatory)][string]$Compiler, [string]$PayloadDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$newerVersion = [version]$properties.Project.PropertyGroup.Version
if ($newerVersion.Build -gt 0) { $olderVersion = [version]::new($newerVersion.Major, $newerVersion.Minor, $newerVersion.Build - 1) }
elseif ($newerVersion.Minor -gt 0) { $olderVersion = [version]::new($newerVersion.Major, $newerVersion.Minor - 1, 0) }
else { $olderVersion = [version]::new([Math]::Max(0, $newerVersion.Major - 1), 0, 0) }
if (-not $PayloadDirectory) { $PayloadDirectory = Join-Path $root "artifacts\DailyToolkit-$newerVersion-win-x64" }
$work = Join-Path $root '.local\installer-tests'
$hostDirectory = Join-Path $root 'tests\DailyToolkit.InstallerHost\bin\Release\net10.0-windows'
$destination = Join-Path $work 'installed'
$testName = 'DailyToolkit.InstallerTests'
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${testName}_is1"
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskName = "$testName-$sid"
if (Test-Path -LiteralPath $uninstallKey) { throw 'An earlier installer test is still registered; inspect it before retrying.' }
if ((Get-ItemProperty -LiteralPath $runKey -Name $testName -ErrorAction SilentlyContinue).$testName) { throw 'A test startup entry already exists.' }
$scheduler = New-Object -ComObject Schedule.Service
$scheduler.Connect()
$folder = $scheduler.GetFolder('\')
if (@($folder.GetTasks(1) | Where-Object Name -eq $taskName).Count -ne 0) { throw 'A test task already exists.' }
New-Item -ItemType Directory -Path $work -Force | Out-Null
$checks = [Collections.Generic.List[string]]::new()
$hostProcess = $null
$stopFile = $null
function Assert-Installer([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}
function Invoke-TestSetup([string]$file, [string]$label, [string[]]$extra = @()) {
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $work "$label.log") + '"')) + $extra
    $process = Start-Process -FilePath $file -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(45000)) { throw "$label did not finish; no process was force-closed." }
    $exitCode = $process.ExitCode
    if ($exitCode -eq 0 -and [IO.Path]::GetFileName($file) -match '^unins\d+\.exe$') {
        # Inno's separate self-deletion helper can finish after the process exits.
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        while ((Test-Path -LiteralPath $file) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        Assert-Installer (-not (Test-Path -LiteralPath $file)) 'Uninstaller self-cleanup did not finish before the next installation.'
    }
    $exitCode
}
function Stop-TestHost {
    if ($hostProcess -and -not $hostProcess.HasExited) {
        [IO.File]::WriteAllText($stopFile, 'stop')
        if (-not $hostProcess.WaitForExit(5000)) { throw 'Temporary test host did not exit normally.' }
    }
}
function Get-TestUninstaller {
    $command = (Get-ItemProperty -LiteralPath $uninstallKey).UninstallString
    $match = [regex]::Match($command, '^"([^"]+)"')
    if (-not $match.Success) { throw 'Unexpected test uninstall command.' }
    $path = [IO.Path]::GetFullPath($match.Groups[1].Value)
    if (-not $path.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($path) -notmatch '^unins\d+\.exe$') { throw 'Test uninstall command escaped the test directory.' }
    $path
}
function Start-TestHost([string]$mode, [string]$label) {
    $hostDirectory = Join-Path $root 'tests\DailyToolkit.InstallerHost\bin\Release\net10.0-windows'
    $ready = Join-Path $work "$label-ready.json"
    $script:stopFile = Join-Path $work "$label-stop.txt"
    foreach ($path in @($ready, $script:stopFile, $ready + '.exit-request')) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    # Rename only the temporary apphost, so uninstall exercises its exact path check.
    Get-ChildItem -LiteralPath $hostDirectory -File | Where-Object Extension -ne '.pdb' |
        Copy-Item -Destination $destination -Force
    Copy-Item -LiteralPath (Join-Path $hostDirectory 'DailyToolkit.InstallerHost.exe') -Destination (Join-Path $destination 'DailyToolkit.exe') -Force
    $script:hostProcess = Start-Process -FilePath (Join-Path $destination 'DailyToolkit.exe') -ArgumentList @(
        ('"' + $ready + '"'), ('"' + (Join-Path $work 'saved-note.txt') + '"'), ('"' + $script:stopFile + '"'), $mode
    ) -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
    Assert-Installer (Test-Path -LiteralPath $ready) 'Temporary native test window did not start.'
    $ready
}
function Register-TestTask([string]$description, [string]$path) {
    $definition = $scheduler.NewTask(0)
    $definition.RegistrationInfo.Description = $description
    $definition.Principal.UserId = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $definition.Principal.LogonType = 3
    $definition.Principal.RunLevel = 0
    $definition.Settings.AllowHardTerminate = $false
    $definition.Settings.ExecutionTimeLimit = 'PT0S'
    $action = $definition.Actions.Create(0)
    $action.Path = $path
    $action.Arguments = '--startup --admin-task'
    $action.WorkingDirectory = [IO.Path]::GetDirectoryName($path)
    $folder.RegisterTaskDefinition($taskName, $definition, 6, $sid, $null, 3) | Out-Null
}
function Test-TaskExists {
    @($folder.GetTasks(1) | Where-Object Name -eq $taskName).Count -ne 0
}
try {
    $env:DOTNET_CLI_HOME = Join-Path $root '.local\dotnet'
    $env:NUGET_PACKAGES = Join-Path $root '.local\nuget'
    & dotnet build (Join-Path $root 'tests\DailyToolkit.InstallerHost\DailyToolkit.InstallerHost.csproj') -c Release --disable-build-servers -m:1 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Installer test host build failed.' }
    foreach ($version in @($olderVersion, $newerVersion)) {
        & $Compiler /Qp "/DAppVersion=$version" "/DInstallerId=$testName" '/DProductName=DailyToolkit Installer Tests' "/DStartupName=$testName" "/DPayloadDir=$PayloadDirectory" "/DOutputDir=$work\$version" (Join-Path $root 'installer\DailyToolkit.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Installer fixture compilation failed.' }
    }
    $older = Join-Path $work "$olderVersion\DailyToolkit-$olderVersion-win-x64-setup.exe"
    $newer = Join-Path $work "$newerVersion\DailyToolkit-$newerVersion-win-x64-setup.exe"
    $installArguments = @('/TASKS=', ('/DIR="' + $destination + '"'))
    Assert-Installer ((Invoke-TestSetup $older 'fresh' $installArguments) -eq 0) 'Fresh installation failed.'
    Assert-Installer ((Get-ItemProperty -LiteralPath $uninstallKey).DisplayVersion -eq "$olderVersion") 'Installed version is absent.'
    $installedExe = Join-Path $destination 'DailyToolkit.exe'
    Assert-Installer ((Get-FileHash -LiteralPath $installedExe).Hash -eq (Get-FileHash -LiteralPath (Join-Path $PayloadDirectory 'DailyToolkit.exe')).Hash) 'Installed application differs from portable payload.'
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'DailyToolkit Installer Tests.lnk'
    $shell = New-Object -ComObject WScript.Shell
    Assert-Installer ((Test-Path -LiteralPath $shortcut) -and $shell.CreateShortcut($shortcut).TargetPath -eq $installedExe) 'Start Menu shortcut does not target the installed application.'
    Assert-Installer (Test-Path -LiteralPath (Join-Path $destination 'microsoft.windowsdesktop.app.runtime-LICENSE')) 'Desktop runtime license is missing.'
    $checks.Add('fresh install, Windows registration, shortcut, exact payload, runtime license')

    $ready = Start-TestHost 'refuse' 'refused-upgrade'
    $before = (Get-FileHash -LiteralPath $installedExe).Hash
    Assert-Installer ((Invoke-TestSetup $newer 'refused-upgrade' $installArguments) -ne 0) 'Upgrade ignored a refused normal exit.'
    Assert-Installer ((Test-Path -LiteralPath ($ready + '.exit-request')) -and -not $hostProcess.HasExited) 'Exit was not requested or the app was force-closed.'
    Assert-Installer ((Get-FileHash -LiteralPath $installedExe).Hash -eq $before -and (Get-ItemProperty -LiteralPath $uninstallKey).DisplayVersion -eq "$olderVersion") 'Aborted upgrade changed installed files or version.'
    $checks.Add('refused exit stops upgrade before changing files; no forced termination')
    Stop-TestHost

    $ready = Start-TestHost 'save' 'saved-upgrade'
    Assert-Installer ((Invoke-TestSetup $newer 'saved-upgrade' @('/TASKS=')) -eq 0) 'Upgrade after normal save failed.'
    Assert-Installer ($hostProcess.WaitForExit(3000)) 'Application did not exit before replacement.'
    Assert-Installer ([IO.File]::ReadAllText((Join-Path $work 'saved-note.txt')) -eq "最后一次修改`nlast edit") 'Upgrade lost the simulated last edit.'
    Assert-Installer ((Get-ItemProperty -LiteralPath $uninstallKey).DisplayVersion -eq "$newerVersion") 'Upgrade did not update Windows version.'
    Assert-Installer ([IO.Path]::GetFullPath((Get-ItemProperty -LiteralPath $uninstallKey).InstallLocation).TrimEnd('\') -eq $destination) 'Upgrade did not reuse the previous installation directory.'
    Assert-Installer ((Invoke-TestSetup $older 'downgrade' $installArguments) -ne 0) 'Downgrade was accepted.'
    Assert-Installer ((Get-FileHash -LiteralPath $installedExe).Hash -eq (Get-FileHash -LiteralPath (Join-Path $PayloadDirectory 'DailyToolkit.exe')).Hash) 'Upgrade or blocked downgrade damaged the payload.'
    $checks.Add('last edit saved before upgrade; installation location reused; downgrade blocked')

    New-ItemProperty -LiteralPath $runKey -Name $testName -Value ('"' + $installedExe + '" --startup') -PropertyType String -Force | Out-Null
    $ready = Start-TestHost 'refuse' 'refused-uninstall'
    Assert-Installer ((Invoke-TestSetup (Get-TestUninstaller) 'refused-uninstall') -ne 0) 'Uninstall ignored a refused exit.'
    Assert-Installer ((Test-Path -LiteralPath $installedExe) -and (Test-Path -LiteralPath $uninstallKey) -and
        (Get-ItemProperty -LiteralPath $runKey -Name $testName).$testName -and -not $hostProcess.HasExited) 'Blocked uninstall changed application, registration or startup.'
    $checks.Add('refused exit stops uninstall and retains startup')
    Stop-TestHost

    # Account-name principals exercise native SID translation, not display-name trust.
    Register-TestTask ('DailyToolkit automatic startup. Owner: ' + $sid) $installedExe
    [IO.File]::WriteAllText((Join-Path $destination 'keep-user-file.txt'), 'retain')
    Assert-Installer ((Invoke-TestSetup (Get-TestUninstaller) 'owned-uninstall') -eq 0) 'Owned-startup uninstall failed.'
    [pscustomobject]@{Run=(Get-ItemProperty -LiteralPath $runKey -Name $testName -ErrorAction SilentlyContinue).$testName;Task=Test-TaskExists} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'owned-cleanup-observed.json') -Encoding utf8
    Assert-Installer (-not (Test-Path -LiteralPath $installedExe) -and -not (Test-Path -LiteralPath $uninstallKey)) 'Uninstall left the program or Windows registration.'
    Assert-Installer (-not (Get-ItemProperty -LiteralPath $runKey -Name $testName -ErrorAction SilentlyContinue).$testName -and -not (Test-TaskExists)) 'Uninstall left its own startup entry.'
    Assert-Installer (-not (Test-Path -LiteralPath $shortcut) -and (Test-Path -LiteralPath (Join-Path $destination 'keep-user-file.txt'))) 'Uninstall removed user-added files or retained its shortcut.'
    $checks.Add('owned Run and task removed; shortcut removed; user-added files retained')

    Assert-Installer ((Invoke-TestSetup $newer 'reinstall' $installArguments) -eq 0) 'Reinstallation failed.'
    $portableExe = Join-Path $PayloadDirectory 'DailyToolkit.exe'
    New-ItemProperty -LiteralPath $runKey -Name $testName -Value ('"' + $portableExe + '" --startup') -PropertyType String -Force | Out-Null
    Register-TestTask ('DailyToolkit automatic startup. Owner: ' + $sid) $portableExe
    Assert-Installer ((Invoke-TestSetup (Get-TestUninstaller) 'portable-startup-preserved') -eq 0) 'Uninstall with portable startup failed.'
    Assert-Installer ((Get-ItemProperty -LiteralPath $runKey -Name $testName).$testName -eq ('"' + $portableExe + '" --startup') -and (Test-TaskExists)) 'Uninstall removed startup belonging to the portable directory.'
    $checks.Add('portable startup and scheduled task preserved')
    $folder.DeleteTask($taskName, 0)

    Assert-Installer ((Invoke-TestSetup $newer 'run-migration' @('/TASKS=migratestartup', ('/DIR="' + $destination + '"'))) -eq 0) 'Ordinary startup migration failed.'
    Assert-Installer ((Get-ItemProperty -LiteralPath $runKey -Name $testName).$testName -eq ('"' + $installedExe + '" --startup')) 'Startup was not moved to the installed path.'
    Register-TestTask 'Another application' $installedExe
    Assert-Installer ((Invoke-TestSetup (Get-TestUninstaller) 'foreign-task-preserved') -eq 0) 'Uninstall with a foreign task failed.'
    Assert-Installer ((Test-TaskExists) -and -not (Get-ItemProperty -LiteralPath $runKey -Name $testName -ErrorAction SilentlyContinue).$testName) 'Uninstall changed a foreign task or retained its owned Run entry.'
    $checks.Add('ordinary startup migration works; foreign scheduled task preserved')

    [pscustomobject]@{Passed=$true; Checks=$checks; Notes="Installer versions $olderVersion/$newerVersion are isolated metadata fixtures sharing the current application payload. Administrator checks are recorded separately; no UAC is requested by this script."} |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $work 'result.json') -Encoding utf8
    $checks | ForEach-Object { Write-Output "PASS $_" }
} catch {
    [pscustomobject]@{Error=$_.Exception.Message; Position=$_.InvocationInfo.PositionMessage; Stack=$_.ScriptStackTrace; PassedChecks=$checks} |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $work 'failure.json') -Encoding utf8
    throw
} finally {
    Stop-TestHost
    if (Test-Path -LiteralPath $uninstallKey) {
        $cleanup = Invoke-TestSetup (Get-TestUninstaller) 'cleanup'
        if ($cleanup -ne 0) { Write-Warning 'Test installation remains; inspect the cleanup log.' }
    }
    if (Test-TaskExists) { $folder.DeleteTask($taskName, 0) }
    Remove-ItemProperty -LiteralPath $runKey -Name $testName -ErrorAction SilentlyContinue
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($scheduler) | Out-Null
}

# CI runners that already hold administrator rights can check the elevated helper.
# Interactive runs never request UAC without a separate user decision.
$testIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $testPrincipal = [Security.Principal.WindowsPrincipal]::new($testIdentity)
    if ($testPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        & (Join-Path $hostDirectory 'DailyToolkit.InstallerHost.exe') --admin-check $newer (Join-Path $work 'admin-installed') $portableExe (Join-Path $work 'admin-result.json')
        if ($LASTEXITCODE -ne 0) { throw 'Administrator installer checks failed; inspect admin-result.json.error.txt.' }
        Write-Output 'PASS highest-level task migration and cleanup; task fields and SDDL retained'
    } else {
        Write-Output 'NOT RUN administrator checks: caller is not elevated.'
    }
} finally { $testIdentity.Dispose() }
