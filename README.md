# EPG Channel Names (Jellyfin 10.11 plugin)

Renames Live TV channels to whatever the EPG says is airing now.
`NHL GAME 01 Old Team vs Stale Team` → `NHL GAME 01 · Oilers vs Flames`.

Jellyfin resets channel names to the M3U name on every "Refresh Guide". This plugin re-applies the EPG name right after each guide refresh, plus every 15 minutes.

## Install (plugin repository, recommended)
1. Dashboard → Plugins → Repositories (or Catalog → gear icon) → **+**
2. Name: `EPG Channel Names`
   URL: `https://raw.githubusercontent.com/Yofuness/jellyfin-epg-sync/main/manifest.json`
3. Save, open the Catalog, and install **EPG Channel Names**
4. Restart Jellyfin

Updates: the release build adds each new version to `manifest.json` on `main` only after the zip is attached, so Jellyfin offers new versions automatically and never sees a half-finished release. Older versions stay listed for rollback.

Upgrading from the old `releases/latest/download/manifest.json` URL: it still works, but switch to the URL above.

## Install (manual)
1. `./build.sh` (needs Docker only)
2. Copy `Jellyfin.Plugin.EpgChannelNames/bin/Release/net9.0/Jellyfin.Plugin.EpgChannelNames.dll` into `<jellyfin config>/plugins/EpgChannelNames/`
3. Restart Jellyfin

## Releasing
Publish a GitHub release with a tag like `v1.0.1`. The Release workflow runs the tests, builds the plugin, attaches the zip and `manifest.json` to the release, then commits the new version into `manifest.json` on `main`.

## Configure
Dashboard → Plugins → EPG Channel Names. Run it now: Dashboard → Scheduled Tasks → Live TV → "Rename channels from EPG".

### Dispatcharr (recommended)
1. In Dispatcharr, create an API key for your user.
2. Set **Dispatcharr URL** (e.g. `http://localhost:9191`) and **API key**, then Save.

The plugin reads every Dispatcharr channel (3 requests) and matches Jellyfin channels by channel number. Then:

**Event channels** are detected automatically: any channel whose stream name looks like `name : event` (`CA - NHL GAME 01 : DUCKS @ RANGERS OCT 19 – 7:00 PM ET`, `UK - TNT SPORTS | EVENT 2 HD : Major League Baseball`, `UK - GAATV 004 : …`).
- The channel is renamed to `{prefix} · {program}`, e.g. `CA - NHL GAME 01 · DUCKS @ RANGERS OCT 19 – 7:00 PM ET`, or just the prefix when nothing is on.
- The program airing now gets the event title, the full stream name as description, `Live` + `Sports` tags (or the group's category), so it tops "On Now" and shows in its row.
- Later guide blocks stay plain until they start airing. With nothing on, the airing placeholder is hidden; the channel stays.

**Programs page rows by group**: Jellyfin sorts programs into rows (Shows, Movies, Sports, News, Kids) by category, and most provider EPGs have none. Map Dispatcharr groups to rows, one per line:
```
AM | CA | DOCUMENTARY = Shows
AM | CA | NHL PPV = Sports
EU | UK | TNT SPORTS = Sports
EU | UK | SKY SPORTS+ = Sports
EU | UK | SPORTS = Sports
```
Group names must match Dispatcharr (spaces don't matter; a typo is logged with the real group names). Removing a rule undoes it. Programs on regular channels keep their real titles; only the row changes.

Other settings:
- **Only channels matching**: optional regex on the channel name to limit what's touched. Empty = all.
- **Name while airing / when nothing is airing**: tokens `{program}`, `{prefix}`, `{original}`, `{number}`.
- **Event channel regex**: how event channels and titles are read from stream names.

If Dispatcharr is unreachable, channels keep their last names until the next guide refresh.

### Without Dispatcharr (EPG-only)
Set **Only channels matching** (e.g. `NHL GAME \d+`). Those channels are renamed to the EPG program airing now.

### Upgrading from 1.x
Channel names now keep the full prefix (`CA - NHL GAME 01 · …` instead of `NHL GAME 01 · …`). A saved `NHL GAME \d+` filter is cleared automatically, so all event channels are handled.

## Tests
- `./test.sh`: unit tests (Docker only)
- `./test.sh --integration`: also runs the plugin in a real Jellyfin 10.11 container

## Requirement
Either Dispatcharr connected (above), or EPG data for each game channel. With neither, the channel keeps the "nothing airing" name.

## License
GPL-3.0. See [LICENSE](LICENSE).
