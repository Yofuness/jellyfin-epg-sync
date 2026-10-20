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

mkdir -p "$WORK/config/plugins/EpgChannelNames" "$WORK/config/plugins/configurations" "$WORK/tv"
# Phase 1 runs in EPG-only mode, which needs a channel filter. (Not "NHL GAME \d+": that v1 default is migrated away.)
cat > "$WORK/config/plugins/configurations/Jellyfin.Plugin.EpgChannelNames.xml" <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <ChannelNameRegex>NHL GAME \d\d</ChannelNameRegex>
</PluginConfiguration>
XML
cp "$ROOT/Jellyfin.Plugin.EpgChannelNames/bin/Release/net9.0/Jellyfin.Plugin.EpgChannelNames.dll" "$WORK/config/plugins/EpgChannelNames/"
cat > "$WORK/tv/ch.m3u" <<'M3U'
#EXTM3U
#EXTINF:-1 tvg-id="nhl01" tvg-chno="101",NHL GAME 01 Old Team vs Stale Team
http://127.0.0.1/none.ts
#EXTINF:-1 tvg-id="nhl07" tvg-chno="107",NHL GAME 07
http://127.0.0.1/none7.ts
#EXTINF:-1 tvg-id="tnt2" tvg-chno="120",UK - TNT SPORTS | EVENT 2 HD :
http://127.0.0.1/none120.ts
#EXTINF:-1 tvg-id="history" tvg-chno="30",CA - HISTORY HD
http://127.0.0.1/none30.ts
#EXTINF:-1 tvg-id="news" tvg-chno="5",CBC News
http://127.0.0.1/none2.ts
M3U
write_epg() {
  local a b c
  a=$(date -u -d '-1 hour' +%Y%m%d%H%M%S); b=$(date -u -d '+2 hour' +%Y%m%d%H%M%S); c=$(date -u -d '+6 hour' +%Y%m%d%H%M%S)
  cat > "$WORK/tv/epg.xml" <<XML
<?xml version="1.0"?><tv>
<channel id="nhl01"><display-name>NHL GAME 01</display-name></channel>
<channel id="nhl07"><display-name>NHL GAME 07</display-name></channel>
<channel id="tnt2"><display-name>UK - TNT SPORTS | EVENT 2 HD :</display-name></channel>
<channel id="history"><display-name>CA - HISTORY HD</display-name></channel>
<channel id="news"><display-name>CBC News</display-name></channel>
<programme start="$a +0000" stop="$b +0000" channel="nhl01"><title>$1</title></programme>
<programme start="$b +0000" stop="$c +0000" channel="nhl01"><title>Later placeholder</title><desc>Early Bird Special - Coffee not included</desc></programme>
<programme start="$a +0000" stop="$b +0000" channel="nhl07"><title>NHL GAME 07</title></programme>
<programme start="$a +0000" stop="$b +0000" channel="tnt2"><title>UK - TNT SPORTS | EVENT 2 HD :</title></programme>
<programme start="$a +0000" stop="$b +0000" channel="history"><title>Pawn Stars</title></programme>
<programme start="$a +0000" stop="$b +0000" channel="news"><title>The National</title></programme>
</tv>
XML
}
write_epg "Maple Leafs vs Canadiens"

# Fake Dispatcharr (scripts/fake_dispatcharr.py): same endpoints and JSON shapes as Dispatcharr 0.32.
mkdir -p "$WORK/fake"
cat > "$WORK/fake/channels.json" <<'JSON'
[{"id": 42, "name": "CA - NHL GAME 01", "channel_number": 101.0, "channel_group_id": 1072, "streams": [7]},
 {"id": 44, "name": "CA - NHL GAME 07", "channel_number": 107.0, "channel_group_id": 1072, "streams": [9]},
 {"id": 45, "name": "UK - TNT SPORTS | EVENT 2 HD :", "channel_number": 120.0, "channel_group_id": 7, "streams": [10]},
 {"id": 46, "name": "CA - HISTORY HD", "channel_number": 30.0, "channel_group_id": 5, "streams": [11]},
 {"id": 43, "name": "CBC News", "channel_number": 5.0, "channel_group_id": null, "streams": [8]}]
