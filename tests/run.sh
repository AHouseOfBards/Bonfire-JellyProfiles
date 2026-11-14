#!/usr/bin/env bash
#
# Run every harness. One command, so there is no "which ones did I remember to run".
#
#   tests/run.sh              # everything
#   tests/run.sh js           # just the JavaScript harnesses
#   tests/run.sh cs           # just the C# ones, against the net9.0 build that ships
#   tests/run.sh cs10         # the same set, against the net10.0 build
#
# The plugin multi-targets net9.0 (Jellyfin 10.11.x) and net10.0 (12.0). net9.0 ships
# because it is the one that loads on both servers, so `all` runs the C# set against it.
# `cs10` proves the other build is more than compiling: a build nobody has loaded is not
# a working build (the lesson of 1.5.2).
#
# The C# harnesses reference bin/Release/<tfm>/Jellyfin.Profiles.dll, so the plugin is
# built first unless SKIP_BUILD is set.
#
# Environment:
#   BONFIRE_JOBS       plain JavaScript harnesses run this many at a time (default 4).
#                      1 runs them one after another in name order.
#   BONFIRE_LOCK_WAIT  seconds to wait for another run on this machine to finish before
#                      giving up (default 3600). One full run at a time: two fight over
#                      the CPU and Chrome, and a harness that times out under that load
#                      is a failure nobody can reproduce.
#   BONFIRE_CHROME_JOBS  harnesses that start Chrome run this many at a time (default 1).
#                      Each launch has its own profile directory and port 0
#                      (tests/screens/chrome.js), so a machine with cores to spare can
#                      raise it. Layout measurements depend on the CPU, so the default
#                      stays 1 and CI does not change it.
#   SKIP_BUILD         skip building the plugin.
#
# Arguments, after the section name (all, js, cs, cs10):
#   --shard K/N        run only shard K of N (1 <= K <= N) of that section. Greedy, longest
#                      first, over tests/timings.txt; deterministic; every harness is in
#                      exactly one shard. A harness missing from the file counts as
#                      DEFAULT_SECONDS. Serial harnesses stay serial inside their shard.
#   --list             print the harnesses that would run (after --shard) and stop. Takes no
#                      lock and builds nothing; tests/js/shards.js uses it.
#
#   --changed [BASE]   run only the harnesses a change can affect: those whose recorded files
#                      (tests/depends.txt) differ between BASE and the working tree (default
#                      BASE: the merge base with origin/beta), plus any harness the record
#                      does not list, plus every harness if the record, this script, _lib.js,
#                      fixture.js or chrome.js changed. Prints what it chose and why. For a
#                      developer loop; CI runs everything.
#   --record           record which repository files each harness reads while it runs, and
#                      fold that into tests/depends.txt. Use it with a full run, or --only.
#   --only a,b         run only these harnesses (section/name, e.g. js/inject,cs/configlock).
#
# Every result line ends with how long that harness took. At the end the total and the ten
# slowest are printed, and every timing is written to $TMPDIR/bonfire-timings.txt in the
# format of tests/timings.txt, ready to be copied over it.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# Microseconds since some fixed point. EPOCHREALTIME is bash 5; whole seconds otherwise.
now() { if [ -n "${EPOCHREALTIME:-}" ]; then echo "${EPOCHREALTIME/[.,]/}"; else echo "$(date +%s)000000"; fi; }

# A parallel worker: run one JS harness and leave its output and exit code where the
# collector looks. The script runs itself, so no second file has to travel with it.
if [ "${1:-}" = "--worker" ]; then
    tmp="$2"; f="$3"; b="$(basename "$f" .js)"
    t0=$(now)
    BONFIRE_RECORD_LABEL="js/$b" node "$f" >"$tmp/$b.out" 2>&1
    rc=$?
    echo $(( $(now) - t0 )) >"$tmp/$b.us"
    echo $rc >"$tmp/$b.rc.part"
    mv "$tmp/$b.rc.part" "$tmp/$b.rc"          # appears whole or not at all
    exit 0
fi

