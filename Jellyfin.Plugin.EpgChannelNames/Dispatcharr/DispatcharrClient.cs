using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.EpgChannelNames.Dispatcharr;

/// <summary>
/// HTTP client for the Dispatcharr REST API.
/// </summary>
public class DispatcharrClient : IDispatcharrClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="DispatcharrClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    public DispatcharrClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Formats a Dispatcharr channel number the way it appears in the M3U <c>tvg-chno</c> (220.0 becomes "220").
    /// </summary>
    /// <param name="channelNumber">Dispatcharr channel number.</param>
    /// <returns>The channel number as text.</returns>
    internal static string FormatChannelNumber(double channelNumber)
        => channelNumber.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetStreamNamesByChannelNumberAsync(
        string baseUrl,
        string apiKey,
        Regex channelFilter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channelFilter);

        using var client = _httpClientFactory.CreateClient(nameof(DispatcharrClient));
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        client.Timeout = TimeSpan.FromSeconds(30);

        // Without page/page_size Dispatcharr returns every channel as a plain array.
        var channels = await client.GetFromJsonAsync<List<ChannelDto>>("api/channels/channels/", cancellationToken).ConfigureAwait(false)
            ?? new List<ChannelDto>();

        var result = new Dictionary<string, string>();
        foreach (var channel in channels)
        {
            if (channel.ChannelNumber is not { } number || channel.Name is null || !channelFilter.IsMatch(channel.Name))
            {
                continue;
            }

            var streams = await client.GetFromJsonAsync<List<StreamDto>>(
                string.Create(CultureInfo.InvariantCulture, $"api/channels/channels/{channel.Id}/streams/"),
                cancellationToken).ConfigureAwait(false);

            // Streams come back in the channel's priority order; the first one is what plays.
            if (streams is { Count: > 0 } && !string.IsNullOrWhiteSpace(streams[0].Name))
            {
                result[FormatChannelNumber(number)] = streams[0].Name!;
            }
        }

        return result;
    }

    private sealed class ChannelDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("channel_number")]
        public double? ChannelNumber { get; set; }
    }

    private sealed class StreamDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
