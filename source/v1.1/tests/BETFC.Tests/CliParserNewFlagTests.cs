using BETFC.Cli;

namespace BETFC.Tests;

/// <summary>Coverage for the v1.3 portability flags.</summary>
public sealed class CliParserNewFlagTests
{
    [Fact]
    public void Json_RequiresSilent()
    {
        var o = CliParser.Parse(new[] { "--json" });
        Assert.NotNull(o.ParseError);
        Assert.Contains("--silent", o.ParseError);
    }

    [Fact]
    public void Json_WithSilent_IsAccepted()
    {
        var o = CliParser.Parse(new[] { "--silent", "--json" });
        Assert.Null(o.ParseError);
        Assert.True(o.Json);
        Assert.True(o.Silent);
    }

    [Fact]
    public void Log_CapturesPath()
    {
        var o = CliParser.Parse(new[] { "--silent", "--log", @"C:\temp\run.log" });
        Assert.Null(o.ParseError);
        Assert.Equal(@"C:\temp\run.log", o.LogPath);
    }

    [Fact]
    public void Log_WithoutPath_IsRejected()
    {
        var o = CliParser.Parse(new[] { "--silent", "--log" });
        Assert.NotNull(o.ParseError);
        Assert.Contains("--log", o.ParseError);
    }

    /// <summary>A path is consumed as --log's argument, not re-parsed as a flag.</summary>
    [Fact]
    public void Log_PathIsNotTreatedAsFlag()
    {
        var o = CliParser.Parse(new[] { "--silent", "--log", @"C:\logs\x.log", "--dry" });
        Assert.Null(o.ParseError);
        Assert.Equal(@"C:\logs\x.log", o.LogPath);
        Assert.Equal(BETFC.Engine.CleanMode.Dry, o.Mode);
    }

    [Fact]
    public void Version_IsParsed()
    {
        var o = CliParser.Parse(new[] { "--version" });
        Assert.True(o.Version);
        Assert.Null(o.ParseError);
    }

    [Fact]
    public void ListCategories_IsParsed()
    {
        var o = CliParser.Parse(new[] { "--list-categories" });
        Assert.True(o.ListCategories);
        Assert.Null(o.ParseError);
    }

    [Fact]
    public void UnknownFlag_StillRejected()
    {
        var o = CliParser.Parse(new[] { "--silent", "--jsonn" });
        Assert.NotNull(o.ParseError);
    }

    [Fact]
    public void FullRmmInvocation_ParsesEndToEnd()
    {
        var o = CliParser.Parse(new[]
        {
            "--silent", "--direct", "--json", "--log", @"C:\ProgramData\rmm\betfc.log",
            "--categories", "win-temp,user-temp", "--commit-stale", "3",
        });

        Assert.Null(o.ParseError);
        Assert.True(o.Silent);
        Assert.True(o.Json);
        Assert.Equal(BETFC.Engine.CleanMode.Direct, o.Mode);
        Assert.Equal(@"C:\ProgramData\rmm\betfc.log", o.LogPath);
        Assert.Equal(new[] { "win-temp", "user-temp" }, o.CategoryIds);
        Assert.Equal(3, o.CommitStaleDays);
    }
}
