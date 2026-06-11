# Jellyfin Profiles Plugin

Adds multi-user profile switching to Jellyfin. A single account can have up to five isolated profiles — each with its own watch history, parental controls, and library access.

> Built for Jellyfin Server **10.11.x** (all minor versions supported).

---

## Screenshots

![Profile selection screen](images/profile-selector.png)

*Profile selector — shown on launch and when switching profiles.*

![Create profile screen](images/create-profile.png)

*Create profile — name, PIN, auto-lock timer, avatar color, parental rating, and library access.*

---

## Installation

1. In your Jellyfin dashboard, go to **Plugins → Repositories → ＋**
2. Paste the following URL and click **Save**:
   ```
   https://ahouseofbards.github.io/JellyProfiles/manifest.json
   ```
3. Go to **Plugins → Catalog**, find **Profiles Management**, and click **Install**
4. Restart your Jellyfin server when prompted

---

## Features

- Up to 5 isolated profiles per Jellyfin account
- Per-profile PIN protection with auto-submit on correct entry
- Configurable inactivity auto-lock (1 min – 1 hour) per profile
- Parental rating limits per profile
- Per-profile library access control
- Profile avatars with 18 customizable colors
- Full profile management dashboard built into the Jellyfin web UI
- Switch Profile button injected into the Jellyfin header — works on desktop, mobile, and TV browsers

---

## Client Compatibility

**Works out of the box:**
- Jellyfin Web (desktop browsers — Chrome, Firefox, Safari, Edge)
- Jellyfin Web (mobile browsers — iOS Safari, Android Chrome)
- Jellyfin (official Android app — renders the Jellyfin web UI internally)
- Jellyfin Media Player (Windows, macOS, Linux)

**Requires developer integration to support profiles:**
- Swiftfin (iOS / tvOS — fully native, does not use the web UI)
- Findroid (Android / Android TV — fully native, does not use the web UI)
- Jellyfin for Roku
- Infuse (iOS / tvOS / macOS — closed source, requires Firecore)
- Any other native Jellyfin client

> TV browsers (Samsung, LG, Fire TV, etc.) may work when accessing Jellyfin Web directly, but this is untested. Native TV apps listed above fall into the integration category.

---

## For Developers

Building a native app or custom Jellyfin client?

📄 **[Developer API Reference](docs/developer-api.md)**

Covers all endpoints, request/response schemas, the session lifecycle, silent PIN verification, inactivity lockout, and platform-specific implementation notes for tvOS, Android, Roku, Tizen, webOS, Xbox, PS4/PS5, and Electron.

---

## License

MIT — see [LICENSE](LICENSE)
