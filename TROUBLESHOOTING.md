# Troubleshooting

## "Client script injection failed", or the switcher never appears

### First, the one that catches most people

**Bonfire does not edit `index.html` by default, and has not since 1.4.1.** An
`index.html` with no plugin tags in it is a **healthy** install, not a failed one. If you
have opened the file looking for a `<script>` tag and not found one, nothing is wrong.

Since 1.5 the script is added **as the page is served**. Nothing is written to disk, so
there is nothing to re-apply after a Jellyfin update and no file permissions to grant.

Check **Dashboard → Plugins → Bonfire → Advanced → Client script**. It reports which
mechanism is in use and how many pages it has served.

### After an update, the old version keeps running

Jellyfin cannot unload a plugin assembly. After an update the **old code keeps running
until the server process restarts** — so the interface can change while the behaviour
does not.

On Docker, the dashboard's **Restart** button often does not restart the process. Only
restarting the container does.

The settings page names both versions — "running 1.5.6, 1.5.8 installed, starts on next
server start" — so you can tell this apart from a real failure without guessing.

### The plugin loaded after Jellyfin had already started

Installing or updating on a running server loads the assembly too late to hook the
request pipeline: Jellyfin calls `RegisterServices` on every plugin during host startup,
and that is the only place the hook can be added.

The symptom is confusing on purpose to nobody: the previous build's middleware is
usually still in the pipeline, so **the switcher keeps working** while the settings page
reports that injection failed. The fix is a restart, and only a restart.

## Editing `index.html` — the legacy modes

Under **Advanced → Client script** on the plugin configuration page:

| Method | What it does |
| --- | --- |
| **Serve it live, never touch index.html** | The default. Any tags an earlier version wrote are taken back out. |
| **Patch index.html, fall back to serving it live** *(legacy)* | For a setup that needs the tag physically in the file, such as a proxy that caches the page. |
| **Patch index.html only** *(legacy)* | What every release up to 1.4 did. No fallback. |

Both legacy modes need Jellyfin to be able to write to its own web files, which it often
cannot on Docker, on Linux packages, or in a protected Windows directory. If that write
fails, the configuration page prints the exact command for your host and the account
Jellyfin is running under.

**Unless you have deliberately picked one of these modes, there is no reason to grant
that access.**

## The switcher works in a browser but not in an app

This is a question about the app, not about your server. Bonfire adds a script to the
web client your server hands out, so it can only appear in an app that **loads that web
client from your server**. An app that ships its own copy, or that is natively written,
never sees the script.

See [Client Compatibility](README.md#client-compatibility) in the README for which is
which.

**Parental controls still apply on every client**, including the ones that cannot show
the switcher. Library access, maximum parental rating and tag filters live on the
Jellyfin user account and are enforced by the server.

## Samsung Tizen

The Tizen app builds its own copy of the web client into the `.wgt` package rather than
fetching yours, so the plugin cannot inject itself at runtime. It has been reported
working with Bonfire bundled into the package at build time — but **the package has to be
rebuilt to pick up plugin updates**. Nothing reaches it automatically, so a fix released
today is not on your TV until you rebuild.

## The interface is unusable and I cannot reach the settings page

Set an **emergency disable code** before you need one, under **Dashboard → Plugins →
Bonfire → Advanced**. Entering it on any Bonfire screen, or pressing `Ctrl+Shift+B`,
switches the client script off until Jellyfin restarts.

It cannot rescue every failure: if the script fails to load at all there is nothing for
the code to run in. For that case, restart Jellyfin or delete the plugin folder.

See **Known Limitations** in the README for what the code does and does not unlock — it
is deliberately not a master key.

## Reporting something

Please open an issue at
<https://github.com/AHouseOfBards/Bonfire-JellyProfiles/issues>.

Worth including, because it is almost always what gets asked first:

- the plugin version, and whether it came from the stable or beta channel;
- your Jellyfin server version, and how it is installed (Docker, package, Windows);
- the client — browser, app, or TV model;
- what **Advanced → Client script** says;
- whether the server has been restarted since the plugin was installed or updated.
