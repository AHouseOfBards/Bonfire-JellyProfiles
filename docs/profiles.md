# Using Bonfire

For everyone at home. Your server's administrator sets Bonfire up. Settings for the whole server are in the [Admin guide](admin-guide.md).

## Who's watching?

The profile screen lists the main account and every profile. Pick one to watch as that person. A profile with a PIN asks for it first.

To switch later, use either of these. Which one you get is your choice under **Settings → Switch button**.

- **In the header.** A Switch button next to Jellyfin's own profile icon.
- **In Jellyfin's user menu.** **Switch profile**, above **Sign out**, and on your profile page.

On a TV, move with the arrow keys and press OK. A PIN is typed with the number keys.

Some apps show profiles on their own sign-in screen instead. Which ones, and how to turn it on, is in [Apps](clients.md).

## Add or edit a profile

Profiles are managed from the main account.

1. Open the main account's tile. It asks for the main PIN if one is set.
2. Choose **Manage profiles**.
3. Choose **Add profile**, or choose a profile to edit it. **Done** goes back.

A profile has:

- **Profile name.** Up to 32 letters, numbers and spaces.
- **Avatar.** Choose one **From this server**, or **Upload an avatar** from your device (JPEG, PNG, WebP or GIF). Drag to position it and use the slider to zoom. **Avatar color** shows behind it.
- **Rating limit.** The highest rating the profile can watch, from **G / TV-G (6+)** to **R / TV-MA (17+)**, or **No limit**. **Kids profile** sets it to 10+.
- **Libraries.** Check the ones the profile can see. Check none and it sees every library your account has.
- **Allowed devices.** See [Keep a profile to some devices](#keep-a-profile-to-some-devices).
- **Theme.** Under **More**. See [Themes](themes.md).
- **Blocked tags** and **Allowed tags**, also under **More**. A blocked tag hides what has it. Allowed tags hide everything without one.

**Copy rules** copies the rating limit, tags, libraries and viewing limits from another profile.

**Delete profile** removes the profile and its account. That can't be undone.

## PINs

There are two kinds.

- **A profile's PIN** opens that profile. Set it under **Security** when you add or edit the profile. A PIN is 4 to 8 digits. **Clear PIN** removes it.
- **The main PIN** protects what only the main account should do: **Manage profiles**, **Settings**, and adding time. Set it under **Settings → Your account → Sign-in**. **Ask for the PIN to open this account** also protects opening the main account itself. Settings always need the PIN.

A PIN can also be skipped at home. Each profile has **Skip the PIN on the server's network**, and so does the main account (under **Settings → Your account**, in the closed section). It shows once that account has been used at home. Away from home the PIN is still asked. The administrator can also add trusted addresses ([Admin guide](admin-guide.md#security)).

**Auto-lock** locks a profile after it has been idle for the time you choose. It only applies when a PIN is set.

### Forgot a PIN

- **A profile's PIN.** Open the profile from the main account (**Manage profiles**), then set a new PIN or choose **Clear PIN**.
- **The main PIN.** On the main PIN screen, choose **Forgot PIN?**. A code is sent to your email and you type a new PIN. The link only shows if you added an email under **Settings → Your account → Email** and the administrator turned this on. If it isn't there, ask an administrator to clear the PIN. They do it under **Households** in the [Admin guide](admin-guide.md#households).

## Viewing hours

Limits are set for each profile, not for main accounts. They need a main PIN, so set one first.

Under **Viewing limits** when you edit a profile:

- **Only at these times.** Check the days and choose from and until times for each.
- **Daily limit** and **Weekend limit**. **Same as weekdays** uses the daily limit for the weekend too.

Only video counts. Music is never limited. Limits follow the time zone under **Settings → Your account → Time zone**.

When time is up, the profile sees **Time's up** and can still browse. It has these choices:

- **Ask for more time.** Sends a request.
- **Parent: add time.** Enter the main PIN and choose how long.
- **Browse without playing.**

A request shows on the profile screen as a line like "Sam asked for more time.", above the profiles. Choose **Answer**, enter the main PIN, then choose how long or **Decline**. Extra time is capped at 4 hours a day. To get an email for each request, turn on **Email me when a profile asks for more time** under **Settings → Your account → Email**.

## Keep a profile to some devices

When you edit a profile, **Allowed devices** lists the screens that have opened Jellyfin on your account. Check the ones this profile can be opened on. None checked means any device. A new device shows up after it opens Jellyfin.

**Settings → Your account → Manage devices** renames, merges or forgets devices.

This keeps a child on their own tablet. It doesn't stop someone determined. See [Security](security.md).

## First-time setup

A new main account is walked through setup the first time it opens Bonfire. Each step can be skipped, and steps that are already done don't show.

1. **Set a main PIN.**
2. **Add your email.** For a forgotten PIN.
3. **Add a profile.**
4. **How do you want to switch?** Whether to ask "Who's watching?" on startup, and where the switch button goes.
5. **Pick a theme.**

Everything it asks can be changed later under **Manage profiles → Settings**.
