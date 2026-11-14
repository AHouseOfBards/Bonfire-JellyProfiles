#!/usr/bin/env bash
#
# Start a real Jellyfin server with the built plugin and prove it comes up.
#
#   tests/live/smoke.sh --plugin DIR --image jellyfin/jellyfin@sha256:...   # Docker (Linux)
#   tests/live/smoke.sh --plugin DIR --server DIR                           # an unpacked Jellyfin
#
# DIR for --plugin is the folder holding Jellyfin.Profiles.dll as shipped
# (bin/Release/net9.0). Only net9.0 ships and it must load on every server and platform,
# so CI runs this against 10.11.x (.NET 9) and 12.x (.NET 10), on Docker and on Windows.
#
# Everything the server writes goes into a new directory (--work, default a fresh one under
# tests/live/.work) on a port of its own (--port, default 18096), so a Jellyfin already
# running on 8096 is never touched. Only the process or container started here is stopped.
#
# What it checks, in order, each of which has shipped broken at some point:
#   1. the server finishes starting and logs that it loaded Bonfire at the csproj version
#   2. no [ERR] or [FTL] line from Jellyfin.Profiles in the log
#   3. index.html as served carries Bonfire's tags, the script it points to is served, and
#      that script passes tests/js/inject.test.js (the one that executes the startup path)
#   4. an anonymous route answers as docs/developer-api.md says (GET /info)
#   5. after the startup wizard, a route that needs sign-in gives 401 without a token and
#      200 with one
#   6. Docker only: after `docker restart` the plugin loads again and 1-5 still hold
#
# Exit 0 on success, 1 on a failed check, 2 on a usage error.
set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PLUGIN=""; IMAGE=""; SERVER=""; PORT=18096; WORK=""; WAIT=${SMOKE_WAIT:-180}
while [ $# -gt 0 ]; do
  case "$1" in
    --plugin) PLUGIN="$2"; shift 2;;
    --image)  IMAGE="$2"; shift 2;;
    --server) SERVER="$2"; shift 2;;
    --port)   PORT="$2"; shift 2;;
    --work)   WORK="$2"; shift 2;;
    *) echo "unknown argument: $1" >&2; exit 2;;
  esac
done
[ -n "$PLUGIN" ] || { echo "usage: smoke.sh --plugin DIR (--image TAG | --server DIR) [--port N] [--work DIR]" >&2; exit 2; }
{ [ -n "$IMAGE" ] || [ -n "$SERVER" ]; } || { echo "give --image or --server" >&2; exit 2; }
[ -d "$PLUGIN" ] || { echo "FAIL  plugin folder does not exist: $PLUGIN"; exit 1; }
[ -f "$PLUGIN/Jellyfin.Profiles.dll" ] || { echo "FAIL  no Jellyfin.Profiles.dll in $PLUGIN"; exit 1; }

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Jellyfin.Profiles.csproj" | head -1)"
[ -n "$VERSION" ] || { echo "cannot read <Version> from the csproj"; exit 2; }

if [ -z "$WORK" ]; then WORK="$ROOT/tests/live/.work/run-$$-$(date +%s)"; fi
[ -e "$WORK" ] && { echo "work directory already exists: $WORK (it must be new)"; exit 2; }
mkdir -p "$WORK/config" "$WORK/data" "$WORK/cache" "$WORK/log" "$WORK/out"
WORK="$(cd "$WORK" && pwd)"
BASE="http://127.0.0.1:$PORT"
CONTAINER="bonfire-smoke-$$"
SPID=""
FAILS=0; PASSES=0

pass() { PASSES=$((PASSES+1)); echo "  PASS  $1"; }
fail() { FAILS=$((FAILS+1)); echo "  FAIL  $1"; }
check() { if [ "$2" = "0" ]; then pass "$1"; else fail "$1${3:+  -- $3}"; fi; }

# Docker mounts /config (config and data live under it) and /cache. A native server gets
# every directory on its command line, so nothing lands in a profile or ProgramData.
if [ -n "$IMAGE" ]; then
  PLUGINS_DIR="$WORK/config/data/plugins"; LOG_DIR="$WORK/config/log"
