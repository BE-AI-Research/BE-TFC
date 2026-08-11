using BETFC.Engine;

namespace BETFC.Tests;

/// <summary>
/// The self-exclusion guard exists so a single-file build cannot quarantine or
/// reboot-delete its own runtime out from under itself while cleaning
/// AppData\Local\Temp. These assertions pin the two ends of that: it must cover
/// where we actually run from, and it must not over-reach.
/// </summary>
public sealed class SelfProtectionTests
{
    /// <summary>
    /// Module enumeration exists to locate the bundle extraction directory,
    /// whose leaf is an unpredictable content hash. Unfiltered it also picks up
    /// every system directory the loader pulled a DLL from — a live 1.4.0 run
    /// protected System32 and three WinSxS directories. That narrows deletion
    /// scope in trees the catalog never targets and turns the startup
    /// "self-protected" log, which exists to state exactly what is excluded,
    /// into noise.
    /// </summary>
    [Fact]
    public void SystemDirectories_AreNotProtected()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var winsxs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS");

        Assert.DoesNotContain(SelfProtection.ProtectedRoots,
            r => r.Equals(system32, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(SelfProtection.ProtectedRoots,
            r => r.StartsWith(winsxs, StringComparison.OrdinalIgnoreCase));

        Assert.False(SelfProtection.IsProtected(system32));
        Assert.False(SelfProtection.IsProtected(winsxs));
    }

    /// <summary>
    /// %TEMP% must not appear as a protected *root*. An existing test covers
    /// IsProtected("%TEMP%"), but that would still pass if the root were added
    /// and then shadowed by another rule, so pin the list itself.
    /// </summary>
    [Fact]
    public void TempRoot_IsNotInTheProtectedRootsList()
    {
        var temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        Assert.DoesNotContain(SelfProtection.ProtectedRoots,
            r => r.Equals(temp, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every protected root must be a real, rooted path — the log
    /// presents these to a tech as fact.</summary>
    [Fact]
    public void ProtectedRoots_AreAllRootedAndNonEmpty()
    {
        foreach (var r in SelfProtection.ProtectedRoots)
        {
            Assert.False(string.IsNullOrWhiteSpace(r));
            Assert.True(Path.IsPathRooted(r), $"{r} is not rooted");
        }
    }

    [Fact]
    public void OwnDirectory_IsProtected()
    {
        var here = AppContext.BaseDirectory;
        Assert.True(SelfProtection.IsProtected(here),
            "the directory the test host is running from must be protected");
    }

    [Fact]
    public void FileInsideOwnDirectory_IsProtected()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "BETFC.Tests.dll");
        Assert.True(SelfProtection.IsProtected(file));
    }

    [Fact]
    public void NestedPathUnderOwnDirectory_IsProtected()
    {
        var nested = Path.Combine(AppContext.BaseDirectory, "sub", "deeper", "x.tmp");
        Assert.True(SelfProtection.IsProtected(nested));
    }

    [Fact]
    public void BundleExtractionBase_IsProtected()
    {
        // %TEMP%\.net is where a self-extracting single-file publish unpacks its
        // native libraries — the exact path user-temp would otherwise sweep.
        var extractBase = Path.Combine(Path.GetTempPath(), ".net");
        Assert.True(SelfProtection.IsProtected(extractBase));
        Assert.True(SelfProtection.IsProtected(Path.Combine(extractBase, "BE-TFC", "abc123", "coreclr.dll")));
    }

    [Fact]
    public void UnrelatedTempPath_IsNotProtected()
    {
        // Guard against over-reach: protecting all of %TEMP% would gut the tool.
        var ordinary = Path.Combine(Path.GetTempPath(), "some-ordinary-junk.tmp");
        Assert.False(SelfProtection.IsProtected(ordinary));
    }

    [Fact]
    public void TempRootItself_IsNotProtected()
    {
        Assert.False(SelfProtection.IsProtected(Path.GetTempPath()));
    }

    [Fact]
    public void VolumeRoot_IsNeverProtected()
    {
        // A bad normalisation collapsing a root to "C:\" would silently disable
        // every category on that volume, so the builder refuses to add one.
        var root = Path.GetPathRoot(AppContext.BaseDirectory)!;
        Assert.False(SelfProtection.IsProtected(root));
        Assert.DoesNotContain(SelfProtection.ProtectedRoots,
            r => string.Equals(r, root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CommonSystemPaths_AreNotProtected()
    {
        Assert.False(SelfProtection.IsProtected(@"C:\Windows\Temp"));
        Assert.False(SelfProtection.IsProtected(@"C:\Windows\SoftwareDistribution\Download"));
    }

    [Fact]
    public void SimilarlyNamedSibling_IsNotProtected()
    {
        // Prefix matching must respect directory boundaries: "…\publish-old"
        // must not be caught by a root of "…\publish".
        var sibling = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar) + "-sibling";
        Assert.False(SelfProtection.IsProtected(sibling));
    }

    [Fact]
    public void EmptyOrGarbagePath_IsHandled()
    {
        Assert.False(SelfProtection.IsProtected(""));
        Assert.False(SelfProtection.IsProtected("   "));
        Assert.False(SelfProtection.IsProtected("|not|a|path|"));
    }

    /// <summary>
    /// Relative paths must not be resolved against the current working
    /// directory. If they were, the answer would change depending on where the
    /// exe was launched from — and under a test host, whose cwd *is* the
    /// protected output directory, everything relative would look protected.
    /// </summary>
    [Fact]
    public void RelativePath_IsNotResolvedAgainstCwd()
    {
        Assert.False(SelfProtection.IsProtected("some-relative-file.tmp"));
        Assert.False(SelfProtection.IsProtected(@"sub\dir\file.tmp"));
        Assert.False(SelfProtection.IsProtected(@".\file.tmp"));
    }

    [Fact]
    public void ProtectedRoots_ContainNoNestedDuplicates()
    {
        var roots = SelfProtection.ProtectedRoots;
        foreach (var a in roots)
        foreach (var b in roots)
        {
            if (ReferenceEquals(a, b)) continue;
            Assert.False(a.StartsWith(b + Path.DirectorySeparatorChar,
                                      StringComparison.OrdinalIgnoreCase),
                $"'{a}' is redundant — already covered by '{b}'");
        }
    }
}
