# Apps

Bonfire works in three ways, depending on the app. Apps that load the web client from your
server get the full switcher. Apps with their own sign-in screen can list your profiles
there instead. Moonfin gets a profile picker (beta) on its home screen.

## What each app can do

Apps are checked at the version in brackets. **Partial** is explained under the table.

| App | Switch profiles | PIN | Main PIN | Kid restrictions | Viewing limits and time-up warning | Ask for more time | Announcements | Maintenance message | First-time setup | PIN reset by email | Linked accounts | Themes | Logo | Auto-lock |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Web, Android, iOS, Media Player, webOS, Tizen | yes | yes | yes | yes | yes | yes | yes | yes | yes | yes | yes | yes | yes | yes |
| Android TV (0.19.10) | partial | partial | partial | yes | partial | no | no | partial | no | no | no | no | no | no |
| Roku (3.2.4) | partial | partial | partial | yes | partial | no | no | partial | no | no | no | no | no | no |
| Swiftfin (1.6.1) | partial | partial | partial | yes | partial | no | no | partial | no | no | no | no | no | no |
| Findroid (1.2.0) | partial | partial | partial | yes | partial | no | no | partial | no | no | no | no | no | no |
| Wholphin (1.1.1) | partial | partial | partial | yes | partial | no | no | partial | no | no | no | no | no | no |
| Streamyfin, Plezy, Tsukimi (name and PIN) | partial | partial | partial | yes | partial | no | no | partial | no | no | no | no | no | no |
| Moonfin picker (beta) | yes | yes | partial | yes | partial | no | no | partial | no | no | no | no | no | no |

The native sign-in list is the row for each app that uses it: Android TV, Roku, Swiftfin, Findroid and Wholphin.

**Partial**, on apps with their own sign-in:

- **Switch profiles:** you pick a profile on the app's sign-in screen, not from the home screen.
- **PIN:** typed where the app asks for a password, and only on a device your household has signed in on.
- **Main PIN:** on a known device it works instead of the account's password. A saved sign-in never asks for it.
- **Viewing limits:** the server always enforces them. The warning shows only in an app that shows server messages.
- **Maintenance message:** shown as a message in an app that shows them, otherwise as the app's error text.

On Moonfin, **Main PIN** is partial because the main account's owner can turn off the keypad on its folder.

Which apps show server messages:

| App | Shows them |
| --- | --- |
| Android TV | Yes, as a toast. |
| Wholphin | Yes, as a toast. |
| Streamyfin | Yes, as an alert. |
| Moonfin | Yes. |
| Swiftfin | No. |
| Findroid | No. |
| Roku, Plezy | No handler found. |
| Tsukimi | Not checked. |

*Jellyfin for iOS* is the Jellyfin app that wraps the web client. *Swiftfin* is a separate app.

## Full support

The switcher, profile management, avatars, library artwork and shared Bonfires.

| App | Notes |
| --- | --- |
| Jellyfin Web | Desktop and mobile browsers. |
| Jellyfin for Android | |
| Jellyfin for iOS | iPhone and iPad. |
| Jellyfin Media Player | Windows, macOS, Linux. |
| LG webOS | webOS 4.5 (2019 sets) and later. |
| Samsung Tizen | Needs the Bonfire loader added once. See [Samsung Tizen](troubleshooting.md#samsung-tizen). |

## Profile sign-in

Your profiles appear on the app's own sign-in screen and open with their PIN. Creating
and editing profiles still needs one of the apps above.

Turn it on under **Dashboard → Bonfire → Apps**.

| App | Status |
| --- | --- |
| Jellyfin for Android TV | Confirmed on hardware. |
| Jellyfin for Roku | Not yet confirmed on hardware. |
| Swiftfin (iOS, tvOS) | Not yet confirmed on hardware. |
| Findroid (phone and TV) | Not yet confirmed on hardware. |
| Wholphin | Not yet confirmed on hardware. |

**Make the app ask every time.** These apps save a sign-in, and a saved profile opens
without its PIN unless the app is set to ask:

- **Android TV:** Settings → Login → *Always ask for credentials*.
- **Roku:** Settings → Global → turn off *Remember Me?*. Bonfire then makes the app ask
  for the PIN each time the user list opens.
- **Wholphin:** on each profile with a PIN, Settings → *Profile protection* →
  *Require login for profile*.
- **Swiftfin** and **Findroid** can't be set to ask. A profile signed in on them opens
  without its PIN. Swiftfin can lock a profile with its own PIN under Settings → the
  profile → *Security*.

**Type the PIN where the app asks for a password.** A main account with no PIN still uses
its real password. A profile with no PIN opens with the box left empty.

**The main account is the weak point.** On a known device its PIN works instead of its
password, and a saved sign-in opens it with neither. Anyone who can pick it on the sign-in
screen gets it, so set the app to ask every time.

**Forgotten PIN.** Reset by email works only in the web app. Otherwise an administrator
clears it under **Dashboard → Bonfire → Households**.

The list appears on every app that asks the server for its user list, not only the apps named here.

**Signed in before installing Bonfire?** Sign out and back in once so the server
recognizes the device. Approving a Quick Connect code counts.

These still apply: PINs, device limits, library access, parental ratings and tag filters.
The profile shows under its own name.

Not available: creating or editing profiles, library artwork, and the switcher itself.
See the table above for the rest.

## Apps with no user list

Streamyfin, Plezy and Tsukimi only offer a username and password box. They still work:

1. Sign in once as the main account, with its real name and password.
2. From then on, type a **profile's name** and its PIN.

This needs **Let profiles sign in with their PIN** under **Apps**. It
works the same way for any app not listed here.

## Profile picker (beta)

Only Moonfin for now. It's beta, off by default, and may change when native support
arrives. Tick it under **Apps → Profile picker**, then sign in on it
with the main account. The home screen shows a **Profiles** library:

1. Open a profile's folder to use that profile. A profile with a PIN opens a keypad. So
   does the main account's own folder, unless its owner turned off **Ask for the PIN to
   open this account**.
2. Open **Switch profile** on the home screen to go back.

Profiles can't sign in directly on a ticked app.

## What only the web app shows

Viewing-limit warnings, **Ask for more time**, maintenance notices, announcements,
first-time setup, PIN reset and themes reach only the web app and the apps that wrap it
(the **Full support** list above). Swiftfin and Roku don't show server messages, so
there playback stops with no warning. The refusal text is sent anyway, and an app that
shows errors will display it.

During maintenance an app that shows no server messages gets a plain "Try again later"
error carrying the administrator's text, not a notice.

## Not supported

| App | Why |
| --- | --- |
| Infuse | Has no Jellyfin user list. |
| Apps with a fixed user list | Nothing for Bonfire to add to. |

Any other app is untested, not known to be broken.

Parental controls are enforced by the server, so they hold in every app, including the
ones on this list.

## Reporting an app

Open an issue with the app name and version, whether your profiles appeared, and the
server log lines starting with `ProfilesPlugin:` from that attempt.
