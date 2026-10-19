#!/usr/bin/env bash
# Unit tests in a .NET 9 container. Add --integration to also run the plugin
# inside a real Jellyfin 10.11 server (Docker) against a fake M3U + XMLTV.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
SDK=mcr.microsoft.com/dotnet/sdk:9.0
# Oldest supported server: proves the plugin loads on every 10.11.x.
JF_IMAGE=jellyfin/jellyfin:10.11.0
PORT=18097

echo "== Unit tests"
docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp -v "$ROOT":/src -w /src/Jellyfin.Plugin.EpgChannelNames.Tests "$SDK" \
  dotnet test --nologo -v q

[[ "${1:-}" == "--integration" ]] || exit 0

echo "== Integration test ($JF_IMAGE)"
docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp -v "$ROOT":/src -w /src/Jellyfin.Plugin.EpgChannelNames "$SDK" \
  dotnet build -c Release --nologo -v q >/dev/null

WORK="$(mktemp -d)"
NAME="epgcn-it-$$"
NET="epgcn-net-$$"
FAKE="epgcn-dispatcharr-$$"
cleanup() {
  docker rm -f "$NAME" "$FAKE" >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
  # Jellyfin writes as root inside the container; delete through a container too.
  docker run --rm -v "$WORK":/w alpine rm -rf /w/config /w/tv /w/fake >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
trap cleanup EXIT

mkdir -p "$WORK/config/plugins/EpgChannelNames" "$WORK/tv"
cp "$ROOT/Jellyfin.Plugin.EpgChannelNames/bin/Release/net9.0/Jellyfin.Plugin.EpgChannelNames.dll" "$WORK/config/plugins/EpgChannelNames/"
cat > "$WORK/tv/ch.m3u" <<'M3U'
#EXTM3U
#EXTINF:-1 tvg-id="nhl01" tvg-chno="101",NHL GAME 01 Old Team vs Stale Team
http://127.0.0.1/none.ts
#EXTINF:-1 tvg-id="news" tvg-chno="5",CBC News
http://127.0.0.1/none2.ts
M3U
write_epg() {
  local a b
  a=$(date -u -d '-1 hour' +%Y%m%d%H%M%S); b=$(date -u -d '+2 hour' +%Y%m%d%H%M%S)
  cat > "$WORK/tv/epg.xml" <<XML
<?xml version="1.0"?><tv>
<channel id="nhl01"><display-name>NHL GAME 01</display-name></channel>
<channel id="news"><display-name>CBC News</display-name></channel>
<programme start="$a +0000" stop="$b +0000" channel="nhl01"><title>$1</title></programme>
<programme start="$a +0000" stop="$b +0000" channel="news"><title>The National</title></programme>
</tv>
XML
}
write_epg "Maple Leafs vs Canadiens"

# Fake Dispatcharr: static JSON served at the real API paths (index.html per directory).
mkdir -p "$WORK/fake/api/channels/channels/42/streams"
cat > "$WORK/fake/api/channels/channels/index.html" <<'JSON'
[{"id": 42, "name": "CA - NHL GAME 01", "channel_number": 101.0, "streams": [7]},
 {"id": 43, "name": "CBC News", "channel_number": 5.0, "streams": [8]}]
JSON
cat > "$WORK/fake/api/channels/channels/42/streams/index.html" <<'JSON'
[{"id": 7, "name": "CA - NHL GAME 01 : ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19 – 7:00 PM ET / OCT 20 – 12:00 AM UK"}]
JSON
docker network create "$NET" >/dev/null
docker run -d --name "$FAKE" --network "$NET" --network-alias dispatcharr -v "$WORK/fake":/srv:ro -w /srv python:3.12-alpine \
  python -m http.server 8000 >/dev/null

docker run -d --name "$NAME" --network "$NET" -p "$PORT:8096" -v "$WORK/config":/config -v "$WORK/tv":/tv "$JF_IMAGE" >/dev/null
J="http://localhost:$PORT"
# /System/Info/Public answers before the startup wizard endpoints are ready.
for _ in $(seq 90); do curl -sf "$J/Startup/Configuration" >/dev/null && break; sleep 2; done

# Throwaway admin for this disposable server only.
H='Content-Type: application/json'
CLIENT='MediaBrowser Client="it", Device="it", DeviceId="it", Version="1"'
curl -sf -X POST "$J/Startup/Configuration" -H "$H" -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
curl -sf "$J/Startup/User" >/dev/null
curl -sf -X POST "$J/Startup/User" -H "$H" -d '{"Name":"it","Password":"it-pass"}' >/dev/null
curl -sf -X POST "$J/Startup/Complete" >/dev/null
TOK=$(curl -sf -X POST "$J/Users/AuthenticateByName" -H "$H" -H "Authorization: $CLIENT" -d '{"Username":"it","Pw":"it-pass"}' \
  | python3 -c 'import json,sys;print(json.load(sys.stdin)["AccessToken"])')
A="Authorization: $CLIENT, Token=\"$TOK\""

curl -sf -X POST "$J/LiveTv/TunerHosts" -H "$H" -H "$A" -d '{"Type":"m3u","Url":"/tv/ch.m3u"}' >/dev/null
curl -sf -X POST "$J/LiveTv/ListingProviders?validateListings=false&validateLogin=false" -H "$H" -H "$A" \
  -d '{"Type":"xmltv","Path":"/tv/epg.xml","EnableAllTuners":true}' >/dev/null

GUIDE_ID=$(curl -sf "$J/ScheduledTasks" -H "$A" | python3 -c 'import json,sys;print(next(t["Id"] for t in json.load(sys.stdin) if t["Key"]=="RefreshGuide"))')

refresh_and_wait() {
  # Wait out any run already in progress (adding a listing provider starts one).
  state() { curl -sf "$J/ScheduledTasks/$GUIDE_ID" -H "$A" | python3 -c 'import json,sys;print(json.load(sys.stdin)["State"])'; }
  for _ in $(seq 60); do [[ "$(state)" == Idle ]] && break; sleep 1; done
  curl -sf -X POST "$J/ScheduledTasks/Running/$GUIDE_ID" -H "$A" >/dev/null
  sleep 2
  for _ in $(seq 60); do [[ "$(state)" == Idle ]] && break; sleep 1; done
  sleep 3 # the rename task is queued right after the guide finishes
}

channel_names() {
  curl -sf "$J/LiveTv/Channels" -H "$A" | python3 -c 'import json,sys;print("\n".join(c["Name"] for c in json.load(sys.stdin)["Items"]))'
}

expect() {
  local names; names="$(channel_names)"
  if grep -qxF "$1" <<<"$names"; then echo "PASS: $1"; else echo "FAIL: expected '$1', got:"; echo "$names"; exit 1; fi
}

refresh_and_wait
expect "NHL GAME 01 · Maple Leafs vs Canadiens"
expect "CBC News"

# Next night's game: Jellyfin resets the name, plugin must apply the new one.
write_epg "Oilers vs Flames"
# Jellyfin caches the XMLTV file; drop the cache so it rereads the new guide.
docker exec "$NAME" sh -c 'rm -rf /cache/xmltv/*'
refresh_and_wait
expect "NHL GAME 01 · Oilers vs Flames"

# Dispatcharr stream names win over the EPG.
PLUGIN_ID=4f1c7a3e-6b2d-4e8a-9c51-0d7e2a9b3f64
RENAME_ID=$(curl -sf "$J/ScheduledTasks" -H "$A" | python3 -c 'import json,sys;print(next(t["Id"] for t in json.load(sys.stdin) if t["Key"]=="EpgChannelNamesRename"))')
set_dispatcharr() {
  curl -sf "$J/Plugins/$PLUGIN_ID/Configuration" -H "$A" \
    | python3 -c 'import json,sys;c=json.load(sys.stdin);c["DispatcharrUrl"]=sys.argv[1];c["DispatcharrApiKey"]="test-key";print(json.dumps(c))' "$1" \
    | curl -sf -X POST "$J/Plugins/$PLUGIN_ID/Configuration" -H "$H" -H "$A" -d @- >/dev/null
}
rename_and_wait() {
  curl -sf -X POST "$J/ScheduledTasks/Running/$RENAME_ID" -H "$A" >/dev/null
  sleep 4
}
set_dispatcharr "http://dispatcharr:8000"
rename_and_wait
expect "NHL GAME 01 · ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19 – 7:00 PM ET"
expect "CBC News"

# Dispatcharr down: fall back to the EPG instead of failing.
docker stop "$FAKE" >/dev/null
rename_and_wait
expect "NHL GAME 01 · Oilers vs Flames"

echo "Integration test passed"
