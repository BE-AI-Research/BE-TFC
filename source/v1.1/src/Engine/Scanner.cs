using BETFC.Models;

namespace BETFC.Engine;

public sealed class Scanner
{
    private readonly List<UserProfile> _profiles;

    public Scanner() => _profiles = ProfileEnumerator.GetUserProfiles();

    public IReadOnlyList<UserProfile> Profiles => _profiles;

    /// <summary>Resolve a target into zero or more concrete on-disk locations.</summary>
    public IEnumerable<string> ResolveTarget(CleanTarget target)
    {
        switch (target.Scope)
        {
            case TargetScope.Machine:
                var p = Environment.ExpandEnvironmentVariables(target.RelativeOrAbsolutePath);
                if (Directory.Exists(p)) yield return p;
                break;

            case TargetScope.PerUser:
                foreach (var prof in _profiles)
                {
                    var up = Path.Combine(prof.ProfilePath, target.RelativeOrAbsolutePath);
                    if (Directory.Exists(up)) yield return up;
                }
                break;

            case TargetScope.PerUserChromiumProfiles:
                foreach (var prof in _profiles)
                foreach (var resolved in ExpandChromium(prof.ProfilePath, target.RelativeOrAbsolutePath))
                    yield return resolved;
                break;

            case TargetScope.PerUserFirefoxProfiles:
                foreach (var prof in _profiles)
                foreach (var resolved in ExpandFirefox(prof.ProfilePath, target.RelativeOrAbsolutePath))
                    yield return resolved;
                break;

            case TargetScope.RecycleBin:
                yield return "::RecycleBin"; // pseudo-path; measured/cleaned via shell32
                break;
        }
    }

    private static IEnumerable<string> ExpandChromium(string profileRoot, string template)
    {
        // template: ...\User Data\{profile}\Cache
        var idx = template.IndexOf(@"{profile}", StringComparison.Ordinal);
        if (idx < 0) yield break;

        var userDataDir = Path.Combine(profileRoot, template[..idx].TrimEnd('\\'));
        var suffix = template[(idx + "{profile}".Length)..].TrimStart('\\');
        if (!Directory.Exists(userDataDir)) yield break;

        foreach (var dir in SafeEnumDirs(userDataDir))
        {
            var name = Path.GetFileName(dir);
            var isProfile = name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                         || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)
                         || name.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase);
            if (!isProfile) continue;

            var candidate = Path.Combine(dir, suffix);
            if (Directory.Exists(candidate)) yield return candidate;
        }
    }

    private static IEnumerable<string> ExpandFirefox(string profileRoot, string template)
    {
        var idx = template.IndexOf(@"{profile}", StringComparison.Ordinal);
        if (idx < 0) yield break;

        var profilesDir = Path.Combine(profileRoot, template[..idx].TrimEnd('\\'));
        var suffix = template[(idx + "{profile}".Length)..].TrimStart('\\');
        if (!Directory.Exists(profilesDir)) yield break;

        foreach (var dir in SafeEnumDirs(profilesDir))
        {
            var candidate = Path.Combine(dir, suffix);
            if (Directory.Exists(candidate)) yield return candidate;
        }
    }

    /// <summary>Scan all categories, sizing each resolved location. Parallel across locations.</summary>
    public async Task<List<CategoryScanResult>> ScanAsync(
        IEnumerable<CleanCategory> categories,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<CategoryScanResult>();

        foreach (var cat in categories)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Scanning: {cat.Name}");

            var result = new CategoryScanResult { Category = cat };

            foreach (var target in cat.Targets)
            foreach (var path in ResolveTarget(target))
                result.Locations.Add(new ResolvedLocation { Path = path, Target = target });

            await Parallel.ForEachAsync(result.Locations,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                (loc, token) =>
                {
                    var (bytes, files) = MeasureLocation(loc);
                    loc.SizeBytes = bytes;
                    loc.FileCount = files;
                    return ValueTask.CompletedTask;
                });

            results.Add(result);
        }

        progress?.Report("Scan complete.");
        return results;
    }

    private static (long bytes, int files) MeasureLocation(ResolvedLocation loc)
    {
        long bytes = 0; int files = 0;

        if (loc.Target.Scope == TargetScope.RecycleBin)
        {
            var (rbBytes, rbItems) = RecycleBinInterop.Query();
            return (rbBytes, (int)Math.Min(rbItems, int.MaxValue));
        }

        // Never advertise space we will refuse to free (own exe dir / bundle
        // extraction dir). Sizing has to agree with cleaning or the totals lie.
        if (SelfProtection.IsProtected(loc.Path)) return (0, 0);

        if (loc.Target.Mode == DeleteMode.FilesMatching)
        {
            foreach (var f in SafeEnumFiles(loc.Path, loc.Target.FilePattern ?? "*"))
            {
                bytes += SafeLength(f); files++;
            }
            return (bytes, files);
        }

        // Recursive measure, never following reparse points (junctions/symlinks).
        var stack = new Stack<string>();
        stack.Push(loc.Path);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var f in SafeEnumFiles(dir, "*")) { bytes += SafeLength(f); files++; }
            foreach (var d in SafeEnumDirs(dir))
            {
                if (IsReparsePoint(d)) continue;   // NEVER traverse junctions — safety rule #1
                if (SelfProtection.IsProtected(d)) continue;
                stack.Push(d);
            }
        }
        return (bytes, files);
    }

    internal static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; } // if we can't tell, treat as unsafe
    }

    internal static IEnumerable<string> SafeEnumFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern); }
        catch { return []; }
    }

    internal static IEnumerable<string> SafeEnumDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch { return []; }
    }

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; } catch { return 0; }
    }
}
