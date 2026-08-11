using System.Diagnostics;
using BETFC.Engine;
using BETFC.Models;

namespace BETFC.Cli;

/// <summary>
/// Run log sink for silent mode. Human lines go to stdout unless --json is in
/// play (JSON must be the only thing on stdout so a caller can pipe it straight
/// into a parser), and to --log's file when one was requested. Warnings are
/// collected separately so they can be surfaced in the JSON report too.
/// </summary>
internal sealed class RunLog : IDisposable
{
    private readonly StreamWriter? _file;
    private readonly bool _toConsole;

    public List<string> Warnings { get; } = new();
    public string? FileError { get; }

    public RunLog(string? path, bool toConsole)
    {
        _toConsole = toConsole;
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _file = new StreamWriter(full, append: true) { AutoFlush = true };
            _file.WriteLine();
            _file.WriteLine($"===== {AppInfo.Banner} — {DateTime.Now:yyyy-MM-dd HH:mm:ss} " +
                            $"on {Environment.MachineName} =====");
        }
        catch (Exception ex)
        {
            // A read-only USB stick or a bad path must not abort the clean.
            FileError = $"could not open log file '{path}': {ex.Message}";
        }
    }

    public void Line(string s)
    {
        if (_toConsole) Console.WriteLine(s);
        _file?.WriteLine(s);
    }

    public void Warn(string s)
    {
        Warnings.Add(s);
        Line("warning: " + s);
    }

    public void Dispose() => _file?.Dispose();
}

/// <summary>Runs a scan/clean/commit sequence with no UI. Exit codes are the
/// numeric return of <see cref="RunAsync"/>.</summary>
public static class SilentRunner
{
    public const int ExitOk            = 0;
    public const int ExitErrors        = 1;
    public const int ExitRebootPending = 2;

