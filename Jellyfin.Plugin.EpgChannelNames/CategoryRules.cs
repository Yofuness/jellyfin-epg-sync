using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.EpgChannelNames.Dispatcharr;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.Plugin.EpgChannelNames;

/// <summary>
/// Programs-page categories a Dispatcharr group can be mapped to.
/// </summary>
public enum ProgramCategory
{
    /// <summary>Shows row (series).</summary>
    Shows,

    /// <summary>Movies row.</summary>
    Movies,

    /// <summary>Sports row.</summary>
    Sports,

    /// <summary>News row.</summary>
    News,

    /// <summary>Kids row.</summary>
    Kids
}

/// <summary>
/// "Dispatcharr group = Category" rules, and applying a category to a program reversibly.
/// </summary>
public sealed partial class CategoryRules
{
    // What the plugin itself changed on a program, so a removed or changed rule can be undone
    // without touching flags that came from the EPG. Comma-separated actions: "series", "movie", "tag:Sports".
    internal const string MarkerKey = "EpgChannelNames.Category";

    private readonly Dictionary<string, ProgramCategory?> _byGroup;

    private CategoryRules(Dictionary<string, ProgramCategory?> byGroup, IReadOnlyList<string> invalidLines)
    {
        _byGroup = byGroup;
        InvalidLines = invalidLines;
    }

    /// <summary>
    /// Gets the lines that could not be parsed.
    /// </summary>
    public IReadOnlyList<string> InvalidLines { get; }

    /// <summary>
    /// Gets the group names that have a rule (normalized).
    /// </summary>
    public IEnumerable<string> Groups => _byGroup.Keys;

    /// <summary>
    /// Parses rules, one per line: "group = Category". Blank lines and lines starting with # are ignored.
    /// The group is everything before the last "=", so group names may contain "|".
    /// </summary>
    /// <param name="text">Rules text.</param>
    /// <returns>The parsed rules.</returns>
    public static CategoryRules Parse(string? text)
    {
        var byGroup = new Dictionary<string, ProgramCategory?>(StringComparer.OrdinalIgnoreCase);
        var invalid = new List<string>();
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var eq = line.LastIndexOf('=');
            var group = eq > 0 ? DispatcharrClient.NormalizeGroupName(line[..eq]) : string.Empty;
            if (group.Length > 0 && line[(eq + 1)..].Trim().Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                byGroup[group] = null;
                continue;
            }

            if (group.Length == 0 || !Enum.TryParse<ProgramCategory>(line[(eq + 1)..].Trim(), true, out var category) || !Enum.IsDefined(category))
            {
                invalid.Add(line);
                continue;
            }

            byGroup[group] = category;
        }

