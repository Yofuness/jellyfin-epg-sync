# EPG Channel Names (Jellyfin 10.11 plugin)

Keeps Jellyfin Live TV in sync with Dispatcharr: event channels show what's on (`CA - NHL GAME 01 · DUCKS @ RANGERS`), and sports programs show the teams playing (`Live NHL Hockey: Flyers vs Avalanche`).

## Quick start (5 minutes)
1. Jellyfin: Dashboard → Plugins → Repositories → **+**, URL `https://raw.githubusercontent.com/Yofuness/jellyfin-epg-sync/main/manifest.json`, Save.
2. Catalog → install **EPG Channel Names** → restart Jellyfin.
3. Dispatcharr: create an API key for your user.
4. Jellyfin: Dashboard → Plugins → **EPG Channel Names** → fill in **Dispatcharr URL** (e.g. `http://192.168.1.10:9191`) and **API key** → Save.

That's all. It runs after every guide refresh and every 15 minutes. To see it right away: Dashboard → Scheduled Tasks → Live TV → **Rename channels from EPG** → run.

**Without step 4 the plugin does nothing** (it logs "Nothing to do"). With the URL and key set, the defaults do this:
- **Event channels** (stream name like `CA - NHL GAME 01 : DUCKS @ RANGERS …`) are renamed to the event, or back to the plain name when nothing is on. The guide block covering the game's start time ("7:00 PM ET" in the stream name) gets the event title, so tonight's games are listed hours before they start. It gets the event title and the Live + Sports tags, so it appears in On Now and the Programs page's Sports row.
- **Empty event channels**: the current placeholder program is hidden, so only real games show as live.
- **Finished events** left in stream names (start time more than 6 hours ago) count as nothing on.
- **Team names**: generic sports titles like "Live NHL Ice Hockey" get the teams from the description. Team list from ESPN, refreshed monthly.
- **Cricket and F1**: "Test Cricket" becomes `Test Cricket: South Africa vs Australia · Day 3`; "Formula 1 Motor Racing" becomes `F1: United States GP 2018` (year shown only for replays, plus Qualifying / Sprint / Practice / Highlights when the description says so). Tennis guides carry no player names, so tennis titles stay as they are.
- **Team logos**: games where both teams are known (team sports, and event channels like `DUCKS @ RANGERS`) get a card with both team logos and the league logo (NHL, NBA, Premier League…) from ESPN. Other programs without an image use the channel logo. Real program images are never replaced.
- **Programs page rows**: each channel's programs go into a row picked from its Dispatcharr group name (`… SPORTS` / `NHL PPV` = Sports, `DOCUMENTARY` = Shows, `NEWS` = News, `KIDS` = Kids, `MOVIES` / `CINEMA` = Movies). Groups that match nothing get no row.
- **One card per show**: back-to-back blocks of the same show on one channel ("Live Tennis" at 4:00 and 5:00) get one Programs-page card; the later blocks stay in the Guide. Toggle: *One card per show on the Programs page*.
- Placeholder programs (titled like their channel, or "Channel No Longer Available") are left alone.


## Install (plugin repository) in detail
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
- Programs without an image show their channel's logo, so Programs-page cards are recognizable.
- Finished events that the provider leaves in stream names are ignored: if the title has a start time (`OCT 17 – 5:30 PM UK`) more than 6 hours ago (setting), the channel counts as nothing on.

**Regular channels** keep their names and guide. Sports programs with a generic title get the teams from their description: "Live NHL Ice Hockey" + "Philadelphia Flyers v Colorado Avalanche" becomes **Live NHL Hockey: Flyers vs Avalanche**. Covers NHL, NFL, NBA, MLB, MLS, Premier League, Championship, LaLiga, Champions League, Premiership Rugby, URC, Champions Cup and GAA counties. Teams come from ESPN's public API (no key) and are refreshed monthly by the *Refresh sports team list* scheduled task. Only programs with exactly two teams of one league are renamed. Turn it off with *Put team names in sports program titles*; the EPG titles come back.

**Programs page rows by group**: Jellyfin sorts programs into rows (Shows, Movies, Sports, News, Kids) by category, and most provider EPGs have none. The row is picked automatically from the group name. To change one, open the plugin settings: the **Dispatcharr groups** table lists every group with channels; pick a row or **Off** for any group and Save. Groups not in Dispatcharr can go in **Custom rules**, one per line (`None` = no row):
```
UK | SPORTS NEWS = Sports
UK | MUSIC SHOWS = None
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
