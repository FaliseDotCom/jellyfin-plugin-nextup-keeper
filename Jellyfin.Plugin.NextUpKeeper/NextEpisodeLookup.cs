using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.NextUpKeeper;

/// <summary>
/// The next episode of a series, and whether it (or one of its versions) has a resume point.
/// </summary>
public sealed record NextEpisode( Episode Episode, bool HasResumePoint );

/// <summary>
/// Finds the first unwatched episode after the stored position, for many series in one library query.
/// Results are cached per user for a short time and dropped when the user's watched state or the
/// library changes, because Next Up is requested several times for every home screen.
/// </summary>
public sealed class NextEpisodeLookup
{
  private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds( 60 );

  private readonly ILibraryManager _libraryManager;
  private readonly ConcurrentDictionary<Guid, UserCache> _cache = new();

  public NextEpisodeLookup( ILibraryManager libraryManager, IUserDataManager userDataManager )
  {
    _libraryManager = libraryManager;
    userDataManager.UserDataSaved += OnUserDataSaved;
    libraryManager.ItemAdded += OnLibraryChanged;
    libraryManager.ItemRemoved += OnLibraryChanged;
  }

  /// <summary>
  /// Returns the next unwatched episode per series. Series without one are left out.
  /// </summary>
  public Dictionary<Guid, NextEpisode> GetNext( User user, IReadOnlyDictionary<Guid, SeriesProgress> positions )
  {
    var result = new Dictionary<Guid, NextEpisode>();
    if ( positions.Count == 0 )
    {
      return result;
    }

    var cache = GetUserCache( user.Id );
    var missing = new Dictionary<Guid, SeriesProgress>();
    foreach ( var (seriesId, progress) in positions )
    {
      if ( cache.TryGet( seriesId, progress, out var cached ) )
      {
        if ( cached is not null )
        {
          result[ seriesId ] = cached;
        }

        continue;
      }

      missing[ seriesId ] = progress;
    }

    if ( missing.Count == 0 )
    {
      return result;
    }

    var found = Query( user, missing );
    foreach ( var (seriesId, progress) in missing )
    {
      found.TryGetValue( seriesId, out var next );
      cache.Set( seriesId, progress, next );
      if ( next is not null )
      {
        result[ seriesId ] = next;
      }
    }

    return result;
  }

  /// <summary>
  /// Forgets everything cached for a user.
  /// </summary>
  public void Invalidate( Guid userId )
  {
    _cache.TryRemove( userId, out _ );
  }

  private Dictionary<Guid, NextEpisode> Query( User user, IReadOnlyDictionary<Guid, SeriesProgress> positions )
  {
    var next = QueryNextEpisodes( user, positions );
    var resumable = QueryResumableEpisodes( user, next.Keys );

    return next.ToDictionary(
      kv => kv.Key,
      kv => new NextEpisode( kv.Value, resumable.Contains( kv.Value.Id ) ) );
  }

  private Dictionary<Guid, Episode> QueryNextEpisodes( User user, IReadOnlyDictionary<Guid, SeriesProgress> positions )
  {
    // Alternate versions are hidden by the library query and the played state is shared by all
    // versions, so this returns one primary per episode, like the server's own Next Up.
    var query = new InternalItemsQuery( user )
    {
      AncestorIds = positions.Keys.ToArray(),
      IncludeItemTypes = [ BaseItemKind.Episode ],
      Recursive = true,
      IsPlayed = false,
      IsVirtualItem = false
    };

    return _libraryManager.GetItemList( query )
      .OfType<Episode>()
      .Where( ep => positions.TryGetValue( ep.SeriesId, out var progress ) && EpisodeOrder.IsAfter( ep, progress ) )
      .GroupBy( ep => ep.SeriesId )
      .ToDictionary(
        group => group.Key,
        group => group
          .OrderBy( ep => ep.ParentIndexNumber!.Value )
          .ThenBy( ep => ep.IndexNumber!.Value )
          .First() );
  }

  /// <summary>
  /// Ids of the episodes in these series that have a resume point. A resume query keeps alternate
  /// versions, so progress on any version is attributed to its primary.
  /// </summary>
  private HashSet<Guid> QueryResumableEpisodes( User user, IEnumerable<Guid> seriesIds )
  {
    var ids = seriesIds.ToArray();
    if ( ids.Length == 0 )
    {
      return [];
    }

    var query = new InternalItemsQuery( user )
    {
      AncestorIds = ids,
      IncludeItemTypes = [ BaseItemKind.Episode ],
      Recursive = true,
      IsResumable = true
    };

    return _libraryManager.GetItemList( query )
      .OfType<Episode>()
      .Select( ep => ep.PrimaryVersionId ?? ep.Id )
      .ToHashSet();
  }

  private UserCache GetUserCache( Guid userId )
  {
    var cache = _cache.GetOrAdd( userId, _ => new UserCache() );
    if ( cache.Created + CacheLifetime >= DateTime.UtcNow )
    {
      return cache;
    }

    var fresh = new UserCache();
    _cache[ userId ] = fresh;
    return fresh;
  }

  private void OnUserDataSaved( object? sender, UserDataSaveEventArgs e )
  {
    if ( e.SaveReason == UserDataSaveReason.PlaybackProgress )
    {
      return;
    }

    Invalidate( e.UserId );
  }

  private void OnLibraryChanged( object? sender, ItemChangeEventArgs e )
  {
    if ( e.Item is Episode or Season or Series )
    {
      _cache.Clear();
    }
  }

  /// <summary>
  /// Cached results for one user, each valid only for the position it was computed for.
  /// </summary>
  private sealed class UserCache
  {
    private readonly ConcurrentDictionary<Guid, (int Season, int Episode, NextEpisode? Next)> _entries = new();

    public DateTime Created { get; } = DateTime.UtcNow;

    public bool TryGet( Guid seriesId, SeriesProgress progress, out NextEpisode? next )
    {
      next = null;
      if ( !_entries.TryGetValue( seriesId, out var entry )
           || entry.Season != progress.Season
           || entry.Episode != progress.Episode )
      {
        return false;
      }

      next = entry.Next;
      return true;
    }

    public void Set( Guid seriesId, SeriesProgress progress, NextEpisode? next )
    {
      _entries[ seriesId ] = (progress.Season, progress.Episode, next);
    }
  }
}
