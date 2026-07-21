# BE-TFC — Temp File Cleaner for Windows 11

A modern successor to OldTimer's TFC. Portable bench-tech utility: one exe,
run elevated, scan, clean, reboot if needed. No installer, no telemetry,
no settings files left behind.

Ships as a single self-contained `BE-TFC.exe`. Drops on a USB stick, runs
on any Windows 10/11 machine with no dependencies.

## Doctrine

1. **Whitelist only.** BE-TFC deletes exclusively from the locations declared
   in `Engine/CategoryCatalog.cs`. No heuristics, no "files older than X"
   scanning outside those roots. If it's not in the catalog, it doesn't get touched.
2. **Never follow reparse points.** Junctions and symlinks are never traversed
   (the link itself may be deleted inside a temp dir; its target never is).
   This blocks junction-swap attacks and OneDrive placeholder disasters.
3. **Locked files → reboot deletion.** `MoveFileEx(…, MOVEFILE_DELAY_UNTIL_REBOOT)`
   registers stragglers in `PendingFileRenameOperations`, same mechanism the
   kernel uses for its own pending deletes. TFC's "reboot to finish" behavior, done natively.
4. **Cookies, passwords, sessions, history are never touched.** Cache only.
   Client stays logged in to everything.
5. **Dangerous categories** (Windows.old, upgrade leftovers) are unchecked by
   default, red in the tree, require a second confirmation, and — if enabled —
   trigger a VSS snapshot as an extra safety net.

## Modes

Three cleaning modes, chosen via the Mode radio group in the GUI or CLI flags:

- **Dry run** — walks every catalog location and logs what *would* be touched
  (with sizes and counts) but changes nothing on disk. Verify before you commit
  on an unfamiliar client machine.
- **Safe (rollbackable)** *(default)* — transactional quarantine + journal.
  See below.
- **Direct (no undo)** — immediate deletion, no rollback. Use when the client's
  drive is critically full and quarantine would defeat the purpose.

## Transactional clean (Safe mode)

Instead of deleting, every file is *renamed* (same-volume, metadata-only —
near-instant, no copy) into a hidden per-volume quarantine store:

```
<Volume>\BE-TFC.Quarantine\<txnId>\<seq>      quarantined files (flat, numbered)
<Volume>\BE-TFC.Quarantine\<txnId>\txn.json   transaction journal
```

The journal maps every quarantine entry back to its original path, size,
category, and file attributes (ReadOnly/Hidden/System are captured before the
quarantine clears them, so Rollback restores them intact). From there:

- **Rollback** — replays the journal in reverse; every file returns to its
  original location with attributes restored. Originals that have since been
  recreated are skipped, never overwritten. Works across app restarts and
  reboots — pending transactions are auto-detected on every launch by scanning
  fixed volumes.
- **Commit** — purges the quarantine. **This is the moment disk space is
  actually freed.** Until commit, the machine *looks* clean (temp dirs empty,
  caches gone) but the bytes still exist in quarantine.

Files that were hard-locked and had to fall back to reboot-deletion are
journaled as `Unrecoverable` — the rollback prompt tells you how many.

Crash safety: the journal is flushed on normal completion *and* from the
transaction's disposer, so an unexpected exit mid-clean still leaves a
rollbackable journal on disk.

**Stale-quarantine prompt.** On launch, any pending transaction older than
7 days is surfaced with a one-click Commit option — keeps client disks from
silently accumulating forgotten quarantine data.

## VSS safety net (Dangerous categories)

When a Dangerous category (Windows.old, `$WINDOWS.~BT`, etc.) is included in a
clean, BE-TFC first creates a Volume Shadow Copy Service snapshot of every
affected volume. Snapshot IDs are logged and the snapshot persists after the
clean, so the user can restore individual files via:

- Right-click any folder → **Restore previous versions** tab, or
- `vssadmin list shadows` for the raw list.