WHICH="all"; SHARD=""; LIST=""; CHANGED=""; CHANGED_BASE=""; RECORD=""; ONLY=""
if [ $# -gt 0 ] && [ "${1#-}" = "$1" ]; then WHICH="$1"; shift; fi
while [ $# -gt 0 ]; do
    case "$1" in
        --shard) SHARD="${2:-}"; shift 2 || { echo "--shard needs K/N" >&2; exit 2; } ;;
        --shard=*) SHARD="${1#--shard=}"; shift ;;
        --list) LIST=1; shift ;;
        --record) RECORD=1; shift ;;
        --only) ONLY="${2:-}"; shift 2 || { echo "--only needs a list" >&2; exit 2; } ;;
        --only=*) ONLY="${1#--only=}"; shift ;;
        --changed) CHANGED=1; shift
            if [ $# -gt 0 ] && [ "${1#-}" = "$1" ]; then CHANGED_BASE="$1"; shift; fi ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done
case "$WHICH" in all|js|cs|cs10) ;; *) echo "unknown section: $WHICH (all, js, cs, cs10)" >&2; exit 2 ;; esac
SHARD_K=""; SHARD_N=""
if [ -n "$SHARD" ]; then
    case "$SHARD" in
        [0-9]*/[0-9]*) SHARD_K="${SHARD%/*}"; SHARD_N="${SHARD#*/}" ;;
    esac
    case "$SHARD_K$SHARD_N" in ''|*[!0-9]*) echo "--shard wants K/N, got '$SHARD'" >&2; exit 2 ;; esac
    if [ "$SHARD_K" -lt 1 ] || [ "$SHARD_K" -gt "$SHARD_N" ]; then
        echo "--shard K/N wants 1 <= K <= N, got '$SHARD'" >&2; exit 2
    fi
fi

JOBS="${BONFIRE_JOBS:-4}"
case "$JOBS" in ''|*[!0-9]*) JOBS=1 ;; esac
[ "$JOBS" -lt 1 ] && JOBS=1
CHROME_JOBS="${BONFIRE_CHROME_JOBS:-1}"
case "$CHROME_JOBS" in ''|*[!0-9]*) CHROME_JOBS=1 ;; esac
[ "$CHROME_JOBS" -lt 1 ] && CHROME_JOBS=1
FAILED=()
RAN=0
START=$SECONDS
SCRATCH=""
XPID=""
TIMES=()                      # "section/name seconds mode", one per harness that ran
SECTION="js"
MODE="parallel"

# ── Discovery, and the shard a harness is in ──────────────────────────────────────────
# Done before the lock, so --list can run beside a full run.
TIMINGS="${BONFIRE_TIMINGS_FILE:-tests/timings.txt}"
DEFAULT_SECONDS=10            # for a harness the timings file has not heard of
PARALLEL_WIDTH=4              # a parallel harness costs seconds/4 of a shard's time; fixed,
                              # not BONFIRE_JOBS, so shards do not depend on the machine
JS_DIR="${BONFIRE_JS_DIR:-tests/js}"   # the override is for tests/js/runner.js

# Harnesses that must not share the machine with each other. Everything else is a plain
# Node process over files and stubs and runs BONFIRE_JOBS at a time.
#   Chrome and the fixture server are not listed: needs_serial holds back any harness
#   whose source requires ../screens/ or names findChrome, so a new Chrome harness is
#   serial from its first run. Those share one Chrome profile cache, one font download
#   (ICONS) and the CPU its layout measurements depend on.
# Listed by name:
#   runner      starts this script about a dozen times to test it, each a full process tree
#   i18ntest    orders replies by real timers of different lengths
#   adminconfirm adminsweep28 dashboardxss serverinfo
#               settle on real 5 to 20 ms timers, which a loaded machine can reorder
SERIAL_JS=" runner i18ntest adminconfirm adminsweep28 dashboardxss serverinfo "

