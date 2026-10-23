using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NextUpKeeper;

/// <summary>
/// Wraps the server's <see cref="ITVSeriesManager"/>. The built-in Next Up only finds a series when a
/// watched episode of it still exists in the library. This wrapper adds the series the plugin knows
/// you were watching, with the first unwatched episode after the furthest one you watched, and moves
/// server items that lie at or before that point forward. Any failure returns the server's own result.
/// </summary>
public sealed class NextUpDecorator : ITVSeriesManager
{
  private readonly ITVSeriesManager _inner;
  private readonly WatchStore _store;
  private readonly NextEpisodeLookup _lookup;
  private readonly ILibraryManager _libraryManager;
  private readonly ILogger<NextUpDecorator> _logger;

  public NextUpDecorator(
    ITVSeriesManager inner,
    WatchStore store,
    NextEpisodeLookup lookup,
    ILibraryManager libraryManager,
    ILogger<NextUpDecorator> logger )
  {
    _inner = inner;
    _store = store;
    _lookup = lookup;
    _libraryManager = libraryManager;
    _logger = logger;
  }

  public QueryResult<BaseItem> GetNextUp( NextUpQuery query, DtoOptions options )
  {
    if ( IsSingleSeries( query ) )
    {
      return FixSingleSeries( _inner.GetNextUp( query, options ), query );
    }

    // The rewatching list is a separate opt-in view; leave it to the server.
    if ( query.EnableRewatching )
    {
      return _inner.GetNextUp( query, options );
    }

    return Augment( query, q => _inner.GetNextUp( q, options ), parentFolders: null );
  }

  public QueryResult<BaseItem> GetNextUp( NextUpQuery request, BaseItem[] parentsFolders, DtoOptions options )
  {
    if ( IsSingleSeries( request ) )
    {
      return FixSingleSeries( _inner.GetNextUp( request, parentsFolders, options ), request );
    }

    if ( request.EnableRewatching )
    {
      return _inner.GetNextUp( request, parentsFolders, options );
    }

    return Augment( request, q => _inner.GetNextUp( q, parentsFolders, options ), parentsFolders );
  }

  private static bool IsSingleSeries( NextUpQuery query )
  {
    return query.SeriesId.HasValue && query.SeriesId.Value != Guid.Empty;
  }

  /// <param name="parentFolders">The folders the request is scoped to; null when the server picks them from the query.</param>
  private QueryResult<BaseItem> Augment( NextUpQuery query, Func<NextUpQuery, QueryResult<BaseItem>> callInner, BaseItem[]? parentFolders )
  {
    // Ask the server for the complete, unpaged list so our additions can be merged in the right order.
    var core = callInner( CopyUnpaged( query ) );

    try
    {
      return Page( Adjust( query, core.Items, parentFolders ), query );
    }
    catch ( Exception ex )
    {
      _logger.LogError( ex, "NextUpKeeper: failed to adjust Next Up, returning server result" );
      return Page( core.Items, query );
    }
  }

  private static NextUpQuery CopyUnpaged( NextUpQuery query )
  {
    return new NextUpQuery
    {
      User = query.User,
      ParentId = query.ParentId,
      SeriesId = query.SeriesId,
      StartIndex = null,
      Limit = null,
      EnableImageTypes = query.EnableImageTypes,
      EnableTotalRecordCount = true,
      NextUpDateCutoff = query.NextUpDateCutoff,
      EnableResumable = query.EnableResumable,
      EnableRewatching = query.EnableRewatching
    };
  }

  private IReadOnlyList<BaseItem> Adjust( NextUpQuery query, IReadOnlyList<BaseItem> coreItems, BaseItem[]? parentFolders )
  {
    var user = query.User;
    var tracked = _store.GetAll( user.Id );
    if ( tracked.Count == 0 )
    {
      return coreItems;
    }

    var behind = FindItemsBehind( coreItems, tracked );
    var missing = FindMissingSeries( query, coreItems, tracked, parentFolders );
    if ( behind.Count == 0 && missing.Count == 0 )
    {
      return coreItems;
    }

    var wanted = behind.Keys.Concat( missing ).ToDictionary( id => id, id => tracked[ id ] );
    var next = GetNext( query, wanted );
    var items = ReplaceItemsBehind( coreItems, behind, next );
    var extras = missing
      .Where( next.ContainsKey )
      .Select( id => (Episode: next[ id ], tracked[ id ].LastPlayed) )
      .ToList();

    _logger.LogDebug(
      "NextUpKeeper: {Added} series added and {Moved} items moved forward for user {User}",
      extras.Count,
      behind.Count,
      user.Username );

    return Merge( user.Id, items, extras );
  }