        return new CategoryRules(byGroup, invalid);
    }

    /// <summary>
    /// Gets the category for a Dispatcharr group, if a rule exists.
    /// </summary>
    /// <param name="group">Normalized group name.</param>
    /// <returns>The category, or null.</returns>
    public ProgramCategory? For(string? group)
        => group is not null && _byGroup.TryGetValue(group, out var c) ? c : null;

    /// <summary>
    /// Gets the category for a Dispatcharr group: its rule if there is one ("= None" means no row), otherwise
    /// a guess from the group name when <paramref name="guess"/> is on.
    /// </summary>
    /// <param name="group">Normalized group name.</param>
    /// <param name="guess">Whether to guess for groups without a rule.</param>
    /// <returns>The category, or null.</returns>
    public ProgramCategory? Resolve(string? group, bool guess)
        => group is not null && _byGroup.TryGetValue(group, out var c) ? c : guess ? Guess(group) : null;

    /// <summary>
    /// Guesses a category from words in a group name, e.g. "EU | UK | SKY SPORTS+" is Sports.
    /// </summary>
    /// <param name="group">Group name.</param>
    /// <returns>The category, or null when no word matches.</returns>
    public static ProgramCategory? Guess(string? group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return null;
        }

        // Kids and News first: "KIDS MOVIES" is Kids, "SPORTS NEWS" is News.
        return KidsRegex().IsMatch(group) ? ProgramCategory.Kids
            : NewsRegex().IsMatch(group) ? ProgramCategory.News
            : SportsRegex().IsMatch(group) ? ProgramCategory.Sports
            : MoviesRegex().IsMatch(group) ? ProgramCategory.Movies
            : ShowsRegex().IsMatch(group) ? ProgramCategory.Shows
            : null;
    }

    /// <summary>
    /// Makes <paramref name="program"/> carry exactly <paramref name="category"/> as far as the plugin is concerned:
    /// undoes what the plugin set earlier for another category, then applies this one. Flags that were already set
    /// (e.g. by the EPG) are left alone and never undone.
    /// </summary>
    /// <param name="program">The program.</param>
    /// <param name="category">The wanted category, or null to undo only.</param>
    /// <returns>True when the program changed.</returns>
    public static bool Apply(LiveTvProgram program, ProgramCategory? category)
    {
        var before = Snapshot(program);
        var done = program.ProviderIds.TryGetValue(MarkerKey, out var m)
            ? m.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var wanted = category is { } c ? new HashSet<string>(StringComparer.Ordinal) { ActionFor(c) } : new HashSet<string>(StringComparer.Ordinal);

        foreach (var action in done.Except(wanted).ToList())
        {
            Undo(program, action);
            done.Remove(action);
        }

        foreach (var action in wanted)
        {
            if (Do(program, action))
            {
                done.Add(action);
            }
        }

        if (done.Count > 0)
        {
            program.ProviderIds[MarkerKey] = string.Join(',', done.Order(StringComparer.Ordinal));
        }
        else
        {
            program.ProviderIds.Remove(MarkerKey);
        }

        return before != Snapshot(program);
    }

    private static string ActionFor(ProgramCategory category) => category switch
    {
        ProgramCategory.Shows => "series",
        ProgramCategory.Movies => "movie",
        _ => "tag:" + category
    };

    // Returns true when the plugin is (now or already) responsible for this action.
    private static bool Do(LiveTvProgram program, string action)
    {
        var alreadyOurs = program.ProviderIds.TryGetValue(MarkerKey, out var m)
            && m.Split(',').Contains(action, StringComparer.Ordinal);
        switch (action)
        {
            case "series":
                if (program.IsSeries && !alreadyOurs)
                {
                    return false;
                }

                program.IsSeries = true;
                return true;
            case "movie":
                if (program.IsMovie && !alreadyOurs)
                {
                    return false;
                }

                program.IsMovie = true;
                return true;
            default:
                var tag = action["tag:".Length..];
                if (program.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    return alreadyOurs;
                }

                program.Tags = program.Tags.Append(tag).ToArray();
                return true;
        }
    }

    private static void Undo(LiveTvProgram program, string action)
    {
        switch (action)
        {
            case "series":
                program.IsSeries = false;
                break;
            case "movie":
                program.IsMovie = false;
                break;
            default:
                var tag = action["tag:".Length..];
                program.Tags = program.Tags.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).ToArray();
                break;
        }
    }

    private static string Snapshot(LiveTvProgram p)
        => string.Join(
            '|',
            p.IsSeries,
            p.IsMovie,
            string.Join(',', p.Tags),
            p.ProviderIds.GetValueOrDefault(MarkerKey) ?? string.Empty);

    [GeneratedRegex(@"\b(KIDS?|CHILDREN|CARTOONS?|JUNIOR|TODDLERS?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex KidsRegex();

    [GeneratedRegex(@"\bNEWS\b", RegexOptions.IgnoreCase)]
    private static partial Regex NewsRegex();

    [GeneratedRegex(@"\b(SPORTS?|NHL|NFL|NBA|MLB|MLS|PPV|GAA|F1|FORMULA|FOOTBALL|SOCCER|RUGBY|HOCKEY|CRICKET|GOLF|TENNIS|BOXING|UFC|WWE|RACING|ESPN|TSN|DAZN|EPL)", RegexOptions.IgnoreCase)]
    private static partial Regex SportsRegex();

    [GeneratedRegex(@"\b(MOVIES?|CINEMA|FILMS?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MoviesRegex();

    [GeneratedRegex(@"\b(DOCUMENTAR(Y|IES)|ENTERTAINMENT|SERIES|SHOWS?|LIFESTYLE|NATURE|HISTORY|REALITY|COMEDY|DRAMA|GENERAL)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ShowsRegex();
}
