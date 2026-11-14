#!/bin/sh
# Resolves every Jellyfin member the built plugin references against other Jellyfin releases, so a member that moved
# inside a major version fails here, not on a user's first call (Bonfire's binchk, moved into the kit).
#   sh tools/binchk.sh                  the releases in BINCHK_VERSIONS (tests/gate.conf)
#   sh tools/binchk.sh 12.0.0 latest    others; "latest" is the newest stable Jellyfin.Controller on NuGet
#   SKIP_BUILD=1 sh tools/binchk.sh     check the DLL that is already built
# Settings in tests/gate.conf: PROJECT (as for the gate), BINCHK_VERSIONS (required: the lowest release the plugin
# supports, then "latest"), and BINCHK_DLL (default: PROJECT's net10.0 Release build).
# It restores each release's packages, which needs the network the first time, so CI runs it as its own job rather
# than inside the gate.
set -u
root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root" || exit 2
# The environment wins over tests/gate.conf, so CI or a test can point it at another DLL.
env_dll=${BINCHK_DLL:-}; env_versions=${BINCHK_VERSIONS:-}
PROJECT=; BINCHK_VERSIONS=; BINCHK_DLL=
# shellcheck disable=SC1091
[ -f tests/gate.conf ] && . tests/gate.conf
[ -n "$env_dll" ] && BINCHK_DLL=$env_dll
[ -n "$env_versions" ] && BINCHK_VERSIONS=$env_versions
[ $# -gt 0 ] && BINCHK_VERSIONS="$*"
[ -n "$BINCHK_VERSIONS" ] || { echo "binchk: set BINCHK_VERSIONS in tests/gate.conf (e.g. \"12.0.0 latest\")" >&2; exit 2; }
if [ -z "$BINCHK_DLL" ]; then
    [ -n "$PROJECT" ] || { echo "binchk: set PROJECT or BINCHK_DLL in tests/gate.conf" >&2; exit 2; }
    BINCHK_DLL="$(dirname "$PROJECT")/bin/Release/net10.0/$(basename "$PROJECT" .csproj).dll"
fi

if [ -z "${SKIP_BUILD:-}" ]; then
    [ -n "$PROJECT" ] || { echo "binchk: PROJECT is not set, so there is nothing to build (use SKIP_BUILD=1)" >&2; exit 2; }
    dotnet build "$PROJECT" -c Release -nologo -v q >/dev/null || { echo "binchk: plugin build FAILED"; exit 1; }
fi
[ -f "$BINCHK_DLL" ] || { echo "binchk: $BINCHK_DLL not found"; exit 1; }

work=${TMPDIR:-/tmp}/campground-binchk-$$
trap 'rm -rf "$work"' EXIT
mkdir -p "$work"
dotnet build tools/binchk/binchk.csproj -c Release -nologo -v q --artifacts-path "$work/a" -o "$work/tool" >/dev/null || { echo "binchk: tool build FAILED"; exit 1; }

# Plain x.y.z only, so pre-releases (12.5.0-rc1) are skipped on purpose: a plugin claims stable releases.
latest() {
    curl -fsS https://api.nuget.org/v3-flatcontainer/jellyfin.controller/index.json \
        | tr ',' '\n' | sed -n 's/.*"\([0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*\)".*/\1/p' | sort -t. -k1,1n -k2,2n -k3,3n | tail -n 1
}

failed=0
for v in $BINCHK_VERSIONS; do
    if [ "$v" = latest ]; then
        v=$(latest)
        [ -n "$v" ] || { echo "binchk: couldn't read the newest Jellyfin.Controller from NuGet"; failed=1; continue; }
    fi
    echo "-- $BINCHK_DLL against Jellyfin $v"
    dotnet publish tools/binchk/pkgs/pkgs.csproj -c Release -nologo -v q -p:JfVersion="$v" --artifacts-path "$work/a$v" -o "$work/$v" >/dev/null \
        || { echo "  couldn't restore Jellyfin $v (network?)"; failed=1; continue; }
    dotnet "$work/tool/binchk.dll" "$BINCHK_DLL" "$work/$v" || failed=1
done
exit $failed
