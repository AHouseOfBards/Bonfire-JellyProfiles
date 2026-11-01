# Bonfire API

| | |
| --- | --- |
| Plugin ID | `b1462fca-774b-4b13-8d02-e2d4f2bc18b9` |
| Base path | `/plugins/profiles` |
| Server | Jellyfin 10.11.x and 12.0 |

Bodies are JSON unless stated otherwise, and fields are returned camelCase. The API exists
for Bonfire's own client. It's documented because other people build against it too.

## Contents

- [All routes](#all-routes)
- [Authentication](#authentication)
- [Errors](#errors)
- [Rate limits](#rate-limits)
- [Stability](#stability)
- [Profiles](#profiles)
- [Sign-in from other apps](#sign-in-from-other-apps)
- [Client script](#client-script)
- [Translations](#translations)
- [Images](#images)
- [Library artwork](#library-artwork)
- [Devices](#devices)
- [Bonfire](#bonfire)
- [Preferences](#preferences)
- [Avatar library](#avatar-library)
- [Emergency disable](#emergency-disable)
- [Admin](#admin)

## All routes

All 53 endpoints. **Auth** is what each one enforces: `user` means any signed-in account,
`anon` means no token at all. `tests/js/apidocs.js` checks this table against the code.

| Route | Auth | Purpose |
| --- | --- | --- |
| `GET /plugins/profiles/list` | user | Profiles the caller can switch to. |
| `GET /plugins/profiles/libraries` | user | Libraries the caller can give a profile. |
| `GET /plugins/profiles/ratings` | user | The rating limits a profile can be given. |
| `POST /plugins/profiles/create` | user | Create a profile. |
| `POST /plugins/profiles/update` | user | Change a profile. |
| `POST /plugins/profiles/delete` | user | Delete a profile and its account. |
| `POST /plugins/profiles/switch` | user | Switch to a profile and get its token. |
| `POST /plugins/profiles/verify-pin` | user | Check a PIN without switching. |
| `GET /plugins/profiles/profiles.js` | anon | The client script. |
| `GET /plugins/profiles/i18n/{locale}` | anon | One translation. |
| `GET /plugins/profiles/image/{profileId}` | anon | A profile picture. |
| `GET /plugins/profiles/library-artwork` | user | The caller's library artwork. |
| `GET /plugins/profiles/library-artwork/{profileId}` | user | One profile's library artwork. |
| `POST /plugins/profiles/library-artwork` | user | Set library artwork. |
| `GET /plugins/profiles/library-art/{profileId}/{libraryId}` | anon | One library tile picture. |
| `GET /plugins/profiles/devices` | user | The household's devices. |
| `POST /plugins/profiles/devices/delete` | user | Forget a device. |
| `POST /plugins/profiles/devices/merge` | user | Merge two records for one device. |
| `POST /plugins/profiles/devices/merge-duplicates` | user | Find, and optionally merge, duplicate records. |
| `POST /plugins/profiles/devices/rename` | user | Rename a device. |
| `GET /plugins/profiles/bonfire/status` | user | The caller's Bonfire. |
| `POST /plugins/profiles/bonfire/settings` | user | Change Bonfire sharing options. |
| `POST /plugins/profiles/bonfire/generate` | user | Start a Bonfire and get its code. |
| `POST /plugins/profiles/bonfire/join` | user | Join a Bonfire with a code. |
| `POST /plugins/profiles/bonfire/kick` | user | Remove a member and change the code. |
| `POST /plugins/profiles/bonfire/regenerate-code` | user | Change the code. |
| `POST /plugins/profiles/bonfire/leave` | user | Leave a Bonfire. |
| `POST /plugins/profiles/bonfire/delete-group` | user | End your Bonfire. |
| `GET /plugins/profiles/preferences` | user | Switcher preferences. |
| `POST /plugins/profiles/preferences` | user | Change them. |
| `POST /plugins/profiles/theme` | user | Set one profile's theme. |
| `GET /plugins/profiles/avatars` | user | The avatar library. |
| `GET /plugins/profiles/avatars/{id}` | anon | One avatar image. |
| `POST /plugins/profiles/admin/avatars` | admin | Add an avatar. |
| `DELETE /plugins/profiles/admin/avatars/{id}` | admin | Remove an avatar. |
| `POST /plugins/profiles/admin/avatars/settings` | admin | Avatar library options. |
| `GET /plugins/profiles/admin/avatars/scan` | admin | List pictures in a server folder. |
| `GET /plugins/profiles/admin/avatars/scan/file` | admin | Read one of them. |
| `POST /plugins/profiles/panic` | anon | Emergency disable. |
| `GET /plugins/profiles/panic-state` | anon | Whether Bonfire is disabled. |
| `GET /plugins/profiles/admin/panic-status` | admin | Whether a code is set. |
| `POST /plugins/profiles/admin/panic-code` | admin | Set or clear the code. |
| `POST /plugins/profiles/admin/settings` | admin | Server-wide settings. |
| `GET /plugins/profiles/admin/mappings` | admin | Every profile, plus script status. |
| `POST /plugins/profiles/admin/reset-pin` | admin | Clear a profile's PIN. |
| `POST /plugins/profiles/admin/cleanup-orphans` | admin | Remove records for deleted users. |
| `GET /plugins/profiles/admin/sessions` | admin | Live sessions by household. |
| `POST /plugins/profiles/admin/sessions/sign-out` | admin | Sign one device out. |
| `GET /plugins/profiles/admin/config/export` | admin | Export the configuration. |
| `POST /plugins/profiles/admin/config/import` | admin | Import a configuration. |
| `POST /plugins/profiles/admin/retry-injection` | admin | Retry writing the script tag. |
| `POST /plugins/profiles/admin/set-profile-limit` | admin | One account's profile limit. |
| `GET /plugins/profiles/admin/audit-logs` | admin | The switch log. |

## Authentication

Send Jellyfin's usual header. Manage profiles with the main account's token; after a
switch, use the token it returns.

```http
Authorization: MediaBrowser Client="<Client>", Device="<Device>", DeviceId="<DeviceId>", Version="<Version>", Token="<token>"
```

Seven routes are reachable without a token, because they're needed before anyone signs in.

| Endpoint | Why it is anonymous |
| --- | --- |
| `GET /profiles.js` | Loaded by the sign-in page. |
| `GET /i18n/{locale}` | Fetched by that script before sign-in. |
| `GET /image/{profileId}` | Pictures on the profile screen, which is shown before sign-in. |
| `GET /avatars/{id}` | Same. |
| `GET /library-art/{profileId}/{libraryId}` | Same. |
| `POST /panic` | Has to work when Bonfire is what's blocking the interface. Rate limited. |
| `GET /panic-state` | Says only whether Bonfire is disabled. |

The image routes can be reached by anyone who knows the id. Ids are GUIDs, so they can't
be guessed.

Every other route checks the caller itself, because the controller is
`[AllowAnonymous]` so the routes above work. A new route with no check is public.

## Errors

| Code | Meaning |
| --- | --- |
| `200` | Done, including when there was nothing to change. |
| `400` | Refused. The body says why. |
| `401` | No valid token, or not allowed. |
| `404` | Unknown profile, image or library. |
| `429` | Rate limited. See below. |
| `500` | A bug. The body is Jellyfin's. |
| `503` | A translation could not be read. Try again. |

**Error bodies are plain text, not JSON.** Read the body as text and show it to the user;
it's written for them.

```
HTTP/1.1 400 Bad Request
Content-Type: text/plain; charset=utf-8

Invalid PIN code.
```

## Rate limits

Attempts are counted before they're checked, so parallel requests can't slip past. A
correct attempt resets the count. A refused request returns `429`.

| Limiter | Applies to | Attempts | Window | Counted per |
| --- | --- | --- | --- | --- |
| PIN | `POST /switch`, `POST /verify-pin` | 5 | 15 minutes | address and profile |
| PIN, per profile | the same, plus the main account's PIN on edits | 10 | 15 minutes | profile, all addresses |
| client PIN | a PIN typed into another app's sign-in | 5, and 20 a day | 15 minutes, 24 hours | account |
| Bonfire | `POST /bonfire/join` | 3 | 15 minutes | address |
| panic | `POST /panic` | 5 | 60 minutes | address |

A client PIN that hits its limit gets Jellyfin's usual `Invalid username or password`. A
main account's real password keeps working.

## Stability

- Fields documented here keep their type and meaning within a major version.
- Ignore fields you don't recognise. New ones arrive in minor releases.
- Don't rely on field order.
- Routes aren't removed within a major version. One being retired is marked deprecated
  here first.
- Optional request fields that you leave out keep their stored value, unless the field
  says otherwise.

---

## Profiles

### `GET /plugins/profiles/list`

**Authorisation:** signed-in user.

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
| `avatarInitial` | string | One letter for a picture-less avatar. |
| `avatarColor` | string | Hex colour behind the avatar. |
| `transparentAvatar` | boolean | Don't paint `avatarColor` behind the picture. |
| `theme` | string | The account owner only. The profile's own [theme](#themes), or `null` if it follows the server default. |
| `requiresPin` | boolean | Ask for a PIN before switching **now**. False on the home network when the bypass is on. |
| `hasPin` | boolean | A PIN is set. Use this, not `requiresPin`, to show PIN state in a form. |
| `isMaster` | boolean | This is the main account. |
| `isAdministrator` | boolean | Main accounts only. The account is a Jellyfin administrator. For warnings, not for access checks. |
| `lockoutMinutes` | integer | Minutes of inactivity before the profile locks. `0` is never. |
| `maxSubProfiles` | integer | Main accounts only. How many profiles it may have. |
| `enabledFolders` | GUID[] | Profiles only. The libraries it was given. |
| `allLibraries` | boolean | Profiles only. It was saved with no library ticked, so it follows the main account's libraries, including ones added later. |
| `blockedTags` | string[] | The profile's own blocked tags. The main account's are added when it's opened. |
| `allowedTags` | string[] | The profile's own allowed tags. Empty means no allow-list. |
| `bypassPinOnLocalNetwork` | boolean | Skip the PIN on the home network. |
| `allowedDeviceIds` | string[] | Devices it may be opened on. Empty means any. |
| `isBonfire` | boolean | Belongs to another household in the caller's Bonfire. |
| `profileImage` | string | `/plugins/profiles/image/{id}?v=…`, an `http(s)` URL, or null. |
| `masterUserId` | GUID | The main account it belongs to. |

`enabledFolders`, the tag lists and `allowedDeviceIds` are empty for profiles in another
household. PINs are stored hashed and never returned.

### `POST /plugins/profiles/switch`

**Authorisation:** signed-in user. The target must be in the caller's household or Bonfire.

```json
{ "profileId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b", "pin": "1234" }
```

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| `profileId` | GUID | Yes | The profile to switch to. |
| `pin` | string | When `requiresPin` | The profile's PIN. |

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
  household, an account turned off in Dashboard → Users, or no session could be made
  (device not allowed, or too many sessions).
- `401`: the caller's own token is invalid, or the target isn't theirs to open. A `401`
  never means the target failed, so a client can treat it as "sign the caller in again".
- `404`: the profile doesn't exist.
- `429`: too many wrong PINs.

### `POST /plugins/profiles/verify-pin`

**Authorisation:** signed-in user.

Checks a profile's PIN without switching.

```json
{ "profileId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b", "pin": "1234" }
```

Returns `200` when it's correct. Errors: `400` wrong PIN or not allowed on this device,
`401`, `429`.

### `POST /plugins/profiles/create`

**Authorisation:** signed-in user. A main account, not one of its profiles.

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
| `avatarColor` | string | No | Hex colour. Defaults to `#1F77B4`. |
| `transparentAvatar` | boolean | No | Don't paint the colour behind the picture. |
| `theme` | string | No | A [theme](#themes) id. Omit or `""` to follow the server default. Ignored while the server doesn't let profiles choose. |
| `maxParentalRating` | string | No | A rating score, such as `"6"`, `"10"`, `"14"` or `"17"`. Never above the main account's. Omit for no limit of its own. |
| `enabledFolders` | GUID[] | No | The libraries it may see. `[]` is none. Omit or `null` for every library the main account has, including ones added later. |
| `blockedTags` | string[] | No | Tags it must not see, on top of the main account's. |
| `allowedTags` | string[] | No | If set, it sees only items with one of these tags. Untagged items are hidden too. `400` if none of them is allowed for the main account. |
| `lockoutMinutes` | integer | No | Minutes before it locks. `0` is never. Defaults to 5. |
| `masterPin` | string | When the main account has a PIN | The main account's PIN. |
| `bypassPinOnLocalNetwork` | boolean | No | Skip the PIN on the home network. |
| `allowedDeviceIds` | string[] | No | Devices it may be opened on. Empty means any. |
| `profileImage` | string | No | See [Uploading a picture](#uploading-a-picture). |
| `profileImageThumb` | string | No | The picture's small version. |
| `avatarLibraryId` | string | No | A picture from the avatar library. Wins over `profileImage`. |

Response: `{ "profileUserId": "…", "profileName": "Kids" }`.

Errors: `400` for a bad PIN, a taken name, the profile limit, an unknown theme, or allowed
tags the main account doesn't allow.

### `POST /plugins/profiles/update`

**Authorisation:** signed-in user.

Takes the same fields as `/create`, plus `profileId`. Most fields left out stay as they
are; these don't:

| Field | Left out or `null` |
| --- | --- |
| `profileName` | Required. |
| `avatarColor` | Resets to `#1F77B4`. |
| `maxParentalRating` | Removes the profile's own limit. The main account's still applies. |
| `enabledFolders` | Every library the main account has, including ones added later. |

Other values worth knowing:

- **`pin`:** `""` clears it, `null` keeps it.
- **`profileImage`:** `""` clears it, `null` keeps it.
- **`theme`:** `""` goes back to the server default, `null` keeps it.
- **`blockedTags`, `allowedTags`:** `[]` clears them, `null` keeps them. Ignored for the
  main account.
- **Rating sub-score:** a sub-score set in Dashboard → Users (TV-PG against TV-PG-V) is kept
  while the rating stays the same.

Returns `200`. Errors as for `/create`.

### `POST /plugins/profiles/delete`

**Authorisation:** signed-in user.

Deletes a profile and its Jellyfin account.

```json
{ "profileId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b", "masterPin": "1234" }
```

Returns `200`. `500` if the account couldn't be deleted; it's disabled instead, so it can't
be signed in to, and the profile stays listed so you can try again.

### `GET /plugins/profiles/libraries`

**Authorisation:** signed-in user.

The libraries the main account can see, which are the ones it can give a profile.

```json
[{ "id": "e67b2d5a39cb400ba45a7b0a70198de7", "name": "Movies", "collectionType": "movies" }]
```

### `GET /plugins/profiles/ratings`

**Authorisation:** signed-in user.

The ratings of the server's **Preferred metadata country**, one entry per age, for a
profile's rating limit. These are the ratings Jellyfin scored the library with.

```json
{
  "country": "GB",
  "ratings": [
    { "score": 0, "labels": ["0+", "All", "E", "G", "U"] },
    { "score": 12, "labels": ["12A", "12PG"] },
    { "score": 15, "labels": ["15"] }
  ]
}
```

Send `score` as `maxParentalRating` to [`/create`](#post-pluginsprofilescreate) or
[`/update`](#post-pluginsprofilesupdate). Adult and banned ratings aren't listed; leave the
limit out for those. `ratings` is empty if Jellyfin has no table for the country.

Unlike `/Localization/ParentalRatings`, this doesn't add Jellyfin's generic ages
(`10`, `13`, `14`, `21`) to countries that don't have them.

### How restrictions reach a profile

A profile is a hidden Jellyfin account. Every time it's opened, through the switcher or by
signing in on another app, Bonfire writes its policy from the main account's:

- **Copied:** libraries, rating, tags, remote access, playback, downloads, Live TV,
  channels, allowed devices and schedules.
- **The stricter of the two:** bitrate, session count, forced transcoding, SyncPlay and
  blocked unrated items. A limit set on the profile's own account in Dashboard → Users
  counts as the profile's. Everything else follows the main account, both tighter and
  looser.
- **Never more than the main account:** management permissions. A profile is never an
  administrator.

A library granted to a profile in Dashboard → Users is kept. A library removed there comes
back, because the profile's own list wins; edit the profile instead.

If none of a profile's allowed tags is still allowed for its main account, it's given a
placeholder tag that no item carries, so it sees nothing until it's edited.

---

## Sign-in from other apps

Three settings under **TVs & Apps** change Jellyfin's own routes, for apps that never load
the web client. All three are off by default.

| Setting | What changes |
| --- | --- |
| `enableClientPinLogin` | Profiles, and main accounts with a PIN, can be signed in to with the PIN where an app asks for a password. |
| `enableClientProfileList` | `GET /Users/Public` includes the household of the device asking, and profiles show under their own name. |
| `skipQuickConnectOnKnownDevices` | `POST /QuickConnect/Initiate` is refused on a TV a household has used, so the app shows its password box. |

### `GET /Users/Public`

The household is found from the `DeviceId` in the request, against the devices Bonfire has
seen a sign-in on. Profiles are added to Jellyfin's list; nothing is removed. An
unrecognised device gets Jellyfin's answer unchanged. Device limits apply.

Android TV and Jellyfin for Android send a device id derived from the signed-in user. Both
forms are recognised.

### `GET /Users/Me` and `POST /Users/AuthenticateByName`

A profile's `Name` is reported as the name its household gave it, `kids` rather than
`Bard_kids`. The account isn't renamed.

### `POST /QuickConnect/Initiate`

Refused with `401 "Quick connect is disabled"` on an Android TV whose name a household has
signed in with. The first sign-in on a TV is never refused. A TV name that someone outside
every household has also used is never refused either, since two sets of the same model
look the same.

### Signing in with a PIN

- **A profile** opens with its PIN. A profile with no PIN opens with an empty password,
  but only on a device its household has signed in on.
- **The username** can be the full one (`Bard_kids`) from anywhere, or the profile's own
  name (`kids`) on a device its household has signed in on.
- **A main account** keeps its real password; the PIN works as well.
- **Every refusal** returns the same message, so nobody can tell which accounts are
  profiles.

---

## Client script

The browser has to load `profiles.js` for any of the switcher to appear. How it's added is
set under **Advanced → Client Script** and reported as `mechanism.mode`:

| `mode` | How |
| --- | --- |
| `middleware` | The default. Tags are added to `index.html` as it's served. The file on disk is untouched. |
| `both` | Legacy. `index.html` is edited on disk, with the middleware as a fallback. |
| `file` | Legacy. `index.html` is edited on disk only. |

An `index.html` with no Bonfire tags is normal under `middleware`. Read `mechanism` from
`GET /admin/mappings` instead. Samsung Tizen packages need the loader described in
[TROUBLESHOOTING.md](../TROUBLESHOOTING.md#samsung-tizen).

### `GET /plugins/profiles/profiles.js`

**Authorisation:** anonymous.

The client script, as `application/javascript`.

| Header | Value |
| --- | --- |
| `ETag` | The plugin version. |
| `Cache-Control` | `public, max-age=300, must-revalidate` |

Returns `304` when `If-None-Match` matches. A browser picks up a new version within five
minutes even if the `?v=` on its script tag is out of date.

---

## Translations

English is built into `profiles.js`. Other languages are one JSON file each in
`Web/i18n/`. The browser's language decides which file is fetched, and an English browser
fetches nothing. If a file is missing or fails, the text stays English.

To add a language, see [Web/i18n/README.md](../Web/i18n/README.md).

### `GET /plugins/profiles/i18n/{locale}`

**Authorisation:** anonymous.

One language's strings, as a flat object. `{locale}` may end in `.json`.

```json
{ "gate.whosWatching": "Qui regarde ?", "common.cancel": "Annuler" }
```

| Status | Meaning |
| --- | --- |
| `200` | The strings. |
| `304` | `If-None-Match` matched. |
| `404` | No translation for that code. |
| `503` | The file couldn't be read. Stay on English and try later. |

`ETag` is `"{pluginVersion}-{locale}"` and `Cache-Control` is the same as
`profiles.js`. There's no route listing the languages. They're written into
`profiles.js` as it's served, on the line marked `__BONFIRE_LOCALES__`.

---

## Images

### `GET /plugins/profiles/image/{profileId}`

**Authorisation:** anonymous.

A profile's stored picture. Add `?size=thumb` for the small version. If there's no small
version, the full one is sent, so `size=thumb` is always safe; use it in grids.

Returns `404` if there's no stored picture, including when the picture is an `http(s)`
link. Load that link directly from `profileImage` in `/list`.

All four image routes send:

| Header | Value |
| --- | --- |
| `Cache-Control` | `private, max-age=3600` |
| `ETag` | From the file's size and modified time. |
| `Last-Modified` | The file's modified time. |

Send `If-None-Match`, or every picture is downloaded again on every page load.

### Uploading a picture

`profileImage` and `profileImageThumb` on `/create` and `/update` take a
`data:image/…;base64,…` value. The size limit is set by the server's picture quality: 2, 4
or 8 MB. `GET /avatars` reports it, and the size to draw at.

The server doesn't resize or convert anything, so the client draws both sizes itself, at
the sizes `GET /avatars` gives. JPEG, PNG, WebP and GIF are accepted; SVG isn't. `profileImage` can also be an `http(s)` URL, which is stored as a
link.

---

## Library artwork

Jellyfin draws one picture per library from whatever is inside it, so a child's profile can
see a tile showing a film it can't open. These routes let a profile replace or hide that
picture. The client does the swap, so it only applies where Bonfire's script runs.

### `GET /plugins/profiles/library-artwork`

**Authorisation:** signed-in user.

The caller's choices.

```json
[{ "libraryId": "f137a2dd21bbc1b99aa5c0f6bf02a805", "mode": "custom", "url": "/plugins/profiles/library-art/<profileId>/<libraryId>?v=638..." }]
```

`mode` is `custom` (use `url`) or `none` (no picture). Libraries left on the default aren't
listed.

### `GET /plugins/profiles/library-artwork/{profileId}`

**Authorisation:** signed-in user.

The same list for one of the caller's profiles. `401` if it isn't theirs.

### `POST /plugins/profiles/library-artwork`

**Authorisation:** signed-in user.

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
| `image`, `thumb` | string | No | The picture, for `custom`. |
| `avatarLibraryId` | string | No | Use an avatar-library picture instead. |
| `masterPin` | string | When the main account has a PIN | The main account's PIN. |

`custom` with no picture keeps the stored one, or is refused if there isn't one. Errors:
`400` unknown library, wrong PIN or bad picture; `401` not the caller's profile.

### `GET /plugins/profiles/library-art/{profileId}/{libraryId}`

**Authorisation:** anonymous.

The stored picture for a `custom` choice. `?size=thumb` for the small one.

---

## Devices

### `GET /plugins/profiles/devices`

**Authorisation:** signed-in user.

The devices the caller's household has used, for the allowed-devices picker. A profile
calling this gets its main account's devices.

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

**When you save a profile, keep allowed devices you weren't shown.** An empty
`allowedDeviceIds` means any device, so dropping them removes the restriction.

### `POST /plugins/profiles/devices/delete`

**Authorisation:** signed-in user.

```json
{ "deviceId": "57bfa7e8d35f492b950bf93c9d747a11" }
```

`400` if it's the only device a profile is allowed on, since removing it would allow every
device. The message names those profiles.

### `POST /plugins/profiles/devices/merge`

**Authorisation:** signed-in user. Must be the main account and own both devices.

For one machine with two records, which happens when the same browser reaches the server by
two addresses.

```json
{ "fromDeviceId": "57bfa7e8…", "intoDeviceId": "9c1e04b7…" }
```

Profiles allowed on `fromDeviceId` become allowed on `intoDeviceId`, and the old record is
removed. `400` if either is unknown, they're the same, or one isn't the caller's.

### `POST /plugins/profiles/devices/merge-duplicates`

**Authorisation:** signed-in user. Must be the main account.

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

**Authorisation:** signed-in user. Must be the main account and own the device.

```json
{ "deviceId": "57bfa7e8d35f492b950bf93c9d747a11", "deviceName": "Living room TV" }
```

The name is kept even when the device reports a different one. Trimmed to 64 characters.
`400` for an unknown device or a blank name.

---

## Bonfire

### `GET /plugins/profiles/bonfire/status`

**Authorisation:** signed-in user.

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

**Authorisation:** signed-in user.

```json
{ "hideMySubProfilesFromOthers": false, "hideOthersSubProfilesFromMe": false, "allowHouseholdLanBypass": false }
```

The two hide flags are required. Leave `allowHouseholdLanBypass` out to keep it as it is.

Before turning the bypass on, a client should warn about two things:

- **An administrator account** gives anyone who opens it control of the server.
- **"Home network"** is whatever Jellyfin counts as local. Behind a reverse proxy missing
  from **Networking → Known Proxies**, that's everyone.

### `POST /plugins/profiles/bonfire/generate`

**Authorisation:** signed-in user.

Starts a Bonfire. Returns `{ "groupId": "4f5c9e2b", "bonfireCode": "B7F8XA", "members": [] }`.

### `POST /plugins/profiles/bonfire/join`

**Authorisation:** signed-in user.

```json
{ "code": "B7F8XA" }
```

Returns `{ "message": "Successfully joined Bonfire group.", "ownerName": "FriendMaster" }`.
Joining leaves any Bonfire the caller was already in. Errors: `400` bad code or your own
Bonfire, `401` not a main account, `429` too many tries.

### `POST /plugins/profiles/bonfire/kick`

**Authorisation:** signed-in user.

```json
{ "memberId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b" }
```

Removes the member and changes the code so they can't rejoin. Returns the new code:
`{ "bonfireCode": "Q7M2KX" }`.

### `POST /plugins/profiles/bonfire/regenerate-code`

**Authorisation:** signed-in user.

Changes the code without removing anyone. Returns `{ "bonfireCode": "Q7M2KX" }`. `400` if
the caller hasn't started a Bonfire.

### `POST /plugins/profiles/bonfire/leave`

**Authorisation:** signed-in user.

Leaves the Bonfire the caller joined.

### `POST /plugins/profiles/bonfire/delete-group`

**Authorisation:** signed-in user.

Ends the caller's Bonfire and removes everyone from it.

---

## Preferences

Each household chooses these for itself. A profile reads its main account's, and only the
main account can change them.

### `GET /plugins/profiles/preferences`

**Authorisation:** signed-in user.

```json
{
  "askOnStartup": true,
  "switcherLocation": "button",
  "switcherMode": "gate",
  "masterUserId": "8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a",
  "emergencyCodeConfigured": false,
  "theme": "crimson",
  "defaultTheme": "bonfire",
  "allowProfileThemes": true
}
```

| Field | Description |
| --- | --- |
| `askOnStartup` | Show "Who's watching?" once per browser session. |
| `switcherLocation` | `button`: a Bonfire button in the header. `menu`: a **Switch Profile** entry in Jellyfin's user menu. |
| `switcherMode` | Deprecated. `gate` is `askOnStartup: true` with `button`; `native` is `false` with `menu`. |
| `masterUserId` | The account these belong to, for caching. |
| `emergencyCodeConfigured` | An administrator has set an emergency code. Only then offer the emergency option. |
| `theme` | The [theme](#themes) to show **this** account, from its own choice or the server default. Unlike the fields above, it isn't the main account's. |
| `defaultTheme` | The server default. |
| `allowProfileThemes` | Profiles may choose their own theme. When `false`, `theme` is always the default. |

An account that hasn't chosen gets the server's defaults. Unknown stored values come back
as the defaults.

Bonfire's own client lets one device keep its own `askOnStartup`. That's stored in the
browser only, so this route always returns the household's answer.

### `POST /plugins/profiles/preferences`

**Authorisation:** signed-in user.

```json
{ "askOnStartup": true, "switcherLocation": "menu" }
```

Both are optional. Returns the stored values in the `GET` shape; cache those rather than
what you sent. `401` for a profile.

### Themes

How Bonfire's own screens look: the profile picker, PIN entry and profile forms. The rest
of Jellyfin keeps its own theme.

| Id | Look |
| --- | --- |
| `bonfire` | The default. Follows the server's Jellyfin accent colour. |
| `crimson` | Flat charcoal, red, square tiles, outlined capital-letter buttons. |
| `starlight` | Flat navy, round tiles with a white ring, solid buttons that invert. |
| `harbor` | Blue-grey gradient, bold type, round tiles, slate buttons. |
| `nightfall` | Black into purple, gradient rings, pill buttons. |
| `glass` | Soft coloured blur, small round tiles, frosted buttons. |
| `harvest` | Autumn. Brown into amber with drifting leaves, leaf-shaped tiles, a serif. |
| `snowfall` | Winter. Night blue with falling snow, frosted rings, white outlined buttons. |
| `bloom` | Spring. Fresh green with blossoms, soft rounded tiles, pink highlights. |

A theme is per profile. Set it with `theme` on [`/create`](#post-pluginsprofilescreate) or
[`/update`](#post-pluginsprofilesupdate), or on its own with
[`/theme`](#post-pluginsprofilestheme); read the one to show from
[`/preferences`](#get-pluginsprofilespreferences). The administrator sets the default and
whether profiles may choose, under [`admin/settings`](#post-pluginsprofilesadminsettings).

### `POST /plugins/profiles/theme`

**Authorisation:** signed-in user. Main account only, with its PIN if it has one.

```json
{ "profileId": "…", "theme": "nightfall", "masterPin": "1234" }
```

Changes the theme and nothing else. `""` goes back to the server default. Returns
`{ profileId, theme, stored }`: `theme` is what that profile now sees, `stored` its own
choice or `null`.

`400` for an unknown theme, `401` for a profile outside your household, `403` when the
server does not let profiles choose.

---

## Avatar library

Pictures an administrator publishes for everyone to choose from, plus thirty built-in
starter avatars unless they're turned off. Choosing one copies it to the profile, so
removing it from the library later doesn't affect profiles that use it.

### `GET /plugins/profiles/avatars`

**Authorisation:** signed-in user.

```json
{
  "allowCustomUploads": true,
  "starterAvatarsEnabled": true,
  "image": { "quality": "high", "masterSize": 1024, "thumbSize": 256, "jpegQuality": 0.9, "maxBytes": 4194304 },
  "avatars": [
    { "id": "9f2c41a0b7d3", "displayName": "Fox", "url": "/plugins/profiles/avatars/9f2c41a0b7d3", "thumbUrl": "/plugins/profiles/avatars/9f2c41a0b7d3?size=thumb", "isStarter": false }
  ]
}
```

| Field | Description |
| --- | --- |
| `allowCustomUploads` | False when pictures must come from this library. The server then refuses uploads and links. Use `avatarLibraryId`. |
| `image` | The picture size and quality to send, and the byte limit. `quality` is `standard`, `high` or `maximum`. |
| `avatars[].displayName` | Free text. Escape it. |
| `avatars[].thumbUrl` | The small version, for grids. |
| `avatars[].isStarter` | A built-in avatar. Its id starts with `starter-`. |

### `GET /plugins/profiles/avatars/{id}`

**Authorisation:** anonymous.

One avatar. `?size=thumb` for the small version. `404` if it's unknown, or it's a starter
avatar while those are off.

### `POST /plugins/profiles/admin/avatars`

**Authorisation:** administrator.

```json
{ "image": "data:image/jpeg;base64,…", "thumb": "data:image/jpeg;base64,…", "displayName": "Fox" }
```

`image` is required and must fit the picture quality's limit. `displayName` is trimmed to
60 characters and defaults to "Avatar". Returns the new entry. `400` for an unreadable or
oversized image.

### `DELETE /plugins/profiles/admin/avatars/{id}`

**Authorisation:** administrator.

Removes an avatar. Profiles using it keep their copy.

### `POST /plugins/profiles/admin/avatars/settings`

**Authorisation:** administrator.

| Field | Description |
| --- | --- |
| `disallowCustomAvatarUploads` | Pictures may only come from the library. |
| `enableStarterAvatars` | Offer the starter avatars. |

Leave a field out to keep it as it is.

### `GET /plugins/profiles/admin/avatars/scan`

**Authorisation:** administrator.

Lists pictures in a folder on the server, for importing a set. `?path=` is the folder as
the server sees it.

```json
{ "folder": "/config/avatars", "truncated": false, "files": [{ "name": "kid.png", "size": 20481 }] }
```

At most 200 files; `truncated` says there were more.

### `GET /plugins/profiles/admin/avatars/scan/file`

**Authorisation:** administrator.

One file from that folder: `?path=` the folder, `?name=` the file name. The client resizes
it and posts it to `POST /admin/avatars`.

---

## Emergency disable

Turns Bonfire's client script off until Jellyfin restarts, for when Bonfire is what's making
the interface unusable. It doesn't open other profiles or loosen any restriction. It does
skip the profile screen, so on a device signed in to a main account, the code gives that
account.

### `POST /plugins/profiles/panic`

**Authorisation:** anonymous.

```json
{ "code": "correct-horse-battery-staple" }
```

Returns `{ "disabled": true }`. `400` for a wrong code, and also when no code is set, so
the response doesn't reveal which. `429` after 5 tries in an hour.

### `GET /plugins/profiles/panic-state`

**Authorisation:** anonymous.

`{ "disabled": false }`. Sent with `no-store`.

### `GET /plugins/profiles/admin/panic-status`

**Authorisation:** administrator.

`{ "isConfigured": true, "isCurrentlyDisabled": false }`. The code itself is never returned.

### `POST /plugins/profiles/admin/panic-code`

**Authorisation:** administrator.

`{ "code": "…" }`, 10 to 128 characters. An empty code turns the feature off.

---

## Admin

### `POST /plugins/profiles/admin/settings`

**Authorisation:** administrator.

Changes only the fields you send. Use this instead of Jellyfin's
`POST /Plugins/{id}/Configuration`, which replaces the whole configuration and loses
anything added since you read it.

| Field | Type | Description |
| --- | --- | --- |
| `maxProfilesPerUser` | integer | Profiles per account, 1 to 20. |
| `requireMasterPinForCreation` | boolean | Ask for the main account's PIN to create a profile. |
| `disallowCustomAvatarUploads` | boolean | Pictures only from the avatar library. |
| `enableStarterAvatars` | boolean | Offer the starter avatars. |
| `defaultAskOnStartup` | boolean | Default for accounts that haven't chosen. |
| `defaultSwitcherLocation` | string | `button` or `menu`. |
| `defaultTheme` | string | A [theme](#themes) id. |
| `allowProfileThemes` | boolean | Let profiles choose their own theme. Turning it off keeps their choices for later. |
| `indexInjectionMode` | string | `middleware`, `both` or `file`. |
| `enableClientPinLogin` | boolean | See [Sign-in from other apps](#sign-in-from-other-apps). |
| `enableClientProfileList` | boolean | Same. |
| `skipQuickConnectOnKnownDevices` | boolean | Same. |
| `profileImageQuality` | string | `standard` (512 px, 2 MB), `high` (1024 px, 4 MB) or `maximum` (2048 px, 8 MB). |

`400` if any field is out of range, and then nothing is changed.

### `GET /plugins/profiles/admin/mappings`

**Authorisation:** administrator.

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
  "mechanism": { "mode": "middleware", "restartRequired": false, "middlewareActive": true }
}
```

| Field | Description |
| --- | --- |
| `subProfiles` | Group them by `masterUserId`. Names change. |
| `injectionSucceeded` | The script is reaching browsers. |
| `isVersionStale` | The script tag on disk is an older version. The switcher still works. Always false under `middleware`. |
| `failureReason` | What's wrong, or null. |
| `serviceAccount`, `isWindows` | The account Jellyfin runs as, for writing a permissions command. |
| `pluginVersion` | Includes `-beta` on beta builds. |

`mechanism`:

| Field | Description |
| --- | --- |
| `mode` | `middleware`, `both` or `file`. |
| `restartRequired` | This version was installed after Jellyfin started. It isn't fully active until a restart. Don't show it as a failure. |
| `runningVersion` | The version running now. |
| `newestInstalledVersion` | The newest version on disk, or null. |
| `newerVersionInstalled` | An update is waiting for a restart. Null means unknown, not "no". |
| `middlewareRegistered` | The middleware was set up at startup. |
| `middlewareActive` | It has served the web page at least once. This is the "working" signal. |
| `middlewareServed`, `middlewareLastServedUtc` | Pages served since startup, and when last. |
| `middlewareError` | Why the last page couldn't have the tags added, or null. |

### `POST /plugins/profiles/admin/reset-pin`

**Authorisation:** administrator.

`{ "profileId": "…" }`. Clears that profile's PIN. `404` if it isn't a profile.

### `POST /plugins/profiles/admin/cleanup-orphans`

**Authorisation:** administrator.

Finds records for Jellyfin users that were deleted outside Bonfire. They still count
against the profile limit. `?apply=false`, the default, only reports:

```json
{
  "applied": false,
  "orphans": [{ "profileUserId": "a90f11cb-…", "masterUserId": "5f2c8a11-…", "profileName": "Sam", "reason": "the profile's Jellyfin user was deleted" }],
  "deadGroups": [],
  "deadMembers": []
}
```

`?apply=true` removes them and returns
`{ "applied": true, "removedMappings": 1, "removedGroups": 0, "removedMembers": 0 }`.
There's no undo. Devices are never removed by this.

### `GET /plugins/profiles/admin/sessions`

**Authorisation:** administrator.

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

**Authorisation:** administrator.

```json
{ "userId": "a90f11cb-…", "deviceId": "57bfa7e8d35f492b950bf93c9d747a11" }
```

Signs out that one account on that one device. Both fields are required, so other profiles
on the same TV stay signed in. Returns `{ "signedOut": 1 }`. `400` if either is missing or
there's no such session.

### `GET /plugins/profiles/admin/config/export`

**Authorisation:** administrator.

The whole configuration, with the version and time.

```json
{ "exportedUtc": "2026-08-31T18:04:11Z", "pluginVersion": "1.6.6.0", "configuration": { "MaxProfilesPerUser": 5, "Mappings": [], "KnownDevices": [] } }
```

It includes every PIN, hashed. A four-digit PIN can be recovered from its hash quickly, so
keep the file as safe as a password.

### `POST /plugins/profiles/admin/config/import`

**Authorisation:** administrator.

The body is the `configuration` object from an export.

- **Sections:** profiles, devices, Bonfires and the avatar library are each replaced
  whole. A section the file doesn't include is left alone.
- **Field names** are matched ignoring case.
- **Profiles for users this server doesn't have** are dropped and named in the response.

```json
{ "mappings": 4, "droppedMappings": ["Sam"], "devices": 7, "bonfires": 1 }
```

`400` if the body isn't a Bonfire configuration. Nothing is changed then.

### `POST /plugins/profiles/admin/retry-injection`

**Authorisation:** administrator.

Tries writing the script tag into `index.html` again, for after fixing file permissions.
Only does anything in the `both` and `file` modes. Returns the same status fields as
`GET /admin/mappings`.

### `POST /plugins/profiles/admin/set-profile-limit`

**Authorisation:** administrator.

```json
{ "userId": "8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a", "maxProfiles": 8 }
```

`maxProfiles` is 1 to 20, or null to use the server-wide limit again. `400` if it's out of
range.

### `GET /plugins/profiles/admin/audit-logs`

**Authorisation:** administrator.

Recent switches.

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
