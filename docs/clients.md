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
| Jellyfin for Android TV | Supported, confirmed on hardware. |
| Jellyfin for Roku | Supported in 1.6.2.1-beta. Not yet confirmed on hardware. |
| Swiftfin (iOS, tvOS) | Supported in 1.6.2.1-beta. Not yet confirmed on hardware. |
| Findroid (phone and TV) | Supported in 1.6.2.1-beta. Not yet confirmed on hardware. |
| Wholphin | Supported in 1.6.2.1-beta. Not yet confirmed on hardware. |

**Turn off automatic sign in.** Every one of these apps can be set to sign straight into
the last account, which skips the screen the profiles are on.

- **Android TV** — Settings → Login → Automatic sign in → *Disable*
- **Roku** — turn off *Remember me* when you sign in
- The others stop at their own user list by default.

If you signed in on the device before installing this, sign out and in once. The device is
noted when somebody signs in on it, so a session that predates the plugin is not on the map
yet.

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
| Any client with a hardcoded user list | Nothing to inject into. |

Every other app is untested rather than known-broken. Two things decide it: whether the
app builds its sign-in screen from `GET /Users/Public`, and whether it sends a `DeviceId`
with that request. Both are needed.

## Apps with no user list

Streamyfin, Plezy and Tsukimi sign in with a typed username and password and never ask the
server who its users are, so there is no list to add profiles to. They still work:

1. Sign in once as the account that owns the profiles, with its real name and password.
2. After that, enter a profile by typing **the profile's name** and its PIN.

Step 1 is what tells the server which household the device belongs to. Needs
**Let profiles be opened with their PIN on any app** under **TVs & Apps**.

The same applies to any app not listed on this page.

## Parental controls hold everywhere

Library access, maximum parental rating and tag filters live on the Jellyfin account and
are enforced by the server. A profile sees only what it is allowed to see on every client,
including the ones listed as unsupported here.

## Reporting a client

Open an issue with the app name and version, whether profiles appeared in its sign-in
screen, and the server log lines beginning `ProfilesPlugin:` from the attempt. Those lines
say whether the request arrived, whether the device was recognised and what was returned.
