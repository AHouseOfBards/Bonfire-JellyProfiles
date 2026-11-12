#!/usr/bin/env bash
#
# Run every harness. One command, so there is no "which ones did I remember to run".
#
#   tests/run.sh              # everything
#   tests/run.sh js           # just the JavaScript harnesses
#   tests/run.sh cs           # just the C# ones, against the net9.0 build that ships
#   tests/run.sh cs10         # the same set, against the net10.0 build
#
# The plugin multi-targets net9.0 and net10.0 — Jellyfin 10.11.x runs on .NET 9 and 12.0
# on .NET 10. net9.0 is what ships, because it is the only one of the two that loads on
# both servers, so `all` runs the C# set against that one. `cs10` is what proves the other
# build is not merely compiling: a plugin nobody has ever loaded is not a working plugin,
# which is the whole lesson of 1.5.2.
#
# The C# harnesses reference bin/Release/<tfm>/Jellyfin.Profiles.dll, so the plugin is
# built first unless SKIP_BUILD is set.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

WHICH="${1:-all}"
FAILED=()
RAN=0

run() {                       # run <label> <command...>
    local label="$1"; shift
    printf '  %-14s ' "$label"
    local out
    if out=$("$@" 2>&1); then
        RAN=$((RAN + 1))
        # A harness that exits 0 having said nothing, or having counted zero checks, proved
        # nothing: an emptied test body looks exactly like that. Fail it.
        if [ -z "$(printf %s "$out" | tr -d '[:space:]')" ] \
           || printf '%s\n' "$out" | grep -Eiq '(^|[^0-9.])0 (passed|checks|assertions)|pass 0([^0-9]|$)'; then
            FAILED+=("$label")
            echo "FAILED (asserted nothing)"
            return
        fi
        # And one that exits 0 without ending on a totals line stopped early: it printed
        # some PASS lines and then fell off the end. A skip says so on its last line.
        if ! printf '%s\n' "$out" | grep -v '^[[:space:]]*$' | tail -1 \
             | grep -Eq '[0-9]+ passed|pass [0-9]+, fail [0-9]+|^[[:space:]]*SKIPPED'; then
            FAILED+=("$label")
            echo "FAILED (no totals line)"
            echo "$out" | sed 's/^/        /' | tail -5
            return
        fi
        echo "$out" | tail -1 | cut -c1-70
    else
        RAN=$((RAN + 1))
        FAILED+=("$label")
        echo "FAILED"
        echo "$out" | sed 's/^/        /' | tail -25
    fi
}

# The framework the C# harnesses load the plugin from. They set their own TargetFramework
# from it too, because a net9 harness cannot load a net10 assembly.
TFM="net9.0"
[ "$WHICH" = "cs10" ] && TFM="net10.0"

if [ "$WHICH" != "js" ]; then
    echo "── Building the plugin (Release) ──────────────────────────────"
    if [ -z "${SKIP_BUILD:-}" ]; then
        if ! dotnet build -c Release -warnaserror --nologo -v q >/dev/null; then
            echo "  build FAILED"
            dotnet build -c Release -warnaserror --nologo -v q 2>&1 | tail -20
            exit 1
        fi
    fi
    echo "  ok"
    echo
fi

if [ "$WHICH" = "all" ] || [ "$WHICH" = "js" ]; then
    echo "── JavaScript ─────────────────────────────────────────────────"
    # _lib.js and _stubdom.js are shared plumbing (any _*.js is). *.verify.js needs the network and refreshes what the
    # offline checks compare against — it is a maintenance tool, not a gate.
    # *.scan.js surveys the source and prints what it finds; it asserts nothing and
    # always exits 0, so counting it would add a harness that can never go red.
    for f in "${BONFIRE_JS_DIR:-tests/js}"/*.js; do   # the override is for tests/js/runner.js
        base="$(basename "$f")"
        [ "${base#_}" != "$base" ] && continue
        case "$base" in *.verify.js|*.scan.js) continue ;; esac
        run "${base%.js}" node "$f"
    done
    # The vendored Campground.Shared must be byte for byte what campground-shared.json
    # records. Edited in place, it would ship a library no other Campground plugin has —
    # and the newest copy on a server runs index.html for all of them. Change it in
    # Campground-Coordination and re-vendor instead. Here rather than as its own CI step so
    # the desk and the pipeline cannot disagree.
    run "sharedcopy" sh -c 'sh Campground.Shared/check-shared.sh && echo "1 passed, 0 failed"'
    echo
fi

if [ "$WHICH" = "all" ] || [ "$WHICH" = "cs" ] || [ "$WHICH" = "cs10" ]; then
    echo "── C# (against the $TFM build) ────────────────────────────────"
    for d in tests/cs/*/; do
        run "$(basename "$d")" dotnet run --project "$d" -c Release --nologo             -p:BonfireTfm="$TFM"
    done
    echo
fi

echo "──────────────────────────────────────────────────────────────"
if [ ${#FAILED[@]} -eq 0 ]; then
    echo "  $RAN harnesses, all green."
    exit 0
fi
echo "  $RAN harnesses, ${#FAILED[@]} failed: ${FAILED[*]}"
exit 1
