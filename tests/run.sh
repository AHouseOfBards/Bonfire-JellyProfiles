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
    # _lib.js is shared plumbing. *.verify.js needs the network and refreshes what the
    # offline checks compare against — it is a maintenance tool, not a gate.
    # *.scan.js surveys the source and prints what it finds; it asserts nothing and
    # always exits 0, so counting it would add a harness that can never go red.
    for f in tests/js/*.js; do
        base="$(basename "$f")"
        [ "${base#_}" != "$base" ] && continue
        case "$base" in *.verify.js|*.scan.js) continue ;; esac
        run "${base%.js}" node "$f"
    done
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
