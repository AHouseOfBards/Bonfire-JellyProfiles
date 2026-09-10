# Clients

Which apps can reach a Bonfire profile, and how.

There are two ways in. Which one an app gets depends on whether it loads the web client
from your server or ships its own copy.

## Fully compatible

The full switcher: the "Who's Watching?" gate, profile creation and editing, avatars,
library artwork, Bonfire sharing.

| Client | Notes |
| --- | --- |
| Jellyfin Web | Desktop and mobile browsers. |
| Jellyfin for Android | A wrapper around your server's web client. |
| Jellyfin Media Player | Windows, macOS, Linux. |
| LG webOS | Loads your server's web client into a frame. |
| Samsung Tizen | Only if Bonfire is bundled into the `.wgt` at build time. The package must be rebuilt to pick up plugin updates. |

## Selection only

Profiles appear in the app's own sign-in screen and are opened with their PIN. No switcher,
no profile management — those need a browser or one of the clients above.

Requires **Dashboard → Bonfire → TVs & Apps**, off by default.

| Client | Status |
| --- | --- |
| Jellyfin for Android TV | Tested. |
| Jellyfin for Roku | Untested. Sends a device id, so it should work. |
| Swiftfin (iOS, tvOS) | Untested. |
| Wholphin | Untested. Merges the public user list, so it should work. |

What carries over:

- PINs, including a profile with no PIN, which opens with an empty box.
- Device restrictions. A profile limited to particular devices is not offered on any other,
  and is refused if asked for.
- Library access, parental rating and tag filters, which Jellyfin enforces server-side.
- The profile's own name, rather than the `master_profile` account name.

What does not:

- Creating, editing or deleting profiles.
- Library artwork, which is applied in the browser.
- The switcher itself, including Bonfire sharing screens.

## Not supported

| Client | Why |
| --- | --- |
| Moonfin | Sends no `Authorization` header on the public user list, so the server cannot tell which household the device belongs to. PIN entry works if you type the full account name. |
| Infuse | No Jellyfin user-selection screen to add profiles to. |
| Findroid | Untested, and its sign-in flow has not been checked. |
| Kodi (JellyCon) | Untested. |
| Any client with a hardcoded user list | Nothing to inject into. |

An app not listed here is untested rather than known-broken. If it signs in through
Jellyfin's own user list and sends a `DeviceId`, selection-only support is likely to work.

## Parental controls hold everywhere

Library access, maximum parental rating and tag filters live on the Jellyfin account and
are enforced by the server. A profile sees only what it is allowed to see on every client,
including the ones listed as unsupported here.

## Reporting a client

Open an issue with the app name and version, whether profiles appeared in its sign-in
screen, and the server log lines beginning `ProfilesPlugin:` from the attempt. Those lines
say whether the request arrived, whether the device was recognised and what was returned.
