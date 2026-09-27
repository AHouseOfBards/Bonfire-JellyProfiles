# What Bonfire protects, and what it does not

Profiles are a convenience for a household and a control for its children. Some of what
Bonfire does is enforced by the server and holds against anyone; some of it only works
against people who are not trying to get round it. This page says which is which.

## Enforced by the server

These hold on every client, including apps Bonfire never runs in, and cannot be switched
off from a browser.

- **What a profile can see.** Library access, the parental rating and tag filters are set
  on the profile's Jellyfin account. So is everything an administrator restricted on the
  account that owns it — remote access, access schedules, bitrate, downloads, Live TV. A
  profile never gets more than its account.
- **PINs.** Every switch and every sign-in checks the PIN on the server.
  - The web switcher allows 5 wrong PINs per 15 minutes from one address, and 10 per
    profile from all addresses together.
  - Other apps' sign-in screens allow 5 per 15 minutes and 20 a day, per account.
- **Other households.** Across a shared Bonfire, entering an account or a profile needs its
  PIN, or its owner's *Allow household switching on this network* while on the local
  network. Hidden profiles cannot be opened, only hidden.
- **The browser holds one session.** It is the session of whoever is signed in now. Going
  from a profile to the account that owns it asks for that account's PIN, like any other
  switch.

## Not a boundary

- **A master account with no PIN.** Any of its profiles can switch into it. If the profiles
  are for children, set a PIN on the master.
- **Device restrictions.** A device is recognised by the id its app sends, and an app can
  send any id. Limiting a profile to the living-room TV keeps it off other devices in
  normal use, not from someone who copies that TV's id. The same goes for profiles with no
  PIN on other apps' sign-in screens, which open only on a device the household has used.
- **"Your home network".** It is whatever the Jellyfin server counts as local. Behind a
  reverse proxy that is not listed under **Dashboard → Networking → Known Proxies**, every
  visitor looks local, and every LAN bypass applies to all of them.
- **The profile screen itself.** It is a screen, not a lock. Anyone at a browser can do
  whatever the profile signed in there can do.
- **Profile names on other apps.** With *TVs & Apps* on, a device the household has used
  is shown the household's profile names on its sign-in screen.

## For administrators

- **Settings export.** It contains every PIN, hashed. A four-digit PIN can be recovered
  from its hash offline in seconds, so keep the file as you would keep passwords.
- **Emergency disable code.** It skips the profile screen on any browser signed in to a
  master account. Make it long.
- **The Bonfire settings page** shows device and app names that any account can set. The
  page displays them as text; if you build tools on the API, do the same.
