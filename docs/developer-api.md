# Bonfire — Developer API Reference

**Plugin ID:** `b1462fca-774b-4b13-8d02-e2d4f2bc18b9`  
**Base path:** `/plugins/profiles`  
**Server:** Jellyfin 10.11.x  

All paths below are relative to the base path. All request and response bodies are JSON
unless stated otherwise. Field names are returned camelCase.

---

## Authentication

Almost every request requires a Jellyfin authorization header. Profile management
endpoints must be authenticated with the master user's token; after a profile switch, the
returned active profile token is used for subsequent requests.

```http
Authorization: MediaBrowser Client="<ClientName>", Device="<DeviceName>", DeviceId="<DeviceId>", Version="<Version>", Token="<token>"
```

### The endpoints that do not

Seven routes are reachable without a token, each because it has to work *before* anyone
has signed in. They are listed here so an integrator does not mistake one for a hole, and
so anyone adding a route knows this list is meant to stay this short.

| Endpoint | Why it is anonymous |
|---|---|
| `GET /profiles.js` | Loaded by a `<script>` tag on the sign-in page itself. |
| `GET /i18n/{locale}` | Fetched by that script, before sign-in, for the same reason. |
| `GET /image/{profileId}` | Avatars are drawn on the profile gate, which is pre-auth. |
| `GET /avatars/{id}` | The avatar library, same. |
| `GET /library-art/{profileId}/{libraryId}` | Library tile artwork, same. |
| `POST /panic` | The emergency disable. It has to work when the plugin is what is blocking the interface. Rate limited, and a wrong code and a missing one return the same error. |
| `GET /panic-state` | Reports only whether the plugin is disabled — already visible on screen. |

The image endpoints allow enumeration by GUID. That is accepted: they must be pre-auth for
the gate to render, and a GUID is not guessable.

The controller carries a class-level `[AllowAnonymous]` because the script and image
endpoints must be reachable pre-auth and Jellyfin's authorization policy name is not part
of the public plugin API. **Authorization is therefore hand-rolled per endpoint** — every
other route calls `GetCurrentUserId()` and returns `401`, or checks
`Policy.IsAdministrator`. There is no framework backstop, so a new route without a check
is silently public.

---

## Profiles API

### `GET /plugins/profiles/list`
Retrieves a list of all profiles (master and sub-profiles) accessible to the authenticated master session.

