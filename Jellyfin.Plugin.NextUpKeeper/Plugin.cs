using System;
using Jellyfin.Plugin.NextUpKeeper.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.NextUpKeeper;

/// <summary>
/// Next Up Keeper: keeps series in Next Up after the watched episodes have been deleted.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
  public Plugin( IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer )
    : base( applicationPaths, xmlSerializer )
  {
    Instance = this;
  }

  public static Plugin? Instance { get; private set; }

  public override string Name => "Next Up Keeper";

  public override Guid Id => Guid.Parse( "3d777d4e-6f17-47d3-a0ba-38220fad15b7" );

  public override string Description =>
    "Keeps series in Next Up / Continue Watching after the episodes you watched have been deleted from the server.";
}