  /// <summary>
  /// Server items that lie at or before the stored position, keyed by series. This happens when a
  /// deleted episode is downloaded again: it gets a new id and the server sees it as unwatched.
  /// </summary>
  private static Dictionary<Guid, Episode> FindItemsBehind( IReadOnlyList<BaseItem> coreItems, Dictionary<Guid, SeriesProgress> tracked )
  {
    var behind = new Dictionary<Guid, Episode>();
    foreach ( var item in coreItems )
    {
      if ( item is Episode ep
           && tracked.TryGetValue( ep.SeriesId, out var progress )
           && EpisodeOrder.IsAtOrBefore( ep, progress ) )
      {
        behind[ ep.SeriesId ] = ep;
      }
    }

    return behind;
  }

  /// <summary>
  /// Replaces each item behind the stored position with the real next episode, or drops it when there is none.
  /// </summary>
  private static List<BaseItem> ReplaceItemsBehind( IReadOnlyList<BaseItem> coreItems, Dictionary<Guid, Episode> behind, Dictionary<Guid, Episode> next )
  {
    var items = new List<BaseItem>( coreItems.Count );
    foreach ( var item in coreItems )
    {
      if ( item is not Episode ep || !behind.TryGetValue( ep.SeriesId, out var old ) || old.Id != ep.Id )
      {
        items.Add( item );
        continue;
      }

      if ( next.TryGetValue( ep.SeriesId, out var replacement ) )
      {
        items.Add( replacement );
      }
    }

    return items;
  }

  /// <summary>
  /// Tracked series that the server left out, filtered the way the server filters its own list.
  /// </summary>
  private List<Guid> FindMissingSeries( NextUpQuery query, IReadOnlyList<BaseItem> coreItems, Dictionary<Guid, SeriesProgress> tracked, BaseItem[]? parentFolders )
  {
    var result = new List<Guid>();
    var user = query.User;
    var presentSeries = coreItems.OfType<Episode>().Select( e => e.SeriesId ).ToHashSet();

    if ( !TryGetLibraryScope( query, parentFolders, out var allowedParents ) )
    {
      return result;
    }

    var excludedLibraries = user.GetPreferenceValues<Guid>( PreferenceKind.LatestItemExcludes ).ToHashSet();

    foreach ( var (seriesId, progress) in tracked )
    {
      if ( presentSeries.Contains( seriesId ) || progress.LastPlayed < query.NextUpDateCutoff )
      {
        continue;
      }

      if ( _libraryManager.GetItemById( seriesId ) is not Series series || !series.IsVisible( user ) )
      {
        continue;
      }

      var ancestors = series.GetAncestorIds().ToHashSet();
      if ( allowedParents.Count > 0 && !ancestors.Overlaps( allowedParents ) )
      {
        continue;
      }

      // Without an explicit scope the server uses all libraries except those excluded from "Latest".
      if ( allowedParents.Count == 0 && ancestors.Overlaps( excludedLibraries ) )
      {
        continue;
      }

      result.Add( seriesId );
    }

    return result;
  }

  /// <summary>
  /// The library ids a request is limited to; an empty set means no limit. Returns false when the
  /// request is scoped to nothing (no folders, or a parent that does not exist): the server then
  /// returns nothing either.
  /// </summary>
  private bool TryGetLibraryScope( NextUpQuery query, BaseItem[]? parentFolders, out HashSet<Guid> allowedParents )
  {
    allowedParents = [];
    if ( parentFolders is not null )
    {
      allowedParents = ResolveLibraries( parentFolders );
      return parentFolders.Length > 0;
    }

    if ( !query.ParentId.HasValue )
    {
      return true;
    }

    var parent = _libraryManager.GetItemById( query.ParentId.Value );
    if ( parent is null )
    {
      return false;
    }

    allowedParents = ResolveLibraries( [ parent ] );
    return true;
  }

