# Bonfire/JellyProfiles

Adds multi-user profile switching to Jellyfin. One account can hold several isolated
profiles, each with its own watch history, parental controls, and library access.

> Built for Jellyfin Server **10.11.x and 12.0** (all minor versions supported).
> One install serves both — there is nothing to choose between.

---

## Screenshots

![The profile picker, showing two profiles in a Bonfire](images/profile-selector.png)

*Shown when the app opens, and whenever you switch.*

![The create-profile form](images/create-profile.png)

*Creating a profile: libraries, PIN, device limits and tag filters in one place.*

---

## Features

- **Several profiles per Jellyfin account**, each with its own watch history, library
  access and parental rating. Five by default; an administrator can set anything from 1
  to 20, and can raise or lower it for individual accounts.
- **Tag filters.** Block or allow content per profile using Jellyfin's own tags
  (`adults`, `kids`, and so on). Tags are inherited, so tagging a series or a whole
  library covers everything inside it. Jellyfin enforces this server-side, so it holds on
  every client — including the ones that cannot show the switcher.
- **PINs.** Optional per profile, stored as salted PBKDF2-SHA256 hashes, with an optional
  bypass on your own network.
- **Device limits.** Restrict a profile to particular devices.
- **Your Bonfire.** Link accounts with a 6-character code so two households share one
  switcher screen.
- **Avatar library.** Upload a set of pictures everyone on the server can pick from, and
  optionally require them. On a TV this is the only practical way to set a picture, since
  there is no file browser.
- **Switcher style.** Each account picks the full-screen "Who's Watching?" gate or a
  **Switch Profile** entry in Jellyfin's own menu, under **Settings → Switcher Style**. It
  is a per-household choice, not a server setting.
- **Library artwork.** Give a profile its own picture for a library, or none at all, so a Kids profile does not get a Movies tile built from a film it cannot open.
- **Televisions and other apps.** Apps that never load the web client — Android TV, Roku, Swiftfin, Findroid, Wholphin — can offer a household's profiles on their own sign-in screen, opened with a PIN. Off until an administrator turns it on.

---

## Installation

1. In your Jellyfin dashboard, go to **Plugins → Repositories → ＋**
2. Paste the following URL and click **Save**:
   ```
   https://ahouseofbards.github.io/Bonfire-JellyProfiles/manifest.json
   ```
3. Go to **Plugins → Catalog**, find **Bonfire/JellyProfiles**, and click **Install**
4. Restart your Jellyfin server when prompted

Once the server restarts the plugin is active and loads on all compatible clients with no
further setup.

Pre-release builds live in a separate repository — see
[BETA-CHANNEL.md](BETA-CHANNEL.md). Add it alongside the stable one, never instead of it.

If the switcher does not appear, or the settings page reports a problem, see
[TROUBLESHOOTING.md](TROUBLESHOOTING.md). The short version: **Bonfire does not edit
`index.html` by default, so a file with no plugin tags in it is a healthy install.**

---

## Client Compatibility

**Fully compatible** — the switcher, profile management, avatars, everything:

- Jellyfin Web, and Jellyfin for Android
- Jellyfin Media Player (Windows, macOS, Linux)
- LG webOS
- Samsung Tizen, if Bonfire is bundled into the `.wgt` at build time

**Selection only** — profiles appear in the app's own sign-in screen and open with their
PIN. PINs, device restrictions and parental controls all hold; profile management needs a
browser. Turn on **Dashboard → Bonfire → TVs & Apps**, off by default.

- Jellyfin for Android TV
- Jellyfin for Roku, Swiftfin, Findroid, Wholphin — new in 1.6.2.1-beta, not yet confirmed
  on hardware

Turn off automatic sign in, or the app goes straight into the last account and never shows
the profiles. On Android TV that is **Settings → Login → Automatic sign in → Disable**; on
Roku, untick *Remember me*.

**Your PIN is the password.** These apps ask for a password because that is the only field
they have. Type the PIN instead — for a sub-profile, and for an account that owns profiles
and has set one. A master account without a PIN still uses its real password.

Everything else, and why, is in [docs/clients.md](docs/clients.md).

> [!IMPORTANT]
> Library access, maximum parental rating and tag filters are stored on the Jellyfin
> account and enforced by the server, so a profile sees only what it is allowed to see on
> every client — including ones Bonfire cannot reach at all.
>
> Each profile also keeps its own copy of that list, and re-applies it on entry so a
> Jellyfin policy reset does not wipe a profile's settings. Edit a profile's libraries in
> **Edit profile → Libraries**; see
> [TROUBLESHOOTING.md](TROUBLESHOOTING.md#a-profiles-library-access-keeps-reverting) for
> how that interacts with **Dashboard → Users**.

---

## Documentation

| | |
| --- | --- |
| [docs/sharing.md](docs/sharing.md) | Sharing a Bonfire, and the two rules that protect it |
| [docs/library-artwork.md](docs/library-artwork.md) | Per-profile pictures for a library |
| [docs/clients.md](docs/clients.md) | Every client, what works on it, and what does not |
| [docs/limitations.md](docs/limitations.md) | Custom themes, and the emergency disable code |
| [TROUBLESHOOTING.md](TROUBLESHOOTING.md) | The switcher does not appear, and other support answers |
| [BETA-CHANNEL.md](BETA-CHANNEL.md) | Pre-release builds, and why the two version lists differ |
| [CHANGELOG.md](CHANGELOG.md) | Every release |
| [docs/developer-api.md](docs/developer-api.md) | All 50 routes, and the Jellyfin routes the plugin changes |

---

## License

MIT — see [LICENSE](LICENSE)
