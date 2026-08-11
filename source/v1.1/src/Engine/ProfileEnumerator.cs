using BETFC.Models;
using Microsoft.Win32;

namespace BETFC.Engine;

/// <summary>
/// Enumerates real user profiles from HKLM ProfileList rather than guessing
/// from C:\Users\*. This correctly picks up local, domain, and Entra ID
/// (AzureAD) profiles and skips service-account SIDs.
/// </summary>
public static class ProfileEnumerator
{
    private const string ProfileListKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public static List<UserProfile> GetUserProfiles()
    {
        var results = new List<UserProfile>();
        using var key = Registry.LocalMachine.OpenSubKey(ProfileListKey);
        if (key is null) return results;

        foreach (var sid in key.GetSubKeyNames())
        {
            // Real user SIDs start with S-1-5-21 (local/domain) or S-1-12-1 (Entra ID).
            // Skips LocalSystem, LocalService, NetworkService, and other well-known SIDs.
            if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) &&
                !sid.StartsWith("S-1-12-1-", StringComparison.Ordinal))
                continue;

            using var sub = key.OpenSubKey(sid);
            var path = sub?.GetValue("ProfileImagePath") as string;
            if (string.IsNullOrWhiteSpace(path)) continue;

            path = Environment.ExpandEnvironmentVariables(path);
            if (!Directory.Exists(path)) continue;

            // Skip OOBE leftover shells — nothing worth cleaning, occasional weird ACLs.
            var name = Path.GetFileName(path.TrimEnd('\\'));
            if (name.StartsWith("defaultuser", StringComparison.OrdinalIgnoreCase)) continue;

            results.Add(new UserProfile(sid, path, name));
        }

        return results;
    }
}
