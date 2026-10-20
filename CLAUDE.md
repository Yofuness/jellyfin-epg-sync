# CLAUDE.md

Jellyfin 10.11 plugin (C#, net9.0) that renames Live TV channels to the EPG program currently airing. Built for Dispatcharr channels like `NHL GAME 01 Team A vs Team B`.

## Build
- No local .NET SDK. Build only with Docker: `./build.sh [version] [repo-base-url]`
- Output: `Jellyfin.Plugin.EpgChannelNames/bin/Release/net9.0/*.dll`, plus `dist/` zip and `manifest.json`
- `build.sh` runs as the host user. If `bin/` or `obj/` end up root-owned, remove them through a container, e.g. `docker run --rm -v "$PWD/Jellyfin.Plugin.EpgChannelNames":/src alpine rm -rf /src/bin /src/obj`

## Releases
- The release workflow commits `manifest.json` to `main` as github-actions[bot] after every release. Always `git fetch` and rebase on `origin/main` before pushing.
`.github/workflows/release.yml` runs when a GitHub release is published. It turns the tag (`v1.2.3`) into a 4-part version, runs `./test.sh`, then `./build.sh`, and uploads `dist/*` to the release. The plugin repository URL is `https://raw.githubusercontent.com/Yofuness/jellyfin-epg-sync/main/manifest.json`: root `manifest.json`, all versions newest-first. The last workflow step merges the release's manifest into it with `scripts/merge-manifest.py` (same version replaces, otherwise prepends) and pushes to `main` as github-actions[bot], only after the zip upload succeeds. The old `releases/latest/download/manifest.json` still works but 404s for about 2 min after each release while the build runs. v1.0.0 is deliberately absent (built against 10.11.11; fails to load on older 10.11). Under `act` the push step is skipped; "Check manifest merge" runs on a temp copy.
Test it locally with `act` (the upload step is skipped when `ACT` is set). `--bind` is required because the scripts start Docker containers that mount the repo:
`echo '{"action":"published","release":{"tag_name":"v1.0.0"}}' > /tmp/ev.json && act release -e /tmp/ev.json --bind -P ubuntu-latest=catthehacker/ubuntu:act-latest`

## Layout
- `Dispatcharr/`: `IDispatcharrClient.GetChannelsAsync` → `DispatcharrChannel(Number, Name, Group, StreamName)`. 3 requests with `X-API-Key`: `GET api/channels/channels/` (unpaginated array; `channel_number` float like `220.0`, `channel_group_id`, `streams` ids in priority order), `GET api/channels/groups/` (~1700 groups, unpaginated), `POST api/channels/streams/by-ids/ {"ids":[...]}` for the first stream of each channel. Sent with Content-Length (`StringContent`); the fake server needs it. Number formatted like M3U `tvg-chno` (`220`). Group names normalized (`NormalizeGroupName`): the provider's NHL group contains U+00A0.
- `ChannelRenamer.cs`: core logic. Per Jellyfin channel: Dispatcharr event channel (`ParseEvent` on the stream name) → rename + `ApplyEventProgramsAsync`; other Dispatcharr channel → `ApplyCategoryAsync` only (never renamed); not in Dispatcharr + filter matches → EPG-only rename.
- `CategoryRules.cs`: parses `group = Shows|Movies|Sports|News|Kids` and applies a category reversibly. Shows=`IsSeries`, Movies=`IsMovie`, others=tags. What the plugin set is recorded in `program.ProviderIds["EpgChannelNames.Category"]` (`series`, `movie`, `tag:X`), so removing or changing a rule undoes exactly that and never EPG-provided flags.
- `RenameChannelsTask.cs`: scheduled task (startup + interval). Key `EpgChannelNamesRename`.
- `GuideRefreshListener.cs`: reruns the task when Jellyfin's `RefreshGuide` task completes.
- `ServiceRegistrator.cs`: DI registration.
- `Configuration/`: settings class + embedded `configPage.html`. Plugin GUID `4f1c7a3e-6b2d-4e8a-9c51-0d7e2a9b3f64` (keep in sync with `Plugin.cs`, the config page, and `build.sh`).

## Key behavior (non-obvious)
- v2 model: event channels are detected from the stream name with `EventRegex` (`prefix : title / …`). Empty title = nothing on now, with no EPG fallback (Dispatcharr's EPG for these is a placeholder repeating the channel name). `{prefix}` is the full stream part before ":" (`CA - NHL GAME 01`), per the user's choice to keep country codes.
- `ChannelNameRegex` is an optional filter (empty = all). It is required only in EPG-only mode (no Dispatcharr, or channel not listed). `Plugin` ctor clears the v1 defaults `NHL GAME \d+` / `^NHL GAME \d+` once (`IsOldDefaultRegex`).
- Placeholders are never categorized (`IsPlaceholder`): program title equals the channel's own name (Jellyfin original, Dispatcharr name or stream name), ignoring HD/FHD/SD/HEVC/UHD/4K/◉/², or the title is in `IgnoredProgramTitles` (default "Channel No Longer Available"). On the user's server ~half of all airing programs are such placeholders (88 channels have no real guide); v2.0.0 tagged them, flooding the Sports section with ~1300 programs. `Apply(null)` on them also undoes those tags.
- Category rules apply to every real program in the window on regular channels, and to the airing block only on event channels (events with no rule default to Sports). Unknown rule groups are warned about with the list of real groups.
- Programs on Dispatcharr-listed channels (`ChannelRenamer.ApplyProgramsAsync`, window now to now+`ProgramWindowHours`): only the block **airing now** (`IsAiring`: start <= now < end) gets the game title, `GameProgramTags` and the full stream name as `Overview`. Every later block becomes a plain untagged placeholder (`NoProgramTemplate` name, empty overview, game tags stripped), so the Sports row lists only watchable games (later blocks only offer Record in the web UI). The next block gets the game within one run (15 min) of starting. Real-EPG programs are never touched.
- No game (`RemoveNoGamePrograms`, default on): only the **airing** placeholder is deleted (`DeleteItem` with `DeleteFileLocation=false, DeleteFromExternalProvider=false, notifyParentItem=false`, as in Jellyfin's `GuideManager.CleanDatabase`). Later blocks are kept, so a game the provider adds later still has a block to rename. Guide refresh re-adds deleted ones; the post-refresh run deletes again.
- `GameProgramTags` default `Live,Sports`. Jellyfin derives `IsLive`/`IsSports` from `Tags`. `Live` is +1 in the "On Now" ranking (`LiveTvManager.GetRecommendationScore`; a favorite channel is +3, per user only). `Sports` lists the program in the web Programs page's Sports row. The web client asks for 12 per row.
- If Dispatcharr goes down, channels keep their last names; after the next guide refresh they show the raw tuner names until Dispatcharr is back (unless a filter enables EPG-only mode). The integration test asserts this.
- Every run logs a summary ("Checked N channels: X from Dispatcharr, E event channels (L with an event now), M EPG-mode; R renamed; programs: U updated, D removed, C categorized").
- Dispatcharr errors (HTTP, JSON, bad URL, timeout) are logged as warnings and never fail the task.
- Jellyfin resets channel names to the M3U name on every guide refresh. The plugin stores `EpgChannelNames.Original` and `EpgChannelNames.Applied` in `channel.ProviderIds`. Any name different from `Applied` counts as a fresh original.
- The filter regex matches against the original name, never the renamed one.
- Target ABI 10.11. `Jellyfin.Controller`/`Jellyfin.Model` are pinned to 10.11.0, the oldest 10.11. Building against a newer patch (e.g. 10.11.11) fails to load on older servers ("Could not load MediaBrowser.Common, Version=10.11.11.0"). Never use a wildcard. Jellyfin 12.x needs a separate build.

## Testing
- `./test.sh`: unit tests (xUnit + Moq) in `Jellyfin.Plugin.EpgChannelNames.Tests/`, about 30 s. Run after every change.
- `./test.sh --integration`: also boots `jellyfin/jellyfin:10.11.0` in Docker with a fake M3U + XMLTV, checks the rename, swaps the game, then switches to a fake Dispatcharr (`scripts/fake_dispatcharr.py`: GET channels/groups, POST by-ids, real 0.32 shapes) with NHL, TNT, History and CBC channels, and checks names, the Sports and Shows rows (exact web-client row queries), rule removal, and Dispatcharr-down behavior. Phase 1 uses filter `NHL GAME \d\d` because `\d+` would be migrated away. `expect` retries for 30 s because the rename task is queued after guide refreshes. About 2 min.
- Tests inject config through the internal `ChannelRenamer(libraryManager, logger, dispatcharr, getConfig)` constructor and set the static `BaseItem.LibraryManager` to a mock, so the test class runs in one serial collection.
- Name templating lives in the pure `ChannelRenamer.BuildName`. Test new tokens there.
- Jellyfin caches XMLTV in `/cache/xmltv`. The integration test clears it before the second refresh.

## Code standards
Jellyfin has no written C# style guide. We follow the official plugin template (github.com/jellyfin/jellyfin-plugin-template), which the build enforces:
- `.editorconfig` and `Directory.Build.props` are copied unchanged from the template. Don't edit the style rules.
- `TreatWarningsAsErrors` + `AnalysisMode=AllEnabledByDefault` + StyleCop, Serilog and Multithreading analyzers. The build must stay at 0 warnings.
- Every public member needs an XML doc comment (`<inheritdoc />` for interface/override members). Property summaries start with "Gets or sets".
- Fix analyzer findings instead of suppressing them. Any `#pragma warning disable` or `NoWarn` needs a comment explaining why.
- Version lives in `Directory.Build.props`. `build.sh` overrides it per release.
- License: GPL-3.0 (matches the Jellyfin plugin template).
- Conduct: Jellyfin Community Standards, https://jellyfin.org/docs/general/community-standards/
