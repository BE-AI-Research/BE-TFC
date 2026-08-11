namespace BETFC.Models;

/// <summary>How a target's paths are resolved at scan time.</summary>
public enum TargetScope
{
    /// <summary>A literal machine-wide path (env vars allowed, e.g. %SystemRoot%\Temp).</summary>
    Machine,
    /// <summary>Resolved once per enumerated user profile. Path is relative to the profile root.</summary>
    PerUser,
    /// <summary>Resolved per user, then expanded per Chromium "User Data\*" profile directory.
    /// Path is relative to the profile root; segment "{profile}" is replaced per browser profile.</summary>
    PerUserChromiumProfiles,
    /// <summary>Resolved per user, expanded per Firefox profile dir under Profiles\*.</summary>
    PerUserFirefoxProfiles,
    /// <summary>Special: the Recycle Bin across all drives, handled via shell32
    /// (SHQueryRecycleBin / SHEmptyRecycleBin), not a directory walk.</summary>
    RecycleBin,
}

/// <summary>What to delete within a resolved directory.</summary>
public enum DeleteMode
{
    /// <summary>Delete the directory's *contents* but keep the directory itself.</summary>
    Contents,
    /// <summary>Delete the directory itself, recursively.</summary>
    DirectoryItself,
    /// <summary>Delete files matching a glob directly inside the directory (non-recursive).</summary>
    FilesMatching,
}

public sealed record CleanTarget(
    string RelativeOrAbsolutePath,
    TargetScope Scope,
    DeleteMode Mode = DeleteMode.Contents,
    string? FilePattern = null);

public sealed class CleanCategory
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required bool DefaultChecked { get; init; }
    public bool Dangerous { get; init; }          // extra-confirm categories (e.g. Windows.old)

    /// <summary>
    /// Consent text shown when the user selects this category in the GUI, for
    /// categories that are not <see cref="Dangerous"/> but still do something
    /// the client should agree to — the Recycle Bin being the case in point:
    /// permanently destroying files the client may still want is inherent to
    /// what the bin *is*, not a sign the category is dangerous to the system.
    ///
    /// Deliberately separate from <see cref="Dangerous"/>, which also gates CLI
    /// behaviour (<c>--include-dangerous</c>) and triggers VSS snapshots. A
    /// warning is a consent problem; Dangerous is a classification.
    ///
    /// Null = no prompt. Lives in the catalog so the form never hard-codes IDs.
    /// </summary>
    public string? SelectWarning { get; init; }

    public required IReadOnlyList<CleanTarget> Targets { get; init; }
    public string Group { get; init; } = "General";
}

public sealed class ResolvedLocation
{
    public required string Path { get; init; }
    public required CleanTarget Target { get; init; }
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
}

public sealed class CategoryScanResult
{
    public required CleanCategory Category { get; init; }
    public List<ResolvedLocation> Locations { get; } = new();
    public long TotalBytes => Locations.Sum(l => l.SizeBytes);
    public int TotalFiles => Locations.Sum(l => l.FileCount);
}

public sealed class CleanReport
{
    public long BytesFreed;
    public int FilesDeleted;
    public int FilesScheduledForReboot;
    public int Errors;
    /// <summary>Files skipped because they belong to BE-TFC itself (own exe dir
    /// or single-file bundle extraction dir). Surfaced separately so the count
    /// never masquerades as an error or as freed space.</summary>
    public int SelfProtectedSkips;
    public bool RebootRecommended => FilesScheduledForReboot > 0;
}

public sealed record UserProfile(string Sid, string ProfilePath, string UserName);
