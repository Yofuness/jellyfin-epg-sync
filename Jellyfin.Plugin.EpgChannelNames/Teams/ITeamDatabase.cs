using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.EpgChannelNames.Teams;

/// <summary>
/// Teams of the leagues in <see cref="League.All"/>, cached on disk.
/// </summary>
public interface ITeamDatabase
{
    /// <summary>
    /// Returns the cached teams. Downloads them first when there is no cache yet.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All known teams; only the built-in ones if nothing could be downloaded.</returns>
    Task<IReadOnlyList<Team>> GetTeamsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Downloads all leagues again and updates the cache. A league that fails keeps its cached teams.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of teams now known.</returns>
    Task<int> RefreshAsync(CancellationToken cancellationToken);
}
