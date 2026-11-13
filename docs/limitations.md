# Known Limitations

## Custom themes

Some themes and skin managers push the Bonfire button out of place. Switch to
**Jellyfin's user menu** under **Settings → Switcher Style**, which removes the button.
Please also open an issue naming the theme.

## Profiles are managed from the home screen

Create and edit profiles from the switcher, not the admin dashboard.
**Dashboard → Plugins → Bonfire** holds the server-wide settings, the avatar library,
PIN resets, TV and app sign-in, and the emergency disable code.

## Back on the PIN keypad

**Back** on the PIN keypad steps back one level at a time, not straight to the profile
list. This is by design.

## Wrong PINs are counted per profile

The limit on wrong PINs is per profile, across every address it is tried from. Ten wrong
PINs in 15 minutes lock the profile for everyone, wherever they are.

## Accounts with no password

An account with no password can't be added to a household by its members. An administrator
adds it.

## What other apps can't do

Each "no" in the [app table](clients.md#what-each-app-can-do), and why.

- **Announcements, themes, logo, first-time setup, linked accounts, auto-lock.** These are
  screens in the web client. An app that doesn't load it has nowhere to show them.
- **Ask for more time.** It needs a button on a screen Bonfire draws. A parent can still
  give time from Manage Profiles in any browser.
- **PIN reset by email.** It needs a form. Native sign-in screens and the Moonfin keypad
  have none, so an administrator resets the PIN instead.
- **Time-up warning.** The server sends it as a message. Swiftfin and Findroid ignore
  messages, and no handler was found in Roku or Plezy. Playback is still stopped.
- **Maintenance notice.** Same reason. Those apps show an error with the text instead.
- **Switching from the home screen.** An app with no switcher signs in again on its own
  screen. Moonfin has a **Switch profile** folder instead.
- **Moonfin picker (beta).** It has no ask-for-more-time tile, announcements, logo or
  linked accounts. It only draws profile tiles in a folder.

## Emergency disable code

Turns Bonfire off until Jellyfin restarts. It's for when Bonfire has made the web
interface hard to use, including the page you'd need to uninstall it.

Set one under **Dashboard → Plugins → Bonfire → Security**. To use it, enter it on any
Bonfire screen or press `Ctrl+Shift+B`. It's off until you set one.

Things to know:

- It does **not** unlock other profiles or loosen any parental controls.
- It **does** skip the profile screen. On a device signed in to a main account, anyone
  with the code gets that account.
- No password is asked for, so make it long. It allows five tries an hour per address,
  and every use is logged.
- If Bonfire's script doesn't load at all, the code can't run. Restart Jellyfin or delete
  the plugin folder instead.
