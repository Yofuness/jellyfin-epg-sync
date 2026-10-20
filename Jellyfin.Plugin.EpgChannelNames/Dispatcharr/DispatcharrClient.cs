using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.EpgChannelNames.Dispatcharr;

/// <summary>
/// HTTP client for the Dispatcharr REST API.
/// </summary>
public partial class DispatcharrClient : IDispatcharrClient
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

    /// <summary>
    /// Normalizes a group name: non-breaking and repeated whitespace become one space, then trimmed.
    /// Provider group names like "AM | CA | NHL PPV" contain U+00A0.
    /// </summary>
    /// <param name="name">Group name.</param>
    /// <returns>The normalized name.</returns>
    internal static string NormalizeGroupName(string name)
        => WhitespaceRegex().Replace(name, " ").Trim();

    /// <inheritdoc />
    public async Task<IReadOnlyList<DispatcharrChannel>> GetChannelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(nameof(DispatcharrClient));
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        client.Timeout = TimeSpan.FromSeconds(30);

        // Without page/page_size Dispatcharr returns plain arrays.
        var channels = await client.GetFromJsonAsync<List<ChannelDto>>("api/channels/channels/", cancellationToken).ConfigureAwait(false)
            ?? new List<ChannelDto>();
        var groups = await client.GetFromJsonAsync<List<GroupDto>>("api/channels/groups/", cancellationToken).ConfigureAwait(false)
            ?? new List<GroupDto>();
        var groupNames = groups
            .Where(g => g.Name is not null)
            .GroupBy(g => g.Id)
            .ToDictionary(g => g.Key, g => NormalizeGroupName(g.First().Name!));

        // Streams come back in the channel's priority order; the first one is what plays.
        var firstStreamIds = channels.Where(c => c.Streams is { Count: > 0 }).Select(c => c.Streams![0]).Distinct().ToList();
        var streamNames = new Dictionary<int, string>();
        if (firstStreamIds.Count > 0)
        {
            // StringContent sends Content-Length; PostAsJsonAsync would send a chunked body. Dispatcharr 0.32
            // accepts both, but simple servers (like the integration test's fake) only read Content-Length.
            using var body = new StringContent(JsonSerializer.Serialize(new { ids = firstStreamIds }), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("api/channels/streams/by-ids/", body, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var streams = await response.Content.ReadFromJsonAsync<List<StreamDto>>(cancellationToken).ConfigureAwait(false) ?? new List<StreamDto>();
            foreach (var stream in streams.Where(s => s.Name is not null))
            {
                streamNames[stream.Id] = stream.Name!;
            }
        }

        var result = new List<DispatcharrChannel>();
        foreach (var channel in channels)
        {
            if (channel.ChannelNumber is not { } number || channel.Name is null)
            {
                continue;
            }

            var group = channel.ChannelGroupId is { } gid && groupNames.TryGetValue(gid, out var g) ? g : null;
            var streamName = channel.Streams is { Count: > 0 } ids && streamNames.TryGetValue(ids[0], out var s) ? s : null;
            result.Add(new DispatcharrChannel(FormatChannelNumber(number), channel.Name, group, streamName));
        }

        return result;
    }

    [GeneratedRegex(@"[\s ]+")]
    private static partial Regex WhitespaceRegex();

    private sealed class ChannelDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("channel_number")]
        public double? ChannelNumber { get; set; }

        [JsonPropertyName("channel_group_id")]
        public int? ChannelGroupId { get; set; }

        [JsonPropertyName("streams")]
        public List<int>? Streams { get; set; }
    }

    private sealed class GroupDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class StreamDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
