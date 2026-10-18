using System;
using System.Collections.Generic;
using Jellyfin.Plugin.EpgChannelNames.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.EpgChannelNames;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "EPG Channel Names";

    public override Guid Id => Guid.Parse("4f1c7a3e-6b2d-4e8a-9c51-0d7e2a9b3f64");

    public override string Description => "Renames Live TV channels to the program currently airing in the EPG.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
        };
    }
}
