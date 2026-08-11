using System.Runtime.InteropServices;
using System.Security.Principal;
using BETFC.Cli;
using BETFC.Engine;
using BETFC.UI;

namespace BETFC;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    private static int Main(string[] args)
    {
        var opts = CliParser.Parse(args);

        // Help / bad args → attach console, print, exit. No UAC needed for these.
        if (opts.Help || opts.ParseError is not null)
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            if (opts.ParseError is not null)
            {
                Console.WriteLine("error: " + opts.ParseError);
                Console.WriteLine();
            }
            Console.WriteLine(CliParser.Usage);
            return opts.ParseError is null ? 0 : 3;
        }

        // Informational flags: pure reads of a compiled-in catalog and assembly
        // metadata, handled before the elevation gate so they cost nothing.
        //
        // They still require a UAC prompt, because the manifest's
        // requireAdministrator is enforced by the OS at process creation — Main
        // never runs unelevated. That is deliberate (doctrine: always elevated).
        // For unelevated inventory, read the PE version resource instead, which
        // needs no launch at all:
        //   (Get-Item BE-TFC.exe).VersionInfo.ProductVersion
        if (opts.Version)
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            Console.WriteLine(AppInfo.Banner);
            Console.WriteLine(AppInfo.ExePath);
            return 0;
        }

        if (opts.ListCategories)
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            PrintCategories();
            return 0;
        }

        // Elevation gate — same rule for GUI and silent mode.
        // Manifest already forces UAC; this catches manifest-stripped launches.
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            if (opts.Silent)
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
                Console.Error.WriteLine("error: BE-TFC requires elevation. Run from an elevated shell.");
                return 3;
            }
            MessageBox.Show(
                "BE-TFC must run elevated to clean all user profiles and system locations.\n" +
                "Right-click → Run as administrator.",
                "Elevation required", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 3;
        }

        if (opts.Silent)
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            return SilentRunner.RunAsync(opts).GetAwaiter().GetResult();
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    private static void PrintCategories()
    {
        Console.WriteLine($"{AppInfo.Banner} — catalog ({CategoryCatalog.All.Count} categories)");
        Console.WriteLine();
        Console.WriteLine("  ID                    DEFAULT  FLAGS      GROUP           NAME");
        foreach (var group in CategoryCatalog.All.GroupBy(c => c.Group))
        foreach (var c in group)
        {
            var flags = c.Dangerous ? "DANGEROUS" : "";
            Console.WriteLine($"  {c.Id,-20}  {(c.DefaultChecked ? "on " : "off"),-7}  " +
                              $"{flags,-9}  {group.Key,-14}  {c.Name}");
        }
        Console.WriteLine();
        Console.WriteLine("Pass ids to --categories, comma separated. DANGEROUS ids additionally");
        Console.WriteLine("require --include-dangerous.");
    }
}
