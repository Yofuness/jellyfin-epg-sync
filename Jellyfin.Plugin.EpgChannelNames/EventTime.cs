using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.EpgChannelNames;

/// <summary>
/// Reads the start time providers put in event titles, e.g. "SHARKS @ LEAFS OCT 19 – 7:30 PM ET".
/// </summary>
public static partial class EventTime
{
    /// <summary>
    /// Parses the first "MON DD – H:MM AM/PM ZONE" in <paramref name="text"/>. The year is not in the title, so the
    /// candidate closest to <paramref name="now"/> is used (handles December/January). Zones: ET = UTC-5,
    /// UK/GMT/BST = UTC+0 (fixed; an hour off around DST changes is irrelevant for the stale check).
    /// Unknown zones are read as UTC.
    /// </summary>
    /// <param name="text">Event title or full stream name.</param>
    /// <param name="now">Current UTC time.</param>
    /// <returns>The start time in UTC, or null when the text has no recognizable date.</returns>
    public static DateTime? ParseStartUtc(string? text, DateTime now)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var m = StartRegex().Match(text);
        if (!m.Success
            || !DateTime.TryParseExact(m.Groups["mon"].Value, "MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monthOnly))
        {
            return null;
        }

        var day = int.Parse(m.Groups["day"].Value, CultureInfo.InvariantCulture);
        var hour = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) % 12;
        if (string.Equals(m.Groups["ampm"].Value, "PM", StringComparison.OrdinalIgnoreCase))
        {
            hour += 12;
        }

        var minute = int.Parse(m.Groups["min"].Value, CultureInfo.InvariantCulture);
        var offset = m.Groups["zone"].Value.ToUpperInvariant() switch
        {
            "ET" or "EST" or "EDT" => TimeSpan.FromHours(-5),
            _ => TimeSpan.Zero
        };

        DateTime? best = null;
        for (var year = now.Year - 1; year <= now.Year + 1; year++)
        {
            if (day > DateTime.DaysInMonth(year, monthOnly.Month) || hour > 23 || minute > 59)
            {
                continue;
            }

            var candidate = new DateTime(year, monthOnly.Month, day, hour, minute, 0, DateTimeKind.Utc) - offset;
            if (best is null || (candidate - now).Duration() < (best.Value - now).Duration())
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Returns whether an event started more than <paramref name="staleHours"/> ago.
    /// </summary>
    /// <param name="text">Event title or full stream name.</param>
    /// <param name="now">Current UTC time.</param>
    /// <param name="staleHours">Threshold; 0 or less disables the check.</param>
    /// <returns>True when the event is stale; false when fresh, undated, or the check is off.</returns>
    public static bool IsStale(string? text, DateTime now, int staleHours)
        => staleHours > 0 && ParseStartUtc(text, now) is { } start && now - start > TimeSpan.FromHours(staleHours);

    [GeneratedRegex(@"\b(?<mon>JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)[A-Z]*\.?\s+(?<day>\d{1,2})\s*[–—-]\s*(?<h>\d{1,2}):(?<min>\d{2})\s*(?<ampm>AM|PM)\s*(?<zone>ET|EST|EDT|UK|GMT|BST)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex StartRegex();
}
