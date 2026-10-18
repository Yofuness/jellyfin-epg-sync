# CLAUDE.md

Jellyfin 10.11 plugin (C#, net9.0) that renames Live TV channels to the EPG program currently airing. Built for Dispatcharr channels like `NHL GAME 01 Team A vs Team B`.

## Build
- No local .NET SDK. Build only with Docker: `./build.sh [version] [repo-base-url]`
- Output: `Jellyfin.Plugin.EpgChannelNames/bin/Release/net9.0/*.dll`, plus `dist/` zip and `manifest.json`
- `build.sh` runs as the host user. If `bin/` or `obj/` end up root-owned, remove them through a container, e.g. `docker run --rm -v "$PWD/Jellyfin.Plugin.EpgChannelNames":/src alpine rm -rf /src/bin /src/obj`

## Layout
- `ChannelRenamer.cs`: core logic. Queries `LiveTvChannel`, then the airing `LiveTvProgram`, applies the name template.
- `RenameChannelsTask.cs`: scheduled task (startup + interval). Key `EpgChannelNamesRename`.
- `GuideRefreshListener.cs`: reruns the task when Jellyfin's `RefreshGuide` task completes.
- `ServiceRegistrator.cs`: DI registration.
- `Configuration/`: settings class + embedded `configPage.html`. Plugin GUID `4f1c7a3e-6b2d-4e8a-9c51-0d7e2a9b3f64` (keep in sync with `Plugin.cs`, the config page, and `build.sh`).

## Key behavior (non-obvious)
- Jellyfin resets channel names to the M3U name on every guide refresh. The plugin stores `EpgChannelNames.Original` and `EpgChannelNames.Applied` in `channel.ProviderIds`. Any name different from `Applied` counts as a fresh original.
- The regex matches against the original name, never the renamed one.
- Target ABI 10.11 (`Jellyfin.Controller` 10.11.*). Jellyfin 12.x needs a separate build.

## Testing
Run `jellyfin/jellyfin:10.11.11` in Docker with the DLL mounted in `/config/plugins/EpgChannelNames/`, a sample M3U, and XMLTV. Finish setup through the `/Startup/*` API, add the tuner and listings, then trigger Refresh Guide by its task Id from `/ScheduledTasks`. Check names with `/LiveTv/Channels`.

## Code standards
Jellyfin has no written C# style guide. We follow the official plugin template (github.com/jellyfin/jellyfin-plugin-template), which the build enforces:
- `.editorconfig` and `Directory.Build.props` are copied unchanged from the template. Don't edit the style rules.
- `TreatWarningsAsErrors` + `AnalysisMode=AllEnabledByDefault` + StyleCop, Serilog and Multithreading analyzers. The build must stay at 0 warnings.
- Every public member needs an XML doc comment (`<inheritdoc />` for interface/override members). Property summaries start with "Gets or sets".
- Fix analyzer findings instead of suppressing them. Any `#pragma warning disable` or `NoWarn` needs a comment explaining why.
- Version lives in `Directory.Build.props`. `build.sh` overrides it per release.
- Conduct: Jellyfin Community Standards, https://jellyfin.org/docs/general/community-standards/
