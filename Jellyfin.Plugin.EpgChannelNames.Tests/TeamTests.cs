using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.EpgChannelNames.Teams;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EpgChannelNames.Tests;

public sealed class TeamTests : IDisposable
{
    private static readonly IReadOnlyList<Team> Teams = BuildTeams();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "epg-teams-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Theory]
    // Real EPG data from the user's server.
    [InlineData("Live NHL Ice Hockey", "Philadelphia Flyers v Colorado Avalanche. Coverage from the Wells Fargo Center.", "Live NHL: Flyers vs Avalanche")]
    [InlineData("Live NHL Ice Hockey", "Philadelphia Flyers v Colorado", "Live NHL: Flyers vs Avalanche")]
    [InlineData("EFL Championship", "Wolves play host to Portsmouth at Molineux in the Sky Bet Championship.", "Championship: Wolves vs Portsmouth")]
    [InlineData("Live Premier League", "Manchester United v Manchester City from Old Trafford.", "Live Premier League: Man United vs Man City")]
    [InlineData("Live Champions Cup Rugby", "Bath Rugby host Leinster at the Rec.", "Live Champions Cup: Bath Rugby vs Leinster")]
    [InlineData("Live GAA", "Kerry v Dublin in the All-Ireland final.", "Live GAA: Kerry vs Dublin")]
    [InlineData("LaLiga", "Deportivo Alaves v Real Madrid", "LaLiga: Alavés vs Real Madrid")]
    public void BuildTitle_FindsBothTeams(string title, string overview, string expected)
    {
        Assert.Equal(expected, TeamMatcher.BuildTitle(title, overview, false, Teams));
    }

    [Theory]
    [InlineData("Live NHL Ice Hockey", "Coverage of the Philadelphia Flyers.")] // one team
    [InlineData("Live NHL Ice Hockey", "Flyers v Avalanche, after the Rangers beat the Kings.")] // four teams
    [InlineData("Live Sport", "Philadelphia Flyers v Manchester City")] // two leagues
    [InlineData("Hope for Wildlife", "A release at the birthplace of hockey goes offside.")]
    [InlineData("Dublin Ireland", "Kerry and Dublin scenery.")] // GAA needs a GAA keyword
    [InlineData("Live NHL Ice Hockey", "New York v Philadelphia Flyers")] // "New York" is two NHL teams
    public void BuildTitle_LeavesOthersAlone(string title, string overview)
    {
        Assert.Null(TeamMatcher.BuildTitle(title, overview, true, Teams));
    }

    [Theory]
    [InlineData("Test Cricket", "Day three of the second Test between South Africa and Australia at St George's Park in Gqeberha.", "Test Cricket: South Africa vs Australia · Day 3")]
    [InlineData("Live ODI Cricket", "England v India from Lord's.", "ODI Cricket: England vs India")]
    [InlineData("Cricket", "Day 2 of the T20 World Cup match between Pakistan and New Zealand.", "T20 Cricket: Pakistan vs New Zealand · Day 2")]
    public void BuildTitle_Cricket(string title, string overview, string expected)
    {
        Assert.Equal(expected, TeamMatcher.BuildTitle(title, overview, true, Teams));
    }

    [Fact]
    public void BuildTitle_CountriesWithoutCricket_AreNotCricket()
    {
        Assert.Null(TeamMatcher.BuildTitle("Six Nations Rugby", "England v Ireland at Twickenham.", true, Teams));
    }

    [Theory]
    // Real EPG data from the user's server.
    [InlineData("Formula 1 Motor Racing", "The 2018 United States Grand Prix arrives from the Circuit of the Americas in Texas. Lewis Hamilton has won this race a record six times.", "F1: United States GP 2018")]
    [InlineData("Formula 1", "It's lights out for the Bahrain Grand Prix in Malaysia. Oscar Piastri was the previous Bahrain race winner.", "F1: Bahrain GP")]
    [InlineData("Formula 1", "A round-up of the leading stories and features with Sky's Formula 1 presenters during the 2026 Hungarian Grand Prix.", "F1: Hungarian GP · Highlights")]
    [InlineData("Formula 1 Motor Racing", "The United States Grand Prix from the Circuit of The Americas in Austin, Texas. Lewis Hamilton was victorious here last year, winning from pole position.", "F1: United States GP")]
    [InlineData("F1 Qualifying", "Live coverage from the Monaco Grand Prix.", "F1: Monaco GP · Qualifying")]
    [InlineData("Formula 1", "Practice 2 for the São Paulo Grand Prix at Interlagos.", "F1: São Paulo GP · Practice 2")]
    [InlineData("Formula 1", "The Abu Dhabi Grand Prix sprint shootout.", "F1: Abu Dhabi GP · Sprint Qualifying")]
    public void Motorsport_BuildsGrandPrixTitles(string title, string overview, string expected)
    {
        Assert.Equal(expected, MotorsportMatcher.BuildTitle(title, overview, 2026));
    }

