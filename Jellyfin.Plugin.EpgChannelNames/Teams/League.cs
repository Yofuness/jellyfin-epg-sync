using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.EpgChannelNames.Teams;

/// <summary>
/// A league the team database knows.
/// </summary>
/// <param name="Key">Stable id, stored in the cache.</param>
/// <param name="Label">Shown in program titles, e.g. "NHL".</param>
/// <param name="EspnPath">ESPN site API path (sport/league), or null for a built-in team list.</param>
/// <param name="Keywords">Words in a title or description that name the league.</param>
/// <param name="KeywordRequired">Whether teams match only when a keyword is present (team names that are common words).</param>
/// <param name="ShortNicknames">Whether <see cref="Team.ShortName"/> is the nickname and the city is a weak alias (US leagues).</param>
public sealed record League(string Key, string Label, string? EspnPath, IReadOnlyList<string> Keywords, bool KeywordRequired = false, bool ShortNicknames = false)
{
    /// <summary>
    /// Gets all leagues. Domestic leagues come first: when the same two teams play in several leagues and the text names
    /// none of them, the first one wins.
    /// </summary>
    public static IReadOnlyList<League> All { get; } = new[]
    {
        new League("nhl", "NHL", "hockey/nhl", new[] { "NHL", "Ice Hockey" }, ShortNicknames: true),
        new League("nfl", "NFL", "football/nfl", new[] { "NFL", "American Football" }, ShortNicknames: true),
        new League("nba", "NBA", "basketball/nba", new[] { "NBA", "Basketball" }, ShortNicknames: true),
        new League("mlb", "MLB", "baseball/mlb", new[] { "MLB", "Baseball" }, ShortNicknames: true),
        // MLS short names are cities ("Colorado", "Chicago") that also name NHL/NBA teams.
        new League("mls", "MLS", "soccer/usa.1", new[] { "MLS", "Major League Soccer" }, KeywordRequired: true),
        new League("epl", "Premier League", "soccer/eng.1", new[] { "Premier League", "EPL" }),
        new League("efl", "Championship", "soccer/eng.2", new[] { "EFL", "Championship" }),
        new League("laliga", "LaLiga", "soccer/esp.1", new[] { "LaLiga", "La Liga" }),
        new League("ucl", "Champions League", "soccer/uefa.champions", new[] { "Champions League", "UCL" }),
        new League("prem-rugby", "Premiership Rugby", "rugby/267979", new[] { "Premiership Rugby", "Prem Rugby", "Gallagher" }),
        new League("urc", "URC", "rugby/270557", new[] { "URC", "United Rugby Championship" }),
        new League("champions-cup", "Champions Cup", "rugby/271937", new[] { "Champions Cup" }),
        new League("gaa", "GAA", null, new[] { "GAA", "Hurling", "Gaelic", "Camogie", "All-Ireland" }, KeywordRequired: true),
    };

    /// <summary>
    /// Gets the built-in GAA county teams. ESPN does not cover GAA, and the 32 counties don't change.
    /// </summary>
    public static IReadOnlyList<Team> GaaCounties { get; } = new[]
    {
        "Antrim", "Armagh", "Carlow", "Cavan", "Clare", "Cork", "Derry", "Donegal", "Down", "Dublin", "Fermanagh",
        "Galway", "Kerry", "Kildare", "Kilkenny", "Laois", "Leitrim", "Limerick", "Longford", "Louth", "Mayo",
        "Meath", "Monaghan", "Offaly", "Roscommon", "Sligo", "Tipperary", "Tyrone", "Waterford", "Westmeath",
        "Wexford", "Wicklow"
    }.Select(c => new Team("gaa", c, new[] { c }, System.Array.Empty<string>(), null)).ToArray();
}
