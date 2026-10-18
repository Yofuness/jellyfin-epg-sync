using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.EpgChannelNames.Configuration;
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
    private readonly PluginConfiguration _config = new();

    public ChannelRenamerTests()
    {
        _library
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes.Contains(BaseItemKind.LiveTvChannel))))
            .Returns(() => _channels.Cast<BaseItem>().ToList());
        _library
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes.Contains(BaseItemKind.LiveTvProgram))))
            .Returns((InternalItemsQuery q) => _programs.TryGetValue(q.ChannelIds.Single(), out var title)
                ? new List<BaseItem> { new LiveTvProgram { Name = title } }
                : new List<BaseItem>());
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

        await new ChannelRenamer(_library.Object, NullLogger<ChannelRenamer>.Instance, () => _config)
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
            () => new ChannelRenamer(_library.Object, NullLogger<ChannelRenamer>.Instance, () => _config)
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

    private Task RunAsync()
        => new ChannelRenamer(_library.Object, NullLogger<ChannelRenamer>.Instance, () => _config)
            .RunAsync(new SyncProgress(new List<double>()), CancellationToken.None);

    private void VerifySaved(LiveTvChannel channel, Times times)
        => _library.Verify(
            l => l.UpdateItemAsync(channel, It.IsAny<BaseItem>(), ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()),
            times);

    // Progress<T> posts asynchronously; this records synchronously so asserts see every report.
    private sealed class SyncProgress(List<double> reports) : IProgress<double>
    {
        public void Report(double value) => reports.Add(value);
    }
}
