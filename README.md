# Bonfire/JellyProfiles

Adds multi-user profile switching to Jellyfin. A single account can have up to five isolated profiles — each with its own watch history, parental controls, and library access.

> Built for Jellyfin Server **10.11.x** (all minor versions supported).

---

## Screenshots

![Profile selection screen](images/profile-selector.png)

*Profile selector — shown on launch and when switching profiles.*

---

## Installation

1. In your Jellyfin dashboard, go to **Plugins → Repositories → ＋**
2. Paste the following URL and click **Save**:
   ```
   https://ahouseofbards.github.io/Bonfire-JellyProfiles/manifest.json
   ```
3. Go to **Plugins → Catalog**, find **Bonfire/JellyProfiles**, and click **Install**
4. Restart your Jellyfin server when prompted

Once the server restarts, the plugin is active and will automatically load on all compatible clients with no further setup.

### Beta channel

The repository above lists **milestone releases only** — the builds meant for everyday use. Pre-release builds live in a separate repository, along with every published point release:

```
https://raw.githubusercontent.com/AHouseOfBards/Bonfire-JellyProfiles/beta/manifest.json
```

Add it alongside the stable one — both describe the same plugin, so Jellyfin merges them and simply offers more versions to choose from. Remove it to go back to milestones only. See [BETA-CHANNEL.md](https://github.com/AHouseOfBards/Bonfire-JellyProfiles/blob/beta/BETA-CHANNEL.md).

> [!WARNING]
> **Beta builds are unfinished and features in them may be broken.** They exist so new work can be tested before it reaches everyone, which means:
>
> * A feature may not work at all, may work differently from its description, or may disappear in the next build.
> * Settings introduced in a beta can change shape before release. Reverting to a stable build afterwards may leave those settings behind or reset them.
> * The profile switcher itself can break. If that happens the plugin can make the Jellyfin web interface hard to use — see the **Emergency disable code** under *Known Limitations* before you rely on a beta on a machine you need working.
>
> Please do report what you find on [GitHub Issues](https://github.com/AHouseOfBards/Bonfire-JellyProfiles/issues) — that is what the channel is for. Just don't put a beta on a server your household depends on that evening.

> [!NOTE]
> **Automatic Client Script Injection & Permissions:**
> On startup, the plugin automatically patches Jellyfin's `index.html` to inject the client-side profile switcher. If the Jellyfin process lacks write permissions to its web client files (common on Docker, Linux, or restricted Windows directories), the injection will fail.
> 
> * **How to know:** If injection fails, a prominent **⚠️ Client Script Auto-Injection Failed** banner will appear at the top of your plugin configuration page (**Dashboard → Plugins → Profiles**) with the copy-pasteable fix commands for your host OS.
> * **Quick Fixes:**
>   * **Linux (Native):** Run `sudo chmod 666 /usr/share/jellyfin/web/index.html` and restart Jellyfin.
>   * **Docker (Run on host):** Run `docker exec -u root <container-name> chmod 666 /usr/share/jellyfin/web/index.html` (adjust path if different) and restart the container.
>   * **Windows (Admin Command Prompt):** Run `icacls "C:\Program Files\Jellyfin\Server\jellyfin-web\index.html" /grant "NT AUTHORITY\NetworkService:(M)"` and restart Jellyfin.

---

## Features

- **Multi-User Profile Switching**: Up to 5 isolated sub-profiles per Jellyfin account, each with separate watch history, library access, and parental ratings.
- **Tag-Based Content Filtering**: Block or allow content per profile using Jellyfin's own tags (e.g. `adults`, `teens`, `kids`). Tags are inherited, so tagging a series or an entire library applies to everything inside it — and because this is enforced by Jellyfin server-side, it holds on *every* client, including the ones that can't do profile switching.
- **Resilient Deletion**: Automatically handles native Jellyfin database deletion bugs (like the playlist null reference error) by deactivating the underlying sub-profile user account and clearing plugin mappings.
- **Bonfire Grouping**: Link different master accounts together using secure 6-character codes to share switcher screens.
- **PIN Protection & LAN Bypass**: Secure profiles with PIN codes and bypass verification automatically when connected on your local network (LAN). PINs are stored as salted PBKDF2-SHA256 hashes.
- **Device Whitelists**: Limit specific profiles to designated devices.
- **Avatar Library**: Administrators can upload a set of profile pictures in the plugin settings that anyone on the server can choose from — with an option to require them, for a consistent household look. This is also the only practical way to set a profile picture on a TV, where there is no file browser. Pictures can be positioned and zoomed before saving.
- **Choose Your Switcher**: Each account decides how it reaches the switcher — the full-screen "Who's Watching?" gate on the home screen, or nothing at all in the way, with a **Switch Profile** entry added to Jellyfin's own user menu and profile page instead. Set it under **Manage Profiles → Switcher Style**; it is a per-household choice, not a server setting.
- **Premium UI**: Seamless native UI integration with custom profile pictures, custom avatar colors, and TV D-pad navigation support.

---

## Library Artwork

Jellyfin builds a library tile from the items inside it, and that query does not know who
is looking. So a Kids profile can end up with a Movies tile showing a poster from a film it
cannot open.

Give a profile its own picture for a library, or no artwork at all, under **Edit profile
→ Library Artwork**. With no artwork the tile falls back to its icon and name. Libraries
left alone keep Jellyfin's own.

Two limits. The swap happens in the browser, so the picture is never shown but jellyfin-web
still downloads the original. And it only applies where Bonfire runs — a client that cannot
load the plugin script shows Jellyfin's artwork as usual.

---

## Client Compatibility

Bonfire works by injecting a script into the web client your server hands out. So the dividing line is not the operating system — it is whether the app loads Jellyfin's web client **from your server** or ships its own copy.

**Works:**
- Jellyfin Web (desktop & mobile browsers)
- Official Jellyfin Android app — a wrapper around your server's web client
- Jellyfin Media Player (Windows, macOS, Linux)
- **LG webOS** — the app loads your server's web client into a frame, so the switcher comes with it

**Works, with a caveat:**
- **Samsung Tizen** — the Tizen app builds its own copy of the web client into the `.wgt` package rather than fetching yours, so the plugin cannot inject itself at runtime. Users have reported the switcher working anyway, with Bonfire bundled into the package at build time. It works, but the package has to be rebuilt to pick up plugin updates — nothing reaches it automatically.

**Cannot work:**
- Jellyfin for Android TV / Google TV — a native app, not a web client
- Swiftfin (iOS / tvOS), Findroid, Jellyfin for Roku, Infuse — likewise native

> [!IMPORTANT]
> **Parental controls still apply on every client, including the ones above.** Library access, maximum parental rating and tag filters are stored on the Jellyfin user account and enforced by the *server*. A sub-profile signed in on Android TV sees exactly what it is allowed to see. What those clients cannot show is the switcher itself — you sign in as the sub-profile directly instead.

---

## Bonfire Sharing & Security

Sharing a Bonfire code lets another household see your switcher screen — and switching into an account gives the person a real, fully privileged session for it. Two rules protect that boundary:

* **An account with no PIN cannot be opened from a shared Bonfire.** If you want other members to be able to switch into your main account, set a profile PIN on it first. Sub-profiles are unaffected — they work with or without a PIN.
* **The LAN bypass never applies across accounts.** Being on the same network as someone in your Bonfire does not skip their PIN; it only skips your own.

> [!TIP]
> A Bonfire code is a credential. Anyone who has it can join, and you can only be in one Bonfire at a time — joining a new one removes you from your current one.

### Sharing a TV with another adult

Both rules are strict by design, and for two adults who share a living room they are strict in the wrong direction: typing a PIN with a TV remote every time you swap accounts is miserable.

So each account can lift them **for itself**. In **Manage Profiles → Bonfire Grouping**, tick *"Let my Bonfire switch into my account on this network"*. People in your Bonfire can then enter your account from your home network without your PIN — including when you have no PIN at all. Away from home nothing changes: the PIN is still required, and an account with no PIN still cannot be opened remotely.

It is off by default, only you can turn it on for your own account, and every switch that uses it is written to the profile activity log.

> [!WARNING]
> Two things to check first. If your account is a **Jellyfin administrator**, anyone who switches into it can change server settings and manage every user on it — only enable this if you would hand them the password. And "your home network" is whatever your *server* counts as local: if it sits behind a reverse proxy that is not listed under **Dashboard → Networking → Known Proxies**, every visitor looks local, and this setting would apply to all of them.

---

## Known Limitations

**Skin Manager / custom themes**  
The Switch Profile button is designed to align with standard Jellyfin layouts. If you use custom themes or a skin manager, the button might occasionally appear misaligned or out of place. Switching to the **Jellyfin menu** style under *Manage Profiles → Switcher Style* removes the injected button entirely and puts the switcher on your profile page instead, which sidesteps theme conflicts. Either way, please open an issue with the name of the theme you are using.

**Profile creation is on the home screen, not the admin dashboard**  
Profiles are created and managed via the Switch Profile button on the Jellyfin home screen. The admin dashboard page (**Dashboard → Plugins → Profiles**) is for server-wide settings (maximum profile count, require-PIN policy), the avatar library, administrator PIN resets, and the emergency disable code.

**Emergency disable code**  
If Bonfire breaks badly it can make the Jellyfin web interface hard to use — including the settings page you would need to uninstall it. Administrators can set a code under **Dashboard → Plugins → Profiles → Emergency Disable Code**. Entering it on any Bonfire screen, or pressing `Ctrl+Shift+B`, shuts the plugin's client script off **until Jellyfin restarts**.

It is off by default, and worth understanding before you turn it on:

* It does **not** unlock other profiles — switching still requires that profile's PIN — and it does not widen library access, parental ratings or tag filters, which Jellyfin enforces server-side regardless of this plugin.
* It **does** skip the profile gate. On a device already signed in to the master account, anyone who knows the code gets that account's full library without being asked to choose a profile.
* It is submitted without a password, so make it long. Attempts are capped at five per hour per address, and every use is logged.
* It cannot rescue every failure. If the plugin's script fails to load at all, the code has nothing to run in — restart Jellyfin, or delete the plugin folder, for that case.

---

## For Developers

Building a native app or custom Jellyfin client?

📄 **[Developer API Reference](docs/developer-api.md)**

Covers all endpoints, request/response schemas, the session lifecycle, silent PIN verification, inactivity lockout, and platform-specific implementation notes for tvOS, Android, Roku, Tizen, webOS, Xbox, PS4/PS5, and Electron.

---

## License

MIT — see [LICENSE](LICENSE)
