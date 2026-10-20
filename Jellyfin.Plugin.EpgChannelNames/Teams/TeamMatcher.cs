using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.EpgChannelNames.Teams;

/// <summary>
/// Finds the two teams of a sports program in its title and description.
/// </summary>
public static partial class TeamMatcher
{
    /// <summary>
    /// Builds a program title like "Live NHL Hockey: Flyers vs Avalanche" from the teams named in the title and description.
    /// </summary>
    /// <param name="title">Current program title, e.g. "Live NHL Ice Hockey".</param>
    /// <param name="overview">Program description, e.g. "Philadelphia Flyers v Colorado Avalanche".</param>
    /// <param name="isSports">Whether the program or its channel is known to be sports. Otherwise the text must name a league.</param>
    /// <param name="teams">The team database.</param>
    /// <returns>The new title, or null unless exactly two teams of one league are found.</returns>
    public static string? BuildTitle(string? title, string? overview, bool isSports, IReadOnlyList<Team> teams)
    {
        var text = Normalize(title + "\n" + overview);
        var named = League.All.Where(l => l.Keywords.Any(k => FindAll(text, Normalize(k)).Any())).Select(l => l.Key).ToHashSet();
        if (!isSports && named.Count == 0)
        {
            return null;
        }

        var required = League.All.Where(l => l.KeywordRequired).Select(l => l.Key).ToHashSet();
        var pool = new Dictionary<string, List<Team>>();
        foreach (var team in teams.Where(t => !required.Contains(t.League) || named.Contains(t.League)))
        {
            var aliases = named.Contains(team.League) ? team.Aliases.Concat(team.WeakAliases) : team.Aliases;
            foreach (var alias in aliases.Select(Normalize).Where(a => a.Length > 0).Distinct())
            {
                if (!pool.TryGetValue(alias, out var list))
                {
                    pool[alias] = list = new List<Team>();
                }

                list.Add(team);
            }
        }

        // Longest names first, so "Manchester United" is found before "United" can claim a part of it.
        var taken = new bool[text.Length];
        var found = new List<(int Position, List<Team> Teams)>();
        foreach (var (alias, owners) in pool.OrderByDescending(p => p.Key.Length))
        {
            foreach (var position in FindAll(text, alias))
            {
                if (taken.AsSpan(position, alias.Length).Contains(true))
                {
                    continue;
                }

                taken.AsSpan(position, alias.Length).Fill(true);
                found.Add((position, owners));
            }
        }

        var ordered = found.OrderBy(f => f.Position).ToList();
        var candidates = League.All
            .Select(l => (League: l, Teams: ordered.SelectMany(f => f.Teams.Where(t => t.League == l.Key)).Distinct().ToList()))
            .Where(c => c.Teams.Count == 2)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var (league, pair) = candidates.FirstOrDefault(c => named.Contains(c.League.Key), candidates[0]);
        if (league.Key == "cricket")
        {
            return $"{CricketFormat(text)}: {pair[0].ShortName} vs {pair[1].ShortName}{CricketDay(text)}";
        }

        var label = league.Key == "gaa" ? GaaLabel(text) : league.Label;
        var live = title is not null && title.TrimStart().StartsWith("Live ", StringComparison.OrdinalIgnoreCase) ? "Live " : string.Empty;
        return $"{live}{label}: {pair[0].ShortName} vs {pair[1].ShortName}";
    }

    // "Test Cricket", "ODI Cricket", "T20 Cricket" or "Cricket", from normalized text.
    private static string CricketFormat(string text)
        => TestRegex().IsMatch(text) ? "Test Cricket"
            : OdiRegex().IsMatch(text) ? "ODI Cricket"
            : T20Regex().IsMatch(text) ? "T20 Cricket"
            : "Cricket";

    // GAA counties play both codes; the text says which.
    private static string GaaLabel(string text)
        => HurlingRegex().IsMatch(text) ? "GAA Hurling" : GaelicFootballRegex().IsMatch(text) ? "GAA Football" : "GAA";

    [GeneratedRegex(@"\b(hurling|camogie)\b")]
    private static partial Regex HurlingRegex();

    [GeneratedRegex(@"\bfootball\b")]
    private static partial Regex GaelicFootballRegex();

    // " · Day 3" for "day three" / "day 3" in normalized text.
    private static string CricketDay(string text)
    {
        var match = DayRegex().Match(text);
        if (!match.Success)
        {
            return string.Empty;
        }

        var day = match.Groups[1].Value;
        var number = Array.IndexOf(new[] { "one", "two", "three", "four", "five" }, day) is var i and >= 0 ? (i + 1).ToString(CultureInfo.InvariantCulture) : day;
        return " · Day " + number;
    }

    [GeneratedRegex(@"\btests?\b")]
    private static partial Regex TestRegex();

    [GeneratedRegex(@"\b(odi|one-day|one day)\b")]
    private static partial Regex OdiRegex();

    [GeneratedRegex(@"\b(t20|twenty20)\b")]
    private static partial Regex T20Regex();

    [GeneratedRegex(@"\bday (one|two|three|four|five|[1-5])\b")]
    private static partial Regex DayRegex();

    /// <summary>
    /// Lowercases and removes accents, so "Alavés" matches "ALAVES".
    /// </summary>
    /// <param name="text">Text to normalize.</param>
    /// <returns>The normalized text.</returns>
    internal static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        // FormD can split one character into several; recompose so lengths match the text being searched.
        return builder.ToString().Normalize(NormalizationForm.FormC).Trim();
    }

    // Whole-word matches only: "Bath" must not match "Bathgate".
    private static IEnumerable<int> FindAll(string text, string word)
    {
        if (word.Length == 0)
        {
            yield break;
        }

        var start = 0;
        while ((start = text.IndexOf(word, start, StringComparison.Ordinal)) >= 0)
        {
            var end = start + word.Length;
            if ((start == 0 || !char.IsLetterOrDigit(text[start - 1])) && (end == text.Length || !char.IsLetterOrDigit(text[end])))
            {
                yield return start;
            }

            start++;
        }
    }
}
