using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.EpgChannelNames.Configuration;
using MediaBrowser.Controller.LiveTv;
using Xunit;

namespace Jellyfin.Plugin.EpgChannelNames.Tests;

public class EventAndCategoryTests
{
    private static readonly Regex DefaultEventRegex = new(new PluginConfiguration().EventRegex, RegexOptions.IgnoreCase);

    // Real stream names from the provider.
    [Theory]
    [InlineData("CA - NHL GAME 01 : ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19 – 7:00 PM ET / OCT 20 – 12:00 AM UK", "CA - NHL GAME 01", "ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19 – 7:00 PM ET")]
    [InlineData("US - NHL GAME 02 : CAROLINA HURRICANES @ CALGARY FLAMES OCT 18 – 8:00 PM ET / 1:00 AM UK", "US - NHL GAME 02", "CAROLINA HURRICANES @ CALGARY FLAMES OCT 18 – 8:00 PM ET")]
    [InlineData("UK - GAATV 004 : DOWN – NEWRY SHAMROCKS @ CLONDUFF OCT 17", "UK - GAATV 004", "DOWN – NEWRY SHAMROCKS @ CLONDUFF OCT 17")]
    [InlineData("UK - TNT SPORTS | EVENT 2 HD : Major League Baseball", "UK - TNT SPORTS | EVENT 2 HD", "Major League Baseball")]
    public void ParseEvent_WithEvent(string stream, string prefix, string title)
    {
        var ev = ChannelRenamer.ParseEvent(stream, DefaultEventRegex);

        Assert.NotNull(ev);
        Assert.Equal(prefix, ev!.Value.Prefix);
        Assert.Equal(title, ev.Value.Title);
    }

    [Theory]
    [InlineData("CA - NHL GAME 05 : ", "CA - NHL GAME 05")]
    [InlineData("UK - TNT SPORTS | EVENT 9 HD :", "UK - TNT SPORTS | EVENT 9 HD")]
    [InlineData("UK - GAATV 007 : ", "UK - GAATV 007")]
    public void ParseEvent_NothingOnNow(string stream, string prefix)
    {
        var ev = ChannelRenamer.ParseEvent(stream, DefaultEventRegex);

        Assert.NotNull(ev);
        Assert.Equal(prefix, ev!.Value.Prefix);
        Assert.Null(ev.Value.Title);
    }

    [Theory]
    [InlineData("CA - HISTORY HD ◉")]
    [InlineData("UK - SKY SPORTS+ HD")]
    [InlineData("##### NHL PPV #####")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseEvent_RegularChannel_IsNull(string? stream)
    {
        Assert.Null(ChannelRenamer.ParseEvent(stream, DefaultEventRegex));
    }

    [Fact]
    public void Rules_Parse_GroupsWithPipesAndNbsp()
    {
        var rules = CategoryRules.Parse("AM | CA | NHL PPV = Sports\n# comment\n\n  am | ca | documentary   =  shows  \r\n");

        Assert.Equal(ProgramCategory.Sports, rules.For("AM | CA | NHL PPV"));
        Assert.Equal(ProgramCategory.Shows, rules.For("AM | CA | DOCUMENTARY"));
        Assert.Null(rules.For("EU | UK | SPORTS"));
        Assert.Null(rules.For(null));
        Assert.Empty(rules.InvalidLines);
    }

    [Theory]
    [InlineData("no equals sign")]
    [InlineData("= Shows")]
    [InlineData("GROUP = Documentaries")]
    [InlineData("GROUP = 7")]
    public void Rules_Parse_BadLines(string line)
    {
        var rules = CategoryRules.Parse(line);

        Assert.Equal(new[] { line }, rules.InvalidLines);
    }

    [Theory]
    [InlineData(ProgramCategory.Shows)]
    [InlineData(ProgramCategory.Movies)]
    [InlineData(ProgramCategory.Sports)]
    [InlineData(ProgramCategory.News)]
    [InlineData(ProgramCategory.Kids)]
    public void Apply_SetsTheRowFlag_ThenUndoRestores(ProgramCategory category)
    {
        var program = new LiveTvProgram { Tags = new[] { "HD" } };

        Assert.True(CategoryRules.Apply(program, category));
        Assert.Equal(category == ProgramCategory.Shows, program.IsSeries);
        Assert.Equal(category == ProgramCategory.Movies, program.IsMovie);
        Assert.Equal(category == ProgramCategory.Sports, program.IsSports);
        Assert.Equal(category == ProgramCategory.News, program.IsNews);
        Assert.Equal(category == ProgramCategory.Kids, program.IsKids);

        Assert.True(CategoryRules.Apply(program, null));
        Assert.False(program.IsSeries || program.IsMovie || program.IsSports || program.IsNews || program.IsKids);
        Assert.Equal(new[] { "HD" }, program.Tags);
        Assert.False(program.ProviderIds.ContainsKey(CategoryRules.MarkerKey));
    }

    [Fact]
    public void Apply_SameCategoryTwice_SecondIsNoChange()
    {
        var program = new LiveTvProgram();
        CategoryRules.Apply(program, ProgramCategory.Shows);

        Assert.False(CategoryRules.Apply(program, ProgramCategory.Shows));
    }

    [Fact]
    public void Apply_ChangeCategory_UndoesTheOldOne()
    {
        var program = new LiveTvProgram();
        CategoryRules.Apply(program, ProgramCategory.Shows);

        CategoryRules.Apply(program, ProgramCategory.News);

        Assert.False(program.IsSeries);
        Assert.True(program.IsNews);
    }

    [Fact]
    public void Apply_FlagFromEpg_IsNeverUndone()
    {
        // The EPG already marked this a series; the plugin must not take credit for it.
        var program = new LiveTvProgram { IsSeries = true, Tags = new[] { "Sports" } };

        CategoryRules.Apply(program, ProgramCategory.Shows);
        CategoryRules.Apply(program, ProgramCategory.Sports);
        CategoryRules.Apply(program, null);

        Assert.True(program.IsSeries);
        Assert.True(program.IsSports);
    }

    [Fact]
    public void Apply_GuideRefreshResetFlag_IsReapplied()
    {
        var program = new LiveTvProgram();
        CategoryRules.Apply(program, ProgramCategory.Shows);
        program.IsSeries = false; // guide refresh rewrote the program but kept ProviderIds

        Assert.True(CategoryRules.Apply(program, ProgramCategory.Shows));
        Assert.True(program.IsSeries);
    }

    // Real airing programs from the server: title vs channel.
    [Theory]
    [InlineData("UK - SKY SPORTS+ 29 FHD", "UK - SKY SPORTS+ 29 FHD", true)]
    [InlineData("##### UK - SKY SPORTS TENNIS  ##### ", "##### UK - SKY SPORTS TENNIS  #####", true)]
    [InlineData("CA - STINGRAY NATURE", "CA - STINGRAY NATURE HD", true)]
    [InlineData("UK - SKY SPORTS+ 9 FHD ", "uk - sky sports+ 9", true)]
    [InlineData("Channel No Longer Available", "CA - DOCUMENTARY HD", true)]
    [InlineData("channel no longer available", "CA - DOCUMENTARY HD", true)]
    [InlineData("", "CA - HISTORY HD", true)]
    [InlineData("Live NHL Ice Hockey", "UK - PREMIER SPORTS 2 SD", false)]
    [InlineData("Pawn Stars", "CA - HISTORY HD ◉", false)]
    [InlineData("UK - SKY SPORTS+ 29 FHD", "UK - SKY SPORTS+ 2 FHD", false)]
    public void IsPlaceholder_RealCases(string title, string channel, bool expected)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Channel No Longer Available" };

        Assert.Equal(expected, ChannelRenamer.IsPlaceholder(title, new[] { channel, null }, ignored));
    }

