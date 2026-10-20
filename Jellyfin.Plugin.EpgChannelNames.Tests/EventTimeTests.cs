using System;
using Xunit;

namespace Jellyfin.Plugin.EpgChannelNames.Tests;

public class EventTimeTests
{
    // 9:30 PM Eastern (EDT) on Oct 19, while the real titles below were live.
    private static readonly DateTime Now = new(2026, 10, 20, 1, 30, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("SAN JOSE SHARKS @ TORONTO MAPLE LEAFS OCT 19 – 7:30 PM ET", "2026-10-19T23:30:00Z")]
    [InlineData("PITTSBURGH PENGUINS @ UTAH MAMMOTH OCT 19 – 9:00 PM ET", "2026-10-20T01:00:00Z")]
    [InlineData("DOWN – NEWRY SHAMROCKS @ CLONDUFF OCT 17 – 5:30 PM UK", "2026-10-17T16:30:00Z")]
    [InlineData("ARMAGH – BALLYHEGAN @ COLLEGELAND – JUNIOR FOOTBALL CHAMPIONSHIP FINAL OCT 17 – 3:45 PM UK", "2026-10-17T14:45:00Z")]
    [InlineData("X OCT 20 – 12:00 AM UK", "2026-10-19T23:00:00Z")]
    [InlineData("X OCT 19 – 12:30 PM UK", "2026-10-19T11:30:00Z")]
    [InlineData("X oct 19 - 7:00 pm et", "2026-10-19T23:00:00Z")]
    [InlineData("X OCT 19 – 7:00 PM", "2026-10-19T19:00:00Z")]
    public void ParseStartUtc_RealFormats(string title, string expectedUtc)
    {
        Assert.Equal(DateTime.Parse(expectedUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal), EventTime.ParseStartUtc(title, Now));
    }

    [Theory]
    [InlineData("ANTRIM – NAOMH SEAMUS @ NAOMH EANNA 2026")]
    [InlineData("Major League Baseball")]
    [InlineData("X FEB 30 – 7:00 PM ET")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseStartUtc_NoDate_IsNull(string? title)
    {
        Assert.Null(EventTime.ParseStartUtc(title, Now));
    }

    [Fact]
    public void ParseStartUtc_YearWrap_PicksClosestYear()
    {
        var newYear = new DateTime(2027, 1, 1, 2, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), EventTime.ParseStartUtc("X DEC 31 – 7:00 PM ET", newYear));
        Assert.Equal(new DateTime(2027, 1, 2, 0, 0, 0, DateTimeKind.Utc), EventTime.ParseStartUtc("X JAN 1 – 7:00 PM ET", newYear));
    }

    [Fact]
    public void ParseStartUtc_WinterTime()
    {
        var winter = new DateTime(2026, 12, 10, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 12, 11, 0, 0, 0, DateTimeKind.Utc), EventTime.ParseStartUtc("X DEC 10 – 7:00 PM ET", winter));
        Assert.Equal(new DateTime(2026, 12, 10, 19, 0, 0, DateTimeKind.Utc), EventTime.ParseStartUtc("X DEC 10 – 7:00 PM UK", winter));
    }

    [Theory]
    [InlineData("DOWN – NEWRY SHAMROCKS @ CLONDUFF OCT 17 – 5:30 PM UK", 6, true)]
    [InlineData("SAN JOSE SHARKS @ TORONTO MAPLE LEAFS OCT 19 – 7:30 PM ET", 6, false)]
    [InlineData("PITTSBURGH PENGUINS @ UTAH MAMMOTH OCT 19 – 9:00 PM ET", 6, false)] // starts later tonight
    [InlineData("ANTRIM – NAOMH SEAMUS @ NAOMH EANNA 2026", 6, false)]               // undated: kept
    [InlineData("DOWN – NEWRY SHAMROCKS @ CLONDUFF OCT 17 – 5:30 PM UK", 0, false)] // check off
    [InlineData("X OCT 19 – 7:00 PM UK", 7, true)]                                  // BST: 18:00Z -> 01:30Z = 7.5 h
    [InlineData("X OCT 19 – 7:00 PM UK", 8, false)]
    public void IsStale(string title, int hours, bool expected)
    {
        Assert.Equal(expected, EventTime.IsStale(title, Now, hours));
    }

    [Theory]
    // Blocks of 4 h like the provider's: 19:00-23:00Z (airing at 22:32Z) and 23:00-03:00Z.
    [InlineData("2026-10-20T23:00:00Z", false, true)]  // game at 7 PM ET: the next block, listed before it starts
    [InlineData("2026-10-21T02:30:00Z", false, true)]  // 10:30 PM ET: still in the next block
    [InlineData("2026-10-20T20:00:00Z", true, false)]  // started at 4 PM ET: the airing block
    [InlineData(null, true, false)]                     // undated: airing block only
    public void IsEventBlock_CoversStartOrAiringOnceStarted(string? startUtc, bool airingBlock, bool nextBlock)
    {
        var now = new DateTime(2026, 10, 20, 22, 32, 0, DateTimeKind.Utc);
        var start = startUtc is null ? (DateTime?)null : DateTime.Parse(startUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        var airing = new MediaBrowser.Controller.LiveTv.LiveTvProgram { StartDate = new DateTime(2026, 10, 20, 19, 0, 0, DateTimeKind.Utc), EndDate = new DateTime(2026, 10, 20, 23, 0, 0, DateTimeKind.Utc) };
        var next = new MediaBrowser.Controller.LiveTv.LiveTvProgram { StartDate = new DateTime(2026, 10, 20, 23, 0, 0, DateTimeKind.Utc), EndDate = new DateTime(2026, 10, 21, 3, 0, 0, DateTimeKind.Utc) };

        Assert.Equal(airingBlock, ChannelRenamer.IsEventBlock(airing, start, now));
        Assert.Equal(nextBlock, ChannelRenamer.IsEventBlock(next, start, now));
    }
}
