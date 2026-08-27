# Changelog

Assembled from the `changelog` field of each entry in `manifest.json`, which is
what Jellyfin shows in the plugin update dialog and what the GitHub release body
uses. That field is the source of truth; this file is the readable index of it.

Versions ending in a pre-release label were published to the beta channel only.
See [BETA-CHANNEL.md](BETA-CHANNEL.md).

---

## 1.5.4  — 2026-08-27

**Beta release** — please report issues on GitHub.

**Update from 1.5.2 or 1.5.3.** The switcher did not run at all on those two builds. 1.5.0 and 1.5.1 are unaffected.

**Fixed**

- The profile screen appeared unstyled, as coloured bars stretched across the page. (#23, #16)
- A TV remote could not select anything on the profile screen. (#16)
- Switcher settings, library artwork and the Ctrl+Shift+B emergency shortcut all stopped working.
- One failed step during startup no longer stops the rest from running.

**Changed**

- The developer API link no longer promises platform notes the document does not contain.

## 1.5.3  — 2026-08-27

**Beta release** — please report issues on GitHub.

**Bonfire speaks French.** Translation support, contributed by [BryanSmee](https://github.com/BryanSmee), with a complete French translation.

**Added**

- Everything a household member sees — the profile gate, the switcher, the profile forms, PIN prompts and their errors — is translated. Your browser's language is used; no setting to change.
- English is built in, so nothing is downloaded and nothing changes for English readers.
- Adding a language is one JSON file in `Web/i18n`. See `Web/i18n/README.md`.

**Fixed**

- A cut-out profile picture was hard to see as the selected one on a TV: the outline had become thinner than the one it replaced, and the pencil in Manage Profiles could disappear against a pale picture.
- A long profile name or PIN badge could spill outside its card.

**Documentation**

- The developer API reference now documents every endpoint, lists the seven that are deliberately reachable without a token, and corrects two statements that were no longer true.

## 1.5.2  — 2026-08-27

**Beta release** — please report issues on GitHub.

Follow-ups to 1.5.1.

**Fixed**

- After updating the plugin, the settings page said it could not modify `index.html` and asked for file permissions — on servers set to serve the script live, where that file is deliberately left alone. A newly installed or updated build cannot attach to Jellyfin until the server restarts, so that is what it says now. On Docker that means restarting the container: Jellyfin's own Restart button does not restart the process. (#25)
- A cut-out profile picture was highlighted twice at once — a square ring around a round picture, with the outline that follows the picture inside it. Every state that draws a ring now knows about cut-outs. (#23)
- Manage Profiles laid a dark square panel over cut-out pictures. (#23)
- Switching back to the account owner left the top-bar avatar showing the profile you had just left. (#23)

**Changed**

- **Transparent background** is now **No background**, and sits directly under the profile name next to the colour it controls, rather than below the picture. (#23)

## 1.5.1  — 2026-08-26

**Beta release** — please report issues on GitHub.

Three reports from the 1.5.0 release.

**Fixed**

- Switching profiles from the user menu left you on the settings page as the new profile. It now takes you to the home screen. (#22)
- On Jellyfin 10.11, Bonfire treated every page as the home screen. The route moved out of the address bar's `#` fragment and into the path, and Bonfire was still reading the fragment.

**Changed**

- The label under the profile you are signed in as read **Watching now**, which looked like somebody else was watching something. It now reads **Signed in**. (#24)
- Profile pictures that are cut out rather than rectangular no longer get the avatar colour behind them, so the corners stay clear and the outline follows the picture. Set automatically when you choose a picture, with a **Transparent background** switch to correct it either way. (#23)
- The avatar colour said it was unused once a picture was set. It is painted behind the picture, and now says so.

## 1.5.0  — 2026-08-25

**New**

- Bonfire no longer edits Jellyfin's `index.html`. The client script is added to the page as it is served, so there is nothing to re-apply after a Jellyfin update and no file permissions to grant. Patching the file stays available as a legacy option under **Advanced → Client script**. (#17, #11, #3)
- The switcher follows your theme's accent colour instead of always drawing Jellyfin blue.
- Profile screens use the whole window on a desktop instead of one phone-width column.
- The picker shows which profile you are currently watching as.

**Fixed**

- On Android, picking a profile could bring the picker straight back, over and over, and a PIN-protected profile could drop you at the account login.
- Picking a profile showed nothing at all for the second or two it takes, so the tap read as missed. The profile you picked now shows a spinner, and the screen no longer goes black while it loads.
- Opening a profile took several seconds and could drop you back on a screen you had already left.
- Bonfire rewrote `index.html` on startup even when set never to touch it.
- The settings page reported the client script as installed whenever the plugin was loaded at all, including on servers where nothing was reaching the page.
- Add Profile was missing from the Manage Profiles screen.
- Library names were cut off to "3D Movi…" when choosing artwork.
- **Upload avatars** and **Upload a folder** were unstyled text instead of buttons.
- "PIN Protected" showed in red and "No PIN" in green — the wrong way round.
- The audit log ran the time into the address as "12:09:28 PMIP".

**Changed**

- The plugin is called **Bonfire** in the Jellyfin sidebar.
- Plugin settings are split into tabs — General, Avatars, Accounts, Activity and Advanced — instead of one page five screens tall.
- Switcher Style and Your Bonfire move out of the profile grid into their own **Settings** screen. Bonfire Grouping is now called Your Bonfire.
- Add Profile appears on Manage Profiles only. The "Who's watching?" screen is people to pick from.
- Setting a profile picture is one panel rather than six controls. The paste-a-URL box is gone: a linked picture could not be cropped, was never stored on your server, and disappeared without warning when the link died.
- Libraries and their artwork are one list, with the artwork controls behind a switch.
- Delete is no longer as prominent as Save.

## 1.4.9  — 2026-08-25

**Beta release** — please report issues on GitHub.

The last beta before 1.5.

**Changed**

- The plugin is called **Bonfire** in the Jellyfin sidebar, instead of Profiles.
- **Client script** moved from General to **Advanced** on the settings page. A working server never needs it changed.
- Editing `index.html` is now labelled a legacy option. Serving the script live is the default and needs no file permissions.

**Documentation**

- The README described the old file-patching as the only method, and told anyone with a problem to grant write access to a file that no longer needs it.
- The developer API reference now documents how the script is delivered and the `mechanism` field reporting it.

## 1.4.8  — 2026-08-25

**Beta release** — please report issues on GitHub.

Five faults found in a review before the next stable release.

**Fixed**

- On Android, the 1.4.6 fix for the profile picker coming back over and over had never actually run — the script that hides the page during a switch was clearing the marker the fix needed before the fix could read it. Switching should now take one reload rather than several.
- Bonfire rewrote Jellyfin's `index.html` once on startup even when set never to touch it, stripping blank lines out of a file it had not patched.
- The settings page reported **Client script is installed and up to date** whenever the plugin was loaded at all, including on a server where nothing was reaching the page.
- A one-off problem stayed on the settings page as the last problem until Jellyfin was restarted.
- Returning to the profile grid could draw it over a screen you had already moved on to.

## 1.4.7  — 2026-08-25

**Beta release** — please report issues on GitHub.

**Fixed**

- Picking a profile showed nothing at all for the second or two it takes, so on a phone the tap read as missed. The profile you picked now shows a spinner and the rest step back, and the screen no longer goes black while it loads.
- The loading spinner on **Your Bonfire** never actually turned.

## 1.4.6  — 2026-08-25

**Beta release** — please report issues on GitHub.

**Fixed**

- On Android, picking a profile brought back the profile picker, over and over. The marker saying a profile is active did not survive the reload that finishes a switch, so Bonfire kept deciding the app had been closed. It no longer depends on which device you are on.
- Entering a PIN-protected profile could drop you back to the account login. Same cause — please report it if it happens again.
- After switching profiles, later page refreshes could sit faded out for a moment before appearing.

**Changed**

- If reverting to the main account ever fails repeatedly, Bonfire now keeps the profile you picked instead of reloading again. There is no longer a way to get stuck on a screen that reloads forever.

## 1.4.5  — 2026-08-24

**Beta release** — please report issues on GitHub.

**Fixed**

- Serving the client script live stopped other plugins from changing `index.html`. **File Transformation**, **Home Screen Sections** and **Plugin Pages** had no effect while it was switched on. Bonfire now adds its script to whatever those plugins produce, instead of answering the request itself.
- `index.html` was served carrying the file's own cache validators, which could let a browser keep a page pointing at an old copy of the client script after a plugin update.

## 1.4.4  — 2026-08-24

**Beta release** — please report issues on GitHub.

**Fixed**

- The plugin settings page was ignoring its own stylesheet. The new tabs came out as plain browser buttons, and the page never followed your theme's accent colour — since 1.4.2.
- The panel for choosing a profile picture hung out past the edge of the card it belongs to.
- Every library still showed an empty grey artwork slot with artwork switched off.
- **Change picture** gave no sign it opens anything. It now has a chevron and stays held while the panel is open.

**Changed**

- The first tab on the settings page is called General. Calling it Settings inside the settings page said nothing.

## 1.4.3  — 2026-08-23

**Beta release** — please report issues on GitHub.

**Changed**

- The plugin settings page is split into tabs — Settings, Avatars, Accounts, Activity and Advanced — instead of one page five screens tall.
- Add Profile appears on Manage Profiles only. The "Who's watching?" screen is people to pick from.
- Setting a profile picture opens one panel, with your server's avatars and your own uploads labelled separately. Remove sits next to Change picture.
- The library list is libraries again. Artwork is behind a switch, already on for any profile using it.

**Removed**

- The "paste image URL" box. A linked picture could not be cropped, was never stored on your server, and disappeared without warning when the link died.

## 1.4.2  — 2026-08-23

**Beta release** — please report issues on GitHub.

**New**

- Bonfire no longer edits Jellyfin's index.html by default. The script is added to the page as it is served, so there is nothing to re-apply after a Jellyfin update and no file permissions to grant. Patching the file is still available under **Settings → Client script**. (#17, #11, #3)
- The switcher follows your theme's accent colour instead of always drawing Jellyfin blue.
- Profile screens use the whole window on a desktop instead of a single phone-width column.
- The gate shows which profile you are currently watching as.

**Fixed**

- Opening a profile took several seconds and could drop you back on a screen you had already left. Forms now open on cached data and a slow one can no longer overwrite a newer screen.
- Add Profile was missing from the Manage Profiles screen.
- Library names were cut off to "3D Movi…" when choosing artwork.
- The audit log ran the time into the address as "12:09:28 PMIP".
- **Upload avatars** and **Upload a folder** were unstyled text instead of buttons.
- "PIN Protected" showed in red and "No PIN" in green — the wrong way round.

**Changed**

- Switcher Style and Your Bonfire move out of the profile grid into their own **Settings** screen. Bonfire Grouping is now called Your Bonfire.
- Libraries and their artwork are one list instead of two lists of the same libraries.
- Delete is no longer as prominent as Save.
- Choosing a profile picture is one button rather than six controls.

## 1.4.1  — 2026-08-22

**Beta release** — please report issues on GitHub.

**New**

- Bonfire can now add its script to the page as Jellyfin serves it, instead of editing index.html on disk. Nothing to re-apply after a Jellyfin update, and no file permissions to grant. (#17, #11, #3)
- **Settings → Client script** picks the method. The default keeps patching the file and only serves it live if that write fails, so upgrading changes nothing on a working server.

**Changed**

- Choosing "never touch index.html" also removes the tags Bonfire added earlier, restoring the file.
- The settings page says whether live injection is actually running.

## 1.4.0  — 2026-08-21

**New**

- Library artwork can be set per profile. Jellyfin builds a library tile from the items inside it, so a Kids profile could see a poster from a film it cannot open; each profile can now be given its own picture for a library, or none at all. (#19)
- Administrators can set the switcher defaults for the whole server, under Dashboard → Plugins → Bonfire. Anyone who has already chosen keeps their own setting. (#14)
- Avatars can be uploaded several at a time, or a whole folder at once. Administrators with a prepared set already on the server can point at its folder and import it. (#14)
- The profile picker has a Cancel button when you opened it yourself, so you can go back to what you were watching.

**Fixed**

- Switching a profile failed with "Invalid username or password" on Jellyfin 10.11.11, and signed the master account out. (#15)
- On Samsung Tizen, picking a profile changed the avatar but left you in the original account. (#15, #16)
- On a TV, the remote could steer the Jellyfin page hidden behind the Bonfire screen. (#16)
- The TV Back button raised Samsung’s "exit application?" prompt behind the Bonfire screen. Back now steps back one screen instead. (#15)
- "Switch Profile" now appears on the Settings page, not only in the header dropdown. (#14)
- Profile pictures could be zoomed but not dragged.
- Save and Cancel are scrolled into view when a remote reaches them, so long profile forms no longer look frozen. (#15)
- Pictures now load in clients that bundle their own web client, such as Samsung Tizen.
- A missing script tag in index.html now says which of the three causes it is. (#17)

**Changed**

- The emergency disable link appears only once an administrator has set a code.
- Error messages, hints and warnings cut down throughout.

## 1.3.3  — 2026-08-09

**Beta release** — please report issues on GitHub.

**Fixed**

- Switching a profile failed with "Invalid username or password" on Jellyfin 10.11.11, and signed the master account out. (#15)
- On a TV, the remote could steer the Jellyfin page hidden behind the Bonfire screen. (#16)
- "Switch Profile" now appears on the Settings page, not only in the header dropdown.
- Profile pictures could be zoomed but not dragged.
- Pictures now load in clients that bundle their own web client, such as Samsung Tizen.
- A missing script tag in index.html now says which of the three causes it is. (#17)

**Changed**

- The emergency disable link appears only once an administrator has set a code.
- Error messages, hints and warnings cut down throughout.

## 1.3.2  — 2026-08-08

⚠️ BETA RELEASE — not a finished release; please report issues on GitHub. ▸ FIX — "Switch Profile" never actually appeared in Jellyfin's user menu in 1.3.1. The code looked for the menu component older Jellyfin builds used; 10.11 rewrote it in React, so the entry was silently never added and the profile page was the only way in. It now attaches to the real menu, directly above Sign out, and is found by its Sign out row rather than by English text — so it lands in the right place on a translated interface too. (Issue #14.) ▸ NEW — Switcher Style is now two separate settings instead of one. "Ask Who's watching? on startup" and "Where to switch from" can be combined freely, which makes the arrangement people actually asked for reachable: keep the startup screen, but reach the switcher from Jellyfin's own menu rather than a second icon beside the native one. Existing choices carry over — the old "Profile gate" and "Jellyfin menu" modes map onto the new pair. (Issue #14.) ▸ NEW — Avatar library. Administrators can upload a set of profile pictures under Dashboard → Plugins → Bonfire that anyone on the server can pick from, with an option to require them for a consistent household look. This is the only practical way to set a profile picture on a TV, which has no file browser to upload from. (Issue #14.) ▸ NEW — Pictures can be positioned and zoomed before saving, by dragging, pinching, or with the arrow keys and the zoom slider on a remote. Stored images went from 96×96 to 512×512, so photos are no longer soft on a large screen, and a small copy is generated alongside them so a screen full of avatars does not have to decode the full-size versions. ▸ NEW — Emergency disable code. If Bonfire breaks badly it can make Jellyfin's interface hard to use, including the settings page needed to remove it. Administrators can set a code that shuts the plugin off until the server restarts, entered on any Bonfire screen or with Ctrl+Shift+B. Off by default. It does not unlock other profiles and does not widen library access or parental ratings — those are enforced by Jellyfin itself — but it does skip the profile gate, so the settings page explains exactly what it costs before you turn it on. ▸ FIX — Unreadable image formats now say why. iPhone photos (HEIC) cannot be decoded by most browsers and previously failed in silence; the upload now explains that the photo needs exporting as JPEG first. SVG files are refused deliberately. ▸ FIX — A profile picture whose file has gone missing falls back to the initial and colour instead of a broken-image icon, and the settings page lists which profiles are affected. ▸ FIX — The profile link in the navigation sidebar did nothing when clicked.

## 1.3.1  — 2026-08-06

⚠️ BETA RELEASE — not a finished release; please report issues on GitHub. ▸ NEW — Choose how you reach the switcher. Until now every account got the same full-screen "Who's Watching?" gate on the home screen. That suits a shared TV and gets in the way everywhere else, so it is now a choice, made under Manage Profiles → Switcher Style. "Profile gate" is the existing behaviour and remains the default. "Jellyfin menu" goes straight to the home screen and instead adds a Switch Profile entry to Jellyfin's own user menu, plus a Bonfire section on your user profile page; the floating switcher button disappears with it. The choice belongs to each account rather than to whoever runs the server, and follows you to every device you sign in on. Sub-profiles inherit their master account's setting, so a household behaves consistently. (Requested in issue #8.) ▸ NEW — Sharing a TV with another adult. Two people with separate accounts linked by a Bonfire had to type a PIN with a TV remote every time they swapped, because an account with no PIN could not be entered from a shared Bonfire at all, and the local-network PIN bypass deliberately stopped at your own account. Both restrictions can now be lifted per account by its own owner, using "Let my Bonfire switch into my account on this network" in Bonfire Grouping. It is off by default so no existing install becomes more open on upgrade, only you can enable it for your own account, and it applies on the local network only — away from home the PIN is still required. Every switch that uses it is written to the profile activity log. The toggle warns before you enable it on an administrator account, and about reverse proxies missing from Jellyfin's Networking → Known Proxies list, where "local" would mean the whole internet. (Requested in issue #13.) ▸ FIX — The profile link in the navigation sidebar did nothing when clicked; it called a function that had never existed. All four ways into the switcher — header button, sidebar link, user menu and profile page — now run through one shared path. ▸ INTERNAL — The cross-account switch rules moved into two pure functions shared by the switch and PIN-verification endpoints, which had drifted: verification honoured the caller's own LAN bypass across a Bonfire link, so it could approve a switch the server then refused.

## 1.3.0  — 2026-08-05

First stable release of the 1.3 line, consolidating the 1.2.x pre-releases. ■ NEW — Tag-based content filtering. Each sub-profile can block or allow content by Jellyfin tag (for example "adults", "teens", "kids"), alongside the existing parental rating limit. Tags are inherited, so tagging a series or a whole library applies to everything inside it, and enforcement happens server-side — meaning it holds on every client, including ones that cannot show the profile switcher at all. Blocked tags are additive with the master account's, and an allow-list can only narrow the master's, so a sub-profile can never see more than its parent. ■ NEW — Reorganised profile editor. Creating and editing a profile is now grouped into four titled sections: Profile (name, colour, picture), Security (PIN, LAN bypass, auto-lock), Libraries, and Content & Device Restrictions. Previously every field sat in one flat list in the order features had been added. Sections are plain blocks rather than collapsible panels so D-pad focus order on TV stays predictable, and the layout adapts to phone widths. ■ NEW — The settings page now lists master accounts with their sub-profiles nested underneath, instead of two separate tables. ■ SECURITY — Fixed a stored cross-site scripting flaw in profile avatar images and colours. Because Bonfire groups render profiles from linked households on each other's switcher screens, a crafted value could run script in another user's browser and steal their session token. Values are now validated on the server and escaped on render. ■ SECURITY — Profile PINs are now stored as salted PBKDF2-SHA256 hashes rather than unsalted SHA-256, under which a 4-8 digit PIN was recoverable almost instantly from the configuration file. Existing PINs keep working and are upgraded automatically the next time they are entered. ■ SECURITY — An account with no PIN can no longer be switched into from a shared Bonfire, and the local-network PIN bypass no longer applies when stepping into another household's account. Also removed an open redirect on the profile image endpoint. ■ FIX — The dashboard's client-script warnings are now accurate. They re-check index.html on every page load rather than reporting server-startup state, a "Re-check Now" button re-runs the injection without a Jellyfin restart, and the message names the specific cause — including whether Jellyfin can write the file at all. The advisory notice can be dismissed on installs where the file is not writable without elevating. ■ FIX — Permission-fix commands are generated for the account Jellyfin actually runs as, rather than covering every possibility at once. ■ FIX — Editing a profile no longer erases device whitelist entries for devices that are switched off, and the device picker is correctly scoped to your own household. ■ FIX — A saved PIN no longer displays as "No PIN" on your own network, and profile changes appear immediately instead of requiring a page reload. ■ PERFORMANCE — Profile switching no longer rewrites the entire audit log on the request, the client script is served with revalidation so browsers pick up updates on their own, and a listener leak that made the interface progressively less responsive over a session has been fixed.

## 1.2.12  — 2026-08-05

⚠️ BETA RELEASE — not a finished release; please report issues on GitHub. ▸ Settings page: master accounts and sub-profiles were listed as two separate tables, so working out which profiles belonged to which account meant reading the "Master Account" column on every row. There is now a single list of master accounts, each with its sub-profiles collapsed underneath it behind a "2 sub-profiles" toggle. PIN status and Reset PIN are available at both levels as before. Any sub-profile whose master account no longer exists — normally the residue of a partially failed deletion — is listed separately under "Unlinked sub-profiles" rather than disappearing, so it can still be found and managed. ▸ The permission-fix commands are now generated for the account Jellyfin is actually running as, and the page states which account that is. Previously they had to cover every possibility at once: on Windows, Jellyfin runs either as a service (NT AUTHORITY\NetworkService) or as the signed-in user when installed as a tray/desktop app, so the icacls command granted Modify to ALL local users in order to work in both cases. It now grants access to exactly one account — the real one — which is both less to type and a considerably narrower permission on a file inside Program Files. ▸ The same applies to the Linux and Docker chown commands, which previously assumed the service account was named "jellyfin". That is the convention but not a guarantee, and the commands silently did nothing useful when it was not. If the account cannot be determined, the page falls back to the previous broader commands, so nothing is lost on unusual setups.

## 1.2.11  — 2026-08-05

⚠️ BETA RELEASE — not a finished release; please report issues on GitHub. ▸ The 'script tag not up to date' notice now explains the actual cause instead of the symptom. It reports whether Jellyfin can write index.html at all — tested directly rather than inferred — so on a Program Files or read-only install it now says so plainly, instead of only stating that the version marker was old and leaving you to guess why re-checking never helped. It also recognises a tag that predates the cache-buster and says that specifically. ▸ The notice is re-titled and re-worded to match its real severity. The profile switcher is unaffected by a stale tag, and since 1.2.6 browsers revalidate the client script on their own within about five minutes, so write access only makes updates instant rather than being required. It previously read like something was broken. ▸ Added a 'Dismiss until next update' button. Where index.html genuinely cannot be written without elevating, there is nothing an administrator can do, and a permanent banner is just noise. Dismissal is tied to the running version, so a new release surfaces it again. The red 'injection failed' banner — the one that does mean the switcher is not loading — can never be dismissed. ▸ Internal: a specific failure reason recorded while patching is no longer overwritten by the generic status refresh that runs immediately afterwards, which was hiding permission errors behind a version-mismatch message.

## 1.2.10  — 2026-08-05

⚠️ BETA RELEASE — not a finished release; please report issues on GitHub. ▸ Fix: the 'Plugin Script Update Pending' warning could appear permanently even when index.html was perfectly up to date, and no amount of fixing permissions or re-checking would clear it. The cache-buster written into the script tag was taken from the plugin instance when available and the assembly otherwise — but this runs at server startup, possibly before the plugin instance exists, so the tag could be WRITTEN using one source and later COMPARED against the other. If those render differently (for example '1.2.8' against '1.2.8.0') the check could never succeed again. The version now comes from a single deterministic source, and the comparison extracts the version from index.html instead of matching the whole tag string character-for-character — so a tag with different attribute order, quoting or spacing is correctly recognised as current. ▸ The warning now names both versions ('index.html requests client script v1.2.8.0, but this build is v1.2.10.0') so it can be checked rather than guessed at, and explains that browsers pick up new client code on their own within about five minutes thanks to the revalidation added in 1.2.6 — granting write access only makes it immediate.

## 1.2.9  — 2026-08-05

⚠️ BETA RELEASE — not a finished release; please report issues on GitHub. ▸ Fix: a newly saved PIN (or any other profile change) still showed the old state until the page was reloaded — a freshly set PIN kept reading as 'No PIN'. Opening the switcher from the home screen uses a background-prefetched copy of the profile list so the overlay appears without a flash, but that copy was also being served to the render that happens straight after a save, and was refilled on every fetch so it never expired. Saving, creating, deleting a profile, and leaving the Bonfire panel now always re-read from the server. ▸ Diagnostics: when the local-network PIN bypass skips a PIN, the client address and the fact that Jellyfin classified it as local are now logged. If you run Jellyfin behind a reverse proxy that is not listed under Networking → Known Proxies, every request arrives carrying the proxy's address and is therefore treated as local — which would apply the bypass to remote users. This log line is how to confirm that is not happening on your server.

## 1.2.8  — 2026-08-05

⚠️ BETA RELEASE — not a finished release; please report issues on GitHub. ▸ Fix: buttons that appeared to do nothing. Alert and confirmation dialogs were rendering behind the full-screen profile overlay, so they were completely invisible. Saving a profile with an invalid PIN silently did nothing instead of explaining why, and the Delete Profile and Bonfire confirmations were affected the same way. ▸ Fix: a saved PIN could look like it had never been stored. The edit form decided whether a PIN existed using a flag that is deliberately false on the local network when 'Bypass PIN on local network' is enabled — so on your own LAN a correctly saved PIN showed as 'Unprotected' with no Clear PIN button. The form now tracks whether a PIN exists separately from whether one will be prompted for, and states plainly when a PIN is set. (A master PIN has never been required in order to give a sub-profile a PIN, and still isn't.) ▸ Fix: PIN validation now reports the problem inline next to the field, including how many digits you actually entered, rather than relying solely on a dialog. ▸ Performance: fixed a listener leak that made clicks progressively slower the longer a session stayed open. Each time the create or edit form opened it attached another document-wide click handler and never removed it, so every click on the page ran a growing pile of stale handlers bound to discarded elements. ▸ Performance: profile switching no longer reads, parses and rewrites the entire audit log on the request. That was synchronous disk I/O and JSON work on the exact click that takes you to the home screen; the log is now kept in memory and persisted in the background. ▸ Diagnostics: profile switches are now timed, and any switch taking over a second logs a warning breaking down where the time went (PIN check, policy sync, session creation, audit write).

## 1.2.7  — 2026-08-04

⚠️ BETA RELEASE — this build contains a reworked profile editor and is not a finished release. Please report anything that looks wrong on GitHub. The plugin settings page shows a 'v1.2.7-beta' badge and a pre-release notice so you can tell it apart from a stable build. ▸ Fix: the Allowed Devices list no longer shows every device connected to the server by any user. v1.2.6 introduced per-household device ownership but treated records with no recorded owner as visible to everyone — and on an existing install every record starts out unowned, so the picker listed the whole server's devices to every account. Unowned records are now claimed for a household first (via that household's live sessions, or by already appearing on one of its profile whitelists) and are never listed until they are. The v1.2.6 fix that keeps powered-off devices from being silently dropped from a whitelist on save is retained. ▸ UI: the Create and Edit Profile forms are reorganised into four titled sections — Profile (name, colour, picture), Security (PIN, LAN bypass, auto-lock), Libraries, and Content & Device Restrictions (devices, parental rating, tags). Previously every field sat in one flat list in the order features had been added, so unrelated controls were adjacent and there were no landmarks to scroll by. Sections are plain blocks rather than collapsible panels so D-pad focus order on TV stays predictable, and the layout adapts for phone widths. ▸ UI: the 'Your Bonfire' header now uses a campfire icon instead of a house, matching the feature's name; your own Bonfire is amber and linked households are ember-orange so the two remain distinguishable. ▸ UI: clearer hints on the library and tag controls, keyboard Escape closes the device dropdown, and the device list no longer shows a bogus 'Last seen' date for devices restored from a whitelist.

## 1.2.6  — 2026-08-04

Fix: the dashboard's injection warning banner no longer persists after you fix the problem. The status was calculated once at server startup, so running the documented chown/chmod/icacls command and reloading the settings page still showed the same error until a full Jellyfin restart. The banner now re-reads index.html every time the page loads, and a new 'Re-check Now' button re-runs the injection on demand — no restart required. Fix: the banner now states the specific problem (missing script tag, out-of-date version, or unwritable file) instead of listing every possible cause, and a green confirmation is shown when everything is correct. Fix: a missing <head> tag now shows the amber 'update pending' notice rather than either claiming success or firing the red failure banner. Fix: the client script is served with a version ETag and revalidation, so browsers pick up new client code within minutes even on servers where index.html cannot be rewritten — previously a stale cache could hide new features indefinitely. Fix: editing a profile no longer erases device whitelist entries for devices that are switched off. The device picker was filtered to devices with a live session, so any powered-down device silently disappeared from the list and was dropped on save — and an emptied whitelist disables the device restriction entirely. Device ownership is now recorded per household, and every already-allowed device always appears in the picker. Fix: stale plugin .old files are now located correctly for cleanup. Internal: version numbers are no longer hardcoded anywhere in the UI or API responses.

## 1.2.5  — 2026-08-04

UI: Added running plugin version badge to admin settings page header (e.g. v1.2.5). Fix: Atomic Regex tag replacement and seamless script tag updates on server startup.

## 1.2.4  — 2026-08-04

Fix: Replaced manual index.html string slicing with atomic Regex tag replacements. Existing script tags (e.g. from older releases) are now updated in-place to current version cache-busters without leaving stale version tags behind.

## 1.2.3  — 2026-08-04

Fix: Streamlined plugin auto-injection and Windows plugin updates — eliminating false-positive 'injection failed' dashboard error banners whenever client script is active. Automatically strips read-only flags on index.html during server startup. Enhances Windows icacls permissions instructions for users running Jellyfin in Desktop/Tray mode.

## 1.2.2  — 2026-08-04

Fix: Added dedicated dashboard warning banner for pending script tag version updates. When host write permissions prevent updating index.html script tag version cache-busters during a plugin update, a distinct warning now informs admins that the profile switcher remains active and recommends a browser hard refresh (Ctrl+Shift+R) if new features do not appear.

## 1.2.1  — 2026-08-04

Fix: Plugin update no longer leaves the old client script cached in the browser — the injection now detects when the script tag's version cache-buster is stale and re-injects with the current version, so new UI features (like tag filtering) appear immediately after update without a manual cache clear. Fix: the 'Allowed Devices' dropdown when creating or editing a sub-profile now shows only devices the calling master account has sessions on, instead of every device that ever connected to the server. Fix: the 'injection failed' dashboard banner no longer appears as a false positive after a successful injection on a plugin update.

## 1.2.0  — 2026-08-04

New: Tag-based content filtering for sub-profiles. Each profile can now block or allow content by Jellyfin tag (e.g. 'adults', 'teens', 'kids'), matching Jellyfin's own parental-control tag feature. Tags are inherited, so tagging a series or an entire library applies to everything inside it, and enforcement is server-side so it holds on every client. Blocked tags are additive with the master account's and an allow-list can only narrow the master's, so a sub-profile can never see more than its parent. Security: fixed a stored XSS in profile avatar images and colours — these are rendered on other households' switcher screens via Bonfire groups, so a crafted value could steal another user's session token; values are now validated server-side and escaped on render. Security: profile PINs are now stored as salted PBKDF2-SHA256 hashes instead of unsalted SHA-256 (a 4-8 digit PIN was trivially recoverable from the config file); existing PINs keep working and upgrade automatically on next entry. Security: an account with no PIN can no longer be switched into from a shared Bonfire, and the LAN PIN bypass no longer applies across accounts. Security: removed an open redirect on the profile image endpoint. Fix: the client-script injection now verifies the script is actually present before reporting success — previously an index.html without the expected anchors reported success while the switcher silently never loaded, and the dashboard's failure banner could never appear. Fix: index.html is now written atomically so an interrupted write cannot corrupt the Jellyfin web client. Fix: audit log entries no longer reverse order when trimmed.

## 1.1.13  — 2026-07-30

Fix: Plugin now loads correctly on Jellyfin 10.11.5. The previous release was compiled against 10.11.6, which caused a loader-level assembly version mismatch (FileNotFoundException on MediaBrowser.Common/Controller/Model) on servers running 10.11.5. Compile target downgraded to 10.11.5 — the plugin remains fully compatible with 10.11.6 and later patch releases.

## 1.1.12  — 2026-07-11

Fix: Creating a sub-profile no longer throws 'Error processing request' on Jellyfin 10.11. Root cause: the profile policy was initialized with new UserPolicy(), leaving AuthenticationProviderId null — which Jellyfin 10.11 enforces as NOT NULL in UpdatePolicyAsync. Policy is now seeded from the newly created user's own DTO so all required provider fields are preserved. (Thanks to PepeTechs for the PR.) Also corrects the v1.1.11.0 manifest checksum which was mistakenly left as all-zeros.

## 1.1.11  — 2026-06-30

Fix: Settings page no longer shows a false-positive injection error when the plugin is working correctly (the status check was treating a missing camelCase field as a failure when ASP.NET returned PascalCase). Fix: Permission fix instructions now show the correct two-step chown+chmod commands (sudo chown jellyfin:jellyfin / sudo chmod 664) that actually work on native Linux installs, based on user-confirmed testing. Fix: Creating or editing a sub-profile with no libraries explicitly selected now correctly inherits all master-accessible libraries instead of silently locking the profile out of every library.

## 1.1.10  — 2026-06-24

Fix: index.html backup is now stored in the plugin data directory instead of next to index.html. On Linux and Docker installs the jellyfin service user can write to index.html after chmod 666, but cannot create new files in the web root directory — causing an UnauthorizedAccessException before injection ran. Backup creation is now also non-fatal (injection proceeds even if the backup step fails). Error messages updated to include chmod 755 on the parent directory.

## 1.1.9  — 2026-06-19

Security & Stability Release: resolves a critical privilege escalation bug in library permission mapping (ensuring sub-profiles cannot be assigned unauthorized libraries); fixes index.html script double-injection and corruption by using safe conditional inserting; adds index.html backup before patching; mitigates IP-based PIN brute force by rate limiting per profile; resolves XSS vulnerability in profile name rendering; ensures proper lock synchronization on configuration mappings.

## 1.1.8  — 2026-06-15

Fix: Rename plugin name/directory from 'Bonfire/JellyProfiles' to 'Bonfire' to resolve the Windows server folder deletion and lock conflict bugs. This stops the restart loop and allows clean updates.

## 1.1.7  — 2026-06-15

Fix: profile switching now works correctly on Jellyfin 10.11+. Root cause: the Authorization header parser was not stripping the 'MediaBrowser ' scheme prefix, so the Client parameter always returned null — causing SessionManager.AuthenticateNewSessionInternal to throw ArgumentNullException (request.App). Fixed in GetAuthorizationParameter; added guaranteed fallbacks on AuthenticationRequest so the switch endpoint can never throw on missing header fields.

## 1.1.6  — 2026-06-15

Fix: merged BonfireController and AdminController back into single ProfilesController. Jellyfin's plugin loader does not support multiple controllers with the same route prefix from a plugin assembly, causing a permanent restart loop on v1.1.4 and v1.1.5. All endpoints now in one controller; ProfilesBaseController retained for shared helpers only.

## 1.1.5  — 2026-06-15

Bonfire UI fixes: Generate and Join buttons are now disabled during fetch to prevent double-fire; settings checkboxes are debounced 300ms to prevent race conditions when toggling both quickly; join input/button now wraps to full width on screens under 360px; .profiles-btn:disabled visual state added.

## 1.1.4  — 2026-06-15

Structural refactor: ProfilesController split into ProfilesController, BonfireController, and AdminController via shared ProfilesBaseController. Five structural issues fixed: controller size, [AllowAnonymous] scope documentation, static field documentation, null-dereference guard on Plugin.Instance in audit log path, redundant SaveConfiguration eliminated from RecordDeviceActivity on known-device updates.

## 1.1.3  — 2026-06-15

Structural cleanup and deletion fix: profile deletion now terminates active sessions first (fixes the root cause of deletion always failing); AuditLogEntry, request model classes, and rate limiters moved to proper namespaces; BonfireRateLimiter/PinRateLimiter merged into a single parameterized RateLimiter class.

## 1.1.2  — 2026-06-15

Bug fixes: GIF profile avatars now served correctly; master token cleared on native sign-out; deletion fallback no longer orphans plugin mapping; Bonfire GroupId no longer re-randomizes on config reload; JS file cached server-side (reduced memory pressure); DOM polling reduced from 150ms to 500ms; audit logs moved to separate file (no longer bloats PluginConfiguration.xml rewrite); profile image uploads capped at 2 MB; Bonfire code lookup is now case-insensitive.

## 1.1.1  — 2026-06-15

Fix: Simplified client-side script auto-injection troubleshooting instructions to use dynamic resolved file paths and a robust permission-granting chmod approach.

## 1.1.0  — 2026-06-14

Release 1.1.0: Codebase cleanup, optimization, documentation updates, and developer API reference alignment.

## 1.0.0  — 2026-06-11

Initial release.
