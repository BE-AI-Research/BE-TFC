using BETFC.Cli;
using BETFC.Engine;

namespace BETFC.Tests;

/// <summary>
/// The journal is the only thing that makes a quarantine reversible. Without
/// txn.json the store holds files that cannot be restored (no mapping back to
/// their original paths) and, before the orphan sweep existed, could not even be
/// found again. These pin the durability guarantees.
///
/// Written after a v1.2.0 build left 160,647 files (3.81 GB) in a real store
/// with no journal, invisible to the tool for as long as it sat there.
/// </summary>
public sealed class JournalDurabilityTests
{
    [Fact]
    public void SaveJournal_WritesTxnJson()
    {
        using var ws = new TestWorkspace();
        var src = ws.CreateFile("a.txt");

        var txn = new SweepTransaction(ws.Log, ws.Layout);
        Assert.True(txn.TryQuarantine(src, 5, "test-cat"));
        txn.SaveJournal();

        var journal = Path.Combine(ws.QuarantineRoot, txn.TxnId, "txn.json");
        Assert.True(File.Exists(journal), "SaveJournal did not produce txn.json");
        Assert.Contains("a.txt", File.ReadAllText(journal));
    }

    /// <summary>
    /// Crash safety: an exit that never reaches SaveJournal must still leave a
    /// rollbackable journal behind, written from the disposer.
    /// </summary>
    [Fact]
    public void Dispose_WritesJournal_WhenSaveNeverRan()
    {
        using var ws = new TestWorkspace();
        var src = ws.CreateFile("b.txt");

        string txnId;
        using (var txn = new SweepTransaction(ws.Log, ws.Layout))
        {
            txnId = txn.TxnId;
            Assert.True(txn.TryQuarantine(src, 5, "test-cat"));
            // deliberately no SaveJournal() — simulate an abrupt exit
        }

        Assert.True(File.Exists(Path.Combine(ws.QuarantineRoot, txnId, "txn.json")),
            "Dispose did not persist the journal — quarantined files would be orphaned");
    }

    /// <summary>A transaction that moved nothing should not litter the disk.</summary>
    [Fact]
    public void Dispose_WritesNothing_WhenNothingWasQuarantined()
    {
        using var ws = new TestWorkspace();
        using (var txn = new SweepTransaction(ws.Log, ws.Layout)) { }

        Assert.Empty(Directory.GetFiles(ws.QuarantineRoot, "txn.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void FindPending_DiscoversAJournaledTransaction()
    {
        using var ws = new TestWorkspace();
        var src = ws.CreateFile("c.txt");

        var txn = new SweepTransaction(ws.Log, ws.Layout);
        txn.TryQuarantine(src, 5, "test-cat");
        txn.SaveJournal();

        var pending = QuarantineStore.FindPending(ws.Layout);
        Assert.Single(pending);
        Assert.Single(pending[0].Journal.Entries);
    }

    /// <summary>
    /// The defect that made the real-world loss permanent: a txn directory with
    /// no journal was skipped by FindPending, so the tool never mentioned it
    /// again. It must be surfaced as an orphan instead — unrollbackable, but at
    /// least visible and reclaimable.
    /// </summary>
    [Fact]
    public void FindOrphans_SurfacesATxnDirectoryWithNoJournal()
    {
        using var ws = new TestWorkspace();

        // A store containing quarantined files but no txn.json.
        var orphanDir = Path.Combine(ws.QuarantineRoot, "20260810-221147-307aaf");
        Directory.CreateDirectory(orphanDir);
        File.WriteAllText(Path.Combine(orphanDir, "000000"), new string('x', 100));
        File.WriteAllText(Path.Combine(orphanDir, "000001"), new string('x', 200));

        Assert.Empty(QuarantineStore.FindPending(ws.Layout));

        var orphans = QuarantineStore.FindOrphans(ws.Layout);
        var orphan = Assert.Single(orphans);
        Assert.Equal(orphanDir, orphan.TxnDir);
        Assert.Equal(2, orphan.FileCount);
        Assert.Equal(300, orphan.TotalBytes);
    }

    /// <summary>A properly journaled transaction is not an orphan.</summary>
    [Fact]
    public void FindOrphans_IgnoresJournaledTransactions()
    {
        using var ws = new TestWorkspace();
        var src = ws.CreateFile("d.txt");

        var txn = new SweepTransaction(ws.Log, ws.Layout);
        txn.TryQuarantine(src, 5, "test-cat");
        txn.SaveJournal();

        Assert.Empty(QuarantineStore.FindOrphans(ws.Layout));
    }

    [Fact]
    public void DiscardOrphans_FlagParses_AndIsOffByDefault()
    {
        Assert.True(CliParser.Parse(["--silent", "--discard-orphans"]).DiscardOrphans);
        // Discarding is unrecoverable, so it must never be implied by other flags.
        Assert.False(CliParser.Parse(["--silent", "--commit-all"]).DiscardOrphans);
        Assert.False(CliParser.Parse(["--silent"]).DiscardOrphans);
    }

    [Fact]
    public void DiscardOrphan_RemovesTheDirectory()
    {
        using var ws = new TestWorkspace();
        var orphanDir = Path.Combine(ws.QuarantineRoot, "20260810-221147-307aaf");
        Directory.CreateDirectory(orphanDir);
        File.WriteAllText(Path.Combine(orphanDir, "000000"), "junk");

        var orphan = Assert.Single(QuarantineStore.FindOrphans(ws.Layout));
        var (ok, _) = QuarantineStore.DiscardOrphan(orphan, ws.Log);

        Assert.True(ok);
        Assert.False(Directory.Exists(orphanDir));
    }

    /// <summary>
    /// Discarding the last transaction must not leave an empty
    /// BE-TFC.Quarantine at the drive root — that is an install footprint, and
    /// a live 1.4.0 run left one behind. Commit already did this; discard did not.
    /// </summary>
    [Fact]
    public void DiscardOrphan_RemovesTheStoreRoot_WhenItEmpties()
    {
        using var ws = new TestWorkspace();
        var orphanDir = Path.Combine(ws.QuarantineRoot, "20260810-221147-307aaf");
        Directory.CreateDirectory(orphanDir);
        File.WriteAllText(Path.Combine(orphanDir, "000000"), "junk");

        var orphan = Assert.Single(QuarantineStore.FindOrphans(ws.Layout));
        QuarantineStore.DiscardOrphan(orphan, ws.Log);

        Assert.False(Directory.Exists(ws.QuarantineRoot),
            "empty quarantine root should have been removed");
    }

    /// <summary>But a root still holding another transaction must survive.</summary>
    [Fact]
    public void DiscardOrphan_KeepsTheStoreRoot_WhenOtherTransactionsRemain()
    {
        using var ws = new TestWorkspace();

        var orphanDir = Path.Combine(ws.QuarantineRoot, "20260810-221147-307aaf");
        Directory.CreateDirectory(orphanDir);
        File.WriteAllText(Path.Combine(orphanDir, "000000"), "junk");

        var keep = ws.CreateFile("keep.txt");
        var txn = new SweepTransaction(ws.Log, ws.Layout);
        txn.TryQuarantine(keep, 5, "test-cat");
        txn.SaveJournal();

        var orphan = Assert.Single(QuarantineStore.FindOrphans(ws.Layout));
        QuarantineStore.DiscardOrphan(orphan, ws.Log);

        Assert.True(Directory.Exists(ws.QuarantineRoot));
        Assert.Single(QuarantineStore.FindPending(ws.Layout));
    }
}
