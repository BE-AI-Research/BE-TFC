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
# Both architectures at once, named and checksummed into dist\
.\tools\publish-all.ps1

# One architecture
.\tools\publish-all.ps1 -Arch win-arm64

# Experimental NativeAOT (~21 MB instead of ~48 MB)
.\tools\publish-all.ps1 -Aot
```

Produces `dist\BE-TFC-<version>-<arch>.exe` plus a `SHA256SUMS.txt` — a portable
exe passed around on a USB stick has no installer and no signature chain to
vouch for it, so the checksums travel with the build.

Or build directly:

```powershell
cd src
dotnet publish -c Release                          # win-x64 (default)
dotnet publish -c Release -p:TargetArch=win-arm64  # Windows 11 on ARM
dotnet publish -c Release -p:UseAot=true           # NativeAOT
```

Output: `src/bin/Release/net9.0-windows/<rid>/publish/BE-TFC.exe`
Copy that one file to the USB stick. Done.

| Build | Size | Notes |
|---|---|---|
| `win-x64` | ~48 MB | Standard. Verified. |
| `win-arm64` | ~47 MB | For Snapdragon X / ARM64 machines. x64 runs there under emulation, but scanning is filesystem-bound and emulation costs real time. |
| `-Aot` | ~21 MB | Experimental. Also extracts nothing to `%TEMP%` at launch — see *Self-protection* below. WinForms AOT is unsupported in .NET 9; verify the GUI before shipping one. |

NativeAOT needs the Visual Studio C++ build tools. `publish-all.ps1 -Aot` adds
the VS Installer directory to PATH for the run, because the ILC targets invoke
`vswhere.exe` unqualified and otherwise fail with a misleading
`'vswhere.exe' is not recognized` even when the toolchain is installed.

## GUI usage

1. Run `BE-TFC.exe` (UAC prompt — manifest requires admin).
2. **Scan** (or `F5`) — enumerates every real user profile from the registry
   ProfileList (local, domain, and Entra ID accounts), resolves all catalog
   locations, and sizes them in parallel.
3. Check/uncheck categories. After a scan the tree reorders biggest-first,
   groups carry their subtotal, and empty categories dim (or hide, via
   **Hide empty**). Selections persist across re-scans and re-sorts.
4. Pick a **Mode** (Dry / Safe / Direct).
5. **Clean selected** — confirm, watch the log. Locked files get scheduled
   for reboot-delete; you'll be offered a reboot when the clean finishes.
6. In Safe mode: click **Rollback** to restore, or **Commit** to purge and
   actually free the space.

Also:

- **Double-click any category** to see exactly which paths it resolved to on
  *this* machine, what each weighs, and how it would be deleted — with
  **Open in Explorer** so you can go look. A whitelist is only reassuring if
  you can see what it expanded to.
- **Cancel** (or `Esc`) aborts a running scan or clean. Work already done stays
  done; quarantined files are journaled and remain rollbackable.
- **Save log** (or `Ctrl+S`) writes the full transcript — including anything
  trimmed from the visible pane — next to the exe, falling back to the Desktop
  when the exe is on read-only media. Right-click the log for copy/clear.
- **Select all** never sweeps in Dangerous categories; they stay opt-in.
  Ticking a *group* checkbox still prompts individually for each dangerous
  member it would enable.
- Free space on the system volume is shown in the status bar, and each clean or
  commit logs a before/after line per volume — that's the number for the ticket.
- Light or dark chrome follows the machine's app-appearance setting
  (`AppsUseLightTheme`, read-only).

## What BE-TFC leaves on a client machine

No settings files, no configuration, no telemetry, no network calls, no
credentials, and no copies of client data. The engine works in *references* —
it resolves catalog entries to paths and reads names, sizes, and attributes. It
never reads file contents, and the quarantine "move" is a same-volume rename
(metadata only), so file data is never duplicated anywhere.

Everything that does persist, and for how long:

| Artifact | Where | Contains | Lifetime |
|---|---|---|---|
| Quarantined files | `<Volume>\BE-TFC.Quarantine\<txnId>\` | The moved files themselves | Until Commit or Rollback |
| Transaction journal | `…\<txnId>\txn.json` | Original paths, sizes, category IDs, attributes | Until Commit or Rollback |
| Pending reboot deletes | `PendingFileRenameOperations` | Paths of locked files | Until next reboot (Windows' own mechanism) |
| VSS snapshot | Volume shadow storage | A point-in-time image of the volume | Until Windows ages it out, or you delete it |
| Run log | Only where *you* save it (`Ctrl+S` / `--log`) | Paths, machine name, profile usernames | Yours |

The quarantine store's ACL is replaced with **Administrators + SYSTEM only,
inheritance disabled**, before any file is moved into it. Without that it would
inherit the drive root's ACL, which grants `BUILTIN\Users` read and execute:
`txn.json` is created inside the store and would inherit that permissive ACL,
exposing the full original-path list of every user on the box, and files whose
origin ACL was already permissive (`%SystemRoot%\Temp`, ProgramData WER dumps,
which can contain process memory) would stay reachable. Quarantined files
themselves keep their source ACL — a same-volume move preserves it rather than
re-inheriting — so restricting the root is what makes the store uniformly
private. A failure to apply it is logged loudly and never aborts a clean.

Commit deletes the whole transaction directory, journal included. Rollback
restores each file to its original path with its original attributes and
removes the store when it empties.

## Self-protection

A `PublishSingleFile` build with `IncludeNativeLibrariesForSelfExtract` unpacks
its native runtime to `%TEMP%\.net\` at launch — which lives inside
`AppData\Local\Temp`, exactly what the `user-temp` category cleans for every
profile including the elevated account running the tool. Windows permits
renaming a loaded image, so a Safe clean would happily quarantine BE-TFC's own
runtime mid-sweep, and any assembly not yet loaded would then fail to load; a
Direct clean would instead reboot-schedule them for no benefit.

`Engine/SelfProtection.cs` excludes the exe's own directory and the bundle
extraction directory from both sizing and cleaning. The extraction directory is
found by enumerating the process's own loaded modules — the path contains a
content hash that cannot be predicted — with `%TEMP%\.net` as a belt. Protected
roots are printed in the log at startup. This only ever *removes* paths from
consideration, so doctrine rule 1 still holds.

The NativeAOT build sidesteps the problem entirely: it extracts nothing.

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
| `--json` | Emit a machine-readable JSON run report on stdout instead of the human log. Requires `--silent`. |
| `--log <path>` | Also write the run log to a file. Directories are created as needed. |
| `--version` | Print version and architecture. |
| `--list-categories` | Print every catalog ID with its default and dangerous flag. |
| `-h`, `--help`, `/?` | Print usage. |

Every invocation triggers UAC — the manifest requires elevation and Windows
enforces that at process creation, before any flag is read. To identify a build
*without* launching it, read the PE version resource:

```powershell
(Get-Item BE-TFC.exe).VersionInfo.ProductVersion
```

Bench-tech example — clean caches on a client machine, purge anything already
sitting in stale quarantine, targeted set only:

```powershell
BE-TFC.exe --silent --commit-stale --categories wu-cache,user-temp,electron-cache,chromium-cache
```

RMM example — direct clean, JSON to the pipeline, human log to a file:

```powershell
$r = BE-TFC.exe --silent --direct --json --log C:\ProgramData\rmm\betfc.log | ConvertFrom-Json
"$($r.Machine): freed $([math]::Round($r.BytesCleaned/1GB,2)) GB, exit $($r.ExitCode)"
```

With `--json`, stdout carries *only* the JSON document so it can be piped
straight into a parser; the human-readable log goes to `--log` if given. The
report includes per-category sizes, per-volume free space before and after,
error and reboot counts, and the quarantine transaction ID (with
`awaitingCommit` set when bytes are still held pending a commit). Fields are
added over time, never renamed or removed.

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
  Engine/             UI-decoupled; catalog, scanner, cleaner, transaction, VSS,
                      self-protection, disk-space sampling, build identity
  Models/             Records + enums (CleanCategory, TxnEntry, CleanReport)
  UI/                 WinForms — MainForm, Theme, LogPane, CategoryDetailForm
  Cli/                Silent-mode parser, runner, JSON run report
tests/BETFC.Tests/    xUnit (49 tests: transaction lifecycle, CLI parsing,
                      self-protection bounds)
tools/                publish-all.ps1 (multi-arch + checksums) and verification
                      helpers (seed-junk, hold-locked-file,
                      inject-stale-quarantine, smoke harness)
dist/                 Publish output — named exes + SHA256SUMS
```

## Deliberately out of scope

- WinSxS / component store (that's `DISM /StartComponentCleanup`'s job)
- Registry "cleaning" (snake oil)
- UWP package data under `AppData\Local\Packages` (breaks Store apps;
  the MSTeams cache path is a targeted, documented exception)
- Anything requiring a settings file or install footprint

## License

GPL-3.0-or-later. See [LICENSE](LICENSE) for full text.