    [Theory]
    [InlineData("Formula 1", "Featuring all the action and highlights from Formula 1 racing.")]
    [InlineData("MotoGP", "The Japanese Grand Prix from Motegi.")]
    public void Motorsport_LeavesOthersAlone(string title, string overview)
    {
        Assert.Null(MotorsportMatcher.BuildTitle(title, overview, 2026));
    }

    [Fact]
    public void IsRepeat_BackToBackSameTitleOnly()
    {
        var start = new DateTime(2026, 10, 20, 16, 0, 0, DateTimeKind.Utc);
        LiveTvProgram Block(string name, int fromHour, int toHour)
            => new() { Name = name, StartDate = start.AddHours(fromHour), EndDate = start.AddHours(toHour) };
        var tennis = Block("Live Tennis", 0, 1);

        Assert.False(ChannelRenamer.IsRepeat(null, tennis));
        Assert.True(ChannelRenamer.IsRepeat(tennis, Block("Live Tennis", 1, 2)));
        Assert.False(ChannelRenamer.IsRepeat(tennis, Block("Live Tennis", 3, 4))); // something else aired in between
        Assert.False(ChannelRenamer.IsRepeat(tennis, Block("Formula 1", 1, 2)));
    }

    [Fact]
    public void BuildTitle_NoLeagueNamed_NeedsASportsProgram()
    {
        Assert.Null(TeamMatcher.BuildTitle("Match of the Day", "Arsenal v Chelsea", false, Teams));
        Assert.Equal("Premier League: Arsenal vs Chelsea", TeamMatcher.BuildTitle("Match of the Day", "Arsenal v Chelsea", true, Teams));
    }

    [Fact]
    public void BuildTitle_SameTeamsInTwoLeagues_PrefersTheNamedOne()
    {
        Assert.Equal("Champions League: Arsenal vs Real Madrid", TeamMatcher.BuildTitle("Champions League", "Arsenal v Real Madrid", false, Teams));
        Assert.Equal("Premier League: Arsenal vs Chelsea", TeamMatcher.BuildTitle("Football", "Arsenal v Chelsea", true, Teams));
    }

