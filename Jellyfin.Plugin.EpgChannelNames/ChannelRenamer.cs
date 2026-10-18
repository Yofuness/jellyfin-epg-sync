using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpgChannelNames;

public class ChannelRenamer
{
    // Stored in ProviderIds so they survive restarts. Jellyfin's guide refresh
    // resets Name to the tuner's name, so any Name != Applied is a fresh original.
    private const string OriginalKey = "EpgChannelNames.Original";
    private const string AppliedKey = "EpgChannelNames.Applied";

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ChannelRenamer> _logger;

    public ChannelRenamer(ILibraryManager libraryManager, ILogger<ChannelRenamer> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
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
            }).FirstOrDefault();

            var template = program is null ? config.NoProgramTemplate : config.NameTemplate;
            var newName = template
                .Replace("{program}", program?.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("{original}", original, StringComparison.OrdinalIgnoreCase)
                .Replace("{number}", channel.Number ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("{prefix}", match.Value.Trim(), StringComparison.OrdinalIgnoreCase)
                .Trim();
            if (string.IsNullOrEmpty(newName))
            {
                newName = original;
            }

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
