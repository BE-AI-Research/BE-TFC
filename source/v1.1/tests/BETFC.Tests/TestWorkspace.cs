using BETFC.Engine;

namespace BETFC.Tests;

/// <summary>
/// Per-test scratch dir under %TEMP%. Holds an "originals" tree (source files
/// to be quarantined) and a "quarantine" root injected via <see cref="Layout"/>
/// so runs never touch a real <c>&lt;drive&gt;\BE-TFC.Quarantine</c>.
/// </summary>
internal sealed class TestWorkspace : IDisposable
{
    public string Root { get; }
    public string OriginalsDir { get; }
    public string QuarantineRoot { get; }
    public IQuarantineLayout Layout { get; }
    public List<string> LogLines { get; } = new();

    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(),
            "BETFC.Tests-" + Guid.NewGuid().ToString("N")[..12]);
        OriginalsDir = Path.Combine(Root, "originals");
        QuarantineRoot = Path.Combine(Root, "quarantine");
        Directory.CreateDirectory(OriginalsDir);
        Directory.CreateDirectory(QuarantineRoot);
        Layout = new FixedRootLayout(QuarantineRoot);
    }

    public void Log(string line) => LogLines.Add(line);

    /// <summary>Create a file with the given content under OriginalsDir. Nested paths OK.</summary>
    public string CreateFile(string relativePath, string content = "hello", FileAttributes? attrs = null)
    {
        var full = Path.Combine(OriginalsDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (attrs is { } a) File.SetAttributes(full, a);
        return full;
    }

    public void Dispose()
    {
        try
        {
            // Strip attributes so read-only files don't block deletion.
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(Root, recursive: true);
        }
        catch { /* best-effort — %TEMP% cleanup will get it */ }
    }
}

/// <summary>Test-only layout: single fixed root, source files must share its volume.</summary>
internal sealed class FixedRootLayout : IQuarantineLayout
{
    private readonly string _root;
    public FixedRootLayout(string root) => _root = root;
    public string QuarantineRootFor(string filePath) => _root;
    public IEnumerable<string> RootsToScan() { yield return _root; }

    /// <summary>Opted out: the harness runs unelevated under %TEMP%, and applying
    /// the production Administrators/SYSTEM-only DACL would lock the test process
    /// out of its own scratch directory.</summary>
    public bool RestrictAccess => false;
}
