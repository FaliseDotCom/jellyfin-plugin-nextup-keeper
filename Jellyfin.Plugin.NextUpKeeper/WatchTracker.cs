using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NextUpKeeper;

/// <summary>
/// Listens for watched/unwatched changes and keeps <see cref="WatchStore"/> up to date.
/// On startup it also records everything that is currently marked as watched.
/// </summary>
/// <remarks>
/// Marking a whole series or season (un)watched arrives as one event per episode, because the
/// server sweeps through the episodes and saves each one with <see cref="UserDataSaveReason.TogglePlayed"/>.
/// </remarks>
public sealed class WatchTracker : IHostedService
{
  private static readonly TimeSpan BackfillDelay = TimeSpan.FromSeconds( 60 );

  private readonly IUserDataManager _userDataManager;
  private readonly IUserManager _userManager;
  private readonly ILibraryManager _libraryManager;
  private readonly WatchStore _store;
  private readonly ILogger<WatchTracker> _logger;
  private CancellationTokenSource? _cts;

  public WatchTracker(
    IUserDataManager userDataManager,
    IUserManager userManager,
    ILibraryManager libraryManager,
    WatchStore store,
    ILogger<WatchTracker> logger )
  {
    _userDataManager = userDataManager;
    _userManager = userManager;
    _libraryManager = libraryManager;
    _store = store;
    _logger = logger;
  }

  public Task StartAsync( CancellationToken cancellationToken )
  {
    _userDataManager.UserDataSaved += OnUserDataSaved;
    _cts = new CancellationTokenSource();
    var token = _cts.Token;
    _ = Task.Run( () => BackfillAsync( token ), token );
    return Task.CompletedTask;
  }

  public Task StopAsync( CancellationToken cancellationToken )
  {
    _userDataManager.UserDataSaved -= OnUserDataSaved;
    _cts?.Cancel();
    _cts?.Dispose();
    _cts = null;
    return Task.CompletedTask;
  }

  private void OnUserDataSaved( object? sender, UserDataSaveEventArgs e )
  {
    try
    {
      // Progress reports arrive every few seconds during playback; nothing to do there.
      if ( e.SaveReason == UserDataSaveReason.PlaybackProgress )
      {
        return;
      }

      if ( e.Item is not Episode episode || e.UserData is null )
      {
        return;
      }

      if ( episode.SeriesId == Guid.Empty || !EpisodeOrder.IsTrackable( episode ) )
      {
        return;
      }

      if ( e.UserData.Played )
      {
        var lastPlayed = e.UserData.LastPlayedDate ?? DateTime.UtcNow;
        _store.Record( e.UserId, episode.SeriesId, episode.ParentIndexNumber!.Value, EpisodeOrder.LastNumber( episode ), lastPlayed );
        return;
      }

      if ( e.SaveReason == UserDataSaveReason.TogglePlayed )
      {
        HandleMarkedUnplayed( e.UserId, episode );
      }
    }
    catch ( Exception ex )
    {
      _logger.LogError( ex, "NextUpKeeper: error handling user data change" );
    }
  }

  /// <summary>
  /// An episode was marked unwatched. When it was the furthest point, fall back to the furthest
  /// episode that is still in the library and watched.
  /// </summary>
  private void HandleMarkedUnplayed( Guid userId, Episode episode )
  {
    var seriesId = episode.SeriesId;
    var season = episode.ParentIndexNumber!.Value;
    if ( !_store.TryGet( userId, seriesId, out var current )
         || EpisodeOrder.Compare( season, EpisodeOrder.LastNumber( episode ), current.Season, current.Episode ) < 0 )
    {
      return;
    }

    var user = _userManager.GetUserById( userId );
    if ( user is null )
    {
      return;
    }

    var furthest = GetSeriesEpisodes( user, seriesId, isPlayed: true )
      .OrderByDescending( ep => ep.ParentIndexNumber!.Value )
      .ThenByDescending( EpisodeOrder.LastNumber )
      .FirstOrDefault();

    if ( furthest is not null )
    {
      _store.SetPosition( userId, seriesId, furthest.ParentIndexNumber!.Value, EpisodeOrder.LastNumber( furthest ) );
      return;
    }

    if ( IsSeriesStart( user, episode ) )
    {
      _store.Remove( userId, seriesId );
      return;
    }

    // Nothing watched is left and the earlier episodes are gone from the library: the user is
    // resetting the series to just before this episode. This is also how a series that was deleted
    // before the plugin was installed gets a position: mark its first episode watched, then unwatched.
    _store.SetPosition( userId, seriesId, season, episode.IndexNumber!.Value - 1 );
  }

