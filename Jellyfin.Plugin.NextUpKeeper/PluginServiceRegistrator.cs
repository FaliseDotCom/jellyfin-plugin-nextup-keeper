using System;
using System.Linq;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.TV;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NextUpKeeper;

/// <summary>
/// Registers the store and tracker, and wraps the server's Next Up service with <see cref="NextUpDecorator"/>.
/// Plugins are registered after the core services, so the original registration is already present here.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
  public void RegisterServices( IServiceCollection serviceCollection, IServerApplicationHost applicationHost )
  {
    serviceCollection.AddSingleton<WatchStore>();
    serviceCollection.AddSingleton<NextEpisodeLookup>();
    serviceCollection.AddHostedService<WatchTracker>();

    var original = serviceCollection.LastOrDefault( d => d.ServiceType == typeof( ITVSeriesManager ) );
    if ( original is null )
    {
      return;
    }

    serviceCollection.Remove( original );
    var createInner = GetFactory( original );

    serviceCollection.AddSingleton<ITVSeriesManager>( sp => new NextUpDecorator(
      createInner( sp ),
      sp.GetRequiredService<WatchStore>(),
      sp.GetRequiredService<NextEpisodeLookup>(),
      sp.GetRequiredService<ILibraryManager>(),
      sp.GetRequiredService<ILogger<NextUpDecorator>>() ) );
  }

  /// <summary>
  /// Builds the original service the way its registration describes.
  /// </summary>
  private static Func<IServiceProvider, ITVSeriesManager> GetFactory( ServiceDescriptor original )
  {
    if ( original.ImplementationInstance is ITVSeriesManager instance )
    {
      return _ => instance;
    }

    if ( original.ImplementationFactory is not null )
    {
      var factory = original.ImplementationFactory;
      return sp => (ITVSeriesManager)factory( sp );
    }

    var implType = original.ImplementationType!;
    return sp => (ITVSeriesManager)ActivatorUtilities.CreateInstance( sp, implType );
  }
}