    public static async Task<int> RunAsync(CliOptions opts, CancellationToken ct = default)
    {
        var startedUtc = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        using var log = new RunLog(opts.LogPath, toConsole: !opts.Json);
        var report = new JsonRunReport
        {
            Version      = AppInfo.Version,
            BuildVersion = AppInfo.BuildVersion,
            Architecture = AppInfo.Architecture,
            Machine      = Environment.MachineName,
            StartedUtc   = startedUtc,
            Mode         = opts.Mode.ToString(),
            DryRun       = opts.Mode == CleanMode.Dry,
            ScanOnly     = opts.NoClean,
        };

        if (log.FileError is { } fileErr) log.Warn(fileErr);

        log.Line($"{AppInfo.Banner} — silent mode ({opts.Mode})");
        log.Line(new string('─', 60));

        // Disclose what is excluded from this run. The GUI prints these on
        // launch; a silent run left them out entirely, so the saved log — the
        // only record a scripted run leaves behind — never said which paths
        // were held back from the totals it reports.
        foreach (var root in SelfProtection.ProtectedRoots)
            log.Line($"Self-protected (never cleaned): {root}");

        var freeBefore = DiskSpace.Snapshot();

        // ─── Optional: handle pre-existing pending quarantine ───
        HandlePendingQuarantine(opts, log);
        HandleOrphanedQuarantine(opts, log);

        // ─── Select categories ───
        var chosen = SelectCategories(opts, out var missing);
        if (missing.Count > 0)
        {
            log.Line($"error: unknown category id(s): {string.Join(", ", missing)}");
            log.Line("       run --list-categories to see valid ids.");
            return Finish(opts, log, report, sw, freeBefore, ExitErrors);
        }
        if (chosen.Count == 0)
        {
            log.Line("No categories selected — nothing to do.");
            return Finish(opts, log, report, sw, freeBefore, ExitOk);
        }
        log.Line($"Categories: {string.Join(", ", chosen.Select(c => c.Id))}");

        // Silent mode cannot prompt, so anything the GUI would ask consent for is
        // recorded instead. The caller opted in by naming the id explicitly.
        if (opts.Mode != CleanMode.Dry)
        {
            foreach (var cat in chosen.Where(c => c.SelectWarning is not null))
                log.Warn($"'{cat.Id}' performs permanent, non-rollbackable deletion. " +
                         $"{cat.Description}");
        }

        // ─── Scan ───
        log.Line("Scanning...");
        var scanner = new Scanner();
        report.Profiles.AddRange(scanner.Profiles.Select(p => p.UserName));
        log.Line($"  Profiles: {string.Join(", ", report.Profiles)}");

        var scanSw = Stopwatch.StartNew();
        var results = await scanner.ScanAsync(chosen, new Progress<string>(_ => { }), ct);
        scanSw.Stop();

        report.ScannedBytes = results.Sum(r => r.TotalBytes);
        report.ScannedFiles = results.Sum(r => r.TotalFiles);
        foreach (var r in results.OrderByDescending(r => r.TotalBytes))
        {
            report.Categories.Add(new JsonCategoryResult
            {
                Id = r.Category.Id, Name = r.Category.Name,
                Dangerous = r.Category.Dangerous,
                Bytes = r.TotalBytes, Files = r.TotalFiles,
            });
        }

        log.Line($"Scan complete in {scanSw.Elapsed.TotalSeconds:F1}s: " +
                 $"{Format.Bytes(report.ScannedBytes)} in {report.ScannedFiles:N0} files.");
        foreach (var c in report.Categories)
            log.Line($"  {Format.Bytes(c.Bytes),12}  {c.Files,8:N0}  {c.Name}");

        if (opts.NoClean)
        {
            log.Line("--no-clean: scan-only run, exiting.");
            return Finish(opts, log, report, sw, freeBefore, ExitOk);
        }

        // ─── Clean ───
        log.Line($"Cleaning ({opts.Mode})...");
        var cleaner = new Cleaner(log.Line, opts.Mode,
            vssForDangerous: opts.Vss && results.Any(r => r.Category.Dangerous));
        var cleanReport = await cleaner.CleanAsync(results, new Progress<string>(_ => { }), ct);

        report.BytesCleaned            = cleanReport.BytesFreed;
        report.FilesDeleted            = cleanReport.FilesDeleted;
        report.FilesScheduledForReboot = cleanReport.FilesScheduledForReboot;
        report.Errors                  = cleanReport.Errors;
        report.SelfProtectedSkips      = cleanReport.SelfProtectedSkips;
        report.RebootRecommended       = cleanReport.RebootRecommended;
        report.TransactionId           = cleaner.TransactionId;
        report.AwaitingCommit          = opts.Mode == CleanMode.Quarantine &&
                                         cleaner.TransactionId is not null;

        // Dry runs must not report in the past tense. The per-category lines
        // already say "[dry]", but this summary is the line most likely to be
        // quoted on its own, so it has to stand up alone.
        var dry = opts.Mode == CleanMode.Dry;
        log.Line($"Report: {Format.Bytes(cleanReport.BytesFreed)} " +
                 (dry ? "would be cleaned, " : "cleaned, ") +
                 $"{cleanReport.FilesDeleted:N0} files " +
                 (dry ? "would be removed, " : "removed, ") +
                 $"{cleanReport.FilesScheduledForReboot:N0} scheduled for reboot, " +
                 $"{cleanReport.Errors} errors.");
        if (report.AwaitingCommit)
        {
            log.Line($"Transaction: {report.TransactionId}");
            log.Line("Space is NOT yet freed — quarantine holds it until --commit-all " +
                     "(or --commit-stale on a later run).");
        }

        var exit = cleanReport.Errors > 0            ? ExitErrors
                 : cleanReport.RebootRecommended     ? ExitRebootPending
                                                     : ExitOk;
        return Finish(opts, log, report, sw, freeBefore, exit);
    }

    // ─────────────────────────── helpers ───────────────────────────

    private static int Finish(CliOptions opts, RunLog log, JsonRunReport report,
                              Stopwatch sw, List<DiskSpace.VolumeFree> freeBefore, int exitCode)
    {
        sw.Stop();
        report.FinishedUtc     = DateTime.UtcNow;
        report.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
        report.ExitCode        = exitCode;
        report.Warnings.AddRange(log.Warnings);

        var freeAfter = DiskSpace.Snapshot();
        var afterByRoot = freeAfter.ToDictionary(v => v.Root, StringComparer.OrdinalIgnoreCase);
        foreach (var was in freeBefore)
        {
            afterByRoot.TryGetValue(was.Root, out var now);
            report.Volumes.Add(new JsonVolume
            {
                Root            = was.Root,
                FreeBytesBefore = was.FreeBytes,
                FreeBytesAfter  = now?.FreeBytes ?? was.FreeBytes,
                TotalBytes      = was.TotalBytes,
            });
        }

        // Only attribute a free-space change to this run if the run could
        // actually have caused one. A dry run that reports "+8.8 MB" is claiming
        // credit for ambient system activity during the scan, which is exactly
        // the kind of dishonest number this tool must not print — a tech pastes
        // that line into a ticket. Commit/rollback/discard do free space even
        // under --dry, so those still report.
        var couldHaveChangedDisk =
            opts.Mode != CleanMode.Dry && !opts.NoClean
            || opts.CommitAll || opts.RollbackAll || opts.DiscardOrphans
            || opts.CommitStaleDays.HasValue;

        if (couldHaveChangedDisk && DiskSpace.Describe(freeBefore, freeAfter) is { } delta)
            log.Line("Disk: " + delta);

        if (opts.Json) Console.WriteLine(RunReportSerializer.ToJson(report));
        return exitCode;
    }

