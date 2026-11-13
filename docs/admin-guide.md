# Admin guide

The admin page is in **Dashboard → Plugins → Bonfire**. It has ten tabs. Most have a **Save** button at the bottom. Settings under **Advanced** are closed until you open them.

## Status

Shows whether Bonfire is working and walks through first setup.

- **Turn on Bonfire.** Saves as soon as you change it. Off on a new install.
- **Show first-time setup?** Asked once on a new install. **Turn it on** or **Leave it off**.
- **Getting started.** A checklist that ticks itself off and disappears when everything is done.

## Households

Lists each main account with its profiles.

- **Profiles per main account.** 1 to 20, default 5. A main account's own limit wins.
- **Leftover records.** **Scan** finds records left when a profile is deleted in **Dashboard → Users**. They still count against the main account's limit.
- **Accounts with no password.** An account with no password, such as an SSO account, can't be added to a household by the household. An administrator adds it here.
- **Forgotten PIN.** Clear it here. The account can then set a new one.

## Sessions

Everyone signed in right now, with their device, when it was last seen and what is playing. A profile shows which main account it belongs to. **Sign out** ends that one session. **Refresh** reloads the list.

## Security

- **Require a main PIN.** Main accounts can't change profiles or settings until they set one. Off by default.
- **Let main accounts reset a forgotten PIN by email.** Off by default. Needs [Email](#setup) set up.
- **Default time zone.** For viewing limits. Default: the server's own. Households can set their own.

Under **Advanced**:

- **Emergency disable code.** At least 10 characters. Anyone with the code can turn Bonfire off until Jellyfin restarts, so signed-in devices skip the profile screen. Blank turns it off. **Save code** saves it on its own, not with **Save**.
- **PIN tries a day.** 1 to 20, default 20. Per account, all apps together.
- **Addresses that skip PINs.** Add an address, then choose which accounts skip the PIN from it.

> [!WARNING]
> The emergency disable code can't be shown again, so write it down.

More in [Security](security.md).

## Theme

- **Default theme.** What Bonfire's screens use. Default: Bonfire. There are twelve built in.
- **Let profiles choose their own theme.** On by default.
- **Richer theme animation** (Advanced). Off by default. Never used with reduced motion or on low-power TVs.
- **Server logo.** **Choose an image** (PNG, JPEG or WebP, up to 1 MB) or **Remove image**. With no image, the **Logo text** shows (up to 40 characters). Also **Logo corners** (Rounded or Square, default Rounded) and **Alt text** for screen readers (up to 100 characters). **Save text** saves the text fields.
- **Themes.** The built-in themes can be hidden. **Your themes** lists custom ones. **New theme** opens the editor, and **Import** loads a theme file. Hiding and fonts save at once.
- **Fonts.** Upload a WOFF2 file, up to 2 MB, with an optional **Font name** and a **Fallback** (Sans-serif or Serif). Tick **I have the right to use this font** first.

The editor and the theme file format are in [Themes](themes.md).

## Setup

- **First-time setup.** **Show first-time setup** is off by default and doesn't affect existing main accounts. **Show again to everyone** makes every main account see it on its next visit.
- **Switcher.** Defaults until a main account chooses. **Ask "Who's watching?" on startup** is on by default. **Switch button** is **In the header** (default) or **In Jellyfin's user menu**.
- **Avatar library.** The only way to set an avatar on a TV. **Upload avatars** or **Upload a folder** (JPEG, PNG, WebP or GIF). **Only allow avatars from this library** stops profiles uploading their own. Off by default.
- **Email.** For PIN reset codes and notices. Enter the **Mail server**, **Port** (default 587), **Security** (STARTTLS by default, or None for a relay on your network), **Username**, **Password** and **From address**. Port 465 isn't supported. **Save and send test** checks it. Email works once a test goes through. **Addresses** lists the accounts' addresses.

Under **Advanced**:

- **Include the starter avatars.** 30 built-in avatars. On by default.
- **Avatar quality.** Low, Medium, High or Maximum, which set the largest size and file. Default: High (2048 px, up to 8 MB). More disk. Applies to new avatars.
- **Copy from a server folder.** A path on the server, not on your computer.

## Clients

For apps without a switcher, such as Android TV, Roku and Swiftfin. Set each app to ask for sign-in every time, or a saved profile opens without its PIN. What each app can do is in [Apps](clients.md).

- **Let profiles sign in with their PIN.** Off by default. The PIN is typed as the password.
- **Show profiles on the sign-in screen.** Off by default. Only on devices the household has signed in on.
- **Open the password field instead of Quick Connect.** Off by default. Android TV only, after a household's first sign-in.
- **Open PIN-less and device-limited profiles from anywhere** (Advanced). Off by default.
- **Profile picker (beta).** Sign in with the main account, then pick a profile on the client's home screen. Turn it on per client (Moonfin). Profiles can't sign in directly on Moonfin while it is on.

## Notices

- **Announcements.** Shown on the profile screen, and in apps that support Bonfire. Set the **Message** and **Importance** (Info, Warning or Critical). Info can be dismissed. Warning and Critical stay until they end. Under **Advanced**, **Starts** (blank is now) and **Ends** (blank until you delete it). **Post announcement** publishes it. Posted announcements are listed below.
- **Maintenance.** Only administrators can use the server while it's closed. Set a **Message** and **Back by** time. **Reopen automatically at this time** and **Stop what people are watching** are optional. **Close the server** and **Reopen** do the rest.

## Switches

The last 1,000 profile switches, kept for 90 days. Filter by **Main account or profile** and by **From** and **To** dates. **Download JSON** includes IP addresses. **Delete the log** clears it. Other events are in **Dashboard → Activity**.

> [!WARNING]
> **Delete the log** can't be undone, and the JSON download includes IP addresses.


## Troubleshoot

- **Common problems.** Short answers for a missing switcher, apps that don't show it, library access that reverts, a forgotten PIN, and being locked out.
- **Report a problem.** **Copy report** copies versions, settings and counts to paste into a GitHub issue. It has no names, ids, addresses or PINs.
- **Client script** and **Backup** (Advanced). The script check shows whether the client script loads. **Export** and **Import** back up the settings, without the mail password and emergency code. **Include PINs** is on by default.

> [!WARNING]
> The backup file holds every PIN. Keep it private.


Longer answers are in [Troubleshooting](troubleshooting.md). Scripts that call the server are in the [Developer API](developer-api.md).
