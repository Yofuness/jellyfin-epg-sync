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
    private readonly PluginConfiguration _config = new();
    private readonly Mock<IDispatcharrClient> _dispatcharr = new();
    private readonly Dictionary<string, string> _streamNames = new();

    public ChannelRenamerTests()
    {
        _library
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes.Contains(BaseItemKind.LiveTvChannel))))
            .Returns(() => _channels.Cast<BaseItem>().ToList());
        _library
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes.Contains(BaseItemKind.LiveTvProgram))))
            .Returns((InternalItemsQuery q) => GetProgram(q.ChannelIds.Single()) is { } p
                ? new List<BaseItem> { p }
                : new List<BaseItem>());
        _dispatcharr
            .Setup(d => d.GetStreamNamesByChannelNumberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Regex>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _streamNames);
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

        Assert.Equal("NHL GAME 01 · ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19 – 7:00 PM ET", channel.Name);
    }

    [Fact]
    public async Task Dispatcharr_EmptyStream_IgnoresPlaceholderEpg()
    {
        // Dispatcharr's dummy EPG just repeats the channel name; it must not become the title.
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        var channel = AddChannel("CA - NHL GAME 07", "CA - NHL GAME 07", number: "10");
        _streamNames["10"] = "CA - NHL GAME 07 : ";

        await RunAsync();

        Assert.Equal("NHL GAME 07", channel.Name);
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
        Assert.Equal("NHL GAME 07", channel.Name); // channel itself stays
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

        Assert.Equal("NHL GAME 07", _programItems[channel.Id].Name);
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
    public async Task Summary_IsLoggedEveryRun()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        AddChannel("CA - NHL GAME 01", "x", number: "16");
        AddChannel("CA - NHL GAME 07", "x", number: "10");
        AddChannel("CBC News", "The National", number: "5");
        _streamNames["16"] = "CA - NHL GAME 01 : DUCKS @ RANGERS";
        _streamNames["10"] = "CA - NHL GAME 07 : ";
        var logger = new ListLogger();

        await new ChannelRenamer(_library.Object, logger, _dispatcharr.Object, () => _config)
            .RunAsync(new SyncProgress(new List<double>()), CancellationToken.None);

        Assert.Contains(
            "Checked 3 channels: 2 matched 'NHL GAME \\d+', 2 renamed; titles from Dispatcharr 1, from EPG 0, nothing airing 1; 1 programs renamed, 1 programs removed",
            logger.Messages);
        Assert.DoesNotContain(logger.Messages, m => m.StartsWith("No Live TV channels matched", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoMatches_LogsWarning()
    {
        _config.ChannelNameRegex = @"^NHL GAME \d+";
        AddChannel("CA - NHL GAME 01", "x");
        var logger = new ListLogger();

        await new ChannelRenamer(_library.Object, logger, _dispatcharr.Object, () => _config)
            .RunAsync(new SyncProgress(new List<double>()), CancellationToken.None);

        Assert.Contains(logger.Warnings, m => m.StartsWith("No Live TV channels matched '^NHL GAME \\d+'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispatcharr_EmptyStreamAndNoEpg_UsesNoProgramTemplate()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        var channel = AddChannel("CA - NHL GAME 05", program: null, number: "12");
        _streamNames["12"] = "CA - NHL GAME 05 : ";

        await RunAsync();

        Assert.Equal("NHL GAME 05", channel.Name);
    }

    [Fact]
    public async Task Dispatcharr_Unreachable_FallsBackToEpg()
    {
        _config.DispatcharrUrl = "http://dispatcharr:9191";
        _dispatcharr
            .Setup(d => d.GetStreamNamesByChannelNumberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Regex>(), It.IsAny<CancellationToken>()))
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
            d => d.GetStreamNamesByChannelNumberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Regex>(), It.IsAny<CancellationToken>()),
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

        await new ChannelRenamer(_library.Object, NullLogger<ChannelRenamer>.Instance, _dispatcharr.Object, () => _config)
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
            () => new ChannelRenamer(_library.Object, NullLogger<ChannelRenamer>.Instance, _dispatcharr.Object, () => _config)
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
            program = new LiveTvProgram { Id = Guid.NewGuid(), Name = title };
            _programItems[channelId] = program;
            _seededTitles[channelId] = title;
        }

        return program;
    }

    private Task RunAsync()
        => new ChannelRenamer(_library.Object, NullLogger<ChannelRenamer>.Instance, _dispatcharr.Object, () => _config)
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
