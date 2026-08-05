# Beta Channel

This branch serves the **pre-release manifest** for Bonfire/JellyProfiles.

## Repository URLs

| Channel | Repository URL | Contains |
|---|---|---|
| **Stable** (default) | `https://ahouseofbards.github.io/Bonfire-JellyProfiles/manifest.json` | Milestone releases only — 1.0, 1.1, 1.1.13, 1.2, 1.2.12, 1.3 |
| **Beta** | `https://raw.githubusercontent.com/AHouseOfBards/Bonfire-JellyProfiles/beta/manifest.json` | Every published version, including all point releases |

Most people want the stable channel. Add the beta URL only if you are testing
pre-release builds or need to install a specific point release.

## How to use the beta channel

In Jellyfin: **Dashboard → Plugins → Repositories → ＋**, then paste the beta URL
above and save. Bonfire will then offer the full version list under
**Plugins → Catalog**.

Adding the beta repository *alongside* the stable one is fine — both describe the
same plugin GUID, so Jellyfin merges them and simply shows more versions to
choose from. Remove the beta repository to go back to milestones only.

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
