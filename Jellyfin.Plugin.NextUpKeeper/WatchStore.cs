using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NextUpKeeper;

/// <summary>
/// Furthest watched position per user and series, stored by series id so it survives deleting the episode files.
/// </summary>
public sealed class SeriesProgress
{
  public int Season { get; set; }

  public int Episode { get; set; }

  public DateTime LastPlayed { get; set; }

  public SeriesProgress Copy()
  {
    return new SeriesProgress { Season = Season, Episode = Episode, LastPlayed = LastPlayed };
  }
}

/// <summary>
/// Thread-safe, JSON-backed store of <see cref="SeriesProgress"/>.
/// </summary>
public sealed class WatchStore
{
  private const string FolderName = "nextupkeeper";
  private const string FileName = "progress.json";

  private readonly object _lock = new();
  private readonly string _path;
  private readonly ILogger<WatchStore> _logger;
  private Dictionary<Guid, Dictionary<Guid, SeriesProgress>> _data = new();

  public WatchStore( IApplicationPaths paths, ILogger<WatchStore> logger )
  {
    _logger = logger;
    _path = Path.Combine( paths.DataPath, FolderName, FileName );
    Load();
  }

  /// <summary>
  /// Records that an episode was watched; only moves the position forward.
  /// </summary>
  public void Record( Guid userId, Guid seriesId, int season, int episode, DateTime? lastPlayed, bool save = true )
  {
    lock ( _lock )
    {
      if ( !_data.TryGetValue( userId, out var perUser ) )
      {
        perUser = new Dictionary<Guid, SeriesProgress>();
        _data[ userId ] = perUser;
      }

      var changed = false;
      if ( !perUser.TryGetValue( seriesId, out var p ) )
      {
        p = new SeriesProgress { Season = season, Episode = episode, LastPlayed = lastPlayed ?? DateTime.MinValue };
        perUser[ seriesId ] = p;
        changed = true;
      }
      else
      {
        if ( EpisodeOrder.Compare( season, episode, p.Season, p.Episode ) > 0 )
        {
          p.Season = season;
          p.Episode = episode;
          changed = true;
        }

        if ( lastPlayed.HasValue && lastPlayed.Value > p.LastPlayed )
        {
          p.LastPlayed = lastPlayed.Value;
          changed = true;
        }
      }

      if ( changed && save )
      {
        SaveLocked();
      }
    }
  }

  /// <summary>
  /// Overwrites the position (used when an episode is marked unwatched).
  /// </summary>
  public void SetPosition( Guid userId, Guid seriesId, int season, int episode )
  {
    lock ( _lock )
    {
      if ( _data.TryGetValue( userId, out var perUser ) && perUser.TryGetValue( seriesId, out var p ) )
      {
        p.Season = season;
        p.Episode = episode;
        SaveLocked();
      }
    }
  }

  public void Remove( Guid userId, Guid seriesId )
  {
    lock ( _lock )
    {
      if ( _data.TryGetValue( userId, out var perUser ) && perUser.Remove( seriesId ) )
      {
        SaveLocked();
      }
    }
  }

  /// <summary>
  /// Gets a copy of the position of one series.
  /// </summary>
  public bool TryGet( Guid userId, Guid seriesId, out SeriesProgress progress )
  {
    lock ( _lock )
    {
      if ( _data.TryGetValue( userId, out var perUser ) && perUser.TryGetValue( seriesId, out var p ) )
      {
        progress = p.Copy();
        return true;
      }

      progress = new SeriesProgress();
      return false;
    }
  }

  /// <summary>
  /// Returns a copy of all tracked series for a user.
  /// </summary>
  public Dictionary<Guid, SeriesProgress> GetAll( Guid userId )
  {
    lock ( _lock )
    {
      if ( !_data.TryGetValue( userId, out var perUser ) )
      {
        return new Dictionary<Guid, SeriesProgress>();
      }

      return perUser.ToDictionary( kv => kv.Key, kv => kv.Value.Copy() );
    }
  }

  public void Save()
  {
    lock ( _lock )
    {
      SaveLocked();
    }
  }

  private void Load()
  {
    try
    {
      if ( File.Exists( _path ) )
      {
        var json = File.ReadAllText( _path );
        _data = JsonSerializer.Deserialize<Dictionary<Guid, Dictionary<Guid, SeriesProgress>>>( json )
                ?? new Dictionary<Guid, Dictionary<Guid, SeriesProgress>>();
      }
    }
    catch ( Exception ex )
    {
      _logger.LogError( ex, "NextUpKeeper: could not read {Path}, keeping it as .bad and starting empty", _path );
      _data = new Dictionary<Guid, Dictionary<Guid, SeriesProgress>>();
      KeepUnreadableFile();
    }
  }

  /// <summary>
  /// Moves an unreadable store aside, so the next save does not overwrite what may still be recovered.
  /// </summary>
  private void KeepUnreadableFile()
  {
    try
    {
      File.Move( _path, _path + ".bad", true );
    }
    catch ( Exception ex )
    {
      _logger.LogError( ex, "NextUpKeeper: could not move {Path} aside", _path );
    }
  }

  private void SaveLocked()
  {
    try
    {
      Directory.CreateDirectory( Path.GetDirectoryName( _path )! );
      var tmp = _path + ".tmp";
      File.WriteAllText( tmp, JsonSerializer.Serialize( _data ) );
      File.Move( tmp, _path, true );
    }
    catch ( Exception ex )
    {
      _logger.LogError( ex, "NextUpKeeper: could not write {Path}", _path );
    }
  }
}
