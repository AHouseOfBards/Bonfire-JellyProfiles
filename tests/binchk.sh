#!/usr/bin/env bash
#
# Resolve every Jellyfin/MediaBrowser member the built plugin references against the newest
# 10.11.x and against 12.0 (C-1008).
#
#   tests/binchk.sh                      # the versions below
#   tests/binchk.sh 10.11.11 12.0.0      # others
#   PLUGIN=path/to/Jellyfin.Profiles.dll tests/binchk.sh
#
# Not part of tests/run.sh, deliberately: it has to restore the Jellyfin packages for each
# version, which needs the network the first time (they are cached after). CI runs it as a
# separate step. Bump the first default when a newer 10.11 patch appears; it is the one
# that is allowed to go stale, 12.0 is the line that will be dropped on.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

VERSIONS=("$@")
[ ${#VERSIONS[@]} -eq 0 ] && VERSIONS=(10.11.11 12.0.0)
PLUGIN="${PLUGIN:-bin/Release/net9.0/Jellyfin.Profiles.dll}"

if [ -z "${SKIP_BUILD:-}" ] && [ -z "${PLUGIN_GIVEN:-}" ]; then
    dotnet build -c Release --nologo -v q >/dev/null || { echo "build FAILED"; exit 1; }
fi

WORK="${TMPDIR:-/tmp}/bonfire-binchk"
rm -rf "$WORK"; mkdir -p "$WORK"
dotnet build tests/binchk/binchk.csproj -c Release --nologo -v q -o "$WORK/tool" >/dev/null || { echo "binchk build FAILED"; exit 1; }

FAILED=0
for v in "${VERSIONS[@]}"; do
    echo "── $PLUGIN against Jellyfin $v"
    dotnet publish tests/binchk/pkgs/pkgs.csproj -c Release --nologo -v q -p:JfVersion="$v" -o "$WORK/$v" >/dev/null \
        || { echo "  could not restore Jellyfin $v (network?)"; FAILED=1; continue; }
    dotnet "$WORK/tool/binchk.dll" "$PLUGIN" "$WORK/$v" || FAILED=1
done
exit $FAILED
