# Testing Next Up Keeper

Run these scenarios after every change to the plugin and after every Jellyfin upgrade. The plugin
replaces a core service, so a server update can break it without a compile error.

## Test server

A throwaway Jellyfin in Docker, with tiny generated episodes. Run from the repository root in Git
Bash; on Windows, pass Windows paths to `docker -v` (`cygpath -w`) and set `MSYS_NO_PATHCONV=1`.

```sh
export JF_TEST_DIR="$PWD/test/run"            # ignored by git
mkdir -p "$JF_TEST_DIR"/{config,cache,media/tv,stash}
ffmpeg -f lavfi -i testsrc=size=320x180:rate=10 -f lavfi -i sine -t 10 \
  -c:v libx264 -preset ultrafast -c:a aac -shortest "$JF_TEST_DIR/stash/sample.mkv"

docker run -d --name jf-test -p 8096:8096 \
  -v "$JF_TEST_DIR/config:/config" -v "$JF_TEST_DIR/cache:/cache" -v "$JF_TEST_DIR/media:/media" \
  jellyfin/jellyfin:12.2
```

`test/jf.sh` holds the helpers: `api`, `login`, `nextup [limit] [startIndex] [extra query]`,
`epid <series> <season> <episode>`, `seriesid`, `played`/`unplayed <id>`, `scan` (waits for the
library scan to finish), `addep`/`delep <series> <season> <episode>`.

Setup through the API (`. test/jf.sh` first):

```sh
api POST /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"NL","PreferredMetadataLanguage":"en"}'
api GET /Startup/User > /dev/null
api POST /Startup/User '{"Name":"alice","Password":"pw"}'
api POST /Startup/Complete
login alice pw; printf 'TOKEN=%s\nUSERID=%s\n' "$TOKEN" "$USERID" > "$JF_TEST_DIR/state.env"
# TV library without internet metadata, so test series are not matched to real shows
api POST "/Library/VirtualFolders?name=TV&collectionType=tvshows&paths=%2Fmedia%2Ftv" \
  '{"LibraryOptions":{"TypeOptions":[{"Type":"Series","MetadataFetchers":[],"ImageFetchers":[]},{"Type":"Season","MetadataFetchers":[],"ImageFetchers":[]},{"Type":"Episode","MetadataFetchers":[],"ImageFetchers":[]}]}}'
```

Install the plugin by stopping the container, copying `Jellyfin.Plugin.NextUpKeeper.dll` and a
`meta.json` (the workflow shows the format) to `config/plugins/NextUpKeeper_1.0.0.0/`, and
starting it again. For debug output, add `"Jellyfin.Plugin.NextUpKeeper": "Debug"` to the
`Override` section of `config/config/logging.default.json`.

Pitfalls found while testing:

* Mark episodes only after `scan` has returned. Items created mid-scan can be replaced, and the
  marks are lost.
* `GET /Shows/NextUp` defaults to `enableResumable=true`. The web client and most apps send
  `false`; test resume behaviour with `false`.
* In the user configuration the field is `LatestItemsExcludes` (with an s); the server-side
  preference is `PreferenceKind.LatestItemExcludes`.
* Re-added files get their watched state back in 12.2 (user data is re-attached by key), so the
  "re-downloaded episode" case only arises when such an episode is then marked unwatched.

## Scenarios and results

Last run: 2026-10-23, Jellyfin 12.2.0 (`jellyfin/jellyfin:12.2`), plugin 1.0.0.0. All passed.

| # | Scenario | Expected | Result |
|---|---|---|---|
| 2 | Without the plugin: E01 watched, E01 deleted + scan, E02 added + scan | E02 missing from Next Up (confirms the bug) | Missing |
| – | Plugin installed | Status *Active*, `backfill done` in the log after ~60 s | Pass |
| 1 | With the plugin, same steps on a new series | E02 in Next Up | Pass |
| 1b | Series from scenario 2 (deleted before install): mark E02 watched, then unwatched | E02 in Next Up | Pass |
| 3 | Several series watched in turn; `limit=2` with `startIndex` 0 and 2 | Last watched first; pages consistent, total stays 4 | Pass |
| 4 | E02 half watched, `enableResumable=false` | E02 in Resume, not in Next Up; back after clearing the position. With `enableResumable=true` it stays, like the server | Pass |
| 5 | Mark the furthest episode unwatched | Position goes back one episode | Pass |
| 5b | Mark the whole series unwatched | Series disappears from Next Up | Pass |
| 6 | `nextUpDateCutoff` between two series' last watched dates | Older series gone | Pass |
| 7 | Library excluded from *Latest* | Next Up empty (as the server); with `parentId=<library>` the series are shown, as the server does | Pass |
| 8 | Server restart | `progress.json` kept, backfill logged, Next Up unchanged | Pass |
| 9 | Second user | Sees only their own series | Pass |
| – | Re-downloaded E01 marked unwatched (server suggests E01, position is E02) | Next Up and `seriesId=` query show E03 | Pass |
| – | `enableRewatching=true` | Server result unchanged, no duplicates | Pass |

## Performance

Measured with 200 series whose watched episodes were deleted, so the plugin adds 200 series
(`limit=24&enableResumable=false`, 5 to 10 calls each, after warm-up):

| Situation | Time per call |
|---|---|
| Without the plugin (server returns 1 item) | 25–29 ms |
| With the plugin, cache warm | 23–25 ms |
| With the plugin, right after a user data change (cache cleared) | 43–53 ms |

The first call after a server start takes about 0.9 s with or without the plugin (warm-up).

An earlier version checked resume points per episode through `Video.GetAllVersions()`, which hits
the database for each episode and added about 230 ms. The lookup now gets all resumable episodes
in one `IsResumable` query.
