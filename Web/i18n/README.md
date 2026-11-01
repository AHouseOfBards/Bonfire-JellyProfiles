# Translations

English is built into `profiles.js`. Every other language is one JSON file in this
folder.

Adding a language means adding one file here and nothing else. The build picks up every
file in this folder, and the server tells the browser which languages it has.

## Add a language

**1. Copy an existing file** and name it for the language code a browser sends:

```
cp Web/i18n/fr.json Web/i18n/de.json
```

For example `de.json`, `es.json`, `pt-BR.json` or `zh-Hans.json`. Use the plain code
(`pt.json`) unless regions really differ. If both exist, the more specific one wins: a
browser asking for `pt-BR` gets `pt-BR.json`, or `pt.json` if there isn't one.

**2. Translate the values, not the keys.** The key on the left is an identifier:

```json
"gate.whosWatching": "Wer schaut?",
"common.cancel": "Abbrechen",
```

**3. Check it:**

```
node Web/i18n/validate.js de
```

It needs only Node, and reports missing keys, unknown keys, empty values, and broken
placeholders or HTML. It also checks two things in the code rather than your file, so a
failure isn't always yours.

**4. Try it.** Build the plugin, set your browser's language and open the profile screen.

## Rules for the values

- **Keep every `{placeholder}`.** They're filled in when shown. A missing `{name}` leaves
  a blank where the profile name should be.

  ```json
  "profilePage.body": "Vous regardez en tant que <strong>{name}</strong>."
  ```

- **Keep every HTML tag**, such as `<strong>` and `<br>`, and don't add new ones.
- **Translate only the words**, not anything inside braces or angle brackets.
- **Leave a key out rather than guess.** Missing keys show in English. Partial files are
  fine.
- **Watch the length.** Many strings sit on buttons that also have to work on a TV. If a
  translation is much longer than the English, check it in a narrow window.

## What isn't translated

The admin settings page is English only. It's read by whoever runs the server, changes
often, and is mostly settings and diagnostics. Everything a household member sees is
translated.

Theme names (Crimson, Snowfall and the rest) are proper names, like Bonfire, and stay as
they are in every language.

## My language doesn't show up

Most likely first:

- **The browser isn't asking for it.** Bonfire uses the browser's language list, not a
  Jellyfin setting. Check `navigator.languages` in the browser console.
- **The plugin wasn't rebuilt.** Files are built into the plugin.
- **Jellyfin wasn't restarted.**
- **The file isn't valid JSON.** The validator will say so. A trailing comma is the usual
  cause.
- **The file name isn't a language code.** `german.json` won't match; `de.json` will.
- **The server doesn't have it.** Open `/plugins/profiles/i18n/de.json` in a browser. A
  404 means the file wasn't built in.

## How it works

English is built in, so the profile screen shows straight away, even if the server is
unreachable. On startup the browser picks the first of its languages the server has,
downloads that one file, and falls back to English for any missing key. English readers
download nothing extra.

The endpoint and its caching are described in
[`docs/developer-api.md`](../../docs/developer-api.md#translations).
