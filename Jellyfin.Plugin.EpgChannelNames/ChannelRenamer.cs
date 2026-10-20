using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.EpgChannelNames.Configuration;
using Jellyfin.Plugin.EpgChannelNames.Dispatcharr;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpgChannelNames;

/// <summary>
/// Renames matching Live TV channels to the program currently airing.
/// </summary>
public class ChannelRenamer
{
    // Stored in ProviderIds so they survive restarts. Jellyfin's guide refresh
    // resets Name to the tuner's name, so any Name != Applied is a fresh original.
    private const string OriginalKey = "EpgChannelNames.Original";
    private const string AppliedKey = "EpgChannelNames.Applied";

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ChannelRenamer> _logger;
    private readonly IDispatcharrClient _dispatcharr;
    private readonly Func<PluginConfiguration> _getConfig;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelRenamer"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ChannelRenamer}"/> interface.</param>
    /// <param name="dispatcharr">Instance of the <see cref="IDispatcharrClient"/> interface.</param>
    public ChannelRenamer(ILibraryManager libraryManager, ILogger<ChannelRenamer> logger, IDispatcharrClient dispatcharr)
        : this(libraryManager, logger, dispatcharr, () => Plugin.Instance!.Configuration)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelRenamer"/> class with an explicit config source. Used by tests.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ChannelRenamer}"/> interface.</param>
    /// <param name="dispatcharr">Instance of the <see cref="IDispatcharrClient"/> interface.</param>
    /// <param name="getConfig">Returns the current configuration.</param>
    internal ChannelRenamer(ILibraryManager libraryManager, ILogger<ChannelRenamer> logger, IDispatcharrClient dispatcharr, Func<PluginConfiguration> getConfig)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _dispatcharr = dispatcharr;
        _getConfig = getConfig;
    }

    /// <summary>
    /// Fills a name template.
    /// </summary>
    /// <param name="template">Template with {program}, {original}, {number} and {prefix} tokens.</param>
    /// <param name="programName">Current program title, or null when nothing is airing.</param>
    /// <param name="original">The channel's original name.</param>
    /// <param name="number">The channel number, if any.</param>
    /// <param name="prefix">The regex match within the original name.</param>
    /// <returns>The filled name, or <paramref name="original"/> when the result is empty.</returns>
    internal static string BuildName(string template, string? programName, string original, string? number, string prefix)
    {
        var name = template
            .Replace("{program}", programName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{original}", original, StringComparison.OrdinalIgnoreCase)
            .Replace("{number}", number ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{prefix}", prefix.Trim(), StringComparison.OrdinalIgnoreCase)
            .Trim();
        return string.IsNullOrEmpty(name) ? original : name;
    }

    /// <summary>
    /// Extracts the game title from a Dispatcharr stream name.
    /// </summary>
    /// <param name="streamName">Stream name, e.g. "CA - NHL GAME 01 : DUCKS @ RANGERS OCT 19 – 7:00 PM ET / OCT 20 – 12:00 AM UK".</param>
    /// <param name="titleRegex">Regex; the "title" group is used if present, else the whole match.</param>
    /// <returns>The title, or null when the stream has no game (e.g. "CA - NHL GAME 05 : ").</returns>
    internal static string? ExtractStreamTitle(string? streamName, Regex titleRegex)
    {
        if (string.IsNullOrWhiteSpace(streamName))
        {
            return null;
        }

        var match = titleRegex.Match(streamName);
        if (!match.Success)
        {
            return null;
        }

        var title = (match.Groups["title"].Success ? match.Groups["title"].Value : match.Value).Trim();
        return title.Length == 0 ? null : title;
    }

    /// <summary>
    /// Renames every matching channel once.
    /// </summary>
    /// <param name="progress">Progress reporter, 0 to 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when all channels are processed.</returns>
    public async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = _getConfig();
        var filter = new Regex(config.ChannelNameRegex, RegexOptions.IgnoreCase);
        var now = DateTime.UtcNow;
        var streamNames = await GetStreamNamesAsync(config, filter, cancellationToken).ConfigureAwait(false);
        var titleRegex = new Regex(config.StreamTitleRegex, RegexOptions.IgnoreCase);

        var channels = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.LiveTvChannel }
        }).OfType<LiveTvChannel>().ToList();

        int matched = 0, renamed = 0, fromDispatcharr = 0, fromEpg = 0, nothingAiring = 0, programsRenamed = 0, programsRemoved = 0;
        for (var i = 0; i < channels.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channel = channels[i];
            progress.Report(100.0 * i / Math.Max(1, channels.Count));

            channel.ProviderIds.TryGetValue(AppliedKey, out var applied);
            if (channel.Name != applied)
            {
                channel.ProviderIds[OriginalKey] = channel.Name;
            }

            var original = channel.ProviderIds.GetValueOrDefault(OriginalKey) ?? channel.Name;
            var match = filter.Match(original);
            if (!match.Success)
            {
                continue;
            }

            matched++;
            string? title;
            var dispatcharrListed = false;
            string? streamName = null;
            if (channel.Number is not null && streamNames.TryGetValue(channel.Number, out streamName))
            {
                // Dispatcharr knows this channel: its stream name is authoritative. An empty game ("CA - NHL GAME 05 : ")
                // means nothing tonight. Don't fall back to the EPG, which for these channels is Dispatcharr's own
                // placeholder repeating the channel name.
                dispatcharrListed = true;
                title = ExtractStreamTitle(streamName, titleRegex);
                if (title is not null)
                {
                    fromDispatcharr++;
                }
            }
            else
            {
                title = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.LiveTvProgram },
                    ChannelIds = new[] { channel.Id },
                    MaxStartDate = now,
                    MinEndDate = now,
                    Limit = 1
                }) is { Count: > 0 } programs ? programs[0].Name : null;
                if (title is not null)
                {
                    fromEpg++;
                }
            }

            if (title is null)
            {
                nothingAiring++;
            }

            var template = title is null ? config.NoProgramTemplate : config.NameTemplate;
            var newName = BuildName(template, title, original, channel.Number, match.Value);

            if (dispatcharrListed)
            {
                // Program titles are what the Programs page and guide show; for these channels they are
                // Dispatcharr's 4-hour placeholders. Only the block airing now carries tonight's game.
                var plainName = BuildName(config.NoProgramTemplate, null, original, channel.Number, match.Value);
                var (programsChanged, programsDeleted) = await ApplyProgramsAsync(
                    channel, config, title, streamName!, plainName, now, cancellationToken).ConfigureAwait(false);
                programsRenamed += programsChanged;
                programsRemoved += programsDeleted;
            }

            if (newName == channel.Name && applied == newName)
            {
                continue;
            }

            renamed++;
            _logger.LogInformation("Renaming channel '{Old}' -> '{New}'", channel.Name, newName);
            channel.Name = newName;
            channel.ProviderIds[AppliedKey] = newName;
            await channel.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        progress.Report(100);

        _logger.LogInformation(
            "Checked {Total} channels: {Matched} matched '{Regex}', {Renamed} renamed; titles from Dispatcharr {FromDispatcharr}, from EPG {FromEpg}, nothing airing {NothingAiring}; {ProgramsRenamed} programs renamed, {ProgramsRemoved} programs removed",
            channels.Count,
            matched,
            config.ChannelNameRegex,
            renamed,
            fromDispatcharr,
            fromEpg,
            nothingAiring,
            programsRenamed,
            programsRemoved);
        if (matched == 0 && channels.Count > 0)
        {
            _logger.LogWarning("No Live TV channels matched '{Regex}'. Check 'Channels to rename' in the plugin settings", config.ChannelNameRegex);
        }

        if (!string.IsNullOrWhiteSpace(config.DispatcharrUrl) && matched > 0 && streamNames.Count == 0)
        {
            _logger.LogWarning("Dispatcharr returned no streams for channels matching '{Regex}'", config.ChannelNameRegex);
        }
    }

    /// <summary>
    /// Splits a comma-separated tag list.
    /// </summary>
    /// <param name="tags">e.g. "Live, Sports".</param>
    /// <returns>Trimmed, non-empty tags.</returns>
    internal static string[] ParseTags(string? tags)
        => (tags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Returns whether <paramref name="program"/> is airing at <paramref name="now"/>.
    /// </summary>
    /// <param name="program">The program.</param>
    /// <param name="now">Current UTC time.</param>
    /// <returns>True when start &lt;= now &lt; end.</returns>
    internal static bool IsAiring(LiveTvProgram program, DateTime now)
        => program.StartDate <= now && (program.EndDate ?? DateTime.MaxValue) > now;

    private async Task<(int Changed, int Deleted)> ApplyProgramsAsync(
        LiveTvChannel channel,
        PluginConfiguration config,
        string? title,
        string streamName,
        string plainName,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var gameTags = ParseTags(config.GameProgramTags);
        var programs = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.LiveTvProgram },
            ChannelIds = new[] { channel.Id },
            MinEndDate = now,
            MaxStartDate = now.AddHours(Math.Max(0, config.ProgramWindowHours))
        }).OfType<LiveTvProgram>();

        int changed = 0, deleted = 0;
        foreach (var program in programs)
        {
            var airing = IsAiring(program, now);

            if (airing && title is null && config.RemoveNoGamePrograms)
            {
                // No game: the placeholder only plays a logo. Same delete options Jellyfin's GuideManager uses.
                _libraryManager.DeleteItem(
                    program,
                    new DeleteOptions
                    {
                        DeleteFileLocation = false,
                        DeleteFromExternalProvider = false
                    },
                    false);
                deleted++;
                continue;
            }

            if (!config.RenamePrograms)
            {
                continue;
            }

            // Airing game: title, tags and the full stream name as description. Everything else is a plain,
            // untagged placeholder; the block gets the game (if any) once it starts airing.
            var isGame = airing && title is not null;
            var name = isGame ? title! : plainName;
            var overview = isGame ? streamName : string.Empty;
            var tags = isGame
                ? program.Tags.Union(gameTags, StringComparer.OrdinalIgnoreCase).ToArray()
                : program.Tags.Where(t => !gameTags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToArray();

            if (program.Name == name && (program.Overview ?? string.Empty) == overview && program.Tags.SequenceEqual(tags))
            {
                continue;
            }

            program.Name = name;
            program.Overview = overview;
            program.Tags = tags;
            await program.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            changed++;
        }

        return (changed, deleted);
    }

    private async Task<IReadOnlyDictionary<string, string>> GetStreamNamesAsync(PluginConfiguration config, Regex filter, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.DispatcharrUrl))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return await _dispatcharr.GetStreamNamesByChannelNumberAsync(config.DispatcharrUrl, config.DispatcharrApiKey, filter, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Dispatcharr being down should not stop EPG-based renaming.
            _logger.LogWarning(ex, "Could not read stream names from Dispatcharr at {Url}; falling back to EPG", config.DispatcharrUrl);
            return new Dictionary<string, string>();
        }
    }
}
