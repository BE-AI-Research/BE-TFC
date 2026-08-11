using BETFC.Engine;

namespace BETFC.Tests;

public sealed class TransactionTests
{
    // ─────────────────────────── Quarantine ───────────────────────────

    [Fact]
    public void Quarantine_MovesFileIntoStore_AndJournalsEntry()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("a.txt", "payload");

        using var txn = new SweepTransaction(ws.Log, ws.Layout);
        var ok = txn.TryQuarantine(file, size: 7, category: "test");

        Assert.True(ok);
        Assert.False(File.Exists(file));
        Assert.Single(txn.Journal.Entries);

        var entry = txn.Journal.Entries[0];
        Assert.Equal(file, entry.OriginalPath);
        Assert.True(File.Exists(entry.QuarantinePath));
        Assert.Equal("payload", File.ReadAllText(entry.QuarantinePath));
        Assert.Equal("test", entry.Category);
        Assert.StartsWith(Path.Combine(ws.QuarantineRoot, txn.TxnId),
            entry.QuarantinePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Quarantine_ClearsReadOnlyAttribute_AndSucceeds()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("readonly.txt", "locked",
            attrs: FileAttributes.ReadOnly | FileAttributes.Hidden);

        using var txn = new SweepTransaction(ws.Log, ws.Layout);
        Assert.True(txn.TryQuarantine(file, 6, "test"));
        Assert.False(File.Exists(file));
        Assert.Single(txn.Journal.Entries);
    }

    [Fact]
    public void Quarantine_HandlesNestedDirs_AsFlatSequencedStore()
    {
        using var ws = new TestWorkspace();
        var f1 = ws.CreateFile(@"A\B\C\deep.txt", "1");
        var f2 = ws.CreateFile(@"A\B\shallow.txt", "2");
        var f3 = ws.CreateFile(@"A\top.txt", "3");

        using var txn = new SweepTransaction(ws.Log, ws.Layout);
        Assert.True(txn.TryQuarantine(f1, 1, "c"));
        Assert.True(txn.TryQuarantine(f2, 1, "c"));
        Assert.True(txn.TryQuarantine(f3, 1, "c"));

        // Store is flat + sequenced (000000, 000001, 000002).
        var txnDir = Path.Combine(ws.QuarantineRoot, txn.TxnId);
        var entries = Directory.GetFiles(txnDir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "000000", "000001", "000002" }, entries);