VSS creation is best-effort — if the Shadow Copy service is disabled or out of
space, BE-TFC logs a warning and continues. In silent mode, VSS is opt-in via
`--vss` (scripted runs shouldn't consume snapshot slots unless asked).

## Build (on Windows, .NET 9 SDK)

```powershell
cd src

# Standard portable build (~50 MB compressed single exe)
dotnet publish -c Release

# Experimental NativeAOT build (~20 MB, WinForms AOT is experimental in .NET 9)
# Must be run from a Developer Command Prompt / Developer PowerShell so the
# native link step can find link.exe from the VS C++ build tools.
dotnet publish -c Release -p:UseAot=true
```

Output: `src/bin/Release/net9.0-windows/win-x64/publish/BE-TFC.exe`
Copy that one file to the USB stick. Done.

## GUI usage

1. Run `BE-TFC.exe` (UAC prompt — manifest requires admin).
2. **Scan** — enumerates every real user profile from the registry ProfileList
   (local, domain, and Entra ID accounts), resolves all catalog locations,
   and sizes them in parallel.
3. Check/uncheck categories. Sizes and file counts show per category.
   Selections persist across re-scans.
4. Pick a **Mode** (Dry / Safe / Direct).
5. **Clean selected** — confirm, watch the log. Locked files get scheduled
   for reboot-delete; you'll be offered a reboot when the clean finishes.
6. In Safe mode: click **Rollback** to restore, or **Commit** to purge and
   actually free the space.

## Silent CLI mode

```powershell
BE-TFC.exe --silent [flags]
```

Runs without a UI, logs to stdout, exits with:
- `0` success
- `1` errors during clean
- `2` reboot recommended (locked files scheduled for reboot-delete)
- `3` invalid usage / not elevated

Flags:

| Flag | What it does |
|---|---|
| `--dry` | Preview only; no changes. |
| `--direct` | Immediate delete (no quarantine). Default is Safe. |
| `--categories id1,id2,...` | Only run these catalog IDs. Default: every `DefaultChecked=true`. |
| `--include-dangerous` | Also run Dangerous categories. Off by default in silent mode. |
| `--vss` | Take a VSS snapshot before Dangerous categories run. |
| `--no-clean` | Scan and print sizes only. |
| `--commit-stale [days]` | Commit pending quarantines older than N days (default 7) first. |
| `--commit-all` | Commit every pending quarantine (any age) first. |
| `--rollback-all` | Roll back every pending quarantine first. |
| `-h`, `--help`, `/?` | Print usage. |

Bench-tech example — clean caches on a client machine, purge anything already
sitting in stale quarantine, targeted set only:

```powershell
BE-TFC.exe --silent --commit-stale --categories wu-cache,user-temp,electron-cache,chromium-cache
```

## Category coverage

- **Windows:** system Temp, WU download cache, Delivery Optimization,
  WER queue, Prefetch (off by default), Windows.old (off + dangerous),
  Recycle Bin (off by default — Direct/Dry only, never Safe)
- **User profiles (all of them):** Temp, INetCache, thumbnail/icon caches,
  crash dumps, Java deployment cache
- **Browsers:** Chrome / Edge / Brave / Vivaldi / Opera (all profiles),
  Firefox (all profiles) — cache dirs only, never cookies/passwords/history
- **App caches:** Discord, Slack, Teams, Spotify, GPU shader caches
  (NVIDIA/AMD/D3D), WinGet installer cache

## Extending

Add a `CleanCategory` to `CategoryCatalog.cs`. Scopes available:
`Machine`, `PerUser`, `PerUserChromiumProfiles`, `PerUserFirefoxProfiles`,
`RecycleBin`. Delete modes: `Contents`, `DirectoryItself`, `FilesMatching`
(glob). Dangerous categories must be `DefaultChecked = false, Dangerous = true`.

## Repo layout

```
src/                  Engine + UI + CLI (net9.0-windows, single-file publish)
  Engine/             UI-decoupled; catalog, scanner, cleaner, transaction, VSS
  Models/             Records + enums (CleanCategory, TxnEntry, CleanReport)
  UI/                 WinForms
  Cli/                Silent-mode CLI parser + runner
tests/BETFC.Tests/    xUnit (28 tests: transaction lifecycle + CLI parsing)
tools/                Verification helpers (seed-junk, hold-locked-file,
                      inject-stale-quarantine, smoke harness)
```

## Deliberately out of scope

- WinSxS / component store (that's `DISM /StartComponentCleanup`'s job)
- Registry "cleaning" (snake oil)
- UWP package data under `AppData\Local\Packages` (breaks Store apps;
  the MSTeams cache path is a targeted, documented exception)
- Anything requiring a settings file or install footprint

## License

GPL-3.0-or-later. See [LICENSE](LICENSE) for full text.
