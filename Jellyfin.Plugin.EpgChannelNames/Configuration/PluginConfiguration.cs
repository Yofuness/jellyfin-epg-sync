using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.EpgChannelNames.Configuration;

/// <summary>
/// Plugin settings, edited on the dashboard config page.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the regex that selects which channels to rename. Matched against the original name.
    /// </summary>
    public string ChannelNameRegex { get; set; } = @"NHL GAME \d+";

    /// <summary>
    /// Gets or sets the name template. Tokens: {program}, {original}, {number}, {prefix} (regex match of <see cref="ChannelNameRegex"/>).
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
    /// Gets or sets the regex that extracts the game title from a Dispatcharr stream name. Uses the "title" group if present.
    /// </summary>
    public string StreamTitleRegex { get; set; } = @":\s*(?<title>[^/]+?)\s*(?:/.*)?$";

    /// <summary>
    /// Gets or sets a value indicating whether guide programs are updated: the airing block gets the Dispatcharr game, later blocks become plain placeholders.
    /// </summary>
    public bool RenamePrograms { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the airing placeholder program is removed from Dispatcharr game channels with no game.
    /// </summary>
    public bool RemoveNoGamePrograms { get; set; } = true;

    /// <summary>
    /// Gets or sets comma-separated tags added to renamed game programs. Jellyfin treats "Live" and "Sports" as program flags.
    /// </summary>
    public string GameProgramTags { get; set; } = "Live,Sports";

    /// <summary>
    /// Gets or sets how many hours ahead programs are checked.
    /// </summary>
    public int ProgramWindowHours { get; set; } = 12;

    /// <summary>
    /// Gets or sets the default task interval in minutes, applied when the task triggers are first created.
    /// </summary>
    public int IntervalMinutes { get; set; } = 15;
}