is_listed_serial() { case "$SERIAL_JS" in *" $1 "*) return 0 ;; esac; return 1; }
# One grep over every file, not one per harness: forking is slow on Windows.
CHROME_FILES="$(grep -lE 'screens/|findChrome|chrome\.launch' "$JS_DIR"/*.js 2>/dev/null)"
is_chrome() { case "
$CHROME_FILES
" in *"
$1
"*) return 0 ;; esac; return 1; }
needs_serial() {          # needs_serial <name> <file>
    is_listed_serial "$1" && return 0
    is_chrome "$2"
}

names=(); files=(); par=(); ser=()
HAS_JS() { [ "$WHICH" = "all" ] || [ "$WHICH" = "js" ]; }
if [ "$WHICH" = "all" ] || [ "$WHICH" = "js" ]; then
    # _lib.js and _stubdom.js are shared plumbing (any _*.js is). *.verify.js needs the
    # network and refreshes what the offline checks compare against: a maintenance tool,
    # not a gate. *.scan.js surveys the source and prints what it finds; it asserts
    # nothing and always exits 0, so counting it would add a harness that can never go red.
    for f in "$JS_DIR"/*.js; do
        base="${f##*/}"
        [ "${base#_}" != "$base" ] && continue
        case "$base" in *.verify.js|*.scan.js) continue ;; esac
        names+=("${base%.js}"); files+=("$f")
    done
fi
cs_names=()
if [ "$WHICH" = "all" ] || [ "$WHICH" = "cs" ] || [ "$WHICH" = "cs10" ]; then
    for d in tests/cs/*/; do d="${d%/}"; cs_names+=("${d##*/}"); done
fi

seconds_of() {            # seconds_of <section/name> -> the file's number, or the default
    local s
    s="$(awk -v k="$1" '$1 == k && $2 ~ /^[0-9.]+$/ { print $2; exit }' "$TIMINGS" 2>/dev/null)"
    echo "${s:-$DEFAULT_SECONDS}"
}

# shard_members <section> <name:mode>... -> " name name " for the chosen shard. A serial
# harness costs its seconds, a parallel one seconds/PARALLEL_WIDTH.
shard_members() {
    local sec="$1"; shift
    local item
    for item in "$@"; do echo "${item%%:*} ${item##*:}"; done \
        | awk -v sec="$sec" -v def="$DEFAULT_SECONDS" -v w="$PARALLEL_WIDTH" '
            FNR == NR { if ($0 !~ /^#/ && $2 ~ /^[0-9.]+$/) t[$1] = $2; next }
            { k = sec "/" $1; s = (k in t) ? t[k] : def; if ($2 == "parallel") s = s / w; printf "%s %.3f\n", $1, s }
          ' "$TIMINGS" - \
        | awk -v N="$SHARD_N" -f tests/shard.awk | awk -v K="$SHARD_K" '$1 == K { printf " %s", $2 } END { print " " }'
}

JS_MEMBERS=""; CS_MEMBERS=""
if [ -n "$SHARD" ]; then
    items=()
    for i in "${!names[@]}"; do
        if needs_serial "${names[$i]}" "${files[$i]}"; then items+=("${names[$i]}:serial"); else items+=("${names[$i]}:parallel"); fi
    done
    # sharedcopy is a check like the rest and sits in a shard like the rest.
    HAS_JS && items+=("sharedcopy:serial")
    if [ ${#items[@]} -gt 0 ]; then JS_MEMBERS="$(shard_members js "${items[@]}")"; fi
    items=()
    for n in "${cs_names[@]}"; do items+=("$n:serial"); done
    if [ ${#items[@]} -gt 0 ]; then CS_MEMBERS="$(shard_members cs "${items[@]}")"; fi
fi
# --only and --changed narrow to a set of "section/name" labels, held as " a b c ".
FILTER=""; FILTERED=""
in_filter() { [ -z "$FILTERED" ] || case "$FILTER" in *" $1 "*) return 0 ;; *) return 1 ;; esac; }
if [ -n "$ONLY" ]; then FILTERED=1; FILTER=" ${ONLY//,/ } "; fi
if [ -n "$CHANGED" ]; then
    # What changed: tracked files against the base, in the working tree too, and files git
    # does not know yet. --no-renames, so a moved file's old name is listed too.
    base="$CHANGED_BASE"
    if [ -z "$base" ]; then base="$(git merge-base HEAD origin/beta 2>/dev/null || git merge-base HEAD beta 2>/dev/null)"; fi
    if [ -z "$base" ] || ! git rev-parse --verify -q "$base^{commit}" >/dev/null; then
        echo "--changed: no base to compare with (no origin/beta or beta); pass one: --changed <commit>" >&2; exit 2
    fi
    CH_DIR="$(mktemp -d "${TMPDIR:-${TEMP:-/tmp}}/bonfire-changed.XXXXXX")"
    { git diff --name-only --no-renames "$base" --; git ls-files -o --exclude-standard; } | sort -u >"$CH_DIR/files"
    { for i in "${!names[@]}"; do echo "js/${names[$i]}"; done
      HAS_JS && echo "js/sharedcopy"
      for n in "${cs_names[@]}"; do echo "cs/$n"; done; } >"$CH_DIR/labels"
    if ! node tests/depends.js select tests/depends.txt "$CH_DIR/files" <"$CH_DIR/labels" >"$CH_DIR/chosen"; then
        echo "--changed: tests/depends.js failed" >&2; rm -rf "$CH_DIR"; exit 2
    fi
    FILTERED=1; FILTER=" "
    skipped=0
    echo "── --changed (against $(git rev-parse --short "$base")): $(wc -l <"$CH_DIR/files" | tr -d ' ') changed files ──" >&2
    head -12 "$CH_DIR/files" | sed 's/^/    /' >&2
    [ "$(wc -l <"$CH_DIR/files")" -gt 12 ] && echo "    ..." >&2
    while IFS=$'\t' read -r verdict label reason; do
        if [ "$verdict" = RUN ]; then
            FILTER="$FILTER$label "
            echo "  run   $label  ($reason)" >&2
        else skipped=$((skipped + 1)); fi
    done <"$CH_DIR/chosen"
    echo "  skipped $skipped harnesses: none of the files they read changed" >&2
    rm -rf "$CH_DIR"
fi
has_cs() { [ -z "$FILTERED" ] || case "$FILTER" in *" cs/"*) return 0 ;; *) return 1 ;; esac; }

js_in_shard() { in_filter "js/$1" || return 1; [ -z "$SHARD" ] || case "$JS_MEMBERS" in *" $1 "*) return 0 ;; *) return 1 ;; esac; }
cs_in_shard() { in_filter "cs/$1" || return 1; [ -z "$SHARD" ] || case "$CS_MEMBERS" in *" $1 "*) return 0 ;; *) return 1 ;; esac; }

