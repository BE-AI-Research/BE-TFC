using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BETFC.Engine;

// ─────────────────────────── Journal models ───────────────────────────

public sealed class TxnEntry
{
    public required string OriginalPath { get; set; }
    public required string QuarantinePath { get; set; }
    public long SizeBytes { get; set; }
    public string Category { get; set; } = "";
    /// <summary>File attributes captured before quarantine cleared them, so
    /// Rollback can put ReadOnly/Hidden/System back the way they were.</summary>
    public FileAttributes Attributes { get; set; } = FileAttributes.Normal;
}

public sealed class TxnJournal
{
    public string TxnId { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public bool Committed { get; set; }
    public List<TxnEntry> Entries { get; set; } = new();
    /// <summary>Files that could not be quarantined and were reboot-scheduled (unrecoverable).</summary>
    public List<string> Unrecoverable { get; set; } = new();
}

// AOT-safe JSON via source generation
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(TxnJournal))]
internal partial class TxnJsonContext : JsonSerializerContext { }

// ─────────────────────────── Quarantine layout ───────────────────────────

/// <summary>
/// Strategy for deciding *where* quarantine directories live and *where* to look
/// for pending transactions. Production uses <see cref="VolumeQuarantineLayout"/>
/// (per-volume roots at <c>&lt;drive&gt;\BE-TFC.Quarantine</c>). Tests inject
/// a layout that keeps everything under a single temp directory so runs never
/// touch the real filesystem.
/// </summary>
public interface IQuarantineLayout
{
    /// <summary>Quarantine root (parent of txn dirs) for a file about to be moved.</summary>
    string QuarantineRootFor(string filePath);
    /// <summary>All quarantine roots to enumerate when looking for pending transactions.</summary>
    IEnumerable<string> RootsToScan();

    /// <summary>Whether to replace the root's inherited ACL with an
    /// Administrators/SYSTEM-only one. True in production. Test layouts opt out:
    /// the harness runs unelevated in its own temp dir and locking itself out of
    /// that directory would break both the assertions and the cleanup.</summary>
    bool RestrictAccess => true;
}

/// <summary>Default: <c>&lt;drive&gt;\BE-TFC.Quarantine</c> per fixed volume. Keeps every move
/// same-volume (metadata-only rename, no copy).</summary>
public sealed class VolumeQuarantineLayout : IQuarantineLayout
{
    public string QuarantineRootFor(string filePath) =>
        Path.Combine(Path.GetPathRoot(filePath)!, SweepTransaction.QuarantineRootName);

    public IEnumerable<string> RootsToScan() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => Path.Combine(d.RootDirectory.FullName, SweepTransaction.QuarantineRootName));
}

// ─────────────────────────── Transaction ───────────────────────────

/// <summary>
/// OmniFS-style transactional sweep: files are renamed (same-volume, metadata-only)
/// into a hidden per-volume quarantine store and journaled. Space is freed only
/// on Commit; Rollback replays the journal in reverse.
///
/// Quarantine layout:  <VolumeRoot>\BE-TFC.Quarantine\<txnId>\<seq>
/// Journal:            <VolumeRoot>\BE-TFC.Quarantine\<txnId>\txn.json
/// </summary>
public sealed class SweepTransaction : IDisposable
{
    public const string QuarantineRootName = "BE-TFC.Quarantine";

    public string TxnId { get; }
    public TxnJournal Journal { get; }

    private readonly Dictionary<string, string> _volumeDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _log;
    private readonly IQuarantineLayout _layout;
    private int _seq;
    private bool _finalized;

    public SweepTransaction(Action<string> log, IQuarantineLayout? layout = null)
    {
        _log = log;
        _layout = layout ?? new VolumeQuarantineLayout();
        TxnId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" +
                Guid.NewGuid().ToString("N")[..6];
        Journal = new TxnJournal { TxnId = TxnId, StartedUtc = DateTime.UtcNow };
    }

