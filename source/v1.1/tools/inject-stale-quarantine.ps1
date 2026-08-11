# Injects a fake quarantine transaction with StartedUtc backdated 10 days,
# so the stale-quarantine prompt fires on next BE-TFC launch. Real files are
# created inside the fake txn dir so Commit actually reclaims something.
#
# When you click Yes on the prompt, BE-TFC purges this whole fake tree.

$daysOld  = 10
$fakeSize = 12345
$root     = "C:\BE-TFC.Quarantine"
$txnId    = ((Get-Date).ToUniversalTime().AddDays(-$daysOld)).ToString("yyyyMMdd-HHmmss") + "-fake01"
$txnDir   = Join-Path $root $txnId

if (-not (Test-Path $root)) { New-Item -ItemType Directory -Path $root | Out-Null }
if (Test-Path $txnDir) { Remove-Item $txnDir -Recurse -Force }
New-Item -ItemType Directory -Path $txnDir | Out-Null

# Real quarantine file at the expected sequenced path.
$quarantineFile = Join-Path $txnDir "000000"
$bytes = [byte[]]::new($fakeSize)
[System.IO.File]::WriteAllBytes($quarantineFile, $bytes)

# Journal — matches TxnJsonContext (PascalCase, source-gen JSON).
$startedUtc = (Get-Date).ToUniversalTime().AddDays(-$daysOld).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
$journal = @{
    TxnId         = $txnId
    StartedUtc    = $startedUtc
    Committed     = $false
    Entries       = @(
        @{
            OriginalPath   = "C:\Users\shayn\AppData\Local\Temp\betfc-fake-stale.dat"
            QuarantinePath = $quarantineFile
            SizeBytes      = $fakeSize
            Category       = "user-temp"
            Attributes     = 0
        }
    )
    Unrecoverable = @()
}
$json = $journal | ConvertTo-Json -Depth 5
Set-Content -LiteralPath (Join-Path $txnDir "txn.json") -Value $json -Encoding utf8

Write-Host ""
Write-Host "Injected stale quarantine:" -ForegroundColor Green
Write-Host "  TxnId:      $txnId" -ForegroundColor Yellow
Write-Host "  Dir:        $txnDir"
Write-Host "  StartedUtc: $startedUtc (~$daysOld days ago)"
Write-Host "  Size:       $fakeSize B in one dummy file"
Write-Host ""
Write-Host "Now launch BE-TFC.exe — you should see the stale-quarantine prompt." -ForegroundColor White
Write-Host "Clicking Yes will Commit (purge) this fake txn. No side effects on real data." -ForegroundColor White
