using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.EpgChannelNames.Dispatcharr;

/// <summary>
/// Reads channel and stream names from a Dispatcharr server.
/// </summary>
public interface IDispatcharrClient
{
    /// <summary>
    /// Gets the first stream name of every Dispatcharr channel whose name matches <paramref name="channelFilter"/>.
    /// </summary>
    /// <param name="baseUrl">Dispatcharr base URL, e.g. http://localhost:9191.</param>
    /// <param name="apiKey">Dispatcharr API key.</param>
    /// <param name="channelFilter">Only channels whose name matches are queried for streams.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stream names keyed by channel number as Jellyfin sees it (e.g. "220").</returns>
    Task<IReadOnlyDictionary<string, string>> GetStreamNamesByChannelNumberAsync(
        string baseUrl,
        string apiKey,
        System.Text.RegularExpressions.Regex channelFilter,
        CancellationToken cancellationToken);
}
