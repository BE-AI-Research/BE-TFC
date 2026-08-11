using BETFC.Engine;

namespace BETFC.Tests;

/// <summary>
/// BE-TFC carries two independent version numbers: the release version (what
/// the tool is called, the only one a user sees) and the assembly build version
/// (identity, driving dist\ filenames, checksums and the publish assertion).
/// Nothing in the build system ties them together, so these pin the contract.
/// </summary>
public sealed class AppInfoTests
{
    [Fact]
    public void UserFacingVersion_IsTheReleaseVersion_NotTheAssemblyVersion()
    {
        Assert.Equal(AppInfo.ReleaseVersion, AppInfo.Version);
    }

    [Fact]
    public void BuildVersion_IsAvailableSeparately()
    {
        // Automation needs to identify the exact binary even though the human
        // surfaces only show the release version.
        Assert.False(string.IsNullOrWhiteSpace(AppInfo.BuildVersion));
        Assert.Matches(@"^\d+\.\d+\.\d+$", AppInfo.BuildVersion);
    }

    [Fact]
    public void ReleaseVersion_IsSetAndWellFormed()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppInfo.ReleaseVersion));
        Assert.Matches(@"^\d+\.\d+(\.\d+)?$", AppInfo.ReleaseVersion);
    }

    /// <summary>
    /// The banner is the log header and the title bar. It must carry the release
    /// version — a build number there would leak internal numbering to clients.
    /// </summary>
    [Fact]
    public void Banner_ShowsTheReleaseVersion()
    {
        Assert.Contains(AppInfo.ReleaseVersion, AppInfo.Banner);
        Assert.Contains("BE-TFC", AppInfo.Banner);
    }

    [Fact]
    public void Architecture_IsReported()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppInfo.Architecture));
    }
}
