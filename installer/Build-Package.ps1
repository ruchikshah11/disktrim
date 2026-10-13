<#
.SYNOPSIS
    Builds the package you hand to users: a folder and a zip with DiskTrim.exe and the installer.

.DESCRIPTION
    Publishes a self-contained single-file DiskTrim.exe (no .NET needed on the target PC) and puts it
    together with Install.cmd, Install-DiskTrim.ps1, Uninstall-DiskTrim.ps1 and README.md into
    artifacts\DiskTrim-<version>-win-x64\ plus a .zip and its SHA-256 checksum.

.EXAMPLE
    .\installer\Build-Package.ps1                 # version from DiskTrim.csproj
.EXAMPLE
    .\installer\Build-Package.ps1 -Version 1.1
#>
[CmdletBinding()]
param([string]$Version)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $repo 'DiskTrim.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }

$name = "DiskTrim-$Version-win-x64"
$out = Join-Path $repo 'artifacts'
$pkg = Join-Path $out $name
$publish = Join-Path $out 'publish'
if (Test-Path -LiteralPath $pkg) { Remove-Item -LiteralPath $pkg -Recurse -Force }
New-Item -ItemType Directory -Force -Path $pkg | Out-Null

Write-Host "Publishing DiskTrim $Version …" -ForegroundColor Cyan
dotnet publish (Join-Path $repo 'DiskTrim.csproj') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Copy-Item (Join-Path $publish 'DiskTrim.exe') $pkg
# The installer is the same exe under a setup name: started as DiskTrim-Setup-x.y.exe it shows the install wizard.
$setupExe = Join-Path $out "DiskTrim-Setup-$Version.exe"
Copy-Item (Join-Path $publish 'DiskTrim.exe') $setupExe -Force
Copy-Item (Join-Path $PSScriptRoot 'Install.cmd'), (Join-Path $PSScriptRoot 'Install-DiskTrim.ps1'), (Join-Path $PSScriptRoot 'Uninstall-DiskTrim.ps1') $pkg
Copy-Item (Join-Path $repo 'README.md') $pkg

# Smoke test: the packaged exe must start and report the right version.
$help = & (Join-Path $pkg 'DiskTrim.exe') help | Out-String
if ($LASTEXITCODE -ne 0 -or $help -notmatch "DiskTrim $([regex]::Escape($Version)) ") { throw "Smoke test failed:`n$help" }

$zip = "$pkg.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
# Antivirus often scans a brand-new exe for a few seconds; retry instead of failing.
Add-Type -AssemblyName System.IO.Compression.FileSystem
for ($try = 1; ; $try++) {
    try { [IO.Compression.ZipFile]::CreateFromDirectory($pkg, $zip, 'Optimal', $false); break }
    catch {
        if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
        if ($try -ge 10) { throw }
        Start-Sleep -Seconds 3
    }
}
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
"$hash  $name.zip" | Out-File -Encoding ascii "$zip.sha256"

Write-Host ''
Write-Host "Package ready:" -ForegroundColor Green
Write-Host "  folder: $pkg"
Write-Host "  zip:    $zip  ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
Write-Host "  sha256: $hash"
Write-Host "  setup:  $setupExe  (one file: double-click to install)"
Write-Host ''
Write-Host "Give users DiskTrim-Setup-$Version.exe (double-click to install), or the zip (unzip, then Install.cmd)."