    [Fact]
    public void ApplyTeamTitle_IsReversibleAndFollowsGuideRefresh()
    {
        var program = new LiveTvProgram { Name = "Live NHL Ice Hockey", Overview = "Philadelphia Flyers v Colorado Avalanche" };

        Assert.True(ChannelRenamer.ApplyTeamTitle(program, Teams, false));
        Assert.Equal("Live NHL: Flyers vs Avalanche", program.Name);
        Assert.False(ChannelRenamer.ApplyTeamTitle(program, Teams, false)); // second run: no change

        // Guide refresh: new EPG title and description.
        program.Name = "Live NHL Ice Hockey";
        program.Overview = "Edmonton Oilers v Calgary Flames";
        Assert.True(ChannelRenamer.ApplyTeamTitle(program, Teams, false));
        Assert.Equal("Live NHL: Oilers vs Flames", program.Name);

        // Feature turned off: the EPG title comes back.
        Assert.True(ChannelRenamer.ApplyTeamTitle(program, null, false));
        Assert.Equal("Live NHL Ice Hockey", program.Name);
        Assert.DoesNotContain(program.ProviderIds.Keys, k => k.StartsWith("EpgChannelNames.Title", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Database_DownloadsOnceAndCaches()
    {
        var handler = new EspnHandler();
        using var db = CreateDb(handler);

        var teams = await db.GetTeamsAsync(CancellationToken.None);
        await db.GetTeamsAsync(CancellationToken.None);

        Assert.Contains(teams, t => t.ShortName == "Flyers");
        Assert.Contains(teams, t => t.ShortName == "Kerry"); // built-in GAA
        Assert.Equal(League.All.Count(l => l.EspnPath is not null), handler.Requests);
        Assert.True(File.Exists(Path.Combine(_dir, "teams.json")));

        // A new instance (Jellyfin restart) reads the cache without downloading.
        using var restarted = CreateDb(handler);
        Assert.Contains(await restarted.GetTeamsAsync(CancellationToken.None), t => t.ShortName == "Flyers");
        Assert.Equal(League.All.Count(l => l.EspnPath is not null), handler.Requests);
    }

    [Fact]
    public async Task Database_RefreshFailure_KeepsCachedTeams()
    {
        using (var db = CreateDb(new EspnHandler()))
        {
            await db.RefreshAsync(CancellationToken.None);
        }

        using var offline = CreateDb(new EspnHandler { Down = true });
        await offline.RefreshAsync(CancellationToken.None);

        Assert.Contains(await offline.GetTeamsAsync(CancellationToken.None), t => t.ShortName == "Flyers");
    }

    [Fact]
    public async Task Database_Offline_ReturnsBuiltInTeamsOnly()
    {
        using var db = CreateDb(new EspnHandler { Down = true });

        var teams = await db.GetTeamsAsync(CancellationToken.None);

        Assert.All(teams, t => Assert.Contains(t.League, new[] { "gaa", "cricket" }));
    }

    [Fact]
    public void ToTeam_UsesReadableShortNames()
    {
        var epl = League.All.Single(l => l.Key == "epl");
        Assert.Equal("Crystal Palace", TeamDatabase.ToTeam(epl, "Crystal Palace", "C Palace", "Crystal Palace", null, null).ShortName);
        Assert.Contains("Bath", TeamDatabase.ToTeam(epl, "Bath Rugby", "Bath Rugby", "Bath Rugby", null, null).Aliases);
    }

    private static IReadOnlyList<Team> BuildTeams()
    {
        League L(string key) => League.All.Single(l => l.Key == key);
        var nhl = L("nhl");
        var raw = new List<Team>
        {
            TeamDatabase.ToTeam(nhl, "Philadelphia Flyers", "Flyers", "Flyers", "Philadelphia", null),
            TeamDatabase.ToTeam(nhl, "Colorado Avalanche", "Avalanche", "Avalanche", "Colorado", null),
            TeamDatabase.ToTeam(nhl, "Edmonton Oilers", "Oilers", "Oilers", "Edmonton", null),
            TeamDatabase.ToTeam(nhl, "Calgary Flames", "Flames", "Flames", "Calgary", null),
            TeamDatabase.ToTeam(nhl, "New York Rangers", "Rangers", "Rangers", "New York", null),
            TeamDatabase.ToTeam(nhl, "New York Islanders", "Islanders", "Islanders", "New York", null),
            TeamDatabase.ToTeam(nhl, "Los Angeles Kings", "Kings", "Kings", "Los Angeles", null),
            TeamDatabase.ToTeam(L("mls"), "Colorado Rapids", "Colorado", "Colorado Rapids", "Colorado Rapids", null),
            TeamDatabase.ToTeam(L("efl"), "Wolverhampton Wanderers", "Wolves", "Wolverhampton Wanderers", null, null),
            TeamDatabase.ToTeam(L("efl"), "Portsmouth", "Portsmouth", "Portsmouth", null, null),
            TeamDatabase.ToTeam(L("epl"), "Manchester United", "Man United", "Manchester United", null, null),
            TeamDatabase.ToTeam(L("epl"), "Manchester City", "Man City", "Manchester City", null, null),
            TeamDatabase.ToTeam(L("epl"), "Arsenal", "Arsenal", "Arsenal", null, null),
            TeamDatabase.ToTeam(L("epl"), "Chelsea", "Chelsea", "Chelsea", null, null),
            TeamDatabase.ToTeam(L("ucl"), "Arsenal", "Arsenal", "Arsenal", null, null),
            TeamDatabase.ToTeam(L("ucl"), "Real Madrid", "Real Madrid", "Real Madrid", null, null),
            TeamDatabase.ToTeam(L("laliga"), "Alavés", "Alavés", "Alavés", null, null),
            TeamDatabase.ToTeam(L("laliga"), "Real Madrid", "Real Madrid", "Real Madrid", null, null),
            TeamDatabase.ToTeam(L("prem-rugby"), "Bath Rugby", "Bath Rugby", "Bath Rugby", null, null),
            TeamDatabase.ToTeam(L("champions-cup"), "Bath Rugby", "Bath Rugby", "Bath Rugby", null, null),
            TeamDatabase.ToTeam(L("champions-cup"), "Leinster", "Leinster", "Leinster", null, null),
        };
        return TeamDatabase.DropSharedWeakAliases(raw).Concat(League.BuiltInTeams).ToList();
    }

    private TeamDatabase CreateDb(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        return new TeamDatabase(factory.Object, NullLogger<TeamDatabase>.Instance, () => _dir, () => "http://espn.test/sports/");
    }

    // Same shape as site.api.espn.com/apis/site/v2/sports/{sport}/{league}/teams.
    private sealed class EspnHandler : HttpMessageHandler
    {
        public bool Down { get; set; }

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Down)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            const string Json = """
                {"sports":[{"leagues":[{"teams":[
                  {"team":{"displayName":"Philadelphia Flyers","shortDisplayName":"Flyers","name":"Flyers","location":"Philadelphia","logos":[{"href":"https://a.espncdn.com/phi.png"}]}},
                  {"team":{"displayName":"Colorado Avalanche","shortDisplayName":"Avalanche","name":"Avalanche","location":"Colorado"}}
                ]}]}]}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json, Encoding.UTF8, "application/json") });
        }
    }
}
