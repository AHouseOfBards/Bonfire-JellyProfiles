# Security

Some of what Bonfire does is enforced by the server and holds against anyone. Some of it
only works against people who aren't trying to get around it. This page says which is
which.

## Enforced by the server

These hold in every app and can't be turned off from a browser.

- **What a profile can see.** Library access, parental rating and tag filters live on
  the profile's Jellyfin account. So does every restriction an administrator put on the
  main account, such as remote access, schedules, bitrate, downloads and Live TV. A profile
  never gets more than its main account.
- **PINs.** Every switch and sign-in checks the PIN on the server.
  - The switcher allows 5 wrong PINs per 15 minutes from one address, and 10 per profile
    from all addresses together.
  - Other apps' sign-in screens allow 5 per 15 minutes and 20 a day, per account.
- **Other households.** In a shared Bonfire, opening someone else's account or profile
  needs its PIN. The exception is when its owner has turned on *Let my Bonfire switch
  into my account on this network* and you're on their network. Hidden profiles can't be
  opened at all.
- **Going back to the main account** from a profile asks for the main account's PIN,
  like any other switch, unless its owner turned off *Ask for the PIN to open this
  account*.
- **Managing the household.** Changing profiles, devices, sharing or switcher settings
  needs the main account's PIN, checked on the server, even when the account opened
  without it. An administrator can require every main account to have one.

## Not a security boundary

- **A main account with no PIN.** Any of its profiles can switch into it. If the profiles
  are for children, give the main account a PIN. The same goes for one with *Ask for the
  PIN to open this account* turned off: the PIN then guards changes, not the account.
- **The main account on other apps.** On a device your household has signed in on, its PIN
  works instead of its password, and a saved sign-in opens it with neither. Profiles on
  that device can reach it. Set the app to ask every time.
- **Device limits.** A device is identified by the ID its app sends, and an app can send
  any ID. A browser's ID is its user agent and first-use time, encoded, so it can be
  guessed. A device limit stops normal use on other devices, not someone who copies the
  allowed device's ID. The same goes for PIN-less profiles on other apps' sign-in screens,
  and for PINs typed in other apps, which only work on a device your household has signed
  in on with a password. A device ID copied from another device passes as that device;
  Jellyfin shows device IDs only to administrators.
- **"Your home network".** This is whatever Jellyfin counts as local. Behind a reverse
  proxy that isn't listed under **Dashboard → Networking → Known Proxies**, every visitor
  looks local.
- **The profile screen.** It's a screen, not a lock. Anyone at the browser can do what
  the signed-in profile can do.
- **What anyone can read.** Without signing in, anyone who can reach the server can read
  the Bonfire version, which server-wide features are on, which apps get the proxy picker,
  and the maintenance message, because an app needs them before it shows a sign-in screen.
  Nothing there belongs to a household.
- **Profile names in other apps.** With *Show profiles on the sign-in screen* on, a device your household has used
  shows your profile names on its sign-in screen.

## For administrators

- **Configuration export.** It contains every PIN, hashed. A four-digit PIN can be
  recovered from its hash in seconds, so keep the file safe like a password. It leaves
  out the mail server password and the emergency code, and an import keeps the current
  ones.
- **Emergency disable code.** It skips the profile screen on any browser signed in to a
  main account. Make it long.
- **Device and app names** on the Bonfire settings page can be set by any account.
  The page shows them as plain text. If you build tools on the API, do the same.
