using System.Runtime.InteropServices;

namespace BETFC.Engine;

/// <summary>Build identity. A bench tech carrying several sticks needs to know
/// at a glance which copy of BE-TFC is in front of them.</summary>
public static class AppInfo
{
    /// <summary>
    /// The public release version — the only version number shown to a user, in
    /// the title bar, the log header and <c>--version</c>.
    ///
    /// Deliberately separate from the assembly version. The two numbering
    /// schemes are independent: this is what the tool is *called*, while the
    /// assembly version is build identity used by the publish script's
    /// ProductVersion assertion, the checksum file and the dist\ filenames.
    /// Neither is derivable from the other, so when the release version changes
    /// it is changed here, by hand, on purpose.
    /// </summary>
    public const string ReleaseVersion = "1.1";

    /// <summary>The public release version. See <see cref="ReleaseVersion"/> for
    /// why this is not the assembly version.</summary>
    public static string Version => ReleaseVersion;

    /// <summary>Assembly version — build identity, not shown to users. Kept for
    /// diagnostics and for matching a running process against a dist\ file.</summary>
    public static string BuildVersion { get; } =
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>"x64" / "arm64" — matters now that both are published.</summary>
    public static string Architecture { get; } =
        RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    /// <summary>Path the running exe was launched from, for the log header.
    /// Assembly.Location is deliberately not consulted — it is always empty in a
    /// single-file publish, which is the shipping configuration.</summary>
    public static string ExePath { get; } =
        Environment.ProcessPath ?? AppContext.BaseDirectory;

    public static string Banner => $"BE-TFC {Version} ({Architecture})";
}
