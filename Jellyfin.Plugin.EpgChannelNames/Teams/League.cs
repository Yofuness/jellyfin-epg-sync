using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.EpgChannelNames.Teams;

/// <summary>
/// A league the team database knows.
/// </summary>
/// <param name="Key">Stable id, stored in the cache.</param>
/// <param name="Label">Shown in program titles, with the sport, e.g. "NHL Hockey", "Championship Soccer".</param>
/// <param name="EspnPath">ESPN site API path (sport/league), or null for a built-in team list.</param>
/// <param name="Keywords">Words in a title or description that name the league.</param>
/// <param name="KeywordRequired">Whether teams match only when a keyword is present (team names that are common words).</param>
/// <param name="ShortNicknames">Whether <see cref="Team.ShortName"/> is the nickname and the city is a weak alias (US leagues).</param>
/// <param name="Logo">League logo for matchup images (ESPN's dark-background version), or null.</param>
public sealed record League(string Key, string Label, string? EspnPath, IReadOnlyList<string> Keywords, bool KeywordRequired = false, bool ShortNicknames = false, string? Logo = null)
{
    /// <summary>
    /// Gets all leagues. Domestic leagues come first: when the same two teams play in several leagues and the text names
    /// none of them, the first one wins.
    /// </summary>
    /// <remarks>Logos are from sports.core.api.espn.com/v2/sports/{sport}/leagues/{league}; ESPN has none for the rugby leagues.</remarks>
    public static IReadOnlyList<League> All { get; } = new[]
    {
        new League("nhl", "NHL Hockey", "hockey/nhl", new[] { "NHL", "Ice Hockey" }, ShortNicknames: true, Logo: "https://a.espncdn.com/i/teamlogos/leagues/500-dark/nhl.png"),
        new League("nfl", "NFL Football", "football/nfl", new[] { "NFL", "American Football" }, ShortNicknames: true, Logo: "https://a.espncdn.com/i/teamlogos/leagues/500-dark/nfl.png"),
        new League("nba", "NBA Basketball", "basketball/nba", new[] { "NBA", "Basketball" }, ShortNicknames: true, Logo: "https://a.espncdn.com/i/teamlogos/leagues/500-dark/nba.png"),
        new League("mlb", "MLB Baseball", "baseball/mlb", new[] { "MLB", "Baseball" }, ShortNicknames: true, Logo: "https://a.espncdn.com/i/teamlogos/leagues/500-dark/mlb.png"),
        // MLS short names are cities ("Colorado", "Chicago") that also name NHL/NBA teams.
        new League("mls", "MLS Soccer", "soccer/usa.1", new[] { "MLS", "Major League Soccer" }, KeywordRequired: true, Logo: "https://a.espncdn.com/i/leaguelogos/soccer/500-dark/19.png"),
        new League("epl", "Premier League Soccer", "soccer/eng.1", new[] { "Premier League", "EPL" }, Logo: "https://a.espncdn.com/i/leaguelogos/soccer/500-dark/23.png"),
        new League("efl", "Championship Soccer", "soccer/eng.2", new[] { "EFL", "Championship" }, Logo: "https://a.espncdn.com/i/leaguelogos/soccer/500-dark/24.png"),
        new League("laliga", "LaLiga Soccer", "soccer/esp.1", new[] { "LaLiga", "La Liga" }, Logo: "https://a.espncdn.com/i/leaguelogos/soccer/500-dark/15.png"),
        new League("ucl", "Champions League Soccer", "soccer/uefa.champions", new[] { "Champions League", "UCL" }, Logo: "https://a.espncdn.com/i/leaguelogos/soccer/500-dark/2.png"),
        new League("prem-rugby", "Premiership Rugby", "rugby/267979", new[] { "Premiership Rugby", "Prem Rugby", "Gallagher" }),
        new League("urc", "URC Rugby", "rugby/270557", new[] { "URC", "United Rugby Championship" }),
        new League("champions-cup", "Champions Cup Rugby", "rugby/271937", new[] { "Champions Cup" }),
        new League("gaa", "GAA", null, new[] { "GAA", "Hurling", "Gaelic", "Camogie", "All-Ireland" }, KeywordRequired: true),

        // National teams; country names appear in football and rugby too, so "Cricket" must be in the text.
        new League("cricket", "Cricket", null, new[] { "Cricket" }, KeywordRequired: true),
    };

    /// <summary>
    /// Gets the teams of the leagues ESPN doesn't cover: GAA counties and cricket nations. They don't change.
    /// </summary>
    public static IReadOnlyList<Team> BuiltInTeams { get; } = new[]
    {
        "Antrim", "Armagh", "Carlow", "Cavan", "Clare", "Cork", "Derry", "Donegal", "Down", "Dublin", "Fermanagh",
        "Galway", "Kerry", "Kildare", "Kilkenny", "Laois", "Leitrim", "Limerick", "Longford", "Louth", "Mayo",
        "Meath", "Monaghan", "Offaly", "Roscommon", "Sligo", "Tipperary", "Tyrone", "Waterford", "Westmeath",
        "Wexford", "Wicklow"
    }.Select(c => new Team("gaa", c, new[] { c }, System.Array.Empty<string>(), null))
    .Concat(new[]
    {
        "England", "Australia", "India", "South Africa", "New Zealand", "Pakistan", "Sri Lanka", "West Indies",
        "Bangladesh", "Zimbabwe", "Afghanistan", "Ireland", "Scotland", "Netherlands", "Nepal", "UAE", "USA",
        "Oman", "Namibia"
    }.Select(c => new Team("cricket", c, new[] { c }, System.Array.Empty<string>(), null)))
    .ToArray();
}