    [Fact]
    public void IsPlaceholder_MatchesAnyKnownName()
    {
        // Jellyfin name differs, but the Dispatcharr stream name equals the title.
        Assert.True(ChannelRenamer.IsPlaceholder("UK - TT RACES 01 HD", new[] { "TT Races 1", "UK - TT RACES 01 HD" }, Array.Empty<string>()));
    }

    [Theory]
    [InlineData(@"NHL GAME \d+", true)]
    [InlineData(@"^NHL GAME \d+", true)]
    [InlineData(@"NHL GAME|TNT SPORTS", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OldDefaultRegex_IsRecognized(string? regex, bool expected)
    {
        Assert.Equal(expected, Plugin.IsOldDefaultRegex(regex));
    }

    [Theory]
    // The user's real groups.
    [InlineData("AM | CA | DOCUMENTARY", ProgramCategory.Shows)]
    [InlineData("AM | CA | NHL PPV", ProgramCategory.Sports)]
    [InlineData("EU | UK | SKY SPORTS+", ProgramCategory.Sports)]
    [InlineData("EU | UK | SPORTS", ProgramCategory.Sports)]
    [InlineData("EU | UK | TNT SPORTS", ProgramCategory.Sports)]
    [InlineData("AM | CA | NEWS", ProgramCategory.News)]
    [InlineData("UK | SPORTS NEWS", ProgramCategory.News)]
    [InlineData("US | KIDS MOVIES", ProgramCategory.Kids)]
    [InlineData("UK | SKY CINEMA", ProgramCategory.Movies)]
    public void Guess_FromGroupName(string group, ProgramCategory expected)
    {
        Assert.Equal(expected, CategoryRules.Guess(group));
    }

    [Theory]
    [InlineData("EU | UK | MUSIC")]
    [InlineData("AM | CA | ENTERTAINMENT2")]
    [InlineData(null)]
    public void Guess_UnknownGroup_NoRow(string? group)
    {
        Assert.Null(CategoryRules.Guess(group));
    }

    [Fact]
    public void Resolve_RuleWinsOverGuess_AndNoneDisables()
    {
        var rules = CategoryRules.Parse("AM | CA | DOCUMENTARY = None\nEU | UK | SPORTS = Shows");

        Assert.Null(rules.Resolve("AM | CA | DOCUMENTARY", true));
        Assert.Equal(ProgramCategory.Shows, rules.Resolve("EU | UK | SPORTS", true));
        Assert.Equal(ProgramCategory.Sports, rules.Resolve("AM | CA | NHL PPV", true));
        Assert.Null(rules.Resolve("AM | CA | NHL PPV", false));
        Assert.Empty(rules.InvalidLines);
    }
}
