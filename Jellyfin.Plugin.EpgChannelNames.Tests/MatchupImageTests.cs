using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.EpgChannelNames.Teams;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkiaSharp;
using Xunit;

namespace Jellyfin.Plugin.EpgChannelNames.Tests;

public sealed class MatchupImageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "epg-matchup-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void Match_EventTitle_FindsNhlTeamsInOrder()
    {
        var nhl = League.All.Single(l => l.Key == "nhl");
        var teams = new[]
        {
            TeamDatabase.ToTeam(nhl, "Anaheim Ducks", "Ducks", "Ducks", "Anaheim", "http://x/ana.png"),
            TeamDatabase.ToTeam(nhl, "New York Rangers", "Rangers", "Rangers", "New York", "http://x/nyr.png"),
        };

        var match = TeamMatcher.Match("ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19 – 7:00 PM ET", null, true, teams);

        Assert.NotNull(match);
        Assert.Equal("Ducks", match.First.ShortName);
        Assert.Equal("Rangers", match.Second.ShortName);
    }

    [Fact]
    public void Render_MakesA16By9Png()
    {
        var png = MatchupImages.Render(Png(SKColors.Red), Png(SKColors.Blue));

        using var image = SKBitmap.Decode(png);
        Assert.Equal(MatchupImages.Width, image.Width);
        Assert.Equal(MatchupImages.Height, image.Height);
        Assert.Equal(SKColors.Red, image.GetPixel(MatchupImages.Width / 4, MatchupImages.Height / 2));
        Assert.Equal(SKColors.Blue, image.GetPixel(MatchupImages.Width * 3 / 4, MatchupImages.Height / 2));
    }

    [Fact]
    public void Render_LeagueLogoOnTop()
    {
        var png = MatchupImages.Render(Png(SKColors.Red), Png(SKColors.Blue), Png(SKColors.Yellow));

        using var image = SKBitmap.Decode(png);
        Assert.Equal(SKColors.Yellow, image.GetPixel(MatchupImages.Width / 2, 82));
        Assert.Equal(SKColors.Red, image.GetPixel(MatchupImages.Width / 4, MatchupImages.Height / 2));
    }

    [Fact]
    public void Leagues_WithEspnTeams_HaveLogosExceptRugby()
    {
        Assert.All(
            League.All.Where(l => l.EspnPath is not null && !l.EspnPath.StartsWith("rugby", StringComparison.Ordinal)),
            l => Assert.StartsWith("https://a.espncdn.com/i/", l.Logo, StringComparison.Ordinal));
    }

    [Fact]
    public void CricketNations_HaveLogos()
    {
        var cricket = League.BuiltInTeams.Where(t => t.League == "cricket" && t.ShortName != "Nepal").ToList();

        Assert.NotEmpty(cricket);
        Assert.All(cricket, t => Assert.StartsWith("https://a.espncdn.com/i/teamlogos/cricket/500/", t.Logo, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetAsync_DownloadsLogosOnceAndCachesTheCard()
    {
        var handler = new LogoHandler();
        using var images = Create(handler);
        var a = new Team("nhl", "Ducks", Array.Empty<string>(), Array.Empty<string>(), "http://espn.test/ana.png");
        var b = new Team("nhl", "Rangers", Array.Empty<string>(), Array.Empty<string>(), "http://espn.test/nyr.png");

        var first = await images.GetAsync(a, b, "http://espn.test/nhl.png", CancellationToken.None);
        var second = await images.GetAsync(a, b, "http://espn.test/nhl.png", CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.True(File.Exists(first));
        Assert.Equal(3, handler.Requests); // two teams + league
    }

    [Fact]
    public async Task GetAsync_TeamWithoutLogo_ReturnsNull()
    {
        using var images = Create(new LogoHandler());
        var gaa = League.BuiltInTeams.First();

        Assert.Null(await images.GetAsync(gaa, gaa, null, CancellationToken.None));
    }

    [Fact]
    public void ApplyImage_MatchupReplacesChannelLogo_AndRealImagesStay()
    {
        var program = new LiveTvProgram();
        var logo = new ItemImageInfo { Path = "/meta/channel.png", Type = ImageType.Primary };
        var card = new ItemImageInfo { Path = "/data/matchups/abc.png", Type = ImageType.Primary };

        Assert.True(ChannelRenamer.ApplyImage(program, logo));
        Assert.True(ChannelRenamer.ApplyImage(program, card));
        Assert.Equal(card.Path, program.GetImageInfo(ImageType.Primary, 0)!.Path);
        Assert.False(ChannelRenamer.ApplyImage(program, card));

        // Feature off: back to the channel logo.
        Assert.True(ChannelRenamer.ApplyImage(program, logo));
        Assert.Equal(logo.Path, program.GetImageInfo(ImageType.Primary, 0)!.Path);

        var real = new LiveTvProgram();
        real.SetImage(new ItemImageInfo { Path = "/epg/show.jpg", Type = ImageType.Primary }, 0);
        Assert.False(ChannelRenamer.ApplyImage(real, card));
        Assert.Equal("/epg/show.jpg", real.GetImageInfo(ImageType.Primary, 0)!.Path);
    }

    private static byte[] Png(SKColor color)
    {
        using var bitmap = new SKBitmap(100, 100);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private MatchupImages Create(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        return new MatchupImages(factory.Object, NullLogger<MatchupImages>.Instance, () => _dir);
    }

    private sealed class LogoHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png(SKColors.Green)) });
        }
    }
}