else
  PLUGINS_DIR="$WORK/data/plugins"; LOG_DIR="$WORK/log"
fi

stop_server() {
  if [ -n "$IMAGE" ]; then
    docker rm -f "$CONTAINER" >/dev/null 2>&1
  elif [ -n "$SPID" ]; then
    # Windows: an MSYS pid is not the process's own, and plain kill leaves jellyfin.exe.
    local w; w="$(cat /proc/$SPID/winpid 2>/dev/null)"
    if [ -n "$w" ]; then taskkill //F //T //PID "$w" >/dev/null 2>&1; else kill "$SPID" 2>/dev/null; sleep 2; kill -9 "$SPID" 2>/dev/null; fi
  fi
}
finish() {
  stop_server
  if [ -n "${SMOKE_KEEP:-}" ]; then echo "kept $WORK"; else rm -rf "$WORK" 2>/dev/null || sudo rm -rf "$WORK" 2>/dev/null; fi
}
trap finish EXIT

# network.xml: our port, and no UDP discovery (7359 is taken by any Jellyfin already here).
mkdir -p "$WORK/config" "$WORK/config/config"
for d in "$WORK/config" "$WORK/config/config"; do
  cat > "$d/network.xml" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <InternalHttpPort>$PORT</InternalHttpPort>
  <PublicHttpPort>$PORT</PublicHttpPort>
  <AutoDiscovery>false</AutoDiscovery>
  <EnableUPnP>false</EnableUPnP>
  <RequireHttps>false</RequireHttps>
  <EnableHttps>false</EnableHttps>
</NetworkConfiguration>
EOF
done

install_plugin() {
  local dest="$PLUGINS_DIR/Bonfire_$VERSION"
  mkdir -p "$dest" && cp "$PLUGIN"/Jellyfin.Profiles.dll "$dest/" || return 1
  [ -f "$PLUGIN/Jellyfin.Profiles.deps.json" ] || true
}

start_server() {
  if [ -n "$IMAGE" ]; then
    # network.xml goes where this image reads it. -p maps our port to the image's 8096, so
    # the file's port must stay 8096 inside.
    sed -i "s:<InternalHttpPort>$PORT<:<InternalHttpPort>8096<:;s:<PublicHttpPort>$PORT<:<PublicHttpPort>8096<:" "$WORK/config/network.xml" "$WORK/config/config/network.xml"
    docker run -d --name "$CONTAINER" -p "127.0.0.1:$PORT:8096" \
      -v "$WORK/config:/config" -v "$WORK/cache:/cache" "$IMAGE" >/dev/null
  else
    local exe
    if [ -f "$SERVER/jellyfin.exe" ]; then exe=("$SERVER/jellyfin.exe")
    elif [ -x "$SERVER/jellyfin" ]; then exe=("$SERVER/jellyfin")
    else exe=(dotnet "$SERVER/jellyfin.dll"); fi
    # Native: configdir is where network.xml is read from (config/ under it for 10.11).
    "${exe[@]}" --datadir "$WORK/data" --configdir "$WORK/config" --cachedir "$WORK/cache" \
      --logdir "$WORK/log" --webdir "$SERVER/jellyfin-web" > "$WORK/out/server.out" 2>&1 &
    SPID=$!
  fi
}

