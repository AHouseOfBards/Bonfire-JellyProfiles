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
- **Televisions and other apps.** Apps that never load the web client — Android TV, Roku, Swiftfin — can offer a household's profiles on their own sign-in screen, opened with a PIN. Off until an administrator turns it on.

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

## Library Artwork

Jellyfin builds a library tile from the items inside it without knowing who is looking, so
a Kids profile can end up with a Movies tile showing a film it cannot open.

Give a profile its own picture for a library, or no artwork at all, under **Edit profile →
Library Artwork**. With no artwork the tile falls back to its icon and name; libraries left
alone keep Jellyfin's own.

Two limits: the swap happens in the browser, so the original is still downloaded, and it
only applies where Bonfire runs.

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

- Jellyfin for Android TV (tested)
- Jellyfin for Roku, Swiftfin, Wholphin (untested)

Everything else, and why, is in [docs/clients.md](docs/clients.md).

> [!IMPORTANT]
> Library access, maximum parental rating and tag filters are stored on the Jellyfin
> account and enforced by the server, so a profile sees only what it is allowed to see on
> every client — including ones Bonfire cannot reach at all.

---

## Bonfire Sharing & Security

Sharing a Bonfire code lets another household see your switcher screen, and switching
into an account gives a real, fully privileged session for it. Two rules protect that:

- **An account with no PIN cannot be opened from a shared Bonfire.** If you want other
  members to be able to switch into your main account, set a profile PIN on it first.
  Sub-profiles are unaffected — they work with or without a PIN.
- **The LAN bypass never applies across accounts.** Being on the same network as someone
  in your Bonfire does not skip their PIN; it only skips your own.

> [!TIP]
> A Bonfire code is a credential. Anyone who has it can join, and you can only be in one
> Bonfire at a time — joining a new one removes you from your current one.

### Sharing a TV with another adult

Typing a PIN with a TV remote every time two adults swap accounts is miserable, so each
account can lift both rules **for itself**. In **Settings → Your Bonfire** on the switcher
screen, tick *"Let my Bonfire switch into my account on this network"*.

People in your Bonfire can then enter your account from your home network without your
PIN, including when you have none. Away from home nothing changes. It is off by default,
only you can turn it on for your own account, and every switch that uses it is logged.

> [!WARNING]
> Two things to check first. If your account is a **Jellyfin administrator**, anyone who
> switches into it can change server settings and manage every user on it — only enable
> this if you would hand them the password. And "your home network" is whatever your
> *server* counts as local: if it sits behind a reverse proxy that is not listed under
> **Dashboard → Networking → Known Proxies**, every visitor looks local, and this setting
> would apply to all of them.

---

## Known Limitations

**Skin Manager / custom themes**  
Custom themes and skin managers can leave the Switch Profile button misaligned. The
**Jellyfin menu** style under *Settings → Switcher Style* removes the injected button and
puts the switcher on your profile page instead. Either way, please open an issue with the
name of the theme.

**Profile creation is on the home screen, not the admin dashboard**  
Profiles are created and managed from the Switch Profile button on the Jellyfin home
screen. **Dashboard → Plugins → Bonfire** is for server-wide settings, the avatar library,
administrator PIN resets, TV and app sign-in, and the emergency disable code.

**Emergency disable code**  
A code that shuts Bonfire off **until Jellyfin restarts**, for when the plugin has made
the web interface hard to use — including the settings page you would need to uninstall
it. Set one under **Dashboard → Plugins → Bonfire → Advanced**, then enter it on any
Bonfire screen or press `Ctrl+Shift+B`. Off by default.

Before you turn it on:

- It does **not** unlock other profiles, and does not widen library access, parental
  ratings or tag filters. Jellyfin enforces those regardless of this plugin.
- It **does** skip the profile gate. On a device already signed in to the master account,
  anyone with the code gets that account's full library.
- It is submitted without a password, so make it long. Five attempts per hour per address,
  and every use is logged.
- If the plugin's script fails to load at all, the code has nothing to run in. Restart
  Jellyfin, or delete the plugin folder.

---

## Documentation

| | |
| --- | --- |
| [docs/clients.md](docs/clients.md) | Every client, what works on it, and what does not |
| [docs/developer-api.md](docs/developer-api.md) | All 50 routes, and the Jellyfin routes the plugin changes |
| [TROUBLESHOOTING.md](TROUBLESHOOTING.md) | The switcher does not appear, and other support answers |
| [BETA-CHANNEL.md](BETA-CHANNEL.md) | Pre-release builds, and why the two version lists differ |
| [CHANGELOG.md](CHANGELOG.md) | Every release |

All 50 endpoints in one table with their authorisation level, the seven routes that work
without a token and why each one has to, error codes and rate limits, profile switching
and PIN verification, the Bonfire sharing rules, and how to add a translation.

For which clients the switcher can appear on at all, see
[Client Compatibility](#client-compatibility) above. The plugin injects a script into your
server's web client, so that is a question about the app, not about the API.

---

## License

MIT — see [LICENSE](LICENSE)
