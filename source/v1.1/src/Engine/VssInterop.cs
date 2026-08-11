using System.Diagnostics;

namespace BETFC.Engine;

/// <summary>
/// Volume Shadow Copy Service interop. Creates a point-in-time snapshot of a
/// volume BEFORE a destructive clean, so the user can restore individual files
/// via Windows' built-in "Previous Versions" tab or vssadmin.
///
/// Implementation shells out to PowerShell's WMI wrapper — avoids adding a
/// reflection-heavy System.Management NuGet and keeps the AOT publish story
/// simple. vssadmin is not used because it is deprecated on Windows 11 client.
/// </summary>
public static class VssInterop
{
    public sealed record CreateResult(bool Ok, string? ShadowId, string? Message);

    /// <summary>Absolute path to the in-box PowerShell. Never resolved through
    /// PATH: BE-TFC runs elevated on machines that are frequently already
    /// compromised, and a planted powershell.exe on PATH would inherit that
    /// elevation.</summary>
    private static string PowerShellPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>Create a client-accessible shadow copy of the given volume.
    /// <paramref name="volumeRoot"/> must end in a backslash, e.g. "C:\\".</summary>
    public static CreateResult CreateSnapshot(string volumeRoot)
    {
        // WMI Create returns { ReturnValue: 0=success, ShadowID: "{guid}" }.
        // Any non-zero return is a documented VSS error code — surface it.
        var escaped = volumeRoot.Replace("\\", "\\\\").Replace("'", "''");
        var script =
            "$ErrorActionPreference = 'Stop'; " +
            "$c = [wmiclass]'root\\cimv2:Win32_ShadowCopy'; " +
            $"$r = $c.Create('{escaped}', 'ClientAccessible'); " +
            "if ($r.ReturnValue -ne 0) { " +
                "Write-Error \"VSS Create returned $($r.ReturnValue)\"; exit 1 " +
            "} " +
            "Write-Output $r.ShadowID";

        try
        {
            if (!File.Exists(PowerShellPath))
                return new CreateResult(false, null, $"PowerShell not found at {PowerShellPath}");

            var psi = new ProcessStartInfo(PowerShellPath,
                $"-NoProfile -NonInteractive -Command \"{script}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var p = Process.Start(psi);
            if (p is null)
                return new CreateResult(false, null, "PowerShell failed to start");

            // Drain both pipes concurrently. Reading stdout to end *then* stderr
            // deadlocks if stderr fills its 64K buffer while we're still blocked
            // on stdout — and a failed VSS Create is exactly when stderr is loud.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new CreateResult(false, null, "VSS snapshot timed out after 30s");
            }
            p.WaitForExit();   // ensures the redirected streams are fully flushed

            var stdout = stdoutTask.GetAwaiter().GetResult().Trim();
            var stderr = stderrTask.GetAwaiter().GetResult().Trim();

            if (p.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                return new CreateResult(true, stdout, null);

            var msg = string.IsNullOrWhiteSpace(stderr)
                ? $"unknown VSS failure (exit {p.ExitCode})"
                : stderr;
            return new CreateResult(false, null, msg);
        }
        catch (Exception ex)
        {
            return new CreateResult(false, null, ex.Message);
        }
    }
}
