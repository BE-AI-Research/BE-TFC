namespace BETFC.Engine;

/// <summary>
/// Free-space sampling. "How much did you actually get back?" is the number the
/// client asks about and the one that goes in the ticket, so it is measured from
/// the volume itself rather than inferred from the sum of deleted file sizes.
/// </summary>
public static class DiskSpace
{
    public sealed record VolumeFree(string Root, long FreeBytes, long TotalBytes);

    /// <summary>Free space on every ready fixed volume, right now.</summary>
    public static List<VolumeFree> Snapshot()
    {
        var list = new List<VolumeFree>();
        foreach (var d in SafeDrives())
        {
            try { list.Add(new VolumeFree(d.RootDirectory.FullName, d.AvailableFreeSpace, d.TotalSize)); }
            catch { /* drive vanished between enumeration and query */ }
        }
        return list;
    }

    /// <summary>System volume free bytes, or 0 if it can't be read.</summary>
    public static long SystemVolumeFree()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrEmpty(root)) return 0;
        try { return new DriveInfo(root).AvailableFreeSpace; } catch { return 0; }
    }

    /// <summary>
    /// One-line human summary of what changed, e.g.
    /// <c>C: 42.1 GB → 51.7 GB free (+9.6 GB)</c>. Volumes that didn't move are
    /// omitted. Returns null when nothing measurably changed — a Safe clean
    /// frees nothing until Commit, and saying "+0 B" there reads as a failure.
    /// </summary>
    public static string? Describe(List<VolumeFree> before, List<VolumeFree> after)
    {
        var byRoot = before.ToDictionary(v => v.Root, StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();

        foreach (var now in after)
        {
            if (!byRoot.TryGetValue(now.Root, out var was)) continue;
            var delta = now.FreeBytes - was.FreeBytes;
            // Ignore sub-MB drift: a live machine writes to disk while we work.
            if (Math.Abs(delta) < 1L << 20) continue;

            var sign = delta > 0 ? "+" : "−";
            parts.Add($"{now.Root.TrimEnd('\\')} {Format.Bytes(was.FreeBytes)} → " +
                      $"{Format.Bytes(now.FreeBytes)} free ({sign}{Format.Bytes(Math.Abs(delta))})");
        }

        return parts.Count == 0 ? null : string.Join("   |   ", parts);
    }

    private static IEnumerable<DriveInfo> SafeDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); } catch { return []; }
        return drives.Where(d =>
        {
            try { return d.DriveType == DriveType.Fixed && d.IsReady; } catch { return false; }
        });
    }
}