        Assert.Equal(3, txn.Journal.Entries.Count);
        Assert.Equal(new[] { f1, f2, f3 },
            txn.Journal.Entries.Select(e => e.OriginalPath).ToArray());
    }

    // ─────────────────────────── Journal & pending detection ───────────────────────────

    [Fact]
    public void SaveJournal_WritesTxnJson_ThatFindPendingCanDeserialize()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("j.txt", "x");

        string txnId;
        using (var txn = new SweepTransaction(ws.Log, ws.Layout))
        {
            txn.TryQuarantine(file, 1, "test");
            txn.SaveJournal();
            txnId = txn.TxnId;

            var journalPath = Path.Combine(ws.QuarantineRoot, txnId, "txn.json");
            Assert.True(File.Exists(journalPath));
        }

        var pending = QuarantineStore.FindPending(ws.Layout);
        Assert.Single(pending);
        Assert.Equal(txnId, pending[0].Journal.TxnId);
        Assert.Single(pending[0].Journal.Entries);
        Assert.False(pending[0].Journal.Committed);
    }

    [Fact]
    public void Dispose_WithoutExplicitSave_StillPersistsJournal_ForCrashSafety()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("c.txt", "y");
        string txnId;

        // Simulate a crash mid-clean: quarantine but never call SaveJournal.
        {
            var txn = new SweepTransaction(ws.Log, ws.Layout);
            txn.TryQuarantine(file, 1, "test");
            txnId = txn.TxnId;
            txn.Dispose();
        }

        var pending = QuarantineStore.FindPending(ws.Layout);
        Assert.Single(pending);
        Assert.Equal(txnId, pending[0].Journal.TxnId);
    }

    [Fact]
    public void FindPending_IgnoresCommittedJournals()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("done.txt", "z");
        using (var txn = new SweepTransaction(ws.Log, ws.Layout))
        {
            txn.TryQuarantine(file, 1, "test");
            txn.SaveJournal();

            // Hand-flip Committed=true on disk (mimics a Commit having marked it done).
            var journalPath = Path.Combine(ws.QuarantineRoot, txn.TxnId, "txn.json");
            var text = File.ReadAllText(journalPath);
            File.WriteAllText(journalPath, text.Replace("\"Committed\": false", "\"Committed\": true"));
        }

        Assert.Empty(QuarantineStore.FindPending(ws.Layout));
    }

    // ─────────────────────────── Rollback ───────────────────────────

    [Fact]
    public void Rollback_RestoresFilesAndRebuildsMissingDirectories()
    {
        using var ws = new TestWorkspace();
        var f1 = ws.CreateFile(@"A\B\C\deep.txt", "one");
        var f2 = ws.CreateFile(@"A\top.txt", "two");

        using (var txn = new SweepTransaction(ws.Log, ws.Layout))
        {
            txn.TryQuarantine(f1, 3, "test");
            txn.TryQuarantine(f2, 3, "test");
            txn.SaveJournal();
        }

        // Simulate Cleaner tearing down empty dirs after removing contents.
        Directory.Delete(Path.Combine(ws.OriginalsDir, "A"), recursive: true);
        Assert.False(Directory.Exists(Path.Combine(ws.OriginalsDir, "A")));

        var pending = QuarantineStore.FindPending(ws.Layout);
        Assert.Single(pending);
        var (restored, skipped, errors) = QuarantineStore.Rollback(pending[0], ws.Log);

        Assert.Equal(2, restored);
        Assert.Equal(0, skipped);
        Assert.Equal(0, errors);
        Assert.True(File.Exists(f1));
        Assert.True(File.Exists(f2));
        Assert.Equal("one", File.ReadAllText(f1));
        Assert.Equal("two", File.ReadAllText(f2));
    }

    [Fact]
    public void Rollback_SkipsFilesWhoseOriginalHasReappeared_NeverOverwrites()
    {
        using var ws = new TestWorkspace();
        var f = ws.CreateFile("collide.txt", "quarantined-copy");
        using (var txn = new SweepTransaction(ws.Log, ws.Layout))
        {
            txn.TryQuarantine(f, 16, "test");
            txn.SaveJournal();
        }

        // Someone (or something) has recreated the original path since the sweep.
        File.WriteAllText(f, "user-recreated");

        var pending = QuarantineStore.FindPending(ws.Layout);
        var (restored, skipped, errors) = QuarantineStore.Rollback(pending[0], ws.Log);

        Assert.Equal(0, restored);
        Assert.Equal(1, skipped);
        Assert.Equal(0, errors);
        Assert.Equal("user-recreated", File.ReadAllText(f));  // never overwritten
        // Quarantined copy stays on disk (skipped restores are not deleted).
        Assert.True(File.Exists(pending[0].Journal.Entries[0].QuarantinePath));
    }

    [Fact]
    public void Rollback_EmptiesTxnDir_AndRemovesQuarantineRootIfEmpty()
    {
        using var ws = new TestWorkspace();
        var f = ws.CreateFile("solo.txt", "s");
        using (var txn = new SweepTransaction(ws.Log, ws.Layout))
        {
            txn.TryQuarantine(f, 1, "test");
            txn.SaveJournal();
        }

        var pending = QuarantineStore.FindPending(ws.Layout);
        QuarantineStore.Rollback(pending[0], ws.Log);

        // Txn dir cleaned; quarantine root left empty and removed too.
        Assert.False(Directory.Exists(pending[0].TxnDir));
        Assert.False(Directory.Exists(ws.QuarantineRoot));
    }

    // ─────────────────────────── Commit ───────────────────────────

    [Fact]
    public void Commit_PurgesQuarantine_ReportsBytesFreed()
    {
        using var ws = new TestWorkspace();
        var f1 = ws.CreateFile("big1.bin", new string('x', 100));
        var f2 = ws.CreateFile("big2.bin", new string('y', 200));

        using (var txn = new SweepTransaction(ws.Log, ws.Layout))
        {
            txn.TryQuarantine(f1, 100, "test");
            txn.TryQuarantine(f2, 200, "test");
            txn.SaveJournal();
        }

        var pending = QuarantineStore.FindPending(ws.Layout);
        var (bytesFreed, errors) = QuarantineStore.Commit(pending[0], ws.Log);

        Assert.Equal(300, bytesFreed);
        Assert.Equal(0, errors);
        Assert.False(Directory.Exists(pending[0].TxnDir));
        Assert.False(Directory.Exists(ws.QuarantineRoot));  // root cleared too
        Assert.Empty(QuarantineStore.FindPending(ws.Layout));
    }

    // ─────────────────────────── Unrecoverable tracking ───────────────────────────

    [Fact]
    public void Rollback_RestoresReadOnlyAndHiddenAttributes()
    {
        using var ws = new TestWorkspace();
        var roFile = ws.CreateFile("ro.txt", "readonly-payload",
            attrs: FileAttributes.ReadOnly);
        var hiddenFile = ws.CreateFile("hidden.txt", "hidden-payload",
            attrs: FileAttributes.Hidden);

        using (var txn = new SweepTransaction(ws.Log, ws.Layout))
        {
            Assert.True(txn.TryQuarantine(roFile, 16, "test"));
            Assert.True(txn.TryQuarantine(hiddenFile, 14, "test"));
            txn.SaveJournal();
        }

        var pending = QuarantineStore.FindPending(ws.Layout);
        QuarantineStore.Rollback(pending[0], ws.Log);

        Assert.True(File.Exists(roFile));
        Assert.True(File.Exists(hiddenFile));
        Assert.True((File.GetAttributes(roFile) & FileAttributes.ReadOnly) != 0,
            "read-only attribute should survive round-trip");
        Assert.True((File.GetAttributes(hiddenFile) & FileAttributes.Hidden) != 0,
            "hidden attribute should survive round-trip");
    }

    [Fact]
    public void MarkUnrecoverable_AppearsInJournal_AndSurvivesRoundTrip()
    {
        using var ws = new TestWorkspace();
        var f = ws.CreateFile("ok.txt", "o");

        string txnId;
        using (var txn = new SweepTransaction(ws.Log, ws.Layout))
        {
            txn.TryQuarantine(f, 1, "test");
            txn.MarkUnrecoverable(@"C:\Windows\Temp\held-open.log");
            txn.SaveJournal();
            txnId = txn.TxnId;
        }

        var pending = QuarantineStore.FindPending(ws.Layout);
        Assert.Single(pending);
        Assert.Equal(new[] { @"C:\Windows\Temp\held-open.log" },
            pending[0].Journal.Unrecoverable.ToArray());
    }
}
