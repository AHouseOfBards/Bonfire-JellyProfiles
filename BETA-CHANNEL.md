# Beta Channel

This branch serves the **pre-release manifest** for Bonfire/JellyProfiles.

## Repository URLs

| Channel | Repository URL | Contains |
|---|---|---|
| **Stable** (default) | `https://ahouseofbards.github.io/Bonfire-JellyProfiles/manifest.json` | Milestone releases — 1.0, 1.1, 1.1.13, 1.2, 1.2.12, 1.3, 1.4 |
| **Beta** | `https://raw.githubusercontent.com/AHouseOfBards/Bonfire-JellyProfiles/beta/manifest.json` | Pre-release builds, and point releases that never became milestones |

The two lists **do not overlap**: every published version appears in exactly one of
them. See *Why nothing appears twice* below.

Most people want the stable channel. Add the beta URL only if you are testing
pre-release builds or need a specific point release — and add it **as well as** the
stable one, never on its own, or you will stop being offered stable releases.

## How to use the beta channel

In Jellyfin: **Dashboard → Plugins → Repositories → ＋**, then paste the beta URL
above and save. Bonfire will then offer the full version list under
**Plugins → Catalog**.

Add the beta repository *alongside* the stable one. Both describe the same plugin
GUID, so Jellyfin merges them into a single version list. Because no version is in
both files, nothing appears twice and the merged list is simply every build there
is. Remove the beta repository to go back to milestones only.

## Why the two lists differ

The stable manifest deliberately lists only a handful of versions. The 1.1 and
1.2 lines each went through a long run of point releases, and listing all of
them in the catalogue made it hard to tell which build anyone should actually be
on. Both lines are represented twice: the `.0` that opened the line, and the
final fully-patched build of it.

Note that `1.1.0` and `1.2.0` are the *opening* builds of their lines and have
known issues that were fixed later — `1.1.13` fixes a loader crash on Jellyfin
10.11.5, and `1.2.12` fixes a device-whitelist data-loss bug present in `1.2.0`.
If you are choosing between them, take the higher one.

## Release assets

Nothing here changes where downloads come from. Every version in both manifests
points at its original GitHub release asset, so no download URL is affected by
which channel you use.

## Why nothing appears twice

Both manifests declare the same plugin GUID, so Jellyfin concatenates their version
lists for anyone subscribed to both. A version listed in each file would show up
twice, and the two copies could disagree: a release only writes its checksum into
the channel it was published to, so the other copy would keep its placeholder and
offer an install that fails the checksum.

So each version lives in exactly one manifest, and the release workflow refuses to
publish a version that is already listed on the other branch.