JSON
printf '%s' '[{"id": 1072, "name": "AM | CA\u00a0|\u00a0NHL PPV"}, {"id": 5, "name": "AM | CA | DOCUMENTARY"}, {"id": 7, "name": "EU | UK | TNT SPORTS"}]' > "$WORK/fake/groups.json"
cat > "$WORK/fake/streams.json" <<'JSON'
[{"id": 7, "name": "CA - NHL GAME 01 : ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19 – 7:00 PM ET / OCT 20 – 12:00 AM UK"},
 {"id": 9, "name": "CA - NHL GAME 07 : "},
 {"id": 10, "name": "UK - TNT SPORTS | EVENT 2 HD : Major League Baseball"},
 {"id": 11, "name": "CA - HISTORY HD"},
 {"id": 8, "name": "CBC News"}]
JSON
docker network create "$NET" >/dev/null
docker run -d --name "$FAKE" --network "$NET" --network-alias dispatcharr \
  -v "$WORK/fake":/data:ro -v "$ROOT/scripts":/scripts:ro python:3.12-alpine \
  python -I /scripts/fake_dispatcharr.py /data 8000 >/dev/null

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

# Retries for up to 30 s: the rename task is queued after a guide refresh, not run inline.
expect() {
  local names
  for _ in $(seq 30); do
    names="$(channel_names)"
    if grep -qxF "$1" <<<"$names"; then echo "PASS: $1"; return; fi
    sleep 1
  done
  echo "FAIL: expected '$1', got:"; echo "$names"
  docker logs "$NAME" 2>&1 | grep -E "EpgChannelNames|Rename channels|Refresh Guide" | tail -8
  exit 1
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

# v2: Dispatcharr on, no filter (event channels auto-detected), Documentary group -> Shows row.
PLUGIN_ID=4f1c7a3e-6b2d-4e8a-9c51-0d7e2a9b3f64
RENAME_ID=$(curl -sf "$J/ScheduledTasks" -H "$A" | python3 -c 'import json,sys;print(next(t["Id"] for t in json.load(sys.stdin) if t["Key"]=="EpgChannelNamesRename"))')
set_dispatcharr() {
  curl -sf "$J/Plugins/$PLUGIN_ID/Configuration" -H "$A" \
    | python3 -c '
import json,sys
c=json.load(sys.stdin)
c.update(DispatcharrUrl=sys.argv[1], DispatcharrApiKey="test-key", ChannelNameRegex="", GroupCategoryRules="AM | CA | DOCUMENTARY = Shows")
print(json.dumps(c))' "$1" \
    | curl -sf -X POST "$J/Plugins/$PLUGIN_ID/Configuration" -H "$H" -H "$A" -d @- >/dev/null
}
rename_and_wait() {
  curl -sf -X POST "$J/ScheduledTasks/Running/$RENAME_ID" -H "$A" >/dev/null
  sleep 4
}
USER_ID=$(curl -sf "$J/Users" -H "$A" | python3 -c 'import json,sys;print(json.load(sys.stdin)[0]["Id"])')
row() { # web Programs page row query -> program names
  curl -sf "$J/LiveTv/Programs/Recommended?userId=$USER_ID&limit=12&$1" -H "$A" \
    | python3 -c 'import json,sys;print("\n".join(i["Name"] for i in json.load(sys.stdin)["Items"]))'
}
expect_in_row() { # $1 = label, $2 = query, $3 = program name, $4 = expected count
  local got; got="$(row "$2")"
  if [[ "$(grep -cxF "$3" <<<"$got")" == "$4" ]]; then echo "PASS: '$3' ${4}x in $1 row"; else echo "FAIL: expected '$3' ${4}x in $1 row, got: $got"; exit 1; fi
}

set_dispatcharr "http://dispatcharr:8000"
rename_and_wait
expect "CA - NHL GAME 01 · ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19 – 7:00 PM ET"
expect "UK - TNT SPORTS | EVENT 2 HD · Major League Baseball"
expect "CA - HISTORY HD"
expect "CBC News"

expect_in_row Sports "hasAired=false&isSports=true" "ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19 – 7:00 PM ET" 1
expect_in_row Sports "hasAired=false&isSports=true" "Major League Baseball" 1
expect_in_row Shows "hasAired=false&isMovie=false&isSports=false&isKids=false&isNews=false&isSeries=true" "Pawn Stars" 1

# No-event channel: airing placeholder removed, channel kept.
expect "CA - NHL GAME 07"
channel_id() { curl -sf "$J/LiveTv/Channels" -H "$A" | python3 -c 'import json,sys;print(next(c["Id"] for c in json.load(sys.stdin)["Items"] if c["Name"].startswith(sys.argv[1])))' "$1"; }
program_count() { curl -sf "$J/LiveTv/Programs?userId=$USER_ID&channelIds=$1" -H "$A" | python3 -c 'import json,sys;print(len(json.load(sys.stdin)["Items"]))'; }
NOGAME_PROGS=$(program_count "$(channel_id "CA - NHL GAME 07")")
if [[ "$NOGAME_PROGS" == 0 ]]; then echo "PASS: no-event placeholder removed"; else echo "FAIL: no-event channel still has $NOGAME_PROGS programs"; exit 1; fi
GAME_ID=$(channel_id "CA - NHL GAME 01")
if [[ "$(program_count "$GAME_ID")" -gt 0 ]]; then echo "PASS: event channel keeps its program"; else echo "FAIL: event channel has no programs"; exit 1; fi
# Airing block: event title + full stream name as description. Later block: plain placeholder, no joke text.
curl -sf "$J/LiveTv/Programs?userId=$USER_ID&channelIds=$GAME_ID&fields=Overview" -H "$A" | python3 -c '
import json,sys
items=sorted(json.load(sys.stdin)["Items"], key=lambda i: i["StartDate"])
airing, later = items[0], items[1]
assert airing["Name"] == "ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19 – 7:00 PM ET", airing["Name"]
assert airing.get("Overview") == "CA - NHL GAME 01 : ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19 – 7:00 PM ET / OCT 20 – 12:00 AM UK", airing.get("Overview")
assert later["Name"] == "CA - NHL GAME 01", later["Name"]
assert not later.get("Overview"), later.get("Overview")
print("PASS: airing block has event + stream description; later block is plain")'

# Removing the rule takes History out of the Shows row again.
curl -sf "$J/Plugins/$PLUGIN_ID/Configuration" -H "$A" \
  | python3 -c 'import json,sys;c=json.load(sys.stdin);c["GroupCategoryRules"]="";print(json.dumps(c))' \
  | curl -sf -X POST "$J/Plugins/$PLUGIN_ID/Configuration" -H "$H" -H "$A" -d @- >/dev/null
rename_and_wait
expect_in_row Shows "hasAired=false&isMovie=false&isSports=false&isKids=false&isNews=false&isSeries=true" "Pawn Stars" 0

# Dispatcharr down: channels keep their last names instead of failing...
docker stop "$FAKE" >/dev/null
rename_and_wait
expect "CA - NHL GAME 01 · ANAHEIM DUCKS @ NEW YORK RANGERS OCT 19 – 7:00 PM ET"
# ...until a guide refresh resets them to the tuner names (no filter set, so no EPG-mode fallback).
docker exec "$NAME" sh -c 'rm -rf /cache/xmltv/*'
refresh_and_wait
expect "NHL GAME 01 Old Team vs Stale Team"

echo "Integration test passed"
