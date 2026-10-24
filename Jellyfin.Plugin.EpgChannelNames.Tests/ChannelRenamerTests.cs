using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.EpgChannelNames.Configuration;
using Jellyfin.Plugin.EpgChannelNames.Dispatcharr;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EpgChannelNames.Tests;

// BaseItem.LibraryManager is a static Jellyfin sets globally; keep these tests serial.
[Collection("StaticLibraryManager")]
public class ChannelRenamerTests
{
    private const string OriginalKey = "EpgChannelNames.Original";
    private const string AppliedKey = "EpgChannelNames.Applied";

    private readonly Mock<ILibraryManager> _library = new();
    private readonly List<LiveTvChannel> _channels = new();
    private readonly Dictionary<Guid, string> _programs = new();
    private readonly Dictionary<Guid, LiveTvProgram> _programItems = new();
    private readonly Dictionary<Guid, string> _seededTitles = new();
    private readonly Dictionary<Guid, LiveTvProgram> _laterItems = new();
    private readonly PluginConfiguration _config = new();
    private readonly Mock<IDispatcharrClient> _dispatcharr = new();
    private readonly Mock<MediaBrowser.Controller.Persistence.IItemPersistenceService> _repository = new();
    private readonly Dictionary<string, string> _streamNames = new();
    private readonly Dictionary<string, string> _groups = new();

