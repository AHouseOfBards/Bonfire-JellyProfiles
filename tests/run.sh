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
#
# Environment:
#   BONFIRE_JOBS       plain JavaScript harnesses run this many at a time (default 4).
#                      1 runs every harness one after another in name order, as before.
#   BONFIRE_LOCK_WAIT  seconds to wait for another run on this machine to finish before
#                      giving up (default 3600). One full run at a time: two of them fight
#                      over the CPU and over Chrome, and a harness that times out under
#                      that load is a failure nobody can reproduce.
#   SKIP_BUILD         skip building the plugin.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# A parallel worker: run one JS harness, leave its output and exit code where the
# collector looks. The script runs itself so no second file has to travel with it.
if [ "${1:-}" = "--worker" ]; then
    tmp="$2"; f="$3"; b="$(basename "$f" .js)"
    node "$f" >"$tmp/$b.out" 2>&1
    echo $? >"$tmp/$b.rc.part"
    mv "$tmp/$b.rc.part" "$tmp/$b.rc"          # appears whole or not at all
    exit 0
fi

WHICH="${1:-all}"
JOBS="${BONFIRE_JOBS:-4}"
case "$JOBS" in ''|*[!0-9]*) JOBS=1 ;; esac
[ "$JOBS" -lt 1 ] && JOBS=1
FAILED=()
RAN=0
START=$SECONDS
SCRATCH=""
XPID=""

# ── One full run at a time ─────────────────────────────────────────────────────────────
# A lock directory under the system temp dir (mkdir is atomic), holding the owner's pid.
# A lock whose process is gone is taken over, so a crash or a Ctrl-C cannot wedge the next
# run. A nested run.sh (tests/js/runner.js starts several to test this one) inherits
# BONFIRE_RUN_LOCKED and does not take it again.
LOCKDIR="${TMPDIR:-${TEMP:-/tmp}}/bonfire-tests.lock"
OWNS_LOCK=""

lock_holder() { cat "$LOCKDIR/pid" 2>/dev/null; }

acquire_lock() {
    [ -n "${BONFIRE_RUN_LOCKED:-}" ] && return 0
    local waited=0 limit="${BONFIRE_LOCK_WAIT:-3600}" told="" pid
    while ! mkdir "$LOCKDIR" 2>/dev/null; do
        pid="$(lock_holder)"
        if [ -z "$pid" ]; then
            # The owner has made the directory and not yet written its pid. Give it a moment
            # before calling the lock abandoned.
            sleep 1
            pid="$(lock_holder)"
        fi
        if [ -z "$pid" ] || ! kill -0 "$pid" 2>/dev/null; then
            # Stale. Take it over by renaming it away: only one of several runs that find
            # the same dead lock wins the rename, so none removes a lock another just made.
            mv "$LOCKDIR" "$LOCKDIR.stale.$$" 2>/dev/null && rm -rf "$LOCKDIR.stale.$$"
            continue
        fi
        if [ "$waited" -ge "$limit" ]; then
            echo "Another tests/run.sh is already running (pid $pid, lock $LOCKDIR)." >&2
            echo "Gave up after ${waited}s; set BONFIRE_LOCK_WAIT to wait longer." >&2
            exit 3
        fi
        if [ -z "$told" ]; then
            echo "Another tests/run.sh is running (pid $pid); waiting for it to finish..." >&2
            told=1
        fi
        sleep 1
        waited=$((waited + 1))
    done
    echo $$ >"$LOCKDIR/pid"
    OWNS_LOCK=1
    export BONFIRE_RUN_LOCKED=1
}

