# EPG Channel Names (Jellyfin 10.11 plugin)

Renames Live TV channels to whatever the EPG says is airing now.
`NHL GAME 01 Old Team vs Stale Team` → `NHL GAME 01 · Oilers vs Flames`.

Jellyfin resets channel names to the M3U name on every "Refresh Guide". This plugin re-applies the EPG name right after each guide refresh, plus every 15 minutes.

## Install (manual, 2 minutes)
1. `./build.sh` (needs Docker only)
2. Copy `Jellyfin.Plugin.EpgChannelNames/bin/Release/net9.0/Jellyfin.Plugin.EpgChannelNames.dll` into `<jellyfin config>/plugins/EpgChannelNames/`
3. Restart Jellyfin

## Install (plugin repository)
1. `./build.sh 1.0.0.0 <url-where-zip-is-hosted>`
2. Upload `dist/epg-channel-names_1.0.0.0.zip` and `dist/manifest.json` (e.g. a GitHub release)
3. Dashboard → Plugins → Repositories → add the `manifest.json` URL, then install "EPG Channel Names" from the catalog

## Configure
Dashboard → Plugins → EPG Channel Names:
- **Channels to rename**: regex on the original name. Default `^NHL GAME \d+`. Other channels are untouched.
- **Name while airing**: default `{prefix} · {program}`. Tokens: `{program}`, `{prefix}` (the regex match), `{original}`, `{number}`.
- **Name when nothing is airing**: default `{prefix}`.

Run now: Dashboard → Scheduled Tasks → Live TV → "Rename channels from EPG".

## Tests
- `./test.sh`: unit tests (Docker only)
- `./test.sh --integration`: also runs the plugin in a real Jellyfin 10.11 container

## Requirement
Each game channel needs EPG data in Dispatcharr (a real EPG source or a Dispatcharr dummy EPG that puts the matchup in the program title). No program = no new name.

## License
GPL-3.0. See [LICENSE](LICENSE).