    public ChannelRenamerTests()
    {
        _library
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes.Contains(BaseItemKind.LiveTvChannel))))
            .Returns(() => _channels.Cast<BaseItem>().ToList());
        _library
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes.Contains(BaseItemKind.LiveTvProgram))))
            .Returns((InternalItemsQuery q) => new[] { GetProgram(q.ChannelIds.Single()), _laterItems.GetValueOrDefault(q.ChannelIds.Single()) }
                .Where(p => p is not null
                    && (q.MinEndDate is null || p.EndDate > q.MinEndDate)
                    && (q.MaxStartDate is null || p.StartDate <= q.MaxStartDate))
                .Cast<BaseItem>()
                .ToList());
        _dispatcharr
            .Setup(d => d.GetChannelsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _streamNames
                .Select(kv => new DispatcharrChannel(kv.Key, "Dispatcharr " + kv.Key, _groups.GetValueOrDefault(kv.Key), kv.Value))
                .ToList());

        // EPG-mode tests below rely on the v1 NHL filter; Dispatcharr tests are unaffected by it for NHL names.
        _config.ChannelNameRegex = @"NHL GAME \d+";

        // Many tests use real titles with fixed dates ("OCT 19 – 7:00 PM ET"); the stale check would turn them into
        // "nothing on" once that date passes. The stale tests enable it explicitly with relative dates.
        _config.StaleEventHours = 0;
        // Deleting a program makes it disappear from later queries, like the real library.
        _library
            .Setup(l => l.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), It.IsAny<bool>()))
            .Callback((BaseItem item, DeleteOptions _, bool _) =>
            {
                foreach (var key in _programItems.Where(kv => kv.Value == item).Select(kv => kv.Key).ToList())
                {
                    _programs.Remove(key);
                    _programItems.Remove(key);
                }

                foreach (var key in _laterItems.Where(kv => kv.Value == item).Select(kv => kv.Key).ToList())
                {
                    _laterItems.Remove(key);
                }
            });
        BaseItem.LibraryManager = _library.Object;
    }

    [Fact]
    public async Task MatchingChannel_IsRenamedToCurrentProgram()
    {
        var channel = AddChannel("NHL GAME 01 Old Team vs Stale Team", "Oilers vs Flames");

        await RunAsync();

        Assert.Equal("NHL GAME 01 · Oilers vs Flames", channel.Name);
        Assert.Equal("NHL GAME 01 Old Team vs Stale Team", channel.ProviderIds[OriginalKey]);
        Assert.Equal(channel.Name, channel.ProviderIds[AppliedKey]);
        VerifySaved(channel, Times.Once());
    }

    [Fact]
    public async Task NonMatchingChannel_IsUntouched()
    {
        var channel = AddChannel("CBC News", "The National");

        await RunAsync();

        Assert.Equal("CBC News", channel.Name);
        VerifySaved(channel, Times.Never());
    }

    [Fact]
    public async Task NoProgramAiring_UsesNoProgramTemplate()
    {
        var channel = AddChannel("NHL GAME 02 A vs B", program: null);

        await RunAsync();

        Assert.Equal("NHL GAME 02", channel.Name);
    }

    [Fact]
    public async Task SecondRun_WithSameProgram_DoesNotSaveAgain()
    {
        var channel = AddChannel("NHL GAME 01 A vs B", "Oilers vs Flames");

        await RunAsync();
        await RunAsync();

        VerifySaved(channel, Times.Once());
    }

    [Fact]
    public async Task ProgramChange_RenamesFromStoredOriginal()
    {
        var channel = AddChannel("NHL GAME 01 A vs B", "Oilers vs Flames");
        await RunAsync();

        _programs[channel.Id] = "Bruins vs Rangers";
        await RunAsync();

        Assert.Equal("NHL GAME 01 · Bruins vs Rangers", channel.Name);
        Assert.Equal("NHL GAME 01 A vs B", channel.ProviderIds[OriginalKey]);
    }

    [Fact]
    public async Task GuideRefreshResetsName_NewOriginalIsRecorded()
    {
        var channel = AddChannel("NHL GAME 01 A vs B", "Oilers vs Flames");
        await RunAsync();

        // Jellyfin's Refresh Guide writes the tuner's (new) name back.
        channel.Name = "NHL GAME 01 C vs D";
        await RunAsync();

        Assert.Equal("NHL GAME 01 C vs D", channel.ProviderIds[OriginalKey]);
        Assert.Equal("NHL GAME 01 · Oilers vs Flames", channel.Name);
    }

    [Fact]
    public async Task DefaultRegex_MatchesProviderPrefixedNames()
    {
        var channel = AddChannel("CA - NHL GAME 01", "Game");

        await RunAsync();

        Assert.Equal("NHL GAME 01 · Game", channel.Name);
    }

    [Fact]
    public async Task Dispatcharr_StreamTitleBeatsEpg()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        var channel = AddChannel("CA - NHL GAME 01", "Stale EPG title", number: "16");
        _streamNames["16"] = "CA - NHL GAME 01 : ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19 – 7:00 PM ET / OCT 20 – 12:00 AM UK";

        await RunAsync();

        Assert.Equal("CA - NHL GAME 01 · ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19 – 7:00 PM ET", channel.Name);
    }

    [Fact]
    public async Task Dispatcharr_EmptyStream_IgnoresPlaceholderEpg()
    {
        // Dispatcharr's dummy EPG just repeats the channel name; it must not become the title.
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        var channel = AddChannel("CA - NHL GAME 07", "CA - NHL GAME 07", number: "10");
        _streamNames["10"] = "CA - NHL GAME 07 : ";

        await RunAsync();

        Assert.Equal("CA - NHL GAME 07", channel.Name);
    }

    [Fact]
    public async Task Dispatcharr_ChannelNotListed_FallsBackToEpg()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        var channel = AddChannel("CA - NHL GAME 01", "EPG Game", number: "99");

        await RunAsync();

        Assert.Equal("NHL GAME 01 · EPG Game", channel.Name);
    }

    [Fact]
    public async Task Dispatcharr_RenamesStalePlaceholderProgram()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        var channel = AddChannel("CA - NHL GAME 03", "CA - NHL GAME 03 : SEATTLE KRAKEN @ TAMPA BAY LIGHTNING OCT 15", number: "14");
        _streamNames["14"] = "CA - NHL GAME 03 : SAN JOSE SHARKS @ TORONTO MAPLE LEAFS OCT 19 – 7:30 PM ET / OCT 20 – 12:30 AM UK";

        await RunAsync();

        var program = _programItems[channel.Id];
        Assert.Equal("SAN JOSE SHARKS @ TORONTO MAPLE LEAFS OCT 19 – 7:30 PM ET", program.Name);
        _library.Verify(l => l.UpdateItemAsync(program, It.IsAny<BaseItem>(), ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task Dispatcharr_GameProgram_IsTaggedLiveAndSports()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        var channel = AddChannel("CA - NHL GAME 03", "placeholder", number: "14");
        _streamNames["14"] = "CA - NHL GAME 03 : SHARKS @ LEAFS";

        await RunAsync();

        var program = _programItems[channel.Id];
        Assert.True(program.IsLive);
        Assert.True(program.IsSports);
    }

    [Fact]
    public async Task Dispatcharr_NoGame_ProgramIsNotTagged()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _config.RemoveNoGamePrograms = false;
        var channel = AddChannel("CA - NHL GAME 07", "placeholder", number: "10");
        _streamNames["10"] = "CA - NHL GAME 07 : ";

        await RunAsync();

        Assert.Empty(_programItems[channel.Id].Tags);
    }

    [Fact]
    public async Task Tags_ExistingTagsKept_NotDuplicated()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        var channel = AddChannel("CA - NHL GAME 03", "placeholder", number: "14");
        _streamNames["14"] = "CA - NHL GAME 03 : SHARKS @ LEAFS";
        GetProgram(channel.Id)!.Tags = new[] { "HD", "sports" };

        await RunAsync();

        Assert.Equal(new[] { "HD", "sports", "Live" }, _programItems[channel.Id].Tags);
    }

    [Fact]
    public async Task Tags_AddedLater_TriggerSave()
    {
        // Program already has the right name (e.g. from v1.2.0) but no tags yet.
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        var channel = AddChannel("CA - NHL GAME 03", "SHARKS @ LEAFS", number: "14");
        _streamNames["14"] = "CA - NHL GAME 03 : SHARKS @ LEAFS";

        await RunAsync();

        Assert.True(_programItems[channel.Id].IsSports);
    }

    [Theory]
    [InlineData("Live,Sports", new[] { "Live", "Sports" })]
    [InlineData(" Live , ,Sports ", new[] { "Live", "Sports" })]
    [InlineData("", new string[0])]
    [InlineData(null, new string[0])]
    public void ParseTags_SplitsAndTrims(string? input, string[] expected)
    {
        Assert.Equal(expected, ChannelRenamer.ParseTags(input));
    }

    [Fact]
    public async Task Dispatcharr_NoGame_PlaceholderProgramIsRemoved()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        var channel = AddChannel("CA - NHL GAME 07", "CA - NHL GAME 07", number: "10");
        _streamNames["10"] = "CA - NHL GAME 07 : ";
        var placeholder = GetProgram(channel.Id)!;

        await RunAsync();

        _library.Verify(
            l => l.DeleteItem(placeholder, It.Is<DeleteOptions>(o => !o.DeleteFileLocation && !o.DeleteFromExternalProvider), false),
            Times.Once());
        Assert.Null(GetProgram(channel.Id));
        Assert.Equal("CA - NHL GAME 07", channel.Name); // channel itself stays
    }

    [Fact]
    public async Task Dispatcharr_Game_ProgramIsNotRemoved()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        AddChannel("CA - NHL GAME 03", "placeholder", number: "14");
        _streamNames["14"] = "CA - NHL GAME 03 : SHARKS @ LEAFS";

        await RunAsync();

        _library.Verify(l => l.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), It.IsAny<bool>()), Times.Never());
    }

    [Fact]
    public async Task EpgPath_ProgramsAreNeverRemoved()
    {
        // Dispatcharr configured but doesn't list the channel: real EPG data, keep it.
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        AddChannel("CA - NHL GAME 01", "Real EPG Game", number: "99");

        await RunAsync();

        _library.Verify(l => l.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), It.IsAny<bool>()), Times.Never());
    }

    [Fact]
    public async Task RemoveNoGamePrograms_WorksWithRenameProgramsOff()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _config.RenamePrograms = false;
        var channel = AddChannel("CA - NHL GAME 07", "placeholder", number: "10");
        _streamNames["10"] = "CA - NHL GAME 07 : ";

        await RunAsync();

        Assert.Null(GetProgram(channel.Id));
    }

    [Fact]
    public async Task Dispatcharr_NoGame_ProgramGetsChannelName()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _config.RemoveNoGamePrograms = false;
        var channel = AddChannel("CA - NHL GAME 07", "CA - NHL GAME 07 : WINNIPEG JETS @ CHICAGO BLACKHAWKS OCT 15", number: "10");
        _streamNames["10"] = "CA - NHL GAME 07 : ";

        await RunAsync();

        Assert.Equal("CA - NHL GAME 07", _programItems[channel.Id].Name);
    }

    [Fact]
    public async Task Programs_SecondRun_DoesNotSaveAgain()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        var channel = AddChannel("CA - NHL GAME 03", "placeholder", number: "14");
        _streamNames["14"] = "CA - NHL GAME 03 : SHARKS @ LEAFS";

        await RunAsync();
        await RunAsync();

        var program = _programItems[channel.Id];
        _library.Verify(l => l.UpdateItemAsync(program, It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task RenameProgramsOff_LeavesProgramsAlone()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _config.RenamePrograms = false;
        var channel = AddChannel("CA - NHL GAME 03", "placeholder", number: "14");
        _streamNames["14"] = "CA - NHL GAME 03 : SHARKS @ LEAFS";

        await RunAsync();

        Assert.Equal("placeholder", GetProgram(channel.Id)!.Name);
    }

    [Fact]
    public async Task EpgTitle_ProgramsAreNotTouched()
    {
        var channel = AddChannel("CA - NHL GAME 01", "Real EPG Game");

        await RunAsync();

        var program = _programItems[channel.Id];
        _library.Verify(l => l.UpdateItemAsync(program, It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task AiringGame_DescriptionIsFullStreamName()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        var channel = AddChannel("CA - NHL GAME 03", "CA - NHL GAME 03", number: "14");
        GetProgram(channel.Id)!.Overview = "Early Bird Special - Coffee not included";
        _streamNames["14"] = "CA - NHL GAME 03 : SAN JOSE SHARKS @ TORONTO MAPLE LEAFS OCT 19 – 7:30 PM ET / OCT 20 – 12:30 AM UK";

        await RunAsync();

        Assert.Equal(_streamNames["14"], _programItems[channel.Id].Overview);
    }

    [Fact]
    public async Task GameChannel_LaterBlock_IsPlainUntaggedPlaceholder()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        var channel = AddChannel("CA - NHL GAME 03", "CA - NHL GAME 03", number: "14");
        var later = AddLaterProgram(channel, "CA - NHL GAME 03");
        later.Overview = "Productivity Killer";
        _streamNames["14"] = "CA - NHL GAME 03 : SHARKS @ LEAFS";

        await RunAsync();

        Assert.Equal("SHARKS @ LEAFS", _programItems[channel.Id].Name);
        Assert.True(_programItems[channel.Id].IsSports);
        Assert.Equal("CA - NHL GAME 03", later.Name);
        Assert.False(later.IsSports);
        Assert.False(later.IsLive);
        Assert.Equal(string.Empty, later.Overview);
    }

    [Fact]
    public async Task LaterBlock_TaggedByOlderVersion_GetsGameTagsStripped_OtherTagsKept()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        var channel = AddChannel("CA - NHL GAME 03", "CA - NHL GAME 03", number: "14");
        var later = AddLaterProgram(channel, "SHARKS @ LEAFS");
        later.Tags = new[] { "HD", "Live", "Sports" };
        _streamNames["14"] = "CA - NHL GAME 03 : SHARKS @ LEAFS";

        await RunAsync();

        Assert.Equal(new[] { "HD" }, later.Tags);
    }

    [Fact]
    public async Task NoGame_AiringRemoved_LaterBlockKeptPlain()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        var channel = AddChannel("CA - NHL GAME 07", "CA - NHL GAME 07", number: "10");
        var later = AddLaterProgram(channel, "CA - NHL GAME 07 : WINNIPEG JETS @ CHICAGO BLACKHAWKS OCT 15");
        _streamNames["10"] = "CA - NHL GAME 07 : ";

        await RunAsync();

        Assert.Null(GetProgram(channel.Id));
        Assert.Same(later, _laterItems[channel.Id]);
        Assert.Equal("CA - NHL GAME 07", later.Name);
    }

    [Fact]
    public async Task GameAddedLater_NextBlockGetsTheGameOnceAiring()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        var channel = AddChannel("CA - NHL GAME 07", "CA - NHL GAME 07", number: "10");
        var later = AddLaterProgram(channel, "CA - NHL GAME 07");
        _streamNames["10"] = "CA - NHL GAME 07 : ";
        await RunAsync();

        // Provider adds tonight's game, and the later block starts airing.
        _streamNames["10"] = "CA - NHL GAME 07 : JETS @ HAWKS";
        later.StartDate = DateTime.UtcNow.AddMinutes(-5);
        await RunAsync();

        Assert.Equal("JETS @ HAWKS", later.Name);
        Assert.True(later.IsSports);
    }

    [Theory]
    [InlineData(-60, 60, true)]
    [InlineData(0, 60, true)]
    [InlineData(-60, 0, false)]
    [InlineData(5, 60, false)]
    public void IsAiring_StartInclusive_EndExclusive(int startOffsetMinutes, int endOffsetMinutes, bool expected)
    {
        var now = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        var program = new LiveTvProgram { StartDate = now.AddMinutes(startOffsetMinutes), EndDate = now.AddMinutes(endOffsetMinutes) };

        Assert.Equal(expected, ChannelRenamer.IsAiring(program, now));
    }

    [Fact]
    public async Task TntEventChannel_IsRenamedAndTagged()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _config.ChannelNameRegex = string.Empty;
        var channel = AddChannel("UK - TNT SPORTS | EVENT 2 HD :", "placeholder", number: "120");
        _streamNames["120"] = "UK - TNT SPORTS | EVENT 2 HD : Major League Baseball";

        await RunAsync();

        Assert.Equal("UK - TNT SPORTS | EVENT 2 HD · Major League Baseball", channel.Name);
        Assert.True(_programItems[channel.Id].IsSports);
        Assert.True(_programItems[channel.Id].IsLive);
    }

    [Fact]
    public async Task RegularChannel_IsNeverRenamed_ButGetsItsGroupRow()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _config.ChannelNameRegex = string.Empty;
        _config.GroupCategoryRules = "AM | CA | DOCUMENTARY = Shows";
        var channel = AddChannel("CA - HISTORY HD ◉", "Pawn Stars", number: "30");
        _streamNames["30"] = "CA - HISTORY HD ◉";
        _groups["30"] = "AM | CA | DOCUMENTARY";

        await RunAsync();

        Assert.Equal("CA - HISTORY HD ◉", channel.Name);
        var program = _programItems[channel.Id];
        Assert.Equal("Pawn Stars", program.Name);
        Assert.True(program.IsSeries);
        Assert.False(program.IsLive);
    }

    [Fact]
    public async Task Placeholder_OnRuledChannel_IsNotCategorized()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _config.ChannelNameRegex = string.Empty;
        _config.GroupCategoryRules = "AM | CA | DOCUMENTARY = Shows";
        var channel = AddChannel("CA - STINGRAY NATURE HD", "CA - STINGRAY NATURE", number: "40");
        _streamNames["40"] = "CA - STINGRAY NATURE HD";
        _groups["40"] = "AM | CA | DOCUMENTARY";

        await RunAsync();

        Assert.False(_programItems[channel.Id].IsSeries);
    }

    [Fact]
    public async Task Placeholder_TaggedByV200_IsUntagged()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _config.ChannelNameRegex = string.Empty;
        _config.GroupCategoryRules = "EU | UK | SKY SPORTS+ = Sports";
        var channel = AddChannel("UK - SKY SPORTS+ 29 FHD", "UK - SKY SPORTS+ 29 FHD", number: "90");
        _streamNames["90"] = "UK - SKY SPORTS+ 29 FHD";
        _groups["90"] = "EU | UK | SKY SPORTS+";
        var program = GetProgram(channel.Id)!;
        CategoryRules.Apply(program, ProgramCategory.Sports); // what v2.0.0 did

        await RunAsync();

        Assert.False(program.IsSports);
        Assert.False(program.ProviderIds.ContainsKey(CategoryRules.MarkerKey));
    }

    [Fact]
    public async Task IgnoredTitle_IsNotCategorized()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _config.ChannelNameRegex = string.Empty;
        _config.GroupCategoryRules = "AM | CA | DOCUMENTARY = Shows";
        var channel = AddChannel("CA - DOCUMENTARY HD", "Channel No Longer Available", number: "41");
        _streamNames["41"] = "CA - DOCUMENTARY HD";
        _groups["41"] = "AM | CA | DOCUMENTARY";

        await RunAsync();

        Assert.False(_programItems[channel.Id].IsSeries);
    }

    [Fact]
    public async Task RuleRemoved_RegularChannelProgramIsRestored()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _config.ChannelNameRegex = string.Empty;
        _config.AutoGroupCategories = false; // removing the rule must undo it, not fall back to the guess
        _config.GroupCategoryRules = "AM | CA | DOCUMENTARY = Shows";
        var channel = AddChannel("CA - HISTORY HD", "Pawn Stars", number: "30");
        _streamNames["30"] = "CA - HISTORY HD";
        _groups["30"] = "AM | CA | DOCUMENTARY";
        await RunAsync();

        _config.GroupCategoryRules = string.Empty;
        await RunAsync();

        Assert.False(_programItems[channel.Id].IsSeries);
    }

    [Fact]
    public async Task EventChannel_AiringGetsItsGroupCategory()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _config.GameProgramTags = "Live";
        _config.GroupCategoryRules = "EU | UK | SPORTS = News"; // unusual on purpose, to prove the rule is used
        var channel = AddChannel("UK - GAATV 004", "placeholder", number: "192");
        _streamNames["192"] = "UK - GAATV 004 : DOWN @ CLONDUFF";
        _groups["192"] = "EU | UK | SPORTS";
        _config.ChannelNameRegex = string.Empty;

        await RunAsync();

        Assert.True(_programItems[channel.Id].IsNews);
        Assert.False(_programItems[channel.Id].IsSports);
    }

    [Fact]
    public async Task EventChannel_NoRule_DefaultsToSports()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _config.GameProgramTags = "Live";
        _config.ChannelNameRegex = string.Empty;
        var channel = AddChannel("UK - GAATV 004", "placeholder", number: "192");
        _streamNames["192"] = "UK - GAATV 004 : DOWN @ CLONDUFF";

        await RunAsync();

        Assert.True(_programItems[channel.Id].IsSports);
    }

    [Fact]
    public async Task Filter_LimitsEventChannels()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _config.ChannelNameRegex = "NHL GAME";
        var tnt = AddChannel("UK - TNT SPORTS | EVENT 2 HD :", "x", number: "120");
        _streamNames["120"] = "UK - TNT SPORTS | EVENT 2 HD : Baseball";

        await RunAsync();

        Assert.Equal("UK - TNT SPORTS | EVENT 2 HD :", tnt.Name);
    }

    [Fact]
    public async Task UnknownRuleGroup_LogsKnownGroups()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _config.GroupCategoryRules = "AM | CA | DOCUMENTARIES = Shows";
        _streamNames["30"] = "CA - HISTORY HD";
        _groups["30"] = "AM | CA | DOCUMENTARY";
        var logger = new ListLogger();

        await new ChannelRenamer(_library.Object, logger, _dispatcharr.Object, _repository.Object, () => _config)
            .RunAsync(new SyncProgress(new List<double>()), CancellationToken.None);

        Assert.Contains(logger.Warnings, m => m.Contains("'AM | CA | DOCUMENTARIES' matches no Dispatcharr channel", StringComparison.Ordinal) && m.Contains("AM | CA | DOCUMENTARY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StaleEvent_IsTreatedAsNothingOn()
    {
        _config.StaleEventHours = 6;
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _config.ChannelNameRegex = string.Empty;
        var stale = DateTime.UtcNow.AddDays(-2).ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
        var channel = AddChannel("UK - GAATV 004", "placeholder", number: "192");
        _streamNames["192"] = $"UK - GAATV 004 : DOWN @ CLONDUFF {stale} – 5:30 PM UK / 12:30 PM ET";

        await RunAsync();

        Assert.Equal("UK - GAATV 004", channel.Name);
        Assert.Null(GetProgram(channel.Id)); // airing placeholder hidden, like any empty event slot
    }

    [Fact]
    public async Task FreshEvent_IsKept()
    {
        _config.StaleEventHours = 6;
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _config.ChannelNameRegex = string.Empty;
        var start = DateTime.UtcNow.AddHours(-1);
        var when = start.ToString("MMM d – h:mm tt", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
        var channel = AddChannel("UK - GAATV 004", "placeholder", number: "192");
        _streamNames["192"] = $"UK - GAATV 004 : DOWN @ CLONDUFF {when} UK";

        await RunAsync();

        Assert.Equal($"UK - GAATV 004 · DOWN @ CLONDUFF {when} UK", channel.Name);
    }

    [Fact]
    public async Task RemoteChannelLogo_IsDownloadedOnce_ThenUsedForPrograms()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _config.ChannelNameRegex = string.Empty;
        var channel = AddChannel("CA - HISTORY HD", "Pawn Stars", number: "30");
        channel.SetImage(new MediaBrowser.Controller.Entities.ItemImageInfo { Path = "http://dispatcharr:9191/logo.png", Type = MediaBrowser.Model.Entities.ImageType.Primary }, 0);
        _streamNames["30"] = "CA - HISTORY HD";
        _library
            .Setup(l => l.ConvertImageToLocal(channel, It.IsAny<MediaBrowser.Controller.Entities.ItemImageInfo>(), 0, false))
            .Callback(() => channel.GetImageInfo(MediaBrowser.Model.Entities.ImageType.Primary, 0)!.Path = "/metadata/livetv/x/poster.png")
            .ReturnsAsync(new MediaBrowser.Controller.Entities.ItemImageInfo { Path = "/metadata/livetv/x/poster.png", Type = MediaBrowser.Model.Entities.ImageType.Primary });

        await RunAsync();
        await RunAsync();

        Assert.Equal("/metadata/livetv/x/poster.png", _programItems[channel.Id].GetImageInfo(MediaBrowser.Model.Entities.ImageType.Primary, 0)!.Path);
        _library.Verify(l => l.ConvertImageToLocal(channel, It.IsAny<MediaBrowser.Controller.Entities.ItemImageInfo>(), 0, false), Times.Once());
        _repository.Verify(r => r.SaveImagesAsync(_programItems[channel.Id], It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task UseChannelLogoOff_NothingIsDownloadedOrSet()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _config.ChannelNameRegex = string.Empty;
        _config.UseChannelLogo = false;
        var channel = AddChannel("CA - HISTORY HD", "Pawn Stars", number: "30");
        channel.SetImage(new MediaBrowser.Controller.Entities.ItemImageInfo { Path = "/metadata/poster.png", Type = MediaBrowser.Model.Entities.ImageType.Primary }, 0);
        _streamNames["30"] = "CA - HISTORY HD";

        await RunAsync();

        Assert.Null(_programItems[channel.Id].GetImageInfo(MediaBrowser.Model.Entities.ImageType.Primary, 0));
    }

    [Fact]
    public async Task Summary_IsLoggedEveryRun()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        AddChannel("CA - NHL GAME 01", "x", number: "16");
        AddChannel("CA - NHL GAME 07", "x", number: "10");
        AddChannel("CBC News", "The National", number: "5");
        _streamNames["16"] = "CA - NHL GAME 01 : DUCKS @ RANGERS";
        _streamNames["10"] = "CA - NHL GAME 07 : ";
        var logger = new ListLogger();

        await new ChannelRenamer(_library.Object, logger, _dispatcharr.Object, _repository.Object, () => _config)
            .RunAsync(new SyncProgress(new List<double>()), CancellationToken.None);

        Assert.Contains(
            "Checked 3 channels: 2 from Dispatcharr, 2 event channels (1 with an event now, 0 stale ignored), 0 EPG-mode; 2 renamed; programs: 1 updated, 1 removed, 0 categorized, 0 retitled with teams, 0 repeats hidden, 0 placeholders skipped",
            logger.Messages);
        Assert.DoesNotContain(logger.Messages, m => m.StartsWith("No Live TV channels matched", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoDispatcharrAndNoFilter_LogsNothingToDo()
    {
        _config.ChannelNameRegex = string.Empty;
        AddChannel("CA - NHL GAME 01", "x");
        var logger = new ListLogger();

        await new ChannelRenamer(_library.Object, logger, _dispatcharr.Object, _repository.Object, () => _config)
            .RunAsync(new SyncProgress(new List<double>()), CancellationToken.None);

        Assert.Contains(logger.Warnings, m => m.StartsWith("Nothing to do", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispatcharr_EmptyStreamAndNoEpg_UsesNoProgramTemplate()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        var channel = AddChannel("CA - NHL GAME 05", program: null, number: "12");
        _streamNames["12"] = "CA - NHL GAME 05 : ";

        await RunAsync();

        Assert.Equal("CA - NHL GAME 05", channel.Name);
    }

    [Fact]
    public async Task Dispatcharr_Unreachable_FallsBackToEpg()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _dispatcharr
            .Setup(d => d.GetChannelsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var channel = AddChannel("CA - NHL GAME 01", "EPG Game", number: "16");

        await RunAsync();

        Assert.Equal("NHL GAME 01 · EPG Game", channel.Name);
    }

    [Fact]
    public async Task Dispatcharr_NotConfigured_IsNeverCalled()
    {
        AddChannel("CA - NHL GAME 01", "Game", number: "16");

        await RunAsync();

        _dispatcharr.Verify(
            d => d.GetChannelsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    [Fact]
    public async Task Regex_IsCaseInsensitive()
    {
        var channel = AddChannel("nhl game 03 x vs y", "Game");

        await RunAsync();

        Assert.Equal("nhl game 03 · Game", channel.Name);
    }

    [Fact]
    public async Task CustomTemplate_IsApplied()
    {
        _config.NameTemplate = "{number}: {program}";
        var channel = AddChannel("NHL GAME 01 A vs B", "Oilers vs Flames", number: "101");

        await RunAsync();

        Assert.Equal("101: Oilers vs Flames", channel.Name);
    }

    [Fact]
    public async Task Progress_EndsAt100()
    {
        AddChannel("NHL GAME 01 A vs B", "Game");
        var reports = new List<double>();

        await new ChannelRenamer(_library.Object, NullLogger<ChannelRenamer>.Instance, _dispatcharr.Object, _repository.Object, () => _config)
            .RunAsync(new SyncProgress(reports), CancellationToken.None);

        Assert.Equal(100, reports[^1]);
    }

    [Fact]
    public async Task Cancellation_Throws()
    {
        AddChannel("NHL GAME 01 A vs B", "Game");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ChannelRenamer(_library.Object, NullLogger<ChannelRenamer>.Instance, _dispatcharr.Object, _repository.Object, () => _config)
                .RunAsync(new SyncProgress(new List<double>()), cts.Token));
    }

    private LiveTvChannel AddChannel(string name, string? program, string? number = null)
    {
        var channel = new LiveTvChannel { Id = Guid.NewGuid(), Name = name, Number = number };
        _channels.Add(channel);
        if (program is not null)
        {
            _programs[channel.Id] = program;
        }

        return channel;
    }

    // One persistent program per channel, so renames by the plugin are visible to later queries.
    // Changing _programs[channel] simulates a guide refresh and creates a fresh program.
    private LiveTvProgram? GetProgram(Guid channelId)
    {
        if (!_programs.TryGetValue(channelId, out var title))
        {
            return null;
        }

        if (!_programItems.TryGetValue(channelId, out var program) || _seededTitles[channelId] != title)
        {
            // Airing now: started an hour ago, ends in two.
            program = new LiveTvProgram { Id = Guid.NewGuid(), Name = title, StartDate = DateTime.UtcNow.AddHours(-1), EndDate = DateTime.UtcNow.AddHours(2) };
            _programItems[channelId] = program;
            _seededTitles[channelId] = title;
        }

        return program;
    }

    // The next 4-hour placeholder block, starting after the airing one.
    private LiveTvProgram AddLaterProgram(LiveTvChannel channel, string title)
    {
        var program = new LiveTvProgram { Id = Guid.NewGuid(), Name = title, StartDate = DateTime.UtcNow.AddHours(2), EndDate = DateTime.UtcNow.AddHours(6) };
        _laterItems[channel.Id] = program;
        return program;
    }

    private Task RunAsync()
        => new ChannelRenamer(_library.Object, NullLogger<ChannelRenamer>.Instance, _dispatcharr.Object, _repository.Object, () => _config)
            .RunAsync(new SyncProgress(new List<double>()), CancellationToken.None);

    private void VerifySaved(LiveTvChannel channel, Times times)
        => _library.Verify(
            l => l.UpdateItemAsync(channel, It.IsAny<BaseItem>(), ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()),
            times);

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger<ChannelRenamer>
    {
        public List<string> Messages { get; } = new();

        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Messages.Add(message);
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Warnings.Add(message);
            }
        }
    }

    // Progress<T> posts asynchronously; this records synchronously so asserts see every report.
    private sealed class SyncProgress(List<double> reports) : IProgress<double>
    {
        public void Report(double value) => reports.Add(value);
    }
}
