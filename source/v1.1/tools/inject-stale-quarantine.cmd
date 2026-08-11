@echo off
powershell.exe -NoProfile -NoExit -ExecutionPolicy Bypass -File "%~dp0inject-stale-quarantine.ps1"
