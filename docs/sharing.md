# Bonfire Sharing

Link two accounts with a 6-character code so they share one switcher screen.

> [!TIP]
> A Bonfire code is a credential. Anyone who has it can join, and you can only be in one
> Bonfire at a time — joining a new one removes you from your current one.

## The two rules that protect it

Switching into an account gives a real, fully privileged session for it, so:

- **An account with no PIN cannot be opened from a shared Bonfire.** If you want other
  members to be able to switch into your main account, set a profile PIN on it first.
  Sub-profiles are unaffected — they work with or without a PIN.
- **The LAN bypass never applies across accounts.** Being on the same network as someone
  in your Bonfire does not skip their PIN; it only skips your own.

## Sharing a TV with another adult

Typing a PIN with a TV remote every time two adults swap accounts is miserable, so each
account can lift both rules **for itself**. In **Settings → Your Bonfire** on the switcher
screen, tick *"Let my Bonfire switch into my account on this network"*.

People in your Bonfire can then enter your account from your home network without your
PIN, including when you have none. Away from home nothing changes. It is off by default,
only you can turn it on for your own account, and every switch that uses it is logged.

> [!WARNING]
> Two things to check first. If your account is a **Jellyfin administrator**, anyone who
> switches into it can change server settings and manage every user on it — only enable
> this if you would hand them the password. And "your home network" is whatever your
> *server* counts as local: if it sits behind a reverse proxy that is not listed under
> **Dashboard → Networking → Known Proxies**, every visitor looks local, and this setting
> would apply to all of them.
