using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.EpgChannelNames;

public class RenameChannelsTask : IScheduledTask
{
    private readonly ChannelRenamer _renamer;

    public RenameChannelsTask(ChannelRenamer renamer)
    {
        _renamer = renamer;
    }

    public string Name => "Rename channels from EPG";

    public string Key => "EpgChannelNamesRename";

    public string Description => "Sets Live TV channel names to the program currently airing.";

    public string Category => "Live TV";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _renamer.RunAsync(progress, cancellationToken);

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
