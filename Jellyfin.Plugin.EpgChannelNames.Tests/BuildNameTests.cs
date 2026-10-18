using Xunit;

namespace Jellyfin.Plugin.EpgChannelNames.Tests;

public class BuildNameTests
{
    [Fact]
    public void DefaultTemplate_CombinesPrefixAndProgram()
    {
        var name = ChannelRenamer.BuildName("{prefix} · {program}", "Oilers vs Flames", "NHL GAME 01 Old vs Stale", "101", "NHL GAME 01");
        Assert.Equal("NHL GAME 01 · Oilers vs Flames", name);
    }

    [Fact]
    public void AllTokens_AreReplaced()
    {
        var name = ChannelRenamer.BuildName("{number} | {original} | {prefix} | {program}", "Game", "Orig", "7", "Pre");
        Assert.Equal("7 | Orig | Pre | Game", name);
    }

    [Fact]
    public void Tokens_AreCaseInsensitive()
    {
        Assert.Equal("Game", ChannelRenamer.BuildName("{PROGRAM}", "Game", "Orig", null, "Pre"));
    }

    [Fact]
    public void Prefix_IsTrimmed()
    {
        Assert.Equal("NHL 01", ChannelRenamer.BuildName("{prefix}", null, "Orig", null, " NHL 01 "));
    }

    [Theory]
    [InlineData("{program}", null)]
    [InlineData("   ", "Game")]
    [InlineData("{number}", null)]
    public void EmptyResult_FallsBackToOriginal(string template, string? program)
    {
        Assert.Equal("Orig", ChannelRenamer.BuildName(template, program, "Orig", null, "Pre"));
    }
}
