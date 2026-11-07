# Apps

Bonfire works in two ways, depending on the app. Apps that load the web client from your
server get the full switcher. Apps with their own sign-in screen can list your profiles
there instead.

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

**Signed in before installing Bonfire?** Sign out and back in once so the server
recognises the device. Approving a Quick Connect code counts.

These still apply: PINs, device limits, library access, parental ratings and tag filters.
The profile shows under its own name.

Not available: creating or editing profiles, library artwork, and the switcher itself.

## Apps with no user list

Streamyfin, Plezy and Tsukimi only offer a username and password box. They still work:

1. Sign in once as the main account, with its real name and password.
2. From then on, type a **profile's name** and its PIN.

This needs **Let profiles sign in with their PIN** under **Apps**. It
works the same way for any app not listed here.

## Profile picker (beta)

Only Moonfin for now. Tick it under **Apps → Profile picker**, then sign in on it
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
