using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.EpgChannelNames.Dispatcharr;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EpgChannelNames.Tests;

public class DispatcharrClientTests
{
    // Shapes copied from Dispatcharr 0.32 responses.
    private const string ChannelsJson = """
        [
          {"id": 3784, "name": "CA - NHL GAME 01", "channel_number": 16.0, "channel_group_id": 1072, "streams": [82814, 9]},
          {"id": 17, "name": "CA - HISTORY HD", "channel_number": 30.0, "channel_group_id": 5, "streams": [500]},
          {"id": 18, "name": "No streams", "channel_number": 12.0, "channel_group_id": null, "streams": []},
          {"id": 2, "name": "No number", "channel_number": null, "channel_group_id": 5, "streams": [2]},
          {"id": 3, "name": "Half", "channel_number": 2.5, "channel_group_id": 999, "streams": [3]}
        ]
        """;

    private const string GroupsJson = "[{\"id\": 1072, \"name\": \"AM | CA | NHL PPV\", \"channel_count\": 15}, {\"id\": 5, \"name\": \"AM | CA | DOCUMENTARY\"}]";

    private const string StreamsJson = """
        [{"id": 82814, "name": "CA - NHL GAME 01 : ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19"},
         {"id": 500, "name": "CA - HISTORY HD"},
         {"id": 3, "name": "half"}]
        """;

    [Fact]
    public async Task ReturnsChannelsWithGroupAndFirstStream()
    {
        var handler = DefaultHandler();

        var result = await CreateClient(handler).GetChannelsAsync("http://dispatcharr:9191/", "secret", CancellationToken.None);

        var nhl = result.Single(c => c.Number == "16");
        Assert.Equal("CA - NHL GAME 01", nhl.Name);
        Assert.Equal("AM | CA | NHL PPV", nhl.Group); // NBSPs normalized
        Assert.Equal("CA - NHL GAME 01 : ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19", nhl.StreamName);
        Assert.Equal("AM | CA | DOCUMENTARY", result.Single(c => c.Number == "30").Group);
        Assert.Null(result.Single(c => c.Number == "12").StreamName);   // no streams
        Assert.Null(result.Single(c => c.Number == "2.5").Group);       // unknown group id
        Assert.DoesNotContain(result, c => c.Name == "No number");
    }

    [Fact]
    public async Task UsesThreeRequests_AndAsksOnlyForFirstStreams()
    {
        var handler = DefaultHandler();

        await CreateClient(handler).GetChannelsAsync("http://dispatcharr:9191", "secret", CancellationToken.None);

        Assert.Equal(
            new[] { "GET /api/channels/channels/", "GET /api/channels/groups/", "POST /api/channels/streams/by-ids/" },
            handler.Requested);
        var ids = JsonDocument.Parse(handler.LastBody!).RootElement.GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(new[] { 82814, 500, 2, 3 }, ids); // first stream only (not 9)
    }

    [Fact]
    public async Task BulkRequest_HasContentLength()
    {
        // Simple HTTP servers read bodies by Content-Length only; a chunked body would arrive empty there.
        var handler = DefaultHandler();

        await CreateClient(handler).GetChannelsAsync("http://dispatcharr:9191", "secret", CancellationToken.None);

        Assert.Equal(handler.LastBody!.Length, handler.LastContentLength);
    }

    [Fact]
    public async Task SendsApiKeyHeader()
    {
        var handler = DefaultHandler();

        await CreateClient(handler).GetChannelsAsync("http://dispatcharr:9191", "secret", CancellationToken.None);

        Assert.Equal("secret", handler.LastApiKey);
    }

    [Fact]
    public async Task NoStreams_SkipsBulkRequest()
    {
        var handler = new FakeHandler(new Dictionary<string, string>
        {
            ["GET /api/channels/channels/"] = """[{"id": 1, "name": "x", "channel_number": 1.0, "streams": []}]""",
            ["GET /api/channels/groups/"] = "[]",
        });

        var result = await CreateClient(handler).GetChannelsAsync("http://dispatcharr:9191", "k", CancellationToken.None);

        Assert.Single(result);
        Assert.DoesNotContain("POST /api/channels/streams/by-ids/", handler.Requested);
    }

    [Fact]
    public async Task HttpError_Throws()
    {
        var handler = new FakeHandler(new Dictionary<string, string>()); // everything 404

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateClient(handler)
            .GetChannelsAsync("http://dispatcharr:9191", "bad", CancellationToken.None));
    }

    [Theory]
    [InlineData(220.0, "220")]
    [InlineData(2.5, "2.5")]
    public void FormatChannelNumber_MatchesTvgChno(double number, string expected)
    {
        Assert.Equal(expected, DispatcharrClient.FormatChannelNumber(number));
    }

    [Theory]
    [InlineData("AM | CA | NHL PPV", "AM | CA | NHL PPV")]
    [InlineData("  EU |  UK | SPORTS ", "EU | UK | SPORTS")]
    public void NormalizeGroupName_CollapsesWhitespace(string input, string expected)
    {
        Assert.Equal(expected, DispatcharrClient.NormalizeGroupName(input));
    }

    private static FakeHandler DefaultHandler() => new(new Dictionary<string, string>
    {
        ["GET /api/channels/channels/"] = ChannelsJson,
        ["GET /api/channels/groups/"] = GroupsJson,
        ["POST /api/channels/streams/by-ids/"] = StreamsJson,
    });

    private static DispatcharrClient CreateClient(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        return new DispatcharrClient(factory.Object);
    }

    private sealed class FakeHandler(Dictionary<string, string> responses) : HttpMessageHandler
    {
        public List<string> Requested { get; } = new();

        public string? LastApiKey { get; private set; }

        public string? LastBody { get; private set; }

        public long? LastContentLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.Method.Method + " " + request.RequestUri!.AbsolutePath;
            Requested.Add(key);
            LastApiKey = request.Headers.TryGetValues("X-API-Key", out var v) ? string.Join(",", v) : null;
            if (request.Content is not null)
            {
                LastContentLength = request.Content.Headers.ContentLength;
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return responses.TryGetValue(key, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