server_log() {
  if [ -n "$IMAGE" ]; then docker logs "$CONTAINER" 2>&1; fi
  cat "$LOG_DIR"/*.log 2>/dev/null
  [ -f "$WORK/out/server.out" ] && cat "$WORK/out/server.out"
}

wait_up() {
  local i=0 code
  while [ $i -lt "$WAIT" ]; do
    # While it starts, Jellyfin answers every URL with a 200 or 503 HTML page. It is up once
    # /System/Info/Public is JSON that names its version.
    code="$(curl -s -m 3 "$BASE/System/Info/Public" 2>/dev/null | jget Version)"
    [ -n "$code" ] && return 0
    if [ -n "$IMAGE" ] && ! docker ps -q --filter "name=$CONTAINER" | grep -q .; then return 1; fi
    sleep 1; i=$((i+1))
  done
  return 1
}

jget() { node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{let v=JSON.parse(s);for(const k of process.argv[1].split("."))v=v==null?v:v[k];console.log(v===undefined||v===null?"":v)}catch(e){console.log("")}})' "$1"; }

AUTH='MediaBrowser Client="bonfire-smoke", Device="smoke", DeviceId="bonfire-smoke-1", Version="1.0"'

# All of 1-5 against a running server. $1 labels the round.
run_checks() {
  local round="$1" out="$WORK/out/$1" log code
  mkdir -p "$out"
  echo "-- $round"
  wait_up; local up=$?; local vok
  check "$round: the server finishes starting on $BASE" $up "no answer in ${WAIT}s"
  if [ $up -ne 0 ]; then server_log | tail -40; return; fi

  # Give the startup task a moment: the plugin's load line is written before the web server
  # is up, but errors from its startup filter can come a second later.
  sleep 3
  log="$(server_log)"
  echo "$log" | grep -qE "Loaded plugin.*Bonfire.*${VERSION//./\\.}|Bonfire.*${VERSION//./\\.}.*Loaded|Loaded assembly.*Jellyfin\.Profiles.*Version=${VERSION//./\\.}"
  check "$round: the log says Bonfire $VERSION was loaded" $? "no 'Loaded plugin' line for it"
  # An assembly that failed to load logs a line naming it, not one from the plugin's own
  # logger, so check for that too.
  echo "$log" | grep -E "\[(ERR|FTL)\]" | grep -E "Jellyfin\.Profiles|Bonfire" > "$out/errors.txt"
  check "$round: no [ERR] or [FTL] line from Jellyfin.Profiles" "$([ -s "$out/errors.txt" ] && echo 1 || echo 0)" "$(head -c 300 "$out/errors.txt")"
  echo "$log" | grep -E "Failed to load assembly.*Jellyfin\.Profiles|Error loading plugin.*Bonfire|Plugin.*Bonfire.*(disabled|malfunction|NotSupported)" > "$out/loadfail.txt"
  check "$round: the log has no plugin load failure for Bonfire" "$([ -s "$out/loadfail.txt" ] && echo 1 || echo 0)" "$(head -c 300 "$out/loadfail.txt")"

  # the wizard, then the administrator signs in
  if [ "$(curl -s -m 10 "$BASE/System/Info/Public" | jget StartupWizardCompleted)" != "true" ]; then
    curl -s -m 10 -o /dev/null -X POST -H 'Content-Type: application/json' -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' "$BASE/Startup/Configuration"
    curl -s -m 10 -o /dev/null "$BASE/Startup/User"
    code="$(curl -s -m 20 -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' -d '{"Name":"smoke","Password":"smoke-pass-1"}' "$BASE/Startup/User")"
    check "$round: the wizard accepts an admin user" "$([ "$code" = 204 ] || [ "$code" = 200 ] && echo 0 || echo 1)" "HTTP $code"
    code="$(curl -s -m 20 -o /dev/null -w '%{http_code}' -X POST "$BASE/Startup/Complete")"
    check "$round: the wizard completes" "$([ "$code" = 204 ] || [ "$code" = 200 ] && echo 0 || echo 1)" "HTTP $code"
  fi
  curl -s -m 20 -X POST -H 'Content-Type: application/json' -H "Authorization: $AUTH" \
    -d '{"Username":"smoke","Pw":"smoke-pass-1"}' "$BASE/Users/AuthenticateByName" > "$out/auth.json"
  local tok; tok="$(jget AccessToken < "$out/auth.json")"
  [ -n "$tok" ]; check "$round: the admin user signs in" $? "$(head -c 200 "$out/auth.json")"
  # A new install has Bonfire off, and an off Bonfire keeps its tags out of index.html
  # (Plugin.IsOff), so turn it on as an administrator would before looking at the page.
  code="$(curl -s -m 20 -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' \
    -H "Authorization: MediaBrowser Token=\"$tok\"" -d '{"bonfireOn":true}' "$BASE/plugins/profiles/admin/settings")"
  check "$round: an administrator can turn Bonfire on (POST /admin/settings)" "$([ "$code" = 200 ] && echo 0 || echo 1)" "HTTP $code"

  # 3. the page as served, and the script it points to
  code="$(curl -s -m 15 -o "$out/index.html" -w '%{http_code}' "$BASE/web/index.html")"
  check "$round: /web/index.html is served" "$([ "$code" = 200 ] && echo 0 || echo 1)" "HTTP $code"
  grep -q 'id="jpf-eh"' "$out/index.html"
  check "$round: index.html carries Bonfire's head script (jpf-eh)" $?
  local src url; src="$(grep -o '<script[^>]*src="[^"]*/plugins/profiles/profiles\.js[^"]*"' "$out/index.html" | head -1 | sed 's/.*src="\([^"]*\)".*/\1/')"
  [ -n "$src" ]; check "$round: index.html points at /plugins/profiles/profiles.js" $?
  if [ -n "$src" ]; then
    case "$src" in http*) url="$src";; *) url="$BASE$src";; esac
    code="$(curl -s -m 30 -o "$out/profiles.js" -w '%{http_code}' "$url")"
    check "$round: the client script is served (200)" "$([ "$code" = 200 ] && [ -s "$out/profiles.js" ] && echo 0 || echo 1)" "HTTP $code"
    node "$ROOT/tests/js/inject.test.js" "$out/profiles.js" > "$out/inject.txt" 2>&1
    check "$round: the served script passes tests/js/inject.test.js" $? "$(grep -E 'FAIL|Error' "$out/inject.txt" | head -3 | tr '\n' ' ')"
  fi

  # 4. an anonymous route, as developer-api.md documents it
  # The version is the informational one, so it may carry a label: 1.9.0.5-beta.
  curl -s -m 10 -H 'Accept: application/json; profile="CamelCase"' "$BASE/plugins/profiles/info" > "$out/info.json"
  case "$(jget version < "$out/info.json")" in "$VERSION"|"$VERSION"-*) vok=0;; *) vok=1;; esac
  [ "$(jget name < "$out/info.json")" = "Bonfire" ] && [ $vok -eq 0 ] && [ "$(jget disabled < "$out/info.json")" = "false" ] && [ "$(jget apiVersion < "$out/info.json")" = "2" ]
  check "$round: GET /plugins/profiles/info is anonymous and reports Bonfire $VERSION, on" $? "$(head -c 200 "$out/info.json")"

  # 5. a route that needs sign-in
  code="$(curl -s -m 10 -o /dev/null -w '%{http_code}' "$BASE/plugins/profiles/list")"
  check "$round: GET /plugins/profiles/list without a token is 401" "$([ "$code" = 401 ] && echo 0 || echo 1)" "HTTP $code"
  code="$(curl -s -m 10 -o /dev/null -w '%{http_code}' -H "Authorization: MediaBrowser Token=\"$tok\"" "$BASE/plugins/profiles/list")"
  check "$round: the same route with a token is 200" "$([ "$code" = 200 ] && echo 0 || echo 1)" "HTTP $code"
}

echo "Bonfire $VERSION on ${IMAGE:-$SERVER}, port $PORT, work $WORK"
install_plugin || { echo "FAIL  could not install the plugin"; exit 1; }
start_server
run_checks first

if [ -n "$IMAGE" ]; then
  before="$(server_log | grep -c 'Loaded plugin.*Bonfire')"
  docker restart "$CONTAINER" >/dev/null
  # The old process answered a moment ago; wait_up must see the new one.
  sleep 3
  run_checks restart
  after="$(server_log | grep -c 'Loaded plugin.*Bonfire')"
  [ "$after" -gt "$before" ]; check "restart: the plugin was loaded again ($before -> $after load lines)" $?
fi

echo
echo "$PASSES passed, $FAILS failed"
[ "$FAILS" -eq 0 ]
