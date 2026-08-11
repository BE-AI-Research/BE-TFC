using BETFC.Cli;
using BETFC.Engine;

namespace BETFC.Tests;

public sealed class CliParserTests
{
    [Fact]
    public void NoArgs_DefaultsToGuiMode()
    {
        var o = CliParser.Parse(Array.Empty<string>());
        Assert.False(o.Silent);
        Assert.False(o.Help);
        Assert.Null(o.ParseError);
        Assert.Equal(CleanMode.Quarantine, o.Mode);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    [InlineData("/?")]
    public void HelpFlags_AllRecognised(string flag)
    {
        var o = CliParser.Parse(new[] { flag });
        Assert.True(o.Help);
    }

    [Fact]
    public void SilentDry_ParsesModeCorrectly()
    {
        var o = CliParser.Parse(new[] { "--silent", "--dry" });
        Assert.True(o.Silent);
        Assert.Equal(CleanMode.Dry, o.Mode);
        Assert.Null(o.ParseError);
    }

    [Fact]
    public void SilentDirect_ParsesModeCorrectly()
    {
        var o = CliParser.Parse(new[] { "--silent", "--direct" });
        Assert.Equal(CleanMode.Direct, o.Mode);
    }

    [Fact]
    public void DryAndDirect_Together_IsRejected()
    {
        var o = CliParser.Parse(new[] { "--silent", "--dry", "--direct" });
        Assert.NotNull(o.ParseError);
        Assert.Contains("mutually exclusive", o.ParseError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Categories_SplitOnComma_TrimAndDropEmpty()
    {
        var o = CliParser.Parse(new[] { "--categories", " wu-cache , user-temp ,, electron-cache" });
        Assert.Equal(new[] { "wu-cache", "user-temp", "electron-cache" }, o.CategoryIds);
    }

    [Fact]
    public void CategoriesMissingValue_IsRejected()
    {
        var o = CliParser.Parse(new[] { "--categories" });
        Assert.NotNull(o.ParseError);
    }

    [Fact]
    public void CommitStale_WithoutNumber_Defaults7()
    {
        var o = CliParser.Parse(new[] { "--silent", "--commit-stale" });
        Assert.Equal(7, o.CommitStaleDays);
    }

    [Fact]
    public void CommitStale_WithNumber_UsesProvidedValue()
    {
        var o = CliParser.Parse(new[] { "--silent", "--commit-stale", "30" });
        Assert.Equal(30, o.CommitStaleDays);
    }

    [Fact]
    public void CommitStale_WithNonNumericFollowing_Defaults7_AndTreatsNextAsFlag()
    {
        var o = CliParser.Parse(new[] { "--silent", "--commit-stale", "--dry" });
        Assert.Equal(7, o.CommitStaleDays);
        Assert.Equal(CleanMode.Dry, o.Mode);
    }

    [Fact]
    public void CommitAllAndRollbackAll_Together_IsRejected()
    {
        var o = CliParser.Parse(new[] { "--silent", "--commit-all", "--rollback-all" });
        Assert.NotNull(o.ParseError);
    }

    [Fact]
    public void UnknownFlag_IsRejected()
    {
        var o = CliParser.Parse(new[] { "--silent", "--gogogo" });
        Assert.NotNull(o.ParseError);
        Assert.Contains("--gogogo", o.ParseError);
    }

    [Fact]
    public void VssFlag_ParsesCorrectly()
    {
        var o = CliParser.Parse(new[] { "--silent", "--include-dangerous", "--vss" });
        Assert.True(o.Vss);
        Assert.True(o.IncludeDangerous);
        Assert.Null(o.ParseError);
    }

    [Fact]
    public void CaseInsensitive_FlagRecognition()
    {
        var o = CliParser.Parse(new[] { "--SILENT", "--Dry" });
        Assert.True(o.Silent);
        Assert.Equal(CleanMode.Dry, o.Mode);
    }
}
