# Next Up Keeper for Jellyfin 12.x

A Jellyfin server plugin that keeps a series in **Next Up** after you delete the episodes you
watched. It works around [jellyfin/jellyfin#15151](https://github.com/jellyfin/jellyfin/issues/15151).

The problem: watch S01E01, delete S01E01, and when S01E02 arrives later it shows up in
*Recently added* but not in *Next Up*. Clients that merge *Continue Watching* and *Next Up*
(such as Wholphin on Android TV) lose the series from that row too.

Because the fix runs on the server, it works in every client: web, Android, Android TV, Wholphin,
Infuse and others.

## Installation

### From the plugin repository (recommended)

1. In Jellyfin go to **Dashboard → Plugins → Repositories** and add
   `https://raw.githubusercontent.com/FaliseDotCom/jellyfin-plugin-nextup-keeper/main/manifest.json`.
2. Open **Catalog**, install **Next Up Keeper**, and restart Jellyfin.
3. Under **Plugins** the plugin shows as *Active*. About a minute after startup the log shows
   `NextUpKeeper: backfill done`.

Updates then appear in the catalog like any other plugin.

### Manually

1. Download the zip from the [latest release](https://github.com/FaliseDotCom/jellyfin-plugin-nextup-keeper/releases).
2. Create a folder `NextUpKeeper_1.0.0.0` in Jellyfin's `plugins` folder and put
   `Jellyfin.Plugin.NextUpKeeper.dll` and `meta.json` in it.
3. Restart Jellyfin.

Where the `plugins` folder lives depends on the install:

| Install | Plugins folder |
|---|---|
| Official Docker image (`jellyfin/jellyfin`) | `/config/plugins` |
| linuxserver image (`lscr.io/linuxserver/jellyfin`) | `/config/data/plugins` |
| Home Assistant add-on by alexbelgium | `/share/jellyfin/data/plugins` by default (option `data_location`). The add-on log line `Setting data location to …` shows the actual path. Use the Samba or File editor add-on to copy the files. |

To uninstall, remove the plugin in the dashboard (or delete the folder) and restart.

## How it works

* **It remembers your position.** For every user and series the plugin stores the furthest
  episode watched (season, episode, date). This is kept by series id in
  `<jellyfin data>/nextupkeeper/progress.json`, separate from the episode files, so deleting
  them does not matter.
* **It reads what you have already watched.** About a minute after the server starts, the plugin
  records every episode that is currently marked as watched.
* **It completes Next Up.** The plugin wraps the server's Next Up service. For each series the
  server has "forgotten", it adds the first unwatched episode after your furthest point, sorted
  by when you last watched that series. When the server suggests an episode at or before your
  furthest point (for example a deleted episode that was downloaded again), the plugin moves it
  forward to the real next episode.
* **It follows unwatched changes.** Marking the furthest episode unwatched moves the position back
  to the furthest episode that is still in the library and watched. Marking a whole series
  unwatched makes the plugin forget the series.

The plugin applies the same filters as the server: the user's library access and parental
rating, libraries excluded from *Latest* (when no library is selected), the *Max days in Next Up*
setting, and the `enableResumable` flag (episodes with a resume point stay in *Continue Watching*).
If anything goes wrong, the plugin returns the server's own result, so Next Up never ends up
empty because of it.

## Limitations

* **Series deleted before the plugin was installed.** The plugin cannot know how far you got.
  When a new episode of such a series arrives, mark it watched and then unwatched again: with no
  earlier episodes left in the library, the plugin places your position just before it and the
  series appears in Next Up.
* **Marking a whole series unwatched while its first episodes are already deleted** keeps the
  series at the first remaining episode, for the same reason.
* **Rewatching mode** (`enableRewatching=true`) is passed straight to the server; the plugin adds
  nothing there.
* **Specials** (season 0) and episodes without numbers are not tracked.

## Compatibility

Built against the Jellyfin 12.2.0 packages (`targetAbi` 12.2.0.0) and tested on Jellyfin 12.2.0.

The plugin replaces the server's `ITVSeriesManager` registration with a wrapper. That is not an
official extension point, so a Jellyfin update can break it: test again after every Jellyfin
upgrade (see [.docs/TESTING.md](.docs/TESTING.md)). If a future version blocks this, the
alternative is an ASP.NET action filter on `GET /Shows/NextUp` that completes the
`QueryResult<BaseItemDto>`.

When upstream fixes #15151, the plugin becomes redundant but harmless: it only adds series that
are missing from the server's result, so it never creates duplicates.

## Background: why the server forgets the series

In the Jellyfin 12.2 source (tag `v12.2`):

* `Jellyfin.Server.Implementations/Item/NextUpService.cs`, `GetNextUpSeriesKeys()` finds
  candidate series by joining existing episodes (`BaseItems`) with `UserData` that has a last
  played date. When an episode is deleted, its `BaseItem` is gone and its `UserData` is moved to
  `BaseItemRepository.PlaceholderId`, so the series no longer qualifies.
* `GetNextUpEpisodesBatch()` also derives "last watched" only from episodes that still exist.
* `Emby.Server.Implementations/TV/TVSeriesManager.cs` (`ITVSeriesManager`) is the layer used by
  `GET /Shows/NextUp` (`TvShowsController`) and by `UserView`. The plugin wraps this interface.

## Development

### Project layout

| File | Role |
|---|---|
| `Plugin.cs` | Plugin class, GUID `3d777d4e-6f17-47d3-a0ba-38220fad15b7`. |
| `WatchStore.cs` | JSON store of the furthest position per user and series. An unreadable file is moved aside as `progress.json.bad`. |
| `WatchTracker.cs` | Hosted service. Listens to `IUserDataManager.UserDataSaved` (marking a whole series or season fires one event per episode) and runs the startup backfill. |
| `NextEpisodeLookup.cs` | Finds the next unwatched episode and its resume state for many series in two library queries, cached per user for 60 seconds and cleared on user data or library changes. |
| `NextUpDecorator.cs` | Wraps `ITVSeriesManager`: fetches the full server list, moves items forward, adds missing series, sorts and pages. |
| `EpisodeOrder.cs` | Season/episode comparison shared by all of the above. |
| `PluginServiceRegistrator.cs` | Registers the services and swaps the original `ITVSeriesManager` registration for the decorator. This works because `ApplicationHost` registers the core services before the plugins'. |

### Building

Requires the .NET 10 SDK.

```sh
dotnet publish Jellyfin.Plugin.NextUpKeeper -c Release -o out
```

The output only needs `Jellyfin.Plugin.NextUpKeeper.dll`; the Jellyfin assemblies come from
the server (`ExcludeAssets=runtime`). The `Jellyfin.Controller` and `Jellyfin.Model` package
versions in the `.csproj` must match the server version you target.

### Releasing

Plugin metadata (name, GUID, `targetAbi`, descriptions) lives in `build.yaml`; the GitHub
Actions workflow reads it from there.

* Every push to `main` builds the plugin; the zip is attached to the workflow run as an artifact.
* Pushing a tag `vX.Y.Z` also creates a GitHub release with the zip and adds the version to
  `manifest.json` on `main`, which is what the plugin repository URL above serves. The manifest
  checksum is the MD5 of the zip.

### Testing

The test scenarios, the Docker setup and the latest results are in
[.docs/TESTING.md](.docs/TESTING.md). `test/jf.sh` contains the API helpers used there.

## AI coding guidelines

No language skill covers C#. Follow the global formatting rules (two-space indentation, braces on
their own line, spaces inside parentheses and brackets); `.editorconfig` encodes them, so
`dotnet format whitespace Jellyfin.Plugin.NextUpKeeper` keeps new code in line.
