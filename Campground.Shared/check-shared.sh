#!/usr/bin/env sh
# Verifies a plugin's vendored Campground.Shared matches its campground-shared.json.
# Plugin CI:  sh Campground.Shared/check-shared.sh
# A failure means someone edited the copy in place: make the change in
# AHouseOfBards/Campground-Coordination/shared and re-vendor instead.
set -eu

dir=$(cd "$(dirname "$0")" && pwd)
manifest="$dir/campground-shared.json"
[ -f "$manifest" ] || { echo "campground-shared.json missing in $dir" >&2; exit 1; }

fail=0
listed=$(sed -n 's/^ *"\([^"]*\)": "\([0-9a-f]\{64\}\)".*/\1 \2/p' "$manifest")
echo "$listed" | while read -r f h; do
  [ -n "$f" ] || continue
  if [ ! -f "$dir/$f" ]; then echo "missing: $f"; exit 1; fi
  actual=$( (sha256sum "$dir/$f" 2>/dev/null || shasum -a 256 "$dir/$f") | cut -d' ' -f1)
  [ "$actual" = "$h" ] || { echo "changed: $f"; exit 1; }
done || fail=1

# Files present but not in the manifest: anywhere in the folder, matched as whole names (a "." is not a wildcard).
(cd "$dir" && find . -type f ! -path ./campground-shared.json | sed 's|^\./||' | LC_ALL=C sort) | while read -r f; do
  echo "$listed" | awk -v f="$f" '$1 == f { found = 1 } END { exit !found }' || { echo "not in manifest: $f"; exit 1; }
done || fail=1

if [ $fail -ne 0 ]; then
  echo "Campground.Shared copy differs from campground-shared.json. Edit the canonical copy in Campground-Coordination and re-vendor." >&2
  exit 1
fi
echo "Campground.Shared $(sed -n 's/.*"version": "\([^"]*\)".*/\1/p' "$manifest") OK"
