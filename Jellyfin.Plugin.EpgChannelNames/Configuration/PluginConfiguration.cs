using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.EpgChannelNames.Configuration;

/// <summary>
/// Plugin settings, edited on the dashboard config page.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets an optional regex on the original channel name. Empty means all channels.
    /// Without Dispatcharr (EPG-only mode) it is required and selects the channels to rename from the EPG.
    /// </summary>
    public string ChannelNameRegex { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name template. Tokens: {program}, {original}, {number}, {prefix} (event channels: the stream name before ":"; EPG mode: the regex match).
    /// </summary>
    public string NameTemplate { get; set; } = "{prefix} · {program}";

    /// <summary>
    /// Gets or sets the name used when no program is airing. Same tokens except {program}.
    /// </summary>
    public string NoProgramTemplate { get; set; } = "{prefix}";

    /// <summary>
    /// Gets or sets the Dispatcharr base URL (e.g. http://localhost:9191). Empty disables Dispatcharr lookups.
    /// </summary>
    public string DispatcharrUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Dispatcharr API key.
    /// </summary>
    public string DispatcharrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the regex that detects event channels from their stream name. Groups: "prefix" (channel part) and "title" (event; empty = no event now).
    /// </summary>
    public string EventRegex { get; set; } = @"^(?<prefix>.+?)\s*:\s*(?<title>[^/]*?)\s*(?:/.*)?$";

    /// <summary>
    /// Gets or sets after how many hours past its start time (read from the title, e.g. "OCT 17 – 5:00 PM UK") an event counts as over. 0 disables.
    /// </summary>
    public int StaleEventHours { get; set; } = 6;

    /// <summary>
    /// Gets or sets category rules, one per line: "Dispatcharr group = Shows|Movies|Sports|News|Kids".
    /// </summary>
    public string GroupCategoryRules { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether groups without a rule get a row guessed from their name ("SPORTS" = Sports, "DOCUMENTARY" = Shows).
    /// </summary>
    public bool AutoGroupCategories { get; set; } = true;

    /// <summary>
    /// Gets or sets program titles that are never categorized, one per line (case-insensitive).
    /// </summary>
    public string IgnoredProgramTitles { get; set; } = "Channel No Longer Available";

    /// <summary>
    /// Gets or sets a value indicating whether guide programs on event channels are updated: the airing block gets the event, later blocks become plain placeholders.
    /// </summary>
    public bool RenamePrograms { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the airing placeholder program is removed from event channels with no event.
    /// </summary>
    public bool RemoveNoGamePrograms { get; set; } = true;

    /// <summary>
    /// Gets or sets comma-separated tags added to the airing event program. Jellyfin treats "Live" and "Sports" as program flags.
    /// </summary>
    public string GameProgramTags { get; set; } = "Live,Sports";

    /// <summary>
    /// Gets or sets a value indicating whether programs without an image get their channel's logo.
    /// </summary>
    public bool UseChannelLogo { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether sports programs are retitled with the two teams found in their description, e.g. "Live NHL: Flyers vs Avalanche".
    /// </summary>
    public bool UseTeamNames { get; set; } = true;

    /// <summary>
    /// Gets or sets the ESPN site API base URL for the team database. Not on the config page; tests point it at a fake server.
    /// </summary>
    public string TeamDatabaseUrl { get; set; } = "https://site.api.espn.com/apis/site/v2/sports/";

    /// <summary>
    /// Gets or sets how many hours ahead programs are checked.
    /// </summary>
    public int ProgramWindowHours { get; set; } = 12;

    /// <summary>
    /// Gets or sets the default task interval in minutes, applied when the task triggers are first created.
    /// </summary>
    public int IntervalMinutes { get; set; } = 15;
}
