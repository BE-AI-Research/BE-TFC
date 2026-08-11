using System.Text.Json;
using System.Text.Json.Serialization;

namespace BETFC.Cli;

// ─────────────────────────── JSON run report ───────────────────────────
//
// Machine-readable output for --silent --json, so BE-TFC drops into an RMM or
// a scripted USB run without anyone screen-scraping stdout. Shape is additive
// only: fields may be added in later versions, never renamed or removed.

public sealed class JsonCategoryResult
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Dangerous { get; set; }
    public long Bytes { get; set; }
    public int Files { get; set; }
}

public sealed class JsonVolume
{
    public string Root { get; set; } = "";
    public long FreeBytesBefore { get; set; }
    public long FreeBytesAfter { get; set; }
    public long TotalBytes { get; set; }
    public long FreeBytesDelta => FreeBytesAfter - FreeBytesBefore;
}

public sealed class JsonRunReport
{
    public string Tool { get; set; } = "BE-TFC";

    /// <summary>Public release version, e.g. "1.1" — what the tool is called.</summary>
    public string Version { get; set; } = "";

    /// <summary>
    /// Assembly build version, e.g. "1.4.2". Independent of <see cref="Version"/>
    /// and moves far more often. Present so an RMM pipeline can still pin down
    /// exactly which binary produced a report — the human-facing surfaces
    /// deliberately show only the release version, which would otherwise make
    /// several distinct builds indistinguishable in automation.
    /// </summary>
    public string BuildVersion { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string Machine { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public double DurationSeconds { get; set; }

    /// <summary>"Dry" | "Quarantine" | "Direct".</summary>
    public string Mode { get; set; } = "";
    /// <summary>True when nothing on disk was modified.</summary>
    public bool DryRun { get; set; }
    /// <summary>True when the run stopped after scanning (--no-clean).</summary>
    public bool ScanOnly { get; set; }

    public List<string> Profiles { get; set; } = new();
    public List<JsonCategoryResult> Categories { get; set; } = new();

    public long ScannedBytes { get; set; }
    public int ScannedFiles { get; set; }

    public long BytesCleaned { get; set; }
    public int FilesDeleted { get; set; }
    public int FilesScheduledForReboot { get; set; }
    public int Errors { get; set; }
    public int SelfProtectedSkips { get; set; }
    public bool RebootRecommended { get; set; }

    /// <summary>Quarantine transaction id, when one was created. Space is not
    /// physically freed until this transaction is committed.</summary>
    public string? TransactionId { get; set; }
    /// <summary>True when bytes are sitting in quarantine awaiting a commit.</summary>
    public bool AwaitingCommit { get; set; }

    public List<JsonVolume> Volumes { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public int ExitCode { get; set; }
}

// AOT-safe JSON via source generation, same as the transaction journal.
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(JsonRunReport))]
internal partial class RunReportJsonContext : JsonSerializerContext { }

internal static class RunReportSerializer
{
    public static string ToJson(JsonRunReport report) =>
        JsonSerializer.Serialize(report, RunReportJsonContext.Default.JsonRunReport);
}
