# Working on Bonfire

Operational rules for this repository. Each one is here because ignoring it cost a
release, and most of them now have a check behind them — the rule and the check are
listed together so it is obvious which ones are still only a promise.

## Run this before you push

```
tests/run.sh          # or tests\run.ps1 on Windows
```

Builds the plugin (Release, `-warnaserror`) and runs all 24 harnesses — 18 JavaScript
and 6 C#, about 1,100 assertions. CI runs the same command, so the desk and the pipeline
cannot disagree about what "the tests pass" means.

`node --check Web/profiles.js` is **necessary and not sufficient.** It passed against the
defect that made 1.5.2 and 1.5.3-beta dead on arrival: a stray backtick inside the
stylesheet template literal produced valid JavaScript that threw on the first call.
`tests/js/inject.test.js` is what catches that class of bug, because it executes the
startup path instead of reading it.

## The rules

**Never edit `Web/profiles.js` through a shell heredoc.** Eleven recorded ways this has
silently produced broken or misplaced code — shell mangling of backslashes and `$`,
CRLF/LF mismatches making a pattern match zero times, regexes eaten by the shell. Use an
editor tool, or write the script to a file first and run it. This bit again while writing
the plan that produced these rules.

**Read the jellyfin-web component before touching an injection selector.**
Do not infer markup from memory or from how it looked in an older release. Fetch it:

```
curl -sL https://raw.githubusercontent.com/jellyfin/jellyfin-web/release-10.11.z/<path>
```

*Check:* `tests/js/selectors.test.js` fails on any selector used by an injection function
that is not recorded in `tests/upstream-selectors.json`, with the file it was verified in.
`tests/js/selectors.verify.js` re-checks that ledger against upstream (needs network, not
part of CI). Ten selectors currently in the code exist nowhere in jellyfin-web; they are
recorded as `dead` and tracked, which is what stopped them being discovered a fourth time.

**A new harness must FAIL against the build carrying the bug before it is allowed to
pass.** Every JS harness takes the source path as `argv[2]` precisely so it can be pointed
at an older checkout:

```
node tests/js/inject.test.js /path/to/old/profiles.js
```

This is the only thing that distinguishes a test from a restatement of the code.
`session.js` was green for three releases while the fix it covered had never once
executed, because it modelled a sequence no browser produces.

**Distrust any check that computes an aggregate, or that answers a coarser question than
the one you care about.** Three releases have shipped a bug past a green check of this
shape:

- A CSS check compared specificity *totals*, so a guard on one state counted as cover for
  a different unguarded state. It passed against the build carrying the bug.
- A rule was asserted *present* — and was, while being outranked by a later rule with the
  same specificity. Resolve the cascade; do not string-match.
- `node --check` answered "does it parse" when the question was "does it run".

Enumerate and match pairwise. Aggregates hide which member failed.

**Do not weaken a check to make it pass.** A weaker second copy of coverage is how a false
green gets built. If a harness starts failing, find out whether the code or the harness is
wrong before touching either.

## Releasing

`InformationalVersion` in `Jellyfin.Profiles.csproj` decides the channel, and it is the
only place a pre-release label can live (Jellyfin requires a purely numeric assembly
version).

| `InformationalVersion` | Manifest | Branch | Release |
| --- | --- | --- | --- |
| `1.6.0-beta` | beta manifest | `beta` | prerelease |
| `1.6.0` | stable manifest | `main` | latest |

Sequence for a release:

1. Bump `<Version>` and `<InformationalVersion>`.
2. Add the `versions[]` entry to `manifest.json` on the channel branch, with
   `"checksum": "0"` — the workflow stamps the real one. It **fails** if the entry is
   missing, and it also fails if the same version exists in the other channel's manifest.
3. Tag `v<Version>`, matching `<Version>` exactly. The workflow cross-checks and refuses a
   mismatch.

A version lives in exactly one manifest. Both declare the same plugin GUID, so Jellyfin
merges them for anyone subscribed to both, and a version in both would keep a placeholder
checksum in one copy — offering an install that fails.

Release-note format is fixed: bold section headers (`**Fixed**` / `**Changed**` /
`**Added**`), blank line after each header or the list will not render, one line per
change, the symptom in the user's words, issue number in brackets at the end. The
reasoning belongs in the commit message.

## House style

**User-facing copy is short and direct.** One sentence where one will do, two at most.
Say the fix, not the diagnosis. Drop reassurance and throat-clearing. Form hints are
labels, not paragraphs — under about ten words.

**Code comments are the opposite, and are wanted.** The *why-not* comments — what was
tried, what broke, what the failure looked like from outside — are the best thing in this
codebase. Keep them, and move them with the code they explain. A comment that has
outlived its code is a wrong specification, so fix it when the code changes.

**Everything a household member sees goes through `t()`.** English lives inline in
`EN_STRINGS`; every other language is one JSON file in `Web/i18n/`. Adding a language is
adding one file — the `.csproj` globs them, the server lists what it finds, and the client
list is rewritten as the script is served. If you find yourself registering a language in
a second place, something has regressed. The admin dashboard is deliberately English.

*Check:* `node Web/i18n/validate.js` (missing keys, unknown keys, dropped placeholders,
dropped HTML tags), and CI runs it.

## Layout

| Path | |
| --- | --- |
| `Web/profiles.js` | The client script. One ~8,000-line IIFE; a split is planned. |
| `Web/profilesDashboard.html` | Admin page, with its JavaScript inline. |
| `Controllers/` | `ProfilesController` (42 routes) over `ProfilesBaseController`. |
| `tests/js/`, `tests/cs/` | The harnesses. `_lib.js` holds shared plumbing. |
| `tests/upstream-selectors.json` | What every injection selector was verified against. |
| `docs/developer-api.md` | The API reference. All 42 routes; keep it that way. |

## Two things that are easy to get wrong

**Authorisation is hand-rolled per endpoint.** `ProfilesController` carries a class-level
`[AllowAnonymous]`, because the script and image endpoints must work before sign-in. There
is no framework backstop: **a new route without an explicit check is silently public.**
Every route either calls `GetCurrentUserId()` and returns 401, or checks
`Policy.IsAdministrator`. Seven routes are deliberately anonymous and are listed in
`docs/developer-api.md`.

**Never lock the configuration object.** `Plugin.Configuration` is replaced by Jellyfin
whenever an administrator saves the plugin's settings, so a monitor taken on it before
that save is on an orphaned object and two writers get inside at once. All 26 sites now
take `ProfilesBaseController.ConfigLock`, a single static lock, and every
`SaveConfiguration()` sits inside the block that made the change it is saving.

*Check:* `tests/cs/configlock` calls the real `UpdateConfiguration` to show the instance is
replaced, walks two threads through the old pattern deterministically and catches both
inside the critical section, then names every `lock (...)` in the plugin one at a time and
fails any taken on something other than a known static object.

**Still open, from the same defect:** 26 of those sites read
`var config = Plugin.Instance?.Configuration;` *before* taking the lock, so a swap between
the read and the lock leaves them mutating the orphan. The lock now holds, but the
reference can be stale. The dashboard no longer triggers it — `POST admin/settings` mutates
in place — but Jellyfin's own `POST /Plugins/{id}/Configuration` still can. Read the
configuration **inside** the lock in anything new; `UpdateAdminSettings` is the shape to
copy. Tracked as P2-25.
