using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.EpgChannelNames.Teams;

/// <summary>
/// Builds Formula 1 program titles from the Grand Prix named in the title or description.
/// </summary>
public static partial class MotorsportMatcher
{
    /// <summary>
    /// Builds a title like "F1: United States GP 2018 · Qualifying".
    /// </summary>
    /// <param name="title">Current program title, e.g. "Formula 1 Motor Racing".</param>
    /// <param name="overview">Program description, e.g. "The 2018 United States Grand Prix arrives from…".</param>
    /// <param name="currentYear">This season; the year is shown only for other seasons (replays).</param>
    /// <returns>The new title, or null when the program is not F1 or no Grand Prix is named.</returns>
    public static string? BuildTitle(string? title, string? overview, int currentYear)
    {
        var text = title + "\n" + overview;
        if (!F1Regex().IsMatch(text))
        {
            return null;
        }

        var gp = GrandPrixRegex().Match(title ?? string.Empty) is { Success: true } inTitle ? inTitle : GrandPrixRegex().Match(overview ?? string.Empty);
        if (!gp.Success)
        {
            return null;
        }

        var name = LeadingTheRegex().Replace(gp.Groups["name"].Value.Trim(), string.Empty);
        var year = gp.Groups["year"].Success && int.Parse(gp.Groups["year"].Value, CultureInfo.InvariantCulture) != currentYear
            ? " " + gp.Groups["year"].Value
            : string.Empty;
        return $"F1: {name} GP{year}{Session(title, overview)}";
    }

    // The session from the title, else the description's first sentence (later sentences talk about past races).
    private static string Session(string? title, string? overview)
    {
        var firstSentence = (overview ?? string.Empty).Split('.', 2)[0];
        foreach (var source in new[] { title ?? string.Empty, firstSentence })
        {
            if (RoundUpRegex().IsMatch(source))
            {
                return " · Highlights";
            }

            if (PreviewRegex().IsMatch(source))
            {
                return " · Preview";
            }

            if (PracticeRegex().Match(source) is { Success: true } practice)
            {
                return " · Practice" + (practice.Groups[1].Success ? " " + practice.Groups[1].Value : string.Empty);
            }

            if (SprintQualifyingRegex().IsMatch(source))
            {
                return " · Sprint Qualifying";
            }

            if (SprintRegex().IsMatch(source))
            {
                return " · Sprint";
            }

            if (QualifyingRegex().IsMatch(source))
            {
                return " · Qualifying";
            }
        }

        return string.Empty;
    }

    [GeneratedRegex(@"\b(Formula (1|One)|F1)\b", RegexOptions.IgnoreCase)]
    private static partial Regex F1Regex();

    // Up to three capitalized words before "Grand Prix": "United States", "Abu Dhabi", "São Paulo", "Hungarian".
    [GeneratedRegex(@"(?:(?<year>(?:19|20)\d\d)\s+)?(?<name>(?:\p{Lu}[\p{L}'.-]*\s+){1,3})Grand Prix")]
    private static partial Regex GrandPrixRegex();

    [GeneratedRegex(@"^(The|At|For|In)\s+")]
    private static partial Regex LeadingTheRegex();

    [GeneratedRegex(@"\b(round-?up|highlights|review)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RoundUpRegex();

    [GeneratedRegex(@"\bpreview\b", RegexOptions.IgnoreCase)]
    private static partial Regex PreviewRegex();

    [GeneratedRegex(@"\b(?:practice|FP)\s*([1-3])?\b", RegexOptions.IgnoreCase)]
    private static partial Regex PracticeRegex();

    [GeneratedRegex(@"\bsprint (qualifying|shootout)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SprintQualifyingRegex();

    [GeneratedRegex(@"\bsprint\b", RegexOptions.IgnoreCase)]
    private static partial Regex SprintRegex();

    [GeneratedRegex(@"\bqualifying\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualifyingRegex();
}
