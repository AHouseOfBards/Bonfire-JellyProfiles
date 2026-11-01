# Beta Channel

Beta builds let you try new work before it reaches everyone.

| Channel | Repository URL |
| --- | --- |
| **Stable** | `https://ahouseofbards.github.io/Bonfire-JellyProfiles/manifest.json` |
| **Beta** | `https://raw.githubusercontent.com/AHouseOfBards/Bonfire-JellyProfiles/beta/manifest.json` |

> [!WARNING]
> **Beta builds are unfinished and features in them may be broken.** Settings added in a
> beta can change before release, and going back to stable may reset them. If the
> switcher breaks, the [emergency disable code](limitations.md#emergency-disable-code)
> can switch Bonfire off. Don't run a beta on a server your household needs that evening.

Please report what you find on
[GitHub Issues](https://github.com/AHouseOfBards/Bonfire-JellyProfiles/issues).

## Using it

In Jellyfin, open **Dashboard → Plugins → Repositories → ＋**, add the beta URL and save.
The beta builds then appear under **Plugins → Catalog**.

**Add it alongside the stable repository, not instead of it.** Each version is listed in
only one of the two, so the beta repository on its own never offers stable releases.
Remove the beta repository to go back to stable only.

## What's in each

**Stable** lists milestone releases only: 1.0, 1.1, 1.1.13, 1.2, 1.2.12, 1.3, 1.4, 1.5,
1.6, 1.6.1, 1.6.2, 1.6.3, 1.6.4, 1.6.5, 1.6.6 and 1.7.

**Beta** lists every pre-release, plus point releases that never became milestones.

If you're choosing between an older line's two stable versions, take the higher one.
1.1.13 fixes a crash loading the plugin on Jellyfin 10.11.5, and 1.2.12 fixes a bug in
1.2.0 that could lose device restrictions.

## Why no version is in both

Both repositories describe the same plugin, so Jellyfin merges their lists. A version in
both would appear twice, and one copy would fail to install. The release workflow
refuses to publish a version that's already in the other list.
