# Contributing

Thanks for helping. Open pull requests against the `beta` branch.

## Build and test

You need the .NET 9 and .NET 10 SDKs, and Node 20 or later.

```
dotnet build -c Release
tests/run.sh
```

On Windows, run `tests\run.ps1` instead. It runs every test, and CI runs the same
command, so if it passes for you it passes in CI.

To try a build on your server, copy `bin/Release/net9.0/Jellyfin.Profiles.dll` into
Jellyfin's plugins folder and restart Jellyfin. On Docker, restart the container.

## Adding a translation

1. Copy `Web/i18n/fr.json` and name it for the language code, such as `de.json` or
   `pt-BR.json`.
2. Translate the values. Leave the keys as they are.
3. Check it with `node Web/i18n/validate.js de`.
4. Add the language to the list in the README and in the translation guide.

The [translation guide](https://github.com/AHouseOfBards/Bonfire/blob/beta/Web/i18n/README.md)
has the details.

## Changing the client script

The client is `Web/profiles.js`. Its styles are in `Web/styles.css`.

- **Edit it in an editor.** Don't change it with `sed`, shell heredocs or one-line
  scripts. The shell can corrupt backslashes and `$`, and edits can silently fail to
  apply.
- **Run `tests/run.sh`.** Checking that the file parses isn't enough. The tests run it.
- **Use older JavaScript.** It must run on LG TVs from 2019, which use Chromium 53. Don't
  use `?.`, `??` or object spread (`{ ...x }`). Use `Object.assign` instead of spread.
  `tests/js/jsbaseline.js` catches these.
- **Check jellyfin-web before using a selector.** Find the element in the
  [jellyfin-web source](https://github.com/jellyfin/jellyfin-web/tree/release-10.11.z/src)
  rather than going from memory. Then add the selector to
  `tests/upstream-selectors.json` with the file you found it in. The tests fail on any
  selector that isn't listed.
- **Keep the route check light.** `checkRoute` runs twice a second on every page. Only
  add to it what must run every time, and avoid new page-wide queries there.
  `tests/js/routetick.js` counts them.
- **Translate what people see.** Put the English in `EN_STRINGS` and show it with
  `t('key')`. The admin dashboard is English only.

## Style

Text people see is short and direct. Use one sentence where one will do, and say how to
fix the problem.

Code comments explain why: what was tried, what broke, and how it failed. Keep them next
to the code they describe.

## Pull requests

Say what you changed and how you tested it. For a bug fix, include a test that fails
without your change. If you couldn't test something, such as a TV app, say so.