    /// <summary>
    /// Try to quarantine a file (rename into the store). Returns true on success.
    /// On failure the caller decides the fallback (reboot-delete etc.).
    /// </summary>
    public bool TryQuarantine(string file, long size, string category)
    {
        try
        {
            var volDir = GetTxnDirForFile(file);
            var dest = Path.Combine(volDir, (_seq++).ToString("D6"));

            // Capture attributes before clearing them — Rollback restores them.
            // A Move on a ReadOnly file can fail on some volumes, so we clear first.
            var attrs = FileAttributes.Normal;
            try { attrs = File.GetAttributes(file); } catch { }
            File.SetAttributes(file, FileAttributes.Normal);
            File.Move(file, dest);

            Journal.Entries.Add(new TxnEntry
            {
                OriginalPath = file,
                QuarantinePath = dest,
                SizeBytes = size,
                Category = category,
                Attributes = attrs,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void MarkUnrecoverable(string file) => Journal.Unrecoverable.Add(file);

    private string GetTxnDirForFile(string file)
    {
        var quarantineRoot = _layout.QuarantineRootFor(file);
        if (_volumeDirs.TryGetValue(quarantineRoot, out var existing)) return existing;

        var freshRoot = !Directory.Exists(quarantineRoot);
        Directory.CreateDirectory(quarantineRoot);
        try
        {
            File.SetAttributes(quarantineRoot, FileAttributes.Directory |
                                               FileAttributes.Hidden | FileAttributes.System);
        }
        catch { /* cosmetic */ }

        // Restrict the store before a single byte lands in it. Re-applied even on
        // a pre-existing root: an earlier build created it with inherited ACLs.
        if (_layout.RestrictAccess) HardenQuarantineAcl(quarantineRoot, freshRoot);

        var txnDir = Path.Combine(quarantineRoot, TxnId);
        Directory.CreateDirectory(txnDir);
        _volumeDirs[quarantineRoot] = txnDir;
        return txnDir;
    }

    /// <summary>
    /// Replace the inherited ACL on the quarantine root with Administrators +
    /// SYSTEM full control only.
    ///
    /// A drive root such as C:\ grants BUILTIN\Users read and execute, and new
    /// subdirectories inherit that. Two consequences, both of which this closes:
    ///
    ///  - txn.json is *created* inside the store, so it inherits the permissive
    ///    ACL. It lists the full original path of every quarantined file —
    ///    usernames, profile layout, cache and dump filenames — readable by any
    ///    local account for as long as the transaction stays pending.
    ///  - Files whose origin ACL was already permissive (%SystemRoot%\Temp,
    ///    ProgramData WER dumps, which can hold process memory) keep that
    ///    permissive ACL and stay reachable while the root is traversable.
    ///
    /// Note the quarantined *files* are not themselves re-permissioned by the
    /// move: a same-volume File.Move preserves the source ACL rather than
    /// re-inheriting from the destination, so a file out of another user's
    /// profile keeps denying cross-user reads. Restricting the root is what
    /// makes that uniform — Users cannot traverse it, whatever a child allows.
    ///
    /// Best-effort: an ACL failure is logged loudly but never aborts a clean.
    /// </summary>
    private void HardenQuarantineAcl(string quarantineRoot, bool freshRoot)
    {
        try
        {
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

            var security = new DirectorySecurity();
            // true, false = stop inheriting, and do not copy inherited rules down.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(admins);

            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit |
                                             InheritanceFlags.ObjectInherit;
            foreach (var sid in new[] { admins, system })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    sid, FileSystemRights.FullControl, inherit,
                    PropagationFlags.None, AccessControlType.Allow));
            }

            new DirectoryInfo(quarantineRoot).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            _log($"   WARNING: could not restrict permissions on {quarantineRoot} — {ex.Message}");
            _log("            Quarantined files may be readable by other local users " +
                 "until this transaction is committed or rolled back.");
            return;
        }

        if (freshRoot)
            _log($"   quarantine store {quarantineRoot} (Administrators/SYSTEM only)");
    }

    /// <summary>Persist the journal to every volume that participated.</summary>
    public void SaveJournal()
    {
        var json = JsonSerializer.Serialize(Journal, TxnJsonContext.Default.TxnJournal);
        foreach (var dir in _volumeDirs.Values)
        {
            try { File.WriteAllText(Path.Combine(dir, "txn.json"), json); }
            catch (Exception ex) { _log($"   journal write failed on {dir}: {ex.Message}"); }
        }
        _finalized = true;
    }

    public void Dispose()
    {
        // Crash safety: if we never finalized, persist whatever we have so
        // a rollback is still possible after an unexpected exit.
        if (!_finalized && Journal.Entries.Count > 0) SaveJournal();
    }
}

// ─────────────────────────── Store operations ───────────────────────────

public static class QuarantineStore
{
    public sealed record PendingTxn(string TxnDir, TxnJournal Journal);

