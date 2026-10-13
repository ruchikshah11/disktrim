@echo off
rem Double-click to install DiskTrim for the current user (no administrator rights needed).
rem Options are passed through, e.g.:  Install.cmd -AllUsers   or   Install.cmd -Silent -NoLaunch
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-DiskTrim.ps1" %*
exit /b %errorlevel%