cleanup() {
    if [ -n "$XPID" ]; then kill "$XPID" 2>/dev/null; fi
    [ -n "$SCRATCH" ] && rm -rf "$SCRATCH"
    [ -n "$OWNS_LOCK" ] && rm -rf "$LOCKDIR"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

acquire_lock

# Print one harness's verdict. judge <label> <exit code> <output>
judge() {
    local label="$1" rc="$2" out="$3"
    printf '  %-14s ' "$label"
    RAN=$((RAN + 1))
    if [ "$rc" -eq 0 ]; then
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
        FAILED+=("$label")
        echo "FAILED"
        echo "$out" | sed 's/^/        /' | tail -25
    fi
}

run() {                       # run <label> <command...>
    local label="$1"; shift
    local out rc
    out=$("$@" 2>&1); rc=$?
    judge "$label" "$rc" "$out"
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
    JS_DIR="${BONFIRE_JS_DIR:-tests/js}"   # the override is for tests/js/runner.js

    # Harnesses that must not share the machine with each other. Everything else is a plain
    # Node process over files and stubs and runs BONFIRE_JOBS at a time.
    #   Chrome and the fixture server are not listed: any harness whose source requires
    #   ../screens/ or names findChrome is held back by the test in needs_serial, so a new
    #   Chrome harness is serial from its first run rather than from whenever someone
    #   remembers to add it here. Those share one Chrome profile cache, one font download
    #   (ICONS) and the CPU that its layout measurements depend on.
    # Listed by name:
    #   runner      starts this script about a dozen times to test it, each a full process tree
    #   i18ntest    orders replies by real timers of different lengths
    #   adminconfirm adminsweep28 dashboardxss serverinfo
    #               settle on real 5 to 20 ms timers, which a loaded machine can reorder
    SERIAL_JS=" runner i18ntest adminconfirm adminsweep28 dashboardxss serverinfo "

    needs_serial() {          # needs_serial <name> <file>
        case "$SERIAL_JS" in *" $1 "*) return 0 ;; esac
        grep -Eq 'screens/|findChrome|chrome\.launch' "$2"
    }

    names=(); files=(); par=(); ser=()
    for f in "$JS_DIR"/*.js; do
        base="$(basename "$f")"
        [ "${base#_}" != "$base" ] && continue
        case "$base" in *.verify.js|*.scan.js) continue ;; esac
        names+=("${base%.js}"); files+=("$f")
    done

    if [ "$JOBS" -le 1 ]; then
        for i in "${!names[@]}"; do
            run "${names[$i]}" node "${files[$i]}"
        done
    else
        SCRATCH="$(mktemp -d "${TMPDIR:-${TEMP:-/tmp}}/bonfire-js.XXXXXX")"
        for i in "${!names[@]}"; do
            if needs_serial "${names[$i]}" "${files[$i]}"; then
                ser+=("$i")
            else
                par+=("$i")
            fi
        done
        for i in "${par[@]}"; do printf '%s\0' "${files[$i]}"; done >"$SCRATCH/par.list"
        xargs -r -0 -n 1 -P "$JOBS" bash "${BASH_SOURCE[0]}" --worker "$SCRATCH" <"$SCRATCH/par.list" &
        XPID=$!
        # Results are printed in name order as each one's turn comes up, so the output reads
        # the same however the workers happen to finish.
        for i in "${par[@]}"; do
            n="${names[$i]}"
            while [ ! -f "$SCRATCH/$n.rc" ]; do
                if ! kill -0 "$XPID" 2>/dev/null; then
                    sleep 0.5
                    [ -f "$SCRATCH/$n.rc" ] || { echo 1 >"$SCRATCH/$n.rc"; echo "the worker never finished" >"$SCRATCH/$n.out"; }
                    break
                fi
                sleep 0.1
            done
            judge "$n" "$(cat "$SCRATCH/$n.rc")" "$(cat "$SCRATCH/$n.out")"
        done
        wait "$XPID" 2>/dev/null
        XPID=""
        # Then the ones that need the machine to themselves, one at a time.
        for i in "${ser[@]}"; do
            run "${names[$i]}" node "${files[$i]}"
        done
    fi
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
    # Every harness is built once, together, and then run from its output. `dotnet run`
    # per harness restored and built each project on its own, one after another, and the
    # net10.0 pass did it all again. The harnesses, their frameworks (BonfireTfm) and what
    # they print are unchanged; only the building moved. A harness that did not build is
    # reported FAILED below, by name, rather than failing the whole section.
    BUILDLOG="$(mktemp "${TMPDIR:-${TEMP:-/tmp}}/bonfire-cs.XXXXXX")"
    for target in RestoreAll BuildAll; do
        if ! dotnet msbuild tests/cs/build-all.proj -nologo -v:q -m -t:$target \
               -p:Configuration=Release -p:BonfireTfm="$TFM" >>"$BUILDLOG" 2>&1; then
            echo "  harness build FAILED ($target); the last of its output:"
            grep -E 'error|Error' "$BUILDLOG" | sort -u | tail -15 | sed 's/^/        /'
            break
        fi
    done
    rm -f "$BUILDLOG"
    for d in tests/cs/*/; do
        d="${d%/}"; n="$(basename "$d")"
        # The output is named for the project's AssemblyName, which is the directory's name
        # in most of them and "<name>harness" or the .csproj's own name in the rest.
        proj="$(ls "$d"/*.csproj | head -1)"
        asm="$(sed -n 's/.*<AssemblyName>\(.*\)<\/AssemblyName>.*/\1/p' "$proj" | head -1)"
        [ -z "$asm" ] && asm="$(basename "$proj" .csproj)"
        dll="$d/bin/Release/$TFM/$asm.dll"
        if [ ! -f "$dll" ]; then
            judge "$n" 1 "no $dll: the harness did not build"
            continue
        fi
        run "$n" dotnet "$dll"
    done
    echo
fi

echo "──────────────────────────────────────────────────────────────"
echo "  ($((SECONDS - START))s)"
if [ ${#FAILED[@]} -eq 0 ]; then
    echo "  $RAN harnesses, all green."
    exit 0
fi
echo "  $RAN harnesses, ${#FAILED[@]} failed: ${FAILED[*]}"
exit 1
