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
using Jellyfin.Plugin.EpgChannelNames.Teams;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpgChannelNames;

/// <summary>
/// Renames event channels from Dispatcharr stream names and sorts programs into Programs-page rows.
/// </summary>
public partial class ChannelRenamer
{
    // Stored in ProviderIds so they survive restarts. Jellyfin's guide refresh
    // resets Name to the tuner's name, so any Name != Applied is a fresh original.
    private const string OriginalKey = "EpgChannelNames.Original";
    private const string AppliedKey = "EpgChannelNames.Applied";

    // Path of the channel logo the plugin put on a program, so a real program image is never replaced
    // and a changed channel logo is followed.
    private const string LogoKey = "EpgChannelNames.Logo";

    // The EPG title before the plugin put team names in it, and the title it set (a guide refresh resets Name).
    private const string TitleOriginalKey = "EpgChannelNames.TitleOriginal";
    private const string TitleAppliedKey = "EpgChannelNames.TitleApplied";

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ChannelRenamer> _logger;
    private readonly IDispatcharrClient _dispatcharr;
    private readonly IItemRepository _itemRepository;
    private readonly ITeamDatabase? _teams;
    private readonly Func<PluginConfiguration> _getConfig;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelRenamer"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ChannelRenamer}"/> interface.</param>
    /// <param name="dispatcharr">Instance of the <see cref="IDispatcharrClient"/> interface.</param>
    /// <param name="itemRepository">Instance of the <see cref="IItemRepository"/> interface.</param>
    /// <param name="teams">Instance of the <see cref="ITeamDatabase"/> interface.</param>
    public ChannelRenamer(ILibraryManager libraryManager, ILogger<ChannelRenamer> logger, IDispatcharrClient dispatcharr, IItemRepository itemRepository, ITeamDatabase teams)
        : this(libraryManager, logger, dispatcharr, itemRepository, () => Plugin.Instance!.Configuration, teams)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelRenamer"/> class with an explicit config source. Used by tests.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ChannelRenamer}"/> interface.</param>
    /// <param name="dispatcharr">Instance of the <see cref="IDispatcharrClient"/> interface.</param>
    /// <param name="itemRepository">Instance of the <see cref="IItemRepository"/> interface.</param>
    /// <param name="getConfig">Returns the current configuration.</param>
    /// <param name="teams">The team database, or null for none.</param>
    internal ChannelRenamer(ILibraryManager libraryManager, ILogger<ChannelRenamer> logger, IDispatcharrClient dispatcharr, IItemRepository itemRepository, Func<PluginConfiguration> getConfig, ITeamDatabase? teams = null)
    {
        _teams = teams;
        _libraryManager = libraryManager;
        _logger = logger;
        _dispatcharr = dispatcharr;
        _itemRepository = itemRepository;
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
    /// Parses an event channel's stream name.
    /// </summary>
    /// <param name="streamName">e.g. "CA - NHL GAME 01 : DUCKS @ RANGERS OCT 19 – 7:00 PM ET / OCT 20 – 12:00 AM UK" or "UK - TNT SPORTS | EVENT 9 HD :".</param>
    /// <param name="eventRegex">Regex with "prefix" and "title" groups.</param>
    /// <returns>Null when the stream is not an event channel; otherwise the prefix and the title (null = no event now).</returns>
    internal static (string Prefix, string? Title)? ParseEvent(string? streamName, Regex eventRegex)
    {
        if (string.IsNullOrWhiteSpace(streamName))
        {
            return null;
        }

        var match = eventRegex.Match(streamName);
        if (!match.Success || !match.Groups["prefix"].Success)
        {
            return null;
        }

        var prefix = match.Groups["prefix"].Value.Trim();
        var title = match.Groups["title"].Success ? match.Groups["title"].Value.Trim() : string.Empty;
        return prefix.Length == 0 ? null : (prefix, title.Length == 0 ? null : title);
    }

    /// <summary>
    /// Renames event channels and updates programs once.
    /// </summary>
    /// <param name="progress">Progress reporter, 0 to 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when all channels are processed.</returns>
    public async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = _getConfig();
        var filter = string.IsNullOrWhiteSpace(config.ChannelNameRegex) ? null : new Regex(config.ChannelNameRegex, RegexOptions.IgnoreCase);
        var eventRegex = new Regex(config.EventRegex, RegexOptions.IgnoreCase);
        var rules = CategoryRules.Parse(config.GroupCategoryRules);
        var ignoredTitles = (config.IgnoredProgramTitles ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var teams = config.UseTeamNames && _teams is not null ? await _teams.GetTeamsAsync(cancellationToken).ConfigureAwait(false) : null;

        foreach (var line in rules.InvalidLines)
        {
            _logger.LogWarning("Ignoring category rule '{Line}'. Use 'Dispatcharr group = Shows|Movies|Sports|News|Kids|None'", line);
        }

        var dispatcharr = await GetDispatcharrChannelsAsync(config, cancellationToken).ConfigureAwait(false);
        var byNumber = new Dictionary<string, DispatcharrChannel>();
        foreach (var d in dispatcharr ?? Array.Empty<DispatcharrChannel>())
        {
            byNumber.TryAdd(d.Number, d);
        }

        if (dispatcharr is not null)
        {
            var known = dispatcharr.Select(d => d.Group).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var group in rules.Groups.Where(g => !known.Contains(g)))
            {
                _logger.LogWarning("Category rule group '{Group}' matches no Dispatcharr channel. Groups in use: {Groups}", group, string.Join(", ", known.Order()));
            }
        }

        var channels = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.LiveTvChannel }
        }).OfType<LiveTvChannel>().ToList();

        var stats = new RunStats();
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
            var filterMatch = filter?.Match(original);
            if (filterMatch is { Success: false })
            {
                continue;
            }

            string? newName = null;
            if (channel.Number is not null && byNumber.TryGetValue(channel.Number, out var d))
            {
                stats.Listed++;
                if (config.UseChannelLogo)
                {
                    await EnsureLocalChannelLogoAsync(channel).ConfigureAwait(false);
                }

                var category = rules.Resolve(d.Group, config.AutoGroupCategories);
                if (ParseEvent(d.StreamName, eventRegex) is { } ev)
                {
                    // Providers leave finished events in stream names for days; past ones count as "nothing on".
                    if (ev.Title is not null && EventTime.IsStale(ev.Title, now, config.StaleEventHours))
                    {
                        stats.StaleEvents++;
                        ev = (ev.Prefix, null);
                    }

                    // Event channel: the stream name is authoritative. No title = nothing on now; the EPG for these
                    // channels is Dispatcharr's placeholder repeating the channel name, so it's never used.
                    stats.Events++;
                    if (ev.Title is not null)
                    {
                        stats.LiveEvents++;
                    }

                    newName = BuildName(ev.Title is null ? config.NoProgramTemplate : config.NameTemplate, ev.Title, original, channel.Number, ev.Prefix);
                    var plainName = BuildName(config.NoProgramTemplate, null, original, channel.Number, ev.Prefix);
                    await ApplyEventProgramsAsync(channel, config, ev.Title, d.StreamName!, plainName, category ?? ProgramCategory.Sports, now, stats, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Regular channel: keep its name and real EPG; only sort its programs into a Programs-page row.
                    // Placeholders (title = the channel's own name) and ignored titles never get a row.
                    var names = new[] { original, d.Name, d.StreamName };
                    await ApplyCategoryAsync(channel, category, names, ignoredTitles, config, teams, now, stats, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (filterMatch is { Success: true })
            {
                // EPG-only mode (Dispatcharr off, down, or not listing this channel): rename from the airing program.
                stats.EpgMatched++;
                var title = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.LiveTvProgram },
                    ChannelIds = new[] { channel.Id },
                    MaxStartDate = now,
                    MinEndDate = now,
                    Limit = 1
                }) is { Count: > 0 } programs ? programs[0].Name : null;
                newName = BuildName(title is null ? config.NoProgramTemplate : config.NameTemplate, title, original, channel.Number, filterMatch.Value);
            }

            if (newName is null || (newName == channel.Name && applied == newName))
            {
                continue;
            }

            stats.Renamed++;
            _logger.LogInformation("Renaming channel '{Old}' -> '{New}'", channel.Name, newName);
            channel.Name = newName;
            channel.ProviderIds[AppliedKey] = newName;
            await channel.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        progress.Report(100);

        _logger.LogInformation(
            "Checked {Total} channels: {Listed} from Dispatcharr, {Events} event channels ({LiveEvents} with an event now, {StaleEvents} stale ignored), {EpgMatched} EPG-mode; {Renamed} renamed; programs: {Updated} updated, {Removed} removed, {Categorized} categorized, {Retitled} retitled with teams, {Repeats} repeats hidden, {Placeholders} placeholders skipped",
            channels.Count,
            stats.Listed,
            stats.Events,
            stats.LiveEvents,
            stats.StaleEvents,
            stats.EpgMatched,
            stats.Renamed,
            stats.ProgramsUpdated,
            stats.ProgramsRemoved,
            stats.ProgramsCategorized,
            stats.ProgramsRetitled,
            stats.RepeatsHidden,
            stats.PlaceholdersSkipped);
        if (dispatcharr is not null && dispatcharr.Count == 0)
        {
            _logger.LogWarning("Dispatcharr returned no channels");
        }

        if (dispatcharr is null && filter is null)
        {
            _logger.LogWarning("Nothing to do: set a Dispatcharr URL, or 'Only channels matching' for EPG-only mode");
        }
    }

    /// <summary>
    /// Returns whether a program is a placeholder rather than a real show: its title is the channel's own name
    /// (ignoring quality tokens like HD/FHD/◉), or it is in <paramref name="ignoredTitles"/>.
    /// Dispatcharr fills channels without a real guide with such placeholders.
    /// </summary>
    /// <param name="title">Program title.</param>
    /// <param name="channelNames">Names the channel is known by (Jellyfin original, Dispatcharr channel, stream).</param>
    /// <param name="ignoredTitles">Titles that are never real programs, e.g. "Channel No Longer Available".</param>
    /// <returns>True for placeholders.</returns>
    internal static bool IsPlaceholder(string? title, IEnumerable<string?> channelNames, IReadOnlyCollection<string> ignoredTitles)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return true;
        }

        if (ignoredTitles.Contains(title.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalized = NormalizeTitle(title);
        return channelNames.Any(n => !string.IsNullOrWhiteSpace(n) && NormalizeTitle(n) == normalized);
    }

    /// <summary>
    /// Gives a program without its own image the channel's logo (a local file Jellyfin already has).
    /// A real program image is never replaced; a logo the plugin set earlier follows the channel's current logo.
    /// </summary>
    /// <param name="program">The program.</param>
    /// <param name="channel">Its channel.</param>
    /// <returns>True when the program changed.</returns>
    internal static bool ApplyChannelLogo(LiveTvProgram program, LiveTvChannel channel)
    {
        var current = program.GetImageInfo(ImageType.Primary, 0);
        program.ProviderIds.TryGetValue(LogoKey, out var ours);
        if (current is not null && current.Path != ours)
        {
            return false; // the program has its own image
        }

        var logo = channel.GetImageInfo(ImageType.Primary, 0);
        if (logo is null || !logo.IsLocalFile)
        {
            if (current is null)
            {
                return false;
            }

            program.RemoveImage(current);
            program.ProviderIds.Remove(LogoKey);
            return true;
        }

        if (current?.Path == logo.Path)
        {
            return false;
        }

        program.SetImage(
            new ItemImageInfo
            {
                Path = logo.Path,
                Type = ImageType.Primary,
                DateModified = logo.DateModified,
                Width = logo.Width,
                Height = logo.Height,
                BlurHash = logo.BlurHash
            },
            0);
        program.ProviderIds[LogoKey] = logo.Path;
        return true;
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

    private async Task ApplyEventProgramsAsync(
        LiveTvChannel channel,
        PluginConfiguration config,
        string? title,
        string streamName,
        string plainName,
        ProgramCategory category,
        DateTime now,
        RunStats stats,
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
                stats.ProgramsRemoved++;
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

            var unchanged = program.Name == name && (program.Overview ?? string.Empty) == overview && program.Tags.SequenceEqual(tags);
            var logoChanged = config.UseChannelLogo && ApplyChannelLogo(program, channel);
            program.Name = name;
            program.Overview = overview;
            program.Tags = tags;

            // The airing event also gets its group's category; placeholders get none.
            var categoryChanged = CategoryRules.Apply(program, isGame ? category : null);
            if (unchanged && !categoryChanged && !logoChanged)
            {
                continue;
            }

            await program.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            await SaveImagesIfChangedAsync(program, logoChanged).ConfigureAwait(false);
            stats.ProgramsUpdated++;
        }
    }

    private async Task ApplyCategoryAsync(
        LiveTvChannel channel,
        ProgramCategory? category,
        string?[] channelNames,
        IReadOnlyCollection<string> ignoredTitles,
        PluginConfiguration config,
        IReadOnlyList<Team>? teams,
        DateTime now,
        RunStats stats,
        CancellationToken cancellationToken)
    {
        var programs = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.LiveTvProgram },
            ChannelIds = new[] { channel.Id },
            MinEndDate = now,
            MaxStartDate = now.AddHours(Math.Max(0, config.ProgramWindowHours))
        }).OfType<LiveTvProgram>().OrderBy(p => p.StartDate).ToList();

        LiveTvProgram? previous = null;
        foreach (var program in programs)
        {
            // Checked against the EPG title, not one the plugin set.
            var epgTitle = program.ProviderIds.GetValueOrDefault(TitleOriginalKey) ?? program.Name;
            var placeholder = IsPlaceholder(epgTitle, channelNames, ignoredTitles);
            if (placeholder && category is not null)
            {
                stats.PlaceholdersSkipped++;
            }

            // Sports-ness before this run's category change, so a hidden repeat still gets team names.
            var isSports = category == ProgramCategory.Sports || program.IsSports;
            var titleChanged = ApplyTeamTitle(program, placeholder ? null : teams, isSports);
            if (titleChanged)
            {
                stats.ProgramsRetitled++;
            }

            // A block continuing the same show ("Live Tennis" 16:00, then 17:00) would be a second card in the row.
            var repeat = config.HideRepeatedPrograms && IsRepeat(previous, program);
            if (repeat && category is not null && !placeholder)
            {
                stats.RepeatsHidden++;
            }

            previous = program;

            // Apply(null) also undoes tags an earlier version put on placeholders and repeats.
            var categoryChanged = CategoryRules.Apply(program, placeholder || repeat ? null : category);

            var logoChanged = config.UseChannelLogo && ApplyChannelLogo(program, channel);
            if (!categoryChanged && !logoChanged && !titleChanged)
            {
                continue;
            }

            await program.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            await SaveImagesIfChangedAsync(program, logoChanged).ConfigureAwait(false);
            stats.ProgramsCategorized++;
        }
    }

    /// <summary>
    /// Returns whether a program directly continues the previous one on the same channel with the same title.
    /// </summary>
    /// <param name="previous">The channel's previous program, or null.</param>
    /// <param name="program">The program.</param>
    /// <returns>True for back-to-back blocks of one show.</returns>
    internal static bool IsRepeat(LiveTvProgram? previous, LiveTvProgram program)
        => previous is not null
            && string.Equals(previous.Name, program.Name, StringComparison.OrdinalIgnoreCase)
            && previous.EndDate is { } end
            && program.StartDate <= end.AddMinutes(1);

    /// <summary>
    /// Puts the two teams found in a program's title and description into its title, or restores the EPG title.
    /// </summary>
    /// <param name="program">The program.</param>
    /// <param name="teams">The team database, or null to restore the EPG title.</param>
    /// <param name="isSports">Whether the program or its channel is sports.</param>
    /// <returns>Whether the title changed.</returns>
    internal static bool ApplyTeamTitle(LiveTvProgram program, IReadOnlyList<Team>? teams, bool isSports)
    {
        program.ProviderIds.TryGetValue(TitleAppliedKey, out var applied);
        if (applied is not null && program.Name != applied)
        {
            // A guide refresh wrote a new EPG title.
            program.ProviderIds.Remove(TitleOriginalKey);
            program.ProviderIds.Remove(TitleAppliedKey);
            applied = null;
        }

        var epgTitle = applied is null ? program.Name : program.ProviderIds.GetValueOrDefault(TitleOriginalKey) ?? program.Name;
        var title = teams is null ? null
            : MotorsportMatcher.BuildTitle(epgTitle, program.Overview, DateTime.UtcNow.Year)
                ?? TeamMatcher.BuildTitle(epgTitle, program.Overview, isSports, teams);
        if (title is null)
        {
            if (applied is null)
            {
                return false;
            }

            program.Name = epgTitle;
            program.ProviderIds.Remove(TitleOriginalKey);
            program.ProviderIds.Remove(TitleAppliedKey);
            return true;
        }

        if (title == applied)
        {
            return false;
        }

        program.ProviderIds[TitleOriginalKey] = epgTitle;
        program.ProviderIds[TitleAppliedKey] = title;
        program.Name = title;
        return true;
    }

    // Jellyfin can keep a channel logo as a remote URL and download it only when viewed. Programs reuse the
    // channel's file, so have Jellyfin download it once. removeOnFailure: false keeps the logo if it fails.
    private async Task EnsureLocalChannelLogoAsync(LiveTvChannel channel)
    {
        var logo = channel.GetImageInfo(ImageType.Primary, 0);
        if (logo is null || logo.IsLocalFile)
        {
            return;
        }

        try
        {
            await _libraryManager.ConvertImageToLocal(channel, logo, 0, false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or TaskCanceledException or InvalidOperationException)
        {
            // InvalidOperationException: Jellyfin's "Unable to convert any images to local" (every URL 404/403).
            _logger.LogWarning(ex, "Could not download the logo of channel '{Channel}'", channel.Name);
        }
    }

    // Jellyfin 10.11 does not persist images of an existing item on update (only the item row), and
    // ILibraryManager.UpdateImagesAsync skips non-library items such as Live TV programs. SaveImages writes them.
    private Task SaveImagesIfChangedAsync(LiveTvProgram program, bool changed)
    {
        if (changed)
        {
            _itemRepository.SaveImages(program);
        }

        return Task.CompletedTask;
    }

    private async Task<IReadOnlyList<DispatcharrChannel>?> GetDispatcharrChannelsAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.DispatcharrUrl))
        {
            return null;
        }

        try
        {
            return await _dispatcharr.GetChannelsAsync(config.DispatcharrUrl, config.DispatcharrApiKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Dispatcharr being down should not fail the task; channels keep their last names until it's back.
            _logger.LogWarning(ex, "Could not read channels from Dispatcharr at {Url}", config.DispatcharrUrl);
            return null;
        }
    }

    private static string NormalizeTitle(string text)
        => SpaceRegex().Replace(QualityRegex().Replace(text, " "), " ").Trim().ToUpperInvariant();

    [GeneratedRegex(@"\b(FHD|HD|SD|HEVC|UHD|4K)\b|[◉²]", RegexOptions.IgnoreCase)]
    private static partial Regex QualityRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();

    private sealed class RunStats
    {
        public int Listed { get; set; }

        public int Events { get; set; }

        public int LiveEvents { get; set; }

        public int EpgMatched { get; set; }

        public int Renamed { get; set; }

        public int ProgramsUpdated { get; set; }

        public int ProgramsRemoved { get; set; }

        public int ProgramsCategorized { get; set; }

        public int PlaceholdersSkipped { get; set; }

        public int ProgramsRetitled { get; set; }

        public int RepeatsHidden { get; set; }

        public int StaleEvents { get; set; }
    }
}