  /// <summary>
  /// Whether unmarking the episode resets the whole series: it is S01E01, or earlier episodes are still
  /// in the library (unwatched, so the user is not just stepping back over deleted ones). Marking a whole
  /// series unwatched always ends on such an episode.
  /// </summary>
  private bool IsSeriesStart( User user, Episode episode )
  {
    var season = episode.ParentIndexNumber!.Value;
    var number = episode.IndexNumber!.Value;
    if ( season == 1 && number <= 1 )
    {
      return true;
    }

    return GetSeriesEpisodes( user, episode.SeriesId, isPlayed: null )
      .Any( ep => EpisodeOrder.Compare( ep.ParentIndexNumber!.Value, ep.IndexNumber!.Value, season, number ) < 0 );
  }

  private Episode[] GetSeriesEpisodes( User user, Guid seriesId, bool? isPlayed )
  {
    var query = new InternalItemsQuery( user )
    {
      AncestorIds = [ seriesId ],
      IncludeItemTypes = [ BaseItemKind.Episode ],
      Recursive = true,
      IsPlayed = isPlayed,
      IsVirtualItem = false
    };

    return _libraryManager.GetItemList( query )
      .OfType<Episode>()
      .Where( EpisodeOrder.IsTrackable )
      .ToArray();
  }

  /// <summary>
  /// Records every episode that is currently watched, so series are covered from the moment
  /// the plugin is installed (as long as their watched episodes still exist at that time).
  /// </summary>
  private async Task BackfillAsync( CancellationToken token )
  {
    try
    {
      // Give the server time to finish starting up.
      await Task.Delay( BackfillDelay, token ).ConfigureAwait( false );

      var total = 0;
      foreach ( var user in _userManager.GetUsers().ToList() )
      {
        token.ThrowIfCancellationRequested();
        total += BackfillUser( user );
      }

      _store.Save();
      _logger.LogInformation( "NextUpKeeper: backfill done, {Count} watched episodes scanned", total );
    }
    catch ( OperationCanceledException )
    {
    }
    catch ( Exception ex )
    {
      _logger.LogError( ex, "NextUpKeeper: backfill failed" );
    }
  }

  private int BackfillUser( User user )
  {
    var query = new InternalItemsQuery( user )
    {
      IncludeItemTypes = [ BaseItemKind.Episode ],
      Recursive = true,
      IsPlayed = true,
      IsVirtualItem = false
    };

    var episodes = _libraryManager.GetItemList( query )
      .OfType<Episode>()
      .Where( ep => ep.SeriesId != Guid.Empty && EpisodeOrder.IsTrackable( ep ) )
      .ToList();

    if ( episodes.Count == 0 )
    {
      return 0;
    }

    var userData = _userDataManager.GetUserDataBatch( episodes.Cast<BaseItem>().ToList(), user );

    foreach ( var ep in episodes )
    {
      DateTime? lastPlayed = userData.TryGetValue( ep.Id, out var data ) ? data.LastPlayedDate : null;
      _store.Record( user.Id, ep.SeriesId, ep.ParentIndexNumber!.Value, EpisodeOrder.LastNumber( ep ), lastPlayed, save: false );
    }

    return episodes.Count;
  }
}