    /// <summary>Find uncommitted transactions across all quarantine roots (defaults to all fixed volumes).</summary>
    public static List<PendingTxn> FindPending(IQuarantineLayout? layout = null)
    {
        layout ??= new VolumeQuarantineLayout();
        var found = new List<PendingTxn>();
        foreach (var root in layout.RootsToScan())
        {
            if (!Directory.Exists(root)) continue;

            foreach (var txnDir in Scanner.SafeEnumDirs(root))
            {
                var journalPath = Path.Combine(txnDir, "txn.json");
                if (!File.Exists(journalPath)) continue;
                try
                {
                    var journal = JsonSerializer.Deserialize(
                        File.ReadAllText(journalPath), TxnJsonContext.Default.TxnJournal);
                    if (journal is { Committed: false })
                        found.Add(new PendingTxn(txnDir, journal));
                }
                catch { /* corrupt journal — leave on disk for manual inspection */ }
            }
        }
        return found;
    }

    /// <summary>
    /// A transaction directory holding quarantined files but no readable journal.
    /// </summary>
    public sealed record OrphanedTxn(string TxnDir, int FileCount, long TotalBytes);

    /// <summary>
    /// Find transaction directories that cannot be rolled back because their
    /// journal is missing or unreadable.
    ///
    /// <see cref="FindPending"/> deliberately ignores these — without the journal
    /// there is no mapping from the numbered quarantine files back to their
    /// original paths, so there is nothing to offer a rollback of. But ignoring
    /// them entirely is worse: a v1.2.0 build once left 160,647 files (3.81 GB)
    /// in a real store with no journal, and because nothing looked for
    /// journal-less directories the tool never mentioned them again. Silently
    /// accumulating quarantine on a client's disk is exactly what the stale
    /// prompt exists to prevent, so orphans get surfaced too — as unrecoverable
    /// and reclaimable, never as rollbackable.
    /// </summary>
    public static List<OrphanedTxn> FindOrphans(IQuarantineLayout? layout = null)
    {
        layout ??= new VolumeQuarantineLayout();
        var found = new List<OrphanedTxn>();

        foreach (var root in layout.RootsToScan())
        {
            if (!Directory.Exists(root)) continue;

            foreach (var txnDir in Scanner.SafeEnumDirs(root))
            {
                // A readable, uncommitted journal means FindPending owns it.
                var journalPath = Path.Combine(txnDir, "txn.json");
                if (File.Exists(journalPath) && IsReadableJournal(journalPath)) continue;

                long bytes = 0;
                int count = 0;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(txnDir, "*", SearchOption.AllDirectories))
                    {
                        if (Path.GetFileName(f).Equals("txn.json", StringComparison.OrdinalIgnoreCase))
                            continue;
                        try { bytes += new FileInfo(f).Length; count++; } catch { }
                    }
                }
                catch { continue; }

                // An empty directory is litter, not orphaned data, but it is
                // still ours to clear — report it so the sweep removes it.
                found.Add(new OrphanedTxn(txnDir, count, bytes));
            }
        }
        return found;
    }

    private static bool IsReadableJournal(string journalPath)
    {
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(journalPath), TxnJsonContext.Default.TxnJournal) is not null;
        }
        catch { return false; }
    }

    /// <summary>
    /// Delete an orphaned transaction directory, reclaiming its space. There is
    /// nothing to restore — the mapping is gone — so this is the only disposition
    /// available. Returns false with a reason if anything could not be removed;
    /// quarantined files keep their source ACL, so a store holding files out of
    /// %SystemRoot%\Temp or another profile needs elevation to clear.
    /// </summary>
    public static (bool ok, string message) DiscardOrphan(OrphanedTxn orphan, Action<string> log)
    {
        try
        {
            // Attributes are cleared on the way in, but a reboot-scheduled or
            // externally-touched file may have picked some back up.
            foreach (var f in Directory.EnumerateFiles(orphan.TxnDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(orphan.TxnDir, recursive: true);
            // Same as Commit: don't leave an empty BE-TFC.Quarantine sitting at
            // the drive root once the last transaction is gone. Doctrine 6 — no
            // install footprint.
            TryRemoveQuarantineRootIfEmpty(orphan.TxnDir);
            log($"   discarded orphaned quarantine {orphan.TxnDir} " +
                $"({orphan.FileCount:N0} files, {Format.Bytes(orphan.TotalBytes)})");
            return (true, "");
        }
        catch (Exception ex)
        {
            log($"   WARNING: could not discard {orphan.TxnDir} — {ex.Message}");
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Restore every quarantined file in this transaction directory to its
    /// original location. Files whose original path is now occupied are skipped.
    /// </summary>
    public static (int restored, int skipped, int errors) Rollback(PendingTxn txn, Action<string> log)
    {
        int restored = 0, skipped = 0, errors = 0;

        // Only entries whose quarantine file lives under THIS volume's txn dir
        foreach (var entry in txn.Journal.Entries
                     .Where(e => e.QuarantinePath.StartsWith(txn.TxnDir, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (!File.Exists(entry.QuarantinePath)) { skipped++; continue; }
                if (File.Exists(entry.OriginalPath))
                {
                    log($"   skip (exists): {entry.OriginalPath}");
                    skipped++; continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(entry.OriginalPath)!);
                File.Move(entry.QuarantinePath, entry.OriginalPath);
                // Restore attributes if we captured any that differ from Normal.
                if (entry.Attributes != FileAttributes.Normal &&
                    entry.Attributes != 0)
                {
                    try { File.SetAttributes(entry.OriginalPath, entry.Attributes); }
                    catch (Exception ex) { log($"   attr restore failed: {entry.OriginalPath} — {ex.Message}"); }
                }
                restored++;
            }
            catch (Exception ex)
            {
                log($"   restore failed: {entry.OriginalPath} — {ex.Message}");
                errors++;
            }
        }

        // Remove the (now mostly empty) txn dir if nothing is left
        TryRemoveTxnDir(txn.TxnDir);
        log($"   rollback: {restored} restored, {skipped} skipped, {errors} errors");
        return (restored, skipped, errors);
    }

    /// <summary>Commit: purge the quarantine directory. THIS is when space is freed.</summary>
    public static (long bytesFreed, int errors) Commit(PendingTxn txn, Action<string> log)
    {
        long bytes = txn.Journal.Entries
            .Where(e => e.QuarantinePath.StartsWith(txn.TxnDir, StringComparison.OrdinalIgnoreCase))
            .Sum(e => e.SizeBytes);
        int errors = 0;

        try { Directory.Delete(txn.TxnDir, recursive: true); }
        catch (Exception ex) { log($"   commit purge issue: {ex.Message}"); errors++; }

        TryRemoveQuarantineRootIfEmpty(txn.TxnDir);
        log($"   committed: {Format.Bytes(bytes)} freed");
        return (bytes, errors);
    }

    private static void TryRemoveTxnDir(string txnDir)
    {
        try
        {
            // delete journal + dir if no files remain
            var journal = Path.Combine(txnDir, "txn.json");
            if (Directory.EnumerateFileSystemEntries(txnDir).All(p =>
                    string.Equals(p, journal, StringComparison.OrdinalIgnoreCase)))
            {
                File.Delete(journal);
                Directory.Delete(txnDir);
                TryRemoveQuarantineRootIfEmpty(txnDir);
            }
        }
        catch { /* leave for next run */ }
    }

    private static void TryRemoveQuarantineRootIfEmpty(string txnDir)
    {
        try
        {
            var root = Path.GetDirectoryName(txnDir)!;
            if (!Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
        catch { }
    }
}
