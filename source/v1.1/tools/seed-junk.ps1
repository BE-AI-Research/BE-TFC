# Seed a controlled test tree under the User Temp catalog category so we can
# run BE-TFC clean cycles and know exactly what should have been picked up.
#
# Layout:
#   %LOCALAPPDATA%\Temp\betfc-junk\
#     top-1.txt ... top-5.txt         (varying sizes, ordinary files)
#     readonly.txt                    (read-only attribute set)
#     nested\
#       n1.txt, n2.txt, n3.txt
#       deeper\
#         d1.txt, d2.txt
#
# Total: 11 files across 3 directory levels. Fully cleanable via the
# "User Temp folders (all profiles)" catalog category.

$root = Join-Path $env:LOCALAPPDATA "Temp\betfc-junk"

if (Test-Path $root) {
    Write-Host "Removing existing $root ..." -ForegroundColor DarkGray
    # Strip read-only so old runs' files delete cleanly.
    Get-ChildItem $root -Recurse -File -Force | ForEach-Object {
        try { $_.IsReadOnly = $false } catch { }
    }
    Remove-Item $root -Recurse -Force
}

New-Item -ItemType Directory -Path $root                              | Out-Null
New-Item -ItemType Directory -Path (Join-Path $root "nested")         | Out-Null
New-Item -ItemType Directory -Path (Join-Path $root "nested\deeper")  | Out-Null

# Top-level files, sizes 1..5 KB
1..5 | ForEach-Object {
    $p = Join-Path $root "top-$_.txt"
    Set-Content -LiteralPath $p -Value ('x' * (1024 * $_)) -NoNewline
}

# Read-only file — verifies quarantine clears attrs before rename
$ro = Join-Path $root "readonly.txt"
Set-Content -LiteralPath $ro -Value 'do-not-touch' -NoNewline
Set-ItemProperty -LiteralPath $ro -Name IsReadOnly -Value $true

# One nesting level
1..3 | ForEach-Object {
    $p = Join-Path $root "nested\n$_.txt"
    Set-Content -LiteralPath $p -Value ('y' * 500) -NoNewline
}
# Two nesting levels
1..2 | ForEach-Object {
    $p = Join-Path $root "nested\deeper\d$_.txt"
    Set-Content -LiteralPath $p -Value ('z' * 250) -NoNewline
}

Write-Host ""
Write-Host "Seeded:" -ForegroundColor Green
Get-ChildItem $root -Recurse -File -Force |
    Sort-Object FullName |
    ForEach-Object {
        $ro = if ($_.IsReadOnly) { " (RO)" } else { "" }
        "{0,7:N0} B  {1}{2}" -f $_.Length, $_.FullName, $ro
    }

$files = Get-ChildItem $root -Recurse -File -Force
$sum   = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host ""
Write-Host ("Total: {0} bytes across {1} files under {2}" -f $sum, $files.Count, $root) -ForegroundColor Cyan
Write-Host ""
Write-Host "Next: run BE-TFC.exe elevated, Scan, verify User Temp size includes this," -ForegroundColor White
Write-Host "      then Clean (Safe or Direct) and re-run the harness to inspect state." -ForegroundColor White
