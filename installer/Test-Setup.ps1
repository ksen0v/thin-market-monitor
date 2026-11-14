param(
    [Parameter(Mandatory)][string]$Setup,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [string]$PreviousSetup,
    [string]$PreviousVersion
)

$ErrorActionPreference = 'Stop'
$Setup = (Resolve-Path -LiteralPath $Setup).Path
$WorkDirectory = [IO.Path]::GetFullPath($WorkDirectory)
$target = Join-Path $WorkDirectory ('test install ' + [guid]::NewGuid().ToString('N'))
$app = Join-Path $target 'ThinMarketMonitor.exe'
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ThinMarketMonitor_is1'
$programs = Join-Path ([Environment]::GetFolderPath('Programs')) 'Thin Market Monitor.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Thin Market Monitor.lnk'
$settings = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ThinMarketMonitor/settings.json'
$uninstall = $null
$stage = $null
$updated = $null

function Run-Checked([string]$File, [string]$Arguments, [int]$Timeout = 90000) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    if (!$process.WaitForExit($Timeout)) { throw "Timed out running $File" }
    if ($process.ExitCode -ne 0) { throw "$File exited with code $($process.ExitCode)" }
    $process.Id
}

function Confirm-Installed([string]$Expected) {
    $registration = Get-ItemProperty -LiteralPath $key
    if ($registration.DisplayName -ne 'Thin Market Monitor' -or $registration.DisplayVersion -ne $Expected -or
        $registration.InstallLocation.TrimEnd('\') -ne $target) {
        throw "Unexpected Windows registration: $($registration.DisplayName), version $($registration.DisplayVersion), path $($registration.InstallLocation)."
    }
    foreach ($shortcut in @($programs, $desktop)) {
        if (!(Test-Path -LiteralPath $shortcut)) { throw "Missing shortcut $shortcut" }
        $shell = New-Object -ComObject Shell.Application
        $folder = $shell.NameSpace((Split-Path -Parent $shortcut))
        $item = $folder.ParseName((Split-Path -Leaf $shortcut))
        $link = $item.GetLink
        if ($link.Path -ne $app) { throw "Unexpected shortcut target '$($link.Path)' for $shortcut" }
    }
    $info = (Get-Item -LiteralPath $app).VersionInfo
    if ("$($info.FileMajorPart).$($info.FileMinorPart).$($info.FileBuildPart)" -ne $Expected) {
        throw 'Installed EXE version differs from the version registered in Windows.'
    }
    $marker = Join-Path $WorkDirectory "installed-$Expected.txt"
    $smokePid = Run-Checked $app "--smoke-test `"$marker`"" 60000
    if ((Get-Content -LiteralPath $marker -Raw).Trim() -ne "Thin Market Monitor $Expected`nInstalled") {
        throw 'The installed app did not start in installed mode.'
    }
    Write-Host "Verified installed v$Expected, both shortcuts, and Windows registration."
    $smokePid
}

if (Test-Path -LiteralPath $key) { throw 'A Thin Market Monitor installation already exists. Use a separate Windows account to test.' }
if ((Test-Path -LiteralPath $programs) -or (Test-Path -LiteralPath $desktop)) { throw 'A Thin Market Monitor shortcut already exists.' }
$null = New-Item -ItemType Directory -Path $WorkDirectory -Force
$createdSettings = !(Test-Path -LiteralPath $settings)
if ($createdSettings) {
    $null = New-Item -ItemType Directory -Path (Split-Path $settings) -Force
    Set-Content -LiteralPath $settings -Value '{"Grid":3,"PriceModule":false,"VolumeModule":false}'
}
$settingsHash = (Get-FileHash -LiteralPath $settings).Hash
try {
    $initial = if ($PreviousSetup) { (Resolve-Path -LiteralPath $PreviousSetup).Path } else { $Setup }
    $initialVersion = if ($PreviousSetup) { $PreviousVersion } else { $Version }
    $log = Join-Path $WorkDirectory 'install.log'
    $null = Run-Checked $initial "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=desktopicon /DIR=`"$target`" /LOG=`"$log`""
    $uninstall = (Get-ItemProperty -LiteralPath $key).UninstallString.Trim('"')
    if ((Split-Path -Parent $uninstall) -ne $target) { throw 'Uninstaller is outside the test installation.' }
    $originalPid = Confirm-Installed $initialVersion

    if ($PreviousSetup) {
        $id = [guid]::NewGuid().ToString('N')
        $stage = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "ThinMarketMonitor/Updates/$id"
        $null = New-Item -ItemType Directory -Path $stage
        $helper = Join-Path $stage 'updater.exe'
        Copy-Item -LiteralPath $app -Destination $helper
        Copy-Item -LiteralPath $Setup -Destination (Join-Path $stage 'ThinMarketMonitor-Setup-win-x64.exe')
        $digest = (Get-FileHash -LiteralPath $Setup -Algorithm SHA256).Hash
        # The smoke-test process already exited, as the original does during a built-in update.
        try { $null = Run-Checked $helper "--apply-setup-update $id $originalPid $Version $digest `"$app`"" 120000 }
        catch {
            $errorFile = Join-Path $stage 'error.txt'
            if (Test-Path -LiteralPath $errorFile) { Get-Content -LiteralPath $errorFile | Write-Host }
            throw
        }
        $updated = Get-Process -Name 'ThinMarketMonitor' -ErrorAction SilentlyContinue | Where-Object Path -eq $app
        if (!$updated) { throw 'The setup update did not restart the installed copy.' }
        if ($uninstall -ne (Get-ItemProperty -LiteralPath $key).UninstallString.Trim('"')) {
            throw 'The update changed the registered uninstaller path.'
        }
        $null = Confirm-Installed $Version
    }
}
finally {
    if (!$updated) {
        $updated = Get-Process -Name 'ThinMarketMonitor' -ErrorAction SilentlyContinue | Where-Object Path -eq $app
    }
    if ($updated -and !$updated.HasExited) {
        $null = $updated.CloseMainWindow()
        if (!$updated.WaitForExit(5000)) { throw 'Could not close the restarted test copy.' }
    }
    if (!$uninstall -and (Test-Path -LiteralPath $key)) {
        $candidate = (Get-ItemProperty -LiteralPath $key).UninstallString.Trim('"')
        if ((Split-Path -Parent $candidate) -eq $target) { $uninstall = $candidate }
    }
    if ($uninstall -and (Split-Path -Parent $uninstall) -eq $target) {
        $null = Run-Checked $uninstall "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$WorkDirectory\uninstall.log`""
    }
    if ((Test-Path -LiteralPath $key) -or (Test-Path -LiteralPath $app) -or
        (Test-Path -LiteralPath $programs) -or (Test-Path -LiteralPath $desktop)) {
        throw 'Uninstall left the app, a shortcut, or its Windows registration.'
    }
    if ((Get-FileHash -LiteralPath $settings).Hash -ne $settingsHash) { throw 'User settings changed in the installation lifecycle.' }
    if ($createdSettings) { Remove-Item -LiteralPath $settings }
    Write-Host 'Verified uninstall and retained user settings.'
}
