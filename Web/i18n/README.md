# Translations

Everything a person sees in the profile gate and switcher comes from one catalogue of
strings. English is built into `profiles.js`; every other language is one JSON file in
this folder.

**Adding a language is adding one file here. There is nothing else to edit** — not the
`.csproj`, not `profiles.js`, not the controller. The build embeds `Web/i18n/*.json` by
wildcard, the server lists what it finds, and it tells the browser. If you find yourself
registering your language in a second place, something has regressed; please say so in
the pull request.

## Add a language

**1. Copy an existing file.** Name it for the language, using the code a browser sends:

```
cp Web/i18n/fr.json Web/i18n/de.json
```

`de.json`, `es.json`, `pt-BR.json`, `zh-Hans.json`. Two or three letters, optionally
followed by region or script subtags. Case is not significant when matching.

Use a plain language code (`pt.json`) unless the difference between regions actually
matters. When both exist, the more specific file wins: a browser asking for `pt-BR` takes
`pt-BR.json` if it is there and `pt.json` if it is not.

**2. Translate the values. Leave every key exactly as it is.** The key on the left is an
identifier, not text:

```json
"gate.whosWatching": "Wer schaut?",
"common.cancel": "Abbrechen",
```

**3. Check it:**

```
node Web/i18n/validate.js de
```

No dependencies, any recent Node. It reports missing keys, keys that do not exist,
broken placeholders and broken HTML. Run it before opening the pull request.

It also checks two things that are ours rather than yours, so a red run may not be your
file: an English string nobody uses (a translator would be working for nothing), and
user-facing text in the stylesheet, which `t()` cannot reach at all.

**4. Build and look at it.** Set your browser's preferred language and open the profile
gate. If your language does not appear, see *It is not showing up* below.

## Rules for the values

**Keep every `{placeholder}`.** They are filled in at runtime, and the surrounding
sentence usually cannot be reordered around them safely — but the placeholder itself must
survive:

```json
"profilePage.body": "Vous regardez en tant que <strong>{name}</strong>."
```

A dropped `{name}` shows the reader a blank where their profile name should be. An
invented one prints the braces verbatim.

**Keep every HTML tag.** Some strings contain `<strong>` or `<br>`. These are rendered as
markup; a missing closing tag breaks the layout of the panel it sits in. Do not add tags
that the English string does not have.

**Do not translate anything inside braces or angle brackets.** Only the words between
them.

**Leave a key out rather than guessing.** A key you omit falls back to English on its own.
A key translated wrongly does not. Partial files are fine and ship happily.

**Watch the length.** These strings sit on buttons and in a grid that also has to work on
a television at three metres. If a translation is much longer than the English, check it
on a narrow window before opening the pull request.

## What is not translated

The **admin settings page** (`Web/profilesDashboard.html`) is deliberately English only.
It is read by whoever runs the server, its wording changes often, and much of it is
diagnostics and shell commands. Translating it would triple the catalogue for the one
audience most able to read the original.

Everything a household member sees — the gate, the switcher, profile forms, PIN prompts,
errors — is translated.

## It is not showing up

Work down this list; it is in order of how often each one is the answer.

- **The browser is not asking for your language.** Detection reads
  `navigator.languages`, not any Jellyfin setting. Check `navigator.languages` in the
  browser console — the language has to be in that list.
- **The plugin was not rebuilt.** The file is embedded at build time. `dotnet build -c
  Release`, then reinstall or restart.
- **Jellyfin was not restarted.** Embedded resources are read once per process.
- **The file is not valid JSON.** `node Web/i18n/validate.js` will say so. A trailing
  comma is the usual cause.
- **The filename is not a locale code.** `german.json` will never match; `de.json` will.
- **Check what the server thinks it has:** open
  `/plugins/profiles/i18n/de.json` in a browser. A 404 means the file was not embedded —
  almost always a stale build.

## How it works, in three sentences

English lives inline in `profiles.js` as `EN_STRINGS`, so the gate can render before any
network request and still renders if the server is unreachable. On startup the client
picks the first of the browser's preferred languages that the server says it has, fetches
that one file, and swaps the whole catalogue over; every lookup falls back to English per
key, so a partial file is never a half-blank screen. Nobody who reads English pays for
any of this — no request is made at all.

The full reference, including the endpoint and its caching, is in
[`docs/developer-api.md`](../../docs/developer-api.md#adding-a-translation).
