using BETFC.Engine;

namespace BETFC.Cli;

/// <summary>Parsed command-line invocation. Immutable.</summary>
public sealed record CliOptions
{
    public bool Silent { get; init; }
    public bool Help { get; init; }
    public bool NoClean { get; init; }
    public bool IncludeDangerous { get; init; }

    /// <summary>Which mode to run in when Silent. Ignored when Silent=false.</summary>
    public CleanMode Mode { get; init; } = CleanMode.Quarantine;

    /// <summary>Empty = "use every category with DefaultChecked=true".</summary>
    public IReadOnlyList<string> CategoryIds { get; init; } = Array.Empty<string>();

    /// <summary>Commit any pending quarantine older than this many days before
    /// scanning. Null = don't touch pending quarantines.</summary>
    public int? CommitStaleDays { get; init; }

    /// <summary>Commit every pending quarantine before scanning, regardless of age.</summary>
    public bool CommitAll { get; init; }

    /// <summary>Roll back every pending quarantine before scanning.</summary>
    public bool RollbackAll { get; init; }

    /// <summary>Delete quarantine folders that have no usable journal. They
    /// cannot be rolled back, so discarding is the only disposition — hence a
    /// separate opt-in rather than being folded into --commit-all.</summary>
    public bool DiscardOrphans { get; init; }

    /// <summary>Take a VSS snapshot before any Dangerous category runs.
    /// Opt-in in silent mode (consumes disk + snapshot slots); auto in GUI.</summary>
    public bool Vss { get; init; }

    /// <summary>Emit a machine-readable JSON run report on stdout instead of the
    /// human log, for RMM/scripted deployment.</summary>
    public bool Json { get; init; }

    /// <summary>Tee the human-readable run log to this file. Null = stdout only.</summary>
    public string? LogPath { get; init; }

    /// <summary>Print version and exit. No elevation required.</summary>
    public bool Version { get; init; }

    /// <summary>Print the catalog (ids, names, defaults) and exit. No elevation required.</summary>
    public bool ListCategories { get; init; }

    /// <summary>Populated by the parser if arguments were invalid.</summary>
    public string? ParseError { get; init; }
}
