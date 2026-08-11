using BETFC.Engine;
using BETFC.Models;

namespace BETFC.Tests;

/// <summary>
/// The catalog is the entire deletion authority of the tool. These pin the
/// invariants that make it safe to trust on a client machine.
/// </summary>
public sealed class CategoryCatalogTests
{
    [Fact]
    public void CategoryIds_AreUnique()
    {
        var dupes = CategoryCatalog.All
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(dupes);
    }

    [Fact]
    public void EveryCategory_HasIdNameAndDescription()
    {
        foreach (var c in CategoryCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Id));
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
            // The description is shown verbatim in the confirm dialogs, so an
            // empty one would produce a consent prompt that explains nothing.
            Assert.False(string.IsNullOrWhiteSpace(c.Description), $"{c.Id} has no description");
            Assert.NotEmpty(c.Targets);
        }
    }

    /// <summary>Doctrine 4: dangerous categories are never on by default.</summary>
    [Fact]
    public void DangerousCategories_AreNeverDefaultChecked()
    {
        foreach (var c in CategoryCatalog.All.Where(c => c.Dangerous))
            Assert.False(c.DefaultChecked, $"{c.Id} is Dangerous but DefaultChecked");
    }

    /// <summary>
    /// A category that warns on selection is asking for consent, which only
    /// makes sense if it is not already armed when the form opens.
    /// </summary>
    [Fact]
    public void WarningCategories_AreNeverDefaultChecked()
    {
        foreach (var c in CategoryCatalog.All.Where(c => c.SelectWarning is not null))
            Assert.False(c.DefaultChecked, $"{c.Id} warns on select but is DefaultChecked");
    }

    /// <summary>
    /// The Recycle Bin is deliberately NOT Dangerous — flagging it would change
    /// --include-dangerous semantics for existing scripts, and emptying the bin
    /// threatens nothing about the system. It carries a consent warning instead,
    /// because permanently destroying files the user chose to keep is inherent
    /// to what the bin is.
    /// </summary>
    [Fact]
    public void RecycleBin_IsNotDangerousButWarnsOnSelect()
    {
        var bin = CategoryCatalog.All.Single(c => c.Id == "recycle-bin");

        Assert.False(bin.Dangerous);
        Assert.False(bin.DefaultChecked);
        Assert.NotNull(bin.SelectWarning);
        Assert.Contains("permanently", bin.SelectWarning, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// RecycleBin scope is emptied through the shell and cannot be quarantined,
    /// so any category using it must carry a consent warning.
    /// </summary>
    [Fact]
    public void EveryRecycleBinScopedCategory_CarriesAWarning()
    {
        var binScoped = CategoryCatalog.All
            .Where(c => c.Targets.Any(t => t.Scope == TargetScope.RecycleBin));

        Assert.NotEmpty(binScoped);
        foreach (var c in binScoped)
            Assert.NotNull(c.SelectWarning);
    }

    [Fact]
    public void FilesMatchingTargets_AlwaysDeclareAPattern()
    {
        foreach (var c in CategoryCatalog.All)
        foreach (var t in c.Targets.Where(t => t.Mode == DeleteMode.FilesMatching))
            Assert.False(string.IsNullOrWhiteSpace(t.FilePattern),
                $"{c.Id} has a FilesMatching target with no pattern — it would match everything");
    }
}
