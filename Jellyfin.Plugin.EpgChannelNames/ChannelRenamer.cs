using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.EpgChannelNames.Configuration;
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
    private readonly Func<PluginConfiguration> _getConfig;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelRenamer"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ChannelRenamer}"/> interface.</param>
    public ChannelRenamer(ILibraryManager libraryManager, ILogger<ChannelRenamer> logger)
        : this(libraryManager, logger, () => Plugin.Instance!.Configuration)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelRenamer"/> class with an explicit config source. Used by tests.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ChannelRenamer}"/> interface.</param>
    /// <param name="getConfig">Returns the current configuration.</param>
    internal ChannelRenamer(ILibraryManager libraryManager, ILogger<ChannelRenamer> logger, Func<PluginConfiguration> getConfig)
    {
        _libraryManager = libraryManager;
        _logger = logger;
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

        var channels = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.LiveTvChannel }
        }).OfType<LiveTvChannel>().ToList();

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

            var program = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.LiveTvProgram },
                ChannelIds = new[] { channel.Id },
                MaxStartDate = now,
                MinEndDate = now,
                Limit = 1
            }) is { Count: > 0 } programs ? programs[0] : null;

            var template = program is null ? config.NoProgramTemplate : config.NameTemplate;
            var newName = BuildName(template, program?.Name, original, channel.Number, match.Value);

            if (newName == channel.Name && applied == newName)
            {
                continue;
            }

            _logger.LogInformation("Renaming channel '{Old}' -> '{New}'", channel.Name, newName);
            channel.Name = newName;
            channel.ProviderIds[AppliedKey] = newName;
            await channel.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        progress.Report(100);
    }
}
