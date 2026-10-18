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
    public string ChannelNameRegex { get; set; } = @"^NHL GAME \d+";

    /// <summary>
    /// Gets or sets the name template. Tokens: {program}, {original}, {number}, {prefix} (regex match of <see cref="ChannelNameRegex"/>).
    /// </summary>
    public string NameTemplate { get; set; } = "{prefix} · {program}";

    /// <summary>
    /// Gets or sets the name used when no program is airing. Same tokens except {program}.
    /// </summary>
    public string NoProgramTemplate { get; set; } = "{prefix}";

    /// <summary>
    /// Gets or sets the default task interval in minutes, applied when the task triggers are first created.
    /// </summary>
    public int IntervalMinutes { get; set; } = 15;
}
