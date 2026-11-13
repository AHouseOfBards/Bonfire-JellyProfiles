# Bonfire API

| | |
| --- | --- |
| Plugin ID | `b1462fca-774b-4b13-8d02-e2d4f2bc18b9` |
| Base path | `/plugins/profiles` |
| Server | Jellyfin 10.11.x and 12.0 |

The [API reference](api.html) lists every route with its request and response.

Bodies are JSON unless stated otherwise. Jellyfin picks the casing of fields it returns:
PascalCase by default, camelCase if the request sends `Accept: application/json; profile="CamelCase"`.
Requests are read either way. The API exists for Bonfire's own client. It's documented
because other people build against it too.

## Contents

- [All routes](#all-routes)
- [Authentication](#authentication)
- [Errors](#errors)
- [Rate limits](#rate-limits)
- [Caching](#caching)
- [Stability](#stability)
- [Profiles](#profiles)
- [Sign-in from other apps](#sign-in-from-other-apps)
- [Client script](#client-script)
- [Translations](#translations)
- [Images](#images)
- [Library artwork](#library-artwork)
- [Devices](#devices)
- [Bonfire](#bonfire)
- [Other accounts](#other-accounts)
- [Preferences](#preferences)
- [First-time setup](#first-time-setup)
- [Email](#email)
- [Viewing hours](#viewing-hours)
- [Avatar library](#avatar-library)
- [Announcements](#announcements)
- [Maintenance](#maintenance)
- [Emergency disable](#emergency-disable)
- [For other plugins](#for-other-plugins)
- [Linking to the settings page](#linking-to-the-settings-page)
- [Admin](#admin)

## All routes

All 101 endpoints. **Auth** is what each one enforces: `user` means any signed-in account,
`anon` means no token at all. `tests/js/apidocs.js` checks this table against the code.

| Route | Auth | Purpose |
| --- | --- | --- |
| `GET /plugins/profiles/list` | user | Profiles the caller can switch to. |
| `GET /plugins/profiles/startup` | user | `info`, `profiles`, `me`, `themes`, `announcements` and `onboarding` in one request. |
| `GET /plugins/profiles/form-options` | user | What the profile form reads: libraries, rating limits, avatars, time zones. |
| `POST /plugins/profiles/create` | user | Create a profile. |
| `POST /plugins/profiles/update` | user | Change a profile. |
| `POST /plugins/profiles/copy-rules` | user | Copy one profile's rules onto another. |
| `POST /plugins/profiles/me` | user | A profile changes its own name and image. |
| `POST /plugins/profiles/delete` | user | Delete a profile and its account. |
| `POST /plugins/profiles/switch` | user | Switch to a profile and get its token. |
| `POST /plugins/profiles/verify-pin` | user | Check a PIN without switching. |
| `POST /plugins/profiles/account/verify-pin` | user | Check the main account's PIN. |
| `POST /plugins/profiles/account` | user | Set the main account's PIN and whether opening it asks. |
| `GET /plugins/profiles/account/email` | user | The main account's email address and notices. |
| `POST /plugins/profiles/account/email` | user | Send a code to a new address, remove the address, or choose which notices to get. |
| `POST /plugins/profiles/account/email/confirm` | user | Confirm the address with the code. |
| `POST /plugins/profiles/account/pin-reset/send` | user | Email a code to reset a forgotten main PIN. |
| `POST /plugins/profiles/account/pin-reset` | user | Set a new main PIN with the code. |
| `GET /plugins/profiles/limits/{profileId}` | user | A profile's viewing hours and where it stands now. |
| `POST /plugins/profiles/limits/{profileId}` | user | Set a profile's viewing hours and daily limit. |
| `POST /plugins/profiles/limits/{profileId}/grant` | user | Give a profile more time, or say no. |
| `POST /plugins/profiles/limits/{profileId}/ask` | user | Ask for more time. |
| `GET /plugins/profiles/time-requests` | user | The household's profiles waiting for more time. |
| `GET /plugins/profiles/profiles.js` | anon | The client script. |
| `GET /plugins/profiles/i18n/{locale}` | anon | One translation. |
| `GET /plugins/profiles/image/{profileId}` | anon | A profile image. |
| `GET /plugins/profiles/library-artwork` | user | The caller's library artwork, or one of their profiles' with `profileId`. |
| `POST /plugins/profiles/library-artwork` | user | Set library artwork. |
| `GET /plugins/profiles/library-art/{profileId}/{libraryId}` | anon | One library tile image. |
| `GET /plugins/profiles/devices` | user | The household's devices. |
| `POST /plugins/profiles/devices/delete` | user | Forget a device. |
| `POST /plugins/profiles/devices/merge` | user | Merge two records for one device. |
| `POST /plugins/profiles/devices/merge-duplicates` | user | Find, and optionally merge, duplicate records. |
| `POST /plugins/profiles/devices/rename` | user | Rename a device. |
| `GET /plugins/profiles/bonfire/status` | user | The caller's Bonfire. |
| `POST /plugins/profiles/bonfire/settings` | user | Change Bonfire sharing options. |
| `POST /plugins/profiles/bonfire/generate` | user | Start a Bonfire, or change the code of the one you have. |
| `POST /plugins/profiles/bonfire/join` | user | Join a Bonfire with a code. |
| `POST /plugins/profiles/bonfire/kick` | user | Remove a member and change the code. |
| `POST /plugins/profiles/bonfire/leave` | user | Leave a Bonfire. |
| `POST /plugins/profiles/bonfire/delete-group` | user | End your Bonfire. |
| `GET /plugins/profiles/accounts/links` | user | Other accounts on the caller's switcher, and the switchers it's on. |
| `POST /plugins/profiles/accounts/links/add` | user | Add another account, with its password. |
| `POST /plugins/profiles/accounts/links/remove` | user | Take an account off, or leave another switcher. |
| `GET /plugins/profiles/preferences` | user | Switcher preferences. |
| `POST /plugins/profiles/preferences` | user | Change them. |
| `GET /plugins/profiles/themes` | anon | Every theme, for other apps. |
| `GET /plugins/profiles/themes/patterns/{name}` | anon | One pattern image. |
| `GET /plugins/profiles/themes/pattern-files/{file}` | anon | A custom theme's uploaded pattern image. |
| `GET /plugins/profiles/themes/fonts/{file}` | anon | One font file, or its license. |
| `GET /plugins/profiles/themes/logo/{file}` | anon | The server's logo image. |
| `GET /plugins/profiles/themes/backgrounds/{file}` | anon | A custom theme's background image. |
| `POST /plugins/profiles/onboarding/done` | user | Record that it was seen. |
| `GET /plugins/profiles/avatars/{id}` | anon | One avatar image. |
| `POST /plugins/profiles/admin/avatars` | admin | Add an avatar. |
| `DELETE /plugins/profiles/admin/avatars/{id}` | admin | Remove an avatar. |
| `GET /plugins/profiles/admin/avatars/scan` | admin | List images in a server folder. |
| `GET /plugins/profiles/admin/avatars/scan/file` | admin | Read one of them. |
| `GET /plugins/profiles/admin/announcements` | admin | Every announcement. |
| `POST /plugins/profiles/admin/announcements` | admin | Create or change one. |
| `POST /plugins/profiles/admin/announcements/delete` | admin | Delete one. |
| `GET /plugins/profiles/admin/maintenance` | admin | Whether the server is closed. |
| `POST /plugins/profiles/admin/maintenance` | admin | Close or reopen it. |
| `POST /plugins/profiles/panic` | anon | Emergency disable. |
| `GET /plugins/profiles/info` | anon | Version and which features are on. |
| `GET /plugins/profiles/panic-state` | anon | Whether Bonfire is disabled. |
| `GET /plugins/profiles/admin/panic-status` | admin | Whether a code is set. |
| `POST /plugins/profiles/admin/panic-code` | admin | Set or clear the code. |
| `POST /plugins/profiles/admin/settings` | admin | Server-wide settings. |
| `POST /plugins/profiles/admin/onboarding/show-again` | admin | Show first-time setup to every main account again. |
| `GET /plugins/profiles/admin/themes` | admin | Custom themes, hidden themes and uploaded fonts. |
| `POST /plugins/profiles/admin/themes/validate` | admin | Check a theme without saving it. |
| `PUT /plugins/profiles/admin/themes/custom/{id}` | admin | Save a custom theme. |
| `DELETE /plugins/profiles/admin/themes/custom/{id}` | admin | Delete one. |
| `PUT /plugins/profiles/admin/themes/hidden` | admin | Choose which built-in themes to hide. |
| `POST /plugins/profiles/admin/themes/fonts` | admin | Upload a font. |
| `DELETE /plugins/profiles/admin/themes/fonts/{file}` | admin | Delete one. |
| `PUT /plugins/profiles/admin/themes/logo` | admin | Set the logo's text, alt text and corners. |
| `POST /plugins/profiles/admin/themes/logo/image` | admin | Upload the logo image. |
| `DELETE /plugins/profiles/admin/themes/logo/image` | admin | Remove it. |
| `POST /plugins/profiles/admin/themes/backgrounds` | admin | Upload a background image for a custom theme. |
| `POST /plugins/profiles/admin/themes/patterns` | admin | Upload a pattern image for a custom theme. |
| `GET /plugins/profiles/admin/email` | admin | Outgoing mail settings and confirmed addresses. |
| `POST /plugins/profiles/admin/email` | admin | Save the outgoing mail settings. |
| `POST /plugins/profiles/admin/email/test` | admin | Send a test message, which makes email available. |
| `POST /plugins/profiles/admin/account-email/remove` | admin | Remove a main account's address. |
| `GET /plugins/profiles/admin/mappings` | admin | Every profile, plus script status. |
| `POST /plugins/profiles/admin/reset-pin` | admin | Clear a profile's PIN. |
| `POST /plugins/profiles/admin/cleanup-orphans` | admin | Remove records for deleted users. |
| `GET /plugins/profiles/admin/sessions` | admin | Live sessions by household. |
| `POST /plugins/profiles/admin/sessions/sign-out` | admin | Sign one device out. |
| `GET /plugins/profiles/admin/config/export` | admin | Export the configuration. |
| `POST /plugins/profiles/admin/config/import` | admin | Import a configuration. |
| `POST /plugins/profiles/admin/retry-injection` | admin | Check the script is reaching browsers again. |
| `POST /plugins/profiles/admin/set-profile-limit` | admin | One account's profile limit. |
| `POST /plugins/profiles/admin/standalone` | admin | Make a profile its own account. |
| `POST /plugins/profiles/admin/make-profile` | admin | Make an account a profile of another. |
| `POST /plugins/profiles/admin/links` | admin | Add an account to a switcher. |
| `POST /plugins/profiles/admin/links/remove` | admin | Take it off. |
| `POST /plugins/profiles/admin/pin-prompts` | admin | Set one household's PIN prompts. |
| `GET /plugins/profiles/admin/audit-logs` | admin | The switch log. |
| `POST /plugins/profiles/admin/audit-logs/clear` | admin | Delete the whole switch log. |

## Authentication

Send Jellyfin's usual header. Manage profiles with the main account's token; after a
switch, use the token it returns.

```http
Authorization: MediaBrowser Client="<Client>", Device="<Device>", DeviceId="<DeviceId>", Version="<Version>", Token="<token>"
```

**Administrator routes also take an API key** from **Dashboard → API Keys**, sent as the
token. A key has no user, so routes for a household (`/list`, `/me`, `/create` and the
rest) refuse one.

Fourteen routes are reachable without a token, because they're needed before anyone signs in.

| Endpoint | Why it is anonymous |
| --- | --- |
| `GET /profiles.js` | Loaded by the sign-in page. |
| `GET /i18n/{locale}` | Fetched by that script before sign-in. |
| `GET /image/{profileId}` | Images on the profile screen, which is shown before sign-in. |
| `GET /avatars/{id}` | Same. |
| `GET /library-art/{profileId}/{libraryId}` | Same. |
| `POST /panic` | Has to work when Bonfire is what's blocking the interface. Rate limited. |
| `GET /info` | Read before sign-in to decide what to show. Server-wide settings only. |
| `GET /panic-state` | Says only whether Bonfire is disabled. |
| `GET /themes` | An app draws its switcher before sign-in. Only the default theme, whether profiles may choose, and which themes are hidden. |
| `GET /themes/patterns/{name}` | Images those themes use. Built-in files only. |
| `GET /themes/pattern-files/{file}` | Pattern images custom themes use. Only images the administrator uploaded. |
| `GET /themes/fonts/{file}` | Fonts those themes use, and their licenses. Built-in files, and fonts the administrator uploaded. |
| `GET /themes/logo/{file}` | The logo on the switcher, shown before sign-in. Only the current one. |
| `GET /themes/backgrounds/{file}` | Background images custom themes use. Only images the administrator uploaded. |

The image routes can be reached by anyone who knows the id. Ids are GUIDs, so they can't
be guessed.

Every other route checks the caller itself, because the controller is
`[AllowAnonymous]` so the routes above work. A new route with no check is public.

### The main account's PIN

Routes that change the household need the main account's PIN, if it has one. Send it as
`masterPin` in the body where a route documents that field, or otherwise in a header:

```http
X-Bonfire-Master-Pin: 1234
```

These take the header: everything under `/bonfire` except `status`, `/devices/delete`,
`/devices/merge`, `/devices/rename`, `/devices/merge-duplicates` with `apply=true`, and
`POST /preferences`.

Check the PIN once with [`POST /account/verify-pin`](#post-pluginsprofilesaccountverify-pin)
and keep it for the session. Without it these routes return `400` with `pin_missing`; a
wrong one returns `pin_wrong` and counts toward the
[rate limit](#rate-limits). A missing PIN isn't counted.

When the administrator requires main PINs (`features.requireMainPin` in `/info`), an
account with no PIN gets `main_pin_required` from these routes until it sets one with
[`POST /account`](#post-pluginsprofilesaccount).

### `GET /plugins/profiles/info`

**Authorization:** anonymous.

Call this first, with no token. A `404` means Bonfire isn't installed. `disabled: true`
means an administrator has switched it off: show no Bonfire screens.

```json
{
  "name": "Bonfire",
  "version": "1.7.1.2-beta",
  "apiVersion": 2,
  "disabled": false,
  "features": {
    "clientPinLogin": true,
    "clientProfileList": true,
    "skipQuickConnectOnKnownDevices": false,
    "profileThemes": true,
    "proxyClients": ["moonfin"],
    "requireMainPin": false,
    "email": false,
    "pinResetByEmail": false
  },
  "maintenance": null
}
```

`features` holds server-wide settings only, never anything about a household. `email` is
true once outgoing mail is set up and tested; `pinResetByEmail` is true when that and the
administrator's switch are both on. A feature is only ever reported available when
everything it needs is too.

This is public on purpose. A television's app and the sign-in page need `proxyClients`,
`requireMainPin`, `clientPinLogin` and `maintenance` before anyone can sign in, and the
sign-in page already carries the version in its script address. It never names an
account, a profile or a PIN.

`maintenance` is null, or `{ "message": "…", "untilUtc": "…" }` while the server is closed:
see [Maintenance](#maintenance). Sent with
`no-store`.

### `GET /plugins/profiles/startup`

**Authorization:** signed-in user. No token, or an API key (which has no user), is `401`.

Everything the profile picker needs, in one request. `info`, `profiles` and `themes` are
what `GET /info`, `GET /list` and `GET /themes` return for the same caller, built by the
same code. `me`, `announcements` and `onboarding` are only here:

```json
{
  "info": { "...": "GET /info" },
  "profiles": [ "...GET /list" ],
  "me": { "...": "see Me, below" },
  "themes": { "...": "GET /themes" },
  "announcements": [ "...see Announcements" ],
  "onboarding": { "...": "see First-time setup" }
}
```

If any part would be refused, the whole call is refused the same way (for example `404`
when the account can't be found), never a partial body. Sent with `no-store`. `/list`,
`/info` and `/themes` stay as routes, and `/info` stays public for the sign-in screen.

### Switching

`POST /switch` returns a new token and leaves the old session signed in.

- **End the old session.** Send `"endPreviousSession": true` with `/switch`, and the token
  that made the request is signed out once the new one exists. Only that token: the
  account's other sessions stay. Without it, send Jellyfin's `POST /Sessions/Logout` with
  the old token yourself. Then use the new token for everything. If your app can fail to
  save the new token, leave the field off and sign out the old one after saving, so a
  failed save leaves the user signed in.
- **Keep one token.** A profile's token can call `/list` and `/switch` too: Bonfire works
  out its main account. Switching back to the main account is another `/switch`.
- **Manage profiles as the main account.** `/create`, `/update` and `/delete` refuse a
  profile's token, so switch to the main account first.
- **Lock after `lockoutMinutes`.** The server stores the value but doesn't enforce it.
  After that long with no input, return to the profile screen.

## Errors

| Code | Meaning |
| --- | --- |
| `200` | Done, including when there was nothing to change. |
| `304` | Not modified: the `ETag` you sent still matches. See [Caching](#caching). |
| `400` | Refused. The body says why. |
| `409` | A save from an older copy of what it changes. Reload and try again. |
| `401` | Not signed in: no token, or the token was rejected. Sign in again. This is the only case that gets a `401`. |
| `403` | Signed in but not allowed: a profile on a main-account route, a non-administrator on an administrator route, or an API key on a route that needs a person. Keep the session. |
| `404` | Unknown profile, image, library or main account. |
| `429` | Rate limited. Most limits also send `Retry-After` in seconds. |
| `500` | A bug. |
| `503` | Closed for maintenance (`maintenance`), or a translation could not be read. Try again later. |

**The body is plain text, written for the user.** Show it. To decide what to do, read the
`X-Bonfire-Error` header instead: a code that never changes once released.

```
HTTP/1.1 400 Bad Request
Content-Type: text/plain; charset=utf-8
X-Bonfire-Error: pin_wrong

Invalid PIN code.
```

| Code | Meaning |
| --- | --- |
| `unauthenticated` | No valid token. The only code sent with `401`. |
| `forbidden` | Not allowed, for a reason with no code of its own. |
| `not_main_account` | Only the main account can do this. Switch to it first. |
| `admin_only` | Only an administrator can do this. |
| `not_your_profile` | The profile belongs to another household. |
| `themes_locked` | The server doesn't let profiles choose a theme. |
| `theme_invalid` | The theme isn't valid. The body lists each problem. Also a theme that can't be hidden. |
| `theme_limit` | 40 custom themes are stored. Delete one first. |
| `theme_not_found` | No custom theme has that id. |
| `theme_in_use` | That theme is the default. Choose another default first. |
| `theme_changed` | The theme was saved again after the copy you edited. Reload it. Sent with `409`. |
| `font_invalid` | The file isn't a WOFF2 font that can draw Latin letters, is over 2 MB, or the name or fallback isn't allowed. |
| `font_rights` | `rights=true` wasn't sent. |
| `font_taken` | A font with that name is already on the server. |
| `font_limit` | 20 fonts are stored. Delete one first. |
| `font_not_found` | No uploaded font has that file name. |
| `font_in_use` | A custom theme uses that font. |
| `logo_invalid` | Logo text or alt text is longer than allowed or more than one line. |
| `pin_wrong` | Wrong PIN. |
| `pin_missing` | The main account's PIN wasn't sent. See [The main account's PIN](#the-main-accounts-pin). |
| `main_pin_required` | The server requires main accounts to have a PIN, and this one has none. |
| `pin_format` | A PIN must be 4 to 8 digits. |
| `pin_needed_for_bonfire` | The account has no PIN, so it can't be opened through a Bonfire. |
| `bonfire_code_wrong` | Wrong or malformed Bonfire code. |
| `code_wrong` | Wrong emergency disable code, or none is set. Also a wrong email code. |
| `device_not_allowed` | The profile is limited to other devices. Also the 403 on Jellyfin's `POST /QuickConnect/Authorize` when a profile authorizes a device it could not sign in itself. |
| `profile_disabled` | The profile's account is turned off in Jellyfin. |
| `too_many_sessions` | Jellyfin's session limit for the account was reached. |
| `sign_in_failed` | Jellyfin refused to create the session. See the server log. |
| `device_in_use` | Forgetting the device would leave a profile with no device limit. The body names the profiles. |
| `profile_limit` | The account has as many profiles as it's allowed. |
| `name_taken` | The household already has a profile with that name. |
| `name_invalid` | The name is empty, too long or uses characters that aren't allowed. |
| `password_required` | A password is needed for this. |
| `main_account_protected` | The main account can't be deleted as a profile. |
| `announcement_invalid` | The announcement's message, severity or dates aren't allowed. |
| `announcement_limit` | 50 announcements are stored. Delete one first. |
| `announcement_not_found` | No announcement has that id. |
| `maintenance` | Closed for maintenance. Sent with `503` and `Retry-After`, on any route. |
| `maintenance_invalid` | The maintenance message is too long, its end time has passed, or it is to reopen by itself with no end time. |
| `outside_viewing_hours` | The profile can't watch at this time. Sent with `403` and `Retry-After`. See [Viewing hours](#viewing-hours). |
| `daily_limit_reached` | The profile has used today's time. Sent with `403` and `Retry-After`. |
| `not_limited` | The profile has no viewing hours or limit. |
| `limits_for_profiles` | Viewing hours are for profiles, not main accounts. |
| `limits_invalid` | A viewing window, limit or amount of time isn't allowed. |
| `time_zone_invalid` | The server doesn't know that time zone. |
| `email_unavailable` | Email isn't set up on this server. |
| `email_invalid`, `email_missing` | The address isn't valid, or there's none confirmed. |
| `code_expired`, `code_missing` | The email code has expired, or none was sent. |
| `email_send_failed` | The mail server refused the message. |
| `credentials_wrong` | The username or password for the account being added is wrong. |
| `admin_must_add` | Retired, never sent. An account that signs in through LDAP, SSO or similar gets `credentials_wrong`, so a username cannot be probed. |
| `not_linkable` | That account can't be added: it's the caller's own, or it's a profile. |
| `already_linked` | That account is already on the switcher. |
| `link_limit` | The switcher already shows 10 other accounts. |
| `admin_pin_required` | An administrator account needs a PIN before it can be added. |
| `link_not_found` | That account isn't on that switcher. |
| `not_convertible` | That account can't be made a profile. The body says why. |
| `bonfire_not_owner`, `bonfire_not_member`, `bonfire_own_group`, `bonfire_member_not_found` | Bonfire group actions that don't apply to the caller. |
| `profile_not_found`, `user_not_found`, `library_not_found`, `image_not_found`, `not_found` | The thing named doesn't exist. |
| `image_invalid` | The upload is missing, unreadable, too large, not an image, or not allowed on this server. |
| `rate_limited` | Too many attempts. Sent with `429`. |
| `not_configured` | Bonfire's configuration couldn't be read. |
| `invalid_request` | Refused, for a reason with no code of its own. |
| `server_error`, `unavailable` | Sent with `500` and `503`. |

New codes can be added in any release. Treat one you don't know by its status code.

## Rate limits

Attempts are counted before they're checked, so parallel requests can't slip past. A
correct attempt resets the count. A refused request returns `429`.

| Limiter | Applies to | Attempts | Window | Counted per |
| --- | --- | --- | --- | --- |
| PIN | `POST /switch`, `POST /verify-pin` | 5 | 15 minutes | address and profile |
| PIN, per profile | the same, plus the main account's PIN wherever it's checked | 10 | 15 minutes | profile, all addresses |
| client PIN | a PIN typed into another app's sign-in | 5, and 20 a day | 15 minutes, 24 hours | account |
| Bonfire | `POST /bonfire/join` | 3 | 15 minutes | address, and separately account |
| panic | `POST /panic` | 5 | 60 minutes | address |
| email codes | `POST /account/email`, `POST /account/pin-reset/send` | 5 | 60 minutes | account and route |
| email codes, server | the same | 60 | 60 minutes | the whole server |

A client PIN that hits its limit gets Jellyfin's usual `Invalid username or password`. A
main account's real password keeps working.

Ten wrong PINs in 15 minutes lock a profile from every address. That is on purpose: it
stops guessing from many addresses. The cost is that someone signed in can lock a sibling
out for 15 minutes.

## Caching

`GET /list`, the client script and translations send an `ETag`.
Send it back as `If-None-Match` and an unchanged answer is a `304` with no body. They're
also sent `private, no-cache`: keep them, but ask every time. A browser does this for
you.

## Stability

- Fields documented here keep their type and meaning within a major version.
- Ignore fields you don't recognize. New ones arrive in minor releases.
- Don't rely on field order.
- Routes aren't removed within a major version.
- **Retiring something:** it's marked **Deprecated** here, with what to use instead, for
  at least two minor releases before it goes. It only goes in a major release, which
  raises `apiVersion`.
- **Checking for a feature:** read `features` in `GET /info` rather than comparing
  version numbers. A new feature that a client has to know about gets a flag there.
- Optional request fields that you leave out, or send as `null`, keep their stored value.
  "No limit" and "every library" are explicit values, such as `""` or `allLibraries`.
- Error wording can change in any release. Branch on `X-Bonfire-Error`, not the text.
- `apiVersion` in `GET /info` goes up only when a released route changes in a way that
  breaks callers.

### Changes in 2.0

`apiVersion` is 2. These routes are gone, with no deprecation period:

- `GET /libraries`, `GET /ratings`, `GET /avatars` and `GET /timezones`: use
  [`GET /form-options`](#get-pluginsprofilesform-options), which returns all four.
- `GET /me`, `GET /announcements` and `GET /onboarding`: read the `me`, `announcements` and
  `onboarding` parts of [`GET /startup`](#get-pluginsprofilesstartup). `POST /me` and
  `POST /onboarding/done` stay.
- `POST /account/email/notifications` and `POST /account/email/remove`: send the switches, or
  `"address": ""`, to [`POST /account/email`](#post-pluginsprofilesaccountemail).
- `GET /library-artwork/{profileId}`: send `profileId` as a query to
  [`GET /library-artwork`](#get-pluginsprofileslibrary-artwork).
- `POST /bonfire/regenerate-code`: call
  [`POST /bonfire/generate`](#post-pluginsprofilesbonfiregenerate), which changes the code when
  the caller already has a Bonfire. The answer carries `bonfireCode`, as before.
- `POST /theme`: send `theme` to [`POST /update`](#post-pluginsprofilesupdate).
- `POST /update` now refuses a `theme` with `403` while the server doesn't let profiles
  choose, where it used to ignore it, and answers `{ profileId, theme, stored }`.

---

## Profiles

### `GET /plugins/profiles/list`

**Authorization:** signed-in user.

Every profile the caller can switch to: their own household, then any linked Bonfire.

```json
[
  {
    "profileUserId": "8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a",
    "profileName": "John",
    "avatarInitial": "J",
    "avatarColor": "#00A4DC",
    "transparentAvatar": false,
    "theme": null,
    "requiresPin": true,
    "hasPin": true,
    "isMaster": true,
    "lockoutMinutes": 10,
    "maxSubProfiles": 5,
    "bypassPinOnLocalNetwork": false,
    "seenOnLocalNetwork": false,
    "allowedDeviceIds": [],
    "isBonfire": false,
    "profileImage": null,
    "masterUserId": "8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a"
  }
]
```

| Field | Type | Description |
| --- | --- | --- |
| `profileUserId` | GUID | The profile's Jellyfin user id. |
| `profileName` | string | Display name. |
| `avatarInitial` | string | One letter for an avatar with no image. |
| `avatarColor` | string | Hex color behind the avatar. |
| `transparentAvatar` | boolean | Don't paint `avatarColor` behind the image. |
| `theme` | string | The account owner only. The profile's own [theme](#themes), or `null` if it follows the server default. |
| `requiresPin` | boolean | Ask for a PIN before switching **now**. False on the home network when the bypass is on, and for a main account whose owner turned off `askPinToOpen`. |
| `hasPin` | boolean | A PIN is set. Use this, not `requiresPin`, to show PIN state in a form. |
| `isMaster` | boolean | This is the main account. |
| `askPinToOpen` | boolean | The main account, to its owner only. Off, the account opens without its PIN; the PIN still guards changes. |
| `isAdministrator` | boolean | Main accounts only, and only the caller's own household; false on another's. The account is a Jellyfin administrator. For warnings, not for access checks. |
| `lockoutMinutes` | integer | Minutes of inactivity before the profile locks. `0` is never. |
| `maxSubProfiles` | integer | Main accounts only, and only the caller's own household; null on another's. How many profiles it may have. |
| `enabledFolders` | GUID[] | Profiles only. The libraries it was given. |
| `allLibraries` | boolean | Profiles only. It was given every library, so it follows the main account's, including ones added later. |
| `blockedTags` | string[] | The profile's own blocked tags. The main account's are added when it's opened. |
| `allowedTags` | string[] | The profile's own allowed tags. Empty means no allow-list. |
| `bypassPinOnLocalNetwork` | boolean | Skip the PIN on the home network. False on another household's entries. |
| `seenOnLocalNetwork` | boolean | The caller's own household has made a request from the server's network. Set once, never cleared. Screens offer `bypassPinOnLocalNetwork` only where this or that is true. False on another household's entries. |
| `allowedDeviceIds` | string[] | Devices it may be opened on. Empty means any. The device id is the one the app sends, as Jellyfin trusts it everywhere. This is a household setting, not a lock against someone who sets their own device id. |
| `isBonfire` | boolean | Belongs to another household in the caller's Bonfire, or is an [added account](#other-accounts). |
| `isLinked` | boolean | Another main account added to the caller's switcher. Opening it signs into that account. |
| `profileImage` | string | `/plugins/profiles/image/{id}?v=…`, an `http(s)` URL, or null. |
| `masterUserId` | GUID | The main account it belongs to. |
| `viewing` | object | Profiles in the caller's household with [viewing hours](#viewing-hours) or a limit on. The `GET /limits/{profileId}` answer, with `rules` null. Otherwise null. |

`enabledFolders`, the tag lists and `allowedDeviceIds` are empty for profiles in another
household. PINs are stored hashed and never returned.

Sent with an `ETag`: see [Caching](#caching).

**The rating limit isn't in this list.** Read it from Jellyfin's `GET /Users/{id}`, as
`Policy.MaxParentalRating`. That's the limit in effect, which is the stricter of the
profile's and its main account's. Send it back on every `/update`, or the profile's limit
is removed.

Errors: `401`, and `404` if the caller's main account no longer exists.

### Me (`me` in `/startup`)

Who the caller is: a profile or a main account, and whose. It is the `me` part of
[`GET /startup`](#get-pluginsprofilesstartup), with the same authorization.

```json
{
  "userId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b",
  "name": "Kids",
  "isProfile": true,
  "mainAccountId": "8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a",
  "mainAccountName": "John",
  "isAdministrator": false,
  "mainHasPin": true,
  "profileCount": 2,
  "maxProfiles": 5,
  "pinResetByEmail": false
}
```

| Field | Type | Description |
| --- | --- | --- |
| `userId` | GUID | The signed-in Jellyfin user. |
| `name` | string | The profile's name, or the account's username. |
| `isProfile` | boolean | A profile, rather than a main account. |
| `mainAccountId` | GUID | The household's main account. The caller's own id when it is one. |
| `mainAccountName` | string | Its username. |
| `isAdministrator` | boolean | The caller is a Jellyfin administrator. |
| `mainHasPin` | boolean | The main account has a PIN. |
| `profileCount` | integer | Profiles in the household, not counting the main account. |
| `maxProfiles` | integer | How many it may have. |
| `pinResetByEmail` | boolean | This main account can [reset its PIN by email](#email) now. Always `false` for a profile. |

Errors: `401`.

### `POST /plugins/profiles/switch`

**Authorization:** signed-in user. The target must be in the caller's household or Bonfire.

```json
{ "profileId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b", "pin": "1234" }
```

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| `profileId` | GUID | Yes | The profile to switch to. |
| `pin` | string | When `requiresPin` | The profile's PIN. |
| `endPreviousSession` | bool | No | Sign out the token that made this request once the new one exists. Default `false`. If the switch fails, nothing is signed out. |

Response:

```json
{
  "activeProfileToken": "7ef4a378297b470183b0b3e6cda7670e",
  "jellyfinUserId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b",
  "libraryArtwork": []
}
```

| Field | Type | Description |
| --- | --- | --- |
| `activeProfileToken` | string | A Jellyfin token for the profile. |
| `jellyfinUserId` | GUID | The profile's user id. |
| `libraryArtwork` | array | The profile's library artwork, as `GET /library-artwork` returns it, so it can be applied before the page reloads. |

Before the token is made, the main account's restrictions are applied to the profile again:
libraries, rating, tags, schedules, bitrate and the rest. Language and subtitle settings
are left alone; they belong to the profile once it exists. The same thing happens when a
profile signs in on another app.

**Another household's account** needs its PIN, even on the home network, and one with no
PIN can't be entered. Both rules are lifted on the home network when that account's owner
has turned on `allowHouseholdLanBypass`. `requiresPin` in `/list` already reflects this.

Errors:

- `400`: wrong PIN, not allowed on this device, an unprotected account in another
  household, an account turned off in **Dashboard → Users**, or no session could be made
  (device not allowed, or too many sessions).
- `401`: the caller's own token is invalid. Sign the caller in again.
- `403`: the target isn't theirs to open.
- `404`: the profile doesn't exist.
- `429`: too many wrong PINs.

### `POST /plugins/profiles/verify-pin`

**Authorization:** signed-in user.

Checks a profile's PIN without switching.

```json
{ "profileId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b", "pin": "1234" }
```

Returns `200` when it's correct. Errors: `400` wrong PIN or not allowed on this device,
`401`, `403` not the caller's profile, `429`.

This answers "may I open this profile", so it returns `200` without checking the PIN
wherever opening needs none. To check the main account's PIN before managing the
household, use `/account/verify-pin`.

### `POST /plugins/profiles/account/verify-pin`

**Authorization:** signed-in user. A main account, not one of its profiles.

Checks the main account's PIN, always: the home network and `askPinToOpen` don't skip it.

```json
{ "pin": "1234" }
```

Returns `200` when it's correct, or when the account has no PIN. Errors: `400` with
`pin_wrong`, `pin_missing` or `main_pin_required`, `401`, `429`.

### `POST /plugins/profiles/account`

**Authorization:** signed-in user. A main account, not one of its profiles.

The main account's own settings.

```json
{ "masterPin": "1234", "pin": "5678", "askPinToOpen": false }
```

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| `masterPin` | string | When the account has a PIN | The current PIN. |
| `pin` | string | No | A new PIN, 4 to 8 digits. `""` clears it, refused while the server requires main PINs. Omit to keep it. |
| `askPinToOpen` | boolean | No | Ask for the PIN to open the account. Omit to keep it. |
| `bypassPinOnLocalNetwork` | boolean | No | Skip the PIN on the home network. Omit to keep it. |
| `lockoutMinutes` | integer | No | Minutes before the account locks. `0` is never. Clamped to 0–1440. Omit to keep it. |
| `timeZone` | string | No | The household's time zone, an IANA id such as `Europe/Paris`, for [viewing hours](#viewing-hours). `""` follows the server default. Omit to keep it. |

Response: `{ "hasPin": true, "askPinToOpen": false, "bypassPinOnLocalNetwork": false, "seenOnLocalNetwork": false, "lockoutMinutes": 5, "timeZone": "" }`.

An account with no PIN can set one without `masterPin`, even while the server requires
it.

Errors: `400` for a bad PIN, `pin_wrong`, `pin_missing` or `main_pin_required`; `401`;
`429`.

### `POST /plugins/profiles/create`

**Authorization:** signed-in user. A main account, not one of its profiles.

```json
{
  "profileName": "Kids",
  "pin": "4321",
  "avatarColor": "#EC4899",
  "maxParentalRating": "6",
  "enabledFolders": ["e67b2d5a39cb400ba45a7b0a70198de7"],
  "blockedTags": ["adults"],
  "allowedTags": [],
  "lockoutMinutes": 5,
  "masterPin": "1234",
  "bypassPinOnLocalNetwork": false,
  "allowedDeviceIds": [],
  "profileImage": null
}
```

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| `profileName` | string | Yes | Display name. |
| `pin` | string | No | 4 to 8 digits. Omit for no PIN. |
| `avatarColor` | string | No | Hex color. Left out, it's the first color from Bonfire's palette that nobody in the household has. |
| `transparentAvatar` | boolean | No | Don't paint the color behind the image. |
| `theme` | string | No | A [theme](#themes) id. Omit or `""` to follow the server default. Ignored while the server doesn't let profiles choose. |
| `maxParentalRating` | string | No | A rating score, such as `"6"`, `"10"`, `"14"` or `"17"`. Never above the main account's. `""` or omitted is no limit of its own. Anything else is `400`. |
| `enabledFolders` | GUID[] | No | The libraries it may see. `[]` is none. |
| `allLibraries` | boolean | No | `true` for every library the main account has, including ones added later. Not with `enabledFolders`. On `/create`, leaving out both means the same. |
| `blockedTags` | string[] | No | Tags it must not see, on top of the main account's. |
| `allowedTags` | string[] | No | If set, it sees only items with one of these tags. Untagged items are hidden too. `400` if none of them is allowed for the main account. |
| `lockoutMinutes` | integer | No | Minutes before it locks. `0` is never. Defaults to 5. |
| `masterPin` | string | When the main account has a PIN | The main account's PIN. The `X-Bonfire-Master-Pin` header works too. |
| `bypassPinOnLocalNetwork` | boolean | No | Skip the PIN on the home network. |
| `askPinToOpen` | boolean | No | `/update` on the main account only. See [`POST /account`](#post-pluginsprofilesaccount). |
| `allowedDeviceIds` | string[] | No | Devices it may be opened on. Empty means any. |
| `profileImage` | string | No | See [Uploading an image](#uploading-an-image). |
| `profileImageThumb` | string | No | The image's small version. |
| `avatarLibraryId` | string | No | An image from the avatar library. Wins over `profileImage`. |

Response: `{ "profileUserId": "…", "profileName": "Kids" }`.

Errors: `400` for a bad PIN, a taken name, the profile limit, an unknown theme, a rating
that isn't a score, both `allLibraries` and `enabledFolders`, or allowed tags the main
account doesn't allow; `main_pin_required` while the server requires main
PINs and the account has none.

### `POST /plugins/profiles/update`

**Authorization:** signed-in user.

Takes the same fields as `/create`, plus `profileId`. `profileName` is required. Every
other field left out or `null` stays as it is.

Values worth knowing:

- **`maxParentalRating`:** `""` removes the profile's own limit (the main account's still
  applies), `null` keeps it.
- **Libraries:** `allLibraries: true` gives every library, `enabledFolders` gives that
  list, and sending neither keeps them. Both at once is `400`.
- **`pin`:** `""` clears it, `null` keeps it.
- **`profileImage`:** `""` clears it, `null` keeps it.
- **`theme`:** `""` goes back to the server default, `null` keeps it. Only the main account
  may set one, with its PIN. `400` for an id the server doesn't offer or has hidden; `403`
  while the server doesn't let profiles choose. The `200` answer is
  `{ profileId, theme, stored }`: `theme` is what that profile now sees, `stored` its own
  choice or `null`. `profileName` is still required for a profile, so a theme-only save
  sends the name it already has.
- **`blockedTags`, `allowedTags`:** `[]` clears them, `null` keeps them. Ignored for the
  main account.
- **Rating sub-score:** a sub-score set in **Dashboard → Users** (TV-PG against TV-PG-V) is kept
  while the rating stays the same.

Returns `200` with `{ "profileId": "a90f11cb-…", "theme": "bonfire", "stored": null }`, whether
or not a `theme` was sent. With none, `theme` and `stored` are what the profile already had.
Errors as for `/create`, plus the `theme` ones above.

### `POST /plugins/profiles/copy-rules`

**Authorization:** signed-in user, and only a main account: a profile gets `403`.

Copies one profile's rules onto another in the same household.

```json
{ "sourceProfileId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b", "targetProfileId": "5d1c3e0a-7b0e-4f7e-9a58-0c5a1d6c2b11", "masterPin": "1234" }
```

What the target gets: the rating limit, the allowed and blocked tags, the libraries, and the
viewing limits (hours, daily and weekend time). It ends as if it had been saved on the
edit form with those values, so it never gets a higher rating than the main account's or a
library the main account can't reach, and a library another plugin manages is left as it
is. Its devices, name, image and PIN are not changed.

Both profiles have to be the caller's. A profile of another household, the main account, a
standalone user and one that doesn't exist all get the same `403 not_your_profile`. Two
identical ids are `400`. Viewing limits need a main PIN, as in `POST /limits/{profileId}`.

Returns `200`. Errors as for `/update`.

### `POST /plugins/profiles/me`

**Authorization:** signed-in user, and only a profile: a main account gets `403`.

A profile changes its own name and image without the main PIN. It takes `profileName`,
`avatarColor`, `transparentAvatar`, `profileImage`, `profileImageThumb` and `avatarLibraryId`,
which mean what they do in `/update`. Every field left out or `null` stays as it is.

There is no `profileId`: the caller is the profile. Nothing else can be sent. Rating, PIN,
libraries, tags, limits, devices and theme are not read from this body, and a field that
names one is ignored. A refused change, such as a name that is taken, changes nothing.

Returns `200`. Errors as for `/update`.

### `POST /plugins/profiles/delete`

**Authorization:** signed-in user.

Deletes a profile and its Jellyfin account.

```json
{ "profileId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b", "masterPin": "1234", "password": "optional" }
```

A profile made from an existing account (`deleteWithProfile` false) is not deleted: the
account stays and signs in on its own with `password`, or a random one if that's empty.

Returns `200`. `403 not_your_profile` for a profile that isn't in your household, whether or
not it exists. `500` if the account couldn't be deleted; it's disabled instead, so it can't
be signed in to, and the profile stays listed so you can try again.

### `GET /plugins/profiles/form-options`

**Authorization:** signed-in user.

Everything the profile form reads that is the same for every profile, in one request.

```json
{
  "libraries": [{ "id": "e67b2d5a39cb400ba45a7b0a70198de7", "name": "Movies", "collectionType": "movies" }],
  "ratings": {
    "country": "GB",
    "ratings": [
      { "score": 0, "labels": ["0+", "All", "E", "G", "U"] },
      { "score": 12, "labels": ["12A", "12PG"] },
      { "score": 15, "labels": ["15"] }
    ]
  },
  "avatars": {
    "allowCustomUploads": true,
    "starterAvatarsEnabled": true,
    "image": { "quality": "high", "masterSize": 1024, "thumbSize": 256, "jpegQuality": 0.9, "maxBytes": 4194304 },
    "avatars": [
      { "id": "9f2c41a0b7d3", "displayName": "Fox", "url": "/plugins/profiles/avatars/9f2c41a0b7d3", "thumbUrl": "/plugins/profiles/avatars/9f2c41a0b7d3?size=thumb", "isStarter": false }
    ]
  },
  "timeZones": {
    "zones": ["Africa/Abidjan", "…"],
    "server": "Europe/London",
    "default": "",
    "household": "Europe/Paris",
    "effective": "Europe/Paris"
  }
}
```

| Part | Description |
| --- | --- |
| `libraries` | The libraries the caller can see, which are the ones it can give a profile. |
| `ratings` | The ratings of the server's **Preferred metadata country**, one entry per age, for a profile's rating limit. `ratings` inside it is empty if Jellyfin has no table for the country. |
| `avatars` | The [avatar library](#avatar-library). |
| `timeZones` | The time zones this server knows. `default` and `household` are empty when not set. `effective` is the zone in use. |

Send a rating's `score` as `maxParentalRating` to [`/create`](#post-pluginsprofilescreate) or
[`/update`](#post-pluginsprofilesupdate). Adult and banned ratings aren't listed; leave the
limit out for those. Unlike `/Localization/ParentalRatings`, this doesn't add Jellyfin's
generic ages (`10`, `13`, `14`, `21`) to countries that don't have them.

An administrator's API key is answered with `timeZones` only: the other three parts are
`null`. `404` if the calling user isn't found.

### How restrictions reach a profile

A profile is a hidden Jellyfin account. Every time it's opened, through the switcher or by
signing in on another app, Bonfire writes its policy from the main account's. A change made
to the main account in **Dashboard → Users** reaches a profile the next time it's opened; a
session already running keeps what it had until then.

- **Copied:** libraries, rating, tags, remote access, playback, downloads, Live TV,
  channels, allowed devices and schedules.
- **The stricter of the two:** bitrate, session count, forced transcoding, SyncPlay and
  blocked unrated items. A limit set on the profile's own account in **Dashboard → Users**
  counts as the profile's. Everything else follows the main account, both tighter and
  looser.
- **Never more than the main account:** management permissions. A profile is never an
  administrator.

A library granted to or removed from a profile in **Dashboard → Users** is kept: Bonfire
records the list it last wrote (`LastAppliedFolders`), and a list that differs from it was
changed by someone. A reset to "all libraries" is still repaired. A profile set to every
library always follows its main account.

If none of a profile's allowed tags is still allowed for its main account, it's given a
placeholder tag that no item carries, so it sees nothing until it's edited.

---

## Sign-in from other apps

These settings under **Apps** change Jellyfin's own routes, for apps that never load
the web client. All are off by default.

| Setting | What changes |
| --- | --- |
| `enableClientPinLogin` | Profiles, and main accounts with a PIN, can be signed in to with the PIN where an app asks for a password. Only on a device the household has signed in on with a password; elsewhere a main account needs its password. |
| `enableClientProfileList` | `GET /Users/Public` includes the household of the device asking, and profiles show under their own name. |
| `clientPinLoginFromAnywhere` | A profile with no PIN, or with a device list, can be signed in to from outside the home network. Off: those two open only for a caller Jellyfin places on the local network, after its known-proxy rules. A profile with a PIN and no device list is not affected. Under Advanced in **Dashboard → Plugins → Bonfire → Clients**. |
| `skipQuickConnectOnKnownDevices` | `POST /QuickConnect/Initiate` is refused on a TV a household has used, so the app shows its password box. |
| `proxyClients` | The apps that get the profile picker. See below. |

### `GET /Users/Public`

The household is found from the `DeviceId` in the request, against the devices Bonfire has
seen a sign-in on. Profiles are added to Jellyfin's list; nothing is removed. An
unrecognized device gets Jellyfin's answer unchanged. Device limits apply.

Android TV and Jellyfin for Android send a device id derived from the signed-in user. Both
forms are recognized.

With `enableClientPinLogin` on, a request with no token from a Roku a household uses also
signs out that Roku's saved sign-ins for the household's profiles that have a PIN. The Roku
app then asks for the PIN instead of reusing them. This happens whether or not
`enableClientProfileList` is on. A profile whose owner skips its PIN on the home network
keeps its saved sign-in when the request is local.

### `GET /Users/Me` and `POST /Users/AuthenticateByName`

A profile's `Name` is reported as the name its household gave it, `kids` rather than
`Bard_kids`. The account isn't renamed.

### `POST /QuickConnect/Initiate`

Refused with `401 "Quick connect is disabled"` on an Android TV whose name a household has
signed in with. The first sign-in on a TV is never refused. A TV name that someone outside
every household has also used is never refused either, since two sets of the same model
look the same.

Bonfire keeps those names itself, as `QuickConnectSharedDeviceNames` in its configuration
(and so in an export). It adds a name when an account outside every household signs in on an
Android TV, and keeps the newest 500. It is not a setting: there is nothing to edit, and an
import brings the list along.

### Signing in with a PIN

- **A profile** opens with its PIN. A profile with no PIN opens with an empty password,
  but only on a device its household has signed in on.
- **The username** can be the full one (`Bard_kids`) from anywhere, or the profile's own
  name (`kids`) on a device its household has signed in on.
- **A main account** keeps its real password; the PIN works as well.
- **Every refusal** returns the same message, so nobody can tell which accounts are
  profiles.

### The profile picker (beta)

For an app listed in `proxyClients` (only `moonfin` so far, matched on the `Client` the app
sends), signed in as a main account that has profiles:

- **Until a profile is picked**, `/UserViews` returns one library, `Profiles`, with a folder
  per profile. Resume, Next Up, Latest and search are empty. Everything else is answered as
  the main account.
- **Opening a profile's folder** (`GET /Users/{id}/Items?ParentId=…`) signs into it. A
  profile with a PIN opens a keypad of folders first. Back on the keypad steps back one
  key press at a time, because each press is a page in the app. The tries count against
  the same limit as a PIN typed into a sign-in form. While the server is
  [closed for maintenance](#maintenance), a profile that isn't an administrator's doesn't
  open and the folder shows the administrator's message.
- **After that**, every request on the main account's token reaches Jellyfin as the
  profile: its token, and its id in place of the main account's. `/UserViews` adds a
  `Switch profile` folder, which signs the profile out and shows the picker again.
- **The app is always shown the main account's id**, in `/Users/Me`, `/Users/{id}` and the
  live connection. The name is the profile's.
- **Signing a profile in directly is refused** on a listed app.
- **Folders, and profiles with no image, get an image drawn by the server**: a 256×256
  PNG in the theme's colors. Its image tag changes when the theme does.

The picker is held in memory: after a server restart the app is back at the picker. An
app with its own Bonfire switcher should not be listed.

---

## Client script

The browser has to load `profiles.js` for any of the switcher to appear. Bonfire adds the
tags as `index.html` is served and never writes the file. Tags an older version wrote to
disk are removed after the first page is served.

An `index.html` with no Bonfire tags is normal. Read `mechanism` from
`GET /admin/mappings` instead. Samsung Tizen packages need the loader described in
[troubleshooting](troubleshooting.md#samsung-tizen).

### `GET /plugins/profiles/profiles.js`

**Authorization:** anonymous.

The client script, as `application/javascript`.

| Header | Value |
| --- | --- |
| `ETag` | The plugin version, with `-off` while Bonfire is turned off. |
| `Cache-Control` | `public, no-cache` |

Returns `304` when `If-None-Match` matches. Every page load asks, so a browser picks up a
new version, or Bonfire being turned off or on, on its next page load, even if the `?v=` on
its script tag is out of date. While Bonfire is turned off it is the same script, told to
draw nothing.

---

## Translations

English is built into `profiles.js`. Other languages are one JSON file each in
`Web/i18n/`. Jellyfin's Display language for the signed-in user decides which file is
fetched, and English fetches nothing. If a file is missing or fails, the text stays English.

To add a language, see [the translation guide](../Web/i18n/README.md).

### `GET /plugins/profiles/i18n/{locale}`

**Authorization:** anonymous.

One language's strings, as a flat object. `{locale}` may end in `.json`.

```json
{ "gate.whosWatching": "Qui regarde ?", "common.cancel": "Annuler" }
```

| Query | |
| --- | --- |
| `part=admin` | Only the admin settings page's strings (keys starting `admin.`). Without it, you get every other key. |

| Status | Meaning |
| --- | --- |
| `200` | The strings. |
| `304` | `If-None-Match` matched. |
| `404` | No translation for that code. |
| `503` | The file couldn't be read. Stay on English and try later. |

`ETag` is `"{pluginVersion}-{locale}"`, with `-admin` added for `part=admin`. `Cache-Control` is the same as
`profiles.js`. There's no route listing the languages. They're written into
`profiles.js` as it's served, on the line marked `__BONFIRE_LOCALES__`.

---

## Images

### `GET /plugins/profiles/image/{profileId}`

**Authorization:** anonymous.

A profile's stored image. Add `?size=thumb` for the small version. If there's no small
version, the full one is sent, so `size=thumb` is always safe; use it in grids.

Returns `404` if there's no stored image, including when the image is an `http(s)`
link. Load that link directly from `profileImage` in `/list`.

All four image routes send:

| Header | Value |
| --- | --- |
| `Cache-Control` | `private, max-age=3600` |
| `ETag` | From the file's size and modified time. |
| `Last-Modified` | The file's modified time. |

Send `If-None-Match`, or every image is downloaded again on every page load.

### Uploading an image

`profileImage` and `profileImageThumb` on `/create` and `/update` take a
`data:image/…;base64,…` value. The size limit is set by the server's image quality: 2, 4,
8 or 16 MB. The `avatars` part of `GET /form-options` reports it, and the size to draw at.

The server doesn't resize or convert anything, so the client draws both sizes itself, at
the sizes `GET /form-options` gives. JPEG, PNG, WebP and GIF are accepted; SVG isn't. `profileImage` can also be an `http(s)` URL, which is stored as a
link.

---

## Library artwork

Jellyfin draws one image per library from whatever is inside it, so a child's profile can
see a tile showing a film it can't open. These routes let a profile replace or hide that
image. The client does the swap, so it only applies where Bonfire's script runs.

### `GET /plugins/profiles/library-artwork`

**Authorization:** signed-in user.

The caller's choices. With `?profileId=<id>`, the same list for one of the caller's
profiles: `403` if it isn't theirs, or if the caller is a profile.

```json
[{ "libraryId": "f137a2dd21bbc1b99aa5c0f6bf02a805", "mode": "custom", "url": "/plugins/profiles/library-art/<profileId>/<libraryId>?v=638..." }]
```

`mode` is `custom` (use `url`) or `none` (no image). Libraries left on the default aren't
listed.

### `POST /plugins/profiles/library-artwork`

**Authorization:** signed-in user.

```json
{
  "profileId": "8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a",
  "libraryId": "f137a2dd21bbc1b99aa5c0f6bf02a805",
  "mode": "custom",
  "image": "data:image/jpeg;base64,...",
  "thumb": "data:image/jpeg;base64,...",
  "avatarLibraryId": null,
  "masterPin": "1234"
}
```

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| `profileId` | GUID | Yes | The profile. |
| `libraryId` | GUID | Yes | A library the server has. |
| `mode` | string | Yes | `inherit`, `custom` or `none`. |
| `image`, `thumb` | string | No | The image, for `custom`. |
| `avatarLibraryId` | string | No | Use an avatar-library image instead. |
| `masterPin` | string | When the main account has a PIN | The main account's PIN. |

`custom` with no image keeps the stored one, or is refused if there isn't one. Errors:
`400` unknown library, wrong PIN or bad image; `403` not the caller's profile.

### `GET /plugins/profiles/library-art/{profileId}/{libraryId}`

**Authorization:** anonymous.

The stored image for a `custom` choice. `?size=thumb` for the small one.

---

## Devices

### `GET /plugins/profiles/devices`

**Authorization:** signed-in user.

The devices the caller's household has used, for the allowed-devices picker. A profile
calling this gets its main account's devices, but only if the main account is open or the
request carries its PIN in the `X-Bonfire-Master-Pin` header. Otherwise the list is empty.

```json
[{
  "deviceId": "57bfa7e8d35f492b950bf93c9d747a11",
  "deviceName": "Chrome",
  "client": "Jellyfin Web",
  "lastSeen": "2026-06-12T09:41:46.806Z",
  "masterUserId": "8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a"
}]
```

Every device already on one of the household's allowed lists is included, even if it
hasn't been seen for months. One with no record left comes back as "Previously allowed
device" with a `lastSeen` of `0001-01-01`.

The English placeholders ("Unrecognized device", "Previously allowed device", "Not seen
recently") come with a code beside them so a client can translate: `nameCode`
(`unrecognised`, `previouslyAllowed`) and `clientCode` (`notSeenRecently`). Both are left
out for an ordinary device.

**When you save a profile, keep allowed devices you weren't shown.** An empty
`allowedDeviceIds` means any device, so dropping them removes the restriction.

### `POST /plugins/profiles/devices/delete`

**Authorization:** signed-in user.

```json
{ "deviceId": "57bfa7e8d35f492b950bf93c9d747a11" }
```

Forgets the device for your household and takes it off your profiles' device lists. Another
household that uses the same device keeps it.

`400` if your household has never used it. `400` if it's the only device one of your
profiles is allowed on, since removing it would allow every device. The message names
those profiles.

### `POST /plugins/profiles/devices/merge`

**Authorization:** signed-in user. Must be the main account and own both devices.

For one machine with two records, which happens when the same browser reaches the server by
two addresses.

```json
{ "fromDeviceId": "57bfa7e8…", "intoDeviceId": "9c1e04b7…" }
```

Profiles allowed on `fromDeviceId` become allowed on `intoDeviceId`, and the old record is
removed. `fromDeviceId` is kept as another id for the same device, so a request from it
counts as `intoDeviceId`. `400` if either is unknown, they're the same, or one isn't the
caller's.

### `POST /plugins/profiles/devices/merge-duplicates`

**Authorization:** signed-in user. Must be the main account.

Finds records that look like one machine: same client, same name, and device ids from the
same browser version. With `?apply=false`, the default, it only reports.

```json
{
  "applied": false,
  "groups": [{
    "keep": { "deviceId": "TW96aWxsY…MDAw", "deviceName": "Chrome", "client": "Jellyfin Web", "lastSeen": "2026-08-31T10:02:00Z" },
    "merge": [{ "deviceId": "TW96aWxsY…NTAw", "deviceName": "Chrome", "lastSeen": "2026-08-23T18:40:00Z" }]
  }]
}
```

With `?apply=true` it merges each group into its most recent record and returns
`{ "applied": true, "merged": 3, "groups": 1 }`.

### `POST /plugins/profiles/devices/rename`

**Authorization:** signed-in user. Must be the main account and own the device.

```json
{ "deviceId": "57bfa7e8d35f492b950bf93c9d747a11", "deviceName": "Living room TV" }
```

The name is kept even when the device reports a different one. Trimmed to 64 characters.
`400` for an unknown device or a blank name.

---

## Bonfire

### `GET /plugins/profiles/bonfire/status`

**Authorization:** signed-in user.

```json
{
  "isOwner": true,
  "ownedCode": "B7F8XA",
  "ownedMembers": [{ "userId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b", "username": "FriendMaster" }],
  "isMember": false,
  "joinedOwnerName": null,
  "joinedOwnerId": null,
  "hideMySubProfilesFromOthers": false,
  "hideOthersSubProfilesFromMe": false,
  "allowHouseholdLanBypass": false,
  "isAdministrator": true,
  "hasPin": true
}
```

| Field | Description |
| --- | --- |
| `isOwner`, `ownedCode`, `ownedMembers` | The Bonfire the caller started, if any. |
| `isMember`, `joinedOwnerName`, `joinedOwnerId` | The Bonfire the caller joined, if any. |
| `hideMySubProfilesFromOthers` | Others in the Bonfire can't see or open the caller's profiles. |
| `hideOthersSubProfilesFromMe` | The caller doesn't see others' profiles. |
| `allowHouseholdLanBypass` | Others may open this account on the home network without its PIN. |
| `isAdministrator` | The caller is a Jellyfin administrator, for warning before turning the bypass on. |
| `hasPin` | The caller's main account has a PIN. |

### `POST /plugins/profiles/bonfire/settings`

**Authorization:** signed-in user.

```json
{ "hideMySubProfilesFromOthers": false, "hideOthersSubProfilesFromMe": false, "allowHouseholdLanBypass": false }
```

A field left out keeps its value.

Before turning the bypass on, a client should warn about two things:

- **An administrator account** gives anyone who opens it control of the server.
- **"Home network"** is whatever Jellyfin counts as local. Behind a reverse proxy missing
  from **Dashboard → Networking → Known Proxies**, that's everyone.

### `POST /plugins/profiles/bonfire/generate`

**Authorization:** signed-in user.

Starts a Bonfire. Returns `{ "groupId": "4f5c9e2b", "bonfireCode": "B7F8XA", "members": [] }`.
Each entry of `members` is `{ "userId": "a90f11cb-…", "username": "FriendMaster" }`, the same
as in `ownedMembers` of [`GET /bonfire/status`](#get-pluginsprofilesbonfirestatus).
`username` is `Unknown User` for an account that no longer exists. A new Bonfire has none.

If the caller already has a Bonfire, this changes its code instead and removes no one; the old
code stops working. The answer has the same shape, with the new `bonfireCode`.

### `POST /plugins/profiles/bonfire/join`

**Authorization:** signed-in user.

```json
{ "code": "B7F8XA" }
```

Returns `{ "message": "Successfully joined Bonfire group.", "ownerName": "FriendMaster" }`.
Joining leaves any Bonfire the caller was already in. Errors: `400` bad code or your own
Bonfire, `403` not a main account, `429` too many tries.

### `POST /plugins/profiles/bonfire/kick`

**Authorization:** signed-in user.

```json
{ "memberId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b" }
```

Removes the member and changes the code so they can't rejoin. Returns the new code:
`{ "bonfireCode": "Q7M2KX" }`.

### `POST /plugins/profiles/bonfire/leave`

**Authorization:** signed-in user.

Leaves the Bonfire the caller joined.

### `POST /plugins/profiles/bonfire/delete-group`

**Authorization:** signed-in user.

Ends the caller's Bonfire and removes everyone from it.

---

## Other accounts

A main account can show another main account on its switcher. The tile opens that
account itself, not its profiles. Adding needs the other account's password, typed once
and never stored, or an administrator. Nothing about the added account changes, and it
sees nothing of the switcher it's on.

- Opening it asks for its own Bonfire PIN if it has one, otherwise nothing. Its owner's
  home-network setting applies.
- An administrator account must have a PIN, and it's always asked.
- An account with no password can't be added by the household. An administrator adds it.
- A switcher shows up to 10 other accounts. One account can be on many.
- Either side can take it off.
- The [profile picker](#the-profile-picker-beta) doesn't show added accounts.

These routes are for main accounts, and take the main account's PIN (see
[The main account's PIN](#the-main-accounts-pin)).

### `GET /plugins/profiles/accounts/links`

**Authorization:** signed-in user, as a main account.

```json
{
  "added": [{ "userId": "…", "name": "grandma", "hasPin": true, "byAdministrator": false, "linkedUtc": "2026-11-04T18:00:00Z" }],
  "shownIn": [{ "userId": "…", "name": "john", "hasPin": false, "byAdministrator": true, "linkedUtc": "2026-11-01T09:12:00Z" }]
}
```

`added` is on the caller's switcher. `shownIn` lists the switchers the caller is on.

### `POST /plugins/profiles/accounts/links/add`

**Authorization:** signed-in user, as a main account.

```json
{ "username": "grandma", "password": "…", "masterPin": "1234" }
```

Returns the `GET` shape. A wrong password counts against the same limits as a wrong PIN,
but not against Jellyfin's lockout for that account, so nobody can get an account turned
off this way. An account that signs in through LDAP, SSO or similar can only be added by an
administrator. Errors: `credentials_wrong`, `not_linkable`,
`already_linked`, `link_limit`, `admin_pin_required`, `429`.

### `POST /plugins/profiles/accounts/links/remove`

**Authorization:** signed-in user, as a main account.

```json
{ "userId": "…", "masterPin": "1234" }
```

Send `userId` to take an account off the caller's switcher, or `householdId` to take the
caller off someone else's. Returns the `GET` shape. `link_not_found` otherwise.

---

## Preferences

Each household chooses these for itself. A profile reads its main account's, and only the
main account can change them.

### `GET /plugins/profiles/preferences`

**Authorization:** signed-in user.

```json
{
  "askOnStartup": true,
  "switcherLocation": "button",
  "masterUserId": "8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a",
  "emergencyCodeConfigured": false,
  "theme": "crimson",
  "defaultTheme": "bonfire",
  "allowProfileThemes": true,
  "richThemeMotion": false,
  "onboardingPending": false
}
```

| Field | Description |
| --- | --- |
| `askOnStartup` | Show "Who's watching?" once per browser session. |
| `switcherLocation` | `button`: a Bonfire button in the header. `menu`: a **Switch Profile** entry in Jellyfin's user menu. |
| `masterUserId` | The account these belong to, for caching. |
| `emergencyCodeConfigured` | An administrator has set an emergency code. Only then offer the emergency option. |
| `theme` | The [theme](#themes) to show **this** account, from its own choice or the server default. Unlike the fields above, it isn't the main account's. |
| `defaultTheme` | The server default. |
| `allowProfileThemes` | Profiles may choose their own theme. When `false`, `theme` is always the default. |
| `richThemeMotion` | Themes with a special animation may use it. Never on a television too slow to blur, or when the device asks for less motion. |
| `onboardingPending` | This account should be shown [first-time setup](#first-time-setup). Always `false` for a profile. |

An account that hasn't chosen gets the server's defaults. Unknown stored values come back
as the defaults.

### `POST /plugins/profiles/preferences`

**Authorization:** signed-in user.

```json
{ "askOnStartup": true, "switcherLocation": "menu" }
```

Both are optional. Returns the stored values in the `GET` shape; cache those rather than
what you sent. `403` for a profile.

## First-time setup

A few screens a main account sees the first time it opens Bonfire, if the administrator
turns them on. Once per account, not per device. Accounts that existed when it was first
turned on count as having seen it.

### Onboarding (`onboarding` in `/startup`)

The `onboarding` part of [`GET /startup`](#get-pluginsprofilesstartup), with the same
authorization.

```json
{
  "pending": true,
  "generation": 1,
  "steps": [
    { "id": "welcome", "done": false },
    { "id": "mainPin", "done": false },
    { "id": "firstProfile", "done": true },
    { "id": "switcher", "done": false },
    { "id": "theme", "done": false }
  ]
}
```

| Field | Description |
| --- | --- |
| `pending` | Show it now. `false` for a profile, when it's off, or once seen. |
| `generation` | Changes when the administrator shows it again. |
| `steps` | In order. Empty when not pending. A step whose feature is off isn't listed: `theme` needs profiles to be allowed a theme. |
| `steps[].done` | Already true: a PIN is set (`mainPin`) or a profile exists (`firstProfile`). |

Each step changes things through the usual routes: [`/account`](#post-pluginsprofilesaccount)
for the PIN, [`/create`](#post-pluginsprofilescreate), [`/preferences`](#post-pluginsprofilespreferences)
and [`/update`](#post-pluginsprofilesupdate) with `theme`.

### `POST /plugins/profiles/onboarding/done`

**Authorization:** signed-in user.

No body. Records that this account has seen the current setup, finished or skipped. A
profile gets `200` and nothing is recorded.

### `POST /plugins/profiles/admin/onboarding/show-again`

**Authorization:** administrator.

Every main account sees first-time setup again on its next visit. Returns
`{ "generation": 2 }`.

### Themes

How Bonfire's own screens look: the profile picker, PIN entry, the profile forms,
Settings and the Bonfire menu. The rest of Jellyfin keeps its own theme. Theme names are
proper names and aren't translated.

| Id | Look |
| --- | --- |
| `bonfire` | The default. Night blue with a warm glow, amber, round tiles. |
| `crimson` | A dark theater. Deep red, gold, a serif. |
| `starlight` | Navy with a few stars, round tiles with a white ring. |
| `harbor` | A harbor at dusk. Blue-gray, bold type, slate buttons. |
| `nightfall` | Black into purple with a rose glow, gradient rings, pill buttons. |
| `glass` | Soft colored blur, small round tiles, frosted buttons. |
| `harvest` | Autumn. Brown into amber with drifting leaves, leaf-shaped tiles, a serif. |
| `snowfall` | Winter. Night blue with falling snow, frosted rings, white outlined buttons. |
| `bloom` | Spring. Fresh green with blossoms, soft rounded tiles, pink highlights. |
| `jellyfin-dark` | Jellyfin's own dark look. |
| `jellyfin-light` | Jellyfin's own light look. |
| `high-contrast` | Yellow on black, large type. |

A theme is per profile. Set it with `theme` on [`/create`](#post-pluginsprofilescreate) or
[`/update`](#post-pluginsprofilesupdate), which a profile's theme can be the only field
of; read the one to show from
[`/preferences`](#get-pluginsprofilespreferences). The administrator sets the default and
whether profiles may choose, under [`admin/settings`](#post-pluginsprofilesadminsettings).

An administrator can also hide built-in themes and add their own; see
[Custom themes](#custom-themes). A profile that chose a hidden or deleted theme sees the
default, and gets its choice back if the theme returns.

### `GET /plugins/profiles/themes`

**Authorization:** anonymous.

Every theme as a file, for an app that draws its own switcher. The file format is in
[Themes](themes.md).

Each entry of `themes` is a [theme file](themes.md#the-file), as stored. Hidden built-ins are
left out of `themes` and named in `hidden`. Custom themes come after
the built-ins, and uploaded fonts after the built-in fonts. `?part=custom` returns only
the custom themes and uploaded fonts, for a client that already has the built-ins.

```json
{
  "schema": 1,
  "default": "bonfire",
  "profilesCanChoose": true,
  "hidden": [ "glass" ],
  "themes": [ { "schema": 1, "id": "harvest", "revision": 1, "…": "…" } ],
  "fonts": [
    { "family": "Noto Sans", "fallback": "sans-serif", "licence": "/plugins/profiles/themes/fonts/noto-sans-OFL.txt",
      "files": [ { "subset": "latin", "url": "/plugins/profiles/themes/fonts/noto-sans-latin.woff2", "format": "woff2",
                   "style": "normal", "weight": [100, 900], "unicodeRange": "U+0000-00FF,…" } ] },
    { "family": "Georgia", "fallback": "serif", "files": [] },
    { "family": "Shop Sign", "fallback": "sans-serif", "custom": true,
      "files": [ { "subset": null, "url": "/plugins/profiles/themes/fonts/3f9c…e1.woff2", "format": "woff2",
                   "style": "normal", "weight": [400, 400], "unicodeRange": null } ] }
  ],
  "patterns": [ { "name": "leaves", "url": "/plugins/profiles/themes/patterns/leaves" } ],
  "logo": { "image": "/plugins/profiles/themes/logo/9be1…04.png", "text": null, "alt": "The Hayes House", "corners": "rounded" }
}
```

`logo` is shown in the corner the theme's `layout.logo` names: the `image` when there is one,
otherwise `text`, otherwise nothing. `alt` is for screen readers. `corners` is `rounded` or
`square`: the corners of the image and of the backing behind a see-through one. A server that
never set it says `rounded`.

Sent with an `ETag`. Send it back as `If-None-Match` and you get `304` until something
changes, including the server's default theme.

### `GET /plugins/profiles/themes/patterns/{name}`

**Authorization:** anonymous.

One built-in pattern image, as SVG. `name` is a decoration's `image`; the `url` in
[`/themes`](#get-pluginsprofilesthemes) points here. `404` for a name Bonfire doesn't hold,
which includes an uploaded image's name: those are at
[`pattern-files`](#get-pluginsprofilesthemespattern-filesfile).

### `GET /plugins/profiles/themes/pattern-files/{file}`

**Authorization:** anonymous.

A pattern image an administrator uploaded, by the name a `pattern` decoration's `image`
gives (a hash and `.png` or `.webp`). Served as that image with an `ETag` (`304` to a
matching `If-None-Match`) and cached for good. `404` for any name that is not an upload.

### `POST /plugins/profiles/admin/themes/patterns`

**Authorization:** administrator.

The body is the image: PNG or WebP, up to 2 MB, 16 to 1024 pixels on each side. Not SVG,
JPEG, GIF, an animated WebP or an APNG. Only stores it: a custom theme names it in a
`pattern` decoration's `image` when it is saved ([docs/themes.md](themes.md#pattern-images)).
Returns `{ "file": "9be1…04.png", "url": "/plugins/profiles/themes/pattern-files/9be1…04.png", "width": 240, "height": 240 }`.

Saving a theme with another image or none, or deleting it, deletes the old file once no
custom theme names it. An upload that is never saved is removed by
[`cleanup-orphans`](#post-pluginsprofilesadmincleanup-orphans).

### `GET /plugins/profiles/themes/fonts/{file}`

**Authorization:** anonymous.

One font file (`font/woff2`), or the license that comes with it (`text/plain`). The `url`
and `licence` in [`/themes`](#get-pluginsprofilesthemes) point here. Each font is a
variable font covering its `weight` range, in one file per `subset`; use the file whose
`unicodeRange` holds the character. Every built-in font is under the SIL Open Font License.
An uploaded font is one file with no subset; use it for everything. Sent with
`Access-Control-Allow-Origin: *` and an `ETag`. `404` for a file Bonfire doesn't hold.

### Custom themes

An administrator can add up to 40 themes of their own and hide any built-in but Bonfire.
A custom theme is a theme file, schema 1, checked by the same rules as the built-ins (see
[Themes](themes.md), "Rules every theme passes") except contrast, which only the
built-ins are held to, plus three of its own: its `id` can't be one a built-in uses, its
name is `name.text`, and it has no `aliases`. It may use the built-in fonts and any
uploaded font.

A problem is `{ "path": "colors.panel", "code": "range", "detail": "…" }`. `detail` is
English; show your own text by `code`.

### `GET /plugins/profiles/admin/themes`

**Authorization:** administrator.

```json
{
  "default": "bonfire",
  "profilesCanChoose": true,
  "maxCustomThemes": 40,
  "maxCustomFonts": 20,
  "maxFontBytes": 2097152,
  "builtIn": [ { "id": "bonfire", "hidden": false }, { "id": "glass", "hidden": true } ],
  "custom": [ { "id": "shop", "theme": { "schema": 1, "id": "shop", "…": "…" }, "problems": [] } ],
  "fonts": [ { "family": "Shop Sign", "file": "3f9c…e1.woff2", "url": "/plugins/profiles/themes/fonts/3f9c…e1.woff2",
               "weightMin": 400, "weightMax": 400, "fallback": "sans-serif", "usedBy": [ "shop" ] } ],
  "builtInFonts": [ "Noto Sans", "…" ]
}
```

Every custom theme is listed as a [theme file](themes.md#the-file), with its problems. One with problems isn't offered
anywhere until it's fixed.

### `POST /plugins/profiles/admin/themes/validate`

**Authorization:** administrator.

The body is a [theme file](themes.md#the-file), at most 64 KB. Returns `{ "problems": [ … ] }`,
empty when it passes. Saves nothing. Unlike a save, it needs the file's `revision`, and it
reports a key the format doesn't have as `unknown`. A body that isn't JSON comes back as a
problem too, with `200`.

### `PUT /plugins/profiles/admin/themes/custom/{id}`

**Authorization:** administrator.

The body is a [theme file](themes.md#the-file), at most 64 KB, whose `id` is `{id}`. Adds it, or replaces the custom theme with
that id. Returns `{ "id": "shop", "created": true }`. `400` with
`{ "message": "…", "problems": [ … ] }` when it doesn't pass, and `400` when 40 are
already stored.

A theme's `revision` goes up on every save. Send one above the stored revision, which is
what the dashboard does. An edit whose revision is not above it was made from an older copy
and gets `409 theme_changed`, leaving the newer save as it is. A file with no `revision` is
still accepted and takes the next one.

### `DELETE /plugins/profiles/admin/themes/custom/{id}`

**Authorization:** administrator.

Deletes a custom theme. `404` if there's none with that id, `400` if it's the default.

### `PUT /plugins/profiles/admin/themes/hidden`

**Authorization:** administrator.

```json
{ "hidden": [ "glass", "starlight" ] }
```

Replaces the list. Built-in ids only, never `bonfire` or the default. Returns the list
as stored.

### `POST /plugins/profiles/admin/themes/fonts`

**Authorization:** administrator.

The body is the font file itself: WOFF2, at most 2 MB, able to draw A–Z, a–z and 0–9.

| Query | Required | Description |
| --- | --- | --- |
| `rights` | Yes | `true`: the administrator has the right to use the font. |
| `fallback` | No | `sans-serif` (the default) or `serif`. |
| `family` | No | The name to use. Letters, digits, spaces, `-`, `_` and `.`, up to 64. Defaults to the name in the file. |

The file is stored under a name made from its contents. Uploading the same file again
returns the font already there. Returns the font as listed by
[`admin/themes`](#get-pluginsprofilesadminthemes). Up to 20 fonts.

### `DELETE /plugins/profiles/admin/themes/fonts/{file}`

**Authorization:** administrator.

Deletes an uploaded font and its file. `400` while a custom theme uses it.

### `PUT /plugins/profiles/admin/themes/logo`

**Authorization:** administrator.

```json
{ "text": "The Hayes House", "alt": "The Hayes House", "corners": "square" }
```

A field left out stays as it is. Send `""` or `null` to clear `text` or `alt`; anything that is not text is `400`. Each is one line: `text` up to 40
characters, `alt` up to 100. `corners` is `rounded` or `square`; any other value is `400`. Returns the logo as [`/themes`](#get-pluginsprofilesthemes) gives it.

### `POST /plugins/profiles/admin/themes/logo/image`

**Authorization:** administrator.

The body is the image: PNG, JPEG or WebP, up to 1 MB, 16 to 4096 pixels on each side. Not
SVG or GIF. Replaces the current image. Returns `{ "image": "…", "width": 400, "height": 120 }`.

### `DELETE /plugins/profiles/admin/themes/logo/image`

**Authorization:** administrator.

Removes the image. The text shows instead, if there is one.

### `GET /plugins/profiles/themes/logo/{file}`

**Authorization:** anonymous.

The logo image. The address changes whenever the image does, so it can be cached for good.
`404` for anything but the current logo.

### `POST /plugins/profiles/admin/themes/backgrounds`

**Authorization:** administrator.

The body is the image: PNG, JPEG or WebP, up to 2 MB, 320 to 4096 pixels on each side. Not
SVG, GIF or an animated WebP. Only stores it: a custom theme names it in
`background.image.file` when it is saved ([docs/themes.md](themes.md#background-image)).
Returns `{ "file": "9be1…04.jpg", "url": "/plugins/profiles/themes/backgrounds/9be1…04.jpg", "width": 1920, "height": 1080 }`.

Saving a theme with another image or none, or deleting it, deletes the old file once no
custom theme names it. An upload that is never saved is removed by
[`cleanup-orphans`](#post-pluginsprofilesadmincleanup-orphans).

### `GET /plugins/profiles/themes/backgrounds/{file}`

**Authorization:** anonymous.

A background image, by the name `background.image.file` gives. Named by its contents, so it
can be cached for good. `404` for any name that is not one an upload stored.

---

## Email

Bonfire can send mail: a code to confirm a main account's address, a code to reset a
forgotten main PIN, and notices the account asks for. It needs the administrator to set up
outgoing mail and send a test first. Until a test goes through, `features.email` is
`false` and every route below that needs it answers `400`.

Codes are six digits, work for 15 minutes, and allow five tries. They're kept in memory, so
a server restart means sending a new one. Mail is in the language the client passes as
`language` (a tag such as `fr`), or English.

### `GET /plugins/profiles/account/email`

**Authorization:** signed-in user. The main account only, with its PIN in
`X-Bonfire-Master-Pin` if it has one.

```json
{
  "available": true,
  "address": "parent@example.com",
  "pendingAddress": "",
  "notifyLockout": true,
  "notifyNewDevice": true,
  "notifyTimeAsked": true,
  "notifyLimitReached": false,
  "pinResetEnabled": true
}
```

| Field | Description |
| --- | --- |
| `available` | Email works on this server. |
| `address` | The confirmed address, or empty. |
| `pendingAddress` | An address waiting for its code, or empty. |
| `notifyLockout` | Mail when a PIN in the household is locked by wrong tries. |
| `notifyNewDevice` | Mail when the household signs in on a new device. |
| `notifyTimeAsked` | Mail when a profile asks for more time. |
| `notifyLimitReached` | Mail when a profile uses up its daily limit. |
| `pinResetEnabled` | The administrator allows PIN reset by email. |

### `POST /plugins/profiles/account/email`

**Authorization:** signed-in user. The main account only, with its PIN.

```json
{ "address": "parent@example.com", "language": "en", "masterPin": "1234" }
```

The main account's email settings in one call. All fields but `masterPin` are optional, and
at least one of `address` and the four switches is needed (`400` otherwise). Returns the
`GET` shape.

| Field | Description |
| --- | --- |
| `address` | A new address: a code is sent to it, and nothing is stored until it's confirmed (`pendingAddress` is set). An empty string removes the address and any code waiting for one. Left out, the address is not touched. |
| `notifyLockout`, `notifyNewDevice`, `notifyTimeAsked`, `notifyLimitReached` | Which notices to get. Left out, the stored value is kept. `400` without a confirmed address, before any code is sent. Ignored when the same call removes the address. |
| `language` | The language the mail is written in. |

### `POST /plugins/profiles/account/email/confirm`

**Authorization:** signed-in user. The main account only, with its PIN.

```json
{ "code": "042917", "language": "en", "masterPin": "1234" }
```

Stores the address. `language` is kept for notices sent later. Returns the `GET` shape.

### `POST /plugins/profiles/account/pin-reset/send`

**Authorization:** signed-in user. The main account only. No PIN: this is for an account
that has forgotten it.

```json
{ "language": "en" }
```

Sends a code to the confirmed address. Returns `{ "sentTo": "p•••@example.com" }`. `400`
unless `/startup`'s `me` says `pinResetByEmail`.

### `POST /plugins/profiles/account/pin-reset`

**Authorization:** signed-in user. The main account only.

```json
{ "code": "042917", "newPin": "2468" }
```

Sets the main account's PIN and lifts any lock on it. The address gets a note saying the PIN
changed. Profiles' PINs are reset by the main account, not by email.

### `GET /plugins/profiles/admin/email`

**Authorization:** administrator.

```json
{
  "host": "smtp.example.com",
  "port": 587,
  "security": "starttls",
  "username": "bonfire@example.com",
  "hasPassword": true,
  "fromAddress": "bonfire@example.com",
  "configured": true,
  "verified": true,
  "pinResetByEmail": false,
  "accounts": [
    { "userId": "…", "name": "John", "address": "john@example.com", "confirmedUtc": "…" }
  ]
}
```

The password is never returned. It is stored in plain text in the plugin configuration.
The configuration export leaves it out.

### `POST /plugins/profiles/admin/email`

**Authorization:** administrator.

| Field | Type | Description |
| --- | --- | --- |
| `host` | string | The mail server. |
| `port` | integer | Usually 587. Port 465 isn't supported. |
| `security` | string | `starttls`, or `none` for a relay on your own network. |
| `username` | string | Empty for a server that takes none. |
| `password` | string | Omit to keep the stored one; empty clears it. |
| `fromAddress` | string | The address mail comes from. It is shown as "Bonfire". |

All optional. Returns the `GET` shape. Changing anything the connection uses makes it
unverified until the next test, and everything that needs email stops until then.

### `POST /plugins/profiles/admin/email/test`

**Authorization:** administrator.

```json
{ "to": "you@example.com" }
```

Sends a test message with the saved settings. If it goes through, email becomes available.
`400` with a reason if it doesn't. Returns the `GET` shape.

### `POST /plugins/profiles/admin/account-email/remove`

**Authorization:** administrator.

```json
{ "userId": "…" }
```

The account stops getting codes and notices.

## Viewing hours

A main account can set, per profile, the hours it may watch on each day of the week and a
daily limit, optionally different at weekends. Main accounts are never limited. Off until
a main account turns it on.

Only video counts. Music, books and photos are never limited, and browsing is always
allowed. Paused time doesn't count. Audio is not gated by type, so a film's soundtrack
asked for through the audio route (`/Audio/{id}/stream`) is not held back.

**Time zone.** Hours and the daily reset follow the household's zone, set on the main
account (`timeZone` on [`POST /account`](#post-pluginsprofilesaccount)). If it has none,
the administrator's `defaultTimeZone`, and then the server's own.

**Enforcement is on the server.** A limited profile that starts a video outside its hours
or past its limit gets `403` from `PlaybackInfo` and from the video stream, with
`X-Bonfire-Error` set to `outside_viewing_hours` or `daily_limit_reached` and
`Retry-After` in seconds until it can watch again. A video already playing gets a
warning five minutes before it's stopped. If time runs out with no warning, it still gets
five minutes. The server also sends the message to the device, because most apps show a
generic playback error for a `403`.

**More time.** Time given lets the profile watch until then, whatever the hours or the
limit say. More time given while some is left adds on to it.

How each app behaves:

| App | Warning and stop | Asking for more time | Giving more time on the spot |
| --- | --- | --- | --- |
| Jellyfin web, webOS, Tizen, Jellyfin Media Player | Message on screen, then playback stops | On the profile screen | On the profile screen, with the main PIN |
| Android TV | Message on screen, then playback stops | No | From the parent's phone or browser |
| Swiftfin | Playback stops, no message | No | Same |
| Others | Depends on the app. Playback is always refused. | No | Same |

A parent gives time from Manage Profiles, in any browser. Emails point there. They don't
carry an approve link, because mail scanners open links and would approve it for you.

### `GET /plugins/profiles/limits/{profileId}`

**Authorization:** signed-in user in the profile's household, or an administrator.

```json
{
  "profileId": "4f1c…",
  "limited": true,
  "allowed": true,
  "reason": "",
  "binding": "limit",
  "minutesLeft": 45,
  "untilUtc": "2026-11-04T18:45:00Z",
  "nextStartUtc": null,
  "usedMinutes": 15,
  "limitMinutes": 60,
  "extraUntilUtc": null,
  "askedUtc": null,
  "timeZone": "Europe/London",
  "rules": {
    "hoursEnabled": true,
    "windows": [{ "days": 62, "start": "15:00", "end": "20:00" }],
    "dailyMinutes": 60,
    "weekendMinutes": 120
  }
}
```

| Field | Description |
| --- | --- |
| `limited` | Hours or a limit are on. When false, the rest is empty. |
| `allowed` | It can watch now. |
| `reason` | Why not: `hours` or `limit`. |
| `binding` | What ends it first: `hours`, `limit` or `extra`. Empty when nothing does today. |
| `minutesLeft`, `untilUtc` | Time left, and when it runs out. |
| `nextStartUtc` | When it can watch again, while it can't. |
| `usedMinutes`, `limitMinutes` | Today's use and limit. |
| `extraUntilUtc` | Extra time given runs until then. |
| `askedUtc` | It asked for more time and nobody has answered. |
| `rules.windows[].days` | Days as bits: Sunday is 1, Monday 2, through Saturday 64. |
| `rules.windows[].start`, `end` | `HH:mm` in the household's zone. An end at or before the start runs past midnight; `24:00` is midnight. |
| `rules.weekendMinutes` | Saturday and Sunday. Null is the same as other days. |

`0` minutes is no limit.

The server sends its time warning as a Jellyfin message, which some apps don't show. To
show your own, poll this route once a minute during playback and warn when `minutesLeft`
is 5 or less. The server stops playback either way.

### `POST /plugins/profiles/limits/{profileId}`

**Authorization:** signed-in user. The profile's main account with its PIN, or an
administrator. The main account needs a PIN to turn limits on.

```json
{
  "hoursEnabled": true,
  "windows": [{ "days": 62, "start": "15:00", "end": "20:00" }],
  "dailyMinutes": 60,
  "weekendMinutes": null,
  "language": "en",
  "masterPin": "1234"
}
```

A field left out keeps its value. `weekendMinutes: null` is the same as other days; left
out, it keeps its value. `[]`, `0` and `false` turn each part off. Up to 28 windows;
limits 0 to 1440. `language` is the language of the messages its apps get. Returns the
`GET` shape.

Errors: `400` with `limits_invalid`, `limits_for_profiles` or `main_pin_required`; `403`.

### `POST /plugins/profiles/limits/{profileId}/grant`

**Authorization:** signed-in user in the profile's household, with the main account's PIN,
or an administrator. From a profile's own device the main account must have a PIN.

```json
{ "minutes": 30, "masterPin": "1234" }
```

1 to 240 minutes. `0` says no to a request and leaves time already given. Returns the
`GET` shape without `rules`.

One profile can be given at most 240 minutes in a household day (midnight in the household's
zone), however many grants that takes and whoever gives them, an administrator included.
A grant that would pass it is `400` with `grant_cap` and gives nothing. Saying no is always
allowed.

### `GET /plugins/profiles/time-requests`

**Authorization:** signed-in user. Returns the caller's own household's requests, never
another's. An API key has no household and gets `403`.

```json
{
  "requests": [
    { "profileId": "…", "profileName": "Sam", "askedUtc": "2026-11-10T19:02:00Z", "timeZone": "Europe/London", "grantMinutesLeft": 240 }
  ]
}
```

The profiles that have asked for more time and are still waiting, oldest first. A profile
with its limits switched off is left out. `grantMinutesLeft` is 240 less what was given
today; at 0 only no is left. Answer with `limits/{profileId}/grant`.

### `POST /plugins/profiles/limits/{profileId}/ask`

**Authorization:** signed-in user in the profile's household.

No body. The main account's open apps get a message, and an email if it chose that. Once
every ten minutes; sooner is `429`. Returns the `GET` shape without `rules`.

## Avatar library

Images an administrator publishes for everyone to choose from, plus thirty built-in
starter avatars unless they're turned off. Choosing one copies it to the profile, so
removing it from the library later doesn't affect profiles that use it.

The library is the `avatars` part of [`GET /form-options`](#get-pluginsprofilesform-options).

| Field | Description |
| --- | --- |
| `allowCustomUploads` | False when images must come from this library. The server then refuses uploads and links. Use `avatarLibraryId`. |
| `image` | The image size and quality to send, and the byte limit. `quality` is `standard`, `high` or `maximum`. |
| `avatars[].displayName` | Free text. Escape it. |
| `avatars[].thumbUrl` | The small version, for grids. |
| `avatars[].isStarter` | A built-in avatar. Its id starts with `starter-`. |

### `GET /plugins/profiles/avatars/{id}`

**Authorization:** anonymous.

One avatar. `?size=thumb` for the small version. `404` if it's unknown, or it's a starter
avatar while those are off.

### `POST /plugins/profiles/admin/avatars`

**Authorization:** administrator.

```json
{ "image": "data:image/jpeg;base64,…", "thumb": "data:image/jpeg;base64,…", "displayName": "Fox" }
```

`image` is required and must fit the image quality's limit. `displayName` is trimmed to
60 characters and defaults to "Avatar". Returns the new entry. `400` for an unreadable or
oversized image.

### `DELETE /plugins/profiles/admin/avatars/{id}`

**Authorization:** administrator.

Removes an avatar. Profiles using it keep their copy.

### `GET /plugins/profiles/admin/avatars/scan`

**Authorization:** administrator.

Lists images in a folder on the server, for importing a set. `?path=` is the folder as
the server sees it.

```json
{ "folder": "/config/avatars", "truncated": false, "files": [{ "name": "kid.png", "size": 20481 }] }
```

At most 200 files; `truncated` says there were more.

### `GET /plugins/profiles/admin/avatars/scan/file`

**Authorization:** administrator.

One file from that folder: `?path=` the folder, `?name=` the file name. The client resizes
it and posts it to `POST /admin/avatars`.

---

## Announcements

Short messages from the administrator, shown on the profile screen.

### Announcements (`announcements` in `/startup`)

The ones showing now, most important first: `critical`, then `warning`, then `info`,
newest first within each. The `announcements` part of
[`GET /startup`](#get-pluginsprofilesstartup), with the same authorization.

```json
[
  {
    "id": "6f1c…",
    "message": "The server restarts at 2am for an update.",
    "severity": "warning",
    "startsUtc": null,
    "endsUtc": "2026-11-05T02:30:00Z"
  }
]
```

The message is plain text. Never render it as HTML. Style by `severity`, not by a color
of your own. Treat an unknown severity as `info`.

### `GET /plugins/profiles/admin/announcements`

**Authorization:** administrator.

Every stored announcement, newest first, each with `createdUtc` and `showing`. Ones that
ended more than 30 days ago are dropped on the next save.

### `POST /plugins/profiles/admin/announcements`

**Authorization:** administrator.

```json
{
  "id": null,
  "message": "The server restarts at 2am for an update.",
  "severity": "warning",
  "startsUtc": null,
  "endsUtc": "2026-11-05T02:30:00Z"
}
```

Leave out `id` to create one; send it to replace that one. `message` is 1 to 280
characters. `severity` is `info` (the default), `warning` or `critical`. `startsUtc: null`
shows it now, `endsUtc: null` keeps it until it's deleted. Dates without a zone are read
as UTC. Returns the saved announcement. At most 50 are stored.

### `POST /plugins/profiles/admin/announcements/delete`

**Authorization:** administrator.

`{ "id": "…" }`. An id that's already gone isn't an error.

## Maintenance

Closes the server to everyone but administrators. Use it for upgrades and moves.

While it is on, every request from an account that isn't an administrator gets
`503`, `X-Bonfire-Error: maintenance`, a `Retry-After` and the administrator's message as
plain text, from any route, Jellyfin's own included. Signing in by name is refused the
same way, before the password is checked, including by a profile's name. These still
answer:

- the item an app was already playing, unless the administrator stopped it. Nothing new
  starts, Live TV included;
- the account's own `GET /Users/Me` and `GET /Users/{ownId}`;
- signing out, Quick Connect, the live connection and the web client's files;
- `GET /System/Info/Public`, `GET /Users/Public` and Bonfire's anonymous routes.

Administrators are never refused. It doesn't end by itself unless it was asked to:
`untilUtc` is shown, and acted on only when `reopenAutomatically` is set. The emergency
disable turns it off until Jellyfin restarts.

With `reopenAutomatically`, the server reopens at `untilUtc` on its own clock, whether or not
anyone has a dashboard open: from that moment nothing is refused and `GET /info` stops
reporting it, and the saved state is written as open on the next request of any kind. The
activity feed gets `BonfireMaintenanceOff` then.

An app should read `maintenance` from `GET /info` before sign-in and show the message.

### `GET /plugins/profiles/admin/maintenance`

**Authorization:** administrator.

`{ "enabled": true, "message": "…", "untilUtc": null, "startedUtc": "…", "stopPlayback": false, "reopenAutomatically": false }`

### `POST /plugins/profiles/admin/maintenance`

**Authorization:** administrator.

```json
{
  "enabled": true,
  "message": "Upgrading to 12.0. Back by 3.",
  "untilUtc": "2026-11-05T03:00:00Z",
  "reopenAutomatically": true,
  "stopPlayback": false
}
```

`message` is up to 280 characters; blank shows "The server is closed for maintenance."
`untilUtc` must be in the future. `reopenAutomatically` (default false) reopens the server at
`untilUtc`, and `400` without one. `stopPlayback: true` stops what household members are
playing and refuses video and audio too.

Closing sends the message to every open app of a household member. Returns the saved
state plus `told` and `stopped`, the number of apps messaged and stopped.

---

## Emergency disable

Turns Bonfire's client script off until Jellyfin restarts, for when Bonfire is what's making
the interface unusable. It doesn't open other profiles or loosen Jellyfin's own library,
rating or tag restrictions. It does turn off everything Bonfire adds: viewing limits, the
maintenance cover, the proxy picker, PIN sign-in and the profile screen. On a device signed
in to a main account, the code gives that account.

### `POST /plugins/profiles/panic`

**Authorization:** anonymous.

```json
{ "code": "correct-horse-battery-staple" }
```

Returns `{ "disabled": true }`. `400` for a wrong code, and also when no code is set, so
the response doesn't reveal which. `429` after 5 tries in an hour.

### `GET /plugins/profiles/panic-state`

**Authorization:** anonymous.

`{ "disabled": false }`. Sent with `no-store`. Only the emergency code sets it; to know
whether Bonfire is turned off, read `disabled` from [`/info`](#get-pluginsprofilesinfo).

### `GET /plugins/profiles/admin/panic-status`

**Authorization:** administrator.

`{ "isConfigured": true, "isCurrentlyDisabled": false }`. The code itself is never returned.

### `POST /plugins/profiles/admin/panic-code`

**Authorization:** administrator.

`{ "code": "…" }`, 10 to 128 characters. An empty code turns the feature off.

---

## For other plugins

### `IBonfireProfiles`

A plugin running in the same server can ask Bonfire which household a user belongs to.
Get it from Jellyfin's service provider. Without a reference to Bonfire's assembly, find
the type by name, `Jellyfin.Profiles.IBonfireProfiles`.

| Method | Returns |
| --- | --- |
| `bool IsProfile(Guid userId)` | True for a profile of another account. |
| `Guid GetMainAccount(Guid userId)` | The household's main account. Itself for anyone who isn't a profile. |
| `IReadOnlyList<Guid> GetHousehold(Guid userId)` | The main account, then each of its profiles. |

Each call reads the current configuration and never waits on Jellyfin. It's read-only:
make changes through the HTTP routes.

### Viewing limits (`campground.limits.v1`)

Bonfire offers `campground.limits.v1` through the Campground registry. A plugin that
carries Campground.Shared asks it whether a user may start something now:
`Limits.CanPlayNowAsync(userId)` returns `LimitsDecision(Allowed, Reason, Until, GraceSeconds)`.

| Answer | When |
| --- | --- |
| `Allowed` | No rule, a main account, an administrator, a user Bonfire doesn't know, or the emergency disable is on. `Until` is when the viewing hours end, if they end first. |
| `outside_viewing_hours` | Refused. `Until` is when the hours open. |
| `daily_limit_reached` | Refused. `Until` is when the limit starts over. |

A refusal's `GraceSeconds` is how long something already running may carry on: what is
left of the grace Bonfire gave a video, or five minutes. The answer is a question, not
a gate; the caller decides what to do. Bonfire counts the time of every activity with a
`UserId` (`Sessions.ListActivities`) that is not `Paused` against the daily limit, once
however many things the user is doing at once. Maintenance is not part of the answer.

When Bonfire is not installed or is disabled, the shared helper answers allowed. When it is
installed but the call fails, takes over two seconds, or Bonfire has not registered, a user
with limits is refused with `limits_unavailable` and everyone else is allowed. Bonfire
publishes who has limits at start-up and after every save that changes it. Check
`Capabilities.IsAvailable` to tell "allowed" from "nobody asked".

### Households and devices

Other Campground plugins reach Bonfire through the shared registry, not Bonfire's assembly.
Besides viewing limits, Bonfire offers two more capabilities. If Bonfire is missing,
disabled or slow, a caller gets nothing from it and uses the library's fallback, shown below.

**`campground.household.v1`, minor 1.**

| Operation | Returns |
| --- | --- |
| `getHousehold(userId)` | The user's main account, whether the user is a profile, the display name (`ProfileName` for a profile, the username for a main account), every member, and `sharingGroup`: the id, owner and main accounts of the Bonfire group the main account is in, or none. The join code is never included. |
| `getSignInPolicy(userId)` | `{ pinLoginFromAnywhere }`: true when PIN login on other apps is on and allowed from outside the home network. |

A main account with no record of its own is still a main account if profiles point at it.
A user Bonfire holds no record of gets no answer, and the library's `HouseholdClient`
turns that into a household of one. A provider without `getSignInPolicy` answers nothing,
so ask for it with `minMinor: 1` and carry on when there is no answer.

Events, published on `CampgroundEvents` after the change is saved:
`household.profile-created`, `-renamed`, `-deleted` and `-switched`. The payload is
`ProfileEvent`: main account, profile, display name, and for a switch the device and the
account it came from. A switch is raised once, whether it came from Bonfire's switcher or a
television's own sign-in screen.

**`campground.device.v1`.** `getDevice` answers from Bonfire's own device record: `stableKey`
is the recorded device id after an administrator's merges, and `displayName` is the name
the household gave the device, otherwise the client's. A name a household chose is given
only to that household's users. A device Bonfire has never recorded gets no answer, and the
library builds its own key.

The key is for grouping and naming. Bonfire's device restrictions match the id the client
sent and never read it.

While Bonfire is turned off or emergency-disabled, every operation answers nothing and no event is raised.

### Activity log

Bonfire writes these to **Dashboard → Activity**. `type` is what to filter on.

| `type` | When |
| --- | --- |
| `BonfireProfileCreated` | A main account added a profile. |
| `BonfireProfileDeleted` | A main account deleted one. |
| `BonfirePinLocked` | A PIN was locked by too many wrong tries. Once per lock, not per try. |
| `BonfirePinReset` | An administrator cleared a PIN. |
| `BonfireProfileMadeAccount` | An administrator made a profile its own account. |
| `BonfireAccountMadeProfile` | An administrator made an account a profile. |
| `BonfireAccountLinked`, `BonfireAccountUnlinked` | An account was added to a switcher, or taken off. |
| `BonfireMaintenanceOn`, `BonfireMaintenanceOff` | The server was closed or reopened. |
| `BonfireEmergencyDisable` | Someone used the emergency disable code. |

Switching isn't written there, because Jellyfin already logs the profile's session
starting. Bonfire's own switch log is in its settings page.

## Linking to the settings page

Another plugin can open Bonfire's settings page on a tab, and on one setting. **Stable** within
a major version.

```
/web/#/configurationpage?name=Bonfire&tab=clients&setting=pinLoginFromAnywhere
```

| Parameter | Values |
| --- | --- |
| `tab` | `status`, `households`, `devices`, `security`, `look`, `setup`, `clients`, `messages`, `activity`, `troubleshoot`. A known tab opens instead of the one the browser last had open. |
| `setting` | `pinLoginFromAnywhere`: opens its Advanced fold, scrolls to it and focuses it. |

Anything else is ignored, and the page opens as usual. A `setting` on a tab other than the
one in `tab` still works: the page shows the tab the setting is on. New values may be added in
minor releases.

## What Bonfire keeps in the browser

All of this is **internal**: it can change in any release, and nothing outside Bonfire
should read or write it. It's listed so a support answer, or a browser's storage panel,
doesn't have to guess. The one exception is `window.BonfireThemePreview`.

Local storage:

| Key | Holds |
| --- | --- |
| `jellyfin_profiles_master_state`, `jellyfin_profiles_active_token` | Who the browser is signed in as, and the profile session in use. Cleared on sign-out. |
| `jellyfin_profiles_active_info` | The active profile's name and image, for the menu. Kept in session storage and mirrored to local storage. |
| `jellyfin_profiles_cached_list` | The last profile list, so the screen draws before the server answers. |
| `jellyfin_profiles_switcher_mode` | The account's switcher choice, so the first page load knows whether to raise the screen. |
| `jellyfin_profiles_theme`, `jellyfin_profiles_custom_themes` | The last theme this browser was told for the account, for the first paint. |
| `jellyfin_profiles_library_art` | The account's library artwork rules, applied before the server answers. |
| `jellyfin_profiles_layout` | This device's TV layout choice. Absent is automatic. |
| `jellyfin_profiles_disabled` | Set after the emergency disable, so a cached copy of the script stays off. |
| `jellyfin_profiles_settings_epoch` | A timestamp the admin page writes when settings are saved; other tabs' scripts see the change and reload their preferences. |
| `jellyfin_profiles_device_gate` | Retired. Removed on load. |
| `jpf-sw`, `jpf-bg` | Set just before a switch reloads the page, so the next load can hide the flash and match the color being left. |
| `jpf-dismissed-announcements` | Announcements this browser has closed. |
| `bonfire-admin-tab` | The admin page's last tab. |

Window globals: `__jpLoaded` is set when the script has run, `__jpSwitching` and
`__jpReveal` belong to the switch reload, and `__jpCoverT` is the timer on the gate's cover.
The `jpf-settings-changed` window event does the same job as `jellyfin_profiles_settings_epoch`,
inside one tab.

### `window.BonfireThemePreview`

Used by the admin page's theme editor, which loads after the script. Without it the editor
works but shows no preview.

| Member | Does |
| --- | --- |
| `css(theme)` | The CSS for a theme object, from the same generator the profile screens use. Writes nothing. |
| `builtIns()` | A copy of the built-in themes, hidden ones included. |
| `refresh()` | Fetches the theme list again, so a theme just saved reaches this window's pickers. Returns a promise. |

## Admin

### `POST /plugins/profiles/admin/settings`

**Authorization:** administrator.

Changes only the fields you send. Use this instead of Jellyfin's
`POST /Plugins/{id}/Configuration`, which replaces the whole configuration and loses
anything added since you read it.

| Field | Type | Description |
| --- | --- | --- |
| `maxProfilesPerUser` | integer | Profiles per account, 1 to 20. |
| `pinGuessesPerDay` | integer | Wrong PIN tries an account may have in a day, across the web, other apps and the picker. 1 to 20, default 20. It can only be lowered. |
| `requireMainPin` | boolean | Main accounts must set a PIN before changing profiles or settings. |
| `disallowCustomAvatarUploads` | boolean | Images only from the avatar library. |
| `enableStarterAvatars` | boolean | Offer the starter avatars. |
| `bonfireOn` | boolean | Bonfire on or off. Off does what the [emergency code](#emergency-disable) does, without a restart either way. A new install starts off; a server that upgrades stays on. |
| `defaultAskOnStartup` | boolean | Default for accounts that haven't chosen. |
| `defaultSwitcherLocation` | string | `button` or `menu`. |
| `defaultTheme` | string | A [theme](#themes) id. |
| `allowProfileThemes` | boolean | Let profiles choose their own theme. Turning it off keeps their choices for later. |
| `richThemeMotion` | boolean | Let themes with a special animation use it. Default off. |
| `enableClientPinLogin` | boolean | See [Sign-in from other apps](#sign-in-from-other-apps). |
| `enableClientProfileList` | boolean | Same. |
| `clientPinLoginFromAnywhere` | boolean | Same. |
| `skipQuickConnectOnKnownDevices` | boolean | Same. |
| `profileImageQuality` | string | `standard` (512 px, 2 MB), `high` (1024 px, 4 MB), `maximum` (2048 px, 8 MB) or `ultra` (4096 px, 16 MB). The dashboard calls them Low, Medium, High and Maximum; the stored ids are unchanged. |
| `onboardingEnabled` | boolean | [First-time setup](#first-time-setup) on or off. The first time it goes on, existing accounts count as having seen it. |
| `onboardingAsked` | boolean | The dashboard's new-install question about first-time setup has been answered, yes or no. Changes nothing else. |
| `gettingStartedDismiss` | string | Hide one item of the Status tab's Getting started list for every administrator: `pins` or `zone`. `400` for anything else. There is no undo. |
| `enablePinResetByEmail` | boolean | Main accounts may [reset a forgotten PIN by email](#email). `400` turning it on while email isn't working; one already on stays on. |
| `defaultTimeZone` | string | The time zone for [viewing hours](#viewing-hours) in households that haven't chosen one. An IANA id, or `""` for the server's own. |
| `trustedAddressEntries` | array of objects | Addresses where a household opens without a PIN, as `{"address": "203.0.113.0/24", "userIds": ["<main account id>"]}`. From that address the named households open their main account and profiles with no PIN, whatever their own settings say. `[]` turns it off. At most 10 entries; no range wider than `/16` (IPv4) or `/48` (IPv6); IPv4 as dotted decimal only; an address once. `userIds` are real main accounts, never a profile and never an administrator. `400` if anything is malformed, too wide, over 10 or names a bad account, and then nothing is changed. It never skips an administrator's PIN, a switch into another household, an account no entry names, or the main PIN for changing household settings. The address is the one Jellyfin resolves after its Known proxies. Administrators only. |

`400` if any field is out of range, and then nothing is changed.

### `GET /plugins/profiles/admin/mappings`

**Authorization:** administrator.

Every account and profile, and whether the client script is reaching browsers.

```json
{
  "masterUsers": [{ "profileUserId": "8e3cdfa5-…", "profileName": "john", "requiresPin": true, "maxProfiles": 5, "limitOverride": null }],
  "subProfiles": [{ "profileUserId": "a90f11cb-…", "profileName": "Kids", "masterName": "john", "masterUserId": "8e3cdfa5-…", "requiresPin": false }],
  "injectionSucceeded": true,
  "isVersionStale": false,
  "indexPath": "/usr/share/jellyfin/web/index.html",
  "failureReason": null,
  "serviceAccount": "NT AUTHORITY\\NETWORK SERVICE",
  "isWindows": true,
  "pluginVersion": "1.6.6",
  "mechanism": { "restartRequired": false, "middlewareActive": true }
}
```

| Field | Description |
| --- | --- |
| `subProfiles` | Group them by `masterUserId`. Names change. |
| `isAdministrator`, `inBonfire` | On each main account. Neither can be made a profile. |
| `added` | On each main account: `[{ "userId", "name" }]`, the other accounts on its switcher. |
| `injectionSucceeded` | The script is reaching browsers. |
| `isVersionStale` | Always false. Kept so existing clients keep working. |
| `failureReason` | What's wrong, or null. |
| `serviceAccount`, `isWindows` | The account Jellyfin runs as, for writing a permissions command. |
| `pluginVersion` | Includes `-beta` on beta builds. |

`mechanism`:

| Field | Description |
| --- | --- |
| `restartRequired` | This version was installed after Jellyfin started. It isn't fully active until a restart. Don't show it as a failure. |
| `runningVersion` | The version running now. |
| `newestInstalledVersion` | The newest version on disk, or null. |
| `newerVersionInstalled` | An update is waiting for a restart. Null means unknown, not "no". |
| `middlewareRegistered` | The middleware was set up at startup. |
| `middlewareActive` | It has served the web page at least once. This is the "working" signal. |
| `middlewareServed`, `middlewareLastServedUtc` | Pages served since startup, and when last. Counted once for every Campground plugin on the server, since one middleware adds all their tags. |
| `middlewareError` | Why the last page couldn't have the tags added, or null. |

### `POST /plugins/profiles/admin/reset-pin`

**Authorization:** administrator.

`{ "profileId": "…" }`. Clears that profile's PIN. `404` if it isn't a profile.

### `POST /plugins/profiles/admin/cleanup-orphans`

**Authorization:** administrator.

Finds records for Jellyfin users that were deleted outside Bonfire. They still count
against the profile limit. `?apply=false`, the default, only reports:

```json
{
  "applied": false,
  "orphans": [{ "profileUserId": "a90f11cb-…", "masterUserId": "5f2c8a11-…", "profileName": "Sam", "reason": "the profile's Jellyfin user was deleted", "reasonCode": "profile_deleted" }],
  "deadGroups": [],
  "deadMembers": [],
  "unusedImages": 0
}
```

`deadGroups` are Bonfires whose owner was deleted: `{ "groupId": "4f5c9e2b", "code": "B7F8XA" }`.
`deadMembers` are deleted users still listed in a Bonfire whose owner is alive:
`{ "groupId": "4f5c9e2b", "userId": "a90f11cb-…" }`.

`?apply=true` removes them and returns
`{ "applied": true, "removedMappings": 1, "removedGroups": 0, "removedMembers": 0, "removedImages": 0 }`.
`unusedImages` in the report, and `removedImages` here, count theme images (background
images and uploaded pattern images) no custom theme names, uploaded in an editor that
was closed without saving.
There's no undo. Devices are never removed by this.

### `GET /plugins/profiles/admin/sessions`

**Authorization:** administrator.

Every live session, with the household it belongs to, most recent first.

```json
[{
  "sessionId": "8a1c...",
  "userId": "a90f11cb-…",
  "username": "Sam",
  "masterUsername": "Dad",
  "isSubProfile": true,
  "deviceId": "57bfa7e8d35f492b950bf93c9d747a11",
  "deviceName": "Living room TV",
  "client": "Jellyfin Android",
  "appVersion": "2.6.3",
  "lastActivity": "2026-08-31T18:04:11Z",
  "remoteEndPoint": "192.168.1.44",
  "nowPlaying": "The Great British Bake Off"
}]
```

`masterUsername` is null for an account outside Bonfire.

### `POST /plugins/profiles/admin/sessions/sign-out`

**Authorization:** administrator.

```json
{ "userId": "a90f11cb-…", "deviceId": "57bfa7e8d35f492b950bf93c9d747a11" }
```

Signs out that one account on that one device. Both fields are required, so other profiles
on the same TV stay signed in. Returns `{ "signedOut": 1 }`. `400` if either is missing or
there's no such session.

### `GET /plugins/profiles/admin/config/export`

**Authorization:** administrator.

The whole configuration, with the version and time.

```json
{ "exportedUtc": "2026-08-31T18:04:11Z", "pluginVersion": "1.6.6.0", "configuration": { "MaxProfilesPerUser": 5, "Mappings": [], "KnownDevices": [] } }
```

Query parameter `includePins` (boolean, default `true`). By default the file includes every
PIN, hashed. A four-digit PIN can be recovered from its hash quickly, so keep the file as
safe as a password. With `includePins=false` every `PinHash`, on profiles and main accounts,
is left out, and an import of that file keeps the PIN each row already has. The file always
leaves out the mail server password and the emergency code.

`configuration` is the plugin's stored settings. Its top-level fields are these, named as
the plugin names them (`PascalCase`); an import matches them ignoring case. The rows inside
a list and the nested settings objects are the plugin's own data and may change in any
release: **not part of the API.**

| Field | Type | Description |
| --- | --- | --- |
| `MaxProfilesPerUser`, `PinGuessesPerDay` | integer | The profile limit, and wrong PIN guesses an account may have in a day. |
| `RequireMainPin`, `BonfireOn` | boolean | A main account needs a PIN to change its household; whether Bonfire is on at all. |
| `Mappings` | list | Every profile and main account, with its settings and its `PinHash`. |
| `KnownDevices` | list | Devices a household has signed in on. |
| `BonfireGroups` | list | Bonfires: each one's code, owner and members. |
| `LinkedAccounts` | list | Accounts added to another main account's switcher. |
| `UserProfileLimitOverrides` | list | An administrator's profile limit for one account. |
| `AvatarLibrary` | list | The avatars the administrator uploaded (the files are not in the export). |
| `CustomThemes` | list | `{ "Id", "Json" }`: the [theme file](themes.md#the-file), as text. |
| `HiddenThemes`, `CustomFonts` | list | Built-in theme ids taken out of the pickers; uploaded fonts (the files are not in the export). |
| `DefaultTheme`, `AllowProfileThemes`, `RichThemeMotion` | string, boolean, boolean | See [Themes](#themes). |
| `LogoImage`, `LogoText`, `LogoAlt`, `LogoCorners` | string or null | The logo (the image file is not in the export). |
| `DefaultTimeZone`, `DefaultSwitcherLocation`, `DefaultAskOnStartup`, `ProfileImageQuality` | | What an account gets before it chooses. |
| `EnableClientPinLogin`, `ClientPinLoginFromAnywhere`, `EnableClientProfileList`, `SkipQuickConnectOnKnownDevices`, `ProxyClients` | | What other apps may do. |
| `TrustedAddressEntries`, `Announcements`, `AccountEmails`, `EnablePinResetByEmail` | | Trusted addresses, announcements, confirmed addresses, and PIN reset by email. |
| `Maintenance`, `Onboarding`, `Email` | object | Maintenance, first-time setup and mail settings. `Email` has no `Password`. |
| `DisallowCustomAvatarUploads`, `EnableStarterAvatars` | boolean | Avatar choices. |
| `ServiceFingerprints`, `HouseholdsSeenLocally`, `QuickConnectSharedDeviceNames`, `CrossHouseholdEntries` | list | What the server learned about devices and networks. An import leaves `CrossHouseholdEntries` as the server has it. |
| `PanicCodeHash` | null | Always null in an export. |

### `POST /plugins/profiles/admin/config/import`

**Authorization:** administrator.

The body is the `configuration` object from an export, or the whole export. At least one of
`Mappings`, `KnownDevices`, `BonfireGroups` and `AvatarLibrary` has to be in it.

- **Sections:** profiles, devices, Bonfires and the avatar library are each replaced
  whole. A section the file doesn't include is left alone.
- **The mail password and the emergency code** are kept as the server has them when the
  file has none. `emergencyCodeIncluded` says whether the file had the code, and
  `emergencyCodeSet` whether the server has one now.
- **Field names** are matched ignoring case.
- **Profiles for users this server doesn't have** are dropped and named in the response.
- **`bonfireOn`** is whether Bonfire is on after the import. A backup from before 2.0 has
  no on/off switch, so it leaves the server's own, and a new install starts off.

```json
{ "mappings": 4, "droppedMappings": ["Sam"], "devices": 7, "bonfires": 1, "emergencyCodeIncluded": false, "emergencyCodeSet": true, "bonfireOn": true }
```

`400` if the body isn't a Bonfire configuration. Nothing is changed then.

### `POST /plugins/profiles/admin/retry-injection`

**Authorization:** administrator.

Checks again whether the script is reaching browsers, and removes any tags an older
version left in `index.html`. Returns the same status fields as `GET /admin/mappings`.

### `POST /plugins/profiles/admin/set-profile-limit`

**Authorization:** administrator.

```json
{ "userId": "8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a", "maxProfiles": 8 }
```

`maxProfiles` is 1 to 20, or null to use the server-wide limit again. `400` if it's out of
range.

### `POST /plugins/profiles/admin/standalone`

**Authorization:** administrator.

```json
{ "profileId": "…", "username": "Sam", "password": "…" }
```

Makes a profile an ordinary Jellyfin account. It keeps the libraries and limits it has
now and inherits nothing afterwards. Its sessions are signed out and it signs in with the
new password, on Jellyfin's own provider even if its main account uses LDAP or SSO. It
shows on the sign-in screen if its main account does. `username` is optional; blank
keeps the current one. Returns `{ "userId": "…", "username": "Sam" }`. It can't be
undone.

### `POST /plugins/profiles/admin/make-profile`

**Authorization:** administrator.

```json
{ "userId": "…", "householdId": "…", "profileName": "Sam", "deleteWithProfile": false }
```

`deleteWithProfile` decides what deleting the profile later does. `false` (the default)
keeps the account: it leaves the household and signs in on its own. `true` deletes it.

The reverse of `standalone`. The account becomes a profile of `householdId` and keeps its
user, so watch history stays. It keeps its PIN, libraries, tags and image. Its password
is replaced with a random one, its sessions are signed out, and it's hidden from the
sign-in screen. Its email and added accounts are removed. `profileName` is optional;
blank keeps the username. Returns `{ "profileUserId": "…", "profileName": "Sam" }`.

`not_convertible` for an administrator, an account with profiles, one in a Bonfire, one
that's already a profile, or a `householdId` that is a profile. `profile_limit` and
`name_taken` as for `/create`.

### `POST /plugins/profiles/admin/links`

**Authorization:** administrator.

```json
{ "householdId": "…", "userId": "…" }
```

Adds `userId` to `householdId`'s switcher without its password. Same errors as
[`accounts/links/add`](#post-pluginsprofilesaccountslinksadd), less `credentials_wrong`.

### `POST /plugins/profiles/admin/links/remove`

**Authorization:** administrator.

Same body. Takes it off. `link_not_found` if it isn't there.

### `POST /plugins/profiles/admin/pin-prompts`

**Authorization:** administrator.

```json
{ "householdId": "…", "askPinToOpen": false,
  "profiles": [{ "profileId": "…", "bypassPinOnLocalNetwork": true }] }
```

Sets one household's PIN prompts: `askPinToOpen` on the main account, and
`bypassPinOnLocalNetwork` on each profile named. Nothing else of the household changes,
and no PIN is touched. Both parts are optional, and the household sees the new values the
next time its screens load. All or nothing: one refused profile changes nothing.

`400` for a `householdId` that is a profile or has none, an administrator's own
household, or a profile that is not in it. `404` for an unknown user.

### `GET /plugins/profiles/admin/audit-logs`

**Authorization:** administrator.

Recent switches: the newest 1,000, and none older than 90 days.

```json
[{
  "timestamp": "2026-06-12T20:52:00Z",
  "masterUsername": "john",
  "targetUsername": "Kids",
  "deviceName": "Chrome",
  "client": "Jellyfin Web",
  "ipAddress": "192.168.1.50"
}]
```

### `POST /plugins/profiles/admin/audit-logs/clear`

**Authorization:** administrator.

Deletes the whole switch log, in memory and on disk. No body. There is no undo. Returns 500 if the file could not be written; the log is then unchanged.
