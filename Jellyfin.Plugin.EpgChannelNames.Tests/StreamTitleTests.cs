using System.Text.RegularExpressions;
using Jellyfin.Plugin.EpgChannelNames.Configuration;
using Xunit;

namespace Jellyfin.Plugin.EpgChannelNames.Tests;

public class StreamTitleTests
{
    private static readonly Regex DefaultRegex = new(new PluginConfiguration().StreamTitleRegex, RegexOptions.IgnoreCase);

    // Real stream names from a Dispatcharr / provider M3U.
    [Theory]
    [InlineData("CA - NHL GAME 01 : ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19 – 7:00 PM ET / OCT 20 – 12:00 AM UK", "ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19 – 7:00 PM ET")]
    [InlineData("CA - NHL GAME 11 : CALGARY FLAMES @ VEGAS GOLDEN KNIGHTS OCT 15 – 10:00 PM ET", "CALGARY FLAMES @ VEGAS GOLDEN KNIGHTS OCT 15 – 10:00 PM ET")]
    [InlineData("US - NHL GAME 02 : CAROLINA HURRICANES @ CALGARY FLAMES OCT 18 – 8:00 PM ET / 1:00 AM UK", "CAROLINA HURRICANES @ CALGARY FLAMES OCT 18 – 8:00 PM ET")]
    public void DefaultRegex_ExtractsMatchup(string streamName, string expected)
    {
        Assert.Equal(expected, ChannelRenamer.ExtractStreamTitle(streamName, DefaultRegex));
    }

    [Theory]
    [InlineData("CA - NHL GAME 05 : ")]
    [InlineData("CA - NHL GAME 05 :")]
    [InlineData("CA - NHL GAME 05")]
    [InlineData("")]
    [InlineData(null)]
    public void NoGame_ReturnsNull(string? streamName)
    {
        Assert.Null(ChannelRenamer.ExtractStreamTitle(streamName, DefaultRegex));
    }

    [Fact]
    public void RegexWithoutTitleGroup_UsesWholeMatch()
    {
        Assert.Equal("DUCKS @ RANGERS", ChannelRenamer.ExtractStreamTitle("X : DUCKS @ RANGERS", new Regex(@"[A-Z]+ @ [A-Z]+")));
    }
}
