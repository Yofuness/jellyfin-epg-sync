# CLAUDE.md

Jellyfin 10.11 plugin (C#, net9.0) that renames Live TV channels to the EPG program currently airing. Built for Dispatcharr channels like `NHL GAME 01 Team A vs Team B`.

## Build
- No local .NET SDK. Build only with Docker: `./build.sh [version] [repo-base-url]`
- Output: `Jellyfin.Plugin.EpgChannelNames/bin/Release/net9.0/*.dll`, plus `dist/` zip and `manifest.json`
- `build.sh` runs as the host user. If `bin/` or `obj/` end up root-owned, remove them through a container, e.g. `docker run --rm -v "$PWD/Jellyfin.Plugin.EpgChannelNames":/src alpine rm -rf /src/bin /src/obj`

## Releases
`.github/workflows/release.yml` runs when a GitHub release is published. It turns the tag (`v1.2.3`) into a 4-part version, runs `./test.sh`, then `./build.sh`, and uploads `dist/*` to the release. The plugin repository URL is `releases/latest/download/manifest.json`.
Test it locally with `act` (the upload step is skipped when `ACT` is set). `--bind` is required because the scripts start Docker containers that mount the repo:
`echo '{"action":"published","release":{"tag_name":"v1.0.0"}}' > /tmp/ev.json && act release -e /tmp/ev.json --bind -P ubuntu-latest=catthehacker/ubuntu:act-latest`

## Layout
- `Dispatcharr/`: `IDispatcharrClient` + `DispatcharrClient`. Reads `GET /api/channels/channels/` (unpaginated array, `channel_number` is a float like `220.0`) and `GET /api/channels/channels/{id}/streams/` with the `X-API-Key` header. Results are keyed by channel number formatted like the M3U `tvg-chno` (`220`).
- `ChannelRenamer.cs`: core logic. Queries `LiveTvChannel`, then the airing `LiveTvProgram`, applies the name template.
- `RenameChannelsTask.cs`: scheduled task (startup + interval). Key `EpgChannelNamesRename`.
- `GuideRefreshListener.cs`: reruns the task when Jellyfin's `RefreshGuide` task completes.
- `ServiceRegistrator.cs`: DI registration.
- `Configuration/`: settings class + embedded `configPage.html`. Plugin GUID `4f1c7a3e-6b2d-4e8a-9c51-0d7e2a9b3f64` (keep in sync with `Plugin.cs`, the config page, and `build.sh`).

## Key behavior (non-obvious)
- Title source order: Dispatcharr stream name (via `StreamTitleRegex`), then the EPG program airing now, then `NoProgramTemplate`. Real-world providers often have empty or stale EPG for game channels; only the stream name is fresh after each M3U refresh.
- When Dispatcharr lists a channel, its stream name is authoritative: an empty game ("CA - NHL GAME 05 : ") means nothing airing, with NO EPG fallback. Dispatcharr's placeholder EPG for these channels just repeats the channel name. EPG is used only when Dispatcharr is off, down, or does not list the channel.
- For Dispatcharr-listed channels (`RenamePrograms`, default on), programs overlapping now to now+`ProgramWindowHours` (12) are renamed to the game title (or the no-game channel name). The Programs page and guide show program titles, which are otherwise Dispatcharr's stale placeholder. Programs with titles from a real EPG are never touched.
- Consequence: if Dispatcharr goes down, the EPG fallback reads the renamed program, so the last known game sticks until the next guide refresh restores the EPG titles. The integration test asserts this.
- Every run logs a summary ("Checked N channels: M matched ..."), and warns when nothing matches the regex.
- Dispatcharr errors (HTTP, JSON, bad URL, timeout) are logged and fall back to the EPG. They never fail the task.
- Jellyfin resets channel names to the M3U name on every guide refresh. The plugin stores `EpgChannelNames.Original` and `EpgChannelNames.Applied` in `channel.ProviderIds`. Any name different from `Applied` counts as a fresh original.
- The regex matches against the original name, never the renamed one.
- Target ABI 10.11. `Jellyfin.Controller`/`Jellyfin.Model` are pinned to 10.11.0, the oldest 10.11. Building against a newer patch (e.g. 10.11.11) fails to load on older servers ("Could not load MediaBrowser.Common, Version=10.11.11.0"). Never use a wildcard. Jellyfin 12.x needs a separate build.

## Testing
- `./test.sh`: unit tests (xUnit + Moq) in `Jellyfin.Plugin.EpgChannelNames.Tests/`, about 30 s. Run after every change.
- `./test.sh --integration`: also boots `jellyfin/jellyfin:10.11.0` in Docker with a fake M3U + XMLTV, checks the rename, swaps the game, then adds a fake Dispatcharr (static JSON via python http.server) and checks stream titles win and the Dispatcharr-down fallback. `expect` retries for 30 s because the rename task is queued after guide refreshes. About 2 min.
- Tests inject config through the internal `ChannelRenamer(libraryManager, logger, getConfig)` constructor and set the static `BaseItem.LibraryManager` to a mock, so the test class runs in one serial collection.
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
