---
name: release
description: Cut a Bonfire release — beta or stable. Use whenever publishing a version, bumping InformationalVersion, adding a manifest entry, or tagging. Covers the channel routing that decides which manifest a build lands in, the three ways the workflow refuses a release, the rebase the bot's checksum commit forces, and the end-to-end verification that proves the bytes users install actually work.
---

# Releasing Bonfire

Every step here exists because skipping it broke something. Two releases shipped with a
placeholder checksum and could not be installed; one version was published to both
manifests and offered users a blind choice between a working entry and a broken one; and
1.5.2 shipped a client script that never ran, past a syntax check that passed.

## Before anything

```
tests/run.sh          # 34 harnesses, ~1,450 assertions. Not optional.
```

`node --check Web/profiles.js` is **necessary and not sufficient** — it passed against the
defect that made 1.5.2 and 1.5.3 dead on arrival.

## 1. Pick the channel

`<InformationalVersion>` in `Jellyfin.Profiles.csproj` is the only place a pre-release
label can live, because Jellyfin requires a purely numeric assembly version. It alone
decides everything downstream:

| `InformationalVersion` | Manifest | Branch | GitHub release |
| --- | --- | --- | --- |
| `1.6.0-beta` (also `-alpha`, `-rc`) | beta manifest | `beta` | prerelease |
| `1.6.0` | stable manifest | `main` | latest |

The stable manifest is a curated list of milestones that every install polls. A beta
pushed there is offered to everyone as an upgrade.

## 2. Bump both versions

```xml
<Version>1.6.0</Version>                          <!-- numeric only -->
<InformationalVersion>1.6.0-beta</InformationalVersion>
```

## 3. Add the manifest entry — on the channel's branch

The workflow **fails** if the entry is missing. It does not create one.

```json
{
  "version": "1.6.0.0",                    // four parts
  "changelog": "…",
  "targetAbi": "10.11.0.0",
  "sourceUrl": ".../releases/download/v1.6.0/Jellyfin.Profiles.zip",
  "checksum": "0",                         // placeholder; the workflow stamps it
  "timestamp": "2026-08-27T00:00:00Z"
}
```

**A version lives in exactly one manifest.** Both files declare the same plugin GUID, so
Jellyfin merges them for anyone subscribed to both. A version in both keeps its
placeholder checksum in one copy and offers an install that fails the checksum. The
workflow checks this and refuses.

Release-note format, which Jellyfin renders as Markdown in the update dialog and the
workflow reuses as the GitHub release body:

```
**Beta release** — please report issues on GitHub.

**Fixed**

- The symptom, in the user's words, one line. (#23)

**Changed**

- One line.
```

Bold section headers, **blank line after each header or the list will not render**, the
symptom rather than the mechanism, issue number in brackets. Reasoning goes in the commit
message. If a release changes nothing a user can see, say so in one line rather than
dressing up internals.

## 4. Commit, push, tag

```
git add -A && git commit          # separate "release: X manifest entry" commit
git push origin beta
git tag -a v1.6.0 -m "…"          # MUST equal <Version> exactly
git push origin v1.6.0
```

The workflow cross-checks the tag against `<Version>` and refuses a mismatch, because the
tag drives the release URL while the csproj version drives which manifest entry gets the
checksum — if they drift it stamps the wrong entry.

## 5. The bot pushes back

The workflow commits `ci: update manifest checksum for vX [skip ci]` to the channel
branch. **Your next push will be rejected** as non-fast-forward.

```
git fetch origin && git rebase origin/beta
```

Do this before any further work. It has caught me out on consecutive releases.

## 6. Verify what users actually get

Do not trust the workflow's own green tick. Check the bytes:

```bash
curl -sL -o rel.zip ".../releases/download/v1.6.0/Jellyfin.Profiles.zip"
md5sum rel.zip                                    # must equal the live manifest checksum
curl -s ".../beta/manifest.json" | head           # the live feed, not your local file
```

Then confirm the shipped script actually runs — extract `profiles.js` from the downloaded
DLL by reflection and run the startup test against it:

```
node tests/js/inject.test.js <extracted-profiles.js>
```

This is the step that would have caught 1.5.2. The DLL also reports its own version:
check `AssemblyInformationalVersionAttribute` reads what you intended.

## Cutting stable from beta

See [[bonfire-release-process]] in memory for the exact merge sequence — it was confirmed
by doing 1.5.0 and has traps that are not obvious. The short version: the stable manifest
on `main` is curated, so promoting a beta means adding a *new* milestone entry there, not
copying the beta entry across.