  /// <summary>
  /// Turns the folders a request is scoped to into library ids that show up in a series' ancestors.
  /// Returns an empty set (no restriction) for grouped views that cannot be mapped.
  /// </summary>
  private static HashSet<Guid> ResolveLibraries( IEnumerable<BaseItem> parents )
  {
    var ids = new HashSet<Guid>();
    foreach ( var parent in parents )
    {
      if ( parent is not UserView view )
      {
        ids.Add( parent.Id );
        continue;
      }

      if ( view.DisplayParentId != Guid.Empty )
      {
        ids.Add( view.DisplayParentId );
      }
      else if ( view.ParentId != Guid.Empty )
      {
        ids.Add( view.ParentId );
      }
      else
      {
        return [];
      }
    }

    return ids;
  }

  /// <summary>
  /// The next episode per series. Unless the query asks for them, episodes with a resume point are
  /// left out: they belong in Continue Watching, and the server skips them too.
  /// </summary>
  private Dictionary<Guid, Episode> GetNext( NextUpQuery query, Dictionary<Guid, SeriesProgress> positions )
  {
    return _lookup.GetNext( query.User, positions )
      .Where( kv => query.EnableResumable || !kv.Value.HasResumePoint )
      .ToDictionary( kv => kv.Key, kv => kv.Value.Episode );
  }

  /// <summary>
  /// Keeps the server's order and slots the extra episodes in by last-watched date.
  /// </summary>
  private List<BaseItem> Merge( Guid userId, IReadOnlyList<BaseItem> coreItems, List<(Episode Episode, DateTime LastPlayed)> extras )
  {
    if ( extras.Count == 0 )
    {
      return coreItems.ToList();
    }

    var dated = new List<(BaseItem Item, DateTime Date, int Order)>();
    var previous = DateTime.MaxValue;
    var order = 0;

    foreach ( var item in coreItems )
    {
      var date = previous;
      if ( item is Episode ep && _store.TryGet( userId, ep.SeriesId, out var p ) && p.LastPlayed <= previous )
      {
        date = p.LastPlayed;
      }

      dated.Add( (item, date, order++) );
      previous = date;
    }

    foreach ( var (episode, lastPlayed) in extras )
    {
      dated.Add( (episode, lastPlayed, order++) );
    }

    return dated
      .OrderByDescending( x => x.Date )
      .ThenBy( x => x.Order )
      .Select( x => x.Item )
      .ToList();
  }

  /// <summary>
  /// For a single series the server returns the very first unwatched episode when no watched
  /// episode is left in the library. Move it forward to the episode after the furthest one watched.
  /// </summary>
  private QueryResult<BaseItem> FixSingleSeries( QueryResult<BaseItem> result, NextUpQuery query )
  {
    try
    {
      var user = query.User;
      var seriesId = query.SeriesId!.Value;
      if ( !_store.TryGet( user.Id, seriesId, out var progress ) )
      {
        return result;
      }

      var tracked = new Dictionary<Guid, SeriesProgress> { [ seriesId ] = progress };
      var behind = FindItemsBehind( result.Items, tracked );
      if ( behind.Count == 0 )
      {
        return result;
      }

      var next = GetNext( query, tracked );

      var items = ReplaceItemsBehind( result.Items, behind, next );
      return new QueryResult<BaseItem>( result.StartIndex, items.Count, items );
    }
    catch ( Exception ex )
    {
      _logger.LogError( ex, "NextUpKeeper: failed to adjust single-series Next Up" );
      return result;
    }
  }

  private static QueryResult<BaseItem> Page( IReadOnlyList<BaseItem> items, NextUpQuery query )
  {
    IEnumerable<BaseItem> paged = items;
    var total = query.EnableTotalRecordCount ? items.Count : 0;

    if ( query.StartIndex.HasValue )
    {
      paged = paged.Skip( query.StartIndex.Value );
    }

    if ( query.Limit.HasValue && query.Limit.Value > 0 )
    {
      paged = paged.Take( query.Limit.Value );
    }

    return new QueryResult<BaseItem>( query.StartIndex, total, paged.ToArray() );
  }
}
