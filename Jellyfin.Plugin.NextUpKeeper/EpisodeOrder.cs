using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.NextUpKeeper;

/// <summary>
/// Compares episodes and stored positions by season and episode number. Specials (season 0) and
/// episodes without numbers have no place in that order and are never tracked.
/// </summary>
public static class EpisodeOrder
{
  /// <summary>
  /// Compares two (season, episode) positions.
  /// </summary>
  public static int Compare( int s1, int e1, int s2, int e2 )
  {
    return s1 != s2 ? s1.CompareTo( s2 ) : e1.CompareTo( e2 );
  }

  /// <summary>
  /// Whether the episode is a numbered, regular-season episode.
  /// </summary>
  public static bool IsTrackable( Episode episode )
  {
    return episode.ParentIndexNumber.HasValue
           && episode.ParentIndexNumber.Value > 0
           && episode.IndexNumber.HasValue;
  }

  /// <summary>
  /// The last episode number the item covers; a double episode E05-E06 counts as E06.
  /// </summary>
  public static int LastNumber( Episode episode )
  {
    return episode.IndexNumberEnd ?? episode.IndexNumber ?? 0;
  }

  /// <summary>
  /// Whether the episode comes after the stored position.
  /// </summary>
  public static bool IsAfter( Episode episode, SeriesProgress progress )
  {
    return IsTrackable( episode )
           && Compare( episode.ParentIndexNumber!.Value, episode.IndexNumber!.Value, progress.Season, progress.Episode ) > 0;
  }

  /// <summary>
  /// Whether the episode lies at or before the stored position, so it was already watched.
  /// </summary>
  public static bool IsAtOrBefore( Episode episode, SeriesProgress progress )
  {
    return IsTrackable( episode ) && !IsAfter( episode, progress );
  }
}
