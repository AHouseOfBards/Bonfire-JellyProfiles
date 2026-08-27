# Contributing

Thanks for looking. Two things worth knowing before you start: the test suite runs in one
command and CI runs the same one, and translations are deliberately the easiest thing to
contribute.

## Build and test

Requires the .NET 9 SDK and any recent Node.

```
dotnet build -c Release      # the plugin
tests/run.sh                 # everything — tests\run.ps1 on Windows
```

`tests/run.sh` builds the plugin with warnings as errors and runs all 19 harnesses (14
JavaScript, 5 C#, about 900 assertions). Pull requests run exactly that, so if it is green
locally it will be green on CI.

To try a build on a real server, copy `bin/Release/net9.0/Jellyfin.Profiles.dll` into your
Jellyfin plugins directory and **restart the server process** — Jellyfin cannot unload a
plugin assembly, so the old code keeps running until it does. On Docker, restart the
container; the dashboard's Restart button is often not enough.

## Adding a translation

This is one file. Copy `Web/i18n/fr.json`, name it for the language code a browser sends
(`de.json`, `pt-BR.json`, `zh-Hans.json`), translate the values and leave every key alone.

```
node Web/i18n/validate.js de
```

That checks missing keys, keys that do not exist, dropped `{placeholders}` and dropped
HTML tags. CI runs it over every file, so a broken catalogue cannot merge.

Three things the validator cannot check for you:

- **Leave a key out rather than guessing.** An omitted key falls back to English on its
  own; a wrong translation does not.
- **Keep every `{placeholder}` and every HTML tag.** They are filled in and rendered at
  runtime.
- **Watch the length.** These strings sit on buttons and in a grid that has to work on a
  television at three metres. French runs 1.7–2.75× English on short labels. Check a long
  one against a narrow window.

`Web/i18n/README.md` has the full guide, including a list of reasons a new language does
not show up, ordered by how often each one is the answer.

## Changing the client script

`Web/profiles.js` is one large IIFE and is being split; until then a few things bite:

- **Do not edit it through a shell heredoc.** Backslashes, `$` and regexes get mangled,
  and a CRLF/LF mismatch makes a pattern match nothing while reporting success. Use an
  editor, or write the script to a file and run that.
- **`node --check` is not enough.** It passes on code that is valid and still throws on
  the first call — that is exactly how 1.5.2 shipped broken. Run `tests/run.sh`.
- **Read the jellyfin-web component before changing any injection selector.**
  `tests/js/selectors.test.js` will fail if you add one without recording what you checked
  it against in `tests/upstream-selectors.json`. Ten selectors currently in the code exist
  nowhere in jellyfin-web; that check is why there will not be an eleventh.

## Style

**User-facing text is short and direct.** One sentence where one will do. Say the fix, not
the diagnosis. Form hints are labels, not paragraphs.

**Code comments are the opposite.** Comments explaining *why* something is the way it is —
particularly what was tried and how it failed — are the most valuable thing in this
codebase. Write them, and keep them next to the code they describe.

Everything a household member sees goes through `t()`. The admin dashboard is deliberately
English only.

## Pull requests

Work against `beta`. Say what you changed and how you checked it — if it is a bug fix, the
most useful thing you can include is a test that fails without your change. If you cannot
test something (a television, a Tizen build, a client you do not own), say so plainly
rather than implying it was verified; that is genuinely more useful than a confident
guess.

More detail on all of this in [`CLAUDE.md`](CLAUDE.md), which is the working notes for the
repository.
