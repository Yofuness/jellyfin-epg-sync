using System;
using System.Collections.Generic;
using Jellyfin.Plugin.EpgChannelNames.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.EpgChannelNames;

/// <summary>
/// Plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // v2: the regex became an optional filter (event channels are auto-detected). The old NHL-only
        // defaults would silently keep v2 limited to NHL, so clear them once.
        if (IsOldDefaultRegex(Configuration.ChannelNameRegex))
        {
            Configuration.ChannelNameRegex = string.Empty;
            SaveConfiguration();
        }
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "EPG Channel Names";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("4f1c7a3e-6b2d-4e8a-9c51-0d7e2a9b3f64");

    /// <inheritdoc />
    public override string Description => "Renames Live TV channels to the program currently airing in the EPG.";

    /// <summary>
    /// Returns whether <paramref name="regex"/> is one of the NHL-only defaults from v1.
    /// </summary>
    /// <param name="regex">Saved channel regex.</param>
    /// <returns>True for "NHL GAME \d+" and "^NHL GAME \d+".</returns>
    internal static bool IsOldDefaultRegex(string? regex)
        => regex is @"NHL GAME \d+" or @"^NHL GAME \d+";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
        };
    }
}
