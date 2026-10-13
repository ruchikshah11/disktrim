<#
.SYNOPSIS
    Installs DiskTrim from the DiskTrim.exe next to this script (or the one given with -Source).

.DESCRIPTION
    - Copies DiskTrim.exe to %LocalAppData%\Programs\DiskTrim (or Program Files with -AllUsers)
    - Adds Start menu and desktop shortcuts
    - Registers DiskTrim in Settings > Apps (so it can be uninstalled the normal way)
    - Adds the install folder to PATH, so "DiskTrim scan" works in any terminal
    Running it again with a newer DiskTrim.exe upgrades in place.

.EXAMPLE
    .\Install-DiskTrim.ps1                       # current user, no admin needed
.EXAMPLE
    .\Install-DiskTrim.ps1 -AllUsers             # every user of this PC (asks for admin)
.EXAMPLE
    .\Install-DiskTrim.ps1 -Silent -NoDesktopShortcut -NoLaunch   # for scripted roll-outs
#>
[CmdletBinding()]
param(
    # DiskTrim.exe to install. Default: the one next to this script.
    [string]$Source,
    # Install for every user of the PC into Program Files (needs administrator rights).
    [switch]$AllUsers,
    [switch]$NoDesktopShortcut,
    [switch]$NoPath,
    [switch]$NoLaunch,
    # No questions, no pop-ups; problems end with a non-zero exit code.
    [switch]$Silent
)

$ErrorActionPreference = 'Stop'
$AppName = 'DiskTrim'
$Publisher = 'DiskTrim'
$RegKeyName = 'DiskTrim'

function Write-Step([string]$Text) { if (-not $Silent) { Write-Host "  $Text" } }
function Fail([string]$Text) {
    Write-Host "Installation failed: $Text" -ForegroundColor Red
    if (-not $Silent) { [void](Read-Host 'Press Enter to close') }
    exit 1
}

# ---- 1. Find the exe -------------------------------------------------------------------------
if (-not $Source) { $Source = Join-Path $PSScriptRoot 'DiskTrim.exe' }
if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { Fail "DiskTrim.exe not found at '$Source'. Put it next to this script or pass -Source." }
$Source = (Resolve-Path -LiteralPath $Source).Path
$info = (Get-Item -LiteralPath $Source).VersionInfo
if ($info.ProductName -notlike 'DiskTrim*' -and $info.FileDescription -notlike '*disk*') {
    Fail "'$Source' does not look like DiskTrim.exe (product: '$($info.ProductName)')."
}
$version = if ($info.ProductVersion) { ($info.ProductVersion -split '[+ ]')[0] } else { $info.FileVersion }

# ---- 2. Administrator rights for -AllUsers -----------------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($AllUsers -and -not $isAdmin) {
    Write-Step 'Asking for administrator rights…'
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Source', "`"$Source`"", '-AllUsers')
    foreach ($s in 'NoDesktopShortcut', 'NoPath', 'NoLaunch', 'Silent') { if ((Get-Variable $s).Value) { $argList += "-$s" } }
    try { $p = Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait -PassThru; exit $p.ExitCode }
    catch { Fail 'Administrator rights were not granted. Run without -AllUsers to install just for you.' }
}

# ---- 3. Where things go ------------------------------------------------------------------------
if ($AllUsers) {
    $installDir = Join-Path $env:ProgramFiles $AppName
    $startMenu  = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
    $desktop    = [Environment]::GetFolderPath('CommonDesktopDirectory')
    $regRoot    = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
    $pathScope  = 'Machine'
} else {
    $installDir = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
    $startMenu  = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    $desktop    = [Environment]::GetFolderPath('Desktop')
    $regRoot    = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
    $pathScope  = 'User'
}
$targetExe = Join-Path $installDir 'DiskTrim.exe'

if (-not $Silent) {
    Write-Host ''
    Write-Host "Installing $AppName $version" -ForegroundColor Cyan
    Write-Host "  for:  $(if ($AllUsers) { 'all users of this PC' } else { "$env:USERNAME only" })"
    Write-Host "  into: $installDir"
    Write-Host ''
}

# ---- 4. Close a running copy (it would lock the exe) -----------------------------------------
$running = Get-Process -Name $AppName -ErrorAction SilentlyContinue |
    Where-Object { try { $_.Path -and ($_.Path -ieq $targetExe) } catch { $false } }
if ($running) {
    if (-not $Silent) {
        $answer = Read-Host 'DiskTrim is running. Close it and continue? [Y/n]'
        if ($answer -match '^(n|no)$') { Fail 'DiskTrim is still running.' }
    }
    $running | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    Start-Sleep -Seconds 2
    $running | Where-Object { -not $_.HasExited } | Stop-Process -Force
}

