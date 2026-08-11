using BETFC.Models;

namespace BETFC.Engine;

/// <summary>
/// The complete whitelist of cleanable locations. BE-TFC NEVER deletes
/// anything outside this catalog — no heuristics, no "old file" scanning.
/// This is what made original TFC trustworthy on client machines.
/// </summary>
public static class CategoryCatalog
{
    public static IReadOnlyList<CleanCategory> All { get; } = Build();

    private static List<CleanCategory> Build() => new()
    {
        // ─────────────────────────── Windows system ───────────────────────────
        new CleanCategory
        {
            Id = "win-temp", Group = "Windows",
            Name = "Windows Temp",
            Description = @"%SystemRoot%\Temp — system-wide temp files",
            DefaultChecked = true,
            Targets = [ new(@"%SystemRoot%\Temp", TargetScope.Machine) ],
        },
        new CleanCategory
        {
            Id = "wu-cache", Group = "Windows",
            Name = "Windows Update download cache",
            Description = @"SoftwareDistribution\Download — safe to clear; WU re-downloads as needed",
            DefaultChecked = true,
            Targets = [ new(@"%SystemRoot%\SoftwareDistribution\Download", TargetScope.Machine) ],
        },
        new CleanCategory
        {
            Id = "delivery-opt", Group = "Windows",
            Name = "Delivery Optimization cache",
            Description = "Peer-to-peer update cache under ProgramData",
            DefaultChecked = true,
            Targets = [ new(@"%ProgramData%\Microsoft\Windows\DeliveryOptimization\Cache", TargetScope.Machine) ],
        },
        new CleanCategory
        {
            Id = "wer", Group = "Windows",
            Name = "Windows Error Reporting queue",
            Description = "Crash report queue and archive (ProgramData WER)",
            DefaultChecked = true,
            Targets =
            [
                new(@"%ProgramData%\Microsoft\Windows\WER\ReportQueue", TargetScope.Machine),
                new(@"%ProgramData%\Microsoft\Windows\WER\ReportArchive", TargetScope.Machine),
                new(@"%ProgramData%\Microsoft\Windows\WER\Temp", TargetScope.Machine),
            ],
        },
        new CleanCategory
        {
            Id = "win-old", Group = "Windows",
            Name = "Windows.old / upgrade leftovers",
            Description = "Previous Windows installation + $WINDOWS.~BT. Removes rollback ability!",
            DefaultChecked = false, Dangerous = true,
            Targets =
            [
                new(@"%SystemDrive%\Windows.old", TargetScope.Machine, DeleteMode.DirectoryItself),
                new(@"%SystemDrive%\$WINDOWS.~BT", TargetScope.Machine, DeleteMode.DirectoryItself),
                new(@"%SystemDrive%\$WINDOWS.~WS", TargetScope.Machine, DeleteMode.DirectoryItself),
            ],
        },
        new CleanCategory
        {
            Id = "prefetch", Group = "Windows",
            Name = "Prefetch",
            Description = "Off by default — clearing it slows first launches; only useful for corruption",
            DefaultChecked = false,
            Targets = [ new(@"%SystemRoot%\Prefetch", TargetScope.Machine, DeleteMode.FilesMatching, "*.pf") ],
        },

        // ─────────────────────────── Per-user core ───────────────────────────
        new CleanCategory
        {
            Id = "user-temp", Group = "User profiles",
            Name = "User Temp folders (all profiles)",
            Description = @"AppData\Local\Temp for every user profile on the machine",
            DefaultChecked = true,
            Targets = [ new(@"AppData\Local\Temp", TargetScope.PerUser) ],
        },
        new CleanCategory
        {
            Id = "inetcache", Group = "User profiles",
            Name = "INetCache / IE-WebView cache",
            Description = "Legacy WinINET cache still used by WebView and Office",
            DefaultChecked = true,
            Targets = [ new(@"AppData\Local\Microsoft\Windows\INetCache", TargetScope.PerUser) ],
        },
        new CleanCategory
        {
            Id = "thumbcache", Group = "User profiles",
            Name = "Thumbnail & icon caches",
            Description = "Explorer thumbcache/iconcache DBs — rebuilt automatically",
            DefaultChecked = true,
            Targets =
            [
                new(@"AppData\Local\Microsoft\Windows\Explorer", TargetScope.PerUser,
                    DeleteMode.FilesMatching, "thumbcache_*.db"),
                new(@"AppData\Local\Microsoft\Windows\Explorer", TargetScope.PerUser,
                    DeleteMode.FilesMatching, "iconcache_*.db"),
            ],
        },
        new CleanCategory
        {
            Id = "crashdumps", Group = "User profiles",
            Name = "User crash dumps",
            Description = @"AppData\Local\CrashDumps per profile",
            DefaultChecked = true,
            Targets = [ new(@"AppData\Local\CrashDumps", TargetScope.PerUser) ],
        },
        new CleanCategory
        {
            Id = "java-cache", Group = "User profiles",
            Name = "Java deployment cache",
            Description = "Legacy Java Web Start cache — the original TFC classic",
            DefaultChecked = true,
            Targets = [ new(@"AppData\LocalLow\Sun\Java\Deployment\cache", TargetScope.PerUser) ],
        },

        // ─────────────────────────── Browsers ───────────────────────────
        new CleanCategory
        {
            Id = "chromium-cache", Group = "Browsers",
            Name = "Chromium browsers cache (Chrome/Edge/Brave/Vivaldi/Opera)",
            Description = "Cache, Code Cache, GPUCache for every profile of every Chromium browser. Does NOT touch cookies, passwords, or history.",
            DefaultChecked = true,
            Targets =
            [
                // {profile} expands to each profile dir (Default, Profile 1, ...)
                new(@"AppData\Local\Google\Chrome\User Data\{profile}\Cache", TargetScope.PerUserChromiumProfiles),
                new(@"AppData\Local\Google\Chrome\User Data\{profile}\Code Cache", TargetScope.PerUserChromiumProfiles),
                new(@"AppData\Local\Google\Chrome\User Data\{profile}\GPUCache", TargetScope.PerUserChromiumProfiles),
                new(@"AppData\Local\Microsoft\Edge\User Data\{profile}\Cache", TargetScope.PerUserChromiumProfiles),
                new(@"AppData\Local\Microsoft\Edge\User Data\{profile}\Code Cache", TargetScope.PerUserChromiumProfiles),
                new(@"AppData\Local\Microsoft\Edge\User Data\{profile}\GPUCache", TargetScope.PerUserChromiumProfiles),
                new(@"AppData\Local\BraveSoftware\Brave-Browser\User Data\{profile}\Cache", TargetScope.PerUserChromiumProfiles),
                new(@"AppData\Local\BraveSoftware\Brave-Browser\User Data\{profile}\Code Cache", TargetScope.PerUserChromiumProfiles),
                new(@"AppData\Local\Vivaldi\User Data\{profile}\Cache", TargetScope.PerUserChromiumProfiles),
                new(@"AppData\Roaming\Opera Software\Opera Stable\{profile}\Cache", TargetScope.PerUserChromiumProfiles),
            ],
        },
        new CleanCategory
        {
            Id = "firefox-cache", Group = "Browsers",
            Name = "Firefox cache",
            Description = "cache2 for every Firefox profile. Cookies/logins untouched.",
            DefaultChecked = true,
            Targets = [ new(@"AppData\Local\Mozilla\Firefox\Profiles\{profile}\cache2", TargetScope.PerUserFirefoxProfiles) ],
        },

        // ─────────────────────────── App caches ───────────────────────────
        new CleanCategory
        {
            Id = "electron-cache", Group = "App caches",
            Name = "Electron app caches (Teams/Discord/Slack/Spotify)",
            Description = "Often the biggest space hogs on modern machines. Users stay logged in.",
            DefaultChecked = true,
            Targets =
            [
                new(@"AppData\Roaming\discord\Cache", TargetScope.PerUser, DeleteMode.DirectoryItself),
                new(@"AppData\Roaming\discord\Code Cache", TargetScope.PerUser, DeleteMode.DirectoryItself),
                new(@"AppData\Roaming\Slack\Cache", TargetScope.PerUser, DeleteMode.DirectoryItself),
                new(@"AppData\Roaming\Slack\Service Worker\CacheStorage", TargetScope.PerUser, DeleteMode.DirectoryItself),
                new(@"AppData\Local\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\WV2Profile_tfw\Cache", TargetScope.PerUser, DeleteMode.DirectoryItself),
                new(@"AppData\Local\Spotify\Data", TargetScope.PerUser),
            ],
        },
        new CleanCategory
        {
            Id = "shader-cache", Group = "App caches",
            Name = "GPU shader caches (NVIDIA/AMD/DirectX)",
            Description = "Rebuilt on demand; clears driver-update stutter artifacts",
            DefaultChecked = true,
            Targets =
            [
                new(@"AppData\Local\NVIDIA\DXCache", TargetScope.PerUser),
                new(@"AppData\Local\NVIDIA\GLCache", TargetScope.PerUser),
                new(@"AppData\Local\AMD\DxCache", TargetScope.PerUser),
                new(@"AppData\Local\AMD\DxcCache", TargetScope.PerUser),
                new(@"AppData\Local\D3DSCache", TargetScope.PerUser),
            ],
        },
        new CleanCategory
        {
            Id = "recycle-bin", Group = "Windows",
            Name = "Recycle Bin (all drives)",
            Description = "Empties the Recycle Bin via shell32. Off by default — client may want deleted files back.",
            DefaultChecked = false,
            // Not Dangerous: emptying the bin threatens nothing about the system,
            // and flagging it would change --include-dangerous semantics for
            // existing scripts. But it is the one category whose whole purpose is
            // irreversible destruction of files the user chose to keep around, so
            // it asks before it is armed.
            SelectWarning =
                "Emptying the Recycle Bin permanently deletes everything in it.\n\n" +
                "This cannot be undone — not even by BE-TFC's rollback. The bin is " +
                "emptied through the shell, so its contents are never quarantined.\n\n" +
                "The client may still want files sitting in there. Ask first.",
            Targets = [ new("::RecycleBin", TargetScope.RecycleBin) ],
        },
        new CleanCategory
        {
            Id = "winget-cache", Group = "App caches",
            Name = "WinGet installer cache",
            Description = "Downloaded installer packages",
            DefaultChecked = true,
            Targets = [ new(@"AppData\Local\Temp\WinGet", TargetScope.PerUser, DeleteMode.DirectoryItself) ],
        },
    };
}