* **Headers:** `Authorization: MediaBrowser Token="<masterToken>"`
* **Response `200 OK`:**
```json
[
  {
    "profileUserId": "8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a",
    "profileName": "John",
    "avatarInitial": "J",
    "avatarColor": "#00A4DC",
    "transparentAvatar": false,
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
|---|---|---|
| `profileUserId` | string (GUID) | Jellyfin user ID assigned to the profile. |
| `profileName` | string | Display name of the profile. |
| `avatarInitial` | string | Single character representing the profile avatar. |
| `avatarColor` | string | Hex color code for the fallback avatar display. |
| `transparentAvatar` | boolean | When true, do not paint `avatarColor` behind `profileImage`. Set for pictures that are cut out rather than rectangular, where a colour would otherwise show in the corners. Always false when there is no picture. |
| `requiresPin` | boolean | Whether a PIN must be entered to switch to this profile **right now**. This is false when `bypassPinOnLocalNetwork` is set and the caller is on the local network, even though a PIN exists. Use this to decide whether to prompt. |
| `hasPin` | boolean | Whether a PIN is configured on this profile at all, regardless of whether one will be prompted for. Use this — never `requiresPin` — to display PIN state in a settings or edit screen. See the note below. |
| `isMaster` | boolean | Indicates if this is the master user account. |
| `lockoutMinutes` | integer | Inactivity timeout in minutes before auto-lock. `0` indicates disabled. |
| `maxSubProfiles` | integer | Maximum sub-profiles allowed (present only when `isMaster` is true). |
| `enabledFolders` | string[] (GUIDs) | Library GUIDs accessible to this sub-profile (present only when `isMaster` is false). |
| `blockedTags` | string[] | Tags this sub-profile is blocked from seeing. This is the profile's own list; the master's blocked tags are merged in when the policy is applied, so it may be narrower than what the underlying Jellyfin user enforces. |
| `allowedTags` | string[] | Tags this sub-profile is restricted to. Empty means no allow-list. Same "profile's own list" caveat as `blockedTags`. |
| `bypassPinOnLocalNetwork` | boolean | If true, PIN entry is bypassed when the client is on a local network (LAN). |
| `allowedDeviceIds` | string[] | Device IDs permitted to access this profile. Empty or null indicates no device restrictions. |
| `isBonfire` | boolean | Indicates if the profile belongs to a linked Bonfire guest home. |
| `profileImage` | string | The profile picture: either `/plugins/profiles/image/{id}?v=…` for a stored image, or an absolute http(s) URL the user pasted. Null if none. Append `size=thumb` to a stored URL for the small variant — see the note below. |
| `masterUserId` | string (GUID) | Jellyfin user ID of the master user account this profile belongs to. |

> **`requiresPin` vs `hasPin`.** These answer different questions and are not interchangeable.
> `requiresPin` means *"prompt for a PIN before switching"*; it is deliberately false on the LAN
> when the bypass is enabled. `hasPin` means *"a PIN is stored"*.
>
> A client that uses `requiresPin` to render PIN state in an edit screen will show a correctly
> saved PIN as "not set" for every user on their own network — which looks exactly like the save
> having failed. Prompt on `requiresPin`; display on `hasPin`.
>
> PINs are stored as salted PBKDF2-SHA256 hashes and are never returned by any endpoint, so a
> client can show *that* a PIN exists but never the PIN itself.

### `POST /plugins/profiles/switch`
Authenticates a profile selection and returns a scoped session token. Rate limited to 5 failed attempts in 15 minutes.

* **Headers:** `Authorization: MediaBrowser Token="<masterToken>"`
* **Request Body:**
```json
{
  "profileId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b",
  "pin": "1234"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `profileId` | string (GUID) | Yes | The Jellyfin user ID of the target profile. |
| `pin` | string | Conditional | Required if `requiresPin` is true for the target profile. |

> **Cross-account switches (Bonfire).** Switching into *another master account* linked via a Bonfire group returns a fully privileged session for that account, so two extra rules apply:
> * If the target master account has no PIN set, the switch is refused with `400` — an unprotected account is not reachable through a shared Bonfire.
> * `bypassPinOnLocalNetwork` is ignored for these switches. It is the caller's own convenience setting and does not carry across a link, so the PIN is required even on the LAN.
>
> Both rules are lifted for a target account whose **owner** has set `allowHouseholdLanBypass`
> (see `POST /bonfire/settings`) *and* whose request Jellyfin classifies as local. Consent comes
> from the account being entered, never from the caller. Remote requests are unaffected in every
> case, and each bypass is written to the audit log.
>
> None of this affects switching to your own account or its sub-profiles.
>
> `requiresPin` in `/list` mirrors all of the above, so clients that trust that flag need no
> special handling.

* **Response `200 OK`:**
```json
{
  "activeProfileToken": "7ef4a378297b470183b0b3e6cda7670e",
  "jellyfinUserId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b"
}
```

| Field | Type | Description |
|---|---|---|
| `activeProfileToken` | string | Scoped Jellyfin session token for the target profile. |
| `jellyfinUserId` | string (GUID) | Jellyfin user ID of the target profile. |
| `libraryArtwork` | array | The target profile's library artwork choices, in the same shape as `GET /library-artwork`. Included so a client can apply them before reloading. Added in 1.4.0. |

* **Error Responses:**
  * `400 Bad Request`: Incorrect PIN, device restrictions not met, target is an unprotected master account reached via Bonfire, invalid parameters, or the session for the target profile could not be created (device not permitted for that user, or its maximum active sessions reached). The body carries the reason.
  * `401 Unauthorized`: Caller is not authenticated, or unauthorized profile switch attempt.
  * `404 Not Found`: Target profile or underlying system user does not exist.
  * `429 Too Many Requests`: PIN authentication rate limit exceeded (5 failed attempts per 15 minutes).

> **A `401` from this endpoint is about the caller, never the target.** Every way the target
> profile can fail to authenticate is returned as a `400` with a reason in the body, so a client
> can safely treat `401` as "my own token is no longer valid" and nothing else. Before 1.3.3 a
> failure to create the target's session surfaced as a `401`, and clients that read it as session
> expiry signed the user out of an account that was working fine (issue #15).

### `GET /plugins/profiles/admin/avatars/scan`
Lists the pictures in a folder on the server, for importing a prepared set into the avatar
library. Administrator only. Added in 1.4.0.

* **Query:** `path` — a folder path as the *server* sees it.
* **Response `200 OK`:** `{ "folder": "/config/avatars", "truncated": false, "files": [{ "name": "kid.png", "size": 20481 }] }`

Only extensions the plugin can store are listed, and at most 200 files; `truncated` says when
there were more.

### `GET /plugins/profiles/admin/avatars/scan/file`
Returns one file from a scanned folder. Administrator only.

* **Query:** `path` — the folder; `name` — the file, a bare name with no separators.

> **Why the client reads the bytes.** The plugin has no server-side image library and keeps
> it that way — every resize in Bonfire happens on a canvas in the browser. A folder import
> therefore lists on the server, reads each file through this endpoint, resizes it in the
> page, and posts the result to `POST /admin/avatars` like any other upload. The server never
> decodes an image, and one code path produces every stored avatar.

### `GET /plugins/profiles/library-artwork`
Returns the calling profile's library tile artwork choices. Added in 1.4.0.

* **Headers:** `Authorization: MediaBrowser Token="<token>"`
* **Response `200 OK`:**

```json
[
  {
    "libraryId": "f137a2dd21bbc1b99aa5c0f6bf02a805",
    "mode": "custom",
    "url": "/plugins/profiles/library-art/<profileId>/<libraryId>?v=638..."
  }
]
```

| Field | Type | Description |
|---|---|---|
| `libraryId` | string (GUID) | The library the choice applies to. |
| `mode` | string | `custom` (use `url`) or `none` (show no artwork). `inherit` is never returned — it is the default and is stored as absence. |
| `url` | string | Path to the stored picture, or null unless the mode is `custom`. |

> **Why a client has to do this.** Jellyfin builds one image per library and caches it on
> the folder (`CollectionFolderImageProvider`, a collage of up to eight random items). The
> query behind it has no user attached, so the artwork cannot respect who is asking: a
> profile restricted to children's films still gets a tile drawn from whatever else lives
> in the library. There is no per-user image for the server to hand out, so a client that
> wants this has to substitute it. The bundled `profiles.js` does it with one `!important`
> stylesheet rule per library, keyed on the `data-id` jellyfin-web puts on every card.

The same list is included in the `POST /switch` response as `libraryArtwork`, so a client
can apply the incoming profile's choices before it reloads rather than after — fetching
afterwards leaves a window in which the restricted artwork is on screen.

### `GET /plugins/profiles/library-artwork/{profileId}`
The same list for another profile. Master-only, like every other profile setting.

  * `401 Unauthorized`: Caller is not the master of that profile.

### `POST /plugins/profiles/library-artwork`
Sets or clears one profile's artwork for one library.

* **Headers:** `Authorization: MediaBrowser Token="<masterToken>"`
* **Body:**

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
|---|---|---|---|
| `profileId` | string (GUID) | Yes | The profile whose view is being changed. |
| `libraryId` | string (GUID) | Yes | Must be a library Jellyfin currently knows about. |
| `mode` | string | Yes | `inherit`, `custom` or `none`. Unrecognised values normalise to `inherit`. |
| `image` | string | No | Full-size picture as a data URL. Only read when the mode is `custom`. |
| `thumb` | string | No | Small rendering of the same picture. |
| `avatarLibraryId` | string | No | Use a picture from the administrator's avatar library instead of `image`. Copied server-side, which is what makes "only allow avatars from this library" enforceable. |
| `masterPin` | string | No | Required when the master account has a PIN. |

`inherit` and `none` both delete any stored picture for that pair. Sending `custom` with
neither `image` nor `avatarLibraryId` keeps whatever is already stored, and is refused if
nothing is.

  * `400 Bad Request`: Unknown library, invalid master PIN, an image that could not be
    stored, or `custom` with no picture available.
  * `401 Unauthorized`: Caller is not the master of that profile.

### `GET /plugins/profiles/library-art/{profileId}/{libraryId}`
Serves a stored picture. Unauthenticated, like the other image routes, and serves only
pairs the configuration currently lists as `custom` — a file left behind by an earlier
choice stops being reachable.

* **Query:** `size=thumb` for the small rendering.

### `POST /plugins/profiles/verify-pin`
Validates a profile PIN without switching the active session. Rate limited to 5 failed attempts in 15 minutes.

* **Headers:** `Authorization: MediaBrowser Token="<masterToken>"`
* **Request Body:**
```json
{
  "profileId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b",
  "pin": "1234"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `profileId` | string (GUID) | Yes | The Jellyfin user ID of the profile. |
| `pin` | string | Yes | The numeric PIN to validate. |

* **Response `200 OK`:** PIN is correct.
* **Error Responses:**
  * `400 Bad Request`: Incorrect PIN, device restrictions not met, or invalid parameters.
  * `401 Unauthorized`: Caller is not authenticated, or unauthorized profile PIN verification.
  * `429 Too Many Requests`: PIN authentication rate limit exceeded (5 failed attempts per 15 minutes).

### `POST /plugins/profiles/create`
Creates a new sub-profile.

* **Headers:** `Authorization: MediaBrowser Token="<masterToken>"`
* **Request Body:**
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
|---|---|---|---|
| `profileName` | string | Yes | Display name for the new profile. |
| `pin` | string | No | Numeric PIN for the profile (4-8 digits). Pass null or omit for no PIN. |
| `avatarColor` | string | No | Hex color code for the fallback avatar. Defaults to `#1F77B4`. |
| `transparentAvatar` | boolean | No | Suppresses `avatarColor` behind the picture. Defaults to false. |
| `maxParentalRating` | string | No | Maximum parental rating allowed (e.g., "6", "10", "14", "17"). Omit for no restriction. |
| `enabledFolders` | string[] (GUIDs) | No | Array of library GUIDs accessible to this profile. Empty array denies all library access. |
| `blockedTags` | string[] | No | Tags this profile must never see. Merged with the master's blocked tags — a sub-profile can add blocks but never remove the master's. Null or empty blocks nothing. |
| `allowedTags` | string[] | No | When non-empty, restricts the profile to items carrying at least one of these tags. Intersected with the master's allow-list when it has one; returns `400` if the two share no tags. **Untagged content is hidden by an allow-list.** |
| `lockoutMinutes` | integer | No | Inactivity timeout in minutes before auto-lock. `0` to disable. Defaults to `5`. |
| `masterPin` | string | Conditional | Required if the master account has a PIN set. |
| `bypassPinOnLocalNetwork` | boolean | No | Bypasses PIN entry when the client is on a local network. Defaults to `false`. |
| `allowedDeviceIds` | string[] | No | Specific device IDs permitted to switch to this profile. Empty or null for no restriction. |
| `profileImage` | string | No | Base64-encoded JPEG data-URL or image URL representing the profile picture. |

* **Response `200 OK`:**
```json
{
  "profileUserId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b",
  "profileName": "Kids"
}
```

| Field | Type | Description |
|---|---|---|
| `profileUserId` | string (GUID) | Jellyfin user ID assigned to the new profile. |
| `profileName` | string | Display name of the new profile. |

### `POST /plugins/profiles/update`
Updates settings for an existing sub-profile.

* **Headers:** `Authorization: MediaBrowser Token="<masterToken>"`
* **Request Body:**
```json
{
  "profileId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b",
  "profileName": "Kids (Edited)",
  "pin": "",
  "avatarColor": "#D946EF",
  "maxParentalRating": "10",
  "enabledFolders": ["e67b2d5a39cb400ba45a7b0a70198de7"],
  "blockedTags": ["adults"],
  "allowedTags": [],
  "lockoutMinutes": 30,
  "masterPin": "1234",
  "bypassPinOnLocalNetwork": false,
  "allowedDeviceIds": [],
  "profileImage": ""
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `profileId` | string (GUID) | Yes | Jellyfin user ID of the profile to update. |
| `profileName` | string | Yes | New display name. |
| `pin` | string | No | New numeric PIN. Pass `""` to clear the PIN. Pass `null` to leave unchanged. |
| `avatarColor` | string | No | New hex color code. |
| `transparentAvatar` | boolean | No | Suppresses `avatarColor` behind the picture. Omit or send `null` to leave unchanged. |
| `maxParentalRating` | string | No | New maximum parental rating code. Pass `null` to leave unchanged. |
| `enabledFolders` | string[] (GUIDs) | No | Updated library GUIDs. Pass `null` to leave unchanged. |
| `blockedTags` | string[] | No | Updated blocked tags. Pass `[]` to clear, `null` to leave unchanged. Ignored for the master profile. |
| `allowedTags` | string[] | No | Updated allow-list. Pass `[]` to clear, `null` to leave unchanged. Returns `400` if it shares no tags with the master's allow-list. Ignored for the master profile. |
| `lockoutMinutes` | integer | No | New inactivity timeout in minutes. Pass `null` to leave unchanged. |
| `masterPin` | string | Conditional | Required if the master account has a PIN set. |
| `bypassPinOnLocalNetwork` | boolean | No | Updated local network PIN bypass setting. Pass `null` to leave unchanged. |
| `allowedDeviceIds` | string[] | No | Updated list of allowed device IDs. Pass `null` to leave unchanged. |
| `profileImage` | string | No | Base64-encoded JPEG data-URL or image URL representing the profile picture. Pass `""` to clear the picture, or `null` to leave unchanged. |

* **Response:** `200 OK` on success.

### `POST /plugins/profiles/delete`
Permanently deletes a sub-profile and its underlying Jellyfin account.

* **Headers:** `Authorization: MediaBrowser Token="<masterToken>"`
* **Request Body:**
```json
{
  "profileId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b",
  "masterPin": "1234"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `profileId` | string (GUID) | Yes | Jellyfin user ID of the profile to delete. |
| `masterPin` | string | Conditional | Required if the master account has a PIN set. |

* **Response:** `200 OK` on success.

### `GET /plugins/profiles/libraries`
Retrieves media library folders visible to the master user.

* **Headers:** `Authorization: MediaBrowser Token="<masterToken>"`
* **Response `200 OK`:**
```json
[
  {
    "id": "e67b2d5a39cb400ba45a7b0a70198de7",
    "name": "Movies",
    "collectionType": "movies"
  }
]
```

| Field | Type | Description |
|---|---|---|
| `id` | string (GUID) | Library folder GUID. |
| `name` | string | Display name of the library. |
| `collectionType` | string | Type of media collection (e.g., "movies", "tvshows"). |

---

## Client Script

The plugin serves one script, which the browser must load for any of the switcher UI to
exist. Delivery is set server-wide under **Advanced → Client script** on the plugin
configuration page, and reported by `mechanism.mode`:

| `mode` | Behaviour |
| --- | --- |
| `middleware` | Default since 1.5. The plugin holds a place in the ASP.NET request pipeline and adds its tags to `index.html` as it is served. The file on disk is not modified, and any tags an earlier version wrote are removed. |
| `both` | Legacy. `index.html` is patched on disk, and the pipeline is used as a fallback if that write fails. |
| `file` | Legacy. `index.html` is patched on disk only. This is what every release up to 1.4 did. |

Under `middleware`, `index.html` on disk contains no plugin tags. Do not treat its absence
as a failure: read `mechanism` instead (see `GET /plugins/profiles/admin/mappings`).

A client that supplies its own copy of the web client — a Tizen `.wgt`, for example — is
not served by any of them and must bundle the script itself.

### `GET /plugins/profiles/profiles.js`
Serves the web client script. Unauthenticated — it is loaded by a `<script>` tag in Jellyfin's `index.html`.

Native clients do not need this endpoint; it exists for the browser client. It is documented because its caching behaviour interacts with the injection status reported by the admin endpoints.

* **Response:**
  * `200 OK`: `application/javascript`
  * `304 Not Modified`: when the request's `If-None-Match` matches the current `ETag`.

| Header | Value |
|---|---|
| `ETag` | The running plugin version, quoted. |
| `Cache-Control` | `public, max-age=300, must-revalidate` |

The script tag written into `index.html` normally carries a `?v={version}` cache-buster. On servers where the plugin cannot rewrite `index.html` — a read-only web root, or a Windows install under `Program Files` — that query string stays pinned to an older version, so a long immutable cache would serve stale client code indefinitely.

Tagging the response with the plugin version and requiring revalidation means a stale URL still picks up new code within `max-age`, while unchanged content costs only a `304`. This is why `isVersionStale` is advisory rather than an error.

---

## Translations

The gate, the switcher, the profile forms, the PIN prompts and their errors are all
translatable. The admin settings page is not — see *Scope* below.

English is compiled into `profiles.js` as `EN_STRINGS` and is the fallback for every
lookup. Other languages are one JSON file each under `Web/i18n/`, embedded in the plugin
assembly and served by the endpoint below.

The client picks a language from `navigator.languages`, not from any Jellyfin setting, and
fetches at most one file. **A reader whose browser asks for English causes no request at
all.** A missing file, a failed request or a malformed payload all leave English in place;
nothing about translation can stop the switcher loading.

Lookups fall back per key, so a file that is missing newer keys renders those keys in
English rather than blank.

### `GET /plugins/profiles/i18n/{locale}`

Returns one language's strings. Unauthenticated — it is fetched before anyone signs in,
for the same reason `profiles.js` is.

`{locale}` may be given with or without a `.json` suffix. Only codes with a file embedded
in the assembly are served; anything else is `404`, and an unknown code is rejected before
any cache is touched.

* **Response:**
  * `200 OK`: `application/json` — a flat object of key/value strings.
  * `304 Not Modified`: when `If-None-Match` matches the current `ETag`.
  * `404 Not Found`: no translation for that code.

| Header | Value |
|---|---|
| `ETag` | `"{pluginVersion}-{locale}"` |
| `Cache-Control` | `public, max-age=300, must-revalidate` |

Same caching contract as `profiles.js`, and for the same reason: the plugin version is the
cache-buster, so an updated translation is picked up within `max-age` without a hard
refresh.

```json
{
  "gate.whosWatching": "Qui regarde ?",
  "common.cancel": "Annuler",
  "profilePage.body": "Vous regardez en tant que <strong>{name}</strong>."
}
```

### Adding a translation

**One JSON file in `Web/i18n/`. Nothing else.** The `.csproj` embeds `Web/i18n/*.json` by
wildcard, the server discovers locale codes by reading its own embedded resource names,
and it rewrites the client's locale list as it serves `profiles.js`. There is no list to
register a language in, in any of the three languages this plugin is written in.

1. Copy `Web/i18n/fr.json` to `Web/i18n/{code}.json`.
2. Translate the values. **Leave the keys exactly as they are** — they are identifiers.
3. Run `node Web/i18n/validate.js {code}` (no dependencies).
4. `dotnet build -c Release`, restart Jellyfin, set your browser's language, open the gate.

`Web/i18n/README.md` is the contributor-facing version of this, including what to do when
a new translation does not appear.

**Naming.** The filename is the locale code, matched against what the browser sends:
`de.json`, `pt-BR.json`, `zh-Hans.json`. Two or three letters, optionally with region or
script subtags. Matching is case-insensitive and prefers the longest match — a browser
asking for `zh-Hans-CN` takes `zh-Hans.json` over `zh.json`, and falls back to `zh.json`
when there is no more specific file. Use a bare language code unless regional differences
genuinely matter.

**Placeholders and markup.** Values may contain `{token}` placeholders and inline HTML
such as `<strong>`. Both must survive translation: `t()` substitutes only the tokens its
caller passes, so a dropped `{name}` renders a gap and an invented one renders the braces
verbatim. These strings reach `innerHTML`, so a dropped closing tag breaks the layout
around it. `validate.js` checks both.

**Partial files are fine.** Omit a key and it renders in English. Do not ship an empty
string for it — that renders as nothing at all.

**Scope.** `Web/profilesDashboard.html` — the admin settings page — is deliberately English
only. It is read by whoever runs the server, its wording changes with almost every
release, and much of it is diagnostics and copy-pasteable shell commands.

**Length.** These strings sit on buttons and in a grid that has to stay usable on a
television at three metres, navigated by a D-pad. A translation much longer than the
English is worth checking in a narrow window before submitting.

### Reading the available languages

There is no endpoint listing them, deliberately — it would be a request every English
reader also paid for. The list is written into `profiles.js` as it is served:

```js
let SUPPORTED_LOCALES = ['fr']; // __BONFIRE_LOCALES__
```

The file on disk carries an empty list and that marker comment. If you consume
`profiles.js` directly, read that line; if you are looking for what a given server has,
request a locale and treat `404` as "not available".


---

## Images API

### `GET /plugins/profiles/image/{profileId}`
Serves the custom profile picture file for the specified profile.

* **Parameters:**
  * `profileId`: string (GUID) in path.
  * `size`: optional query. Pass `thumb` for the small variant.
* **Response:**
  * `200 OK`: Binary image file (JPEG, PNG, WebP, or GIF).
  * `404 Not Found`: Profile or image not found, or the profile's picture is an externally hosted URL.

This endpoint is intentionally unauthenticated: the URL is consumed directly as an image source and browsers do not send the `Authorization` header on image requests. This matches how Jellyfin serves its own user images, and the content is a low-sensitivity avatar.

It serves **locally stored images only**. When a profile's picture is an external `http(s)` URL, that URL is returned in the `profileImage` field of `GET /plugins/profiles/list` and clients should load it directly — this endpoint returns `404` rather than redirecting to it. (It previously issued a `302`, which made an anonymous endpoint into an open redirect.)

> **Use `size=thumb` in grids.** Every stored picture is written twice — a full-size master and
> a small thumbnail — because a switcher screen showing twenty full-size avatars decodes tens of
> megabytes of bitmap, which is enough to stall the TV browsers this plugin supports. If no
> thumbnail was stored (an image saved before this existed, or a client that sent only the
> master) the full-size file is served instead, so the parameter is always safe to pass.

### Uploading a picture

`POST /create` and `POST /update` accept the picture in `profileImage` and, optionally, its
small rendering in `profileImageThumb`. Both are `data:image/…;base64,…` payloads capped at
2 MB decoded.

**The plugin does no server-side image processing** — no resizing, no cropping, no format
conversion. A client is expected to render both sizes itself, which is what keeps the plugin
free of an image-processing dependency. The bundled client produces a 512×512 master and a
128×128 thumbnail from a square crop the user positions.

`profileImage` also accepts an absolute `http(s)` URL, which is stored as a link rather than a
file. Those have no thumbnail variant and are returned unchanged in `/list`.

---

## Devices API

### `GET /plugins/profiles/devices`
Retrieves the devices belonging to the caller's household, for populating an allowed-devices picker.

* **Headers:** `Authorization: MediaBrowser Token="<masterToken>"`
* **Response `200 OK`:**
```json
[
  {
    "deviceId": "57bfa7e8d35f492b950bf93c9d747a11",
    "deviceName": "Chrome",
    "client": "Jellyfin Web",
    "lastSeen": "2026-06-12T09:41:46.806Z",
    "masterUserId": "8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a"
  }
]
```

| Field | Type | Description |
|---|---|---|
| `deviceId` | string | Recorded device identifier. |
| `deviceName` | string | Display name of the device. |
| `client` | string | Client application name. |
| `lastSeen` | string (ISO-8601) | Timestamp of the last interaction. `0001-01-01T00:00:00Z` for a placeholder entry (see below) — treat that as "unknown", not as a real date. |
| `masterUserId` | string (GUID) | Master account this device is recorded against. |

**Scoping.** Results are limited to devices recorded against the caller's master account. Devices are attributed on first use, so a device that has never contacted this household does not appear. A sub-profile calling this endpoint sees its master's devices.

> **Writing an editor: do not drop devices you were not shown.**
>
> The response always includes every device already present in one of this account's
> `allowedDeviceIds` lists, **even if that device is switched off, has not been seen in months,
> or is no longer in the device log at all**. Entries synthesised for that last case carry
> `deviceName: "Previously allowed device"` and a zero `lastSeen`.
>
> This exists because an edit form that rebuilds `allowedDeviceIds` purely from the checkboxes it
> rendered will silently delete any whitelisted device missing from this list — and a whitelist
> that empties out stops restricting anything at all, quietly turning the restriction off. Either
> render every returned entry, or preserve unknown IDs when submitting.

### `POST /plugins/profiles/devices/delete`
Deletes a device from the known devices log.

* **Headers:** `Authorization: MediaBrowser Token="<masterToken>"`
* **Request Body:**
```json
{
  "deviceId": "57bfa7e8d35f492b950bf93c9d747a11"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `deviceId` | string | Yes | The device ID to remove. |

* **Response:** `200 OK` on success.

---

## Bonfire API

### `GET /plugins/profiles/bonfire/status`
Retrieves the bonfire group status and visibility settings for the caller.

* **Headers:** `Authorization: MediaBrowser Token="<masterToken>"`
* **Response `200 OK`:**
```json
{
  "isOwner": true,
  "ownedCode": "B7F8XA",
  "ownedMembers": [
    {
      "userId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b",
      "username": "FriendMaster"
    }
  ],
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

| Field | Type | Description |
|---|---|---|
| `isOwner` | boolean | Indicates if the master user owns a Bonfire group. |
| `ownedCode` | string | 6-character alphanumeric join code for the owned group. Null if none. |
| `ownedMembers` | array | List of guest master users in the owned group. Each member has `userId` and `username`. |
| `isMember` | boolean | Indicates if the master user has joined another user's Bonfire group. |
| `joinedOwnerName` | string | Username of the owner of the joined group. Null if none. |
| `joinedOwnerId` | string (GUID) | User ID of the owner of the joined group. Null if none. |
| `hideMySubProfilesFromOthers` | boolean | If true, local sub-profiles are hidden from Bonfire group members. |
| `hideOthersSubProfilesFromMe` | boolean | If true, remote sub-profiles are hidden locally. |
| `allowHouseholdLanBypass` | boolean | If true, Bonfire members may switch into this account from the local network without its PIN. See `POST /bonfire/settings`. |
| `isAdministrator` | boolean | Whether the caller's account has Jellyfin administrator rights. Provided so a client can warn about what `allowHouseholdLanBypass` gives away; it is not an authorisation signal. |
| `hasPin` | boolean | Whether the caller's master account has a PIN configured. |

### `POST /plugins/profiles/bonfire/settings`
Updates the visibility preferences for sharing profiles in Bonfire crossover homes.

* **Headers:** `Authorization: MediaBrowser Token="<masterToken>"`
* **Request Body:**
```json
{
  "hideMySubProfilesFromOthers": false,
  "hideOthersSubProfilesFromMe": false,
  "allowHouseholdLanBypass": false
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `hideMySubProfilesFromOthers` | boolean | Yes | Hide local sub-profiles from Bonfire group members. |
| `hideOthersSubProfilesFromMe` | boolean | Yes | Hide remote sub-profiles locally. |
| `allowHouseholdLanBypass` | boolean | No | Let Bonfire members switch into **this** account from the local network without entering its PIN. Omit the field to leave the current value alone — sending `false` turns it off. |

* **Response:** `200 OK` on success.

> **`allowHouseholdLanBypass` is a grant, not a convenience setting.** It is the only Bonfire
> setting that widens access rather than narrowing visibility, and it is deliberately written by
> the account's own owner: `bypassPinOnLocalNetwork` belongs to the caller and never reaches
> across a link.
>
> When set, both cross-account rules described under `POST /switch` are lifted for this account —
> including the refusal to enter an account with no PIN at all. Requests Jellyfin does not
> classify as local are unaffected, so remote access still needs the PIN.
>
> Two things a client should surface before writing `true`:
> * If the account is an administrator (`isAdministrator` in `/bonfire/status`), anyone who
>   switches into it gets server and user management.
> * "Local" is decided by Jellyfin's network settings and is relative to the *server*. If the
>   server sits behind a reverse proxy that is not in **Networking → Known Proxies**, every
>   request arrives with the proxy's address and is classified as local.
>
> The field is nullable so a client that predates it — or a cached older script — cannot clear
> the setting by posting the two hide flags alone.

### `POST /plugins/profiles/bonfire/generate`
Generates a new 6-character alphanumeric bonfire join code.

* **Headers:** `Authorization: MediaBrowser Token="<masterToken>"`
* **Response `200 OK`:**
```json
{
  "groupId": "4f5c9e2b",
  "bonfireCode": "B7F8XA",
  "members": []
}
```

| Field | Type | Description |
|---|---|---|
| `groupId` | string | Identifier of the generated Bonfire group. |
| `bonfireCode` | string | Alphanumeric code needed to join the group. |
| `members` | array | List of group members. |

### `POST /plugins/profiles/bonfire/join`
Joins a target group using its 6-character code. Rate limited to 3 failed attempts in 15 minutes.

* **Headers:** `Authorization: MediaBrowser Token="<masterToken>"`
* **Request Body:**
```json
{
  "code": "B7F8XA"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `code` | string | Yes | 6-character alphanumeric Bonfire join code. |

* **Response `200 OK`:**
```json
{
  "message": "Successfully joined Bonfire group.",
  "ownerName": "FriendMaster"
}
```

| Field | Type | Description |
|---|---|---|
| `message` | string | Confirmation message of successful group joining. |
| `ownerName` | string | Username of the bonfire group owner. |

* **Error Responses:**
  * `400 Bad Request`: Invalid code format, invalid Bonfire Code, or attempting to join owned group.
  * `401 Unauthorized`: Caller is not authenticated, or caller is not a master profile.
  * `429 Too Many Requests`: Join rate limit exceeded (3 failed attempts per 15 minutes).

### `POST /plugins/profiles/bonfire/kick`
Kicks a guest master user from the owned bonfire group.

* **Headers:** `Authorization: MediaBrowser Token="<masterToken>"`
* **Request Body:**
```json
{
  "memberId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `memberId` | string (GUID) | Yes | The user ID of the guest member to remove. |

* **Response:** `200 OK` on success.

### `POST /plugins/profiles/bonfire/leave`
Leaves the currently joined bonfire group.

* **Headers:** `Authorization: MediaBrowser Token="<masterToken>"`
* **Response:** `200 OK` on success.

### `POST /plugins/profiles/bonfire/delete-group`
Dissolves the owned bonfire group. All member associations are removed.

* **Headers:** `Authorization: MediaBrowser Token="<masterToken>"`
* **Response:** `200 OK` on success.

---

## Preferences API

Settings the account holder chooses for themselves. These are not server policy: an
administrator cannot set them on someone else's behalf, and each household's answer applies
only to its own accounts.

### `GET /plugins/profiles/preferences`
Returns the calling account's switcher preferences.

* **Headers:** `Authorization: MediaBrowser Token="<token>"`
* **Response `200 OK`:**

```json
{
  "askOnStartup": true,
  "switcherLocation": "button",
  "switcherMode": "gate",
  "masterUserId": "8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a",
  "emergencyCodeConfigured": false
}
```

| Field | Type | Description |
|---|---|---|
| `askOnStartup` | boolean | Whether the "Who's Watching?" screen appears when the client loads. Shown once per browser session, not on every visit to the home screen. |
| `switcherLocation` | string | `"button"` or `"menu"`. See below. |
| `switcherMode` | string | **Deprecated.** Derived from `askOnStartup` for clients written against the 1.3.1 API. |
| `masterUserId` | string (GUID) | The master account these preferences belong to. |
| `emergencyCodeConfigured` | boolean | Whether an administrator has set an emergency disable code. A client should offer the emergency entry point only when this is true. Server-wide, not per-account, and it says nothing about the code itself. Added in 1.3.3. |

| `switcherLocation` | Behaviour |
|---|---|
| `button` | The default. Bonfire injects its own switcher button into the client header, beside Jellyfin's user icon. |
| `menu` | No injected button. A "Switch Profile" row is added above *Sign out* in Jellyfin's own user menu, and a Bonfire section is added to the user profile page. |

A sub-profile token may call this and receives its **master's** preferences, so the switcher
behaves the same way throughout a household rather than changing as profiles are switched.
Unrecognised stored values normalise to the defaults, so a client never has to handle a third
value for either field.

An account that has never set either field inherits the server-wide defaults an administrator
configures under Dashboard → Plugins → Bonfire (`DefaultAskOnStartup` and
`DefaultSwitcherLocation` in the plugin configuration, added in 1.3.4). Choosing for the
account writes the fields and pins them; the default only ever fills a blank. Both endpoints
return the resolved values, so a client never has to know which of the two it is looking at.

`masterUserId` is returned so a client can cache the preferences against the account they
belong to. The bundled `profiles.js` mirrors them into `localStorage`, because the decision of
whether to raise the gate has to be made on page load, long before a request could answer it —
a cache keyed by account is what stops the next person to sign in on a shared browser from
inheriting the previous one's choice.

> **`switcherMode` is deprecated.** It was a single setting in 1.3.1 and could not express
> "ask on startup, but put the switcher in Jellyfin's menu" — the combination requested in
> issue #14. It is still accepted on `POST` and still returned on `GET`, mapped as
> `gate` ⇄ `askOnStartup: true, switcherLocation: "button"` and
> `native` ⇄ `askOnStartup: false, switcherLocation: "menu"`. New clients should read and
> write the two fields; a value posted in `switcherMode` is expanded first and then overridden
> by either newer field present in the same request.

### `POST /plugins/profiles/preferences`
Updates the calling account's switcher preferences.

* **Headers:** `Authorization: MediaBrowser Token="<masterToken>"`
* **Request Body:**

```json
{
  "askOnStartup": true,
  "switcherLocation": "menu"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `askOnStartup` | boolean | No | Omit to leave unchanged. |
| `switcherLocation` | string | No | `"button"` or `"menu"`. Anything else normalises to `"button"`. Omit to leave unchanged. |
| `switcherMode` | string | No | Deprecated; see above. |

* **Response `200 OK`:** the stored values after normalisation, in the same shape as the `GET`
  response. Clients should cache what comes back rather than what they sent.
* **Error Responses:**
  * `401 Unauthorized`: Caller is not authenticated, or is a sub-profile. Unlike the `GET`, only
    the master account may write — a sub-profile changing this would silently rewrite the whole
    household's experience.

---

## Avatar Library API

Profile pictures an administrator publishes for everyone on the server to choose from.

Choosing one **copies** it to the profile rather than referencing it, so each user can crop the
same picture differently and removing a library entry cannot break profiles already using it.

### `GET /plugins/profiles/avatars`
Lists the available avatars. Any authenticated user may call this.

* **Response `200 OK`:**

```json
{
  "allowCustomUploads": true,
  "avatars": [
    {
      "id": "9f2c41a0b7d3",
      "displayName": "Fox",
      "url": "/plugins/profiles/avatars/9f2c41a0b7d3",
      "thumbUrl": "/plugins/profiles/avatars/9f2c41a0b7d3?size=thumb"
    }
  ]
}
```

| Field | Type | Description |
|---|---|---|
| `allowCustomUploads` | boolean | False when the administrator has restricted profile pictures to this library. A client should hide its own upload control when false; the server also refuses `data:` payloads in that state. |
| `avatars[].id` | string | Opaque identifier. |
| `avatars[].displayName` | string | Label for the picker. Free text — escape it on render. |
| `avatars[].url` | string | Full-size image. |
| `avatars[].thumbUrl` | string | Small variant — use this in grids. |

### `GET /plugins/profiles/avatars/{id}`
Serves a library image. Unauthenticated, for the same reason as `/image/{profileId}`: it is
rendered as an `<img src>` and browsers do not attach the Authorization header to image
requests. Returns `404` if the id is unknown or its file is missing.

| Query | Description |
|---|---|
| `size=thumb` | Serves the small variant, falling back to the full-size image if no thumbnail was stored. |

### `POST /plugins/profiles/admin/avatars`
Adds an image to the library. **Administrators only.**

```json
{
  "image": "data:image/jpeg;base64,…",
  "thumb": "data:image/jpeg;base64,…",
  "displayName": "Fox"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `image` | string | Yes | Full-size rendering as a data URL. Max 2 MB decoded. |
| `thumb` | string | No | Small rendering. When absent the full-size image is served in its place — that costs bandwidth but never breaks the picture. |
| `displayName` | string | No | Trimmed to 60 characters. Defaults to `"Avatar"`. |

### Setting a profile's picture from the library

`POST /create` and `POST /update` accept **`avatarLibraryId`**, which takes precedence over
`profileImage`. The server copies the library file to the profile byte for byte — nothing is
decoded, and the only bytes that can land are ones an administrator published.

That distinction matters when `allowCustomUploads` is false. Under copy-on-pick, a client that
crops a library image and posts the result sends something the server cannot tell apart from
any other upload, so a locked-down server refuses `profileImage` data payloads and `http(s)`
URLs outright and accepts `avatarLibraryId` only. The trade is that such a server gives up
per-user cropping — reasonable for a setting whose purpose is a consistent set.

When `allowCustomUploads` is true, either route works: the bundled client crops library picks
and posts them as `profileImage`, so each user can frame the same picture differently.

* **Response `200 OK`:** the created entry, in the same shape as one `avatars[]` element.
* **Error Responses:** `400` if the payload is not a readable image or exceeds the size limit.

> **The plugin does no server-side image processing.** Both renderings are produced by the
> client, which is why two are sent rather than one. Accepted stored formats are JPEG, PNG,
> WebP and GIF; SVG is rejected deliberately, since these files are served back from the
> server's own origin and SVG can carry script.

### `DELETE /plugins/profiles/admin/avatars/{id}`
Removes a library entry and its files. **Administrators only.** Profiles created from it keep
their own copy and are unaffected.

### `POST /plugins/profiles/admin/avatars/settings`
**Administrators only.**

| Field | Type | Required | Description |
|---|---|---|---|
| `disallowCustomAvatarUploads` | boolean | No | When true, profile pictures may only come from the library. Omit to leave unchanged. |

---

## Emergency Disable API

An escape hatch for when the plugin itself is what is making the interface unusable.

> **This is a gate bypass, not an escalation.** It does not unlock other profiles — `/switch`
> still requires the target's PIN — and it does not widen library access, parental ratings or
> tag filters, all of which Jellyfin enforces server-side. What it does remove is the profile
> gate, so on a device already signed in to a master account, anyone holding the code reaches
> that account's library without being asked to pick a profile.

### `POST /plugins/profiles/panic`
Validates the emergency code and, on success, disables the plugin's client script until the
server restarts. `/plugins/profiles/profiles.js` then serves an inert script that tears down
any overlay a previously loaded copy left behind.

**Deliberately unauthenticated.** An administrator locked behind a broken switcher may be
holding a sub-profile's token or none at all, so requiring admin rights would make the feature
useless exactly when it is needed.

```json
{ "code": "correct-horse-battery-staple" }
```

* **Response `200 OK`:** `{ "disabled": true }`
* **Error Responses:**
  * `400 Bad Request`: Incorrect code. Returned identically when no code is configured, so the
    response cannot be used to discover whether a server has the feature armed.
  * `429 Too Many Requests`: More than 5 attempts from this address in the last hour.

The disable flag lives in memory and is never persisted — a flag that survived a restart could
leave a server stuck in a state whose own settings page is the thing you need it to reach.

### `GET /plugins/profiles/panic-state`
Whether the plugin is currently shut down by the emergency disable.

Unauthenticated, and deliberately so: `profiles.js` reads it before anyone has signed in,
which is the situation the emergency disable exists for. It reveals only whether the
plugin is running — already obvious to anyone looking at the screen — and never whether a
code is configured. Served `no-store`, because a cached answer here would outlive the
condition it describes.

```json
{ "disabled": false }
```

### `GET /plugins/profiles/admin/panic-status`
**Administrators only.** The code itself is stored as a PBKDF2 hash and is never returned.

```json
{ "isConfigured": true, "isCurrentlyDisabled": false }
```

### `POST /plugins/profiles/admin/panic-code`
**Administrators only.** Sets or clears the code.

| Field | Type | Description |
|---|---|---|
| `code` | string | At least 10 characters, at most 128. Empty or whitespace clears the code and turns the feature off. |

The minimum length is doing the work an authenticated endpoint would normally get from a
login, since `POST /panic` has no credentials of its own.

---

## Admin API

### `POST /plugins/profiles/admin/settings`
Saves the six server-wide settings the plugin's settings page owns.

Every field is optional and only the fields present in the request are changed. Use this
rather than Jellyfin's generic `POST /Plugins/{id}/Configuration`: that endpoint replaces
the whole plugin configuration document, so a profile, device, group or avatar added
between reading it and writing it back is silently reverted.

* **Headers:** `Authorization: MediaBrowser Token="<adminToken>"`
* **Request Body:**
```json
{
  "maxProfilesPerUser": 5,
  "requireMasterPinForCreation": true,
  "disallowCustomAvatarUploads": false,
  "defaultAskOnStartup": true,
  "defaultSwitcherLocation": "button",
  "indexInjectionMode": "middleware"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `maxProfilesPerUser` | integer | No | Server-wide profile limit. 1–20. |
| `requireMasterPinForCreation` | boolean | No | Require the account's PIN before creating a profile. |
| `disallowCustomAvatarUploads` | boolean | No | Restrict profile pictures to the avatar library. |
| `defaultAskOnStartup` | boolean | No | Default for accounts that have not chosen: show the gate on startup. |
| `defaultSwitcherLocation` | string | No | `button` or `menu`. |
| `indexInjectionMode` | string | No | `file`, `middleware` or `both`. |

* **Response:** `200 OK` on success.

* **Error Responses:**
  * `400 Bad Request`: A field is out of range or not one of its permitted values. The
    message names the bound or the permitted values. Nothing is applied — a request with
    one bad field is rejected whole.
  * `401 Unauthorized`: Caller is not authenticated, or caller is not an administrator.

### `GET /plugins/profiles/admin/mappings`
Retrieves all user profile mappings configured on the server.

* **Headers:** `Authorization: MediaBrowser Token="<adminToken>"`
* **Response `200 OK`:**
```json
{
  "masterUsers": [
    {
      "profileUserId": "8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a",
      "profileName": "john",
      "requiresPin": true,
      "maxProfiles": 5,
      "limitOverride": null
    }
  ],
  "subProfiles": [
    {
      "profileUserId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b",
      "profileName": "Kids",
      "masterName": "john",
      "masterUserId": "8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a",
      "requiresPin": false
    }
  ],
  "injectionSucceeded": true,
  "isVersionStale": false,
  "indexPath": "/usr/share/jellyfin/web/index.html",
  "failureReason": null,
  "serviceAccount": "NT AUTHORITY\\NETWORK SERVICE",
  "isWindows": true,
  "pluginVersion": "1.3.0"
}
```

| Field | Type | Description |
|---|---|---|
| `masterUsers` | array | Master accounts. Each entry has `profileUserId`, `profileName`, `requiresPin`, `maxProfiles`, and `limitOverride`. |
| `subProfiles` | array | Sub-profiles. Each entry has `profileUserId`, `profileName`, `masterName`, `masterUserId`, and `requiresPin`. Group by `masterUserId`, not `masterName` — names change on rename and are not guaranteed unique. |
| `injectionSucceeded` | boolean | Whether the script is reaching the browser. Under `middleware` this comes from `mechanism`; under the legacy modes it means the tags are present in `index.html`. |
| `isVersionStale` | boolean | True when the script is present but `index.html` is not fully current. The switcher works; this is advisory. |
| `indexPath` | string | Resolved absolute path to Jellyfin's `index.html` on the host. |
| `failureReason` | string \| null | Human-readable description of the specific problem, or null when everything is correct. |
| `serviceAccount` | string \| null | OS account the Jellyfin process is running under (e.g. `NT AUTHORITY\NETWORK SERVICE`, `DESKTOP-PC\alice`, `jellyfin`). Null if it could not be determined. Use it to generate an exact permission command instead of guessing between service and desktop installs. |
| `isWindows` | boolean | Whether the server is running on Windows, for choosing between `icacls` and `chown`/`chmod` guidance. |
| `pluginVersion` | string | Running plugin version. Carries a pre-release suffix (e.g. `1.3.1-beta`) on pre-release builds; a stable build has no suffix. |
| `mechanism` | object | How the script is being delivered. See below. |

`mechanism`:

```json
{
  "mode": "middleware",
  "restartRequired": false,
  "middlewareRegistered": true,
  "middlewareActive": true,
  "middlewareServed": 42,
  "middlewareLastServedUtc": "2026-08-25T15:04:11.000Z",
  "middlewareError": null
}
```

| Field | Type | Description |
| --- | --- | --- |
| `mode` | string | `middleware`, `both` or `file`. See **Client Script**. |
| `restartRequired` | boolean | This build was installed or updated after Jellyfin started, so its pipeline hook was never registered and cannot be until the server restarts. Treat as "not finished installing", not as a failure — the previously loaded build usually keeps serving, so the switcher still works. Suppress any injection warning while this is true. |
| `middlewareRegistered` | boolean | The pipeline hook was registered, which happens once per plugin during server startup. False means this build was loaded afterwards — see `restartRequired`. True does **not** mean the hook is working; use `middlewareActive` for that. |
| `middlewareActive` | boolean | The hook has handled at least one request for the web page. This is the signal that it is live. |
| `middlewareServed` | number | Pages served with the tags added, since the server started. |
| `middlewareLastServedUtc` | string \| null | When the last one was served. |
| `middlewareError` | string \| null | Why the most recent handled request could not have the tags added, or null. Reset per request. |

In `middleware` mode, `injectionSucceeded` is derived from `middlewareActive` and
`middlewareError`, not from the contents of `index.html`. `isVersionStale` is always false,
because the tags are generated per request and cannot be out of date.

This endpoint re-reads `index.html` on every call, so in the two legacy modes the injection
fields reflect the file as it is now rather than as it was at server startup.

* **Error Responses:**
  * `401 Unauthorized`: Caller is not authenticated, or caller is not an administrator.

### `POST /plugins/profiles/admin/reset-pin`
Removes the PIN requirement from the specified profile.

* **Headers:** `Authorization: MediaBrowser Token="<adminToken>"`
* **Request Body:**
```json
{
  "profileId": "a90f11cb-42a1-432d-94bb-97cc2d42ef8b"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `profileId` | string (GUID) | Yes | The user ID of the target profile. |

* **Response:** `200 OK` on success.

* **Error Responses:**
  * `401 Unauthorized`: Caller is not authenticated, or caller is not an administrator.
  * `404 Not Found`: Profile mapping not found.

### `POST /plugins/profiles/admin/retry-injection`
Re-runs the on-disk injection into Jellyfin's `index.html` and returns the resulting status.
Lets an administrator apply a file-permission fix and confirm it worked without restarting
the server.

Only meaningful in the `both` and `file` modes. Under `middleware` there is nothing on disk
to retry, and the call reports the current state without writing anything.

* **Headers:** `Authorization: MediaBrowser Token="<adminToken>"`
* **Request Body:** none.

* **Response `200 OK`:**
```json
{
  "injectionSucceeded": true,
  "isVersionStale": false,
  "indexPath": "/usr/share/jellyfin/web/index.html",
  "failureReason": null,
  "serviceAccount": "NT AUTHORITY\\NETWORK SERVICE",
  "isWindows": true,
  "pluginVersion": "1.3.0"
}
```

Returns the same injection-status fields as `GET /plugins/profiles/admin/mappings`, including
`mechanism`, reflecting the state *after* the retry.

`injectionSucceeded: true` with `isVersionStale: false` means the script is installed and
current. `isVersionStale: true` alone is advisory: the switcher is running, and clients pick
up new script versions within `max-age` on their own (see `GET /plugins/profiles/profiles.js`).

`failureReason` names the cause, including whether Jellyfin can write `index.html`, which is
tested directly rather than inferred. Combine it with `serviceAccount` to present an exact
permission command.

* **Error Responses:**
  * `401 Unauthorized`: Caller is not authenticated, or caller is not an administrator.

### `POST /plugins/profiles/admin/set-profile-limit`
Overrides the maximum number of profiles a master user is allowed to create.

* **Headers:** `Authorization: MediaBrowser Token="<adminToken>"`
* **Request Body:**
```json
{
  "userId": "8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a",
  "maxProfiles": 8
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `userId` | string (GUID) | Yes | The user ID of the master account to override. |
| `maxProfiles` | integer | No | The custom limit, 1–20. Pass null to remove the override. |

* **Response:** `200 OK` on success.

* **Error Responses:**
  * `400 Bad Request`: Maximum profiles must be between 1 and 20, or plugin configuration missing.
  * `401 Unauthorized`: Caller is not authenticated, or caller is not an administrator.

### `GET /plugins/profiles/admin/audit-logs`
Retrieves recent profile switching event logs.

* **Headers:** `Authorization: MediaBrowser Token="<adminToken>"`
* **Response `200 OK`:**
```json
[
  {
    "timestamp": "2026-06-12T20:52:00Z",
    "masterUsername": "john",
    "targetUsername": "Kids",
    "deviceName": "Chrome",
    "client": "Jellyfin Web",
    "ipAddress": "192.168.1.50"
  }
]
```

| Field | Type | Description |
|---|---|---|
| `timestamp` | string (ISO-8601) | Timestamp of the profile switch event. |
| `masterUsername` | string | Username of the master account owner. |
| `targetUsername` | string | Username of the profile switched to. |
| `deviceName` | string | Recorded device name. |
| `client` | string | Recorded client name. |
| `ipAddress` | string | Client IP address. |

* **Error Responses:**
  * `401 Unauthorized`: Caller is not authenticated, or caller is not an administrator.
