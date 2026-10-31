# Known Limitations

## Custom themes

Some themes and skin managers push the Bonfire button out of place. Switch to
**Jellyfin's user menu** under **Settings → Switcher Style**, which removes the button.
Please also open an issue naming the theme.

## Profiles are managed from the home screen

Create and edit profiles from the switcher, not the admin dashboard.
**Dashboard → Plugins → Bonfire** holds the server-wide settings, the avatar library,
PIN resets, TV and app sign-in, and the emergency disable code.

## Emergency disable code

Turns Bonfire off until Jellyfin restarts. It's for when Bonfire has made the web
interface hard to use, including the page you'd need to uninstall it.

Set one under **Dashboard → Plugins → Bonfire → Advanced**. To use it, enter it on any
Bonfire screen or press `Ctrl+Shift+B`. It's off until you set one.

Things to know:

- It does **not** unlock other profiles or loosen any parental controls.
- It **does** skip the profile screen. On a device signed in to a main account, anyone
  with the code gets that account.
- No password is asked for, so make it long. It allows five tries an hour per address,
  and every use is logged.
- If Bonfire's script doesn't load at all, the code can't run. Restart Jellyfin or delete
  the plugin folder instead.
