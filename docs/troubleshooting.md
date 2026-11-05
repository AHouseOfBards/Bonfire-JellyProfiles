# Troubleshooting

## The switcher doesn't appear

### Restart the server

Jellyfin can't unload a plugin. After you install or update Bonfire, the old version
keeps running until the server process restarts. The settings page shows both the version
that's running and the one that's installed, so you can see when this is the problem.

On Docker, the dashboard's **Restart** button often doesn't restart the process. Restart
the container instead.

Installing on a running server can also make the settings page report that the script
failed, while the switcher keeps working. A restart fixes that too.

### No script tag in `index.html` is normal

Bonfire does not edit `index.html` by default, and hasn't since 1.4.1. It adds its
script as the page is served, so nothing is written to disk and nothing needs redoing
after a Jellyfin update.

To check it's working, open **Dashboard → Plugins → Bonfire → Troubleshoot → Advanced → Client script**.
It shows which method is in use and how many pages it has served.

### It works in a browser but not in an app

Bonfire can only appear in apps that load the web client from your server. Apps that
ship their own copy never see it. See [Apps](clients.md) for which apps
work and how.

Parental controls still apply in every app, because the server enforces them.

## Writing to `index.html` (legacy modes)

Under **Troubleshoot → Advanced → Client script** you can choose how the script is added:

| Method | What it does |
| --- | --- |
| **Serve it live, never touch index.html** | The default. Removes any tags an older version wrote. |
| **Patch index.html, fall back to serving it live** *(legacy)* | For setups that need the tag in the file, such as a proxy that caches the page. |
| **Patch index.html only** *(legacy)* | How every release up to 1.4 worked. |

Both legacy modes need Jellyfin to be able to write to its own web files, which it often
can't on Docker, Linux packages or protected Windows folders. If the write fails, the
settings page shows the command to fix it for your system.

If you haven't chosen a legacy mode, you don't need to grant that access.

## Samsung Tizen

The Tizen app builds the web client into its `.wgt` package instead of loading yours.
Add the **Bonfire loader** to the package once, and it loads Bonfire from your server
each time the app starts. Plugin updates then reach the TV without another rebuild.

1. Get the package's files: the `.wgt` you build, or the one Apps2Samsung prepares.
2. Copy [`Web/tizen/bonfire-loader.js`](../Web/tizen/bonfire-loader.js) into its `www/` folder.
3. In `www/index.html`, add this line just before `</body>`:

   ```html
   <script src="bonfire-loader.js"></script>
   ```

4. If you added Bonfire the old way, remove the `<script>` tag for `profiles.js` and the
   copied file. The loader stays out of the way while the old copy is there.
5. Install the package as usual.

If nothing appears, open the app's web inspector and look for a message starting with
`Bonfire loader:`.

## A profile's library access keeps reverting

Each profile keeps its own library list and re-applies it when someone enters the
profile. That's what restores a profile after Jellyfin resets a user's settings.

Adding or removing a library in **Dashboard → Users** sticks from 1.7.2: the profile's
list follows it. Two exceptions:

- A profile set to every library (nothing ticked in **Edit Profile → Libraries**) always
  gets every library its main account has. Tick the ones it should have instead.
- On a profile not entered since the upgrade, a removal made before its first sign-in
  comes back once. Remove it again.

A profile can never get a library its main account doesn't have.

## I can't use the interface or reach the settings page

Set an **emergency disable code** before you need it, under
**Dashboard → Plugins → Bonfire → Security**. Entering it on any Bonfire screen, or
pressing `Ctrl+Shift+B`, turns Bonfire off until Jellyfin restarts.

If Bonfire's script doesn't load at all, the code can't run. Restart Jellyfin or delete
the plugin folder. See [Limitations](limitations.md) for what the code
does and doesn't unlock.

## Where do I get avatars?

Bonfire includes thirty starter avatars from Microsoft's Fluent Emoji. You can turn them
off with **Include the starter avatars** under
**Dashboard → Plugins → Bonfire → Experience → Avatar library**. Profiles already using one keep it.

Film and TV characters aren't included, because they belong to their studios. Add your
own on the same page:

- **Upload a folder** from the computer you're on.
- **Copy from folder** copies from a path on the server, such as `/config/avatars` on
  Docker. Easier for a large set.

JPEG, PNG, WebP and GIF all work. Pictures are cropped and resized when added. Turn on
**Only allow avatars from this library** to make profiles choose from your set instead
of uploading their own.

## Reporting a problem

Open an issue at <https://github.com/AHouseOfBards/Bonfire-JellyProfiles/issues> and
include:

- the Bonfire version, and whether it's from the stable or beta channel
- your Jellyfin version, and how it's installed (Docker, package, Windows)
- the browser, app or TV model
- the text from **Troubleshoot → Copy report**
- whether the server has been restarted since installing or updating
