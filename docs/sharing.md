# Bonfire Sharing

Link two accounts with a 6-character code so they share one switcher screen.

> [!TIP]
> A Bonfire code is a credential. Anyone who has it can join, and you can only be in one
> Bonfire at a time — joining a new one removes you from your current one. Removing a
> member gives your Bonfire a new code, so they cannot rejoin with the old one; **New code**
> does the same without removing anybody.

## The rules that protect it

A link runs both ways: everyone in a Bonfire can see everyone else's profiles. Switching
into any of them gives a real session for it, so:

- **Nothing with no PIN can be opened from another household.** That covers your main
  account and your profiles alike. Set a PIN on each one you want other members to be
  able to enter.
- **The LAN bypass never applies across accounts.** Being on the same network as someone
  in your Bonfire does not skip their PIN; it only skips your own.
- **Hidden means hidden.** *Hide my profiles from others*, and *hide others' profiles
  from me*, also stop those profiles being opened, not only being shown.

## Sharing a TV with another adult

Typing a PIN with a TV remote every time two adults swap accounts is miserable, so each
account can lift both rules **for itself**. In **Settings → Your Bonfire** on the switcher
screen, tick *"Let my Bonfire switch into my account on this network"*.

People in your Bonfire can then enter your account and your profiles from your home
network without a PIN, including ones that have none. Away from home nothing changes. It is off by default,
only you can turn it on for your own account, and every switch that uses it is logged.

> [!WARNING]
> Two things to check first. If your account is a **Jellyfin administrator**, anyone who
> switches into it can change server settings and manage every user on it — only enable
> this if you would hand them the password. And "your home network" is whatever your
> *server* counts as local: if it sits behind a reverse proxy that is not listed under
> **Dashboard → Networking → Known Proxies**, every visitor looks local, and this setting
> would apply to all of them.
