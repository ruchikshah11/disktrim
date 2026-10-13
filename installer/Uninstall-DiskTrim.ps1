<#
.SYNOPSIS
    Removes DiskTrim: program, shortcuts, PATH entry, Settings > Apps entry, scheduled cleanup and right-click menu.

.DESCRIPTION
    Your scan history, settings and deletion logs (%LocalAppData%\DiskTrimConsole) are kept unless you
    pass -RemoveData (or answer yes when asked). Settings > Apps > DiskTrim > Uninstall runs this script.

.EXAMPLE
    .\Uninstall-DiskTrim.ps1
.EXAMPLE
    .\Uninstall-DiskTrim.ps1 -Silent -RemoveData
#>
[CmdletBinding()]
param(
    # Uninstall the copy installed for all users (Program Files). Needs administrator rights.
    [switch]$AllUsers,
    # Also delete scan history, settings and deletion logs.
    [switch]$RemoveData,
    [switch]$Silent
)

$ErrorActionPreference = 'Continue'
$AppName = 'DiskTrim'

function Write-Step([string]$Text) { if (-not $Silent) { Write-Host "  $Text" } }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($AllUsers -and -not $isAdmin) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-AllUsers')
    if ($RemoveData) { $argList += '-RemoveData' }
    if ($Silent) { $argList += '-Silent' }
    try { $p = Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait -PassThru; exit $p.ExitCode }
    catch { Write-Host 'Administrator rights were not granted — nothing was removed.' -ForegroundColor Red; exit 1 }
}

if ($AllUsers) {
    $installDir = Join-Path $env:ProgramFiles $AppName
    $startMenu  = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
    $desktop    = [Environment]::GetFolderPath('CommonDesktopDirectory')
    $regKey     = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppName"
} else {
    $installDir = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
    $startMenu  = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    $desktop    = [Environment]::GetFolderPath('Desktop')
    $regKey     = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppName"
}
$exe = Join-Path $installDir 'DiskTrim.exe'

if (-not $Silent) {
    Write-Host ''
    Write-Host "Uninstalling $AppName from $installDir" -ForegroundColor Cyan
    $answer = Read-Host 'Continue? [Y/n]'
    if ($answer -match '^(n|no)$') { Write-Host 'Nothing was removed.'; exit 1 }
}

# Close it if running
Get-Process -Name $AppName -ErrorAction SilentlyContinue |
    Where-Object { try { $_.Path -ieq $exe } catch { $false } } |
    ForEach-Object { $_.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2; if (-not $_.HasExited) { $_ | Stop-Process -Force } }

# Things DiskTrim itself may have set up (Settings page): scheduled cleanup and right-click menu
& schtasks.exe /Query /TN 'DiskTrimConsole\Safe cleanup' *> $null
if ($LASTEXITCODE -eq 0) { & schtasks.exe /Delete /F /TN 'DiskTrimConsole\Safe cleanup' *> $null; Write-Step 'Removed the scheduled cleanup' }
foreach ($k in 'HKCU:\Software\Classes\Directory\shell\DiskTrimConsole', 'HKCU:\Software\Classes\Drive\shell\DiskTrimConsole') {
    if (Test-Path $k) { Remove-Item $k -Recurse -Force; Write-Step 'Removed the right-click menu entry' }
}

# Shortcuts
foreach ($lnk in (Join-Path $startMenu "$AppName.lnk"), (Join-Path $desktop "$AppName.lnk")) {
    if (Test-Path -LiteralPath $lnk) { Remove-Item -LiteralPath $lnk -Force; Write-Step "Removed shortcut $lnk" }
}

# PATH (keeps %VARIABLES% in the remaining value intact)
$hive = if ($AllUsers) { [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment', $true) }
        else { [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true) }
if ($hive) {
    $current = [string]$hive.GetValue('Path', '', 'DoNotExpandEnvironmentNames')
    $parts = $current -split ';'   # empty parts kept on purpose, so the rest is restored exactly
    if ($parts -contains $installDir) {
        $hive.SetValue('Path', (($parts | Where-Object { $_ -ne $installDir }) -join ';'), 'ExpandString')
        Write-Step 'Removed from PATH'
    }
    $hive.Close()
}

# Settings > Apps entry
if (Test-Path $regKey) { Remove-Item $regKey -Recurse -Force; Write-Step 'Removed from Settings > Apps' }

# Program files (this script lives there too — PowerShell has already read it, so it can go)
if (Test-Path -LiteralPath $installDir) {
    Remove-Item -LiteralPath $installDir -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $installDir) { Write-Step "Some files in $installDir are in use; delete the folder after a restart." }
    else { Write-Step "Removed $installDir" }
}

# User data
$dataDir = Join-Path $env:LOCALAPPDATA 'DiskTrimConsole'
if (Test-Path -LiteralPath $dataDir) {
    if (-not $RemoveData -and -not $Silent) {
        $answer = Read-Host "Also delete your scan history, settings and deletion logs ($dataDir)? [y/N]"
        $RemoveData = $answer -match '^(y|yes)$'
    }
    if ($RemoveData) { Remove-Item -LiteralPath $dataDir -Recurse -Force; Write-Step 'Removed scan history, settings and logs' }
    else { Write-Step "Kept your scan history and logs in $dataDir" }
}

if (-not $Silent) {
    Write-Host ''
    Write-Host "$AppName is uninstalled." -ForegroundColor Green
    [void](Read-Host 'Press Enter to close')
}
exit 0
