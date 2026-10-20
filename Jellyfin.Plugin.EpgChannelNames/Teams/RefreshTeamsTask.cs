using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.EpgChannelNames.Teams;

/// <summary>
/// Scheduled task that downloads the team database again, monthly by default.
/// </summary>
public class RefreshTeamsTask : IScheduledTask
{
    private readonly ITeamDatabase _teams;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshTeamsTask"/> class.
    /// </summary>
    /// <param name="teams">The team database.</param>
    public RefreshTeamsTask(ITeamDatabase teams)
    {
        _teams = teams;
    }

    /// <inheritdoc />
    public string Name => "Refresh sports team list";

    /// <inheritdoc />
    public string Key => "EpgChannelNamesRefreshTeams";

    /// <inheritdoc />
    public string Description => "Downloads the teams used to put team names in sports program titles.";

    /// <inheritdoc />
    public string Category => "Live TV";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _teams.RefreshAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromDays(30).Ticks
        };
    }
}
