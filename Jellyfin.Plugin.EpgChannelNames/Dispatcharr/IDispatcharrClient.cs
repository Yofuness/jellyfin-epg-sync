using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.EpgChannelNames.Dispatcharr;

/// <summary>
/// Reads channels, groups and stream names from a Dispatcharr server.
/// </summary>
public interface IDispatcharrClient
{
    /// <summary>
    /// Gets every Dispatcharr channel with its group and first stream name.
    /// </summary>
    /// <param name="baseUrl">Dispatcharr base URL, e.g. http://localhost:9191.</param>
    /// <param name="apiKey">Dispatcharr API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All channels that have a channel number.</returns>
    Task<IReadOnlyList<DispatcharrChannel>> GetChannelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken);
}