if [ -n "$LIST" ]; then
    for i in "${!names[@]}"; do js_in_shard "${names[$i]}" && echo "js/${names[$i]}"; done
    if HAS_JS && js_in_shard sharedcopy; then echo "js/sharedcopy"; fi
    for n in "${cs_names[@]}"; do cs_in_shard "$n" && echo "cs/$n"; done
    exit 0
fi

# ── One full run at a time ─────────────────────────────────────────────────────────────
# A lock directory under the system temp dir (mkdir is atomic) holding the owner's pid. A
# lock whose process is gone is taken over, so a crash or Ctrl-C cannot wedge the next
# run. A nested run.sh (tests/js/runner.js starts several) inherits BONFIRE_RUN_LOCKED and
# does not take it again.
LOCKDIR="${TMPDIR:-${TEMP:-/tmp}}/bonfire-tests.lock"
OWNS_LOCK=""

lock_holder() { cat "$LOCKDIR/pid" 2>/dev/null; }

acquire_lock() {
    [ -n "${BONFIRE_RUN_LOCKED:-}" ] && return 0
    local waited=0 limit="${BONFIRE_LOCK_WAIT:-3600}" told="" pid
    while ! mkdir "$LOCKDIR" 2>/dev/null; do
        pid="$(lock_holder)"
        if [ -z "$pid" ]; then
            # The owner has made the directory and not yet written its pid. Give it a
            # moment before calling the lock abandoned.
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

# Recording: every Node process a harness starts loads tests/record-reads.js, which writes
# what it read under $BONFIRE_RECORD_DIR. The record is rewritten at the end of the run.
if [ -n "$RECORD" ]; then
    REC_DIR="$(mktemp -d "${TMPDIR:-${TEMP:-/tmp}}/bonfire-record.XXXXXX")"
    # Node on Windows cannot open Git Bash's /d/... paths, so hand it the real ones.
    REC_WIN="$REC_DIR"; PRELOAD="$ROOT/tests/record-reads.js"
    if command -v cygpath >/dev/null 2>&1; then REC_WIN="$(cygpath -m "$REC_DIR")"; PRELOAD="$(cygpath -m "$PRELOAD")"; fi
    export BONFIRE_RECORD_DIR="$REC_WIN"
    export NODE_OPTIONS="${NODE_OPTIONS:-} --require \"$PRELOAD\""
fi

# Print one harness's verdict. judge <label> <exit code> <output>
judge() {
    local label="$1" rc="$2" out="$3" us="${4:-0}" took
    took="$(printf '%d.%d' $((us / 1000000)) $(((us % 1000000) / 100000)))"
    TIMES+=("$SECTION/$label $took $MODE")
    printf '  %-14s ' "$label"
    RAN=$((RAN + 1))
    if [ "$rc" -eq 0 ]; then
        # Exit 0 with no output, or zero checks counted, proved nothing: an emptied test
        # body looks exactly like that. Fail it.
        if [ -z "$(printf %s "$out" | tr -d '[:space:]')" ] \
           || printf '%s\n' "$out" | grep -Eiq '(^|[^0-9.])0 (passed|checks|assertions)|pass 0([^0-9]|$)'; then
            FAILED+=("$label")
            echo "FAILED (asserted nothing)  (${took}s)"
            return
        fi
        # Exit 0 without ending on a totals line means it stopped early (some PASS lines,
        # then fell off the end). A skip says so on its last line.
        if ! printf '%s\n' "$out" | grep -v '^[[:space:]]*$' | tail -1 \
             | grep -Eq '[0-9]+ passed|pass [0-9]+, fail [0-9]+|^[[:space:]]*SKIPPED'; then
            FAILED+=("$label")
            echo "FAILED (no totals line)  (${took}s)"
            echo "$out" | sed 's/^/        /' | tail -5
            return
        fi
        printf '%s  (%ss)\n' "$(echo "$out" | tail -1 | cut -c1-70)" "$took"
    else
        FAILED+=("$label")
        echo "FAILED  (${took}s)"
        # The failing lines first: the tail alone hid both of themestep's failures behind
        # 25 PASS lines on the first Linux run.
        echo "$out" | grep -E '^[[:space:]]*FAIL[[:space:]:]' | head -20 | sed 's/^/        /'
        echo "$out" | sed 's/^/        /' | tail -25
    fi
}

run() {                       # run <label> <command...>
    local label="$1"; shift
    local out rc t0
    t0=$(now)
    out=$(BONFIRE_RECORD_LABEL="$SECTION/$label" "$@" 2>&1); rc=$?
    judge "$label" "$rc" "$out" $(( $(now) - t0 ))
}

# Run the harnesses at these indexes, <jobs> at a time, and print their verdicts in the
# order given however the workers finish. run_pool <jobs> <mode> <index>...
run_pool() {
    local jobs="$1" i n; MODE="$2"; shift 2
    [ $# -eq 0 ] && return 0
    for i in "$@"; do printf '%s\0' "${files[$i]}"; done >"$SCRATCH/pool.list"
    xargs -r -0 -n 1 -P "$jobs" bash "${BASH_SOURCE[0]}" --worker "$SCRATCH" <"$SCRATCH/pool.list" &
    XPID=$!
    for i in "$@"; do
        n="${names[$i]}"
        while [ ! -f "$SCRATCH/$n.rc" ]; do
            if ! kill -0 "$XPID" 2>/dev/null; then
                sleep 0.5
                [ -f "$SCRATCH/$n.rc" ] || { echo 1 >"$SCRATCH/$n.rc"; echo "the worker never finished" >"$SCRATCH/$n.out"; }
                break
            fi
            sleep 0.1
        done
        judge "$n" "$(cat "$SCRATCH/$n.rc")" "$(cat "$SCRATCH/$n.out")" "$(cat "$SCRATCH/$n.us" 2>/dev/null || echo 0)"
    done
    wait "$XPID" 2>/dev/null
    XPID=""
}

# The framework the C# harnesses load the plugin from. They set their own TargetFramework
# from it too, because a net9 harness cannot load a net10 assembly.
TFM="net9.0"
[ "$WHICH" = "cs10" ] && TFM="net10.0"

if [ "$WHICH" != "js" ] && has_cs; then
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
    SECTION="js"
    # Only this shard's harnesses (all of them when there is no --shard).
    sel=()
    for i in "${!names[@]}"; do js_in_shard "${names[$i]}" && sel+=("$i"); done

    if [ "$JOBS" -le 1 ]; then
        for i in "${sel[@]}"; do
            if needs_serial "${names[$i]}" "${files[$i]}"; then MODE=serial; else MODE=parallel; fi
            run "${names[$i]}" node "${files[$i]}"
        done
    else
        SCRATCH="$(mktemp -d "${TMPDIR:-${TEMP:-/tmp}}/bonfire-js.XXXXXX")"
        chrome=()
        for i in "${sel[@]}"; do
            if ! needs_serial "${names[$i]}" "${files[$i]}"; then
                par+=("$i")
            elif [ "$CHROME_JOBS" -gt 1 ] && ! is_listed_serial "${names[$i]}"; then
                chrome+=("$i")           # only these may overlap, and only when asked to
            else
                ser+=("$i")
            fi
        done
        # Results print in name order as each one's turn comes up, so the output reads the
        # same however the workers finish.
        run_pool "$JOBS" parallel "${par[@]}"
        # Then the ones that need the machine to themselves, one at a time...
        MODE=serial
        for i in "${ser[@]}"; do
            run "${names[$i]}" node "${files[$i]}"
        done
        # ...except Chrome ones, which run BONFIRE_CHROME_JOBS at a time when that is set.
        run_pool "$CHROME_JOBS" serial "${chrome[@]}"
    fi
    MODE=serial
    # The vendored Campground.Shared must match campground-shared.json byte for byte.
    # Edited in place, it would ship a library no other Campground plugin has, and the
    # newest copy on a server runs index.html for all of them. Change it in
    # Campground-Coordination and re-vendor. Here, not its own CI step, so the desk and
    # the pipeline cannot disagree.
    if js_in_shard sharedcopy; then
        run "sharedcopy" sh -c 'sh Campground.Shared/check-shared.sh && echo "1 passed, 0 failed"'
    fi
    echo
fi

if [ "$WHICH" = "all" ] || [ "$WHICH" = "cs" ] || [ "$WHICH" = "cs10" ]; then
    echo "── C# (against the $TFM build) ────────────────────────────────"
    # Every harness is built once, together, then run from its output. `dotnet run` per
    # harness restored and built each project on its own, and the net10.0 pass did it all
    # again. A harness that did not build is reported FAILED below, by name, rather than
    # failing the whole section.
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
    SECTION="cs"; MODE="serial"
    for d in tests/cs/*/; do
        d="${d%/}"; n="$(basename "$d")"
        cs_in_shard "$n" || continue
        # The output is named for the project's AssemblyName: the directory's name in most,
        # "<name>harness" or the .csproj's own name in the rest.
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

if [ -n "$RECORD" ]; then
    # C# harnesses are recorded by what they are built from (tests/depends.js csDeps) plus
    # whatever Node they started. The ones that ran are listed so they get that entry.
    cs_ran=""
    for t in "${TIMES[@]}"; do case "$t" in cs/*) l="${t%% *}"; cs_ran="$cs_ran,${l#cs/}" ;; esac; done
    node tests/depends.js merge "$REC_DIR" tests/depends.txt --cs "${cs_ran#,}"
    rm -rf "$REC_DIR"
fi
echo "──────────────────────────────────────────────────────────────"
echo "  ($((SECONDS - START))s)"
if [ ${#TIMES[@]} -gt 0 ]; then
    OUT_TIMES="${TMPDIR:-${TEMP:-/tmp}}/bonfire-timings.txt"
    {
        echo "# Wall seconds per harness: <section/name> <seconds> <serial|parallel>."
        echo "# Written by tests/run.sh; copy over tests/timings.txt. A trailing 'est' marks a guess."
        printf '%s\n' "${TIMES[@]}" | sort
    } >"$OUT_TIMES" 2>/dev/null
    echo "  the ten slowest:"
    printf '%s\n' "${TIMES[@]}" | sort -k2,2 -nr | head -10 | awk '{ printf "    %-28s %7.1fs  %s\n", $1, $2, $3 }'
    echo "  timings written to $OUT_TIMES"
fi
if [ ${#FAILED[@]} -eq 0 ]; then
    echo "  $RAN harnesses, all green."
    exit 0
fi
echo "  $RAN harnesses, ${#FAILED[@]} failed: ${FAILED[*]}"
exit 1
