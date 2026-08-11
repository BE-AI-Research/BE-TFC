using System.Diagnostics;
using System.Security.Principal;
using BETFC.Engine;
using BETFC.Models;
using Microsoft.Win32;

// Read-only smoke / verify harness.
//   (no args)  -> Profile enum + full catalog scan + pending quarantine + PFRO + junk tree.
//   --quick    -> Skip the slow full scan. Everything else.
//
// Always safe: no writes, no deletes. Unelevated shows 0 B for machine-scoped
// paths (access denied); run elevated for the full picture.

var quick = args.Any(a => a.Equals("--quick", StringComparison.OrdinalIgnoreCase));

using var identity = WindowsIdentity.GetCurrent();
var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

Console.WriteLine($"BE-TFC smoke harness  (mode: {(quick ? "quick" : "full")})");
Console.WriteLine($"Elevated: {elevated}");
Console.WriteLine(new string('─', 78));

// ─────────────────────────── Profiles ───────────────────────────
var profiles = ProfileEnumerator.GetUserProfiles();
Console.WriteLine($"\nProfiles found: {profiles.Count}");
foreach (var p in profiles)
    Console.WriteLine($"  {p.UserName,-24} {p.Sid,-48} {p.ProfilePath}");

// ─────────────────────────── Full scan ───────────────────────────
if (!quick)
{
    Console.WriteLine("\nScanning full catalog...");
    var sw = Stopwatch.StartNew();
    var scanner = new Scanner();
    var results = await scanner.ScanAsync(CategoryCatalog.All,
        new Progress<string>(_ => { }));
    sw.Stop();

    Console.WriteLine($"Scan complete in {sw.Elapsed.TotalSeconds:F1}s.\n");

    long grandTotal = 0;
    int  grandFiles = 0;
    foreach (var group in results.GroupBy(r => r.Category.Group))
    {
        Console.WriteLine($"── {group.Key}");
        foreach (var r in group)
        {
            var flag = r.Category.Dangerous ? " [DANGER]" :
                       !r.Category.DefaultChecked ? " [off-by-default]" : "";
            Console.WriteLine($"  {r.Category.Name}{flag}");
            Console.WriteLine($"    total: {Human(r.TotalBytes),12}   files: {r.TotalFiles,10:N0}   locations: {r.Locations.Count}");
            foreach (var loc in r.Locations.OrderByDescending(l => l.SizeBytes))
                Console.WriteLine($"      {Human(loc.SizeBytes),12}   {loc.FileCount,8:N0}   {loc.Path}");
            grandTotal += r.TotalBytes;
            grandFiles += r.TotalFiles;
        }
        Console.WriteLine();
    }
    Console.WriteLine(new string('─', 78));
    Console.WriteLine($"Grand total (all categories): {Human(grandTotal)} in {grandFiles:N0} files");
}

// ─────────────────────────── Pending quarantine ───────────────────────────
var pending = QuarantineStore.FindPending();
Console.WriteLine($"\n── Pending quarantine transactions: {pending.Count}");
foreach (var t in pending)
{
    Console.WriteLine($"  txn {t.Journal.TxnId}  entries={t.Journal.Entries.Count}  " +
                      $"unrecoverable={t.Journal.Unrecoverable.Count}  " +
                      $"committed={t.Journal.Committed}");
    Console.WriteLine($"    dir: {t.TxnDir}");
    var betfcEntries = t.Journal.Entries
        .Where(e => e.OriginalPath.Contains("betfc-", StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (betfcEntries.Count > 0)
    {
        Console.WriteLine($"    betfc-* entries ({betfcEntries.Count}):");
        foreach (var e in betfcEntries)
            Console.WriteLine($"      [{e.Category}]  {e.OriginalPath}");
    }
    var betfcUnrec = t.Journal.Unrecoverable
        .Where(u => u.Contains("betfc-", StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (betfcUnrec.Count > 0)
    {
        Console.WriteLine($"    betfc-* unrecoverable ({betfcUnrec.Count}):");
        foreach (var u in betfcUnrec) Console.WriteLine($"      {u}");
    }
}

// ─────────────────────────── PendingFileRenameOperations ───────────────────────────
Console.WriteLine("\n── PendingFileRenameOperations (reboot-delete queue)");
try
{
    using var key = Registry.LocalMachine.OpenSubKey(
        @"SYSTEM\CurrentControlSet\Control\Session Manager", writable: false);
    var raw = key?.GetValue("PendingFileRenameOperations") as string[];
    if (raw is null || raw.Length == 0)
    {
        Console.WriteLine("  (empty)");
    }
    else
    {
        // Format is pairs of strings: [source, dest]. Dest is empty string for a delete.
        // Paths are in NT form: \??\C:\path...
        var all = raw.Where(s => !string.IsNullOrEmpty(s)).ToArray();
        Console.WriteLine($"  {all.Length} entries in queue");
        var betfc = all.Where(s => s.Contains("betfc-", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (betfc.Length > 0)
        {
            Console.WriteLine($"  betfc-* entries ({betfc.Length}):");
            foreach (var s in betfc) Console.WriteLine($"    {s}");
        }
        else
        {
            Console.WriteLine("  no betfc-* entries in queue");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"  (read failed — need elevation? {ex.Message})");
}

// ─────────────────────────── Seeded junk tree ───────────────────────────
var junkRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Temp", "betfc-junk");
Console.WriteLine($"\n── Seeded junk tree: {junkRoot}");
if (!Directory.Exists(junkRoot))
{
    Console.WriteLine("  (not present — run tools/seed-junk.cmd to create)");
}
else
{
    long jbytes = 0; int jcount = 0;
    foreach (var f in Directory.EnumerateFiles(junkRoot, "*", SearchOption.AllDirectories))
    {
        var fi = new FileInfo(f);
        Console.WriteLine($"  {fi.Length,7:N0} B  {f}");
        jbytes += fi.Length; jcount++;
    }
    Console.WriteLine($"  = {jcount} files, {Human(jbytes)}");
}

static string Human(long b) => b switch
{
    >= 1L << 30 => $"{b / (double)(1L << 30):0.##} GB",
    >= 1L << 20 => $"{b / (double)(1L << 20):0.#} MB",
    >= 1L << 10 => $"{b / (double)(1L << 10):0} KB",
    _ => $"{b} B",
};
