# Sharing a Bonfire

Link two accounts with a 6-character code so both households appear on one switcher.
Find it under **Settings → Sharing** on the switcher.

> [!WARNING]
> **Treat the code like a password.** Anyone with it can join.

You can be in one Bonfire at
a time, so joining another one takes you out of your current one. Removing a member
changes your code so they can't rejoin. **New code** changes it without removing anyone.

## How it's protected

Everyone in a Bonfire sees everyone else's profiles, and opening one signs you in as
that profile. So by default:

- **Anything without a PIN can't be opened by another household.** That includes main
  accounts. Set a PIN on whatever you want others to be able to open.
- **Being on the same network doesn't skip someone else's PIN.** The home network bypass
  only covers your own account.
- **Hidden profiles can't be opened.** *Hide my profiles from others* and *Hide other
  people's profiles from me* block opening as well as showing.

## Sharing a TV with another adult

Typing a PIN with a remote every time gets old. Each account can turn on
*Let Bonfire members open my account on the server's network* for itself.

With it on, people in your Bonfire can open your account and profiles on your home
network without a PIN, even ones that have none. Away from home nothing changes. Only you
can turn it on for your account, and every switch that uses it is logged.

Check two things first:

- **Is your account a Jellyfin administrator?** Anyone who switches into it can change
  server settings and manage every user. Only turn this on if you'd give them the
  password.
- **Is your server behind a reverse proxy?** If it isn't listed under
  **Dashboard → Networking → Known Proxies**, every visitor looks local and this setting
  applies to all of them.
