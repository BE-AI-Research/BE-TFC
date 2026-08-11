using System.Runtime.InteropServices;
using BETFC.Models;

namespace BETFC.Engine;

public enum CleanMode
{
    /// <summary>Preview only: log every file that would be touched and roll up
    /// bytes/counts as if it were cleaned. No file operations, no journal.
    /// Use for verification on unfamiliar client machines before a real run.</summary>
    Dry,
    /// <summary>Transactional: files renamed into per-volume quarantine, journaled,
    /// rollback available. Space freed only on Commit.</summary>
    Quarantine,
    /// <summary>Immediate deletion. Space freed now, no rollback.</summary>
    Direct,
}

public sealed class Cleaner
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);
    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    private readonly Action<string> _log;
    private readonly CleanMode _mode;
    private readonly bool _vssForDangerous;
    private SweepTransaction? _txn;
    private string _currentCategory = "";

    public Cleaner(Action<string> log, CleanMode mode, bool vssForDangerous = false)
    {
        _log = log;
        _mode = mode;
        _vssForDangerous = vssForDangerous;
    }

    public async Task<CleanReport> CleanAsync(
        IEnumerable<CategoryScanResult> selected,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var report = new CleanReport();
        if (_mode == CleanMode.Quarantine)
            _txn = new SweepTransaction(_log);

        var dryTag = _mode == CleanMode.Dry ? "[dry] " : "";

        // Take VSS snapshots BEFORE any deletion — one per unique volume touched
        // by a Dangerous category. Best-effort: log + continue on failure.
        // Skip entirely in Dry mode (would consume snapshot slots for no reason).
        if (_vssForDangerous && _mode != CleanMode.Dry)
            TakeVssSnapshotsForDangerous(selected);

        try
        {
            await Task.Run(() =>
            {
                foreach (var cat in selected)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"Cleaning: {cat.Category.Name}");
                    _log($"── {dryTag}{cat.Category.Name}");
                    _currentCategory = cat.Category.Id;

                    foreach (var loc in cat.Locations)
                    {
                        // Recycle Bin is emptied via shell32, not a directory walk —
                        // and a shell empty is NOT rollbackable, so it only runs in
                        // Direct/Dry mode. In Safe mode it's skipped loudly.
                        if (loc.Target.Scope == TargetScope.RecycleBin)
                        {
                            if (_mode == CleanMode.Quarantine)
                            {
                                _log("   SKIP Recycle Bin: not rollbackable — " +
                                     "use Direct clean (or Commit first, then run again).");
                                continue;
                            }
                            var (rbBytes, _) = RecycleBinInterop.Query();
                            if (_mode == CleanMode.Dry)
                            {
                                report.BytesFreed += rbBytes;
                                _log($"   [dry] would empty Recycle Bin ({Format.Bytes(rbBytes)})");
                                continue;
                            }
                            if (RecycleBinInterop.Empty())
                            {
                                report.BytesFreed += rbBytes;
                                _log($"   Recycle Bin emptied ({Format.Bytes(rbBytes)})");
                            }
                            else { report.Errors++; _log("   Recycle Bin empty failed"); }
                            continue;
                        }

                        if (Scanner.IsReparsePoint(loc.Path))
                        {
                            _log($"   SKIP (reparse point): {loc.Path}");
                            continue;
                        }

                        if (SelfProtection.IsProtected(loc.Path))
                        {
                            _log($"   SKIP (BE-TFC's own files): {loc.Path}");
                            continue;
                        }

                        switch (loc.Target.Mode)
                        {
                            case DeleteMode.FilesMatching:
                                foreach (var f in Scanner.SafeEnumFiles(loc.Path, loc.Target.FilePattern ?? "*"))
                                    RemoveFile(f, report);
                                break;

                            case DeleteMode.Contents:
                                RemoveContents(loc.Path, report, ct);
                                break;

                            case DeleteMode.DirectoryItself:
                                RemoveContents(loc.Path, report, ct);
                                TryDeleteEmptyDir(loc.Path);
                                break;
                        }
                    }
                }
            }, ct);
        }
        finally
        {
            if (_txn is not null)
            {
                _txn.SaveJournal();
                _log($"── Transaction {_txn.TxnId} journaled " +
                     $"({_txn.Journal.Entries.Count} files quarantined, " +
                     $"{_txn.Journal.Unrecoverable.Count} unrecoverable).");
                _txn.Dispose();
            }
        }

        var freedNote = _mode switch
        {
            CleanMode.Quarantine => " (space frees on Commit)",
            CleanMode.Dry        => " (dry run — nothing was changed)",
            _                    => "",
        };
        var verb = _mode == CleanMode.Dry ? "would remove" : "removed";
        _log($"── {dryTag}Done. {Format.Bytes(report.BytesFreed)}{freedNote}, " +
             $"{report.FilesDeleted} files {verb}, " +
             $"{report.FilesScheduledForReboot} scheduled for reboot, " +
             $"{report.Errors} errors.");
        if (report.SelfProtectedSkips > 0)
            _log($"   ({report.SelfProtectedSkips} files skipped: BE-TFC's own runtime files)");
        return report;
    }

    public string? TransactionId => _txn?.TxnId;

    // ─────────────────────────── internals ───────────────────────────

    private void RemoveContents(string root, CleanReport report, CancellationToken ct)
    {
        foreach (var dir in Scanner.SafeEnumDirs(root))
        {
            ct.ThrowIfCancellationRequested();
            if (SelfProtection.IsProtected(dir))
            {
                _log($"   SKIP (BE-TFC's own files): {dir}");
                continue;
            }
            if (Scanner.IsReparsePoint(dir))
            {
                if (_mode == CleanMode.Dry) continue;
                try { Directory.Delete(dir, recursive: false); } // link only, never target
                catch { report.Errors++; }
                continue;
            }
            RemoveContents(dir, report, ct);
            if (_mode != CleanMode.Dry) TryDeleteEmptyDir(dir);
        }

        foreach (var file in Scanner.SafeEnumFiles(root, "*"))
        {
            ct.ThrowIfCancellationRequested();
            RemoveFile(file, report);
        }
    }

    private void RemoveFile(string file, CleanReport report)
    {
        // Defence in depth: the directory walk already skips protected roots,
        // but a target can resolve directly onto one.
        if (SelfProtection.IsProtected(file)) { report.SelfProtectedSkips++; return; }

        long size = 0;
        try { size = new FileInfo(file).Length; } catch { }

        if (_mode == CleanMode.Dry)
        {
            report.BytesFreed += size;
            report.FilesDeleted++;
            return;
        }

        // Quarantine mode: rename into store (metadata-only, often works on locked files too)
        if (_mode == CleanMode.Quarantine && _txn!.TryQuarantine(file, size, _currentCategory))
        {
            report.BytesFreed += size;   // "cleaned" size; physically freed on Commit
            report.FilesDeleted++;
            return;
        }

        // Direct mode, or quarantine move failed (hard lock / cross-volume oddity)
        try
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
            report.BytesFreed += size;
            report.FilesDeleted++;
        }
        catch (IOException)                 { ScheduleForReboot(file, size, report); }
        catch (UnauthorizedAccessException) { ScheduleForReboot(file, size, report); }
        catch                               { report.Errors++; }
    }

    private void ScheduleForReboot(string file, long size, CleanReport report)
    {
        if (MoveFileEx(file, null, MOVEFILE_DELAY_UNTIL_REBOOT))
        {
            report.FilesScheduledForReboot++;
            report.BytesFreed += size;
            _txn?.MarkUnrecoverable(file);   // journaled as non-rollbackable
            _log($"   reboot-delete (NOT rollbackable): {file}");
        }
        else
        {
            report.Errors++;
            _log($"   FAILED: {file}");
        }
    }

    private static void TryDeleteEmptyDir(string dir)
    {
        try { Directory.Delete(dir, recursive: false); }
        catch { /* not empty or in use — fine */ }
    }

    private void TakeVssSnapshotsForDangerous(IEnumerable<Models.CategoryScanResult> selected)
    {
        var volumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in selected.Where(c => c.Category.Dangerous))
        foreach (var loc in cat.Locations)
        {
            var root = Path.GetPathRoot(loc.Path);
            if (!string.IsNullOrEmpty(root)) volumes.Add(root);
        }

        if (volumes.Count == 0) return;
        _log("── VSS snapshot (safety net for Dangerous categories)");
        foreach (var vol in volumes)
        {
            var r = VssInterop.CreateSnapshot(vol);
            if (r.Ok)
                _log($"   snapshot created on {vol}: {r.ShadowId}");
            else
                _log($"   snapshot FAILED on {vol}: {r.Message}  " +
                     "(continuing — restore via Previous Versions won't be available)");
        }
    }
}

public static class Format
{
    public static string Bytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{b / (double)(1L << 10):0} KB",
        _ => $"{b} B",
    };
}