# ---- 5. Copy files --------------------------------------------------------------------------
try {
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    if ($Source -ine $targetExe) { Copy-Item -LiteralPath $Source -Destination $targetExe -Force }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-DiskTrim.ps1') -Destination $installDir -Force
    $readme = Join-Path (Split-Path $Source) 'README.md'
    if (Test-Path -LiteralPath $readme) { Copy-Item -LiteralPath $readme -Destination $installDir -Force }
    Write-Step "Copied DiskTrim.exe"
} catch { Fail "Could not copy files: $($_.Exception.Message)" }

# ---- 6. Shortcuts ---------------------------------------------------------------------------
function New-Shortcut([string]$Path, [string]$Target, [string]$Description) {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($Path)
    $lnk.TargetPath = $Target
    $lnk.WorkingDirectory = Split-Path $Target
    $lnk.IconLocation = "$Target,0"
    $lnk.Description = $Description
    $lnk.Save()
}
$desc = 'Find what fills your disk and free space safely'
New-Shortcut (Join-Path $startMenu "$AppName.lnk") $targetExe $desc
Write-Step 'Added to the Start menu'
if (-not $NoDesktopShortcut) {
    New-Shortcut (Join-Path $desktop "$AppName.lnk") $targetExe $desc
    Write-Step 'Added a desktop shortcut'
}

# ---- 7. PATH (keeps %VARIABLES% in the existing value intact) ---------------------------------
if (-not $NoPath) {
    $hive = if ($pathScope -eq 'Machine') { [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment', $true) }
            else { [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true) }
    $current = [string]$hive.GetValue('Path', '', 'DoNotExpandEnvironmentNames')
    if (($current -split ';') -notcontains $installDir) {
        # Append only — every other character of the existing value stays exactly as it was.
        $new = if ($current.Length -eq 0) { $installDir } elseif ($current.EndsWith(';')) { "$current$installDir;" } else { "$current;$installDir" }
        $hive.SetValue('Path', $new, 'ExpandString')
        Write-Step 'Added to PATH (open a new terminal to use "DiskTrim")'
    }
    $hive.Close()
}

# ---- 8. Settings > Apps entry -----------------------------------------------------------------
$key = Join-Path $regRoot $RegKeyName
$uninstall = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$installDir\Uninstall-DiskTrim.ps1`""
New-Item -Path $key -Force | Out-Null
$values = @{
    DisplayName          = $AppName
    DisplayVersion       = $version
    Publisher            = $Publisher
    DisplayIcon          = "$targetExe,0"
    InstallLocation      = $installDir
    InstallDate          = (Get-Date -Format 'yyyyMMdd')
    UninstallString      = $uninstall + $(if ($AllUsers) { ' -AllUsers' } else { '' })
    QuietUninstallString = $uninstall + ' -Silent' + $(if ($AllUsers) { ' -AllUsers' } else { '' })
    Comments             = $desc
}
foreach ($k in $values.Keys) { Set-ItemProperty -Path $key -Name $k -Value $values[$k] }
$sizeKb = [int][Math]::Ceiling((Get-ChildItem -LiteralPath $installDir -File | Measure-Object Length -Sum).Sum / 1KB)
New-ItemProperty -Path $key -Name EstimatedSize -Value $sizeKb -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $key -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $key -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
Write-Step 'Registered in Settings > Apps'

# ---- 9. Tell Windows the environment changed (new terminals see PATH) --------------------------
if (-not ('DiskTrimInstall.Env' -as [type])) {
    Add-Type -Namespace DiskTrimInstall -Name Env -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public static extern System.IntPtr SendMessageTimeout(System.IntPtr hWnd, uint Msg, System.UIntPtr wParam, string lParam, uint flags, uint timeout, out System.UIntPtr result);
'@
}
$r = [UIntPtr]::Zero
[void][DiskTrimInstall.Env]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 3000, [ref]$r)

if (-not $Silent) {
    Write-Host ''
    Write-Host "$AppName $version is installed." -ForegroundColor Green
    Write-Host "  Start it from the Start menu$(if (-not $NoDesktopShortcut) { ' or the desktop' }), or type 'DiskTrim help' in a new terminal."
    Write-Host "  Uninstall from Settings > Apps > $AppName."
    Write-Host ''
}
if (-not $NoLaunch -and -not $Silent) { Start-Process -FilePath $targetExe }
exit 0
