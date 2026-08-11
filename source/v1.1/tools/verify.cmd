@echo off
REM Quick state check (no full catalog scan): dumps profiles, pending
REM quarantine, PendingFileRenameOperations, and the seeded junk tree.
REM Add "full" to also do the ~15s catalog scan.
setlocal
pushd "%~dp0BETFC.SmokeHarness"
if /i "%~1"=="full" (
    dotnet run --no-build --
) else (
    dotnet run --no-build -- --quick
)
popd
pause
