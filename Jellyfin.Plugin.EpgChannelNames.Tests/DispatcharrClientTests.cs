using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
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
          {"id": 3988, "name": "CA - NHL GAME 01 : BUFFALO SABRES @ TORONTO MAPLE LEAFS OCT 15", "channel_number": 220.0, "streams": [82814]},
          {"id": 16, "name": "CA - NHL GAME 01", "channel_number": 16.0, "streams": [82814]},
          {"id": 17, "name": "CA - NHL GAME 05", "channel_number": 12.0, "streams": []},
          {"id": 1, "name": "CBC News", "channel_number": 5.0, "streams": [1]},
          {"id": 2, "name": "NHL GAME 99", "channel_number": null, "streams": [2]},
          {"id": 3, "name": "NHL GAME 98", "channel_number": 2.5, "streams": [3]}
        ]
        """;

    private const string GameStreamJson = """[{"id": 82814, "name": "CA - NHL GAME 01 : ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19"}, {"id": 9, "name": "backup"}]""";

    [Fact]
    public async Task ReturnsFirstStreamName_KeyedByChannelNumber()
    {
        var handler = new FakeHandler(new Dictionary<string, string>
        {
            ["/api/channels/channels/"] = ChannelsJson,
            ["/api/channels/channels/3988/streams/"] = GameStreamJson,
            ["/api/channels/channels/16/streams/"] = GameStreamJson,
            ["/api/channels/channels/17/streams/"] = "[]",
            ["/api/channels/channels/3/streams/"] = """[{"id": 3, "name": "half"}]""",
        });

        var result = await CreateClient(handler).GetStreamNamesByChannelNumberAsync(
            "http://dispatcharr:9191/", "secret", new Regex("NHL GAME \\d+"), CancellationToken.None);

        Assert.Equal("CA - NHL GAME 01 : ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19", result["220"]);
        Assert.Equal("CA - NHL GAME 01 : ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19", result["16"]);
        Assert.Equal("half", result["2.5"]);
        Assert.False(result.ContainsKey("12")); // no streams
        Assert.False(result.ContainsKey("5"));  // filtered out by regex
        Assert.DoesNotContain("/api/channels/channels/1/streams/", handler.Requested);
        Assert.DoesNotContain("/api/channels/channels/2/streams/", handler.Requested); // no channel number
    }

    [Fact]
    public async Task SendsApiKeyHeader()
    {
        var handler = new FakeHandler(new Dictionary<string, string> { ["/api/channels/channels/"] = "[]" });

        await CreateClient(handler).GetStreamNamesByChannelNumberAsync("http://dispatcharr:9191", "secret", new Regex("x"), CancellationToken.None);

        Assert.Equal("secret", handler.LastApiKey);
    }

    [Fact]
    public async Task HttpError_Throws()
    {
        var handler = new FakeHandler(new Dictionary<string, string>()); // everything 404

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateClient(handler)
            .GetStreamNamesByChannelNumberAsync("http://dispatcharr:9191", "bad", new Regex("x"), CancellationToken.None));
    }

    [Theory]
    [InlineData(220.0, "220")]
    [InlineData(2.5, "2.5")]
    public void FormatChannelNumber_MatchesTvgChno(double number, string expected)
    {
        Assert.Equal(expected, DispatcharrClient.FormatChannelNumber(number));
    }

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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requested.Add(path);
            LastApiKey = request.Headers.TryGetValues("X-API-Key", out var v) ? string.Join(",", v) : null;
            return Task.FromResult(responses.TryGetValue(path, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
