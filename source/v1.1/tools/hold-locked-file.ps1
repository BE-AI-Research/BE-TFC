# Live-test helper: hold an exclusive handle on a file inside the User Temp
# catalog category so BE-TFC's locked-file path runs. Keep this window OPEN
# while BE-TFC.exe cleans; press Enter here after to release + clean up.
#
# Usage:
#   pwsh -File .\tools\hold-locked-file.ps1
#
# What to verify while it's held:
#   1. In an ELEVATED shell, run:
#        Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' `
#                         -Name PendingFileRenameOperations -ErrorAction SilentlyContinue
#      Note the current value (may be empty).
#   2. Launch BE-TFC.exe (elevated), Scan, uncheck everything except
#      "User Temp folders (all profiles)", Clean selected.
#   3. In the BE-TFC log pane, look for:
#        reboot-delete (NOT rollbackable): C:\Users\<you>\AppData\Local\Temp\betfc-locked-test-*.dat
#   4. Re-run the Get-ItemProperty command above and confirm our path is now
#      in PendingFileRenameOperations (values are UTF-16 \??\path pairs).
#   5. Open the txn.json under <drive>\BE-TFC.Quarantine\<txnId>\ and confirm
#      our locked file appears in "Unrecoverable" (NOT in "Entries").

$path = Join-Path $env:LOCALAPPDATA "Temp\betfc-locked-test-$([guid]::NewGuid().ToString('N').Substring(0,8)).dat"
'BE-TFC locked-file test payload' | Out-File -FilePath $path -Encoding utf8 -NoNewline

Write-Host ""
Write-Host "Created and holding exclusive handle on:" -ForegroundColor Cyan
Write-Host "  $path" -ForegroundColor Yellow
Write-Host ""

$fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open,
                             [System.IO.FileAccess]::ReadWrite,
                             [System.IO.FileShare]::None)

Write-Host "Handle open (FileShare.None). Any process — including BE-TFC — will hit sharing violation." -ForegroundColor Green
Write-Host ""
Write-Host "Now: run BE-TFC.exe elevated, Scan, Clean the 'User Temp folders' category." -ForegroundColor White
Write-Host "     Expect log line: 'reboot-delete (NOT rollbackable): $path'" -ForegroundColor Gray
Write-Host ""
Read-Host "Press Enter here to release the handle"

$fs.Close()
$fs.Dispose()

if (Test-Path $path) {
    Remove-Item $path -Force -ErrorAction SilentlyContinue
    Write-Host "Released and deleted." -ForegroundColor Green
} else {
    Write-Host "Released. File already gone (BE-TFC may have caught it after all)." -ForegroundColor Green
}