    /// <summary>
    /// Quarantine folders with no usable journal cannot be rolled back, so they
    /// are reported separately from the pending count and only ever discarded —
    /// never presented as recoverable. Silent runs discard only when explicitly
    /// asked; otherwise this is a loud warning, because the alternative is the
    /// space sitting on a client's disk unnoticed indefinitely.
    /// </summary>
    private static void HandleOrphanedQuarantine(CliOptions opts, RunLog log)
    {
        var orphans = QuarantineStore.FindOrphans();
        if (orphans.Count == 0) return;

        var bytes = orphans.Sum(o => o.TotalBytes);
        var files = orphans.Sum(o => o.FileCount);

        if (opts.DiscardOrphans)
        {
            log.Line($"Discarding {orphans.Count} orphaned quarantine folder(s) " +
                     $"({files:N0} files, {Format.Bytes(bytes)})...");
            long freed = 0; int failed = 0;
            foreach (var o in orphans)
            {
                var (ok, _) = QuarantineStore.DiscardOrphan(o, log.Line);
                if (ok) freed += o.TotalBytes; else failed++;
            }
            log.Line($"Reclaimed {Format.Bytes(freed)}" +
                     (failed > 0 ? $", {failed} could not be removed." : "."));
            return;
        }

        log.Warn($"{orphans.Count} quarantine folder(s) have no usable journal, holding " +
                 $"{Format.Bytes(bytes)} in {files:N0} files. They CANNOT be rolled back " +
                 "(no record of where the files came from). Use --discard-orphans to reclaim.");
        foreach (var o in orphans) log.Warn($"   orphaned: {o.TxnDir}");
    }

    private static void HandlePendingQuarantine(CliOptions opts, RunLog log)
    {
        var pending = QuarantineStore.FindPending();
        if (pending.Count == 0) return;

        if (opts.RollbackAll)
        {
            log.Line($"Rolling back {pending.Count} pending transaction(s)...");
            foreach (var t in pending)
            {
                log.Line($"  {t.Journal.TxnId}");
                QuarantineStore.Rollback(t, log.Line);
            }
        }
        else if (opts.CommitAll)
        {
            log.Line($"Committing {pending.Count} pending transaction(s)...");
            long freed = 0;
            foreach (var t in pending)
            {
                log.Line($"  {t.Journal.TxnId}");
                var (b, _) = QuarantineStore.Commit(t, log.Line);
                freed += b;
            }
            log.Line($"Freed {Format.Bytes(freed)} from prior transactions.");
        }
        else if (opts.CommitStaleDays is { } days)
        {
            var cutoff = DateTime.UtcNow.AddDays(-days);
            var stale = pending.Where(p => p.Journal.StartedUtc < cutoff).ToList();
            if (stale.Count == 0)
            {
                log.Line($"No stale transactions (>{days}d old) to commit.");
                return;
            }

            log.Line($"Committing {stale.Count} stale transaction(s) (older than {days}d)...");
            long freed = 0;
            foreach (var t in stale)
            {
                log.Line($"  {t.Journal.TxnId} " +
                         $"(age {(int)Math.Floor((DateTime.UtcNow - t.Journal.StartedUtc).TotalDays)}d)");
                var (b, _) = QuarantineStore.Commit(t, log.Line);
                freed += b;
            }
            log.Line($"Freed {Format.Bytes(freed)} from stale transactions.");
        }
        else
        {
            log.Warn($"{pending.Count} pending quarantine transaction(s) present, holding " +
                     $"{Format.Bytes(pending.Sum(p => p.Journal.Entries.Sum(e => e.SizeBytes)))} " +
                     "of client disk (use --commit-all / --commit-stale / --rollback-all).");
        }
    }

    private static List<CleanCategory> SelectCategories(CliOptions opts, out List<string> missing)
    {
        missing = new();
        IEnumerable<CleanCategory> pool;

        if (opts.CategoryIds.Count == 0)
        {
            pool = CategoryCatalog.All.Where(c => c.DefaultChecked);
        }
        else
        {
            var byId = CategoryCatalog.All.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
            var chosen = new List<CleanCategory>();
            foreach (var id in opts.CategoryIds)
            {
                if (byId.TryGetValue(id, out var cat)) chosen.Add(cat);
                else missing.Add(id);
            }
            pool = chosen;
        }

        return opts.IncludeDangerous
            ? pool.ToList()
            : pool.Where(c => !c.Dangerous).ToList();
    }
}
