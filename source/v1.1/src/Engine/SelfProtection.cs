using System.Diagnostics;

namespace BETFC.Engine;

/// <summary>
/// Paths BE-TFC must never touch: its own executable directory and — critically —
/// the directory a single-file publish extracted its native libraries into.
///
/// Why this exists: <c>PublishSingleFile</c> + <c>IncludeNativeLibrariesForSelfExtract</c>
/// unpacks native runtime files to <c>%TEMP%\.net\BE-TFC\&lt;hash&gt;\</c> at launch.
/// That path lives inside <c>AppData\Local\Temp</c>, which the <c>user-temp</c>
/// category cleans for *every* profile — including the elevated account running
/// this process. Windows permits renaming a loaded image, so a Safe clean would
/// happily quarantine BE-TFC's own runtime mid-sweep and any not-yet-loaded
/// assembly would then fail to load. A Direct clean instead reboot-schedules
/// them, polluting PendingFileRenameOperations for no benefit.
///
/// This is a *narrowing* of deletion authority, never a widening — it only ever
/// removes paths from consideration, so doctrine rule #1 (whitelist only) holds.
/// </summary>
public static class SelfProtection
{
    private static readonly string[] Roots = BuildRoots();

    /// <summary>The directories excluded from sizing and cleaning, for logging.</summary>
    public static IReadOnlyList<string> ProtectedRoots => Roots;

    /// <summary>True if <paramref name="path"/> is, or lives under, a protected root.</summary>
    public static bool IsProtected(string path)
    {
        if (Roots.Length == 0 || string.IsNullOrEmpty(path)) return false;

        var normalized = Normalize(path);
        if (normalized is null) return false;

        foreach (var root in Roots)
        {
            if (normalized.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
            if (normalized.StartsWith(root + Path.DirectorySeparatorChar,
                                      StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string[] BuildRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            if (Normalize(p) is { } n && !IsVolumeRoot(n)) roots.Add(n);
        }

        // The directory the exe itself sits in (USB stick, tech share, wherever).
        Add(AppContext.BaseDirectory);
        if (Environment.ProcessPath is { } exe) Add(Path.GetDirectoryName(exe));

        // The bundle extraction base. Belt on its own — it covers assemblies
        // extracted but not yet loaded.
        var extractBase = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
        extractBase = !string.IsNullOrWhiteSpace(extractBase)
            ? extractBase
            : Path.Combine(Path.GetTempPath(), ".net");
        Add(extractBase);

        // Then the directories we actually have modules loaded from, but ONLY
        // those inside the user's temp tree. Module enumeration exists to find
        // the extraction dir, whose leaf is a content hash we cannot predict —
        // it is not a licence to protect wherever the loader happened to pull a
        // DLL from. Unfiltered, this adds System32 and several WinSxS
        // directories (ntdll, comctl32, gdiplus), which silently narrows
        // deletion scope in trees the catalog never targets, and makes the
        // startup "self-protected" log — a trust feature — read as noise.
        //
        // Temp is the right bound because temp is the only place a cleanup
        // category would ever delete our own runtime out from under us.
        var tempRoot = Normalize(Path.GetTempPath());
        if (tempRoot is not null)
        {
            try
            {
                using var self = Process.GetCurrentProcess();
                foreach (ProcessModule module in self.Modules)
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(module.FileName);
                        // Note: never add tempRoot itself — protecting all of
                        // %TEMP% would disable the largest category in the
                        // catalog. Only directories strictly beneath it.
                        if (dir is not null && IsUnder(dir, tempRoot)) Add(dir);
                    }
                    catch { /* module unloaded mid-enumeration */ }
                }
            }
            catch { /* module enumeration unavailable — the extract base still covers us */ }
        }

        // Drop any root that is contained in another — keeps IsProtected cheap.
        return roots.Where(r => !roots.Any(other =>
                        !ReferenceEquals(other, r) &&
                        r.Length > other.Length &&
                        r.StartsWith(other + Path.DirectorySeparatorChar,
                                     StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
    }

    /// <summary>
    /// Full path, trailing separator stripped. Null if the path is unusable.
    ///
    /// Rooted paths only. Path.GetFullPath resolves a relative path against the
    /// current working directory, which would make protection depend on where
    /// the exe happened to be launched from — a portable tool run off a USB
    /// stick has no stable cwd. Every path the engine passes here comes from
    /// catalog resolution and is already absolute.
    /// </summary>
    private static string? Normalize(string path)
    {
        try
        {
            if (!Path.IsPathRooted(path)) return null;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch { return null; }
    }

    /// <summary>True if <paramref name="candidate"/> lives strictly beneath
    /// <paramref name="root"/>. Equality is deliberately false: the callers use
    /// this to bound module directories by %TEMP%, and %TEMP% itself must never
    /// become a protected root.</summary>
    private static bool IsUnder(string candidate, string root)
    {
        var n = Normalize(candidate);
        return n is not null &&
               n.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Refuse to protect "C:\" — that would disable the whole tool if a
    /// path ever normalized badly.</summary>
    private static bool IsVolumeRoot(string path) =>
        string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path) ?? ""),
                      StringComparison.OrdinalIgnoreCase);
}
