using BETFC.Engine;

namespace BETFC.Cli;

public static class CliParser
{
    public const string Usage = """
        BE-TFC — Temp File Cleaner for Windows 11

        Usage:
          BE-TFC.exe                       Launch GUI (default).
          BE-TFC.exe --silent [flags]      Run without UI, exit when done.

        Silent-mode flags:
          --dry                            Preview only; do not modify anything.
          --direct                         Immediate delete (no quarantine, no undo).
                                           Default: Safe (quarantine + rollback).
          --categories id1,id2,...         Only run these catalog category IDs.
                                           Default: every DefaultChecked=true category.
          --include-dangerous              Also run Dangerous categories (Windows.old etc).
                                           Off by default in silent mode as a safety belt.
          --vss                            Take a VSS snapshot before any Dangerous category
                                           runs, for file-level restore via Previous Versions.
          --no-clean                       Scan and print sizes; do not clean anything.
          --commit-stale [days]            Commit quarantine older than N days (default 7)
                                           before this run. Reclaims client disk.
          --commit-all                     Commit every pending quarantine (any age) first.
          --rollback-all                   Roll back every pending quarantine first.
          --discard-orphans                Delete quarantine folders that have no usable
                                           journal (left by a crash or an older build).
                                           They cannot be rolled back; without this they
                                           are only reported. Reclaims client disk.
          --json                           Emit a machine-readable JSON report on stdout
                                           instead of the human log. For RMM pipelines.
          --log <path>                     Also write the run log to <path>. Directories
                                           are created as needed.

        Informational:
          --version                        Print version and architecture.
          --list-categories                Print every catalog id, with defaults and
                                           dangerous flags. Use ids with --categories.
          -h, --help, /?                   Print this help.

        Note: every invocation triggers UAC — the manifest requires elevation and
        Windows enforces that at process creation, before any flag is read. To
        identify a build without launching it, read the PE version resource:
          (Get-Item BE-TFC.exe).VersionInfo.ProductVersion

        Exit codes:
          0  success
          1  errors during clean
          2  reboot recommended (locked files scheduled for reboot-delete)
          3  invalid usage / bad args
        """;

    public static CliOptions Parse(string[] args)
    {
        bool silent = false, help = false, noClean = false, includeDangerous = false;
        bool commitAll = false, rollbackAll = false, discardOrphans = false;
        bool dry = false, direct = false, vss = false;
        bool json = false, version = false, listCategories = false;
        string? logPath = null;
        int? commitStaleDays = null;
        IReadOnlyList<string> categories = Array.Empty<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "--silent": silent = true; break;
                case "-h": case "--help": case "/?": help = true; break;
                case "--dry":    dry = true; break;
                case "--direct": direct = true; break;
                case "--no-clean": noClean = true; break;
                case "--include-dangerous": includeDangerous = true; break;
                case "--commit-all": commitAll = true; break;
                case "--rollback-all": rollbackAll = true; break;
                case "--discard-orphans": discardOrphans = true; break;
                case "--vss": vss = true; break;
                case "--json": json = true; break;
                case "--version": version = true; break;
                case "--list-categories": listCategories = true; break;
                case "--log":
                    if (i + 1 >= args.Length)
                        return new CliOptions { ParseError = "--log requires a file path" };
                    logPath = args[++i];
                    break;
                case "--commit-stale":
                    // Optional numeric arg — accept 7 as default.
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var days) && days > 0)
                    { commitStaleDays = days; i++; }
                    else
                    { commitStaleDays = 7; }
                    break;
                case "--categories":
                    if (i + 1 >= args.Length)
                        return new CliOptions { ParseError = "--categories requires a comma-separated list" };
                    categories = args[++i]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                default:
                    return new CliOptions { ParseError = $"Unknown argument: {a}" };
            }
        }

        if (dry && direct)
            return new CliOptions { ParseError = "--dry and --direct are mutually exclusive" };
        if (commitAll && rollbackAll)
            return new CliOptions { ParseError = "--commit-all and --rollback-all are mutually exclusive" };

        // --json only shapes silent-mode output; asking for it without --silent is
        // a scripting mistake worth catching loudly rather than launching the GUI.
        if (json && !silent)
            return new CliOptions { ParseError = "--json requires --silent" };

        var mode = dry ? CleanMode.Dry :
                   direct ? CleanMode.Direct :
                            CleanMode.Quarantine;

        return new CliOptions
        {
            Silent = silent,
            Help = help,
            NoClean = noClean,
            IncludeDangerous = includeDangerous,
            Mode = mode,
            CategoryIds = categories,
            CommitStaleDays = commitStaleDays,
            CommitAll = commitAll,
            RollbackAll = rollbackAll,
            DiscardOrphans = discardOrphans,
            Vss = vss,
            Json = json,
            LogPath = logPath,
            Version = version,
            ListCategories = listCategories,
        };
    }
}
