# Contributing

Thanks for helping. Pull requests go against `beta`.

## Build and test

You need the .NET 9 and .NET 10 SDKs and a recent Node.

```
dotnet build -c Release      # the plugin
tests/run.sh                 # every test (tests\run.ps1 on Windows)
```

`tests/run.sh` builds with warnings as errors and runs every JavaScript and C# test. CI
runs the same command, so green locally means green on CI.

To try a build on a real server, copy `bin/Release/net9.0/Jellyfin.Profiles.dll` into
Jellyfin's plugins folder and restart the server process. On Docker, restart the
container; the dashboard's Restart button often isn't enough.

## Adding a translation

Copy `Web/i18n/fr.json`, name it for the language code (`de.json`, `pt-BR.json`), and
translate the values. Leave the keys alone. Then check it:

```
node Web/i18n/validate.js de
```

The full guide is in [Web/i18n/README.md](Web/i18n/README.md).

## Changing the client script

`Web/profiles.js` is one large file. A few things to know:

- **Don't edit it through a shell heredoc.** Backslashes, `$` and regexes get mangled,
  and line-ending mismatches make edits silently miss. Use an editor, or write the script
  to a file and run that.
- **`node --check` isn't enough.** Code can parse and still fail when it runs. That's how
  1.5.2 shipped broken. Run `tests/run.sh`.
- **Keep it working on older TVs.** webOS 4.5 runs Chromium 53, so avoid newer syntax
  like `?.` and object spread `{ ...x }`. `tests/js/jsbaseline.js` checks this.
- **Read the jellyfin-web source before changing a selector.** Record what you checked
  it against in `tests/upstream-selectors.json`, or `tests/js/selectors.test.js` fails.

## Style

**Text people see is short and direct.** One sentence where one will do. Say the fix,
not the diagnosis.

**Code comments explain why.** What was tried, what broke and how it failed are the most
useful comments here. Keep them next to the code they describe.

Everything a household member sees goes through `t()` so it can be translated. The admin
dashboard is English only.

## Pull requests

Say what you changed and how you checked it. For a bug fix, the most useful thing to
include is a test that fails without your change. If you couldn't test something (a TV,
a Tizen build, an app you don't have), say so.
