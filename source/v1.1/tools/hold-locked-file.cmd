@echo off
REM Double-clickable wrapper for hold-locked-file.ps1.
REM Uses -NoExit so any error stays visible instead of closing the window.
REM ExecutionPolicy Bypass is scoped to this process only — no machine change.
powershell.exe -NoProfile -NoExit -ExecutionPolicy Bypass -File "%~dp0hold-locked-file.ps1"
