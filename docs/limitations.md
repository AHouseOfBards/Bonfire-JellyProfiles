# Known Limitations

## Skin Manager / custom themes

Custom themes and skin managers can leave the Switch Profile button misaligned. The
**Jellyfin menu** style under *Settings → Switcher Style* removes the injected button and
puts the switcher on your profile page instead. Either way, please open an issue with the
name of the theme.

## Profile creation is on the home screen, not the admin dashboard

Profiles are created and managed from the Switch Profile button on the Jellyfin home
screen. **Dashboard → Plugins → Bonfire** is for server-wide settings, the avatar library,
administrator PIN resets, TV and app sign-in, and the emergency disable code.

## Emergency disable code

A code that shuts Bonfire off **until Jellyfin restarts**, for when the plugin has made
the web interface hard to use — including the settings page you would need to uninstall
it. Set one under **Dashboard → Plugins → Bonfire → Advanced**, then enter it on any
Bonfire screen or press `Ctrl+Shift+B`. Off by default.

Before you turn it on:

- It does **not** unlock other profiles, and does not widen library access, parental
  ratings or tag filters. Jellyfin enforces those regardless of this plugin.
- It **does** skip the profile gate. On a device already signed in to the master account,
  anyone with the code gets that account's full library.
- It is submitted without a password, so make it long. Five attempts per hour per address,
  and every use is logged.
- If the plugin's script fails to load at all, the code has nothing to run in. Restart
  Jellyfin, or delete the plugin folder.
