using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.EpgChannelNames;

/// <summary>
/// Reads the start time providers put in event titles, e.g. "SHARKS @ LEAFS OCT 19 – 7:30 PM ET".
/// </summary>
public static partial class EventTime
{
    private static readonly TimeZoneInfo _eastern = FindZone("America/New_York", -5);
    private static readonly TimeZoneInfo _london = FindZone("Europe/London", 0);

    /// <summary>
    /// Parses the first "MON DD – H:MM AM/PM ZONE" in <paramref name="text"/>. The year is not in the title, so the
    /// candidate closest to <paramref name="now"/> is used (handles December/January). Zones follow daylight saving:
    /// ET = America/New_York, UK/GMT/BST = Europe/London (fixed UTC-5 / UTC+0 if the server has no time zone data).
    /// No zone is read as UTC.
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
        var zone = m.Groups["zone"].Value.ToUpperInvariant() switch
        {
            "ET" or "EST" or "EDT" => _eastern,
            "UK" or "GMT" or "BST" => _london,
            _ => TimeZoneInfo.Utc
        };

        DateTime? best = null;
        for (var year = now.Year - 1; year <= now.Year + 1; year++)
        {
            if (day > DateTime.DaysInMonth(year, monthOnly.Month) || hour > 23 || minute > 59)
            {
                continue;
            }

            var candidate = ToUtc(new DateTime(year, monthOnly.Month, day, hour, minute, 0, DateTimeKind.Unspecified), zone);
            if (best is null || (candidate - now).Duration() < (best.Value - now).Duration())
            {
                best = candidate;
            }
        }

        return best;
    }

    // Times skipped by a DST change (2:30 AM on spring-forward day) are read as the hour after.
    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
        => zone.IsInvalidTime(local) ? TimeZoneInfo.ConvertTimeToUtc(local.AddHours(1), zone) : TimeZoneInfo.ConvertTimeToUtc(local, zone);

    private static TimeZoneInfo FindZone(string id, int fallbackHours)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(fallbackHours), id, id);
        }
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
