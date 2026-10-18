using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.EpgChannelNames.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Only channels whose original name matches this regex are renamed.</summary>
    public string ChannelNameRegex { get; set; } = @"^NHL GAME \d+";

    /// <summary>Name template. Tokens: {program}, {original}, {number}, {prefix} (regex match of ChannelNameRegex).</summary>
    public string NameTemplate { get; set; } = "{prefix} · {program}";

    /// <summary>Name used when no program is airing. Same tokens except {program}.</summary>
    public string NoProgramTemplate { get; set; } = "{prefix}";

    public int IntervalMinutes { get; set; } = 15;
}
