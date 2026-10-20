using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.EpgChannelNames.Teams;

/// <summary>
/// Matchup images: two team logos side by side.
/// </summary>
public interface IMatchupImages
{
    /// <summary>
    /// Returns the local path of the matchup image for two teams, creating it on first use.
    /// </summary>
    /// <param name="first">Team shown on the left.</param>
    /// <param name="second">Team shown on the right.</param>
    /// <param name="leagueLogo">League logo URL shown on top, or null for none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The image path, or null when a team has no logo or the image can't be made.</returns>
    Task<string?> GetAsync(Team first, Team second, string? leagueLogo, CancellationToken cancellationToken);
}
