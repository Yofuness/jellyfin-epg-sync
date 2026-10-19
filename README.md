# EPG Channel Names (Jellyfin 10.11 plugin)

Renames Live TV channels to whatever the EPG says is airing now.
`NHL GAME 01 Old Team vs Stale Team` → `NHL GAME 01 · Oilers vs Flames`.

Jellyfin resets channel names to the M3U name on every "Refresh Guide". This plugin re-applies the EPG name right after each guide refresh, plus every 15 minutes.

## Install (plugin repository, recommended)
1. Dashboard → Plugins → Repositories (or Catalog → gear icon) → **+**
2. Name: `EPG Channel Names`
   URL: `https://github.com/Yofuness/jellyfin-epg-sync/releases/latest/download/manifest.json`
3. Save, open the Catalog, and install **EPG Channel Names**
4. Restart Jellyfin

Updates: the URL always points at the newest release, so Jellyfin offers new versions automatically.

## Install (manual)
1. `./build.sh` (needs Docker only)
2. Copy `Jellyfin.Plugin.EpgChannelNames/bin/Release/net9.0/Jellyfin.Plugin.EpgChannelNames.dll` into `<jellyfin config>/plugins/EpgChannelNames/`
3. Restart Jellyfin

## Releasing
Publish a GitHub release with a tag like `v1.0.1`. The Release workflow runs the tests, builds the plugin, and attaches the zip and `manifest.json` to the release.

## Configure
Dashboard → Plugins → EPG Channel Names:
- **Channels to rename**: regex on the original name. Default `NHL GAME \d+` (also matches `CA - NHL GAME 01`). Other channels are untouched.
- **Name while airing**: default `{prefix} · {program}`. Tokens: `{program}`, `{prefix}` (the regex match), `{original}`, `{number}`.
- **Name when nothing is airing**: default `{prefix}`.

### Dispatcharr (recommended for game channels)
Many providers ship game channels with an empty or stale EPG; the matchup only lives in the stream name (`CA - NHL GAME 01 : DUCKS @ RANGERS OCT 19 – 7:00 PM ET`). Connect Dispatcharr and the plugin reads it from there:
1. In Dispatcharr, create an API key for your user.
2. In the plugin settings, set **Dispatcharr URL** (e.g. `http://localhost:9191`) and **API key**, then Save.
3. Optional: **Game title regex** controls what part of the stream name is used. Default: text after `:` up to `/`.

Channels are matched by channel number. If Dispatcharr is unreachable, the plugin falls back to the EPG.

Run now: Dashboard → Scheduled Tasks → Live TV → "Rename channels from EPG".

## Tests
- `./test.sh`: unit tests (Docker only)
- `./test.sh --integration`: also runs the plugin in a real Jellyfin 10.11 container

## Requirement
Either Dispatcharr connected (above), or EPG data for each game channel. With neither, the channel keeps the "nothing airing" name.

## License
GPL-3.0. See [LICENSE](LICENSE).
