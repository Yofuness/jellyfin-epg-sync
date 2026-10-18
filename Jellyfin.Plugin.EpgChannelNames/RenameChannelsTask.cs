using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.EpgChannelNames;

/// <summary>
/// Scheduled task that runs <see cref="ChannelRenamer"/>.
/// </summary>
public class RenameChannelsTask : IScheduledTask
{
    private readonly ChannelRenamer _renamer;

    /// <summary>
    /// Initializes a new instance of the <see cref="RenameChannelsTask"/> class.
    /// </summary>
    /// <param name="renamer">The channel renamer.</param>
    public RenameChannelsTask(ChannelRenamer renamer)
    {
        _renamer = renamer;
    }

    /// <inheritdoc />
    public string Name => "Rename channels from EPG";

    /// <inheritdoc />
    public string Key => "EpgChannelNamesRename";

    /// <inheritdoc />
    public string Description => "Sets Live TV channel names to the program currently airing.";

    /// <inheritdoc />
    public string Category => "Live TV";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _renamer.RunAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var minutes = Plugin.Instance?.Configuration.IntervalMinutes ?? 15;
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(Math.Max(1, minutes)).Ticks
        };
    }
}
