(function () {
    'use strict';

    // Told to the inline head script, which is the only other piece of the plugin that
    // runs on a page load. This tag is deferred, so it has finished evaluating before
    // DOMContentLoaded — meaning the absence of this flag at that point is proof the
    // script never loaded, and the head script can stop holding the page hidden for
    // something that is not coming.
    try { window.__jpLoaded = 1; } catch (e) { /* no window: reading the file, not running it */ }

    function escapeHtml(str) {
        if (!str) return '';
        return String(str)
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#039;");
    }

    // Avatar colours and profile images are stored server-side and — through Bonfire
    // groups — rendered on *other* accounts' switcher screens. Both are validated on the
    // server, but they are re-validated here so a value predating that validation (or
    // written directly to PluginConfiguration.xml) can never break out of its attribute.

    const HEX_COLOR_RE = /^#[0-9a-fA-F]{6}$/;
    const DEFAULT_AVATAR_COLOR = '#00A4DC';

    function safeColor(color) {
        return HEX_COLOR_RE.test(color || '') ? color : DEFAULT_AVATAR_COLOR;
    }

    /// Our image endpoints are stored root-relative, which only resolves when the page was
    /// served by the Jellyfin server. Inside a packaged client — Samsung Tizen bundles the
    /// web client into the app — the origin is the app itself and those requests 404.
    /// ApiClient knows the real server address, so route through it whenever we can.
    function pluginUrl(value) {
        try {
            if (typeof ApiClient !== 'undefined' && ApiClient && typeof ApiClient.getUrl === 'function') {
                const resolved = ApiClient.getUrl(value.replace(/^\//, ''));
                if (resolved) return resolved;
            }
        } catch (e) { /* no ApiClient yet — the relative path is still right in a browser */ }
        return value;
    }

    /// Allows only the three shapes the plugin actually produces: its own image
    /// endpoint, a data:image payload, and an absolute http(s) URL. Anything else
    /// resolves to an empty src rather than being trusted.
    function safeImageSrc(src) {
        if (!src) return '';
        const value = String(src).trim();
        if (value.startsWith('/plugins/profiles/image/')) return escapeHtml(pluginUrl(value));
        if (value.startsWith('/plugins/profiles/avatars/')) return escapeHtml(pluginUrl(value));
        if (/^data:image\/(png|jpeg|jpg|gif|webp);base64,[A-Za-z0-9+/=]+$/i.test(value)) return escapeHtml(value);
        try {
            const parsed = new URL(value, window.location.origin);
            if (parsed.protocol === 'http:' || parsed.protocol === 'https:') return escapeHtml(parsed.href);
        } catch (e) { /* not a parseable URL — fall through */ }
        return '';
    }

    /// Appends ?size=thumb to one of our own image URLs. Grids and switcher cards ask for
    /// the small rendering; showing twenty full-size avatars would decode tens of megabytes
    /// of bitmap, which is enough to stall the TV browsers this plugin supports.
    /// Remote URLs and data payloads are returned untouched — there is no variant of those.
    function thumbSrc(src) {
        if (!src) return '';
        const value = String(src).trim();
        if (!value.startsWith('/plugins/profiles/image/') && !value.startsWith('/plugins/profiles/avatars/')) return value;
        return value + (value.includes('?') ? '&' : '?') + 'size=thumb';
    }

    /// Markup for the inside of an avatar circle.
    ///
    /// The initial is always rendered, with the picture layered over it. If the image fails
    /// — the file was deleted, the disk is unreadable, a remote host is down — onerror
    /// removes the img and the initial is simply revealed underneath. That is the whole
    /// fallback: no state, no second request, and nothing for a caller to remember to do.
    ///
    /// The containing element must be position:relative (all of ours already are, or are
    /// given it inline at the call site).
    function avatarInner(src, initial, useThumb) {
        const safeInitial = escapeHtml(initial || '?');
        const resolved = safeImageSrc(useThumb ? thumbSrc(src) : src);
        if (!resolved) return safeInitial;
        return safeInitial +
            `<img src="${resolved}" alt="" onerror="this.remove()" ` +
            `style="position:absolute;top:0;left:0;width:100%;height:100%;object-fit:cover;" />`;
    }

    // The profile gate overlay sits at z-index 99999. Anything that has to appear *over*
    // it — alerts, confirmations — must beat that number, or it renders behind a full-screen
    // opaque background and is completely invisible. That produced dead-looking buttons:
    // clicking Save with an invalid PIN fired a validation alert nobody could see.
    const OVERLAY_Z = 99999;
    const DIALOG_Z = OVERLAY_Z + 10;

    // ── Internationalisation ────────────────────────────────────────────────────
    // English lives inline, as the single dependency-free fallback every client can
    // render immediately — no fetch, no flash of untranslated text, works even if the
    // server is briefly unreachable. Every other language is a JSON file the server
    // hands out from Web/i18n/, fetched once and only for a visitor whose browser
    // actually asks for it: nobody pays for a translation they will not read.
    //
    // To add a language: drop Web/i18n/<code>.json next to fr.json, with the same keys
    // and translated values. That is the whole job — the .csproj takes Web/i18n/*.json
    // as a wildcard, the server lists what it finds embedded, and the line below is
    // rewritten with that list as the script is served. Nothing here needs editing.
    //
    // `node Web/i18n/validate.js` checks a file against the catalogue below before you
    // open the pull request.
    //
    // The empty default is what a copy of this file read straight off disk will use, so
    // reading the raw file always yields English and never a broken fetch.
    let SUPPORTED_LOCALES = []; // __BONFIRE_LOCALES__

    /*
     * The stylesheet, spliced in by the server from Web/styles.css.
     *
     * This used to be a 1,400-line template literal right inside injectStyles, and
     * it cost two releases: a code comment containing two backticks ended the literal
     * early, the file still parsed, node --check passed, and the script threw at
     * runtime three calls into startup. 1.5.2 and 1.5.3-beta shipped dead.
     *
     * PublishStyles replaces the marker below with the CSS encoded as a JSON string
     * literal — double-quoted and fully escaped — so nothing in the stylesheet can
     * terminate anything here. The failure mode is gone rather than guarded.
     *
     * The default is an empty string, not the real CSS, so this file is never the
     * place the stylesheet lives. If the marker is ever edited away the replace does
     * nothing and the plugin renders unstyled, which is loud and immediate — unlike a
     * silently truncated literal, which is what got us here.
     */
    let BONFIRE_STYLES = ""; // __BONFIRE_STYLES__

    const EN_STRINGS = {
        'common.cancel': 'Cancel',
        'common.confirm': 'Confirm',
        'common.back': 'Back',
        'common.done': 'Done',
        'common.save': 'Save',
        'common.ok': 'OK',
        'common.remove': 'Remove',
        'common.add': 'Add',
        'common.selectAll': 'Select all',
        'common.create': 'Create',
        'common.join': 'Join',
        'common.submit': 'Submit',
        'common.unlock': 'Unlock',
        'common.settings': 'Settings',

        'gate.whosWatching': "Who's Watching?",
        'gate.manageProfiles': 'Manage Profiles',
        'gate.signedIn': 'Signed in',
        'gate.pinProtected': 'PIN Protected',
        'gate.noPin': 'No PIN',
        'gate.addProfile': 'Add Profile',
        'gate.yourBonfire': 'Your Bonfire',
        'gate.guest': 'Guest',
        'gate.bonfireOf': "{name}'s Bonfire",
        'gate.limitReached': '{count}/{max} profiles — limit reached',
        'gate.cantGetPast': "Can't get past this screen?",
        'gate.bonfireProfile': 'Bonfire Profile',

        'pin.enterProfilePin': 'Enter Profile PIN',
        'pin.enterMasterPin': 'Enter Master PIN',
        'pin.incorrectPin': 'Incorrect PIN. Please try again.',
        'pin.incorrectMasterPin': 'Incorrect Master PIN. Please try again.',

        'avatar.positionYourPicture': 'Position your picture',
        'avatar.dragOrArrows': 'Drag or arrows to move, slider to zoom. Press OK when it looks right.',
        'avatar.usePicture': 'Use picture',
        'avatar.fromThisServer': 'From this server',
        'avatar.fromThisDevice': 'From this device',
        'avatar.uploadPicture': 'Upload a picture',
        'avatar.uploadHint': 'JPEG, PNG, WebP or GIF. You can position and zoom it after choosing.',
        'avatar.adminLimited': 'Your server administrator has limited profile pictures to the set above.',
        'avatar.profilePicture': 'Profile Picture',
        'avatar.changePicture': 'Change picture',
        'avatar.choosePicture': 'Choose a picture',
        'avatar.avatarLabel': 'Avatar',
        'avatar.zoom': 'Zoom',
        'avatar.chooseFirst': 'Choose a picture first.',
        'avatar.libraryArtworkTitle': 'Library artwork',

        'errors.error': 'Error',
        'errors.validationError': 'Validation Error',
        'errors.unauthorized': 'Unauthorized',
        'errors.failedDeleteDevice': 'Failed to delete device.',
        'errors.failedRenameDevice': 'Failed to rename device.',
        'errors.failedMergeDevice': 'Failed to merge devices.',
        'errors.withMessage': 'Error: {message}',
        'errors.savingProfile': 'Error saving profile: {message}',
        'errors.loadProfileDetails': 'Failed to load profile details: {message}',
        'errors.failedDeleteProfile': 'Failed to delete profile',
        'errors.deletingProfile': 'Error deleting profile: {message}',
        'errors.couldNotSaveArtwork': 'Could not save the artwork.',
        'errors.couldNotSaveThat': 'Could not save that.',
        'errors.heicUnsupported': "HEIC photos aren't supported. Export the photo as JPEG first.",
        'errors.svgUnsupported': "SVG images aren't supported. Use a JPEG, PNG, WebP or GIF.",
        'errors.imageTooLarge': 'That image is over 25 MB. Use a smaller one.',
        'errors.fileUnreadable': 'That file could not be read.',
        'errors.imageEmpty': 'That image appears to be empty.',
        'errors.imageFormatUnsupported': "That image format isn't supported. Save it as a JPEG or PNG first.",
        'errors.imageLoadFailed': 'That image could not be loaded.',

        'profileForm.profileName': 'Profile Name',
        'profileForm.namePlaceholder': 'e.g. Kids',
        'profileForm.masterNameHint': 'The master profile takes its name from your Jellyfin account.',
        'profileForm.avatarColor': 'Avatar Color',
        'profileForm.colorHintNoPicture': 'Used as the avatar background when no picture is set.',
        'profileForm.colorHintHasPicture': 'Shows behind the picture, wherever it is transparent.',
        'profileForm.colorHintUnused': 'Not used while the background is transparent.',
        'profileForm.noBackground': 'No background',
        'profileForm.noBackgroundHint': 'For pictures that are cut out rather than rectangular. Leaves the corners clear instead of filling them with the avatar colour. Set automatically when a picture is chosen.',
        'profileForm.pin': 'PIN',
        'profileForm.pinPlaceholderEmpty': 'Leave empty for no PIN',
        'profileForm.pinPlaceholderNew': 'New PIN',
        'profileForm.pinPlaceholderNone': 'No PIN',
        'profileForm.pinPlaceholderUnprotected': 'Unprotected',
        'profileForm.clearPin': 'Clear PIN',
        'profileForm.pinIsSetHint': '🔒 <strong>A PIN is set.</strong> Leave blank to keep it, type a new one to replace it, or use Clear PIN.',
        'profileForm.noPinSetHint': 'No PIN set. This profile can be opened by anyone who can reach the switcher.',
        'profileForm.bypassPinLan': 'Bypass PIN on local network (LAN)',
        'profileForm.bypassPinHint': 'No PIN prompt on your home network.',
        'profileForm.autoLock': 'Auto-lock after inactivity',
        'profileForm.autoLockHintCreate': 'Only applies when this profile has a PIN set.',
        'profileForm.autoLockHintEdit': 'Only applies when a PIN is set on this profile.',
        'profileForm.lockoutNever': 'Never',
        'profileForm.lockout1Min': '1 minute',
        'profileForm.lockout5Min': '5 minutes',
        'profileForm.lockout5MinDefault': '5 minutes (default)',
        'profileForm.lockout10Min': '10 minutes',
        'profileForm.lockout20Min': '20 minutes',
        'profileForm.lockout30Min': '30 minutes',
        'profileForm.lockout1Hour': '1 hour',
        'profileForm.enabledLibraries': 'Enabled Libraries',
        'profileForm.libraries': 'Libraries',
        'profileForm.librariesInheritHint': 'Tick nothing and this profile sees the same libraries as your account.',
        'profileForm.libraryHeader': 'Library',
        'profileForm.artworkHeader': 'Artwork',
        'profileForm.artworkForAria': 'Artwork for {name}',
        'profileForm.removeTagAria': 'Remove tag {tag}',
        'profileForm.artworkDefault': 'Default',
        'profileForm.artworkPicture': 'Picture',
        'profileForm.artworkHidden': 'Hidden',
        'profileForm.artworkChoose': 'Choose',
        'profileForm.artworkToggleLabel': 'Choose the artwork on each library tile',
        'profileForm.artworkExplainer': 'A library tile takes its picture from whatever is inside the library — which can be something this profile is not allowed to open. <strong>Picture</strong> puts an image you choose on the tile instead, and <strong>Hidden</strong> leaves just the icon and the name.',
        'profileForm.allowedDevices': 'Allowed Devices',
        'profileForm.allDevicesAllowed': 'All Devices Allowed',
        'profileForm.oneDeviceAllowed': '1 Device Allowed',
        'profileForm.devicesAllowed': '{count} Devices Allowed',
        'profileForm.noDevicesYet': 'No devices found for your account yet',
        'profileForm.noDevicesConnected': 'No connected devices found',
        'profileForm.devicesHint': 'If no devices are selected, this profile can be accessed from any device.',
        'profileForm.unknownDevice': 'Unknown Device',
        'profileForm.noTags': 'No tags — this filter is off',
        'profileForm.unknownClient': 'Unknown Client',
        'profileForm.unknown': 'Unknown',
        'profileForm.deviceLastSeen': '{client} • Last seen {date}',
        'profileForm.forgetDevice': 'Forget this device',
        'profileForm.forgetDeviceAria': 'Forget {name}',
        'profileForm.renameDevice': 'Rename this device',
        'profileForm.renameDeviceAria': 'Rename {name}',
        'profileForm.renameDeviceTitle': 'Rename device',
        'profileForm.renameDeviceLabel': 'Name',
        'profileForm.mergeDevice': 'This is the same device as…',
        'profileForm.mergeDeviceAria': 'Mark {name} as the same device as another',
        'profileForm.mergeDeviceTitle': 'Same device?',
        'profileForm.mergeDeviceMessage': 'These two become one device. Any profile allowed on either one stays allowed.',
        'profileForm.tidyDevices': 'Tidy up duplicates',
        'profileForm.tidyNone': 'No duplicates found.',
        'profileForm.tidyFound': 'Found {count} device(s) that look like duplicates of one you already have. Merge them?',
        'profileForm.maximumRating': 'Maximum rating',
        'profileForm.noRestrictions': 'No Restrictions',
        'profileForm.ratingG': 'G / TV-G (6+)',
        'profileForm.ratingPG': 'PG / TV-PG (10+)',
        'profileForm.ratingPG13': 'PG-13 / TV-14 (14+)',
        'profileForm.ratingR': 'R / TV-MA (17+)',
        'profileForm.blockedTags': 'Blocked tags',
        'profileForm.blockedTagsHint': 'Hides anything with these tags. A tag on a series or library covers everything inside it.',
        'profileForm.allowedTags': 'Allowed tags',
        'profileForm.allowedTagsHint': '⚠️ Allow-list: if you add any tag here, this profile sees <strong>only</strong> matching items. Untagged content is hidden too.',
        'profileForm.tagPlaceholderAdults': 'e.g. adults',
        'profileForm.tagPlaceholderKids': 'e.g. kids',
        'profileForm.createTitle': 'Create Profile',
        'profileForm.editTitle': 'Edit Profile',
        'profileForm.sectionProfileTitle': 'Profile',
        'profileForm.sectionProfileSubtitle': 'Name, colour, and picture',
        'profileForm.sectionSecurityTitle': 'Security',
        'profileForm.sectionSecuritySubtitle': 'PIN protection and automatic locking',
        'profileForm.sectionLibrariesSubtitle': 'Which libraries this profile can browse',
        'profileForm.sectionRestrictionsTitle': 'Content & Device Restrictions',
        'profileForm.sectionRestrictionsSubtitle': 'Limits applied on top of the libraries above',
        'profileForm.deleteProfile': 'Delete Profile',
        'profileForm.nameRequired': 'Profile name is required.',
        'profileForm.pinMustBeDigits': 'A PIN can only contain digits.',
        'profileForm.pinLengthCreate': 'PIN must be 4–8 digits.',
        'profileForm.pinLengthEdit': 'A PIN must be 4-8 digits — you entered {count}.',
        'profileForm.deleteConfirmMessage': 'Are you sure you want to delete profile "{name}" and its underlying user account? This action is irreversible.',
        'profileForm.deleteDeviceTitle': 'Delete Device History',
        'profileForm.deleteDeviceMessage': 'Remove this device? Any access restrictions for it go too.',

        'panic.emergencyDisable': 'Emergency disable',
        'panic.dialogBody': "Enter the code your server administrator set. This shuts the Bonfire switcher off until Jellyfin is restarted — the profile gate disappears and this account is used as-is. It does not unlock anyone else's profile.",
        'panic.emergencyCodePlaceholder': 'Emergency code',
        'panic.disable': 'Disable',
        'panic.checking': 'Checking…',
        'panic.tooManyAttempts': 'Too many attempts. Try again in an hour, or restart Jellyfin.',
        'panic.incorrectCode': 'Incorrect code.',
        'panic.disabledTitle': 'Bonfire disabled',
        'panic.disabledBody': 'The switcher is off until Jellyfin restarts. Reload the page if anything still looks wrong.',

        'settings.title': 'Settings',
        'settings.switcherStyleTitle': 'Switcher Style',
        'settings.switcherStyleBody': 'Where you reach this screen from, and whether it opens on startup.',
        'settings.yourBonfireTitle': 'Your Bonfire',
        'settings.yourBonfireBody': 'Share your profiles with another home, or join theirs.',

        'switcher.title': 'Switcher Style',
        'switcher.intro': 'How you reach this screen. Applies to your account on every device.',
        'switcher.askOnStartup': 'Ask "Who\'s watching?" on startup',
        'switcher.askOnStartupHint': 'Shown once when the app opens — not every time you return to the home screen.',
        'switcher.whereToSwitchFrom': 'Where to switch from',
        'switcher.bonfireButtonTitle': 'Bonfire button',
        'switcher.bonfireButtonBody': "A separate switcher button in the header, next to Jellyfin's own profile icon.",
        'switcher.menuTitle': "Jellyfin's user menu",
        'switcher.menuBody': 'Adds "Switch Profile" above Sign out in Jellyfin\'s own menu, and to your profile page. Removes the second header icon.',
        'switcher.switchProfile': 'Switch Profile',
        'switcher.reloadFailedTitle': 'Almost there',
        'switcher.reloadFailedBody': 'Close and reopen the app to finish switching profiles.',
        'switcher.notSavedTitle': 'Switch not saved',
        'switcher.notSavedBody': 'This device would not store the change, so you are still signed in as before.',

        'bonfire.yourBonfireTitle': 'Your Bonfire',
        'bonfire.hostedTitle': 'Your Hosted Bonfire',
        'bonfire.shareCode': 'Share this code to invite someone to your Bonfire:',
        'bonfire.members': 'Members ({count})',
        'bonfire.kick': 'Kick',
        'bonfire.noMembersYet': 'No members joined yet.',
        'bonfire.deleteGroup': 'Delete Group',
        'bonfire.hostTitle': 'Host a Bonfire',
        'bonfire.hostBody': 'Host your own group to share your sub-profiles with friends.',
        'bonfire.generateJoinCode': 'Generate Join Code',
        'bonfire.generating': 'Generating…',
        'bonfire.joinedTitle': 'Joined Bonfire',
        'bonfire.joinedOwnedBy': 'You have joined a bonfire group owned by:',
        'bonfire.accessEachOther': "You can access each other's profiles from the switcher grid.",
        'bonfire.leaveGroup': 'Leave Group',
        'bonfire.joinTitle': 'Join a Bonfire',
        'bonfire.joinBody': "Enter a friend's Bonfire Code to join their group:",
        'bonfire.joinCodePlaceholder': 'e.g. B7F8XA',
        'bonfire.joining': 'Joining…',
        'bonfire.pleaseEnterCode': 'Please enter a 6-character code.',
        'bonfire.tooManyJoinAttempts': 'Too many failed attempts. Try again in 15 minutes.',
        'bonfire.failedToJoin': 'Failed to join group.',
        'bonfire.leaveCurrentTitle': 'Leave your current Bonfire?',
        'bonfire.leaveCurrentBody': 'Joining this Bonfire removes you from your current one.',
        'bonfire.kickConfirmTitle': 'Kick Member',
        'bonfire.kickConfirmBody': 'Are you sure you want to kick this user from your Bonfire group?',
        'bonfire.failedKick': 'Failed to kick member.',
        'bonfire.deleteGroupConfirmTitle': 'Delete Group',
        'bonfire.deleteGroupConfirmBody': 'Delete your Bonfire? Members are disconnected and drop out of your switcher.',
        'bonfire.failedDeleteGroup': 'Failed to delete group.',
        'bonfire.leaveGroupConfirmTitle': 'Leave Group',
        'bonfire.leaveGroupConfirmBody': 'Leave this Bonfire? You will stop sharing switchers.',
        'bonfire.failedLeaveGroup': 'Failed to leave group.',
        'bonfire.failedGenerateCode': 'Failed to generate code: {message}',
        'bonfire.failedLoadStatus': 'Failed to load Bonfire status: {message}',
        'bonfire.unknownUser': 'Unknown User',
        'bonfire.hideMine': 'Hide my sub-profiles from others',
        'bonfire.hideMineHint': 'Connected homes see only your master profile.',
        'bonfire.hideOthers': "Hide other people's sub-profiles from me",
        'bonfire.hideOthersHint': 'You see only the master profiles of connected homes.',
        'bonfire.lanSwitchLabel': 'Let my Bonfire switch into my account on this network',
        'bonfire.lanSwitchHint': 'No PIN needed on your home network. Away from home it still is{extra}.',
        'bonfire.lanSwitchHintExtra': ', and until you set one your account cannot be opened remotely at all',
        'bonfire.settingsSaved': 'Saved.',
        'bonfire.settingsSaveFailed': 'Could not save. Nothing was changed.',
        'bonfire.adminAccountWarning': 'This is an admin account.',
        'bonfire.adminAccountWarningBody': 'Whoever switches into it gets your admin rights.',
        'bonfire.proxyHint': 'Behind a reverse proxy, check Networking → Known Proxies first, or everyone looks local.',
        'bonfire.allowHouseholdTitle': 'Allow household switching?',
        'bonfire.allowHouseholdBody': 'Anyone in your Bonfire can open your account on your home network without your PIN.',

        'profilePage.title': 'Bonfire Profiles',
        'profilePage.body': 'Currently watching as <strong>{name}</strong>. Switch to another profile in your household without signing out.',
        'profilePage.thisAccount': 'this account',
    };

    /// The active language's strings. Starts out equal to EN_STRINGS — every lookup
    /// already resolves correctly before loadLocale() has done anything — and is
    /// swapped wholesale, never merged, once a translation arrives: a partial file
    /// would otherwise leave a stale mix of two languages on screen.
    let activeStrings = EN_STRINGS;

    /// Looks up `key`, filling in `{token}` placeholders from `vars`. Falls back to the
    /// English string, then to the key itself, so a translation file missing a newer
    /// key degrades to English rather than showing nothing.
    ///
    /// `vars` values are interpolated as-is: pass already-escaped HTML for anything
    /// that ends up in a template destined for innerHTML, exactly as callers already do
    /// for user-supplied text like profile and device names.
    function t(key, vars) {
        let str = (activeStrings && activeStrings[key]) || EN_STRINGS[key] || key;
        if (vars) {
            Object.keys(vars).forEach(k => {
                str = str.split('{' + k + '}').join(vars[k]);
            });
        }
        return str;
    }

    /// First of the browser's preferred languages that this plugin ships a translation
    /// for, or null to stay on English.
    ///
    /// A full tag wins over its base language, so pt-BR picks the Brazilian file when
    /// there is one and falls back to pt when there is not. Slicing to two characters —
    /// which is what this did first — made a regional translation unreachable and would
    /// have handed a pt-BR reader a pt-PT file without either of them choosing that.
    function detectLocale() {
        const langs = (navigator.languages && navigator.languages.length)
            ? navigator.languages : [navigator.language || ''];

        // Locale codes reach a URL below. They come from the server rather than from
        // anything a visitor controls, but the shape is checked anyway — this is the one
        // place a bad value could turn into a request for something else.
        const have = SUPPORTED_LOCALES
            .filter(c => typeof c === 'string' && /^[a-z]{2,3}(-[a-z0-9]{2,8})*$/i.test(c));
        const lower = have.map(c => c.toLowerCase());

        for (const lang of langs) {
            const tag = (lang || '').toLowerCase();
            if (!tag) continue;

            // Longest match first, dropping one subtag at a time: zh-Hans-CN, then
            // zh-Hans, then zh. Trying only the full tag and the bare language would
            // skip straight past a zh-Hans file for a browser asking for zh-Hans-CN.
            const parts = tag.split('-');
            for (let n = parts.length; n >= 1; n--) {
                const i = lower.indexOf(parts.slice(0, n).join('-'));
                if (i !== -1) return have[i];
            }
        }
        return null;
    }

    /// Fetches the detected locale's strings and swaps them in. Always resolves —
    /// nothing here is worth failing startup over. No detected/supported locale, a
    /// missing file, or a network error all leave English active, which is already
    /// what `activeStrings` is until this resolves.
    function loadLocale() {
        const locale = detectLocale();
        if (!locale) return Promise.resolve();

        return fetch(pluginUrl('/plugins/profiles/i18n/' + locale + '.json'))
            .then(res => res.ok ? res.json() : null)
            .then(strings => {
                if (strings && typeof strings === 'object') activeStrings = strings;
            })
            .catch(() => { /* stay on English */ });
    }

    const ProfilesPlugin = {
        config: {
            masterStorageKey: 'jellyfin_profiles_master_state',
            activeSessionKey: 'jellyfin_profiles_active_token',
            // Key set before window.location.reload() so the early-hide
            // inline head script can suppress the page flash on the next load.
            switchingKey: 'jpf-sw',
            // The background colour and colour-scheme of the page being left, written
            // alongside switchingKey and read by the head script on the next load. See
            // _captureLeavingBackground.
            backgroundKey: 'jpf-bg',
            // Set when the emergency disable succeeds. profiles.js is served with a
            // five-minute cache, so a reload inside that window can re-run this very script
            // from cache — the marker is what stops it coming back to life.
            panicKey: 'jellyfin_profiles_disabled',
            // Local mirror of the account's gate/native preference, so checkRoute can decide
            // whether to raise the gate without waiting on a request.
            // Per-account cache of the library tile artwork rules, applied before the
            // server answers so the real artwork never gets a frame on screen.
            libraryArtKey: 'jellyfin_profiles_library_art',
            switcherModeKey: 'jellyfin_profiles_switcher_mode'
        },
        pluginId: 'b1462fca-774b-4b13-8d02-e2d4f2bc18b9',
        isManageMode: false,
        masterPin: null,
        cachedProfiles: [],
        currentProfiles: [],
        inactivityTimer: null,
        inactivityEventHandlers: null,
        _pageRevealed: false,
        _switchLock: false,
        _switcherPrefs: null,
        _switcherPrefsLoading: false,
        _panicDisabled: false,
        // Null until the server answers. Only true reveals the emergency link.
        _panicLinkAvailable: null,
        _overlayTrap: null,
        // Set once the artwork rules have been fetched for this page load.
        _libraryArtLoaded: false,

        getAuthHeaders: function (token) {
            const apiClient = ApiClient;
            const client = typeof apiClient.appName === 'function' ? apiClient.appName() : (apiClient.appName || apiClient._appName || 'Jellyfin Web');
            const device = typeof apiClient.deviceName === 'function' ? apiClient.deviceName() : (apiClient.deviceName || apiClient._deviceName || 'Chrome');
            const deviceId = typeof apiClient.deviceId === 'function' ? apiClient.deviceId() : (apiClient.deviceId || apiClient._deviceId || '');
            const version = typeof apiClient.appVersion === 'function' ? apiClient.appVersion() : (apiClient.appVersion || apiClient._appVersion || '');

            return {
                'Authorization': `MediaBrowser Client="${client}", Device="${device}", DeviceId="${deviceId}", Version="${version}", Token="${token}"`
            };
        },

        /// A one-field dialog. Used where a value has to be typed rather than chosen, which
        /// so far is renaming a device.
        ///
        /// Not window.prompt(): it is blocked in several of the environments this runs in
        /// (and silently returns null in a WebView), it cannot be reached with a D-pad on a
        /// television, and it renders outside the focus trap the rest of the overlay lives
        /// inside. The input is focused and selected on open so a rename is type-and-enter.
        showPromptDialog: function (title, label, initialValue, onSubmit) {
            const dialog = document.createElement('div');
            dialog.id = 'profiles-prompt-dialog';
            dialog.classList.add('jpf-trap-surface');
            dialog.style.cssText = `
                position: fixed;
                top: 0;
                left: 0;
                right: 0;
                bottom: 0;
                background: rgba(0, 0, 0, 0.82);
                backdrop-filter: blur(8px);
                display: flex;
                align-items: center;
                justify-content: center;
                z-index: ${DIALOG_Z};
                opacity: 0;
                transition: opacity 0.15s ease-out;
            `;

            dialog.innerHTML = `
                <div class="prompt-dialog-content" style="
                    background: #181818;
                    border: 1px solid rgba(255, 255, 255, 0.1);
                    border-radius: var(--jpf-r-md);
                    padding: 24px;
                    max-width: 420px;
                    width: 90%;
                    box-shadow: 0 10px 30px rgba(0,0,0,0.5);
                ">
                    <h2 style="margin-top: 0; color: #fff; font-size: 1.25rem; font-weight: 700; margin-bottom: 12px;">${escapeHtml(title)}</h2>
                    <label for="profiles-prompt-input" style="display: block; color: rgba(255,255,255,0.7); font-size: 0.85rem; margin-bottom: 6px;">${escapeHtml(label)}</label>
                    <input id="profiles-prompt-input" type="text" maxlength="64" value="${escapeHtml(initialValue || '')}" style="width: 100%; box-sizing: border-box; margin-bottom: 20px;" />
                    <div style="display: flex; gap: var(--jpf-gap); justify-content: flex-end;">
                        <button id="prompt-cancel-btn" type="button" class="profiles-btn">${t('common.cancel')}</button>
                        <button id="prompt-save-btn" type="button" class="profiles-btn btn-primary">${t('common.save')}</button>
                    </div>
                </div>
            `;

            document.body.appendChild(dialog);
            requestAnimationFrame(() => requestAnimationFrame(() => {
                dialog.style.opacity = '1';
            }));

            const close = () => {
                dialog.style.opacity = '0';
                setTimeout(() => dialog.remove(), 160);
            };

            const input = dialog.querySelector('#profiles-prompt-input');
            const submit = () => {
                const value = (input.value || '').trim();
                if (!value) return;
                close();
                if (typeof onSubmit === 'function') onSubmit(value);
            };

            dialog.querySelector('#prompt-cancel-btn').addEventListener('click', close);
            dialog.querySelector('#prompt-save-btn').addEventListener('click', submit);

            dialog.addEventListener('keydown', (e) => {
                if (e.key === 'Escape') { e.preventDefault(); close(); }
                else if (e.key === 'Enter') { e.preventDefault(); submit(); }
            });

            input.focus();
            input.select();
        },

        /// A dialog that asks which one. `options` is [{ id, name }]; the chosen id is
        /// handed back. Built on a real <select> so a television's D-pad and a phone's
        /// native picker both work without any focus handling of our own.
        showSelectDialog: function (title, message, options, onSubmit) {
            const dialog = document.createElement('div');
            dialog.id = 'profiles-select-dialog';
            dialog.classList.add('jpf-trap-surface');
            dialog.style.cssText = `
                position: fixed;
                top: 0;
                left: 0;
                right: 0;
                bottom: 0;
                background: rgba(0, 0, 0, 0.82);
                backdrop-filter: blur(8px);
                display: flex;
                align-items: center;
                justify-content: center;
                z-index: ${DIALOG_Z};
                opacity: 0;
                transition: opacity 0.15s ease-out;
            `;

            const choices = options.map(o =>
                `<option value="${escapeHtml(o.id)}">${escapeHtml(o.name)}</option>`).join('');

            dialog.innerHTML = `
                <div class="select-dialog-content" style="
                    background: #181818;
                    border: 1px solid rgba(255, 255, 255, 0.1);
                    border-radius: var(--jpf-r-md);
                    padding: 24px;
                    max-width: 420px;
                    width: 90%;
                    box-shadow: 0 10px 30px rgba(0,0,0,0.5);
                ">
                    <h2 style="margin-top: 0; color: #fff; font-size: 1.25rem; font-weight: 700; margin-bottom: 12px;">${escapeHtml(title)}</h2>
                    <p style="color: rgba(255,255,255,0.7); font-size: 0.92rem; line-height: 1.5; margin-bottom: 16px;">${escapeHtml(message)}</p>
                    <select id="profiles-select-input" style="width: 100%; box-sizing: border-box; margin-bottom: 20px;">
                        ${choices}
                    </select>
                    <div style="display: flex; gap: var(--jpf-gap); justify-content: flex-end;">
                        <button id="select-cancel-btn" type="button" class="profiles-btn">${t('common.cancel')}</button>
                        <button id="select-confirm-btn" type="button" class="profiles-btn btn-primary">${t('common.ok')}</button>
                    </div>
                </div>
            `;

            document.body.appendChild(dialog);
            requestAnimationFrame(() => requestAnimationFrame(() => {
                dialog.style.opacity = '1';
            }));

            const close = () => {
                dialog.style.opacity = '0';
                setTimeout(() => dialog.remove(), 160);
            };

            const select = dialog.querySelector('#profiles-select-input');
            dialog.querySelector('#select-cancel-btn').addEventListener('click', close);
            dialog.querySelector('#select-confirm-btn').addEventListener('click', () => {
                const value = select.value;
                close();
                if (value && typeof onSubmit === 'function') onSubmit(value);
            });

            dialog.addEventListener('keydown', (e) => {
                if (e.key === 'Escape') { e.preventDefault(); close(); }
            });

            select.focus();
        },

        showConfirmDialog: function (title, message, onConfirm, onCancel) {
            const dialog = document.createElement('div');
            dialog.id = 'profiles-confirm-dialog';
            dialog.classList.add('jpf-trap-surface');
            dialog.style.cssText = `
                position: fixed;
                top: 0;
                left: 0;
                right: 0;
                bottom: 0;
                background: rgba(0, 0, 0, 0.82);
                backdrop-filter: blur(8px);
                display: flex;
                align-items: center;
                justify-content: center;
                z-index: ${DIALOG_Z};
                opacity: 0;
                transition: opacity 0.15s ease-out;
            `;

            dialog.innerHTML = `
                <div class="confirm-dialog-content" style="
                    background: #181818;
                    border: 1px solid rgba(255, 255, 255, 0.1);
                    border-radius: var(--jpf-r-md);
                    padding: 24px;
                    max-width: 420px;
                    width: 90%;
                    box-shadow: 0 10px 30px rgba(0,0,0,0.5);
                    text-align: center;
                ">
                    <h2 style="margin-top: 0; color: #fff; font-size: 1.25rem; font-weight: 700; margin-bottom: 12px;">${title}</h2>
                    <p style="color: rgba(255,255,255,0.7); font-size: 0.92rem; line-height: 1.5; margin-bottom: 24px;">${message}</p>
                    <div style="display: flex; gap: var(--jpf-gap); justify-content: center;">
                        <button id="dialog-confirm-btn" class="profiles-btn btn-danger" style="padding: 10px 20px; font-weight: 600; min-width: 100px;">${t('common.confirm')}</button>
                        <button id="dialog-cancel-btn" class="profiles-btn btn-secondary" style="padding: 10px 20px; font-weight: 600; min-width: 100px;">${t('common.cancel')}</button>
                    </div>
                </div>
            `;

            document.body.appendChild(dialog);
            
            requestAnimationFrame(() => requestAnimationFrame(() => {
                dialog.style.opacity = '1';
            }));

            const closeDialog = () => {
                dialog.style.opacity = '0';
                setTimeout(() => dialog.remove(), 160);
            };

            const confirmBtn = dialog.querySelector('#dialog-confirm-btn');
            const cancelBtn = dialog.querySelector('#dialog-cancel-btn');

            confirmBtn.addEventListener('click', () => {
                closeDialog();
                if (typeof onConfirm === 'function') onConfirm();
            });

            cancelBtn.addEventListener('click', () => {
                closeDialog();
                if (typeof onCancel === 'function') onCancel();
            });

            cancelBtn.focus();
            
            dialog.addEventListener('keydown', (e) => {
                if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
                    e.preventDefault();
                    if (document.activeElement === confirmBtn) {
                        cancelBtn.focus();
                    } else {
                        confirmBtn.focus();
                    }
                }
                if (e.key === 'Escape') {
                    e.preventDefault();
                    cancelBtn.click();
                }
            });
        },

        showAlert: function (title, message, onClose) {
            const dialog = document.createElement('div');
            dialog.id = 'profiles-alert-dialog';
            dialog.classList.add('jpf-trap-surface');
            dialog.style.cssText = `
                position: fixed;
                top: 0;
                left: 0;
                right: 0;
                bottom: 0;
                background: rgba(0, 0, 0, 0.82);
                backdrop-filter: blur(8px);
                display: flex;
                align-items: center;
                justify-content: center;
                z-index: ${DIALOG_Z};
                opacity: 0;
                transition: opacity 0.15s ease-out;
            `;

            dialog.innerHTML = `
                <div class="alert-dialog-content" style="
                    background: #181818;
                    border: 1px solid rgba(255, 255, 255, 0.1);
                    border-radius: var(--jpf-r-md);
                    padding: 24px;
                    max-width: 420px;
                    width: 90%;
                    box-shadow: 0 10px 30px rgba(0,0,0,0.5);
                    text-align: center;
                ">
                    <h2 style="margin-top: 0; color: #fff; font-size: 1.25rem; font-weight: 700; margin-bottom: 12px;">${title}</h2>
                    <p style="color: rgba(255,255,255,0.7); font-size: 0.92rem; line-height: 1.5; margin-bottom: 24px;">${message}</p>
                    <div style="display: flex; justify-content: center;">
                        <button id="dialog-close-btn" class="profiles-btn btn-primary" style="padding: 10px 24px; font-weight: 600; min-width: 120px;">${t('common.ok')}</button>
                    </div>
                </div>
            `;

            document.body.appendChild(dialog);
            
            requestAnimationFrame(() => requestAnimationFrame(() => {
                dialog.style.opacity = '1';
            }));

            const closeDialog = () => {
                dialog.style.opacity = '0';
                setTimeout(() => dialog.remove(), 160);
            };

            const closeBtn = dialog.querySelector('#dialog-close-btn');

            closeBtn.addEventListener('click', () => {
                closeDialog();
                if (typeof onClose === 'function') onClose();
            });

            closeBtn.focus();

            dialog.addEventListener('keydown', (e) => {
                if (e.key === 'Escape' || e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    closeBtn.click();
                }
            });
        },

        /// Writes a token and user into jellyfin-web's own stored credentials, and returns
        /// whether it actually managed to.
        ///
        /// The return value is the point of this function. Everything else about a switch
        /// can succeed — the server issues the token, ApiClient takes it, the page
        /// reloads — and the profile still not be the one that comes back, because this is
        /// the only part that survives the reload. It used to fail silently in two ways,
        /// both of which look from outside exactly like "it lets me pick a profile but
        /// loads my own account anyway", which is issue #16:
        ///
        ///   - The server was matched with `server.Id === currentServerId`, a raw string
        ///     comparison of two GUIDs. Every other id comparison in this file goes
        ///     through normalizeGuid because casing and dashes vary by where the value
        ///     came from. With more than one server stored — an address and a hostname for
        ///     the same box is the common way that happens — a mismatch updated nothing
        ///     and reported nothing.
        ///   - Writing to localStorage can be accepted and discarded (a full quota, a
        ///     television in a private-browsing-like mode). Nothing read it back.
        ///
        /// So: match by id, fall back to the address ApiClient is actually talking to,
        /// fall back to a single stored server, and then read back what was written. The
        /// caller decides what to do with a false; it must not be a reload.
        updateStoredCredentials: function (newToken, newUserId) {
            try {
                const credsStr = localStorage.getItem('jellyfin_credentials');
                if (!credsStr) return false;

                const creds = JSON.parse(credsStr);
                if (!creds || !Array.isArray(creds.Servers) || !creds.Servers.length) return false;

                const serverId = this.normalizeGuid(
                    typeof ApiClient.serverId === 'function' ? ApiClient.serverId() : (ApiClient.serverId || ''));
                const address = (typeof ApiClient.serverAddress === 'function' ? ApiClient.serverAddress() : '') || '';
                const sameAddress = (server) => !!address && [server.ManualAddress, server.LocalAddress, server.RemoteAddress]
                    .some(a => a && String(a).replace(/\/+$/, '') === address.replace(/\/+$/, ''));

                // In order, and only one of them applies. Widening the match after a
                // narrower one has already hit would write another server's entry.
                let targets = creds.Servers.filter(s => serverId && this.normalizeGuid(s.Id) === serverId);
                if (!targets.length) targets = creds.Servers.filter(sameAddress);
                if (!targets.length && creds.Servers.length === 1) targets = creds.Servers.slice();
                if (!targets.length) {
                    console.error('ProfilesPlugin: none of the stored servers is the one being switched on.');
                    return false;
                }

                targets.forEach(server => {
                    server.AccessToken = newToken;
                    server.UserId = newUserId;
                });
                localStorage.setItem('jellyfin_credentials', JSON.stringify(creds));

                // Read back. A write that was accepted and dropped leaves the old token in
                // place, and the reload would sign back in as whoever was there before.
                const saved = JSON.parse(localStorage.getItem('jellyfin_credentials') || 'null');
                return !!(saved && Array.isArray(saved.Servers)
                    && saved.Servers.some(s => s.AccessToken === newToken && s.UserId === newUserId));
            } catch (e) {
                console.error("ProfilesPlugin: Stored credentials update failed:", e);
                return false;
            }
        },

        normalizeGuid: function (guid) {
            if (!guid) return '';
            return guid.toString().toLowerCase().replace(/[^a-z0-9]/g, '');
        },

        /// Registers a document-level listener that is torn down the next time a modal is
        /// rendered.
        ///
        /// The create/edit forms attach a document click handler to close the device dropdown.
        /// Those were added on every render and never removed, so each open/close cycle left
        /// another live closure bound to `document`, each capturing a now-detached modal. After
        /// a few passes every click on the page ran a growing pile of stale handlers — which
        /// showed up as clicks taking seconds to register, getting worse the longer the tab
        /// stayed open.
        addManagedDocumentListener: function (type, handler, options) {
            if (!this._managedDocListeners) this._managedDocListeners = [];
            document.addEventListener(type, handler, options);
            this._managedDocListeners.push({ type, handler, options });
        },

        /// Drops every listener registered via addManagedDocumentListener. Safe to call when
        /// none are outstanding.
        clearManagedDocumentListeners: function () {
            if (!this._managedDocListeners) return;
            this._managedDocListeners.forEach(({ type, handler, options }) => {
                document.removeEventListener(type, handler, options);
            });
            this._managedDocListeners = [];
        },

        /// Normalizes the /list response into the camelCase shape the UI uses.
        /// ASP.NET may serialize either casing depending on server configuration, so every
        /// field is read both ways. Single definition — adding a field here reaches every
        /// caller, which two hand-maintained copies of this map did not.
        normalizeProfiles: function (profiles) {
            const pick = (p, name, fallback) => {
                const upper = name.charAt(0).toUpperCase() + name.slice(1);
                if (p[name] !== undefined && p[name] !== null) return p[name];
                if (p[upper] !== undefined && p[upper] !== null) return p[upper];
                return fallback;
            };

            return (profiles || []).map(p => ({
                profileUserId: pick(p, 'profileUserId', null),
                profileName: pick(p, 'profileName', ''),
                avatarInitial: pick(p, 'avatarInitial', '?'),
                avatarColor: pick(p, 'avatarColor', DEFAULT_AVATAR_COLOR),
                // Cut-out picture: paint no colour behind it (issue #23).
                transparentAvatar: pick(p, 'transparentAvatar', false),
                requiresPin: pick(p, 'requiresPin', false),
                // "A PIN exists" — distinct from requiresPin, which is false on the LAN when
                // the bypass is enabled. Forms must use this one.
                hasPin: pick(p, 'hasPin', false),
                isMaster: pick(p, 'isMaster', false),
                lockoutMinutes: pick(p, 'lockoutMinutes', 5),
                maxSubProfiles: pick(p, 'maxSubProfiles', 5),
                bypassPinOnLocalNetwork: pick(p, 'bypassPinOnLocalNetwork', false),
                allowedDeviceIds: pick(p, 'allowedDeviceIds', []),
                enabledFolders: pick(p, 'enabledFolders', []),
                blockedTags: pick(p, 'blockedTags', []),
                allowedTags: pick(p, 'allowedTags', []),
                isBonfire: pick(p, 'isBonfire', false),
                profileImage: pick(p, 'profileImage', null),
                masterUserId: pick(p, 'masterUserId', null)
            }));
        },

        initTVCheckboxes: function (container) {
            container.querySelectorAll('.library-check-label').forEach(label => {
                if (!label.hasAttribute('tabindex')) {
                    label.setAttribute('tabindex', '0');
                }
                if (label._tvInit) return;
                label._tvInit = true;

                label.addEventListener('keydown', (e) => {
                    if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault();
                        const checkbox = label.querySelector('input[type="checkbox"]');
                        if (checkbox) {
                            checkbox.checked = !checkbox.checked;
                            checkbox.dispatchEvent(new Event('change'));
                        }
                    }
                });
            });
        },

        /// Wraps a group of form fields in a titled, self-contained section.
        /// The create/edit forms grew field-by-field in the order features were added, which
        /// left unrelated controls adjacent and gave no landmarks to scroll by — worst on
        /// phones, where the form is several screens long. Sections give each group a heading
        /// and one-line purpose, and are plain blocks (no collapsing) so D-pad focus order on
        /// TV stays linear and predictable.
        renderSection: function (icon, title, subtitle, bodyHtml) {
            return `
                <section class="profile-section">
                    <div class="profile-section-header">
                        <span class="material-icons profile-section-icon" aria-hidden="true">${icon}</span>
                        <div class="profile-section-heading">
                            <h2 class="profile-section-title">${escapeHtml(title)}</h2>
                            <span class="profile-section-subtitle">${escapeHtml(subtitle)}</span>
                        </div>
                    </div>
                    <div class="profile-section-body">${bodyHtml}</div>
                </section>
            `;
        },

        /// The avatar swatches are identical in both forms; keeping one copy means a palette
        /// change lands in both places at once.
        /// Dims the avatar colour when it has stopped doing anything.
        ///
        /// It is twenty-one swatches over three rows, the largest thing on the form, so
        /// leaving it at full strength when it cannot affect the avatar reads as a
        /// control that is simply being ignored. Dimmed rather than hidden: it starts
        /// mattering again the moment the picture is removed, and a control that
        /// vanishes and reappears is worse than one that fades.
        ///
        /// It used to dim whenever a picture was set, and say "Not used while a picture
        /// is set". That was wrong, and it is how issue #23 came to be filed: the colour
        /// is painted *behind* the picture and shows through wherever the picture is
        /// transparent, so somebody read the hint, chose a cut-out picture, and got a
        /// coloured square anyway. The colour is genuinely unused in one case only —
        /// a picture with the background switched off.
        setColorGroupInert: function (prefix, hasPicture, isTransparent) {
            const group = document.getElementById(prefix + '-color-group');
            if (!group) return;

            const unused = !!hasPicture && !!isTransparent;
            group.classList.toggle('is-inert', unused);

            const hint = group.querySelector('[data-role="color-hint"]');
            if (hint) {
                if (unused) {
                    hint.textContent = t('profileForm.colorHintUnused');
                } else if (hasPicture) {
                    hint.textContent = t('profileForm.colorHintHasPicture');
                } else {
                    hint.textContent = t('profileForm.colorHintNoPicture');
                }
            }
        },

        renderColorPicker: function (selectedColor) {
            const palette = [
                '#00A4DC', '#E50914', '#22C55E', '#EAB308', '#A855F7', '#EC4899',
                '#F97316', '#06B6D4', '#3B82F6', '#10B981', '#6366F1', '#8B5CF6',
                '#D946EF', '#F43F5E', '#14B8A6', '#F59E0B', '#84CC16', '#64748B'
            ];
            const active = (selectedColor || '').toLowerCase();
            return `
                <div class="avatar-color-picker">
                    ${palette.map(c => `
                        <div class="color-dot${c.toLowerCase() === active ? ' active' : ''}"
                             style="background-color: ${c}" data-color="${c}"
                             role="radio" aria-label="${c}" tabindex="0"></div>
                    `).join('')}
                </div>
            `;
        },

        // ── Tag filtering ───────────────────────────────────────────────────────────
        // Jellyfin matches blocked/allowed tags against an item's inherited tags, so a tag
        // on a series or a whole library applies to everything inside it.

        /// Fetches the distinct tags present in the master's libraries, for the suggestion
        /// list. Degrades to an empty list — the inputs stay usable as free text.
        // ── Navigation ──────────────────────────────────────────────────────

        /// Ticket for the screen currently being asked for.
        ///
        /// Opening a form fires network requests and only draws when they return.
        /// Nothing used to stop a second navigation in the meantime, and nothing
        /// stopped the first response drawing itself over whatever had replaced it —
        /// so clicking Manage Profiles and then Switcher Style showed Switcher Style,
        /// then silently replaced it with Manage Profiles seconds later.
        ///
        /// Every screen change takes a ticket. An async render compares its ticket
        /// before touching the DOM and drops the response if the user has moved on.
        _navTicket: 0,

        /// Claims the screen. Call from anything that replaces the modal contents,
        /// including synchronous renders — those are exactly what a slow form loses
        /// the race to.
        beginNavigation: function () {
            return ++this._navTicket;
        },

        /// True while the ticket still owns the screen.
        navIsCurrent: function (ticket) {
            return ticket === this._navTicket;
        },

        // ── Shared form data ────────────────────────────────────────────────

        /// Cached answer to the four requests that are the same for every profile.
        ///
        /// Add and Edit Profile each fired five requests and waited for all of them.
        /// Only one — Users/{id} — is about the profile you clicked; the available
        /// libraries, the connected devices, the library tags and the avatar library
        /// belong to the account and were refetched in full every single time.
        _sharedForm: null,

        /// Fetches them once per gate session. Prefetched when the overlay opens, so
        /// by the time anyone picks a profile the shared half is already in hand and
        /// opening a form costs one request instead of five.
        fetchSharedFormData: function (apiClient, masterState) {
            if (this._sharedForm) return this._sharedForm;

            const headers = this.getAuthHeaders(masterState.masterToken);
            this._sharedForm = Promise.all([
                fetch(apiClient.getUrl('plugins/profiles/libraries'), { headers })
                    .then(res => res.json()),
                fetch(apiClient.getUrl('plugins/profiles/devices'), { headers })
                    .then(res => res.json()).catch(() => []),
                this.fetchLibraryTags(apiClient, masterState.masterToken, masterState.masterUserId),
                this.fetchAvatarLibrary(apiClient, masterState.masterToken)
            ]).then(([libraries, devices, libraryTags, avatarLibrary]) => ({
                libraries, devices, libraryTags, avatarLibrary
            })).catch(err => {
                // Never cache a failure. A server that was briefly unreachable would
                // otherwise keep every form broken until the overlay was closed.
                this._sharedForm = null;
                throw err;
            });

            return this._sharedForm;
        },

        /// Dropped when the overlay closes, so reopening the gate always re-reads the
        /// server. Within one session the data does not change underneath us.
        clearSharedFormData: function () {
            this._sharedForm = null;
        },

        fetchLibraryTags: function (apiClient, token, userId) {
            let url;
            try {
                url = apiClient.getUrl('Items/Filters2', { userId: userId, recursive: true });
            } catch (e) {
                return Promise.resolve([]);
            }
            return fetch(url, { headers: this.getAuthHeaders(token) })
                .then(res => res.ok ? res.json() : null)
                .then(data => {
                    const tags = (data && (data.Tags || data.tags)) || [];
                    return Array.from(new Set(tags.filter(tag => tag)))
                        .sort((a, b) => a.localeCompare(b));
                })
                .catch(() => []);
        },

        renderTagSuggestions: function (id, tags) {
            return `<datalist id="${id}">${(tags || []).map(tag => `<option value="${escapeHtml(tag)}"></option>`).join('')}</datalist>`;
        },

        renderTagChip: function (tag) {
            const safe = escapeHtml(tag);
            return `<span class="tag-chip" data-tag="${safe}"><span>${safe}</span><button type="button" class="tag-chip-remove" tabindex="0" aria-label="${t('profileForm.removeTagAria', { tag: safe })}">×</button></span>`;
        },

        renderTagEditor: function (id, tags, placeholder, suggestionsId) {
            const chips = (tags || []).map(tag => this.renderTagChip(tag)).join('');
            return `
                <div class="tag-editor" id="${id}">
                    <div class="tag-chip-list" data-empty-label="${escapeHtml(t('profileForm.noTags'))}" ${(tags || []).length ? '' : 'data-empty="true"'}>${chips}</div>
                    <div class="tag-input-row">
                        <input type="text" class="tag-input" placeholder="${escapeHtml(placeholder)}" list="${suggestionsId}" autocomplete="off" />
                        <button type="button" class="profiles-btn btn-secondary tag-add-btn">${t('common.add')}</button>
                    </div>
                </div>
            `;
        },

        /// The allowed-devices dropdown, shared by the Add and Edit profile forms.
        ///
        /// These two forms were built by two functions that had been copied from each
        /// other and then edited apart: 213 identical lines, ten runs of six or more.
        /// The copies had already drifted — a fix to one was a fix to one — and the
        /// duplicated *comments* were the clearest sign of it, the same sentence about
        /// sending null rather than an empty array sitting verbatim in both.
        ///
        /// Four things actually differ between the modes, and they are all here:
        ///   - create prefixes its element ids, because both forms can be in the
        ///     document at once and duplicate ids would make getElementById a coin toss;
        ///   - the checkbox class follows the same prefix, since the handlers query it;
        ///   - only edit has a profile to read existing selections from;
        ///   - only edit offers Forget, because a device cannot be stale for a profile
        ///     that does not exist yet.
        renderDeviceDropdown: function (mode, devices, profile) {
            const isEdit = mode === 'edit';
            const p = isEdit ? '' : 'create-';
            const boxClass = isEdit ? 'device-checkbox' : 'create-device-checkbox';

            const items = (devices && devices.length > 0)
                ? devices.map(dev => {
                    const deviceId = dev.deviceId || dev.DeviceId || '';
                    const deviceName = dev.deviceName || dev.DeviceName || t('profileForm.unknownDevice');
                    const client = dev.client || dev.Client || t('profileForm.unknownClient');
                    const lastSeen = dev.lastSeen || dev.LastSeen;
                    const lastSeenDate = lastSeen ? new Date(lastSeen) : null;
                    // Year 1 is what an unset DateTime serialises to; showing "01/01/0001"
                    // reads as a bug rather than as "we have never seen this device".
                    const lastSeenStr = (lastSeenDate && lastSeenDate.getFullYear() > 1)
                        ? lastSeenDate.toLocaleDateString() : t('profileForm.unknown');
                    // Both spellings, because the list can come from the plugin's own
                    // camelCase JSON or straight off a Jellyfin DTO.
                    const isChecked = isEdit && profile && profile.allowedDeviceIds
                        && (profile.allowedDeviceIds.includes(deviceId)
                            || (dev.DeviceId && profile.allowedDeviceIds.includes(dev.DeviceId)));
                    // Housekeeping actions belong to the edit form only: the create form is
                    // listing devices to tick, and a half-made profile is the wrong place to
                    // be reorganising the household's hardware.
                    //
                    // Rename and merge are here because one machine does not have one device
                    // id. jellyfin-web keeps its id in localStorage, which is per origin, so
                    // reaching the server by LAN address and by domain name produces two
                    // devices for one computer — as does a new browser profile or a reinstall.
                    // Nothing in the request can tell those apart from two real machines, so
                    // the plugin will not guess: merging two that are genuinely different
                    // would widen a whitelist. Renaming is what makes them tellable apart in
                    // the first place, and the name sticks — see KnownDevice.NameIsCustom.
                    const actions = isEdit
                        ? `<button type="button" class="device-rename-btn" data-id="${escapeHtml(deviceId)}" data-name="${escapeHtml(deviceName)}" title="${t('profileForm.renameDevice')}" aria-label="${t('profileForm.renameDeviceAria', { name: escapeHtml(deviceName) })}">✏️</button>
                           <button type="button" class="device-merge-btn" data-id="${escapeHtml(deviceId)}" data-name="${escapeHtml(deviceName)}" title="${t('profileForm.mergeDevice')}" aria-label="${t('profileForm.mergeDeviceAria', { name: escapeHtml(deviceName) })}">⇄</button>
                           <button type="button" class="device-delete-btn" data-id="${escapeHtml(deviceId)}" title="${t('profileForm.forgetDevice')}" aria-label="${t('profileForm.forgetDeviceAria', { name: escapeHtml(deviceName) })}">🗑️</button>`
                        : '';
                    return `
                        <div class="device-dropdown-item" data-device-id="${escapeHtml(deviceId)}">
                            <label style="display: flex; align-items: center; gap: 8px; cursor: pointer; flex: 1; margin: 0; font-size: 0.9rem; min-width: 0;">
                                <input type="checkbox" class="${boxClass}" value="${escapeHtml(deviceId)}" ${isChecked ? 'checked' : ''} style="cursor: pointer; accent-color: var(--jpf-accent); flex-shrink: 0;" />
                                <span style="display: flex; flex-direction: column; min-width: 0;">
                                    <span class="device-row-name" style="font-weight: 500; overflow: hidden; text-overflow: ellipsis;">${escapeHtml(deviceName)}</span>
                                    <span style="font-size: 0.75rem; opacity: 0.6;">${t('profileForm.deviceLastSeen', { client: escapeHtml(client), date: lastSeenStr })}</span>
                                </span>
                            </label>
                            ${actions}
                        </div>
                    `;
                }).join('')
                : `
                    <div style="padding: 12px; text-align: center; opacity: 0.6; font-size: 0.9rem;">${t('profileForm.noDevicesYet')}</div>
                `;

            return `
                <div class="form-group">
                    <label>${t('profileForm.allowedDevices')}</label>
                    <div class="devices-dropdown-container" style="position: relative;">
                        <div id="${p}devices-dropdown-trigger" class="devices-dropdown-trigger" tabindex="0" role="button" aria-expanded="false">
                            <span id="${p}devices-dropdown-selected-text">${t('profileForm.allDevicesAllowed')}</span>
                        </div>
                        <div id="${p}devices-dropdown-list" class="devices-dropdown-list" style="display: none;">
                            ${items}
                        </div>
                    </div>
                    <div class="form-hint">${t('profileForm.devicesHint')}</div>
                    ${isEdit ? `<button type="button" id="devices-tidy-btn" class="profiles-btn btn-secondary device-tidy-btn">${t('profileForm.tidyDevices')}</button>` : ''}
                </div>
            `;
        },

        /// The maximum-rating select, shared by both profile forms.
        ///
        /// The five options and their numeric values were written out twice; a rating
        /// added to one copy would silently not exist in the other. Only the id prefix
        /// and which option is preselected differ.
        ///
        /// `maxRating` is compared with ===, so it must already be a number or null.
        /// Passing the raw string from a form control would preselect nothing and
        /// quietly reset the profile's rating on the next save.
        renderRatingSelect: function (mode, maxRating) {
            const id = mode + '-rating-select';
            const opts = [
                ['', 'profileForm.noRestrictions'],
                ['6', 'profileForm.ratingG'],
                ['10', 'profileForm.ratingPG'],
                ['14', 'profileForm.ratingPG13'],
                ['17', 'profileForm.ratingR'],
            ].map(([value, key]) => {
                const current = value === '' ? maxRating === null || maxRating === undefined
                    : maxRating === Number(value);
                return `<option value="${value}" ${current ? 'selected' : ''}>${t(key)}</option>`;
            }).join('\n                            ');

            return `
                <div class="form-group">
                    <label for="${id}">${t('profileForm.maximumRating')}</label>
                    <select id="${id}">
                            ${opts}
                    </select>
                </div>
            `;
        },

        /// Blocked and allowed tag editors, shared by both profile forms.
        ///
        /// Only the id prefix and the starting tags differ: a new profile begins with
        /// none, an existing one with whatever it has. The allowed-tags hint carries
        /// form-hint-warn because an allow-list is subtractive — naming one tag hides
        /// everything without it — and that warning existing in only one of two copies
        /// is exactly the drift this merge is for.
        renderTagSection: function (mode, libraryTags, profile) {
            const p = mode + '-';
            const suggestions = p + 'tag-suggestions';
            const blocked = (profile && profile.blockedTags) || [];
            const allowed = (profile && profile.allowedTags) || [];

            return `
                ${this.renderTagSuggestions(suggestions, libraryTags)}
                <div class="form-group">
                    <label>${t('profileForm.blockedTags')}</label>
                    ${this.renderTagEditor(p + 'blocked-tags', blocked, t('profileForm.tagPlaceholderAdults'), suggestions)}
                    <div class="form-hint">${t('profileForm.blockedTagsHint')}</div>
                </div>
                <div class="form-group">
                    <label>${t('profileForm.allowedTags')}</label>
                    ${this.renderTagEditor(p + 'allowed-tags', allowed, t('profileForm.tagPlaceholderKids'), suggestions)}
                    <div class="form-hint form-hint-warn">${t('profileForm.allowedTagsHint')}</div>
                </div>
            `;
        },

        initTagEditors: function (container) {
            container.querySelectorAll('.tag-editor').forEach(editor => {
                if (editor._tagInit) return;
                editor._tagInit = true;

                const chipList = editor.querySelector('.tag-chip-list');
                const input = editor.querySelector('.tag-input');
                const addBtn = editor.querySelector('.tag-add-btn');
                if (!chipList || !input || !addBtn) return;

                const syncEmpty = () => {
                    if (chipList.querySelector('.tag-chip')) {
                        chipList.removeAttribute('data-empty');
                    } else {
                        // Set here too: a list that starts non-empty carries no label, and
                        // removing its last chip would otherwise show an empty grey box.
                        chipList.setAttribute('data-empty-label', t('profileForm.noTags'));
                        chipList.setAttribute('data-empty', 'true');
                    }
                };

                const addTag = () => {
                    const value = (input.value || '').trim();
                    input.value = '';
                    if (!value) return;
                    const exists = Array.from(chipList.querySelectorAll('.tag-chip')).some(
                        chip => (chip.getAttribute('data-tag') || '').toLowerCase() === value.toLowerCase());
                    if (!exists) {
                        chipList.insertAdjacentHTML('beforeend', this.renderTagChip(value));
                        syncEmpty();
                    }
                };

                addBtn.addEventListener('click', (e) => { e.preventDefault(); addTag(); input.focus(); });
                input.addEventListener('keydown', (e) => {
                    if (e.key === 'Enter') { e.preventDefault(); addTag(); }
                });
                // Committing on blur means a typed-but-not-added tag isn't silently lost on save.
                input.addEventListener('blur', () => addTag());

                chipList.addEventListener('click', (e) => {
                    const removeBtn = e.target.closest('.tag-chip-remove');
                    if (!removeBtn) return;
                    e.preventDefault();
                    removeBtn.closest('.tag-chip').remove();
                    syncEmpty();
                });
                chipList.addEventListener('keydown', (e) => {
                    if (e.key !== 'Enter' && e.key !== ' ') return;
                    const removeBtn = e.target.closest('.tag-chip-remove');
                    if (!removeBtn) return;
                    e.preventDefault();
                    removeBtn.closest('.tag-chip').remove();
                    syncEmpty();
                });
            });
        },

        getTagEditorValues: function (container, id) {
            const editor = container.querySelector('#' + id);
            if (!editor) return [];
            return Array.from(editor.querySelectorAll('.tag-chip'))
                .map(chip => chip.getAttribute('data-tag'))
                .filter(tag => tag);
        },

        /// Runs one startup step, and refuses to let it take the rest of init() with it.
        ///
        /// 1.5.2 shipped a stray backtick inside the stylesheet template literal in
        /// injectStyles. The file still parsed — it was valid JavaScript, just not what
        /// anyone meant — so the syntax check passed and it went out. At runtime
        /// injectStyles threw, and because init() called these bare, the nine steps after
        /// it never ran: no focus trap, so a remote could not reach the gate; no switcher
        /// preferences; no panic shortcut, which is the escape hatch for exactly this;
        /// no session validation. bindEvents had already run, so the route poll kept
        /// raising an unstyled gate and the plugin looked alive while almost nothing
        /// worked. A broken stylesheet should cost styling and nothing else.
        _step: function (name, fn) {
            try {
                fn();
            } catch (e) {
                console.error('ProfilesPlugin: startup step "' + name + '" failed.', e);
            }
        },

        /// Upstream's TV test, copied so the two cannot disagree.
        ///
        /// From jellyfin-web src/scripts/browser.js isTv(), which is the function behind
        /// browser.tv and therefore behind layoutManager's own auto-detection. Its own
        /// first comment is "This is going to be really difficult to get right", which is
        /// why this is a copy rather than something reasoned out afresh.
        ///
        /// `web0s` is spelt with a ZERO. That is upstream's spelling because it is what
        /// LG actually puts in the user agent, and writing the letter O here would miss
        /// every webOS set silently.
        _uaLooksLikeTv: function (ua) {
            const s = String(ua || '').toLowerCase();
            // The Oculus browser carries samsungbrowser in its UA and is a headset.
            if (s.indexOf('oculusbrowser') !== -1) return false;
            return s.indexOf('tv') !== -1
                || s.indexOf('samsungbrowser') !== -1
                || s.indexOf('viera') !== -1
                || s.indexOf('titanos') !== -1
                || s.indexOf('netcast') !== -1
                || s.indexOf('web0s') !== -1;
        },

        /// Does this browser lay out flex containers with `gap`?
        ///
        /// Chrome 84. webOS 5 (the LG CX) is Chromium 68 and Tizen 6.0 is 76, so on both
        /// of the televisions this plugin has open issues from, every `gap` on a flex
        /// container is ignored and the spacing between children is simply gone.
        ///
        /// Measured, not asked. `@supports (gap: 1px)` answers TRUE on Chromium 68,
        /// because grid gap shipped in 66 and the query cannot tell the two layout modes
        /// apart — so the obvious feature query returns yes on exactly the devices where
        /// flex gap does nothing. Two empty children in a column box with a 1px row-gap
        /// make the box 1px tall where it works and 0 where it does not.
        _supportsFlexGap: function () {
            if (this._flexGapOk !== undefined) return this._flexGapOk;
            if (!document.body) return true;   // too early to tell; assume modern
            let probe;
            try {
                probe = document.createElement('div');
                probe.style.cssText = 'display:flex;flex-direction:column;row-gap:1px;'
                    + 'position:absolute;visibility:hidden;pointer-events:none';
                probe.appendChild(document.createElement('div'));
                probe.appendChild(document.createElement('div'));
                document.body.appendChild(probe);
                this._flexGapOk = probe.scrollHeight === 1;
            } catch (e) {
                this._flexGapOk = true;
            } finally {
                if (probe && probe.parentNode) probe.parentNode.removeChild(probe);
            }
            return this._flexGapOk;
        },

        /// Puts `jpf-tv` and `jpf-no-flex-gap` on <html>, so the stylesheet can size for
        /// a screen three metres away and can replace flex gaps the device ignores.
        ///
        /// The plan said to read `layoutManager.tv`. It cannot be read: layoutManager is
        /// an ES module default export in jellyfin-web and is never placed on window.
        /// What it does do is write `layout-tv` / `layout-mobile` / `layout-desktop` onto
        /// document.documentElement (src/components/layoutManager.js, setLayout), so that
        /// class is the supported signal — and it is strictly better than sniffing,
        /// because it also honours a layout the user has forced in Display settings. A
        /// desktop set to TV mode gets TV sizing, which sniffing would never give it.
        ///
        /// The UA is only the fallback, for the two moments the class is not there yet:
        /// a cold load where we run before layoutManager.init(), and the pre-sign-in gate.
        ///
        /// Re-run on class changes rather than on a timer. layoutManager can switch mode
        /// at runtime and fires 'modechange', but that event is on the module object we
        /// cannot reach; the class attribute it writes is observable, costs nothing when
        /// nothing changes, and is not the route poll's business.
        applyDeviceClasses: function () {
            const root = document.documentElement;
            if (!root) return;

            // Writes only on an actual change. The observer below watches the very
            // attribute this touches, and a write that changes nothing would still be a
            // mutation record and a second pass through here on every unrelated class
            // change Jellyfin makes.
            const apply = () => {
                const isTv = root.classList.contains('layout-tv')
                    || (!root.classList.contains('layout-mobile')
                        && !root.classList.contains('layout-desktop')
                        && this._uaLooksLikeTv(navigator.userAgent));
                if (isTv !== root.classList.contains('jpf-tv')) {
                    root.classList.toggle('jpf-tv', isTv);
                }
            };

            apply();
            root.classList.toggle('jpf-no-flex-gap', !this._supportsFlexGap());

            if (this._deviceClassObserver || typeof MutationObserver === 'undefined') return;
            this._deviceClassObserver = new MutationObserver(() => apply());
            this._deviceClassObserver.observe(root, {
                attributes: true,
                attributeFilter: ['class'],
            });
        },

        init: function () {
            if (typeof ApiClient === 'undefined') {
                // If ApiClient is not defined yet, wait for it
                setTimeout(() => this.init(), 100);
                return;
            }
            // viewshow fires when Jellyfin's React view system finishes rendering a view.
            // We gate _revealPage() on this event so we never fade in to a blank shell.
            this._viewShowFired = false;
            this._pendingReveal = false;
            // Kicked off now so it usually finishes before anyone actually needs a
            // translated string — fetchAndRenderProfiles awaits it just before drawing
            // the gate for the first time, which is the only render early enough to
            // matter.
            this._step('loadLocale', () => { this._i18nReady = loadLocale(); });
            // fetchAndRenderProfiles awaits this before the first gate render, so it has to
            // be a promise even if the step above failed.
            if (!this._i18nReady) this._i18nReady = Promise.resolve();

            this._step('bindEvents', () => this.bindEvents());
            // Before injectStyles, so the sheet's .jpf-tv and .jpf-no-flex-gap rules match
            // on the first paint rather than after a reflow the viewer can see.
            this._step('applyDeviceClasses', () => this.applyDeviceClasses());
            this._step('injectStyles', () => this.injectStyles());
            // Kicked off before the first route check so the gate decision is usually made
            // with the real answer in hand rather than the cached one.
            // Before any Jellyfin view renders: the cached rules have to be in the document
            // by the time the first library card paints, or the artwork this profile is not
            // meant to see gets a frame on screen.
            this._step('applyCachedLibraryArtwork', () => this.applyCachedLibraryArtwork());
            this._step('loadLibraryArtwork', () => this.loadLibraryArtwork());
            this._step('loadSwitcherPrefs', () => this.loadSwitcherPrefs());
            this._step('bindPanicShortcut', () => this.bindPanicShortcut());
            // Bound once for the life of the page. It resolves the active Bonfire screen on
            // every event and does nothing when there is none, so it covers the gate, the
            // PIN prompt, the profile forms and every dialog without per-screen wiring.
            this._step('_bindOverlayFocusTrap', () => this._bindOverlayFocusTrap());
            // Before validateSessionState, which can trigger a reload of its own.
            this._step('checkPersistedPanic', () => this.checkPersistedPanic());
            if (this._panicDisabled) return;
            this._step('validateSessionState', () => this.validateSessionState());
            // If the user refreshes while a profile is active, restart the inactivity timer
            setTimeout(() => this._step('initLockoutTimer', () => this.initLockoutTimer()), 800);
        },

        bindEvents: function () {
            const doCheck = () => this.checkRoute();

            // Jellyfin view-system events (fires on every page/view change)
            // Use doCheck so all logic is centralised in checkRoute.
            document.addEventListener('viewshow', () => {
                // Mark the view as ready so _revealPage() knows content is rendered.
                this._viewShowFired = true;
                // If _revealPage() was deferred waiting for this event, run it now.
                if (this._pendingReveal) this._revealPage();
                doCheck();
            });

            // SPA navigation events
            window.addEventListener('popstate', doCheck);
            window.addEventListener('hashchange', doCheck);

            // The corner-pill fallback is placed by geometry, so its position is stale
            // only when the viewport changes — a rotation on a phone, a window resize.
            // Marking it here is what lets the route tick stop recomputing it.
            ['resize', 'orientationchange'].forEach(evt => {
                window.addEventListener(evt, () => { this._fallbackPosDirty = true; });
            });

            // Intercept history.pushState / replaceState (React-router style navigation)
            ['pushState', 'replaceState'].forEach(method => {
                const orig = history[method];
                history[method] = function (...args) {
                    orig.apply(history, args);
                    // Small delay so the URL is committed before we read it
                    setTimeout(doCheck, 0);
                };
            });

            // Clear the cached master token on native Jellyfin sign-out, so it does not
            // persist in localStorage on a shared or public device. Signing out of
            // Jellyfin has to mean signing out of Bonfire too, or the next person at the
            // machine inherits the switcher.
            document.addEventListener('usersignedout', () => {
                try {
                    localStorage.removeItem(this.config.masterStorageKey);
                    localStorage.removeItem(this.config.activeSessionKey);
                    localStorage.removeItem(this.config.switcherModeKey);
                    this._switcherPrefs = null;
                    // Learned from the same response as the preferences, so it goes with them.
                    this._panicLinkAvailable = null;
                    localStorage.removeItem(this.config.libraryArtKey);
                    this.clearProfileSession();
                } catch (e) { /* ignore storage errors */ }
            });

            // 500 ms is sufficient because viewshow, popstate and hashchange already cover
            // every SPA navigation. The poll is only a safety net for the cases that change
            // the DOM without changing the route — the video OSD is the one that matters.
            //
            // It backs off to 2 s once playback starts. There is nothing for the plugin to
            // do during playback — the gate is not raised, the bubble is hidden, and the
            // menus are not on screen — and that is exactly when the device has least
            // headroom: a TV decoding 4K is the weakest hardware in the house doing the
            // hardest thing it ever does. Four times fewer wake-ups for work that has no
            // effect.
            //
            // Not stopped outright. The tick is also what notices playback *ending* on a
            // client whose URL does not change, and a poll that switched itself off would
            // then need something else to switch it back on. 2 s is a quarter of a second
            // of latency on the bubble reappearing, which nobody can see.
            this._tickMs = 500;
            const arm = () => {
                clearInterval(this._routeTimer);
                this._routeTimer = setInterval(() => {
                    doCheck();
                    // _lastRouteType is set by checkRoute, so this reads the classification
                    // that has just been made rather than repeating the work to get it.
                    const want = this._lastRouteType === 'videoosd' ? 2000 : 500;
                    if (want !== this._tickMs) {
                        this._tickMs = want;
                        arm();
                    }
                }, this._tickMs);
            };
            arm();

            // Initial check on load
            setTimeout(doCheck, 200);
        },

        /// True when the browser is sitting on the home screen.
        ///
        /// Extracted from checkRoute so reloadAtHome() can ask the same question. The two
        /// must agree: reloadAtHome only redirects when this is false, so a route this
        /// says is home is a route it will never try to build a URL for.
        isHomeRoute: function () {
            const hash = window.location.hash || '';
            const path = window.location.pathname || '';

            // Check if we are on the home screen
            // The home screen route can be: empty, '#/', '#/home', '#/home.html', or similar.
            // But we must NOT trigger it if we are on pages like configuration, plugins, selectserver, login, etc.
            const isIgnoredPage = hash.includes('configuration') ||
                                 hash.includes('plugin') ||
                                 hash.includes('login') ||
                                 hash.includes('selectserver') ||
                                 path.includes('configuration') ||
                                 path.includes('plugin') ||
                                 path.includes('login') ||
                                 path.includes('selectserver');

            if (isIgnoredPage) return false;

            // 10.11 moved the route out of the fragment and into the path, so on those
            // builds the hash is empty on every page — and the `hash === ''` clause below
            // was therefore answering "yes, this is home" for all of them. Whenever the
            // path carries a route, it is the authority and the hash has nothing to say.
            const webRoute = path.match(/^.*\/web\/(.*)$/);
            if (webRoute) {
                const route = webRoute[1].replace(/\/+$/, '');
                if (route !== '' && route !== 'index.html') {
                    return route === 'home' || route === 'home.html';
                }
            }

            return (
                hash === '' ||
                hash === '#/' ||
                hash.includes('home') ||
                path.endsWith('/home') ||
                path.endsWith('/home.html') ||
                // If there is no hash and we are at /web/index.html or root
                (!hash && (path.endsWith('index.html') || path === '/' || path === '/web/'))
            );
        },

        /// The home screen's URL in whichever routing style this build of jellyfin-web
        /// uses, or null when neither shape is recognisable.
        ///
        /// Two styles are in the wild: 10.10 and earlier keep the route in the fragment
        /// ('/web/index.html#/home.html'), 10.11 moved it into the path ('/web/home').
        /// Guessing wrong here means a 404 rather than a stale page, so an unrecognised
        /// shape returns null and the caller stays put instead.
        homeUrl: function () {
            const loc = window.location;
            const hash = loc.hash || '';

            // Hash-routed: the document does not change, only the fragment. Whichever
            // prefix this build uses is reused rather than guessed at.
            if (hash.startsWith('#!/')) return loc.pathname + loc.search + '#!/home.html';
            if (hash.startsWith('#/')) return loc.pathname + loc.search + '#/home.html';

            // Path-routed: '/web/mypreferencesmenu' -> '/web/home'. Matched from '/web/'
            // so a server hosted on a base path keeps it.
            const m = loc.pathname.match(/^(.*\/web\/)/);
            if (m) return m[1] + 'home';

            return null;
        },

        /// Reloads onto the home screen. For every caller here the signed-in identity has
        /// just changed, so the app must re-initialise under the new token — and the page
        /// that was on screen belonged to the previous profile.
        ///
        /// Issue #22: this used to be a bare reload, which preserves the route. Switching
        /// from the user menu therefore reloaded /web/mypreferencesmenu as the new
        /// profile, leaving the user to find their own way home. The gate never showed it
        /// because the gate only ever runs from home, where the two are the same thing.
        reloadAtHome: function () {
            // Already home: reload exactly as before. This is the path the gate uses and
            // the one with hardware testing behind it, so it is left untouched. It also
            // keeps homeUrl() away from the one case it could get wrong — a hash-routed
            // build sitting at '/web/' with no fragment, where the path branch would
            // wrongly produce '/web/home'.
            if (this.isHomeRoute()) {
                this._reloadWithFallback(null);
                return;
            }

            this._reloadWithFallback(this.homeUrl());
        },

        /// How long to wait before deciding a navigation attempt did not take.
        ///
        /// Long enough that a slow television is not interrupted mid-teardown, short
        /// enough that somebody who tapped a profile is not left staring at the wrong
        /// account. A page that is genuinely unloading never reaches the timer.
        RELOAD_ESCALATE_MS: 1200,

        /// Reloads the page, and checks that the reload happened.
        ///
        /// By the time this runs the switch is complete everywhere except the screen: the
        /// profile's token is in localStorage and in ApiClient, and all that is left is for
        /// the document to be rebuilt under the new identity. If that does not happen,
        /// jellyfin-web goes on rendering the page it built for the *previous* profile —
        /// its home sections, its Continue Watching, its Next Up — while the header shows
        /// the profile that was picked, because Bonfire draws that part itself.
        ///
        /// Reported on Samsung Tizen as "it lets you pick a profile, but nothing changes
        /// once you load in". The credentials, the token and the server were all ruled out
        /// first; what was left is that `location.reload()` is not reliable everywhere this
        /// runs. The bundled television clients serve jellyfin-web from a local file://
        /// origin, which is where it has been seen to do nothing at all.
        ///
        /// So each step is a different mechanism rather than a retry of the same one. The
        /// last rung says so out loud: showing a child the parent's Continue Watching while
        /// they believe it is theirs is worse than admitting the reload failed.
        _reloadWithFallback: function (target) {
            const step = (n) => {
                try {
                    if (n === 0) {
                        if (target) {
                            // replace() rather than assignment so Back does not return to
                            // the previous profile's page.
                            window.location.replace(target);
                            // A fragment-only change does not reload the document, and a
                            // full re-initialisation is the entire point of the call.
                            if (target.indexOf('#') !== -1) window.location.reload();
                        } else {
                            window.location.reload();
                        }
                    } else if (n === 1) {
                        // Assignment, not replace(): a different code path in the browser,
                        // which is the only reason to try it at all.
                        window.location.href = target || window.location.href;
                    } else if (n === 2) {
                        // Force a cache-busting navigation. Some webviews treat a reload
                        // of an identical URL as a no-op but will honour a changed query.
                        const url = target || window.location.href;
                        const sep = url.indexOf('?') === -1 ? '?' : '&';
                        const bust = url.indexOf('#') === -1
                            ? url + sep + 'jpf=' + Date.now()
                            : url.replace('#', sep + 'jpf=' + Date.now() + '#');
                        window.location.href = bust;
                    } else {
                        this._reloadFailed();
                        return;
                    }
                } catch (e) {
                    // A blocked navigation must not stop the ladder.
                }
                this._reloadTimer = setTimeout(() => step(n + 1), this.RELOAD_ESCALATE_MS);
            };

            // A navigation that has committed fires pagehide, and on a set slow enough to
            // still be running timers a step later the escalation would then navigate a
            // second time — onto a cache-busted URL nobody asked for. The ladder is for a
            // reload that does nothing, not for one that is merely slow.
            const stop = () => { if (this._reloadTimer) clearTimeout(this._reloadTimer); };
            window.addEventListener('pagehide', stop);
            window.addEventListener('beforeunload', stop);

            step(0);
        },

        /// Nothing rebuilt the page, so the profile that was chosen is signed in but the
        /// screen still belongs to whoever was there before. Reveal the page and say so —
        /// the alternative is leaving somebody looking at another person's library and
        /// believing it is their own.
        _reloadFailed: function () {
            this._reloading = false;
            try { localStorage.removeItem(this.config.switchingKey); } catch (e) { /* ignore */ }

            // The switch hid the page behind an opaque overlay, or behind the head
            // script's opacity:0. Either would leave this message invisible.
            document.documentElement.style.removeProperty('opacity');
            document.documentElement.style.removeProperty('background');
            const overlay = document.getElementById('profiles-gate-overlay');
            if (overlay) overlay.remove();

            console.error('ProfilesPlugin: the page could not be reloaded after switching.');
            this.showAlert(t('switcher.reloadFailedTitle'), t('switcher.reloadFailedBody'));
        },

        checkRoute: function () {
            // Emergency disable: do nothing at all. This runs on a 500 ms timer, so without
            // this guard it would rebuild the gate immediately after the teardown.
            if (this._panicDisabled) return;

            const hash = window.location.hash || '';
            const path = window.location.pathname || '';

            const isHome = this.isHomeRoute();

            // skipReveal: set to true when we know the gate overlay is about to be shown.
            // In that case showProfileOverlay() will call _revealPage() once the overlay
            // covers the page, preventing a blank-home flash during the async profile fetch.
            let skipReveal = false;

            if (isHome) {
                // Preferences are null until we have learned them for this account. Waiting
                // is the safe direction: loadSwitcherPrefs() re-runs this check the moment it
                // resolves, so at worst the gate arrives a beat late — whereas guessing would
                // flash a full-screen overlay at somebody who turned it off, on every load.
                //
                // Usually unknown because init() ran before the user had signed in, so there
                // was no token to ask with. Retrying here is what gets the gate working on the
                // first home screen after a fresh login. loadSwitcherPrefs guards itself and
                // settles on the historical default if the request fails, so this cannot spin
                // on the 500 ms route poll.
                // _panicLinkAvailable rides on the same response and is never cached, so a
                // browser holding a valid preferences cache would otherwise never learn it
                // and would hide the emergency link for the whole session.
                if (this.getSwitcherPrefs() === null || this._panicLinkAvailable === null) {
                    this.loadSwitcherPrefs();
                }

                // Same reason: init runs before sign-in on a fresh load, and there was no
                // token to ask with. Without a retry the artwork rules never arrive.
                if (!this._libraryArtLoaded) this.loadLibraryArtwork();

                if (this.shouldAskOnStartup()
                    && !this.isProfileSessionActive()
                    && !document.getElementById('profiles-gate-overlay')) {
                    skipReveal = true;
                    this.interceptHomeAndShowProfiles();
                }
            } else {
                // If we navigate away from home, ensure the overlay is removed if it somehow got stuck
                if (document.getElementById('profiles-gate-overlay') && this.isProfileSessionActive()) {
                    this.removeProfileOverlay();
                }
            }

            // Monitor and hide shadow profiles from admin user management list
            const isUsersPage = hash.includes('users') || path.includes('users');
            if (isUsersPage) {
                this.monitorAndHideShadowProfiles();
            } else {
                if (this.usersObserver) {
                    this.usersObserver.disconnect();
                    this.usersObserver = null;
                }
                this.isMonitoringUsers = false;
                this.subProfileIdsToHide = null;
            }

            // ── Active player: URL-based detection ──────────────────────────────
            const isActivePlayer = hash.includes('videoosd') ||
                                   hash.includes('/nowplaying') ||
                                   (hash.includes('video') && !hash.includes('videos'));

            // ── Active player: DOM-based detection (catches delayed URL updates) ──
            // The OSD element appears in the DOM the moment playback starts.
            // Class selectors only. This list carried [class*="videoOsd"] and
            // [class*="osdBottom"] as well, and an unqualified attribute-substring
            // selector cannot use the class index — the engine walks every element in the
            // document and tests each one. That ran twice a second, for the life of the
            // page, on every device, including the weakest hardware in the house *during
            // playback*, which is the one moment it has least to spare.
            //
            // They also never matched anything the three explicit names beside them did
            // not already match: .videoOsdBottom is itself the element [class*="videoOsd"]
            // was reaching for. Verified against jellyfin-web 10.11 —
            // src/controllers/playback/video/index.html — and recorded in
            // tests/upstream-selectors.json.
            const hasOsdDom = !!document.querySelector(
                '.videoOsdBottom, .osdControls, .upNextContainer, .btnExitVideo'
            );

            // ── Admin / server-management pages ─────────────────────────────────
            // Exception: our own plugin settings page (configurationpage?name=Bonfire) is
            // the only admin-area page where the button should stay visible.
            //
            // Both spellings are accepted. The page was registered as "Profiles" until 1.5
            // and the name is what forms this URL, so a browser sitting on the old address —
            // a bookmark, or a tab open across the upgrade — would otherwise lose the button.
            const isProfilesSettingsPage = hash.includes('configurationpage')
                && (hash.toLowerCase().includes('name=bonfire')
                    || hash.toLowerCase().includes('name=profiles'));

            const isDashboard = !isProfilesSettingsPage && (
                hash.includes('dashboard')       || hash.includes('/admin')       ||
                hash.includes('useredit')        || hash.includes('usernew')      ||
                hash.includes('userparentalcontrol') || hash.includes('userlibraryaccess') ||
                hash.includes('userpassword')    || hash.includes('scheduledtasks') ||
                hash.includes('serveractivity')  || hash.includes('installedplugins') ||
                hash.includes('pluginscatalog')  || hash.includes('apikeys')      ||
                hash.includes('devices')         || hash.includes('dlnaprofiles') ||
                hash.includes('dlnasettings')    || hash.includes('networking')   ||
                hash.includes('notificationlist')|| hash.includes('streamingsettings') ||
                hash.includes('playbackconfiguration') ||                           
                hash.includes('library.html')    || hash.includes('librarydisplay') ||
                hash.includes('librarypathmapping') || hash.includes('log.html')  ||
                hash.includes('metadataeditor')  || hash.includes('metadatamanager') ||
                hash.includes('edititemmetadata')|| hash.includes('mediainfo')    ||
                hash.includes('configurationpage')  || // all other plugin config pages
                path.includes('dashboard')       || path.includes('/admin')
            );

            const viewType = (isActivePlayer || hasOsdDom) ? 'videoosd'
                           : isDashboard                   ? 'dashboard'
                           : isHome                        ? 'home'
                                                          : 'other';
            this._lastRouteType = viewType;
            this.evaluateFloatingBubbleVisibility(viewType);

            // Menu-location entry points. Both are re-asserted on every route change
            // because React discards and rebuilds these views freely; the calls are cheap
            // and no-op when the element is already in place.
            this.syncUserMenuEntry();
            this.syncPreferencesMenuEntry();
            if (this.isMenuLocation()) {
                const isUserProfilePage = hash.includes('userprofile') || path.includes('userprofile');
                if (isUserProfilePage) this.injectProfilePageSection();
            }

            // Reveal the page now that the gate decision has been made.
            // Skip when skipReveal is set — the overlay isn't in the DOM yet and
            // revealing now would show a blank page during the profile fetch.
            if (!skipReveal) this._revealPage();

        },

        // Smoothly fades the page back in after a profile switch.
        // Guards on _viewShowFired to ensure React has rendered the view before we
        // expose it — otherwise a blank white shell flashes for a moment.
        /**
         * Records the colour the page is currently painted in, so the next load can hold
         * that colour while it renders instead of a hardcoded one.
         *
         * The head script used to set background:#101010 and color-scheme:dark
         * unconditionally. Jellyfin ships light themes and lets a user pick any of them,
         * so on every one of those a profile switch flashed a full-screen black rectangle
         * — the exact flash the head script exists to prevent, just in the other
         * direction. It also forced dark form controls and scrollbars for that moment.
         *
         * Read here rather than guessed there, because here is the one place the answer is
         * knowable: the page is fully rendered, the theme stylesheet has applied, and
         * whatever the user is looking at is what we should keep showing.
         *
         * Returns the colour, so the caller can paint with it immediately as well.
         */
        _captureLeavingBackground: function () {
            let colour = '';
            try {
                // The body carries the theme colour in Jellyfin's stylesheets; when it is
                // transparent the colour is on <html> behind it, so ask that instead.
                const fromBody = getComputedStyle(document.body || document.documentElement).backgroundColor;
                colour = this._isSeeThrough(fromBody)
                    ? getComputedStyle(document.documentElement).backgroundColor
                    : fromBody;
            } catch (e) { /* fall through to the default */ }

            if (!colour || this._isSeeThrough(colour)) colour = '#101010';

            // The scheme has to match the colour or the browser paints dark scrollbars and
            // form controls over a light page for the length of the switch.
            const scheme = this._isLightColour(colour) ? 'light' : 'dark';

            try {
                localStorage.setItem(this.config.backgroundKey, colour + '|' + scheme);
            } catch (e) { /* private mode: the head script falls back on its own */ }

            return colour;
        },

        /** True for a colour that is missing or fully transparent. */
        _isSeeThrough: function (colour) {
            if (!colour) return true;
            if (colour === 'transparent') return true;
            const m = /^rgba\(\s*[\d.]+\s*,\s*[\d.]+\s*,\s*[\d.]+\s*,\s*0*\.?0*\s*\)$/.exec(colour);
            return !!m;
        },

        /**
         * Relative luminance, so "which colour-scheme is this" is decided by the colour
         * itself rather than by a list of theme names that would need updating whenever
         * Jellyfin adds one.
         */
        _isLightColour: function (colour) {
            const m = /^rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)/.exec(colour);
            if (!m) return false;
            const r = parseFloat(m[1]), g = parseFloat(m[2]), b = parseFloat(m[3]);
            return (0.2126 * r + 0.7152 * g + 0.0722 * b) > 128;
        },

        _revealPage: function () {
            if (this._pageRevealed || !document.documentElement.style.opacity) return;

            // Defer until Jellyfin's view system has finished rendering the view.
            // _viewShowFired is set by the viewshow listener in bindEvents().
            if (!this._viewShowFired) {
                this._pendingReveal = true;
                return;
            }

            this._pageRevealed = true;
            this._pendingReveal = false;

            // The reload this flag was raised for has landed. Clearing it here rather than
            // only in removeProfileOverlay() matters twice over: the session mirror is
            // believed only while the flag is up, so leaving it set would keep a marker
            // alive across a genuine app close; and the head script hides the page whenever
            // it sees the flag, so a stale one cost a fade on every later hard refresh.
            //
            // Safe this late: validateSessionState() ran during init() and has already
            // pulled anything it needed into the in-memory tier.
            if (!this._reloading) {
                try { localStorage.removeItem(this.config.switchingKey); } catch (e) { /* ignore */ }
                // The head script has almost certainly already taken the key; this is the
                // copy that actually outlives it.
                try { window.__jpSwitching = 0; } catch (e) { /* ignore */ }
            }

            if (window.__jpReveal) {
                clearTimeout(window.__jpReveal);
                window.__jpReveal = null;
            }

            document.documentElement.style.transition = 'opacity 0.18s ease';
            document.documentElement.style.opacity = '1';
            setTimeout(() => {
                document.documentElement.style.removeProperty('opacity');
                document.documentElement.style.removeProperty('transition');
                document.documentElement.style.removeProperty('background');
                document.documentElement.style.removeProperty('color-scheme');
                this._pageRevealed = false;
            }, 220);
        },

        isMonitoringUsers: false,
        subProfileIdsToHide: null,
        usersObserver: null,

        monitorAndHideShadowProfiles: function () {
            const apiClient = ApiClient;
            if (!apiClient) return;

            // Fetch only once per page visit
            if (this.isMonitoringUsers) {
                this.applyUsersHide();
                return;
            }
            this.isMonitoringUsers = true;

            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            const token = masterState ? masterState.masterToken : apiClient.accessToken();
            if (!token) return;

            const url = apiClient.getUrl('plugins/profiles/admin/mappings');
            fetch(url, {
                headers: this.getAuthHeaders(token)
            })
            .then(res => {
                if (!res.ok) throw new Error("Could not load mappings");
                return res.json();
            })
            .then(data => {
                const subProfiles = data.SubProfiles || [];
                this.subProfileIdsToHide = subProfiles.map(p => this.normalizeGuid(p.ProfileUserId));
                
                // Start a MutationObserver to hide cards as they are rendered dynamically
                if (this.usersObserver) {
                    this.usersObserver.disconnect();
                }

                this.usersObserver = new MutationObserver(() => this.applyUsersHide());
                this.usersObserver.observe(document.body, { childList: true, subtree: true });
                this.applyUsersHide();
            })
            .catch(err => {
                console.error("ProfilesPlugin: Error fetching admin mappings for hide logic:", err);
                this.isMonitoringUsers = false;
            });
        },

        applyUsersHide: function () {
            if (!this.subProfileIdsToHide || this.subProfileIdsToHide.length === 0) return;

            // Find all cards, list items, or elements with data-id or containing useredit links
            const cards = document.querySelectorAll('.card, .listItem, [data-id]');
            cards.forEach(card => {
                let id = card.getAttribute('data-id');
                if (id) {
                    id = this.normalizeGuid(id);
                    if (this.subProfileIdsToHide.includes(id)) {
                        card.style.display = 'none';
                        return;
                    }
                }

                // Check for links inside (e.g. useredit.html?userId=...)
                const links = card.querySelectorAll('a');
                for (let i = 0; i < links.length; i++) {
                    const href = links[i].getAttribute('href') || '';
                    if (href.includes('userId=')) {
                        const match = href.match(/userId=([^&]+)/);
                        if (match) {
                            const normalizedId = this.normalizeGuid(match[1]);
                            if (this.subProfileIdsToHide.includes(normalizedId)) {
                                card.style.display = 'none';
                                break;
                            }
                        }
                    }
                }
            });
        },

        // ── Active-profile session storage ────────────────────────────────────────
        // The active profile lives in sessionStorage deliberately: closing the app should
        // drop back to the picker rather than leave a child profile signed in.
        //
        // Some runtimes throw sessionStorage away on a full reload — and a full reload is
        // exactly how a profile switch finishes. The marker is then gone by the time the
        // page comes back, validateSessionState() concludes the app was closed, and it
        // reverts to the master token. On Samsung's Tizen that looked like the avatar
        // changing and nothing else (issues #15 and #16). On Android it is worse, because
        // the revert reloads: the reload loses the marker again, which reverts again — the
        // picker over and over, which is what an Android reporter saw.
        //
        // That was first fixed by sniffing for Tizen, which meant meeting each new runtime
        // through a bug report. There is no sniff any more. The rule is about WHY
        // sessionStorage is empty, which is the part that actually decides the answer:
        //
        //   empty because the app was closed     -> show the picker, drop the profile
        //   empty because our own reload lost it -> put it back, keep the profile
        //
        // and those two are told apart by the switching flag, which is set immediately
        // before every reload this plugin performs and at no other time.
        //
        // Values are also held in memory for the life of the page, so a runtime where
        // sessionStorage does not work at all still behaves while the page lives.
        SESSION_MIRROR_PREFIX: 'jpf-persist-',

        /// How long after our own reload the mirror is still believable. A reload takes
        /// seconds; anything older is a new session wearing a flag nobody cleared.
        SESSION_MIRROR_MAX_AGE_MS: 60000,

        _sessionMemory: {},

        _sessionSet: function (key, value) {
            this._sessionMemory[key] = value;
            try { sessionStorage.setItem(key, value); } catch (e) { /* storage blocked */ }
            try {
                localStorage.setItem(
                    this.SESSION_MIRROR_PREFIX + key,
                    JSON.stringify({ v: value, t: Date.now() }));
            } catch (e) { /* full or blocked — sessionStorage is still the primary */ }
        },

        _sessionGet: function (key) {
            if (Object.prototype.hasOwnProperty.call(this._sessionMemory, key)) {
                return this._sessionMemory[key];
            }

            let value = null;
            try { value = sessionStorage.getItem(key); } catch (e) { /* storage blocked */ }
            if (value !== null) {
                this._sessionMemory[key] = value;
                return value;
            }

            const mirrored = this._readSessionMirror(key);
            if (mirrored === null) return null;

            // Put it back where the rest of the code expects to find it, so this costs one
            // read per page load rather than one per call.
            this._sessionMemory[key] = mirrored;
            try { sessionStorage.setItem(key, mirrored); } catch (e) { /* storage blocked */ }
            return mirrored;
        },

        /// The mirrored value, but only when this page load is the continuation of a reload
        /// we caused, and that reload was recent. Null otherwise — which is what still makes
        /// closing the app drop back to the picker.
        _readSessionMirror: function (key) {
            // The injected head script consumes this key: it runs synchronously during head
            // parsing, while profiles.js is deferred, so by the time anything here looks the
            // key has been gone for the whole document parse. Reading localStorage alone made
            // this function return null every single time, which left the 1.4.6 switch-loop
            // fix dead on arrival — the three-reload backstop was doing all the work.
            //
            // The head script republishes it as a window global for this reason. localStorage
            // stays as the fallback for a page still carrying an older head script, and for
            // any caller reached before the head script has run.
            let switching = false;
            try { switching = !!window.__jpSwitching; } catch (e) { /* no window */ }
            if (!switching) {
                try {
                    switching = !!localStorage.getItem(this.config.switchingKey);
                } catch (e) { return null; }
            }
            if (!switching) return null;

            let raw = null;
            try { raw = localStorage.getItem(this.SESSION_MIRROR_PREFIX + key); } catch (e) { return null; }
            if (raw === null) return null;

            let parsed = null;
            try { parsed = JSON.parse(raw); } catch (e) { /* the older mirror: a bare string */ }

            if (!parsed || typeof parsed !== 'object' || typeof parsed.v !== 'string') {
                // Written by the Tizen-only mirror, which carried no timestamp. Upgrading
                // must not sign anyone out, so the switching flag above is all it gets.
                return raw;
            }

            if (typeof parsed.t !== 'number'
                || Date.now() - parsed.t > this.SESSION_MIRROR_MAX_AGE_MS) {
                return null;
            }
            return parsed.v;
        },

        _sessionRemove: function (key) {
            delete this._sessionMemory[key];
            try { sessionStorage.removeItem(key); } catch (e) { /* storage blocked */ }
            // Always clear the mirror: a stale copy would outlive every sign-out.
            try { localStorage.removeItem(this.SESSION_MIRROR_PREFIX + key); } catch (e) { /* ignore */ }
        },

        /// Drops every trace of an active profile session. Used by sign-out, the login
        /// route and the revert-to-master path, which must all clear both copies.
        clearProfileSession: function () {
            this._sessionRemove(this.config.activeSessionKey);
            this._sessionRemove('jellyfin_profiles_active_info');
        },

        isProfileSessionActive: function () {
            return !!this._sessionGet(this.config.activeSessionKey);
        },

        // ── Emergency disable ──────────────────────────────────────────────────────
        // An escape hatch for when the switcher itself is what is blocking the interface.
        // The administrator sets a long code in the plugin settings; entering it here shuts
        // the plugin down until the server restarts.
        //
        // Note the honest limit: this is script running inside the plugin, so it only helps
        // when the plugin is misbehaving, not when profiles.js fails to load or throws on
        // startup. In that case the answer is restarting Jellyfin or removing the plugin
        // folder, which is what the documentation says.
        //
        // Entry is a deliberate action rather than a keystroke sniffer. Watching everything
        // typed anywhere would burn the server's five-per-hour attempt budget on ordinary
        // typing — a search box would lock the real administrator out of their own escape
        // hatch.

        /// Tears the switcher down and stops every code path that could rebuild it.
        /// Setting the flag matters as much as the teardown: checkRoute runs on a 500 ms
        /// timer, so removing the overlay without it would have the gate back within half a
        /// second — failing in exactly the situation this feature exists for.
        applyPanicDisable: function (persist) {
            this._panicDisabled = true;
            this._releaseOverlayFocusTrap();

            if (persist) {
                try {
                    localStorage.setItem(this.config.panicKey, String(Date.now()));
                } catch (e) { /* private mode — the in-memory flag still holds for this page */ }
            }

            this.removeProfileOverlay();
            const bubble = document.getElementById('profiles-floating-bubble');
            if (bubble) bubble.remove();
            // The drawer link injectSidebarLink used to add. Kept in the teardown, and
            // only here: a browser that ran an older build in this tab may still have one
            // in the page, and the emergency disable has to leave nothing of ours behind.
            const sidebarLink = document.getElementById('profiles-sidebar-link');
            if (sidebarLink) sidebarLink.remove();
            const menuItem = document.getElementById('profiles-user-menu-item');
            if (menuItem) menuItem.remove();
            const prefsItem = document.getElementById('profiles-preferences-menu-item');
            if (prefsItem) prefsItem.remove();

            this.stopInactivityTimer();
            document.body.classList.remove('profiles-no-scroll');
            document.documentElement.classList.remove('profiles-no-scroll');
            document.documentElement.style.removeProperty('opacity');
            try { localStorage.removeItem(this.config.switchingKey); } catch (e) { /* ignore */ }
        },

        /// On startup, honour a disable from a previous page load, then confirm it with the
        /// server. The flag is cleared once Jellyfin has restarted and the plugin is live
        /// again — the disable is meant to last until a restart, not forever.
        checkPersistedPanic: function () {
            let marked = false;
            try {
                marked = !!localStorage.getItem(this.config.panicKey);
            } catch (e) { /* storage unavailable — nothing to honour */ }
            if (!marked) return;

            // Apply first and ask afterwards: the request may be slow, and a gate that
            // appears for two seconds before vanishing is the behaviour being escaped.
            this.applyPanicDisable(/* persist */ false);

            fetch(ApiClient.getUrl('plugins/profiles/panic-state'), { cache: 'no-store' })
                .then(res => res.ok ? res.json() : Promise.reject(new Error('unavailable')))
                .then(state => {
                    const disabled = (state.disabled !== undefined ? state.disabled : state.Disabled) === true;
                    if (disabled) return;
                    try { localStorage.removeItem(this.config.panicKey); } catch (e) { /* ignore */ }
                    this._panicDisabled = false;
                    // The teardown above released the focus trap. Coming back to life has to
                    // put it back, or issue #16 returns for the rest of this page load.
                    this._bindOverlayFocusTrap();
                    this.checkRoute();
                })
                .catch(() => {
                    // Server unreachable: stay disabled. Erring towards "off" leaves the
                    // interface usable, which is the whole point.
                });
        },

        /// Shows the emergency link only once the server has said a code exists. Unknown
        /// counts as no — the feature is off by default, and advertising an escape hatch
        /// that cannot work is worse than not offering one.
        applyPanicLinkVisibility: function () {
            const link = document.getElementById('profiles-panic-link');
            if (!link) return;
            link.style.display = this._panicLinkAvailable === true ? '' : 'none';
        },

        bindPanicShortcut: function () {
            document.addEventListener('keydown', (e) => {
                // Ctrl+Shift+B. Chosen to be unreachable by accident and not to collide
                // with Jellyfin's own shortcuts. It is the way in when the switcher has
                // failed badly enough that no overlay renders at all.
                if (e.ctrlKey && e.shiftKey && (e.key === 'B' || e.key === 'b')) {
                    e.preventDefault();
                    this.showPanicPrompt();
                }
            });
        },

        showPanicPrompt: function () {
            if (document.getElementById('profiles-panic-dialog')) return;

            const dialog = document.createElement('div');
            dialog.id = 'profiles-panic-dialog';
            dialog.classList.add('jpf-trap-surface');
            dialog.style.cssText = `
                position: fixed; top: 0; left: 0; right: 0; bottom: 0;
                background: rgba(0,0,0,0.85); backdrop-filter: blur(8px);
                display: flex; align-items: center; justify-content: center;
                z-index: ${DIALOG_Z};
            `;

            dialog.innerHTML = `
                <div style="background:#181818; border:1px solid rgba(255,255,255,0.1); border-radius: var(--jpf-r-md);
                            padding:24px; max-width:460px; width:90%; box-shadow:0 10px 30px rgba(0,0,0,0.5);">
                    <h2 style="margin:0 0 12px 0; color:#fff; font-size:1.2rem; font-weight:700;">${t('panic.emergencyDisable')}</h2>
                    <p style="color:rgba(255,255,255,0.7); font-size:0.9rem; line-height:1.5; margin:0 0 16px 0;">
                        ${t('panic.dialogBody')}
                    </p>
                    <input type="password" id="profiles-panic-input" autocomplete="off" placeholder="${t('panic.emergencyCodePlaceholder')}"
                           style="width:100%; box-sizing:border-box; padding:10px; font-size:1rem; margin-bottom:8px;" />
                    <div id="profiles-panic-error" style="display:none; color:var(--jpf-danger); font-size:0.85rem; font-weight:600; margin-bottom:8px;"></div>
                    <div style="display:flex; gap: var(--jpf-gap); justify-content:flex-end; margin-top:12px;">
                        <button id="profiles-panic-cancel" class="profiles-btn btn-secondary" style="padding:10px 20px; font-weight:600;">${t('common.cancel')}</button>
                        <button id="profiles-panic-submit" class="profiles-btn btn-danger" style="padding:10px 20px; font-weight:600;">${t('panic.disable')}</button>
                    </div>
                </div>
            `;

            document.body.appendChild(dialog);

            const input = dialog.querySelector('#profiles-panic-input');
            const errDiv = dialog.querySelector('#profiles-panic-error');
            const submitBtn = dialog.querySelector('#profiles-panic-submit');
            const close = () => dialog.remove();

            dialog.querySelector('#profiles-panic-cancel').addEventListener('click', close);

            const submit = () => {
                const code = (input.value || '').trim();
                if (!code) return;

                submitBtn.disabled = true;
                submitBtn.textContent = t('panic.checking');
                errDiv.style.display = 'none';

                fetch(ApiClient.getUrl('plugins/profiles/panic'), {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ code: code })
                })
                .then(res => {
                    if (res.ok) {
                        // Tear the switcher down here and now rather than waiting for a
                        // reload: the person doing this is stuck, and telling them to
                        // reload a page they cannot use would not help.
                        close();
                        this.applyPanicDisable(/* persist */ true);
                        this.showAlert(t('panic.disabledTitle'), t('panic.disabledBody'));
                        return;
                    }
                    if (res.status === 429) throw new Error(t('panic.tooManyAttempts'));
                    throw new Error(t('panic.incorrectCode'));
                })
                .catch(err => {
                    errDiv.textContent = err.message || t('panic.incorrectCode');
                    errDiv.style.display = 'block';
                    submitBtn.disabled = false;
                    submitBtn.textContent = t('panic.disable');
                });
            };

            submitBtn.addEventListener('click', submit);
            input.addEventListener('keydown', (e) => {
                if (e.key === 'Enter') { e.preventDefault(); submit(); }
                if (e.key === 'Escape') { e.preventDefault(); close(); }
            });

            setTimeout(() => input.focus(), 50);
        },

        // ── Switcher preferences ───────────────────────────────────────────────────
        // Two independent settings, because they answer different questions:
        //
        //   askOnStartup     — does the "Who's Watching?" screen appear when the client
        //                      loads? (Once per browser session, not per home visit.)
        //   switcherLocation — 'button' (Bonfire's floating header button) or 'menu'
        //                      (a row in Jellyfin's own user menu, no floating button).
        //
        // These were one setting in 1.3.1-beta, which could not express "ask me on startup
        // but put the switcher in Jellyfin's menu" — the combination issue #14 asked for.
        //
        // checkRoute has to decide about the gate long before a network round trip could
        // answer, so both are mirrored into localStorage and read synchronously, with the
        // server refreshing the copy in the background on every load.

        /// The cached preferences, or null when we have not learned them for this account.
        /// Null means "do not raise the gate yet" — a wrong guess that way costs a moment of
        /// home screen, whereas wrongly assuming a gate throws a full-screen overlay at
        /// somebody who deliberately turned it off, on every single load.
        getSwitcherPrefs: function () {
            if (this._switcherPrefs) return this._switcherPrefs;

            try {
                const cached = JSON.parse(localStorage.getItem(this.config.switcherModeKey) || 'null');
                if (!cached) return null;

                // The cache belongs to one account. On a shared browser the next person to
                // sign in must not inherit it, so it only counts when it matches either the
                // signed-in user or the master profile behind the active sub-profile.
                const cachedMaster = this.normalizeGuid(cached.masterUserId);
                const currentUserId = (typeof ApiClient !== 'undefined' && ApiClient)
                    ? this.normalizeGuid(ApiClient.getCurrentUserId()) : '';
                const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey) || 'null');
                const knownMaster = masterState ? this.normalizeGuid(masterState.masterUserId) : '';
                if (!cachedMaster || (cachedMaster !== currentUserId && cachedMaster !== knownMaster)) return null;

                let prefs;
                if (typeof cached.askOnStartup === 'boolean' && cached.location) {
                    prefs = { askOnStartup: cached.askOnStartup, location: cached.location };
                } else if (cached.mode) {
                    // A cache written by 1.3.1-beta. Expand it rather than discarding it,
                    // so upgrading does not flash a gate at someone who turned it off.
                    prefs = cached.mode === 'native'
                        ? { askOnStartup: false, location: 'menu' }
                        : { askOnStartup: true, location: 'button' };
                } else {
                    return null;
                }

                this._switcherPrefs = prefs;
                return prefs;
            } catch (e) { /* unreadable cache — fall through and refetch */ }

            return null;
        },

        /// True only when we know the account wants the startup prompt. Unknown reads as
        /// false so the gate is never raised on a guess.
        shouldAskOnStartup: function () {
            const p = this.getSwitcherPrefs();
            return !!p && p.askOnStartup === true;
        },

        /// True when the switcher belongs in Jellyfin's user menu rather than the floating
        /// button. Unknown reads as false, which keeps the historical button behaviour.
        isMenuLocation: function () {
            const p = this.getSwitcherPrefs();
            return !!p && p.location === 'menu';
        },

        _cacheSwitcherPrefs: function (askOnStartup, location, masterUserId) {
            this._switcherPrefs = { askOnStartup: askOnStartup, location: location };
            try {
                localStorage.setItem(this.config.switcherModeKey, JSON.stringify({
                    askOnStartup: askOnStartup,
                    location: location,
                    masterUserId: masterUserId
                }));
            } catch (e) { /* storage full or blocked — the in-memory copy still works */ }
        },

        /// Refreshes the cached preferences from the server. Called once per page load;
        /// re-runs the route check afterwards so a first-ever load on a new device settles
        /// into the right behaviour without the user having to navigate.
        loadSwitcherPrefs: function () {
            if (this._switcherPrefsLoading) return;
            if (typeof ApiClient === 'undefined' || !ApiClient || !ApiClient.accessToken()) return;

            this._switcherPrefsLoading = true;
            fetch(ApiClient.getUrl('plugins/profiles/preferences'), {
                cache: 'no-store',
                headers: this.getAuthHeaders(ApiClient.accessToken())
            })
            .then(res => res.ok ? res.json() : Promise.reject(new Error('preferences unavailable')))
            .then(prefs => {
                const askRaw = prefs.askOnStartup !== undefined ? prefs.askOnStartup : prefs.AskOnStartup;
                const locRaw = prefs.switcherLocation || prefs.SwitcherLocation;
                const master = prefs.masterUserId || prefs.MasterUserId;

                // A server older than this build answers with switcherMode only.
                const legacyNative = (prefs.switcherMode || prefs.SwitcherMode) === 'native';
                const ask = typeof askRaw === 'boolean' ? askRaw : !legacyNative;
                const location = locRaw === 'menu' || locRaw === 'button'
                    ? locRaw
                    : (legacyNative ? 'menu' : 'button');

                const emergency = (prefs.emergencyCodeConfigured !== undefined
                    ? prefs.emergencyCodeConfigured
                    : prefs.EmergencyCodeConfigured) === true;

                const before = this._switcherPrefs;
                const changed = !before || before.askOnStartup !== ask || before.location !== location;
                this._cacheSwitcherPrefs(ask, location, master);
                this._panicLinkAvailable = emergency;
                // The gate may already be on screen — reveal or hide the link in place
                // rather than waiting for the next load.
                this.applyPanicLinkVisibility();
                this._switcherPrefsLoading = false;
                // Only re-check when the answer actually moved; checkRoute runs on a timer
                // anyway and this avoids a redundant pass on every load.
                if (changed) this.checkRoute();
            })
            .catch(() => {
                this._switcherPrefsLoading = false;
                // Server unreachable or an older plugin build: fall back to the historical
                // behaviour rather than leaving the gate permanently suppressed.
                if (!this._switcherPrefs) this._switcherPrefs = { askOnStartup: true, location: 'button' };
                // Settle this too. Leaving it unknown would have the route check re-request
                // on every poll, and an emergency link we cannot vouch for stays hidden.
                if (this._panicLinkAvailable === null) this._panicLinkAvailable = false;
            });
        },

        getCachedActiveProfile: function () {
            const activeInfoStr = this._sessionGet('jellyfin_profiles_active_info');
            if (activeInfoStr) {
                try {
                    const info = JSON.parse(activeInfoStr);
                    if (info && info.initial && info.color) return info;
                } catch (e) {}
            }

            // Fallback: search in localStorage cached profiles
            const currentUserId = ApiClient.getCurrentUserId();
            if (currentUserId) {
                try {
                    const cachedListStr = localStorage.getItem('jellyfin_profiles_cached_list');
                    if (cachedListStr) {
                        const profiles = JSON.parse(cachedListStr);
                        if (Array.isArray(profiles)) {
                            const profile = profiles.find(p => this.normalizeGuid(p.profileUserId) === this.normalizeGuid(currentUserId));
                            if (profile) {
                                const info = {
                                    name: profile.profileName,
                                    color: profile.avatarColor || DEFAULT_AVATAR_COLOR,
                                    initial: profile.avatarInitial || (profile.profileName ? profile.profileName.charAt(0).toUpperCase() : 'P'),
                                    profileImage: profile.profileImage || null,
                                    transparent: !!profile.transparentAvatar
                                };
                                // Store it in sessionStorage for future fast access
                                this._sessionSet('jellyfin_profiles_active_info', JSON.stringify(info));
                                return info;
                            }
                        }
                    }
                } catch (e) {
                    console.error("ProfilesPlugin: Failed to read from profiles cache:", e);
                }
            }

            // Ultimate fallback (e.g. before any profile list has been loaded)
            const apiClient = ApiClient;
            let fallbackName = 'Profiles';
            let fallbackInitial = 'P';
            if (apiClient) {
                const user = apiClient._currentUser || apiClient.currentUser;
                if (user && user.Name) {
                    fallbackName = user.Name;
                    fallbackInitial = user.Name.charAt(0).toUpperCase();
                }
            }
            return {
                name: fallbackName,
                color: DEFAULT_AVATAR_COLOR,
                initial: fallbackInitial,
                profileImage: null
            };
        },

        validateSessionState: function () {
            const apiClient = ApiClient;
            if (!apiClient) return;

            const hash = window.location.hash || '';
            const path = window.location.pathname || '';
            if (hash.includes('login') || hash.includes('selectserver') || path.includes('login') || path.includes('selectserver')) {
                localStorage.removeItem(this.config.masterStorageKey);
                this.clearProfileSession();
                return;
            }

            const currentToken = apiClient.accessToken();
            if (!currentToken) {
                // If ApiClient token is empty, check if we are truly logged out of Jellyfin.
                // During initial page load, ApiClient.accessToken() is temporarily empty
                // while the web app initializes, but 'jellyfin_credentials' in localStorage
                // still contains the active session token.
                let isLoggedOut = true;
                try {
                    const credsStr = localStorage.getItem('jellyfin_credentials');
                    if (credsStr) {
                        const creds = JSON.parse(credsStr);
                        if (creds && Array.isArray(creds.Servers) && creds.Servers.some(s => !!s.AccessToken)) {
                            isLoggedOut = false;
                        }
                    }
                } catch (e) {}

                if (isLoggedOut) {
                    localStorage.removeItem(this.config.masterStorageKey);
                    this.clearProfileSession();
                }
                return;
            }

            // Dual-token check: if tab/app was closed, sessionStorage is wiped out.
            // If the current token in Jellyfin is NOT the master token, but sessionStorage is empty,
            // we must revert the browser to the master token and force the selection gate to display.
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            if (masterState && masterState.masterToken) {
                const currentUserId = apiClient.getCurrentUserId();
                if (this.normalizeGuid(currentUserId) === this.normalizeGuid(masterState.masterUserId)) {
                    if (currentToken !== masterState.masterToken) {
                        masterState.masterToken = currentToken;
                        localStorage.setItem(this.config.masterStorageKey, JSON.stringify(masterState));
                        console.log("ProfilesPlugin: Master session token updated to match new valid token.");
                    }
                } else if (currentToken !== masterState.masterToken && !this.isProfileSessionActive()) {
                    if (!this._revertReloadAllowed()) {
                        // Reverting has not stuck, and reverting again means reloading again,
                        // which is what turns this into the picker over and over. Accept the
                        // session actually in play and record it so every later check agrees.
                        // The cost is a profile staying signed in where it would have dropped
                        // back to the master — the safer of the two, since the master is the
                        // less restricted account.
                        console.warn(
                            'ProfilesPlugin: session revert is looping; keeping the active profile instead.');
                        this._sessionSet(this.config.activeSessionKey, currentToken);
                        return;
                    }
                    this.updateStoredCredentials(masterState.masterToken, masterState.masterUserId);
                    apiClient.setAuthenticationInfo(masterState.masterToken, masterState.masterUserId);

                    // The cached "who is active" record survives this reload, and nothing
                    // here was clearing it — so the header avatar went on naming the
                    // profile we just reverted away from while the gate, which reads the
                    // signed-in user from ApiClient, correctly showed the master. Two
                    // places disagreeing about who you are is worse than either being
                    // wrong. The active-token key is already absent (it is what put us in
                    // this branch), so this only clears the stale name and picture.
                    this.clearProfileSession();
                    // Hide current page instantly so there is no visible frame
                    // between old page unloading and new page's head script running.
                    // Held in the colour the page is already painted in, not a fixed
                    // dark one — see _captureLeavingBackground.
                    const leavingBg = this._captureLeavingBackground();
                    document.documentElement.style.cssText =
                        'opacity:0;background:' + leavingBg + ';color-scheme:'
                        + (this._isLightColour(leavingBg) ? 'light' : 'dark');
                    this._reloading = true;
                    localStorage.setItem(this.config.switchingKey, '1');
                    // Same reasoning as the switch itself (issue #22): the signed-in
                    // identity just changed back to the master, so the page on screen
                    // belonged to a profile that is no longer active.
                    this.reloadAtHome();
                }
            }
        },

        /// Whether the revert-to-master path may reload again.
        ///
        /// That path exists for "the app was closed while a profile was active": put the
        /// master token back and reload, so the picker appears. It assumes the reload fixes
        /// the condition that triggered it. If anything makes the active marker vanish on
        /// every load, the same decision is reached every time and the user is stuck in a
        /// reload loop with no way out of it from inside the app.
        ///
        /// Three in thirty seconds is not a session being restored, it is a loop. The window
        /// resets itself, so a reload an hour later starts from zero and no cleanup is owed.
        _revertReloadAllowed: function () {
            const KEY = 'jpf-revert-guard';
            const now = Date.now();

            let state = null;
            try { state = JSON.parse(localStorage.getItem(KEY) || 'null'); } catch (e) { /* unreadable */ }
            if (!state || typeof state.n !== 'number' || typeof state.t !== 'number'
                || now - state.t > 30000) {
                state = { n: 0, t: now };
            }

            state.n += 1;
            state.t = now;
            try { localStorage.setItem(KEY, JSON.stringify(state)); } catch (e) { /* full */ }

            return state.n <= 3;
        },

        handleSessionExpired: function () {
            console.warn("ProfilesPlugin: Master session expired or invalid. Redirecting to login.");
            localStorage.removeItem(this.config.masterStorageKey);
            this.clearProfileSession();
            
            const apiClient = ApiClient;
            if (apiClient) {
                if (typeof apiClient.clearUser === 'function') {
                    apiClient.clearUser();
                } else if (typeof apiClient.logout === 'function') {
                    apiClient.logout();
                }
            }
            
            window.location.hash = '#/login';
            window.location.reload();
        },

        interceptHomeAndShowProfiles: function () {
            const apiClient = ApiClient;
            if (!apiClient) return;

            const masterUserId = apiClient.getCurrentUserId();
            const masterToken = apiClient.accessToken();

            if (!masterUserId || !masterToken) return;

            let masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey)) || {};
            if (!masterState.masterToken) {
                masterState.masterToken = masterToken;
                masterState.masterUserId = masterUserId;
                localStorage.setItem(this.config.masterStorageKey, JSON.stringify(masterState));
            }

            this.fetchAndRenderProfiles(apiClient, masterUserId, masterToken);
        },

        /// Drops the prefetch cache so the next render is guaranteed to hit the server.
        /// Must be called after anything that changes profile state — otherwise the render
        /// that follows a save shows the pre-save snapshot.
        invalidateProfileCache: function () {
            this.cachedProfiles = [];
            this._profilePrefetchPending = false;
        },

        /// Renders the profile grid.
        ///
        /// `forceRefresh` bypasses the prefetch cache and is REQUIRED after any mutation
        /// (create / edit / delete). The cache exists only to make opening the switcher from
        /// the home screen flash-free; serving it after a save showed stale data — a freshly
        /// saved PIN still read as "No PIN" until the page was reloaded.
        fetchAndRenderProfiles: function (apiClient, masterUserId, masterToken, forceRefresh) {
            // Recorded, not claimed: several callers have already taken a ticket and are
            // finishing by coming back to the grid, and claiming a new one here would
            // invalidate theirs. This only needs to know whether the screen moved on while
            // the list was in flight — Settings, or a profile form, drawn over by a slow
            // grid was the same race the tickets were added for.
            const ticket = this._navTicket;

            if (forceRefresh) {
                this.invalidateProfileCache();
            } else if (this.cachedProfiles && this.cachedProfiles.length) {
                // Consume the prefetch exactly once, then drop it.
                const profiles = this.cachedProfiles;
                this.invalidateProfileCache();
                (this._i18nReady || Promise.resolve()).then(() => this.showProfileOverlay(profiles));
                return;
            }

            const url = apiClient.getUrl(`plugins/profiles/list`);

            fetch(url, {
                // Never let a conditional/heuristic cache answer this — it drives the
                // PIN and library state shown in the editor.
                cache: 'no-store',
                headers: this.getAuthHeaders(masterToken)
            })
            .then(res => {
                if (res.status === 401) {
                    this.handleSessionExpired();
                    throw new Error("Unauthorized");
                }
                return res.json();
            })
            .then(profiles => {
                if (!this.navIsCurrent(ticket)) return;
                const normalized = this.normalizeProfiles(profiles);
                // Deliberately NOT stored in cachedProfiles: that field is the one-shot
                // prefetch buffer, and repopulating it here made the *next* call short-circuit
                // to data that was already stale.
                localStorage.setItem('jellyfin_profiles_cached_list', JSON.stringify(normalized));
                return (this._i18nReady || Promise.resolve()).then(() => this.showProfileOverlay(normalized));
            })
            .catch(err => {
                console.error("Failed to load sub-profiles:", err);
                localStorage.removeItem(this.config.masterStorageKey);
            });
        },

        showProfileOverlay: function (profiles) {
            // Always stop the inactivity timer when showing the profile selector
            this.stopInactivityTimer();

            // Warm the account-wide form data now, while the user is still reading the
            // grid. Opening a profile then costs one request rather than five, which is
            // the difference between instant and the several-second wait that made
            // people click a second time.
            try {
                const warmState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
                if (warmState && warmState.masterToken) {
                    this.fetchSharedFormData(ApiClient, warmState).catch(() => {
                        // A cold prefetch failing is not an error anyone needs to see;
                        // the form will retry and report it properly if it still fails.
                    });
                }
            } catch (e) { /* no master state yet — the form will fetch on demand */ }
            this._overlayMountTime = Date.now();

            // Returning to the grid replaces the modal contents, so any document-level
            // listeners the previous form registered are now bound to detached nodes.
            this.clearManagedDocumentListeners();

            const skinHeader = document.querySelector('.skinHeader');
            if (skinHeader) skinHeader.style.display = 'none';

            // Do NOT apply filter:blur to #view-home — it triggers a GPU compositing
            // layer creation which causes a one-frame white flash on first paint.
            // The overlay's solid-dark background makes the blur redundant anyway.

            let overlay = document.getElementById('profiles-gate-overlay');
            if (!overlay) {
                overlay = document.createElement('div');
                overlay.id = 'profiles-gate-overlay';
                // Start at opacity 0 so the browser creates the compositing layer
                // silently.  We fade it in via rAF once it exists in the DOM.
                overlay.style.opacity = '0';
                document.body.appendChild(overlay);
            }

            // Disable scrolling
            document.body.classList.add('profiles-no-scroll');
            document.documentElement.classList.add('profiles-no-scroll');

            // Keep a stable reference to the profiles being shown so that
            // back-navigation (PIN cancel, master PIN cancel, promptMasterPinEntry)
            // can re-show the grid without depending on cachedProfiles, which is
            // intentionally cleared after first use.
            this.currentProfiles = profiles;
            this.renderOverlayContent(overlay, profiles);

            // Reveal the page NOW — the overlay covers the home screen so there
            // is no blank-page flash.  checkRoute() skipped _revealPage() earlier
            // specifically so we could do it here at the right moment.
            this._viewShowFired = true; // overlay is rendered; treat this as view-ready
            this._revealPage();

            // Two rAF calls: first lets the browser paint with opacity:0 (compositing
            // layer created silently), second begins the CSS opacity transition.
            requestAnimationFrame(() => requestAnimationFrame(() => {
                overlay.style.opacity = ''; // CSS transition takes over
            }));

            // Auto-focus the first interactive element so TV/keyboard users
            // don't need to Tab before they can navigate the profile grid.
            setTimeout(() => {
                const first = overlay.querySelector('[tabindex="0"], button, input');
                if (first) first.focus();
            }, 100);
        },

        /// Builds the per-library artwork controls in the edit form and returns a handle
        /// whose save() posts whatever changed.
        ///
        /// Choices are held in memory until the form is saved, so backing out leaves
        /// nothing behind — the same contract as every other field on the form.
        /// Greys the artwork controls on a library this profile cannot see.
        ///
        /// Now that the tick and the artwork sit on one row, an enabled Picture
        /// dropdown next to an unticked library would be offering to style something
        /// that is not there.
        syncLibraryRowState: function (row) {
            if (!row) return;
            const box = row.querySelector('.library-checkbox');
            const on = !box || box.checked;

            row.querySelectorAll('.libart-mode, .libart-choose').forEach(el => {
                el.disabled = !on;
            });
            row.style.opacity = on ? '' : '0.5';
        },

        /// Applies the above to every row in a container.
        syncAllLibraryRows: function (root) {
            const scope = root || document;
            scope.querySelectorAll('.libart-row').forEach(r => this.syncLibraryRowState(r));
        },

        initLibraryArtworkEditor: function (container, profileId) {
            const rows = Array.prototype.slice.call(container.querySelectorAll(".libart-row"));
            const list = container.querySelector(".libart-list");
            const artToggle = container.querySelector(".libart-toggle input");
            const artHint = container.querySelector(".libart-explainer");

            // Off is the honest default: most profiles never touch artwork, and the
            // controls meant nothing to anyone who was only picking libraries.
            const showArtwork = (on) => {
                if (list) list.classList.toggle("show-artwork", on);
                if (artHint) artHint.style.display = on ? "" : "none";
            };
            if (artToggle) {
                artToggle.addEventListener("change", (e) => showArtwork(e.target.checked));
            }
            showArtwork(false);
            const state = {};
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey) || "null");

            // A value safe to drop into a CSS url(): a data payload or our own endpoint.
            // Anything else previews as empty rather than as a broken rule.
            const previewUrl = (entry) => {
                if (entry.image && /^data:image\/(png|jpeg|jpg|gif|webp);base64,[A-Za-z0-9+/=]+$/i.test(entry.image)) {
                    return entry.image;
                }
                return entry.url ? this._libraryArtUrl(entry.url) : "";
            };

            const paint = (row) => {
                const id = row.getAttribute("data-lib");
                const entry = state[id] || { mode: "inherit" };
                const select = row.querySelector(".libart-mode");
                const choose = row.querySelector(".libart-choose");
                const thumb = row.querySelector(".libart-thumb");

                select.value = entry.mode;
                choose.style.visibility = entry.mode === "custom" ? "visible" : "hidden";
                // Class, not an inline style: the artwork switch hides these by rule, and an
                // inline display would outrank it and put the placeholder back.
                row.classList.toggle("libart-has-art", entry.mode !== "inherit");

                const preview = entry.mode === "custom" ? previewUrl(entry) : "";
                thumb.style.backgroundImage = preview ? ("url(\"" + preview + "\")") : "";
                thumb.textContent = (!preview && entry.mode === "none") ? "\u2014" : "";
            };

            rows.forEach(row => {
                const id = row.getAttribute("data-lib");
                state[id] = { mode: "inherit" };

                row.querySelector(".libart-mode").addEventListener("change", (e) => {
                    state[id].mode = e.target.value;
                    state[id].dirty = true;
                    paint(row);
                    // Choosing Picture with nothing picked yet opens the picker, rather
                    // than leaving a mode the server would refuse to save.
                    if (state[id].mode === "custom" && !state[id].image && !state[id].url) {
                        row.querySelector(".libart-choose").click();
                    }
                });

                row.querySelector(".libart-choose").addEventListener("click", () => {
                    this.pickLibraryArtwork((picked) => {
                        state[id].mode = "custom";
                        state[id].image = picked.image;
                        state[id].thumb = picked.thumb;
                        state[id].avatarLibraryId = picked.libraryId;
                        state[id].url = null;
                        state[id].dirty = true;
                        paint(row);
                    });
                });

                paint(row);
            });

            // Existing choices arrive after the form is drawn; the rows work meanwhile.
            if (masterState && masterState.masterToken && rows.length) {
                fetch(ApiClient.getUrl("plugins/profiles/library-artwork/" + profileId), {
                    cache: "no-store",
                    headers: this.getAuthHeaders(masterState.masterToken)
                })
                .then(res => res.ok ? res.json() : Promise.reject(new Error("unavailable")))
                .then(entries => {
                    (entries || []).forEach(entry => {
                        const wanted = this.normalizeGuid(entry.libraryId || entry.LibraryId);
                        const row = rows.find(r => this.normalizeGuid(r.getAttribute("data-lib")) === wanted);
                        if (!row) return;
                        const key = row.getAttribute("data-lib");
                        // Anything already changed by hand outranks what the server had.
                        if (state[key].dirty) return;
                        state[key] = {
                            mode: String(entry.mode || entry.Mode || "inherit").toLowerCase(),
                            url: entry.url || entry.Url || null
                        };
                        paint(row);
                    });

                    // A profile that already has artwork set should not have to find the
                    // switch to see it. Only ever turns the section on, so it cannot fold
                    // away controls somebody has just opened by hand.
                    const anyArt = rows.some(r => (state[r.getAttribute("data-lib")] || {}).mode !== "inherit");
                    if (anyArt && artToggle && !artToggle.checked) {
                        artToggle.checked = true;
                        showArtwork(true);
                    }
                })
                .catch(() => { /* nothing stored yet, or a server older than this build */ });
            }

            return {
                save: (targetProfileId) => {
                    const changed = rows.filter(r => state[r.getAttribute("data-lib")].dirty);
                    if (!changed.length) return Promise.resolve();

                    // One at a time: each request can carry an image and the server writes
                    // files, so firing them together buys nothing and risks a partial save.
                    return changed.reduce((chain, row) => chain.then(() => {
                        const id = row.getAttribute("data-lib");
                        const entry = state[id];
                        return fetch(ApiClient.getUrl("plugins/profiles/library-artwork"), {
                            method: "POST",
                            headers: {
                                "Content-Type": "application/json",
                                ...this.getAuthHeaders(masterState ? masterState.masterToken : ApiClient.accessToken())
                            },
                            body: JSON.stringify({
                                profileId: targetProfileId,
                                libraryId: id,
                                mode: entry.mode,
                                image: entry.image || null,
                                thumb: entry.thumb || null,
                                avatarLibraryId: entry.avatarLibraryId || null,
                                masterPin: this.masterPin
                            })
                        }).then(res => {
                            if (!res.ok) return res.text().then(body => { throw new Error(body || t('errors.couldNotSaveArtwork')); });
                            entry.dirty = false;
                        });
                    }), Promise.resolve());
                }
            };
        },

        /// Opens the picker and crop editor the profile pictures already use, so library
        /// artwork gets the avatar library, uploads and positioning without a second
        /// implementation of any of it.
        pickLibraryArtwork: function (onPicked) {
            const apiClient = ApiClient;
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey) || "null");
            const token = masterState ? masterState.masterToken : apiClient.accessToken();

            this.fetchAvatarLibrary(apiClient, token).then(library => {
                if (document.getElementById("profiles-libart-dialog")) return;

                const dialog = document.createElement("div");
                dialog.id = "profiles-libart-dialog";
                dialog.classList.add("jpf-trap-surface");
                dialog.style.cssText =
                    "position:fixed; top:0; left:0; right:0; bottom:0;" +
                    "background:rgba(0,0,0,0.85); backdrop-filter:blur(8px);" +
                    "display:flex; align-items:center; justify-content:center; z-index:" + DIALOG_Z + ";";
                dialog.innerHTML =
                    '<div style="background:#181818; border:1px solid rgba(255,255,255,0.1); border-radius: var(--jpf-r-md);' +
                    ' padding:22px; max-width:420px; width:94%; max-height:86vh; overflow:auto;">' +
                    '<h2 style="margin:0 0 14px 0; color:#fff; font-size:1.15rem; font-weight:700;">' + t('avatar.libraryArtworkTitle') + '</h2>' +
                    '<div id="profiles-libart-host"></div>' +
                    '<div id="profiles-libart-error" style="display:none; color:var(--jpf-danger); font-size:0.85rem;' +
                    ' font-weight:600; margin-top:8px;"></div>' +
                    '<div style="display:flex; gap: var(--jpf-gap); justify-content:flex-end; margin-top:16px;">' +
                    '<button id="profiles-libart-cancel" class="profiles-btn btn-secondary" style="padding:10px 20px; font-weight:600;">' + t('common.cancel') + '</button>' +
                    '<button id="profiles-libart-save" class="profiles-btn btn-primary" style="padding:10px 20px; font-weight:600;">' + t('avatar.usePicture') + '</button>' +
                    '</div></div>';
                document.body.appendChild(dialog);

                const host = dialog.querySelector("#profiles-libart-host");
                host.innerHTML = this.renderAvatarPicker("libart", library, null, DEFAULT_AVATAR_COLOR);
                const picker = this.initAvatarPicker(host, "libart", library, null, () => {});

                const close = () => dialog.remove();
                dialog.querySelector("#profiles-libart-cancel").addEventListener("click", close);
                dialog.querySelector("#profiles-libart-save").addEventListener("click", () => {
                    const picked = picker.get();
                    if (!picked.image && !picked.libraryId) {
                        const err = dialog.querySelector("#profiles-libart-error");
                        err.textContent = t('avatar.chooseFirst');
                        err.style.display = "block";
                        return;
                    }
                    close();
                    onPicked(picked);
                });

                setTimeout(() => {
                    const first = dialog.querySelector("button, input");
                    if (first) first.focus();
                }, 50);
            });
        },

        // ─── Library tile artwork (issue #19) ──────────────────────────────────────
        //
        // Jellyfin builds one image per library and caches it on the folder, from a query
        // with no user attached, so the tile for a mixed library can be drawn from something
        // the active profile is not allowed to open. There is no per-user image to ask the
        // server for, so the substitution happens here.
        //
        // It is done with a stylesheet rule per library rather than by touching the cards.
        // jellyfin-web renders `.card[data-id]` containing a `.cardImageContainer` and fills
        // its background in later from `data-src`, as a plain inline style. A stylesheet rule
        // marked !important outranks a non-important inline one, so ours wins without having
        // to race the lazy loader, survives every re-render with no observer, and applies
        // anywhere a card for that library appears.
        //
        // Honest limit: jellyfin-web preloads the original through `new Image()` before it
        // sets the style, so the file is still fetched even though it is never displayed.
        // Stopping that would mean rewriting `data-src` before the loader reads it, which is
        // the fragile DOM race this approach exists to avoid. Nobody sees the picture; it is
        // not a claim that the bytes never reach the device.

        LIBRARY_ART_STYLE_ID: 'profiles-library-art',

        /// Accepts only the shape our own endpoint produces. The value is interpolated into
        /// a stylesheet, where escapeHtml would be the wrong tool and a stray quote or
        /// parenthesis would end the rule early.
        _libraryArtUrl: function (value) {
            if (!value) return '';
            const raw = String(value).trim();
            if (!/^\/plugins\/profiles\/library-art\/[0-9a-f-]{36}\/[0-9a-f-]{36}(\?v=\d+)?$/i.test(raw)) return '';
            return pluginUrl(raw);
        },

        /// Writes the rules for `entries` into a single style element, replacing whatever was
        /// there. Entries are `{ libraryId, mode, url }`; anything unrecognised is skipped.
        applyLibraryArtwork: function (entries) {
            let css = '';

            (entries || []).forEach(entry => {
                // Jellyfin serialises GUIDs without dashes, and that is the form it puts
                // in data-id, so the selector has to use the same one. Both spellings are
                // accepted coming in; only hex goes out.
                const id = String(entry.libraryId || entry.LibraryId || '').replace(/-/g, '').toLowerCase();
                if (!/^[0-9a-f]{32}$/.test(id)) return;

                const mode = String(entry.mode || entry.Mode || '').toLowerCase();
                const selector = '.card[data-id="' + id + '"] .cardImageContainer';

                if (mode === 'custom') {
                    const url = this._libraryArtUrl(entry.url || entry.Url);
                    if (!url) return;
                    css += selector + '{background-image:url("' + url + '")!important;' +
                           'background-size:cover!important;background-position:center!important;}';
                } else if (mode === 'none') {
                    // Reveals the card padder underneath, which is the icon and name — the
                    // same thing a library with no artwork shows.
                    css += selector + '{background-image:none!important;}';
                }
            });

            let style = document.getElementById(this.LIBRARY_ART_STYLE_ID);
            if (!css) {
                if (style) style.remove();
                return;
            }
            if (!style) {
                style = document.createElement('style');
                style.id = this.LIBRARY_ART_STYLE_ID;
                document.head.appendChild(style);
            }
            style.textContent = css;
        },

        /// Remembers the rules against the account they belong to, so the next load can apply
        /// them before any request comes back. Without this every page load would show the
        /// real artwork until the fetch resolved, which is the whole thing being avoided.
        cacheLibraryArtwork: function (userId, entries) {
            try {
                localStorage.setItem(this.config.libraryArtKey, JSON.stringify({
                    userId: this.normalizeGuid(userId),
                    entries: entries || []
                }));
            } catch (e) { /* storage full or blocked — the fetch below still applies them */ }
        },

        /// Applies the cached rules if they belong to whoever is signed in now.
        applyCachedLibraryArtwork: function () {
            try {
                const cached = JSON.parse(localStorage.getItem(this.config.libraryArtKey) || 'null');
                if (!cached || !cached.entries) return;

                const current = (typeof ApiClient !== 'undefined' && ApiClient)
                    ? this.normalizeGuid(ApiClient.getCurrentUserId()) : '';
                // A cache belonging to somebody else is worse than none: it would paint one
                // profile's choices over another's libraries.
                if (!current || this.normalizeGuid(cached.userId) !== current) return;

                this.applyLibraryArtwork(cached.entries);
            } catch (e) { /* unreadable cache — the fetch will rebuild it */ }
        },

        /// Refreshes the rules from the server for whoever is signed in.
        loadLibraryArtwork: function () {
            if (typeof ApiClient === 'undefined' || !ApiClient || !ApiClient.accessToken()) return;
            const userId = ApiClient.getCurrentUserId();
            if (!userId) return;

            fetch(ApiClient.getUrl('plugins/profiles/library-artwork'), {
                cache: 'no-store',
                headers: this.getAuthHeaders(ApiClient.accessToken())
            })
            .then(res => res.ok ? res.json() : Promise.reject(new Error('unavailable')))
            .then(entries => {
                const list = Array.isArray(entries) ? entries : [];
                this._libraryArtLoaded = true;
                this.cacheLibraryArtwork(userId, list);
                this.applyLibraryArtwork(list);
            })
            .catch(() => {
                // Server unreachable, or a server older than this build. Leave whatever the
                // cache already applied rather than dropping back to Jellyfin's artwork.
            });
        },

        // ─── D-pad focus trap ─────────────────────────────────────────────────────
        // Issue #16: on a television our screens cover the page, but Jellyfin's own
        // navigation is still listening on document and still sees every element behind
        // them. Pressing a direction moved focus into the home screen underneath, leaving
        // the remote controlling a page nobody could see.
        //
        // `inert` would be the tidy answer and is not an option — the TV browsers this
        // has to work on predate it. So directions are taken at the capture phase, before
        // Jellyfin's handlers run, and resolved against our own elements.
        //
        // The listeners are bound once and stay bound. They resolve the active surface on
        // every event and return immediately when there is none, so the trap covers every
        // Bonfire screen without anyone having to remember to arm it, and stops applying
        // the instant the last one leaves the DOM. Lifecycle bookkeeping is what leaks.

        /// Selector for the surfaces we trap focus inside: the gate overlay and any of our
        /// own dialogs (confirm, alert, crop, panic), all of which sit above the page.
        /// Every dialog gets this class as it is built. It replaced
        /// `[id^="profiles-"][id$="-dialog"]`, which is two unqualified attribute
        /// selectors: the engine cannot use an index for either, so resolving them meant
        /// walking every element in the document — and this runs on every keydown and
        /// focusin *in the whole application*, so typing in Jellyfin's own search box
        /// walked the page once per keystroke.
        ///
        /// A class keeps the property that made the id pattern attractive: it travels with
        /// the element, so removing the dialog removes it from the set with no bookkeeping
        /// to get wrong. See the note above about lifecycle bookkeeping being what leaks.
        TRAP_SURFACE_CLASS: 'jpf-trap-surface',

        TRAP_SURFACE_SELECTOR: '#profiles-gate-overlay, .jpf-trap-surface',

        /// The surface the remote should currently be confined to, or null when Bonfire
        /// has nothing on screen. Dialogs render above the gate, and the last one in the
        /// DOM is the topmost, so document order picks the right one.
        _activeTrapSurface: function () {
            // Class first, id second. Dialogs render above the gate, so the last dialog in
            // document order is the topmost surface; with none open, the gate is. On the
            // far more common path — nothing of ours on screen — this is one class-index
            // lookup that comes back empty, where it used to be a full document walk on
            // every keystroke anywhere in the application.
            //
            // The answer is identical to the querySelectorAll version it replaces,
            // including the case that looks like an oversight: a topmost surface that has
            // already detached disarms the trap rather than falling back to the gate
            // underneath. That is existing behaviour with a test on it, and this change is
            // about how the surface is found, not about what counts as one.
            const dialogs = document.getElementsByClassName(this.TRAP_SURFACE_CLASS);
            const last = dialogs.length
                ? dialogs[dialogs.length - 1]
                : document.getElementById('profiles-gate-overlay');
            if (!last) return null;

            // A dialog mid-fade still counts; one already detached does not.
            return last.isConnected === false ? null : last;
        },

        /// Everything inside a surface a remote can land on. Hidden elements are left out:
        /// the overlay swaps between a grid and a form, and the old one lingers.
        ///
        /// The offsetParent read below was flagged as forcing layout once per element
        /// inside a focus handler. It does not: layout is invalidated by writes, and this
        /// filter only reads, so the whole loop costs one layout however many controls a
        /// surface has. Spatial navigation needs geometry anyway.
        ///
        /// `checkVisibility()` is the tidy replacement and is **not an option here**. It
        /// needs Chrome 105; the TVs this has to work on are Chrome 68 (webOS 5) and
        /// Chrome 76 (Tizen 6.0), so it would throw on exactly the devices the focus trap
        /// exists for — and a remote is the one input with no way to escape a broken trap.
        _overlayFocusables: function (root) {
            return Array.prototype.filter.call(
                root.querySelectorAll('button, input, select, textarea, a[href], [tabindex]:not([tabindex="-1"])'),
                el => !el.disabled && el.offsetParent !== null
            );
        },

        /// True for a control that binds the arrow keys to something other than moving
        /// between controls — the crop view pans the picture with them. Those keys are left
        /// alone there; the trap still keeps focus from escaping the surface.
        _ownsArrowKeys: function (node) {
            if (!node) return false;
            if (node.closest) return !!node.closest('[data-profiles-own-keys="1"]');
            return node.getAttribute && node.getAttribute('data-profiles-own-keys') === '1';
        },

        /// Steps one screen back from `surface`, or absorbs the press when there is nowhere
        /// to go. Every sub-screen carries a Back or Cancel control already, so this clicks
        /// the one it finds rather than duplicating each screen's teardown.
        ///
        /// Doing nothing is the right answer on the picker itself: it is a required choice,
        /// and on a TV the alternative is closing the whole app mid-selection.
        _dismissSurface: function (surface) {
            const back = surface.querySelector(
                '#profiles-crop-cancel, #profiles-panic-cancel, #dialog-cancel-btn, #dialog-close-btn, ' +
                '#pin-cancel-btn, #master-pin-cancel-btn, #create-cancel-btn, #edit-cancel-btn, ' +
                '#bonfire-back-btn, #switcher-mode-back-btn, #settings-back-btn, #profiles-resume-btn, #profiles-libart-cancel'
            );
            if (back) back.click();
        },

        /// Moves focus one step through the surface in document order, wrapping at both
        /// ends. This is what Tab means; geometry is for the direction keys.
        _stepOverlayFocus: function (root, delta) {
            const items = this._overlayFocusables(root);
            if (!items.length) return;
            const at = items.indexOf(document.activeElement);
            const next = at < 0
                ? (delta > 0 ? 0 : items.length - 1)
                : (at + delta + items.length) % items.length;
            this._focusVisibly(items[next]);
        },

        /// Focuses a control and brings it into view.
        ///
        /// The profile forms are taller than a television screen, so Save and Cancel sit
        /// below the fold. Focus alone moved to them without scrolling on the TV clients,
        /// which reads as the remote having stopped responding.
        _focusVisibly: function (el) {
            if (!el) return;
            el.focus();
            try {
                const r = el.getBoundingClientRect();
                const h = window.innerHeight || document.documentElement.clientHeight;
                // Only scroll when it is actually out of the viewport — an unconditional
                // scrollIntoView jerks the picker around on every arrow press.
                if (r.top < 0 || r.bottom > h) {
                    el.scrollIntoView({ block: 'nearest', inline: 'nearest' });
                }
            } catch (e) { /* no layout information — focus alone will do */ }
        },

        /// Nearest focusable in a direction. Movement across the axis is penalised so a
        /// grid walks along its row instead of cutting diagonally to a closer card.
        _moveOverlayFocus: function (root, dir) {
            const items = this._overlayFocusables(root);
            if (!items.length) return;

            const current = (document.activeElement && root.contains(document.activeElement))
                ? document.activeElement : null;
            if (!current) { this._focusVisibly(items[0]); return; }

            const from = current.getBoundingClientRect();
            const fx = from.left + from.width / 2;
            const fy = from.top + from.height / 2;
            const horizontal = (dir === 'left' || dir === 'right');

            let best = null, bestScore = Infinity;
            items.forEach(el => {
                if (el === current) return;
                const r = el.getBoundingClientRect();
                const dx = (r.left + r.width / 2) - fx;
                const dy = (r.top + r.height / 2) - fy;
                if (dir === 'left'  && dx > -1) return;
                if (dir === 'right' && dx <  1) return;
                if (dir === 'up'    && dy > -1) return;
                if (dir === 'down'  && dy <  1) return;

                const along  = horizontal ? Math.abs(dx) : Math.abs(dy);
                const across = horizontal ? Math.abs(dy) : Math.abs(dx);
                const score = along + across * 3;
                if (score < bestScore) { bestScore = score; best = el; }
            });

            if (best) this._focusVisibly(best);
        },

        _bindOverlayFocusTrap: function () {
            if (this._overlayTrap) return;

            // e.key is missing on some older TV browsers, so keyCode is the fallback.
            const dirOf = (e) => {
                switch (e.key) {
                    case 'ArrowLeft':  case 'Left':  return 'left';
                    case 'ArrowRight': case 'Right': return 'right';
                    case 'ArrowUp':    case 'Up':    return 'up';
                    case 'ArrowDown':  case 'Down':  return 'down';
                }
                switch (e.keyCode) {
                    case 37: return 'left';
                    case 38: return 'up';
                    case 39: return 'right';
                    case 40: return 'down';
                }
                return null;
            };

            const onKeyDown = (e) => {
                const surface = this._activeTrapSurface();
                if (!surface) return;
                // Never swallow a shortcut — Ctrl+Shift+B has to keep working from here.
                if (e.ctrlKey || e.altKey || e.metaKey) return;

                // The TV Back/Return key. Samsung sends 10009, LG sends 461, and neither is
                // a key the page gets a second chance at: unhandled, Tizen puts up its
                // "exit application?" prompt behind our overlay, where it cannot be read or
                // dismissed. While a Bonfire screen is up, Back belongs to that screen.
                if (e.keyCode === 10009 || e.keyCode === 461 || e.key === 'XF86Back') {
                    e.preventDefault();
                    e.stopPropagation();
                    this._dismissSurface(surface);
                    return;
                }

                // An event dispatched at `window` has no element target, and contains()
                // wants a node. Treat it as outside: the direction keys still move, and
                // _moveOverlayFocus enters at the first control when the active element is
                // not in the surface, which is the right first press.
                const inside = !!(e.target && e.target.nodeType && surface.contains(e.target));
                const dir = dirOf(e);

                if (dir) {
                    // The crop view pans with the arrows. Ownership is declared on that
                    // element rather than the whole dialog, so the buttons beside it stay
                    // reachable — a remote has no Tab key to escape with otherwise.
                    if (inside && this._ownsArrowKeys(e.target)) return;

                    const tag = (e.target.tagName || '').toLowerCase();
                    if (inside) {
                        // Left/right belong to the caret while typing a PIN or a name.
                        if ((tag === 'input' || tag === 'textarea') && (dir === 'left' || dir === 'right')) return;
                        // Up/down change the value of a dropdown, which is what a remote
                        // expects. Left/right stay ours, so there is still a way off it.
                        if (tag === 'select' && (dir === 'up' || dir === 'down')) return;
                    }
                    e.preventDefault();
                    e.stopPropagation();
                    this._moveOverlayFocus(surface, dir);
                    return;
                }

                if (e.key === 'Tab' || e.keyCode === 9) {
                    // Tab follows document order, not geometry. Routing it through the
                    // spatial search meant it did nothing at all in a stacked form, where
                    // every candidate is directly above or below rather than beside.
                    e.preventDefault();
                    e.stopPropagation();
                    this._stepOverlayFocus(surface, e.shiftKey ? -1 : 1);
                    return;
                }

                // A select landing on the page behind would act on something invisible.
                // Inside the surface it is left alone: that is how our own buttons fire.
                const isSelect = e.key === 'Enter' || e.key === ' ' || e.keyCode === 13;
                if (isSelect && !inside) {
                    e.preventDefault();
                    e.stopPropagation();
                    const items = this._overlayFocusables(surface);
                    if (items.length) items[0].focus();
                }
            };

            // Second line of defence: whatever moved focus out, take it back. Membership is
            // tested against any surface rather than the topmost one, so focus returning to
            // the gate under a dialog that is still fading out is left where it belongs.
            const onFocusIn = (e) => {
                const surface = this._activeTrapSurface();
                if (!surface) return;
                if (e.target && e.target.closest && e.target.closest(this.TRAP_SURFACE_SELECTOR)) return;
                if (e.target === document.body || e.target === document.documentElement) return;
                const items = this._overlayFocusables(surface);
                if (items.length) items[0].focus();
            };

            // On `window`, and in the capture phase. Both halves of that matter, and the
            // second time #16 was reported it was because of the first.
            //
            // jellyfin-web binds its own remote navigation with
            // `window.addEventListener('keydown', ...)` — no capture — in
            // scripts/keyboardNavigation.js, and it bails on `e.defaultPrevented`. So for
            // any key event that travels through the document we are ahead of it either
            // way. What document-level capture does NOT see is an event dispatched at
            // `window` itself: its propagation path is `window` alone, so a document
            // listener is never on it while upstream's window listener is. Television
            // webviews do dispatch remote keys that way.
            //
            // When that happens the arrows belong to upstream's focusManager, and its
            // focusable set is INPUT/TEXTAREA/SELECT/BUTTON/A plus `.focusable` — it does
            // not look at [tabindex] at all. A profile card is a div with tabindex="0", so
            // the grid is invisible to it and our two footer buttons are not: every arrow
            // press lands on Manage Profiles and no profile can be reached. That is
            // exactly how it was reported. The cards carry `focusable` now as well, so
            // both ends of this are covered.
            //
            // Window capture is first on the path for everything else too, so this is a
            // superset of what it replaces, not a trade.
            window.addEventListener('keydown', onKeyDown, true);
            window.addEventListener('focusin', onFocusIn, true);
            this._overlayTrap = { onKeyDown: onKeyDown, onFocusIn: onFocusIn };
        },

        _releaseOverlayFocusTrap: function () {
            if (!this._overlayTrap) return;
            window.removeEventListener('keydown', this._overlayTrap.onKeyDown, true);
            window.removeEventListener('focusin', this._overlayTrap.onFocusIn, true);
            this._overlayTrap = null;
        },

        removeProfileOverlay: function () {
            const overlay = document.getElementById('profiles-gate-overlay');
            if (overlay) overlay.remove();

            // Reopening the gate should re-read the server; caching only spans one
            // session so a library added elsewhere shows up next time.
            this.clearSharedFormData();

            // Tearing down the overlay must also drop the form listeners it owned.
            this.clearManagedDocumentListeners();

            // Re-enable scrolling
            document.body.classList.remove('profiles-no-scroll');
            document.documentElement.classList.remove('profiles-no-scroll');

            const skinHeader = document.querySelector('.skinHeader');
            if (skinHeader) skinHeader.style.display = '';

            // Note: view-home blur no longer applied (removed in v1.0.14)
        },

        // ─── Inactivity Lockout Timer ─────────────────────────────────────────────

        // Called on page load when an active profile session already exists.
        // Fetches /list (using the master token) to find the active profile's
        // lockout setting, then arms the inactivity timer.
        initLockoutTimer: function () {
            if (!this.isProfileSessionActive()) return;

            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            if (!masterState || !masterState.masterToken) return;

            const apiClient = ApiClient;
            if (!apiClient) return;

            const currentUserId = typeof apiClient.getCurrentUserId === 'function'
                ? apiClient.getCurrentUserId() : null;
            if (!currentUserId) return;

            const url = apiClient.getUrl('plugins/profiles/list');
            fetch(url, { headers: this.getAuthHeaders(masterState.masterToken) })
            .then(res => { if (!res.ok) throw new Error('fail'); return res.json(); })
            .then(profiles => {
                const active = (profiles || []).find(p => {
                    const id = p.profileUserId || p.ProfileUserId;
                    return this.normalizeGuid(id) === this.normalizeGuid(currentUserId);
                });
                if (!active) return;
                const requiresPin = active.requiresPin !== undefined ? active.requiresPin : active.RequiresPin;
                if (!requiresPin) return; // No PIN = no lockout
                const minutes = active.lockoutMinutes !== undefined ? active.lockoutMinutes
                    : (active.LockoutMinutes !== undefined ? active.LockoutMinutes : 5);
                if (minutes > 0) this.startInactivityTimer(minutes);
            })
            .catch(() => { /* silent — lockout timer is best-effort */ });
        },

        isMediaPlaying: function () {
            const mediaElements = document.querySelectorAll('video, audio');
            for (let i = 0; i < mediaElements.length; i++) {
                const media = mediaElements[i];
                if (media && !media.paused && !media.ended && media.currentTime > 0) {
                    return true;
                }
            }
            return false;
        },

        // Arms the inactivity timer. Resets on any user interaction.
        // Any device event (mouse, keyboard, touch, pointer, scroll) counts as activity,
        // making this safe for TV remotes, magic remotes, game pads, and touchscreens.
        startInactivityTimer: function (minutes) {
            this.stopInactivityTimer();
            const ms = minutes * 60 * 1000;
            const events = [
                'mousemove', 'mousedown', 'keydown',
                'touchstart', 'scroll', 'wheel', 'click',
                'pointermove', 'pointerdown'  // covers LG Magic Remote and pointer-based TV inputs
            ];

            const checkAndLock = () => {
                if (this.isMediaPlaying()) {
                    // Defer lockout by 1 minute if media is actively playing
                    this.inactivityTimer = setTimeout(checkAndLock, 60 * 1000);
                } else {
                    this.lockActiveProfile();
                }
            };

            const resetTimer = () => {
                clearTimeout(this.inactivityTimer);
                this.inactivityTimer = setTimeout(checkAndLock, ms);
            };

            events.forEach(ev => document.addEventListener(ev, resetTimer, { passive: true }));
            this.inactivityEventHandlers = { resetTimer, events };
            resetTimer(); // Arm immediately
        },

        stopInactivityTimer: function () {
            clearTimeout(this.inactivityTimer);
            this.inactivityTimer = null;
            if (this.inactivityEventHandlers) {
                const { resetTimer, events } = this.inactivityEventHandlers;
                events.forEach(ev => document.removeEventListener(ev, resetTimer));
                this.inactivityEventHandlers = null;
            }
        },

        // Called when the inactivity timer fires. Clears the active session,
        // restores master credentials, then shows the profile selector.
        lockActiveProfile: function () {
            this.stopInactivityTimer();
            this.clearProfileSession();
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            if (masterState) {
                this.updateStoredCredentials(masterState.masterToken, masterState.masterUserId);
                ApiClient.setAuthenticationInfo(masterState.masterToken, masterState.masterUserId);
            }
            this.interceptHomeAndShowProfiles();
        },

        renderOverlayContent: function (overlay, profiles) {
            // Claims the screen so a form still loading in the background does not
            // draw itself over the grid when it finally returns.
            this.beginNavigation();

            const title = this.isManageMode ? t('gate.manageProfiles') : t('gate.whosWatching');
            const manageBtnText = this.isManageMode ? t('common.done') : t('gate.manageProfiles');

            const masterProfile = profiles.find(p => p.isMaster && !p.isBonfire);
            const maxSubProfiles = masterProfile ? masterProfile.maxSubProfiles : 5;
            const subProfileCount = profiles.filter(p => !p.isMaster && !p.isBonfire).length;
            const atLimit = subProfileCount >= maxSubProfiles;

            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            const localMasterId = masterState ? this.normalizeGuid(masterState.masterUserId) : '';

            // Same condition the two settings tiles used, so Settings shows up in exactly
            // the cases they used to.
            const hasLocalMaster = profiles.some(p => p.isMaster && !p.isBonfire);

            // Group profiles by masterUserId
            const grouped = {};
            for (const p of profiles) {
                const key = this.normalizeGuid(p.masterUserId || (p.isBonfire ? '' : localMasterId));
                if (!grouped[key]) {
                    grouped[key] = [];
                }
                grouped[key].push(p);
            }

            // Order groups so local home is first, then rest alphabetical by master user name
            const groupKeys = Object.keys(grouped);
            groupKeys.sort((a, b) => {
                if (a === localMasterId) return -1;
                if (b === localMasterId) return 1;
                const masterA = grouped[a].find(p => p.isMaster);
                const masterB = grouped[b].find(p => p.isMaster);
                const nameA = masterA ? masterA.profileName.toLowerCase() : '';
                const nameB = masterB ? masterB.profileName.toLowerCase() : '';
                return nameA.localeCompare(nameB);
            });

            // The signed-in Jellyfin user IS the active profile — switching signs in as
            // that profile's own account. The gate never said which one that was, and the
            // avatar-colour rings made it look as though it did.
            //
            // But ApiClient is not the authority when the picker was opened from the header
            // bubble. handleBubbleClick puts the *master's* credentials back before listing,
            // because a sub-profile's token cannot see its siblings — so by the time this
            // renders, getCurrentUserId() is the master on every platform, and the badge
            // sat on the master's card however long you had been using a profile. Reported
            // as "the switcher says I am signed in as my main profile"; it is this, not a
            // switch that failed. _resumeState is the profile that was active a moment ago
            // and is what the badge means, so it wins when it is set.
            const resumed = this._resumeState && !this._resumeState.closeOnly
                ? this._resumeState.userId
                : null;
            const signedInId = resumed
                ? this.normalizeGuid(resumed)
                : ((typeof ApiClient.getCurrentUserId === 'function')
                    ? this.normalizeGuid(ApiClient.getCurrentUserId())
                    : '');

            // `focusable` is jellyfin-web's own hook, not ours — see focusManager.js,
            // whose focusable set is INPUT/TEXTAREA/SELECT/BUTTON/A plus that class, and
            // which never looks at [tabindex]. Without it a profile card does not exist as
            // far as upstream's remote navigation is concerned, so any moment our own trap
            // does not get the key event leaves the whole grid unreachable and only the
            // footer buttons focusable. That is issue #16's second report, word for word:
            // "if I move off the top left one, it goes to manage profiles".
            const renderCard = (p) => `
                <div class="profile-card focusable ${this.isManageMode ? 'manage-mode' : ''}${
                    signedInId && this.normalizeGuid(p.profileUserId) === signedInId ? ' is-current' : ''
                }" data-id="${p.profileUserId}" data-pin="${p.requiresPin}" tabindex="0"${
                    signedInId && this.normalizeGuid(p.profileUserId) === signedInId ? ' aria-current="true"' : ''
                }>
                    <div class="profile-avatar-container">
                        ${p.isMaster ? `
                        <div class="profile-crown">
                            <svg viewBox="0 0 24 24" fill="currentColor" style="width: 28px; height: 28px; color: var(--jpf-warn); filter: drop-shadow(0 2px 5px rgba(0,0,0,0.55));">
                                <path d="M5 16h14a1 1 0 0 0 1-.76l2.89-10.12a.5.5 0 0 0-.74-.53l-5.6 3.73-4.11-6.17a.5.5 0 0 0-.88 0L7.45 8.32 1.85 4.59a.5.5 0 0 0-.74.53L4 15.24a1 1 0 0 0 1 .76z"/>
                                <rect x="4" y="18" width="16" height="2" rx="1"/>
                            </svg>
                        </div>
                        ` : ''}
                        <div class="profile-avatar${
                            p.profileImage && p.transparentAvatar ? ' is-transparent' : ''
                        }" style="background-color: ${
                            p.profileImage && p.transparentAvatar ? 'transparent' : safeColor(p.avatarColor)
                        }; overflow: ${
                            p.profileImage && p.transparentAvatar ? 'visible' : 'hidden'
                        }; display: flex; align-items: center; justify-content: center; position: relative;">
                            ${avatarInner(p.profileImage, p.avatarInitial, /* useThumb */ true)}
                            ${this.isManageMode ? `
                            <div class="profile-avatar-overlay-wrap">
                                <svg class="profile-avatar-overlay-svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" style="width: 32px; height: 32px; color: #fff;">
                                    <path d="M12 20h9"></path>
                                    <path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4L16.5 3.5z"></path>
                                </svg>
                            </div>
                            ` : ''}
                        </div>
                        ${p.requiresPin ? `
                        <div class="profile-lock-indicator">
                            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round" style="width: 14px; height: 14px; color: #fff;">
                                <rect x="3" y="11" width="18" height="11" rx="2" ry="2"></rect>
                                <path d="M7 11V7a5 5 0 0 1 10 0v4"></path>
                            </svg>
                        </div>
                        ` : ''}
                        ${p.isBonfire ? `
                        <div class="profile-bonfire-indicator" title="${t('gate.bonfireProfile')}">
                            <span class="material-icons" style="font-size: 1.15rem; color: #fff;">local_fire_department</span>
                        </div>
                        ` : ''}
                    </div>
                    <div class="profile-name">
                        <span>${escapeHtml(p.profileName)}</span>
                        ${signedInId && this.normalizeGuid(p.profileUserId) === signedInId
                            ? `<span class="profile-current-badge">${t('gate.signedIn')}</span>` : ''}
                        ${this.isManageMode ? `
                            <span class="profile-pin-badge ${p.requiresPin ? 'locked' : 'unlocked'}">
                                ${p.requiresPin ? t('gate.pinProtected') : t('gate.noPin')}
                            </span>
                        ` : ''}
                    </div>
                </div>
            `;

            let sectionsHtml = '';
            for (const k of groupKeys) {
                const groupProfiles = grouped[k];
                const isLocalGroup = (k === localMasterId);

                let headerTitle = '';
                let headerIcon = '';
                let isBonfireIcon = false;

                // Both headers use the campfire glyph — the section is called a Bonfire, so a
                // house icon read as a different concept entirely. Your own Bonfire keeps the
                // warm flame colour; linked ones are tinted by .bonfire-icon-color so the two
                // remain distinguishable at a glance.
                if (isLocalGroup) {
                    headerTitle = t('gate.yourBonfire');
                    headerIcon = "local_fire_department";
                } else {
                    const masterProfileForGroup = groupProfiles.find(p => p.isMaster);
                    const groupName = masterProfileForGroup ? escapeHtml(masterProfileForGroup.profileName) : t('gate.guest');
                    headerTitle = t('gate.bonfireOf', { name: groupName });
                    headerIcon = "local_fire_department";
                    isBonfireIcon = true;
                }

                let cardsHtml = groupProfiles.map(p => renderCard(p)).join('');

                if (isLocalGroup) {
                    // Settings used to sit here, as two cards in the same row as people.
                    // They are not profiles: Switcher Style is per-account and Your Bonfire
                    // is between accounts, and neither belongs to the grid of who can watch.
                    // They live behind Settings now — which also means the LAN-bypass toggle,
                    // the one that can hand somebody your admin rights, is somewhere you went
                    // on purpose rather than somewhere you can land while renaming a profile.

                    // Manage Profiles only. The gate answers one question — who is
                    // watching — and a card in that grid reads as a person. Adding one is
                    // administration, and it belongs on the screen named for it, which is
                    // one button away and already where you go to rename or delete.
                    if (this.isManageMode) {
                        if (!atLimit) {
                            cardsHtml += `
                                <div class="profile-card action-add-profile focusable" tabindex="0">
                                    <div class="profile-avatar-container">
                                        <div class="profile-avatar add-avatar">+</div>
                                    </div>
                                    <div class="profile-name">${t('gate.addProfile')}</div>
                                </div>
                            `;
                        } else {
                            cardsHtml += `
                                <div class="profiles-limit-notice">${t('gate.limitReached', { count: subProfileCount, max: maxSubProfiles })}</div>
                            `;
                        }
                    }
                }

                sectionsHtml += `
                    <div class="profiles-home-section">
                        <div class="profiles-home-header">
                            <span class="material-icons profiles-home-icon ${isBonfireIcon ? 'bonfire-icon-color' : ''}">${headerIcon}</span>
                            <span class="profiles-home-title">${headerTitle}</span>
                        </div>
                        <div class="profiles-grid">
                            ${cardsHtml}
                        </div>
                    </div>
                `;
            }

            overlay.innerHTML = `
                <div class="profiles-modal-content anim-fade-in">
                    <h1 class="profiles-title">${title}</h1>
                    ${sectionsHtml}
                    <div class="profiles-footer">
                        <button id="profiles-toggle-manage-btn" class="profiles-btn btn-secondary">${manageBtnText}</button>
                        ${this.isManageMode && hasLocalMaster
                            ? `<button id="profiles-settings-btn" class="profiles-btn btn-secondary">${t('common.settings')}</button>`
                            : ''}
                        ${this._resumeState && !this.isManageMode
                            ? `<button id="profiles-resume-btn" class="profiles-btn btn-secondary">${t('common.cancel')}</button>`
                            : ''}
                    </div>
                    <!-- Deliberately plain and dim. It has to be reachable by D-pad, because
                         a TV has no keyboard shortcut, and this screen is exactly where
                         someone locked out by a broken switcher would be standing.
                         Hidden until the server confirms a code is configured. -->
                    <button id="profiles-panic-link" tabindex="0" style="
                        display: none; background: none; border: none; color: rgba(255,255,255,0.28);
                        font-size: 0.72rem; margin-top: 1.5rem; cursor: pointer;
                        text-decoration: underline; padding: 6px 10px;">${t('gate.cantGetPast')}</button>
                </div>
            `;

            this.attachOverlayInteractions(overlay, profiles);
        },

        attachOverlayInteractions: function (overlay, profiles) {
            // Support D-pad Enter/Space selection on focused profile cards
            overlay.addEventListener('keydown', (e) => {
                if ((e.key === 'Enter' || e.key === ' ') && e.target.classList.contains('profile-card')) {
                    // Prevent accidental selection from propagated keydown events during login
                    if (Date.now() - (this._overlayMountTime || 0) < 350) {
                        e.preventDefault();
                        return;
                    }
                    e.preventDefault();
                    e.target.click();
                }
            });

            // Card selection logic
            overlay.querySelectorAll('.profile-card:not(.action-add-profile)').forEach(card => {
                card.addEventListener('click', (e) => {
                    e.stopPropagation();
                    if (this._switchLock) return;
                    const profileId = card.getAttribute('data-id');
                    const profile = profiles.find(p => this.normalizeGuid(p.profileUserId) === this.normalizeGuid(profileId));
                    if (!profile) return;

                    if (this.isManageMode) {
                        this.showEditProfileModal(profile);
                    } else {
                        if (profile.requiresPin) {
                            this.promptPinEntry(profileId);
                        } else {
                            this.executeProfileSwitch(profileId, null);
                        }
                    }
                });
            });

            // "Add Profile" action
            const addCard = overlay.querySelector('.action-add-profile');
            if (addCard) {
                addCard.addEventListener('click', () => {
                    if (this._switchLock) return;
                    const masterProfile = profiles.find(p => p.isMaster && !p.isBonfire);
                    const masterRequiresPin = masterProfile && masterProfile.requiresPin;
                    
                    if (masterRequiresPin) {
                        ApiClient.getPluginConfiguration(this.pluginId).then(config => {
                            if (config.RequireMasterPinForCreation) {
                                this.promptMasterPinEntry('create', () => {
                                    this.showAddProfileModal();
                                });
                            } else {
                                this.showAddProfileModal();
                            }
                        }).catch(() => {
                            this.showAddProfileModal();
                        });
                    } else {
                        this.showAddProfileModal();
                    }
                });
            }

            // Settings: everything that is not a profile.
            const settingsBtn = overlay.querySelector('#profiles-settings-btn');
            if (settingsBtn) {
                settingsBtn.addEventListener('click', () => {
                    if (this._switchLock) return;
                    this.showSettingsMenu();
                });
            }

            // Emergency disable entry point for clients with no keyboard shortcut.
            const panicLink = overlay.querySelector('#profiles-panic-link');
            if (panicLink) {
                panicLink.addEventListener('click', (e) => {
                    e.preventDefault();
                    this.showPanicPrompt();
                });
                panicLink.addEventListener('keydown', (e) => {
                    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); panicLink.click(); }
                });
                this.applyPanicLinkVisibility();
            }

            // Back out of a switcher the user opened on purpose, returning to the profile
            // they were already using. Only present when there is something to return to —
            // the startup gate is a required choice and has no Cancel.
            const resumeBtn = overlay.querySelector('#profiles-resume-btn');
            if (resumeBtn) {
                resumeBtn.addEventListener('click', () => {
                    if (this._switchLock) return;
                    this.resumePreviousProfile();
                });
            }

            // "Manage Profiles" / "Done" toggle
            const manageBtn = document.getElementById('profiles-toggle-manage-btn');
            if (manageBtn) {
                manageBtn.addEventListener('click', () => {
                    if (this._switchLock) return;
                    if (this.isManageMode) {
                        this.isManageMode = false;
                        this.masterPin = null;
                        this.renderOverlayContent(overlay, profiles);
                    } else {
                        const masterProfile = profiles.find(p => p.isMaster && !p.isBonfire);
                        if (masterProfile && masterProfile.requiresPin) {
                            this.promptMasterPinEntry('manage', () => {
                                this.isManageMode = true;
                                this.renderOverlayContent(overlay, profiles);
                            });
                        } else {
                            this.isManageMode = true;
                            this.renderOverlayContent(overlay, profiles);
                        }
                    }
                });
            }
        },

        promptPinEntry: function (profileId) {
            this.beginNavigation();
            const content = document.querySelector('.profiles-modal-content');
            content.innerHTML = `
                <h1 class="profiles-title">${t('pin.enterProfilePin')}</h1>
                <div class="pin-entry-container">
                    <input type="text" id="profile-pin-input" maxlength="8" pattern="[0-9]*" inputmode="numeric" placeholder="••••" autocomplete="one-time-code" data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true" autofocus />
                    <div id="pin-error-msg" style="display:none; color:var(--jpf-danger); font-size:0.9rem; font-weight:600; text-align:center; margin-top:-0.5rem;"></div>
                    <div class="pin-actions">
                        <button id="pin-submit-btn" class="profiles-btn btn-primary">${t('common.unlock')}</button>
                        <button id="pin-cancel-btn" class="profiles-btn btn-secondary">${t('common.back')}</button>
                    </div>
                </div>
            `;

            const pinInput = document.getElementById('profile-pin-input');
            const errorMsg = document.getElementById('pin-error-msg');
            pinInput.focus();

            // Track in-flight silent verify so we can cancel it if the user keeps typing,
            // and prevent a second switch from firing if one is already in progress.
            let verifyController = null;
            let switchInProgress = false;

            const showPinError = (msg) => {
                switchInProgress = false;
                pinInput.style.borderColor = 'var(--jpf-danger)';
                pinInput.style.boxShadow = '0 0 15px rgba(255,107,107,0.5)';
                errorMsg.textContent = msg || t('pin.incorrectPin');
                errorMsg.style.display = 'block';
                pinInput.value = '';
                // setTimeout avoids re-triggering the 'input' clearError listener on refocus
                setTimeout(() => pinInput.focus(), 0);
            };

            const clearError = () => {
                pinInput.style.borderColor = '';
                pinInput.style.boxShadow = '';
                errorMsg.style.display = 'none';
                errorMsg.textContent = '';
            };

            pinInput.addEventListener('input', () => {
                clearError();
                const currentValue = pinInput.value;

                // Need at least 4 digits, and don't fire another switch if one is underway
                if (currentValue.length < 4 || switchInProgress || this._switchLock) return;

                // Cancel any previous in-flight verify — only the latest keystroke matters
                if (verifyController) verifyController.abort();
                verifyController = typeof AbortController !== 'undefined' ? new AbortController() : null;

                const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
                if (!masterState) return;

                // Silent verify — no error shown on failure, user just keeps typing
                fetch(ApiClient.getUrl('plugins/profiles/verify-pin'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...this.getAuthHeaders(masterState.masterToken)
                    },
                    body: JSON.stringify({ profileId: profileId, pin: currentValue }),
                    ...(verifyController ? { signal: verifyController.signal } : {})
                })
                .then(res => {
                    if (res.status === 401) {
                        this.handleSessionExpired();
                        return;
                    }
                    // Only proceed if PIN matched and nothing else already triggered a switch
                    if (res.ok && !switchInProgress && !this._switchLock) {
                        switchInProgress = true;
                        this.executeProfileSwitch(profileId, currentValue, () => {
                            // Verify said OK but switch failed (edge case) — reset silently
                            switchInProgress = false;
                        });
                    }
                })
                .catch(err => {
                    // AbortError = user typed another digit, a new verify is already in flight
                    // Other errors (network) = ignore silently, user can still hit Enter
                });
            });

            // Manual submit — only place where we show an error on wrong PIN
            const submitPin = () => {
                if (switchInProgress || this._switchLock) return;
                if (verifyController) verifyController.abort();
                verifyController = null;
                const pin = pinInput.value;
                if (!pin) return;
                switchInProgress = true;
                this.executeProfileSwitch(profileId, pin, (msg) => {
                    switchInProgress = false;
                    showPinError(msg);
                });
            };

            document.getElementById('pin-submit-btn').addEventListener('click', submitPin);
            pinInput.addEventListener('keydown', (e) => {
                if (e.key === 'Enter') submitPin();
            });

            document.getElementById('pin-cancel-btn').addEventListener('click', () => {
                if (verifyController) verifyController.abort();
                this.isManageMode = false;
                this.showProfileOverlay(this.currentProfiles);
            });
        },

        promptMasterPinEntry: function (actionType, callback) {
            this.beginNavigation();
            const masterProfile = this.currentProfiles.find(p => p.isMaster && !p.isBonfire);
            if (!masterProfile) return;

            const content = document.querySelector('.profiles-modal-content');
            content.innerHTML = `
                <h1 class="profiles-title">${t('pin.enterMasterPin')}</h1>
                <div class="pin-entry-container">
                    <input type="text" id="master-pin-input" maxlength="8" pattern="[0-9]*" inputmode="numeric" placeholder="••••" autocomplete="one-time-code" data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true" autofocus />
                    <div id="master-pin-error-msg" style="display:none; color:var(--jpf-danger); font-size:0.9rem; font-weight:600; text-align:center; margin-top:-0.5rem;"></div>
                    <div class="pin-actions">
                        <button id="master-pin-submit-btn" class="profiles-btn btn-primary">${t('common.submit')}</button>
                        <button id="master-pin-cancel-btn" class="profiles-btn btn-secondary">${t('common.cancel')}</button>
                    </div>
                </div>
            `;

            const pinInput = document.getElementById('master-pin-input');
            const errorMsg = document.getElementById('master-pin-error-msg');
            pinInput.focus();

            let verifyController = null;
            let verified = false; // prevent callback firing more than once

            const showPinError = (msg) => {
                verified = false;
                pinInput.style.borderColor = 'var(--jpf-danger)';
                pinInput.style.boxShadow = '0 0 15px rgba(255,107,107,0.5)';
                errorMsg.textContent = msg || t('pin.incorrectPin');
                errorMsg.style.display = 'block';
                pinInput.value = '';
                setTimeout(() => pinInput.focus(), 0);
            };

            const clearError = () => {
                pinInput.style.borderColor = '';
                pinInput.style.boxShadow = '';
                errorMsg.style.display = 'none';
                errorMsg.textContent = '';
            };

            // Manual submit — only place where we show an error on wrong master PIN
            let verifyInProgress = false;

            pinInput.addEventListener('input', () => {
                clearError();
                const currentValue = pinInput.value;
                if (currentValue.length < 4 || verified || verifyInProgress) return;

                // Cancel previous in-flight verify — only the latest matters
                if (verifyController) verifyController.abort();
                verifyController = typeof AbortController !== 'undefined' ? new AbortController() : null;

                const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
                if (!masterState) return;

                // Silent verify — no error on failure, user keeps typing
                fetch(ApiClient.getUrl('plugins/profiles/verify-pin'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...this.getAuthHeaders(masterState.masterToken)
                    },
                    body: JSON.stringify({ profileId: masterProfile.profileUserId, pin: currentValue }),
                    ...(verifyController ? { signal: verifyController.signal } : {})
                })
                .then(res => {
                    if (res.status === 401) {
                        this.handleSessionExpired();
                        return;
                    }
                    if (res.ok && !verified && !verifyInProgress) {
                        verified = true;
                        this.masterPin = currentValue;
                        callback();
                    }
                })
                .catch(() => {
                    // AbortError or network error — ignore silently
                });
            });

            const submitPin = () => {
                if (verifyInProgress || verified) return;
                if (verifyController) verifyController.abort();
                verifyController = null;
                const pin = pinInput.value;
                if (!pin) return;
                const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
                if (!masterState) return;

                verifyInProgress = true;
                fetch(ApiClient.getUrl('plugins/profiles/verify-pin'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...this.getAuthHeaders(masterState.masterToken)
                    },
                    body: JSON.stringify({ profileId: masterProfile.profileUserId, pin: pin })
                })
                .then(res => {
                    verifyInProgress = false;
                    if (res.status === 401) {
                        this.handleSessionExpired();
                        throw new Error('Session expired');
                    }
                    if (!res.ok) {
                        return res.text().then(text => {
                            throw new Error(text || t('pin.incorrectMasterPin'));
                        });
                    }
                    this.masterPin = pin;
                    verified = true;
                    callback();
                })
                .catch(err => {
                    verifyInProgress = false;
                    if (err.message !== 'Session expired') {
                        showPinError(err.message || t('pin.incorrectMasterPin'));
                    }
                });
            };

            document.getElementById('master-pin-submit-btn').addEventListener('click', submitPin);
            pinInput.addEventListener('keydown', (e) => {
                if (e.key === 'Enter') submitPin();
            });

            document.getElementById('master-pin-cancel-btn').addEventListener('click', () => {
                if (verifyController) verifyController.abort();
                this.showProfileOverlay(this.currentProfiles);
            });
        },

        // onError: optional callback(message) invoked on a failed switch.
        // Callers capture their own DOM references via closure so we never re-query
        // the DOM inside an async callback (which can race against overlay teardown).
        /// Puts the picker into "working on it" while a switch is in flight.
        ///
        /// The switch is a round trip to the server and then a full page reload. On a phone
        /// that is a couple of seconds during which nothing on screen changed at all, so the
        /// only reasonable reading was that the tap had missed — and the reporter kept
        /// tapping. _switchLock already made the extra taps harmless; it just never said so.
        setSwitchBusy: function (profileId, busy) {
            const overlay = document.getElementById('profiles-gate-overlay');
            if (overlay) {
                overlay.classList.toggle('is-switching', busy);
                if (busy) overlay.setAttribute('aria-busy', 'true');
                else overlay.removeAttribute('aria-busy');

                // Matched by normalised id rather than an attribute selector, so a profile
                // id can never be read as one.
                const wanted = busy ? this.normalizeGuid(profileId) : null;
                overlay.querySelectorAll('.profile-card').forEach(card => {
                    const isTarget = !!wanted
                        && this.normalizeGuid(card.getAttribute('data-id')) === wanted;
                    card.classList.toggle('is-switching', isTarget);
                });
            }

            // Started from a PIN prompt, the button is what the user is looking at — the
            // card behind it may not even be on screen.
            ['pin-submit-btn', 'master-pin-submit-btn'].forEach(id => {
                const btn = document.getElementById(id);
                if (btn) btn.classList.toggle('is-busy', busy);
            });
        },

        executeProfileSwitch: function (profileId, pin, onError) {
            if (this._switchLock) return;

            const apiClient = ApiClient;
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            if (!masterState) return;

            this._switchLock = true;
            this.setSwitchBusy(profileId, true);
            const url = apiClient.getUrl('plugins/profiles/switch');

            fetch(url, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    ...this.getAuthHeaders(masterState.masterToken)
                },
                body: JSON.stringify({ profileId: profileId, pin: pin })
            })
            .then(res => {
                if (!res.ok) {
                    return res.text().then(text => {
                        const body = (text || '').trim();
                        // Our own 401 (caller's token rejected) has an empty body. A 401 with a
                        // message came from something failing further in — the target profile,
                        // not the caller — so signing the master out would be wrong (issue #15).
                        if (res.status === 401 && !body) {
                            this._switchLock = false;
                            this.handleSessionExpired();
                            throw new Error('Session expired');
                        }
                        throw new Error(body || t('pin.incorrectPin'));
                    });
                }
                return res.json();
            })
            .then(data => {
                const activeProfileToken = data.activeProfileToken || data.ActiveProfileToken;
                const jellyfinUserId = data.jellyfinUserId || data.JellyfinUserId;

                if (this.normalizeGuid(jellyfinUserId) === this.normalizeGuid(masterState.masterUserId)) {
                    masterState.masterToken = activeProfileToken;
                    localStorage.setItem(this.config.masterStorageKey, JSON.stringify(masterState));
                }

                // Before anything else is written, because this is the one write that has
                // to survive the reload. Without it the token lives only on this page's
                // ApiClient, and the reload signs back in as whoever was there before —
                // right name in the header, wrong account underneath. Bail here rather
                // than leave session keys describing a switch that will not happen.
                if (!this.updateStoredCredentials(activeProfileToken, jellyfinUserId)) {
                    this._switchLock = false;
                    this.setSwitchBusy(profileId, false);
                    this.showAlert(t('switcher.notSavedTitle'), t('switcher.notSavedBody'));
                    return;
                }

                this._sessionSet(this.config.activeSessionKey, activeProfileToken);
                // Cached against the profile being switched into, before the reload, so the
                // next load starts with the right rules already in place.
                this.cacheLibraryArtwork(jellyfinUserId, data.libraryArtwork || data.LibraryArtwork || []);

                // A switch has happened; there is no longer an earlier profile to go back to.
                this._resumeState = null;

                const profile = this.currentProfiles.find(p => this.normalizeGuid(p.profileUserId) === this.normalizeGuid(profileId));
                if (profile) {
                    this._sessionSet('jellyfin_profiles_active_info', JSON.stringify({
                        name: profile.profileName,
                        color: profile.avatarColor,
                        initial: profile.avatarInitial,
                        profileImage: profile.profileImage || null,
                        transparent: !!profile.transparentAvatar
                    }));
                }

                apiClient.setAuthenticationInfo(activeProfileToken, jellyfinUserId);

                // The overlay is opaque and covers the viewport, so leaving it up is all it
                // takes to keep the old page out of sight — and it keeps the spinner on
                // screen for however long the reload costs. Blanking the document instead
                // meant a phone showed several seconds of black with nothing to say the tap
                // had registered. Only the background is calmed towards the next page.
                const leavingBg = this._captureLeavingBackground();
                const overlay = document.getElementById('profiles-gate-overlay');
                if (overlay) {
                    overlay.style.transition = 'background 0.2s ease';
                    overlay.style.background = leavingBg;
                } else {
                    // Menu-mode switch with no gate on screen: nothing is covering the app,
                    // so the old page does still have to be hidden before it repaints.
                    document.documentElement.style.cssText =
                        'opacity:0;background:' + leavingBg + ';color-scheme:'
                        + (this._isLightColour(leavingBg) ? 'light' : 'dark');
                }
                this._reloading = true;
                localStorage.setItem(this.config.switchingKey, '1');
                this.reloadAtHome();
            })
            .catch(err => {
                this._switchLock = false;
                this.setSwitchBusy(profileId, false);
                if (err.message === 'Session expired') return;
                if (typeof onError === 'function') {
                    // Caller has closed-over references to the DOM — no re-query needed
                    onError(err.message || t('pin.incorrectPin'));
                } else {
                    // Fallback: no PIN screen is currently shown (e.g. direct card tap without PIN prompt)
                    this.isManageMode = false;
                    this.interceptHomeAndShowProfiles();
                }
            });
        },

        // ── Avatar images ──────────────────────────────────────────────────────────
        // Two renderings are produced for every picture: a master used where the avatar is
        // shown large, and a thumbnail used by grids and switcher cards. Twenty full-size
        // avatars in a picker would otherwise decode about twenty megabytes of bitmap,
        // which is enough to stall the TV browsers this plugin supports.
        //
        // Both are produced here, in the browser, so the plugin needs no server-side image
        // library. It also means the accepted input set is "whatever this browser can
        // decode" while the stored output stays deliberately narrow.

        IMAGE_MASTER_SIZE: 512,
        IMAGE_THUMB_SIZE: 128,

        /// Formats refused before we even try to decode, each with a reason worth showing.
        /// Everything else is handed to the browser: if it decodes, we can store it.
        _rejectImageFile: function (file) {
            const name = (file.name || '').toLowerCase();
            const type = (file.type || '').toLowerCase();

            // The common case by a distance: iPhones shoot HEIC by default, and only Safari
            // can decode it. Elsewhere the canvas load simply fails, so without this the
            // user gets silence and no idea why.
            if (type.includes('heic') || type.includes('heif') || /\.hei[cf]$/.test(name)) {
                return t('errors.heicUnsupported');
            }

            // SVG can carry script, and these files are served back from the server's own
            // origin. Not worth it for an avatar.
            if (type.includes('svg') || /\.svgz?$/.test(name)) {
                return t('errors.svgUnsupported');
            }

            if (file.size > 25 * 1024 * 1024) {
                return t('errors.imageTooLarge');
            }

            return null;
        },

        /// Reads a File into a decoded <img>. Rejects with a message fit to show the user.
        loadImageFromFile: function (file) {
            return new Promise((resolve, reject) => {
                const reason = this._rejectImageFile(file);
                if (reason) { reject(new Error(reason)); return; }

                const reader = new FileReader();
                reader.onerror = () => reject(new Error(t('errors.fileUnreadable')));
                reader.onload = (event) => {
                    const img = new Image();
                    img.onload = () => {
                        if (!img.width || !img.height) {
                            reject(new Error(t('errors.imageEmpty')));
                            return;
                        }
                        resolve(img);
                    };
                    // Reached for any format this browser cannot decode — including a HEIC
                    // that slipped past the check above with an empty MIME type.
                    img.onerror = () => reject(new Error(t('errors.imageFormatUnsupported')));
                    img.src = event.target.result;
                };
                reader.readAsDataURL(file);
            });
        },

        /// Loads a URL into an <img> for cropping. Used when someone picks an avatar from the
        /// administrator's library. The canvas has to stay untainted or the crop cannot be
        /// re-encoded, so anything off-origin is requested with CORS — that is the packaged
        /// clients, where the page origin is the app rather than the server.
        loadImageFromUrl: function (url) {
            const resolved = pluginUrl(url);
            return new Promise((resolve, reject) => {
                const img = new Image();
                img.onload = () => resolve(img);
                img.onerror = () => reject(new Error(t('errors.imageLoadFailed')));
                try {
                    if (new URL(resolved, window.location.href).origin !== window.location.origin) {
                        img.crossOrigin = 'anonymous';
                    }
                } catch (e) { /* unparseable — treat as same-origin */ }
                img.src = resolved;
            });
        },

        /// Smallest zoom at which the image still covers a square of `size`.
        _coverZoom: function (img, size) {
            return Math.max(size / img.width, size / img.height);
        },

        /// Keeps the image covering the viewport, so a crop can never include blank edges.
        _clampCrop: function (img, viewport, crop) {
            const w = img.width * crop.zoom;
            const h = img.height * crop.zoom;
            return {
                zoom: crop.zoom,
                x: Math.min(0, Math.max(viewport - w, crop.x)),
                y: Math.min(0, Math.max(viewport - h, crop.y))
            };
        },

        /// Renders the cropped square at `outSize` onto a canvas.
        _cropCanvas: function (img, viewport, crop, outSize) {
            const canvas = document.createElement('canvas');
            canvas.width = outSize;
            canvas.height = outSize;
            const ctx = canvas.getContext('2d');
            const k = outSize / viewport;
            ctx.drawImage(img, crop.x * k, crop.y * k, img.width * crop.zoom * k, img.height * crop.zoom * k);
            return canvas;
        },

        /// Renders the cropped square at `outSize` and returns it as a data URL.
        /// PNG is used when the source has transparency — re-encoding a cut-out avatar as
        /// JPEG would fill the transparent area with black.
        renderCrop: function (img, viewport, crop, outSize, preferPng) {
            const canvas = this._cropCanvas(img, viewport, crop, outSize);
            return preferPng ? canvas.toDataURL('image/png') : canvas.toDataURL('image/jpeg', 0.85);
        },

        /// Reads an image's alpha channel at low resolution and answers the two separate
        /// questions the picker has about it. One decode, two answers.
        ///
        ///   hasAlpha — is any pixel less than fully opaque? Decides PNG over JPEG, where
        ///              a single soft pixel is enough to matter, because re-encoding a
        ///              cut-out as JPEG fills the transparent area with black.
        ///
        ///   cutout   — is this shaped art rather than a rectangular photo? Decides
        ///              whether to paint the avatar colour behind it (issue #23). This is
        ///              a much stricter question and deliberately not the same test: an
        ///              antialiased edge or a soft border makes hasAlpha true, and must
        ///              not be allowed to switch somebody's background off.
        ///
        /// cutout reads the corners, because `object-fit: cover` means the corners are the
        /// only place the colour is ever visible. A circular avatar has four transparent
        /// corners; a photograph has none.
        _alphaProfile: function (source) {
            const out = { hasAlpha: false, cutout: false };
            try {
                const s = 32;
                const canvas = document.createElement('canvas');
                canvas.width = s; canvas.height = s;
                const ctx = canvas.getContext('2d');
                ctx.drawImage(source, 0, 0, s, s);
                const data = ctx.getImageData(0, 0, s, s).data;

                for (let i = 3; i < data.length; i += 4) {
                    if (data[i] < 250) { out.hasAlpha = true; break; }
                }

                // Nothing is transparent at all, so there is no shape to work around and
                // the corner walk cannot say otherwise.
                if (!out.hasAlpha) return out;

                // A corner counts as cut away only when a whole 4x4 block of it is clear.
                // One outermost pixel would let a single stray sample decide this.
                const BLOCK = 4;
                const corners = [[0, 0], [s - BLOCK, 0], [0, s - BLOCK], [s - BLOCK, s - BLOCK]];
                let clearCorners = 0;

                for (let c = 0; c < corners.length; c++) {
                    const cx = corners[c][0];
                    const cy = corners[c][1];
                    let clear = true;
                    for (let y = cy; y < cy + BLOCK && clear; y++) {
                        for (let x = cx; x < cx + BLOCK && clear; x++) {
                            if (data[(y * s + x) * 4 + 3] >= 16) clear = false;
                        }
                    }
                    if (clear) clearCorners++;
                }

                // Three of four rather than all four: a circle with one stray opaque
                // sample still counts, a rectangle with one rounded corner still does not.
                out.cutout = clearCorners >= 3;
            } catch (e) { /* tainted canvas or no context — assume an opaque photo */ }
            return out;
        },

        /// True when any pixel is not fully opaque. Sampled at low resolution — this only
        /// decides an output format, so an exact answer is not worth the work.
        _hasTransparency: function (img) {
            return this._alphaProfile(img).hasAlpha;
        },

        /// Opens the crop editor. Calls back with { image, thumb } data URLs, or does
        /// nothing if the user cancels.
        ///
        /// Supports all three input modes the supported clients need: mouse drag, touch
        /// drag, and D-pad (arrows pan, +/- zoom, and the zoom slider is focusable).
        showCropDialog: function (img, onDone) {
            const VIEW = 260;
            const preferPng = this._hasTransparency(img);

            const minZoom = this._coverZoom(img, VIEW);
            let crop = this._clampCrop(img, VIEW, {
                zoom: minZoom,
                // Start centred — the subject of a photo is far more often in the middle
                // than in a corner.
                x: (VIEW - img.width * minZoom) / 2,
                y: (VIEW - img.height * minZoom) / 2
            });

            const dialog = document.createElement('div');
            dialog.id = 'profiles-crop-dialog';
            dialog.classList.add('jpf-trap-surface');
            dialog.style.cssText = `
                position: fixed; top: 0; left: 0; right: 0; bottom: 0;
                background: rgba(0,0,0,0.85); backdrop-filter: blur(8px);
                display: flex; align-items: center; justify-content: center;
                z-index: ${DIALOG_Z};
            `;
            dialog.innerHTML = `
                <div style="background:#181818; border:1px solid rgba(255,255,255,0.1); border-radius: var(--jpf-r-md);
                            padding:22px; max-width:340px; width:92%; box-shadow:0 10px 30px rgba(0,0,0,0.5); text-align:center;
                            user-select:none; -webkit-user-select:none;">
                    <h2 style="margin:0 0 4px 0; color:#fff; font-size:1.15rem; font-weight:700;">${t('avatar.positionYourPicture')}</h2>
                    <p style="color:rgba(255,255,255,0.55); font-size:0.78rem; margin:0 0 14px 0;">
                        ${t('avatar.dragOrArrows')}
                    </p>
                    <!-- data-profiles-own-keys marks the arrows as this element's own, so
                         the focus trap leaves them for panning. It is on the view and not
                         the dialog so the buttons below stay reachable by remote. -->
                    <div id="profiles-crop-view" tabindex="0" data-profiles-own-keys="1" style="
                        width:${VIEW}px; height:${VIEW}px; margin:0 auto; border-radius:50%;
                        overflow:hidden; position:relative; cursor:grab; touch-action:none;
                        background:#0d0d12; outline-offset:3px;">
                        <canvas id="profiles-crop-canvas" width="${VIEW}" height="${VIEW}"
                                style="display:block; width:${VIEW}px; height:${VIEW}px;"></canvas>
                    </div>
                    <input type="range" id="profiles-crop-zoom" min="1" max="4" step="0.01" value="1"
                           style="width:100%; margin:16px 0 4px 0;" aria-label="${t('avatar.zoom')}" />
                    <div style="display:flex; gap: var(--jpf-gap); justify-content:center; margin-top:12px;">
                        <button id="profiles-crop-cancel" class="profiles-btn btn-secondary" style="padding:10px 20px; font-weight:600;">${t('common.cancel')}</button>
                        <button id="profiles-crop-save" class="profiles-btn btn-primary" style="padding:10px 20px; font-weight:600;">${t('avatar.usePicture')}</button>
                    </div>
                </div>
            `;
            document.body.appendChild(dialog);

            const view = dialog.querySelector('#profiles-crop-view');
            const canvas = dialog.querySelector('#profiles-crop-canvas');
            const ctx = canvas.getContext('2d');
            const zoomInput = dialog.querySelector('#profiles-crop-zoom');

            const draw = () => {
                ctx.clearRect(0, 0, VIEW, VIEW);
                ctx.drawImage(img, crop.x, crop.y, img.width * crop.zoom, img.height * crop.zoom);
            };
            draw();

            const setZoom = (multiplier, anchorX, anchorY) => {
                const next = Math.max(1, Math.min(4, multiplier));
                const newZoom = minZoom * next;
                // Zoom about a point so the image does not lurch sideways: keep whatever
                // was under the anchor in the same place.
                const ax = anchorX === undefined ? VIEW / 2 : anchorX;
                const ay = anchorY === undefined ? VIEW / 2 : anchorY;
                const ratio = newZoom / crop.zoom;
                crop = this._clampCrop(img, VIEW, {
                    zoom: newZoom,
                    x: ax - (ax - crop.x) * ratio,
                    y: ay - (ay - crop.y) * ratio
                });
                zoomInput.value = String(next);
                draw();
            };

            zoomInput.addEventListener('input', () => setZoom(parseFloat(zoomInput.value)));

            // ── Dragging ──────────────────────────────────────────────────────────
            // Movement and release are tracked on window, not on the circle. Pointer
            // capture used to be what kept a gesture alive, and it is not dependable
            // across the clients this runs on — a throw from setPointerCapture aborted
            // the handler before dragging was ever set, which is why panning did nothing
            // while the zoom slider still worked. Capture is now best-effort only.
            let dragging = false, lastX = 0, lastY = 0, pinchStart = 0, pinchZoomStart = 1;
            const activePointers = new Map();

            const beginPointer = (id, x, y) => {
                activePointers.set(id, { x: x, y: y });
                if (activePointers.size === 1) {
                    dragging = true; lastX = x; lastY = y;
                    view.style.cursor = 'grabbing';
                } else if (activePointers.size === 2) {
                    const pts = Array.from(activePointers.values());
                    pinchStart = Math.hypot(pts[0].x - pts[1].x, pts[0].y - pts[1].y);
                    pinchZoomStart = parseFloat(zoomInput.value);
                }
            };

            const movePointer = (id, x, y) => {
                if (!activePointers.has(id)) return;
                activePointers.set(id, { x: x, y: y });

                if (activePointers.size === 2 && pinchStart > 0) {
                    const pts = Array.from(activePointers.values());
                    const dist = Math.hypot(pts[0].x - pts[1].x, pts[0].y - pts[1].y);
                    setZoom(pinchZoomStart * (dist / pinchStart));
                    return;
                }
                if (!dragging) return;
                crop = this._clampCrop(img, VIEW, {
                    zoom: crop.zoom,
                    x: crop.x + (x - lastX),
                    y: crop.y + (y - lastY)
                });
                lastX = x; lastY = y;
                draw();
            };

            const endPointer = (id) => {
                activePointers.delete(id);
                if (activePointers.size < 2) pinchStart = 0;
                if (activePointers.size === 0) { dragging = false; view.style.cursor = 'grab'; }
            };

            // Tracked so the window-level listeners come off again when the dialog closes.
            const bound = [];
            const on = (target, type, fn, opts) => {
                target.addEventListener(type, fn, opts);
                bound.push([target, type, fn, opts]);
            };

            if (window.PointerEvent) {
                on(view, 'pointerdown', (e) => {
                    // Stops the browser starting a text selection or image drag instead,
                    // which on desktop swallows the rest of the gesture.
                    e.preventDefault();
                    // preventDefault also suppresses the focus that a click would give,
                    // and focus is what the arrow-key panning below needs.
                    view.focus();
                    try { view.setPointerCapture(e.pointerId); } catch (err) { /* best effort */ }
                    beginPointer(e.pointerId, e.clientX, e.clientY);
                });
                on(window, 'pointermove', (e) => movePointer(e.pointerId, e.clientX, e.clientY));
                on(window, 'pointerup', (e) => endPointer(e.pointerId));
                on(window, 'pointercancel', (e) => endPointer(e.pointerId));
            } else {
                // Older TV browsers have no Pointer Events at all.
                on(view, 'mousedown', (e) => { e.preventDefault(); beginPointer('mouse', e.clientX, e.clientY); });
                on(window, 'mousemove', (e) => movePointer('mouse', e.clientX, e.clientY));
                on(window, 'mouseup', () => endPointer('mouse'));

                on(view, 'touchstart', (e) => {
                    e.preventDefault();
                    Array.prototype.forEach.call(e.changedTouches, t => beginPointer(t.identifier, t.clientX, t.clientY));
                }, { passive: false });
                on(window, 'touchmove', (e) => {
                    if (!activePointers.size) return;
                    // Without this the page scrolls under the finger instead of panning.
                    e.preventDefault();
                    Array.prototype.forEach.call(e.changedTouches, t => movePointer(t.identifier, t.clientX, t.clientY));
                }, { passive: false });
                const endTouch = (e) => Array.prototype.forEach.call(e.changedTouches, t => endPointer(t.identifier));
                on(window, 'touchend', endTouch);
                on(window, 'touchcancel', endTouch);
            }

            // D-pad / keyboard. A television has no pointer at all, so this is the only way
            // in on the clients that need the avatar library most.
            view.addEventListener('keydown', (e) => {
                const step = 12;
                let handled = true;
                switch (e.key) {
                    case 'ArrowLeft':  crop = this._clampCrop(img, VIEW, { zoom: crop.zoom, x: crop.x + step, y: crop.y }); break;
                    case 'ArrowRight': crop = this._clampCrop(img, VIEW, { zoom: crop.zoom, x: crop.x - step, y: crop.y }); break;
                    case 'ArrowUp':    crop = this._clampCrop(img, VIEW, { zoom: crop.zoom, x: crop.x, y: crop.y + step }); break;
                    case 'ArrowDown':  crop = this._clampCrop(img, VIEW, { zoom: crop.zoom, x: crop.x, y: crop.y - step }); break;
                    case '+': case '=': setZoom(parseFloat(zoomInput.value) + 0.2); break;
                    case '-': case '_': setZoom(parseFloat(zoomInput.value) - 0.2); break;
                    // The arrows are spent on panning here, so OK is the way onward. A
                    // remote has nothing else to leave the picture with.
                    case 'Enter': case ' ':
                        e.preventDefault();
                        dialog.querySelector('#profiles-crop-save').focus();
                        return;
                    default: handled = false;
                }
                if (handled) { e.preventDefault(); draw(); }
            });

            const close = () => {
                bound.forEach(([target, type, fn, opts]) => target.removeEventListener(type, fn, opts));
                bound.length = 0;
                dialog.remove();
            };
            dialog.querySelector('#profiles-crop-cancel').addEventListener('click', close);
            dialog.querySelector('#profiles-crop-save').addEventListener('click', () => {
                const thumbCanvas = this._cropCanvas(img, VIEW, crop, this.IMAGE_THUMB_SIZE);

                // Read from the cropped result, not the source. Cropping into the opaque
                // middle of a cut-out leaves a picture with nothing transparent about it,
                // and the background decision has to describe what will actually be shown.
                // preferPng stays on the source, where being over-eager costs only bytes.
                const cutout = this._alphaProfile(thumbCanvas).cutout;

                const image = this.renderCrop(img, VIEW, crop, this.IMAGE_MASTER_SIZE, preferPng);
                const thumb = preferPng
                    ? thumbCanvas.toDataURL('image/png')
                    : thumbCanvas.toDataURL('image/jpeg', 0.85);
                close();
                onDone({ image: image, thumb: thumb, cutout: cutout });
            });

            setTimeout(() => view.focus(), 50);
        },

        /// Fetches the administrator's avatar library. Resolves to a safe empty shape on
        /// failure so a picker can always render.
        fetchAvatarLibrary: function (apiClient, token) {
            return fetch(apiClient.getUrl('plugins/profiles/avatars'), {
                cache: 'no-store',
                headers: this.getAuthHeaders(token)
            })
            .then(res => res.ok ? res.json() : Promise.reject(new Error('unavailable')))
            .then(data => ({
                allowCustomUploads: (data.allowCustomUploads !== undefined ? data.allowCustomUploads : data.AllowCustomUploads) !== false,
                avatars: (data.avatars || data.Avatars || []).map(a => ({
                    id: a.id || a.Id,
                    displayName: a.displayName || a.DisplayName || '',
                    url: a.url || a.Url,
                    thumbUrl: a.thumbUrl || a.ThumbUrl
                }))
            }))
            .catch(() => ({ allowCustomUploads: true, avatars: [] }));
        },

        /// Renders the profile-picture block. Shared by the create and edit forms so the two
        /// cannot drift — they already carried near-identical copies of the old upload UI.
        ///
        /// `prefix` namespaces the element ids ('create' or 'edit').
        /// The cut-out background choice (issue #23).
        ///
        /// Rendered by the form rather than by renderAvatarPicker, and placed directly under
        /// the name field: it decides what happens to the avatar *colour*, so sitting below
        /// the picture panel put the whole picture row between it and the swatches it
        /// governs. initAvatarPicker still owns the wiring and finds it by id wherever the
        /// form has put it.
        ///
        /// Hidden until a picture is set — with no picture the colour is the entire
        /// background and there is nothing to switch off. The library-artwork dialog reuses
        /// the picker and simply never calls this.
        renderTransparentToggle: function (prefix, currentImage, currentTransparent) {
            return `
                <div id="${prefix}-transparent-row" class="form-group picture-transparent-row"
                     style="display: ${currentImage ? 'block' : 'none'};">
                    <label class="library-check-label picture-transparent-toggle">
                        <input type="checkbox" id="${prefix}-transparent-toggle"${currentTransparent ? ' checked' : ''} />
                        <span>${t('profileForm.noBackground')}</span>
                    </label>
                    <div class="form-hint picture-transparent-hint">${t('profileForm.noBackgroundHint')}</div>
                </div>
            `;
        },

        /// `transparency` opts this picker into the cut-out background choice (issue #23):
        /// null or omitted means the form is not offering it, which is what the
        /// library-artwork dialog wants — it reuses this picker to choose a tile picture,
        /// where a transparent avatar background means nothing. Otherwise { enabled: bool }.
        renderAvatarPicker: function (prefix, library, currentImage, currentColor, transparency) {
            const supportsTransparent = !!transparency;
            const currentTransparent = supportsTransparent && !!transparency.enabled;
            const hasLibrary = library.avatars.length > 0;
            const preview = currentImage ? avatarInner(currentImage, '+', /* useThumb */ true) : '+';

            const libraryHtml = hasLibrary ? `
                <div class="picture-source-block">
                    <div class="picture-source-title">${t('avatar.fromThisServer')}</div>
                    <div id="${prefix}-avatar-library" class="avatar-library-grid">
                        ${library.avatars.map(a => `
                            <button type="button" class="avatar-library-item" tabindex="0"
                                    data-id="${escapeHtml(a.id)}" data-url="${escapeHtml(a.url)}"
                                    title="${escapeHtml(a.displayName)}"
                                    aria-label="${escapeHtml(a.displayName) || t('avatar.avatarLabel')}">
                                <img src="${safeImageSrc(a.thumbUrl)}" alt="" loading="lazy" />
                            </button>
                        `).join('')}
                    </div>
                </div>
            ` : '';

            // The upload control disappears entirely when the administrator has locked
            // avatars to the library — a disabled button people cannot use is just noise.
            const uploadHtml = library.allowCustomUploads ? `
                <div class="picture-source-block">
                    <div class="picture-source-title">${t('avatar.fromThisDevice')}</div>
                    <label for="${prefix}-profile-image-file" id="${prefix}-profile-image-label" class="profiles-btn btn-secondary image-upload-btn" tabindex="0">
                        <span class="material-icons" style="font-size: 1.25rem;">photo_camera</span>
                        <span>${t('avatar.uploadPicture')}</span>
                    </label>
                    <input type="file" id="${prefix}-profile-image-file" accept="image/jpeg,image/png,image/webp,image/gif,image/avif,image/bmp" style="display: none;" />
                    <div class="form-hint" style="margin: 0;">${t('avatar.uploadHint')}</div>
                </div>
            ` : `
                <div class="form-hint" style="margin: 0; min-width: 0;">
                    ${t('avatar.adminLimited')}
                </div>
            `;

            // A pasted URL was the odd one out among the ways of setting a picture: it
            // could not be cropped, nothing was copied onto the server, and the avatar
            // broke the day the far end went away — with no sign here that it had. Every
            // other path ends in a file this server owns.

            // Open when there is no picture yet, because on a new profile choosing one IS
            // the task. On a profile that already has one, the whole apparatus was sitting
            // open for nothing.
            const sourcesOpen = !currentImage;

            // Remove is rendered here rather than by the edit form, so the two actions are
            // the same size and sit together. It was a third-height button hanging under
            // the row, which read as belonging to whatever came next.
            return `
                <div class="form-group">
                    <label>${t('avatar.profilePicture')}</label>
                    <div class="profile-image-upload-container" style="display: flex; flex-direction: column; gap: var(--jpf-gap);">
                        <div class="image-upload-row">
                            <div id="${prefix}-image-upload-preview" class="image-upload-preview${
                                currentImage && currentTransparent ? ' is-transparent' : ''
                            }" style="background-color: ${
                                currentImage && currentTransparent ? 'transparent' : safeColor(currentColor)
                            };">${preview}</div>
                            <div class="image-upload-actions">
                                <button type="button" id="${prefix}-change-picture" class="profiles-btn btn-secondary image-upload-btn picture-change-btn"
                                        aria-expanded="${sourcesOpen}" aria-controls="${prefix}-picture-sources">
                                    <span class="material-icons" style="font-size: 1.25rem;">photo_camera</span>
                                    <span>${currentImage ? t('avatar.changePicture') : t('avatar.choosePicture')}</span>
                                    <span class="material-icons picture-caret" aria-hidden="true">expand_more</span>
                                </button>
                                <button type="button" id="${prefix}-clear-profile-image-btn" class="profiles-btn btn-secondary image-upload-btn picture-remove-btn"
                                        style="display: ${currentImage ? 'inline-flex' : 'none'};">
                                    <span class="material-icons" style="font-size: 1.25rem;">delete_outline</span>
                                    <span>${t('common.remove')}</span>
                                </button>
                            </div>
                        </div>
                        <div id="${prefix}-picture-sources" class="picture-sources${sourcesOpen ? ' is-open' : ''}">
                            ${libraryHtml}
                            ${uploadHtml}
                        </div>
                        <div id="${prefix}-image-error" style="display:none; color:var(--jpf-danger); font-size:0.82rem; font-weight:600; line-height:1.45;"></div>
                    </div>
                </div>
            `;
        },

        /// Wires the picker up. Returns an accessor for the chosen image so the caller can
        /// read it at save time without tracking the state itself.
        /// `transparency` must match what was passed to renderAvatarPicker — see there.
        initAvatarPicker: function (container, prefix, library, initialImage, onPreviewChange, transparency) {
            const initialTransparent = !!transparency && !!transparency.enabled;
            // libraryId is set instead of image/thumb when the picture comes from the
            // library on a locked-down server: the server copies the file itself, which is
            // the only form of the choice it can actually verify.
            const state = {
                image: initialImage || null,
                thumb: null,
                libraryId: null,
                // Only meaningful while a picture is set. With no picture the colour is
                // the entire background, so there is nothing to make transparent.
                transparent: !!(initialImage && initialTransparent)
            };

            const previewEl = container.querySelector(`#${prefix}-image-upload-preview`);
            const errEl = container.querySelector(`#${prefix}-image-error`);
            const fileInput = container.querySelector(`#${prefix}-profile-image-file`);
            const fileLabel = container.querySelector(`#${prefix}-profile-image-label`);
            const removeBtn = container.querySelector(`#${prefix}-clear-profile-image-btn`);
            const transparentRow = container.querySelector(`#${prefix}-transparent-row`);
            const transparentInput = container.querySelector(`#${prefix}-transparent-toggle`);

            const showError = (message) => {
                if (!errEl) return;
                errEl.textContent = message;
                errEl.style.display = message ? 'block' : 'none';
            };

            /// The colour currently chosen on the form. Read from the DOM rather than
            /// passed in: the two forms already keep `.active` on exactly one dot, so
            /// this cannot drift out of step with what will be saved.
            const currentColor = () => {
                const active = container.querySelector('.color-dot.active');
                return active ? safeColor(active.getAttribute('data-color')) : DEFAULT_AVATAR_COLOR;
            };

            /// Whether the colour swatches can still affect the avatar. Owned here rather
            /// than by the two forms, because it depends on both facts this function
            /// holds — a picture being set, and its background being switched off — and
            /// two writers would race each other.
            const syncColorGroup = () => {
                this.setColorGroupInert(prefix, !!state.image, state.transparent);
            };

            /// Paints the preview to match state.transparent and keeps the checkbox in
            /// step. Called both when detection decides and when the user overrides, so
            /// the two can never disagree about what is stored.
            const applyTransparency = () => {
                if (transparentInput) transparentInput.checked = state.transparent;
                syncColorGroup();
                if (!previewEl) return;
                previewEl.classList.toggle('is-transparent', state.transparent);
                previewEl.style.backgroundColor = state.transparent ? 'transparent' : currentColor();
            };

            const setPreview = (src) => {
                if (!previewEl) return;
                previewEl.style.position = 'relative';
                previewEl.innerHTML = src ? avatarInner(src, '+', /* useThumb */ true) : '+';
                // Removing is only an option while there is something to remove. Driven
                // from here so it is right on the first paint too — setPreview runs once
                // at init.
                if (removeBtn) removeBtn.style.display = src ? 'inline-flex' : 'none';
                // Same for the transparency choice: it describes a picture, so it has
                // nothing to say when there is not one.
                if (transparentRow) transparentRow.style.display = src ? 'block' : 'none';
                syncColorGroup();
                if (typeof onPreviewChange === 'function') onPreviewChange(src);
            };
            setPreview(state.image);
            applyTransparency();

            if (transparentInput) {
                // The user's answer always beats detection's. Detection only ever writes
                // this value when a new picture arrives.
                transparentInput.addEventListener('change', () => {
                    state.transparent = transparentInput.checked;
                    applyTransparency();
                });
            }

            // The ways of choosing a picture are collapsed behind one button. Every id
            // the handlers below bind to still exists — they have only moved inside the
            // panel — so nothing else in this function changes.
            const sourcesEl = container.querySelector(`#${prefix}-picture-sources`);
            const changeBtn = container.querySelector(`#${prefix}-change-picture`);
            if (sourcesEl && changeBtn) {
                changeBtn.addEventListener('click', () => {
                    const open = sourcesEl.classList.toggle('is-open');
                    changeBtn.setAttribute('aria-expanded', open ? 'true' : 'false');
                });
            }

            const applyCropped = (result) => {
                state.image = result.image;
                state.thumb = result.thumb;
                state.libraryId = null;
                setPreview(result.image);
                // A new picture re-answers the question, so detection wins here even if
                // the user had overridden it for the previous one — the override was
                // about a picture that is no longer set.
                state.transparent = !!result.cutout;
                applyTransparency();
                showError('');
                if (fileInput) fileInput.value = '';
            };

            // Label doubles as the file trigger so it can be focused by D-pad.
            if (fileLabel && fileInput) {
                fileLabel.addEventListener('keydown', (e) => {
                    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); fileInput.click(); }
                });
            }

            if (fileInput) {
                fileInput.addEventListener('change', (e) => {
                    const file = e.target.files && e.target.files[0];
                    if (!file) return;
                    showError('');
                    this.loadImageFromFile(file)
                        .then(img => this.showCropDialog(img, applyCropped))
                        .catch(err => {
                            showError(err.message);
                            fileInput.value = '';
                        });
                });
            }

            container.querySelectorAll(`#${prefix}-avatar-library .avatar-library-item`).forEach(btn => {
                const pick = () => {
                    showError('');

                    // On a locked-down server the crop step is skipped and the id is sent
                    // instead. Cropping would produce a data payload indistinguishable from
                    // an upload, which is exactly what that server has chosen to refuse.
                    if (!library.allowCustomUploads) {
                        state.image = btn.getAttribute('data-url');
                        state.thumb = null;
                        state.libraryId = btn.getAttribute('data-id');
                        setPreview(state.image);

                        // No crop step on this path, so there is no cropped canvas to read
                        // and the source has to be fetched again to be sampled. Same
                        // origin — it is our own avatar endpoint — so the canvas is not
                        // tainted. Detection is best-effort: a failure just leaves the
                        // colour painted, which is the old behaviour.
                        const pickedUrl = state.image;
                        this.loadImageFromUrl(pickedUrl)
                            .then(img => {
                                // The user may have picked something else while this was
                                // in flight; a late answer must not overwrite it.
                                if (state.image !== pickedUrl) return;
                                state.transparent = this._alphaProfile(img).cutout;
                                applyTransparency();
                            })
                            .catch(() => { /* leave the colour on */ });
                        return;
                    }

                    // Full size, not the thumbnail — this is about to be re-cropped, and
                    // cropping a 128px source would produce a soft avatar.
                    this.loadImageFromUrl(btn.getAttribute('data-url'))
                        .then(img => this.showCropDialog(img, applyCropped))
                        .catch(err => showError(err.message));
                };
                btn.addEventListener('click', (e) => { e.preventDefault(); pick(); });
                btn.addEventListener('keydown', (e) => {
                    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); pick(); }
                });
            });

            // Empty string, not null: the server reads that as "delete the picture",
            // whereas null means "leave it alone".
            const clearPicture = () => {
                state.image = '';
                state.thumb = null;
                state.libraryId = null;
                // With no picture the colour is the whole background. Leaving this set
                // would save a profile whose avatar is a transparent square.
                state.transparent = false;
                setPreview(null);
                applyTransparency();
                if (fileInput) fileInput.value = '';
            };

            if (removeBtn) removeBtn.addEventListener('click', clearPicture);

            return {
                get: () => ({
                    image: state.image,
                    thumb: state.thumb,
                    libraryId: state.libraryId,
                    transparent: state.transparent
                }),
                clear: clearPicture,
                setError: showError
            };
        },

        showAddProfileModal: function () {
            const apiClient = ApiClient;
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            if (!masterState) return;

            const ticket = this.beginNavigation();

            // Everything this form needs is account-wide, so it is usually already
            // cached from the prefetch that ran when the overlay opened.
            this.fetchSharedFormData(apiClient, masterState)
            .then(shared => [shared.libraries, shared.devices, shared.libraryTags, shared.avatarLibrary])
            .then(([libraries, devices, libraryTags, avatarLibrary]) => {
                if (!this.navIsCurrent(ticket)) return;
                const normalizedLibs = (libraries || []).map(lib => ({
                    id: lib.id || lib.Id,
                    name: lib.name || lib.Name,
                    collectionType: lib.collectionType || lib.CollectionType
                }));
                // Re-rendering the modal orphans any listeners the previous form owned.
                this.clearManagedDocumentListeners();
                const content = document.querySelector('.profiles-modal-content');
                // ── Section 1: who this profile is ──────────────────────────────
                const createAppearance = `
                    <div class="form-group">
                        <label for="create-name-input">${t('profileForm.profileName')}</label>
                        <input type="text" id="create-name-input" placeholder="${t('profileForm.namePlaceholder')}" required />
                    </div>
                    ${this.renderTransparentToggle('create', null, false)}
                    <div class="form-group avatar-color-group" id="create-color-group">
                        <label>${t('profileForm.avatarColor')}</label>
                        ${this.renderColorPicker(DEFAULT_AVATAR_COLOR)}
                        <div class="form-hint" data-role="color-hint">${t('profileForm.colorHintNoPicture')}</div>
                    </div>
                    ${this.renderAvatarPicker('create', avatarLibrary, null, DEFAULT_AVATAR_COLOR, { enabled: false })}
                `;

                // ── Section 2: getting into this profile ────────────────────────
                const createSecurity = `
                    <div class="form-group">
                        <label for="create-pin-input">${t('profileForm.pin')}</label>
                        <input type="text" id="create-pin-input" maxlength="8" pattern="[0-9]*" inputmode="numeric" placeholder="${t('profileForm.pinPlaceholderEmpty')}" autocomplete="one-time-code" data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true" />
                    </div>
                    <div class="form-group">
                        <label class="library-check-label" style="display: inline-flex; align-items: center; gap: 0.5rem; cursor: pointer; user-select: none;">
                            <input type="checkbox" id="create-local-bypass-checkbox" style="cursor: pointer; accent-color: var(--jpf-accent);" />
                            <span>${t('profileForm.bypassPinLan')}</span>
                        </label>
                        <div class="form-hint">${t('profileForm.bypassPinHint')}</div>
                    </div>
                    <div class="form-group">
                        <label for="create-lockout-select">${t('profileForm.autoLock')}</label>
                        <select id="create-lockout-select">
                            <option value="0">${t('profileForm.lockoutNever')}</option>
                            <option value="1">${t('profileForm.lockout1Min')}</option>
                            <option value="5" selected>${t('profileForm.lockout5MinDefault')}</option>
                            <option value="10">${t('profileForm.lockout10Min')}</option>
                            <option value="20">${t('profileForm.lockout20Min')}</option>
                            <option value="30">${t('profileForm.lockout30Min')}</option>
                            <option value="60">${t('profileForm.lockout1Hour')}</option>
                        </select>
                        <div class="form-hint">${t('profileForm.autoLockHintCreate')}</div>
                    </div>
                `;

                // ── Section 3: what this profile can browse ─────────────────────
                const createLibraries = `
                    <div class="form-group">
                        <div class="section-inline-header">
                            <label style="margin: 0;">${t('profileForm.enabledLibraries')}</label>
                            <label class="library-check-label" style="font-size: 0.85rem; color: rgba(255,255,255,0.6); margin: 0; display: inline-flex; align-items: center; gap: 0.4rem;">
                                <input type="checkbox" id="create-select-all-libraries" style="margin: 0; cursor: pointer; accent-color: var(--jpf-accent);" />
                                <span>${t('common.selectAll')}</span>
                            </label>
                        </div>
                        <div class="library-checklist">
                            ${normalizedLibs.map(lib => `
                                <label class="library-check-label">
                                    <input type="checkbox" class="library-checkbox" value="${lib.id}" />
                                    <span>${escapeHtml(lib.name)}</span>
                                </label>
                            `).join('')}
                        </div>
                        <div class="form-hint">${t('profileForm.librariesInheritHint')}</div>
                    </div>
                `;

                // ── Section 4: limits applied on top of the libraries above ─────
                const createRestrictions = `
                    ${this.renderDeviceDropdown('create', devices, null)}

                    ${this.renderRatingSelect('create', null)}
                    ${this.renderTagSection('create', libraryTags, null)}
                `;

                content.innerHTML = `
                    <h1 class="profiles-title">${t('profileForm.createTitle')}</h1>
                    <div class="create-profile-container is-two-col">
                        <div class="form-col">
                            ${this.renderSection('person', t('profileForm.sectionProfileTitle'), t('profileForm.sectionProfileSubtitle'), createAppearance)}
                            ${this.renderSection('lock', t('profileForm.sectionSecurityTitle'), t('profileForm.sectionSecuritySubtitle'), createSecurity)}
                        </div>
                        <div class="form-col">
                            ${this.renderSection('video_library', t('profileForm.libraries'), t('profileForm.sectionLibrariesSubtitle'), createLibraries)}
                            ${this.renderSection('shield', t('profileForm.sectionRestrictionsTitle'), t('profileForm.sectionRestrictionsSubtitle'), createRestrictions)}
                        </div>

                        <div id="create-error-msg" class="form-error" style="display:none;"></div>
                        <div class="pin-actions">
                            <button id="create-submit-btn" class="profiles-btn btn-primary">${t('common.create')}</button>
                            <button id="create-cancel-btn" class="profiles-btn btn-secondary">${t('common.cancel')}</button>
                        </div>
                    </div>
                `;

                // Color selector interaction
                const dots = content.querySelectorAll('.color-dot');
                let selectedColor = DEFAULT_AVATAR_COLOR;
                dots.forEach(dot => {
                    dot.addEventListener('click', () => {
                        dots.forEach(d => d.classList.remove('active'));
                        dot.classList.add('active');
                        selectedColor = dot.getAttribute('data-color');
                        const createPreview = document.getElementById('create-image-upload-preview');
                        if (createPreview && !createPreview.querySelector('img')) {
                            createPreview.style.backgroundColor = selectedColor;
                        }
                    });
                });

                // The colour group is dimmed by the picker itself now — it depends on the
                // transparency choice as well as on whether a picture is set.
                const avatarPicker = this.initAvatarPicker(
                    content, 'create', avatarLibrary, null, null, { enabled: false });

                // With no picture chosen, the preview shows the profile's initial — so it
                // has to follow what is being typed into the name field.
                const previewDiv = document.getElementById('create-image-upload-preview');
                const nameInput = document.getElementById('create-name-input');
                if (nameInput && previewDiv) {
                    nameInput.addEventListener('input', () => {
                        if (avatarPicker.get().image) return;
                        const nameVal = nameInput.value.trim();
                        previewDiv.innerHTML = nameVal ? escapeHtml(nameVal.charAt(0).toUpperCase()) : '+';
                    });
                }

                // Support D-pad Enter/Space select on color dots
                content.addEventListener('keydown', (e) => {
                    if ((e.key === 'Enter' || e.key === ' ') && e.target.classList.contains('color-dot')) {
                        e.preventDefault();
                        e.target.click();
                    }
                });

                // Select all libraries logic for creation
                const selectAllCheckbox = document.getElementById('create-select-all-libraries');
                const libCheckboxes = content.querySelectorAll('.library-checkbox');
                if (selectAllCheckbox) {
                    selectAllCheckbox.addEventListener('change', (e) => {
                        const isChecked = e.target.checked;
                        libCheckboxes.forEach(cb => {
                            cb.checked = isChecked;
                        });
                    });

                    libCheckboxes.forEach(cb => {
                        cb.addEventListener('change', () => {
                            const allChecked = Array.from(libCheckboxes).every(c => c.checked);
                            selectAllCheckbox.checked = allChecked;
                        });
                    });
                }

                // Devices dropdown logic for create
                const createTrigger = document.getElementById('create-devices-dropdown-trigger');
                const createList = document.getElementById('create-devices-dropdown-list');
                if (createTrigger && createList) {
                    // Keep aria-expanded in step with the visual state so screen readers and
                    // TV remotes report the dropdown correctly.
                    const setCreateOpen = (open) => {
                        createList.style.display = open ? 'block' : 'none';
                        createTrigger.setAttribute('aria-expanded', open ? 'true' : 'false');
                    };
                    createTrigger.addEventListener('click', (e) => {
                        e.stopPropagation();
                        setCreateOpen(createList.style.display === 'none');
                    });
                    createTrigger.addEventListener('keydown', (e) => {
                        if (e.key === 'Enter' || e.key === ' ') {
                            e.preventDefault();
                            createTrigger.click();
                        } else if (e.key === 'Escape') {
                            setCreateOpen(false);
                        }
                    });
                    this.addManagedDocumentListener('click', () => setCreateOpen(false));
                    createList.addEventListener('click', (e) => {
                        e.stopPropagation();
                    });
                    createList.addEventListener('keydown', (e) => {
                        if (e.key === 'Enter' && e.target.type === 'checkbox') {
                            e.preventDefault();
                            e.target.checked = !e.target.checked;
                            e.target.dispatchEvent(new Event('change'));
                        }
                    });
                }

                const updateCreateSelectedText = () => {
                    const checked = Array.from(content.querySelectorAll('.create-device-checkbox:checked'));
                    const txt = document.getElementById('create-devices-dropdown-selected-text');
                    if (txt) {
                        if (checked.length === 0) {
                            txt.textContent = t('profileForm.allDevicesAllowed');
                        } else if (checked.length === 1) {
                            txt.textContent = t('profileForm.oneDeviceAllowed');
                        } else {
                            txt.textContent = t('profileForm.devicesAllowed', { count: checked.length });
                        }
                    }
                };
                content.querySelectorAll('.create-device-checkbox').forEach(cb => {
                    cb.addEventListener('change', updateCreateSelectedText);
                });
                updateCreateSelectedText();

                document.getElementById('create-submit-btn').addEventListener('click', () => {
                    const name = document.getElementById('create-name-input').value.trim();
                    const pin = document.getElementById('create-pin-input').value;
                    const rating = document.getElementById('create-rating-select').value;
                    const lockoutMinutes = parseInt(document.getElementById('create-lockout-select').value, 10);
                    const bypassPin = document.getElementById('create-local-bypass-checkbox').checked;
                    
                    const checkedLibs = [];
                    content.querySelectorAll('.library-checkbox:checked').forEach(cb => {
                        checkedLibs.push(cb.value);
                    });

                    const checkedDevices = [];
                    content.querySelectorAll('.create-device-checkbox:checked').forEach(cb => {
                        checkedDevices.push(cb.value);
                    });

                    const showCreateError = (msg) => {
                        const el = document.getElementById('create-error-msg');
                        if (el) { el.textContent = msg; el.style.display = 'block'; }
                    };

                    if (!name) {
                        showCreateError(t('profileForm.nameRequired'));
                        return;
                    }

                    if (pin && (pin.length < 4 || pin.length > 8 || !/^\d+$/.test(pin))) {
                        showCreateError(t('profileForm.pinLengthCreate'));
                        return;
                    }

                    const createUrl = apiClient.getUrl('plugins/profiles/create');
                    fetch(createUrl, {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json',
                            ...this.getAuthHeaders(masterState.masterToken)
                        },
                        body: JSON.stringify({
                            profileName: name,
                            pin: pin,
                            avatarColor: selectedColor,
                            maxParentalRating: rating || null,
                            // Send null (not empty array) when no libraries are checked.
                            // An empty array tells the server "allow no libraries",
                            // while null means "inherit all accessible libraries from master".
                            enabledFolders: checkedLibs.length > 0 ? checkedLibs : null,
                            blockedTags: this.getTagEditorValues(content, 'create-blocked-tags'),
                            allowedTags: this.getTagEditorValues(content, 'create-allowed-tags'),
                            masterPin: this.masterPin,
                            lockoutMinutes: lockoutMinutes,
                            bypassPinOnLocalNetwork: bypassPin,
                            allowedDeviceIds: checkedDevices,
                            profileImage: avatarPicker.get().image,
                            profileImageThumb: avatarPicker.get().thumb,
                            avatarLibraryId: avatarPicker.get().libraryId,
                            transparentAvatar: avatarPicker.get().transparent
                        })
                    })
                    .then(res => {
                        if (!res.ok) return res.text().then(text => { throw new Error(text); });
                        return res.json();
                    })
                    .then(() => {
                        this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.masterToken, /* forceRefresh */ true);
                    })
                    .catch(err => {
                        const el = document.getElementById('create-error-msg');
                        if (el) { el.textContent = err.message; el.style.display = 'block'; }
                    });
                });

                document.getElementById('create-cancel-btn').addEventListener('click', () => {
                    this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.masterToken);
                });
                this.initTVCheckboxes(content);
                this.initTagEditors(content);
            });
        },

        showEditProfileModal: function (profile) {
            const apiClient = ApiClient;
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            if (!masterState) return;

            const ticket = this.beginNavigation();

            // Users/{id} is the only one of the five that is about the profile you
            // clicked. The rest come from the account-wide cache.
            const userUrl = apiClient.getUrl(`Users/${profile.profileUserId}`);

            Promise.all([
                this.fetchSharedFormData(apiClient, masterState),
                fetch(userUrl, { headers: this.getAuthHeaders(masterState.masterToken) }).then(res => res.json())
            ])
            .then(([shared, userDetails]) => [shared.libraries, userDetails, shared.devices, shared.libraryTags, shared.avatarLibrary])
            .then(([libraries, userDetails, devices, libraryTags, avatarLibrary]) => {
                if (!this.navIsCurrent(ticket)) return;
                const normalizedLibs = (libraries || []).map(lib => ({
                    id: lib.id || lib.Id,
                    name: lib.name || lib.Name,
                    collectionType: lib.collectionType || lib.CollectionType
                }));
                const policy = userDetails.Policy || userDetails.policy || {};
                const blockedFolders = policy.BlockedMediaFolders || policy.blockedMediaFolders || [];
                const enableAll = policy.EnableAllFolders !== undefined ? policy.EnableAllFolders : (policy.enableAllFolders || false);
                const maxRating = policy.MaxParentalRating !== undefined ? policy.MaxParentalRating : (policy.maxParentalRating !== undefined ? policy.maxParentalRating : null);
                const currentLockout = profile.lockoutMinutes !== undefined ? profile.lockoutMinutes : 5;

                // Re-rendering the modal orphans any listeners the previous form owned.
                this.clearManagedDocumentListeners();
                const content = document.querySelector('.profiles-modal-content');

                const isSub = !profile.isMaster;

                // ── Section 1: who this profile is ──────────────────────────────
                const appearanceBody = `
                    <div class="form-group">
                        <label for="edit-name-input">${t('profileForm.profileName')}</label>
                        <input type="text" id="edit-name-input" value="${escapeHtml(profile.profileName)}" ${profile.isMaster ? 'disabled style="opacity: 0.6"' : ''} required />
                        ${profile.isMaster ? `<div class="form-hint">${t('profileForm.masterNameHint')}</div>` : ''}
                    </div>
                    ${this.renderTransparentToggle('edit', profile.profileImage, !!profile.transparentAvatar)}
                    <div class="form-group avatar-color-group" id="edit-color-group">
                        <label>${t('profileForm.avatarColor')}</label>
                        ${this.renderColorPicker(profile.avatarColor)}
                        <div class="form-hint" data-role="color-hint">${t('profileForm.colorHintNoPicture')}</div>
                    </div>
                    ${this.renderAvatarPicker('edit', avatarLibrary, profile.profileImage, profile.avatarColor, { enabled: !!profile.transparentAvatar })}
                `;

                // ── Section 2: getting into this profile ────────────────────────
                const securityBody = `
                    <div class="form-group">
                        <label for="edit-pin-input">${t('profileForm.pin')}</label>
                        <div class="pin-edit-group" style="display:flex; gap: var(--jpf-gap); flex-wrap: wrap;">
                            <input type="text" id="edit-pin-input" maxlength="8" pattern="[0-9]*" inputmode="numeric" placeholder="${profile.hasPin ? t('profileForm.pinPlaceholderNew') : t('profileForm.pinPlaceholderNone')}" autocomplete="one-time-code" data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true" style="flex:1; min-width: 160px;" />
                            ${profile.hasPin ? `<button id="edit-clear-pin-btn" class="profiles-btn btn-secondary" style="padding:10px 15px;">${t('profileForm.clearPin')}</button>` : ''}
                        </div>
                        <div id="edit-pin-error" class="form-error" style="display:none; margin-top:8px;"></div>
                        <div class="form-hint">
                            ${profile.hasPin
                                ? t('profileForm.pinIsSetHint')
                                : t('profileForm.noPinSetHint')}
                        </div>
                    </div>
                    <div class="form-group">
                        <label class="library-check-label" style="display: inline-flex; align-items: center; gap: 0.5rem; cursor: pointer; user-select: none;">
                            <input type="checkbox" id="edit-local-bypass-checkbox" ${profile.bypassPinOnLocalNetwork ? 'checked' : ''} style="cursor: pointer; accent-color: var(--jpf-accent);" />
                            <span>${t('profileForm.bypassPinLan')}</span>
                        </label>
                        <div class="form-hint">${t('profileForm.bypassPinHint')}</div>
                    </div>
                    <div class="form-group">
                        <label for="edit-lockout-select">${t('profileForm.autoLock')}</label>
                        <select id="edit-lockout-select">
                            <option value="0" ${currentLockout === 0 ? 'selected' : ''}>${t('profileForm.lockoutNever')}</option>
                            <option value="1" ${currentLockout === 1 ? 'selected' : ''}>${t('profileForm.lockout1Min')}</option>
                            <option value="5" ${currentLockout === 5 ? 'selected' : ''}>${t('profileForm.lockout5Min')}</option>
                            <option value="10" ${currentLockout === 10 ? 'selected' : ''}>${t('profileForm.lockout10Min')}</option>
                            <option value="20" ${currentLockout === 20 ? 'selected' : ''}>${t('profileForm.lockout20Min')}</option>
                            <option value="30" ${currentLockout === 30 ? 'selected' : ''}>${t('profileForm.lockout30Min')}</option>
                            <option value="60" ${currentLockout === 60 ? 'selected' : ''}>${t('profileForm.lockout1Hour')}</option>
                        </select>
                        <div class="form-hint">${t('profileForm.autoLockHintEdit')}</div>
                    </div>
                `;

                // ── Section 3: what this profile can browse ─────────────────────
                const librariesBody = `
                    <div class="form-group">
                        <div class="section-inline-header">
                            <label style="margin: 0;">${t('profileForm.libraries')}</label>
                            <label class="library-check-label" style="font-size: 0.85rem; color: rgba(255,255,255,0.6); margin: 0; display: inline-flex; align-items: center; gap: 0.4rem;">
                                <input type="checkbox" id="edit-select-all-libraries" style="margin: 0; cursor: pointer; accent-color: var(--jpf-accent);" />
                                <span>${t('common.selectAll')}</span>
                            </label>
                        </div>
                        <div class="form-hint" style="margin: 0 0 8px 0;">
                            ${t('profileForm.librariesInheritHint')}
                        </div>
                        <div class="libart-list" id="edit-library-artwork">
                            <div class="libart-head" aria-hidden="true">
                                <span class="libart-head-lib">${t('profileForm.libraryHeader')}</span>
                                <span class="libart-head-art">${t('profileForm.artworkHeader')}</span>
                            </div>
                            ${normalizedLibs.map(lib => {
                                const storedFolders = profile.enabledFolders;
                                let isChecked;
                                if (storedFolders !== null && storedFolders !== undefined) {
                                    isChecked = storedFolders.some(id => this.normalizeGuid(id) === this.normalizeGuid(lib.id));
                                } else {
                                    isChecked = enableAll || !blockedFolders.some(bf => this.normalizeGuid(bf) === this.normalizeGuid(lib.id));
                                }
                                return `
                                    <div class="libart-row" data-lib="${lib.id}">
                                        <label class="library-check-label libart-check">
                                            <input type="checkbox" class="library-checkbox" value="${lib.id}" ${isChecked ? 'checked' : ''} />
                                            <span class="libart-thumb" aria-hidden="true"></span>
                                            <span class="libart-name" title="${escapeHtml(lib.name)}">${escapeHtml(lib.name)}</span>
                                        </label>
                                        <select class="libart-mode" aria-label="${t('profileForm.artworkForAria', { name: escapeHtml(lib.name) })}">
                                            <option value="inherit">${t('profileForm.artworkDefault')}</option>
                                            <option value="custom">${t('profileForm.artworkPicture')}</option>
                                            <option value="none">${t('profileForm.artworkHidden')}</option>
                                        </select>
                                        <button type="button" class="profiles-btn btn-secondary libart-choose" style="padding:6px 12px; font-size:0.8rem;">${t('profileForm.artworkChoose')}</button>
                                    </div>
                                `;
                            }).join('')}
                        </div>
                        <label class="library-check-label libart-toggle">
                            <input type="checkbox" id="edit-libart-toggle" />
                            <span>${t('profileForm.artworkToggleLabel')}</span>
                        </label>
                        <div class="form-hint libart-explainer" id="edit-libart-hint" style="display: none;">
                            ${t('profileForm.artworkExplainer')}
                        </div>
                    </div>
                `;

                // ── Section 4: limits applied on top of the libraries above ─────
                const restrictionsBody = `
                    ${this.renderDeviceDropdown('edit', devices, profile)}

                    ${this.renderRatingSelect('edit', maxRating)}
                    ${this.renderTagSection('edit', libraryTags, profile)}
                `;

                content.innerHTML = `
                    <h1 class="profiles-title">${t('profileForm.editTitle')}</h1>
                    <div class="create-profile-container${isSub ? ' is-two-col' : ''}">
                        <div class="form-col">
                            ${this.renderSection('person', t('profileForm.sectionProfileTitle'), t('profileForm.sectionProfileSubtitle'), appearanceBody)}
                            ${this.renderSection('lock', t('profileForm.sectionSecurityTitle'), t('profileForm.sectionSecuritySubtitle'), securityBody)}
                        </div>
                        <div class="form-col">
                            ${isSub ? this.renderSection('video_library', t('profileForm.libraries'), t('profileForm.sectionLibrariesSubtitle'), librariesBody) : ''}
                            ${isSub ? this.renderSection('shield', t('profileForm.sectionRestrictionsTitle'), t('profileForm.sectionRestrictionsSubtitle'), restrictionsBody) : ''}
                        </div>

                        <div class="profile-dialog-actions">
                            <div class="dialog-action-buttons">
                                <button id="edit-submit-btn" class="profiles-btn btn-primary">${t('common.save')}</button>
                                <button id="edit-cancel-btn" class="profiles-btn btn-secondary">${t('common.cancel')}</button>
                            </div>
                            ${isSub ? `
                                <button id="edit-delete-btn" class="profiles-btn btn-danger-quiet">${t('profileForm.deleteProfile')}</button>
                            ` : ''}
                        </div>
                    </div>
                `;

                // Setup active color dot selection
                const dots = content.querySelectorAll('.color-dot');
                let selectedColor = profile.avatarColor || DEFAULT_AVATAR_COLOR;
                dots.forEach(dot => {
                    const color = dot.getAttribute('data-color');
                    if (color.toLowerCase() === selectedColor.toLowerCase()) {
                        dot.classList.add('active');
                    }
                    dot.addEventListener('click', () => {
                        dots.forEach(d => d.classList.remove('active'));
                        dot.classList.add('active');
                        selectedColor = color;
                        const editPreview = document.getElementById('edit-image-upload-preview');
                        if (editPreview && !editPreview.querySelector('img')) {
                            editPreview.style.backgroundColor = selectedColor;
                        }
                    });
                });

                // Remove now lives inside the picker, next to Change picture, and shows
                // itself from setPreview — so there is nothing left to wire here.
                const avatarPicker = this.initAvatarPicker(
                    content, 'edit', avatarLibrary, profile.profileImage,
                    (src) => {
                        // Colour dimming is the picker's own business now; this callback
                        // is only here to put the initial back when the picture goes.
                        const preview = document.getElementById('edit-image-upload-preview');
                        if (preview && !src) preview.innerHTML = escapeHtml(profile.avatarInitial);
                    },
                    { enabled: !!profile.transparentAvatar });

                // Support D-pad Enter/Space select on color dots
                content.addEventListener('keydown', (e) => {
                    if ((e.key === 'Enter' || e.key === ' ') && e.target.classList.contains('color-dot')) {
                        e.preventDefault();
                        e.target.click();
                    }
                });

                // Select all libraries logic for edit
                const selectAllCheckbox = document.getElementById('edit-select-all-libraries');
                const libCheckboxes = content.querySelectorAll('.library-checkbox');
                if (selectAllCheckbox) {
                    const allChecked = libCheckboxes.length > 0 && Array.from(libCheckboxes).every(c => c.checked);
                    selectAllCheckbox.checked = allChecked;

                    selectAllCheckbox.addEventListener('change', (e) => {
                        const isChecked = e.target.checked;
                        libCheckboxes.forEach(cb => {
                            cb.checked = isChecked;
                        });
                        this.syncAllLibraryRows(content);
                    });

                    libCheckboxes.forEach(cb => {
                        cb.addEventListener('change', () => {
                            const allChecked = Array.from(libCheckboxes).every(c => c.checked);
                            selectAllCheckbox.checked = allChecked;
                            this.syncLibraryRowState(cb.closest('.libart-row'));
                        });
                    });
                }

                // The tick and the artwork controls share a row now, so the artwork half
                // has to start out matching the tick rather than waiting for a change.
                this.syncAllLibraryRows(content);

                // Devices dropdown logic for edit
                const editTrigger = document.getElementById('devices-dropdown-trigger');
                const editList = document.getElementById('devices-dropdown-list');
                if (editTrigger && editList) {
                    // Keep aria-expanded in step with the visual state — see the create form.
                    const setEditOpen = (open) => {
                        editList.style.display = open ? 'block' : 'none';
                        editTrigger.setAttribute('aria-expanded', open ? 'true' : 'false');
                    };
                    editTrigger.addEventListener('click', (e) => {
                        e.stopPropagation();
                        setEditOpen(editList.style.display === 'none');
                    });
                    editTrigger.addEventListener('keydown', (e) => {
                        if (e.key === 'Enter' || e.key === ' ') {
                            e.preventDefault();
                            editTrigger.click();
                        } else if (e.key === 'Escape') {
                            setEditOpen(false);
                        }
                    });
                    this.addManagedDocumentListener('click', () => setEditOpen(false));
                    editList.addEventListener('click', (e) => {
                        e.stopPropagation();
                    });
                    editList.addEventListener('keydown', (e) => {
                        if (e.key === 'Enter' && e.target.type === 'checkbox') {
                            e.preventDefault();
                            e.target.checked = !e.target.checked;
                            e.target.dispatchEvent(new Event('change'));
                        }
                    });
                }

                const updateSelectedText = () => {
                    const checked = Array.from(content.querySelectorAll('.device-checkbox:checked'));
                    const txt = document.getElementById('devices-dropdown-selected-text');
                    if (txt) {
                        if (checked.length === 0) {
                            txt.textContent = t('profileForm.allDevicesAllowed');
                        } else if (checked.length === 1) {
                            txt.textContent = t('profileForm.oneDeviceAllowed');
                        } else {
                            txt.textContent = t('profileForm.devicesAllowed', { count: checked.length });
                        }
                    }
                };
                content.querySelectorAll('.device-checkbox').forEach(cb => {
                    cb.addEventListener('change', updateSelectedText);
                });
                updateSelectedText();

                // Device deletion handler
                content.querySelectorAll('.device-delete-btn').forEach(btn => {
                    btn.addEventListener('click', (e) => {
                        e.preventDefault();
                        e.stopPropagation();
                        const devId = btn.getAttribute('data-id');
                        this.showConfirmDialog(t('profileForm.deleteDeviceTitle'), t('profileForm.deleteDeviceMessage'), () => {
                            const delDevUrl = apiClient.getUrl('plugins/profiles/devices/delete');
                            fetch(delDevUrl, {
                                method: 'POST',
                                headers: {
                                    'Content-Type': 'application/json',
                                    ...this.getAuthHeaders(masterState.masterToken)
                                },
                                body: JSON.stringify({ deviceId: devId })
                            })
                            .then(res => {
                                if (res.ok) {
                                    // The device list is part of the account-wide cache the
                                    // forms share. Removing only the row would let the next
                                    // form re-list the device it just deleted, and allow it
                                    // to be ticked as permitted.
                                    this.clearSharedFormData();

                                    const row = btn.closest('.device-dropdown-item');
                                    if (row) row.remove();
                                    const remaining = editList.querySelectorAll('.device-dropdown-item');
                                    if (remaining.length === 0) {
                                        editList.innerHTML = '<div style="padding: 12px; text-align: center; opacity: 0.6; font-size: 0.9rem;">' + t('profileForm.noDevicesConnected') + '</div>';
                                    }
                                    updateSelectedText();
                                    return null;
                                }
                                // The server refuses this when the device is the only one a
                                // profile is allowed on, and says which profiles those are —
                                // forgetting it would empty their whitelist, and an empty
                                // whitelist means "any device". Showing the generic failure
                                // instead threw that away and left the administrator with a
                                // button that just did not work.
                                return res.text().then(body => {
                                    this.showAlert(
                                        t('errors.error'),
                                        (body && body.trim()) || t('errors.failedDeleteDevice'));
                                });
                            })
                            .catch(err => this.showAlert(t('errors.error'), t('errors.withMessage', { message: err.message })));
                        });
                    });
                });

                // ── Renaming and merging ────────────────────────────────────────────
                //
                // Both post and then re-open the form, rather than patching the row in
                // place: a merge changes which devices exist and which of them this profile
                // is allowed on, and the checkbox states are rebuilt from that. Patching
                // would leave the ticks describing a list that no longer exists.
                const deviceAction = (path, body, failKey) => {
                    // Some of these take their argument in the query string and no body at
                    // all. Sending the four bytes "null" for those is not harmless: ASP.NET
                    // binds it as an explicit null, which is a different thing from absent.
                    const init = {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json',
                            ...this.getAuthHeaders(masterState.masterToken)
                        }
                    };
                    if (body !== null && body !== undefined) init.body = JSON.stringify(body);

                    fetch(apiClient.getUrl('plugins/profiles/devices/' + path), init)
                    .then(res => {
                        if (res.ok) {
                            this.clearSharedFormData();
                            this.showEditProfileModal(profile);
                            return null;
                        }
                        return res.text().then(text => {
                            this.showAlert(t('errors.error'), (text && text.trim()) || t(failKey));
                        });
                    })
                    .catch(err => this.showAlert(t('errors.error'), t('errors.withMessage', { message: err.message })));
                };

                content.querySelectorAll('.device-rename-btn').forEach(btn => {
                    btn.addEventListener('click', (e) => {
                        e.preventDefault();
                        e.stopPropagation();
                        this.showPromptDialog(
                            t('profileForm.renameDeviceTitle'),
                            t('profileForm.renameDeviceLabel'),
                            btn.getAttribute('data-name'),
                            (name) => deviceAction(
                                'rename',
                                { deviceId: btn.getAttribute('data-id'), deviceName: name },
                                'errors.failedRenameDevice'));
                    });
                });

                // One browser that has minted a new id every time its site data was cleared
                // leaves four or five identical-looking rows, and merging them a pair at a
                // time is the kind of chore nobody finishes. Two calls: the first only
                // reports, the second acts.
                const tidyBtn = content.querySelector('#devices-tidy-btn');
                if (tidyBtn) {
                    tidyBtn.addEventListener('click', (e) => {
                        e.preventDefault();
                        e.stopPropagation();
                        const url = apiClient.getUrl('plugins/profiles/devices/merge-duplicates');
                        fetch(url, {
                            method: 'POST',
                            headers: {
                                'Content-Type': 'application/json',
                                ...this.getAuthHeaders(masterState.masterToken)
                            }
                        })
                        .then(res => res.json())
                        .then(data => {
                            const count = (data.groups || [])
                                .reduce((n, g) => n + (g.merge ? g.merge.length : 0), 0);
                            if (!count) {
                                this.showAlert(t('profileForm.tidyDevices'), t('profileForm.tidyNone'));
                                return;
                            }
                            this.showConfirmDialog(
                                t('profileForm.tidyDevices'),
                                t('profileForm.tidyFound', { count: count }),
                                () => deviceAction('merge-duplicates?apply=true', null,
                                                   'errors.failedMergeDevice'));
                        })
                        .catch(err => this.showAlert(t('errors.error'),
                            t('errors.withMessage', { message: err.message })));
                    });
                }

                content.querySelectorAll('.device-merge-btn').forEach(btn => {
                    btn.addEventListener('click', (e) => {
                        e.preventDefault();
                        e.stopPropagation();

                        const fromId = btn.getAttribute('data-id');
                        const others = Array.from(content.querySelectorAll('.device-dropdown-item'))
                            .map(row => ({
                                id: row.getAttribute('data-device-id'),
                                name: (row.querySelector('.device-row-name') || {}).textContent || ''
                            }))
                            .filter(d => d.id && d.id !== fromId);

                        if (others.length === 0) return;

                        this.showSelectDialog(
                            t('profileForm.mergeDeviceTitle'),
                            t('profileForm.mergeDeviceMessage'),
                            others,
                            (intoId) => deviceAction(
                                'merge',
                                { fromDeviceId: fromId, intoDeviceId: intoId },
                                'errors.failedMergeDevice'));
                    });
                });

                // Clear PIN logic
                let isPinCleared = false;
                const clearPinBtn = document.getElementById('edit-clear-pin-btn');
                if (clearPinBtn) {
                    clearPinBtn.addEventListener('click', (e) => {
                        e.preventDefault();
                        isPinCleared = true;
                        document.getElementById('edit-pin-input').value = '';
                        document.getElementById('edit-pin-input').placeholder = t('profileForm.pinPlaceholderUnprotected');
                        clearPinBtn.style.display = 'none';
                    });
                }


                // Save handler
                document.getElementById('edit-submit-btn').addEventListener('click', () => {
                    const name = document.getElementById('edit-name-input').value.trim();
                    const pinVal = document.getElementById('edit-pin-input').value;
                    const bypassPin = document.getElementById('edit-local-bypass-checkbox').checked;
                    
                    let rating = null;
                    let checkedLibs = null;
                    let checkedDevices = null;
                    // Null for the master profile, which has no tag editor — the server reads
                    // null as "leave unchanged".
                    let blockedTags = null;
                    let allowedTags = null;
                    if (!profile.isMaster) {
                        blockedTags = this.getTagEditorValues(content, 'edit-blocked-tags');
                        allowedTags = this.getTagEditorValues(content, 'edit-allowed-tags');
                        rating = document.getElementById('edit-rating-select').value;
                        const rawLibs = [];
                        content.querySelectorAll('.library-checkbox:checked').forEach(cb => {
                            rawLibs.push(cb.value);
                        });
                        // Send null (not empty array) when no libraries are checked.
                        // An empty array tells the server "allow no libraries",
                        // while null means "inherit all accessible libraries from master".
                        checkedLibs = rawLibs.length > 0 ? rawLibs : null;
                        checkedDevices = [];
                        content.querySelectorAll('.device-checkbox:checked').forEach(cb => {
                            checkedDevices.push(cb.value);
                        });
                    }
                    const lockoutSel = document.getElementById('edit-lockout-select');
                    const lockoutMinutes = lockoutSel ? parseInt(lockoutSel.value, 10) : undefined;

                    // Report validation failures inline next to the offending field as well as
                    // in a dialog. A dialog alone is fragile — when it was rendering behind the
                    // overlay this button looked completely dead on an invalid PIN.
                    const pinError = document.getElementById('edit-pin-error');
                    const showPinError = (msg) => {
                        if (pinError) {
                            pinError.textContent = msg;
                            pinError.style.display = 'block';
                        }
                        const input = document.getElementById('edit-pin-input');
                        if (input) {
                            input.style.borderColor = 'var(--jpf-danger)';
                            input.focus();
                        }
                    };
                    if (pinError) pinError.style.display = 'none';
                    const pinInputEl = document.getElementById('edit-pin-input');
                    if (pinInputEl) pinInputEl.style.borderColor = '';

                    if (!name) {
                        this.showAlert(t('errors.validationError'), t('profileForm.nameRequired'));
                        return;
                    }

                    let pin = null;
                    if (isPinCleared) {
                        pin = ''; // Tells backend to clear the PIN
                    } else if (pinVal) {
                        if (!/^\d+$/.test(pinVal)) {
                            showPinError(t('profileForm.pinMustBeDigits'));
                            return;
                        }
                        if (pinVal.length < 4 || pinVal.length > 8) {
                            showPinError(t('profileForm.pinLengthEdit', { count: pinVal.length }));
                            return;
                        }
                        pin = pinVal;
                    }

                    const updateUrl = apiClient.getUrl('plugins/profiles/update');
                    fetch(updateUrl, {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json',
                            ...this.getAuthHeaders(masterState.masterToken)
                        },
                        body: JSON.stringify({
                            profileId: profile.profileUserId,
                            profileName: name,
                            pin: pin,
                            avatarColor: selectedColor,
                            maxParentalRating: rating || null,
                            enabledFolders: checkedLibs,
                            blockedTags: blockedTags,
                            allowedTags: allowedTags,
                            masterPin: this.masterPin,
                            lockoutMinutes: lockoutMinutes,
                            bypassPinOnLocalNetwork: bypassPin,
                            allowedDeviceIds: checkedDevices,
                            profileImage: avatarPicker.get().image,
                            profileImageThumb: avatarPicker.get().thumb,
                            avatarLibraryId: avatarPicker.get().libraryId,
                            transparentAvatar: avatarPicker.get().transparent
                        })
                    })
                    .then(res => {
                        if (!res.ok) return res.text().then(text => { throw new Error(text); });
                        // Artwork is stored per library, so it is saved after the profile
                        // itself rather than folded into that one request.
                        return artworkEditor.save(profile.profileUserId).then(() => {
                            this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.masterToken, /* forceRefresh */ true);
                        });
                    })
                    .catch(err => this.showAlert(t('errors.error'), t('errors.savingProfile', { message: err.message })));
                });

                // Delete handler
                const delBtn = document.getElementById('edit-delete-btn');
                if (delBtn) {
                    delBtn.addEventListener('click', () => {
                        this.showConfirmDialog(t('profileForm.deleteProfile'), t('profileForm.deleteConfirmMessage', { name: escapeHtml(profile.profileName) }), () => {
                            this.executeProfileDeletion(profile.profileUserId);
                        });
                    });
                }
// Cancel handler
                document.getElementById('edit-cancel-btn').addEventListener('click', () => {
                    this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.masterToken);
                });
                const artworkEditor = this.initLibraryArtworkEditor(content, profile.profileUserId);
                this.initTVCheckboxes(content);
                this.initTagEditors(content);
            })
            .catch(err => {
                this.showAlert(t('errors.error'), t('errors.loadProfileDetails', { message: err.message }));
                this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.masterToken);
            });
        },

        showBonfireModal: function () {
            // Kept, so a slow render cannot draw over a screen the user moved on to.
            const navTicket = this.beginNavigation();
            const apiClient = ApiClient;
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            if (!masterState) return;

            const content = document.querySelector('.profiles-modal-content');
            if (!content) return;

            content.innerHTML = `
                <h1 class="profiles-title">${t('bonfire.yourBonfireTitle')}</h1>
                <div class="create-profile-container" style="max-width: 500px; width: 100%;">
                    <div id="bonfire-container" style="width: 100%; min-height: 100px; display: flex; flex-direction: column; gap: 1.5rem;">
                        <div style="display: flex; justify-content: center; padding: 20px;">
                            <div class="profiles-loading-spinner" style="border: 3px solid rgba(255,255,255,0.1); border-radius: 50%; border-top: 3px solid var(--jpf-accent); width: 24px; height: 24px; animation: jpfSpin 1s linear infinite;"></div>
                        </div>
                        <div class="bonfire-dialog-actions" style="margin-top: 2rem !important; display: flex !important; justify-content: center !important; width: 100% !important; box-sizing: border-box !important; position: relative !important; bottom: auto !important; left: auto !important; right: auto !important; top: auto !important;">
                            <button id="bonfire-back-btn" class="profiles-btn btn-secondary" style="padding: 10px 24px !important; font-size: 1rem !important; box-sizing: border-box !important; margin: 0 !important; display: inline-block !important; width: auto !important; flex: 0 0 auto !important; position: relative !important; bottom: auto !important; left: auto !important; right: auto !important; top: auto !important;">${t('common.back')}</button>
                        </div>
                    </div>
                </div>
            `;

            const attachBackBtnListener = () => {
                const btn = content.querySelector('#bonfire-back-btn');
                if (btn) {
                    btn.addEventListener('click', () => {
                        this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.masterToken, /* forceRefresh */ true);
                    });
                }
            };

            attachBackBtnListener();

            this.loadBonfireStatus(content, apiClient, masterState.masterToken);

            // Auto-focus first focusable element for TV D-pad navigation
            setTimeout(() => {
                const first = content.querySelector('input, button');
                if (first) first.focus();
            }, 250);
        },

        /// Lets the account holder choose between the forced gate and native-menu access.
        /// A per-account preference rather than a server setting — some households want the
        /// Netflix-style "Who's Watching?" screen, others find it intrusive, and neither
        /// answer should be imposed on the other by whoever runs the server.
        /// Everything that is not a profile.
        ///
        /// Bonfire has four scopes of setting: per profile (Edit Profile), per account
        /// (Switcher Style), between accounts (Your Bonfire) and server-wide (the
        /// dashboard). The middle two used to be cards in the profile grid, which is
        /// why they needed their own gradient tiles to avoid reading as people.
        showSettingsMenu: function () {
            this.beginNavigation();

            const content = document.querySelector('.profiles-modal-content');
            if (!content) return;

            const entry = (id, icon, title, body) => `
                <div class="settings-menu-entry focusable" id="${id}" tabindex="0" role="button" style="
                    display: flex; gap: var(--jpf-gap); text-align: left; padding: 16px;
                    border-radius: var(--jpf-r-md); cursor: pointer; box-sizing: border-box;
                    border: 2px solid rgba(255,255,255,0.08);
                    background: rgba(255,255,255,0.02);
                ">
                    <span class="material-icons" style="font-size: 2rem; color: rgba(255,255,255,0.5); flex-shrink: 0;">${icon}</span>
                    <div style="flex: 1 1 auto; min-width: 0;">
                        <div style="font-weight: 700; font-size: 1rem; margin-bottom: 4px;">${title}</div>
                        <div style="font-size: 0.85rem; opacity: 0.7; line-height: 1.5;">${body}</div>
                    </div>
                </div>
            `;

            content.innerHTML = `
                <h1 class="profiles-title">${t('settings.title')}</h1>
                <div class="create-profile-container" style="max-width: var(--jpf-w-form); width: 100%;">
                    <div style="display: flex; flex-direction: column; gap: var(--jpf-gap); width: 100%;">
                        ${entry('settings-switcher-style', 'switch_account', t('settings.switcherStyleTitle'),
                            t('settings.switcherStyleBody'))}
                        ${entry('settings-your-bonfire', 'local_fire_department', t('settings.yourBonfireTitle'),
                            t('settings.yourBonfireBody'))}
                    </div>
                    <div class="bonfire-dialog-actions" style="margin-top: 2rem !important; display: flex !important; justify-content: center !important; width: 100% !important;">
                        <button id="settings-back-btn" class="profiles-btn btn-secondary" style="padding: 10px 24px !important; font-size: 1rem !important; margin: 0 !important; width: auto !important;">${t('common.back')}</button>
                    </div>
                </div>
            `;

            const open = (id, fn) => {
                const el = content.querySelector('#' + id);
                if (!el) return;
                el.addEventListener('click', () => fn.call(this));
                el.addEventListener('keydown', (e) => {
                    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); el.click(); }
                });
            };
            open('settings-switcher-style', this.showSwitcherModeModal);
            open('settings-your-bonfire', this.showBonfireModal);

            const back = content.querySelector('#settings-back-btn');
            if (back) {
                back.addEventListener('click', () => {
                    const st = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
                    if (st) {
                        this.fetchAndRenderProfiles(ApiClient, st.masterUserId, st.masterToken, /* forceRefresh */ true);
                    }
                });
            }

        },

        showSwitcherModeModal: function () {
            this.beginNavigation();
            const apiClient = ApiClient;
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey) || 'null');
            if (!masterState) return;

            const content = document.querySelector('.profiles-modal-content');
            if (!content) return;

            const prefs = this.getSwitcherPrefs() || { askOnStartup: true, location: 'button' };

            // Two separate questions, deliberately not folded into one list of modes: the
            // combination people asked for in issue #14 — ask on startup, but switch from
            // Jellyfin's menu — is unreachable when it is a single choice.
            const locationOption = (value, icon, title, body) => `
                <div class="switcher-location-option focusable" data-location="${value}" tabindex="0" role="radio"
                     aria-checked="${prefs.location === value}" style="
                    display: flex; gap: var(--jpf-gap); text-align: left; padding: 16px;
                    border-radius: var(--jpf-r-md); cursor: pointer; box-sizing: border-box;
                    border: 2px solid ${prefs.location === value ? 'var(--jpf-accent)' : 'rgba(255,255,255,0.08)'};
                    background: ${prefs.location === value ? 'var(--jpf-accent-a08)' : 'rgba(255,255,255,0.02)'};
                ">
                    <span class="material-icons" style="font-size: 2rem; color: ${prefs.location === value ? 'var(--jpf-accent)' : 'rgba(255,255,255,0.5)'}; flex-shrink: 0;">${icon}</span>
                    <div style="flex: 1 1 auto; min-width: 0;">
                        <div style="font-weight: 700; font-size: 1rem; margin-bottom: 4px;">${title}</div>
                        <div style="font-size: 0.85rem; opacity: 0.7; line-height: 1.5;">${body}</div>
                    </div>
                </div>
            `;

            content.innerHTML = `
                <h1 class="profiles-title">${t('switcher.title')}</h1>
                <div class="create-profile-container" style="max-width: 560px; width: 100%;">
                    <p style="opacity: 0.75; font-size: 0.9rem; line-height: 1.5; margin: 0 0 1.5rem 0; text-align: left;">
                        ${t('switcher.intro')}
                    </p>

                    <div class="bonfire-form-group" style="gap: 4px; text-align: left; margin-bottom: 1.5rem;">
                        <label class="library-check-label" style="display: inline-flex !important; align-items: center !important; gap: 0.5rem !important; cursor: pointer !important; user-select: none !important; font-size: 0.95rem !important; font-weight: 600 !important; position: relative !important;">
                            <input type="checkbox" id="switcher-ask-startup" ${prefs.askOnStartup ? 'checked' : ''} style="cursor: pointer !important; accent-color: var(--jpf-accent) !important; position: relative !important; opacity: 1 !important; width: 18px !important; height: 18px !important; margin: 0 !important; padding: 0 !important; flex-shrink: 0 !important;" />
                            <span>${t('switcher.askOnStartup')}</span>
                        </label>
                        <div class="form-hint" style="margin-left: 1.6rem !important; opacity: 0.5 !important; font-size: 0.78rem !important; position: relative !important; display: block !important;">
                            ${t('switcher.askOnStartupHint')}
                        </div>
                    </div>

                    <div style="text-align: left; font-size: 0.95rem; font-weight: 600; margin-bottom: 10px;">${t('switcher.whereToSwitchFrom')}</div>
                    <div role="radiogroup" style="display: flex; flex-direction: column; gap: var(--jpf-gap); width: 100%;">
                        ${locationOption('button', 'account_circle', t('switcher.bonfireButtonTitle'),
                            t('switcher.bonfireButtonBody'))}
                        ${locationOption('menu', 'switch_account', t('switcher.menuTitle'),
                            t('switcher.menuBody'))}
                    </div>

                    <div id="switcher-mode-error" style="display: none; color: var(--jpf-danger); font-size: 0.85rem; font-weight: 600; margin-top: 12px;"></div>
                    <div class="bonfire-dialog-actions" style="margin-top: 2rem !important; display: flex !important; justify-content: center !important; width: 100% !important;">
                        <button id="switcher-mode-back-btn" class="profiles-btn btn-secondary" style="padding: 10px 24px !important; font-size: 1rem !important; margin: 0 !important; width: auto !important;">${t('common.done')}</button>
                    </div>
                </div>
            `;

            // Back to Settings, which is where this was opened from.
            const goBack = () => this.showSettingsMenu();

            const backBtn = content.querySelector('#switcher-mode-back-btn');
            if (backBtn) backBtn.addEventListener('click', goBack);

            const errDiv = content.querySelector('#switcher-mode-error');
            const askCb = content.querySelector('#switcher-ask-startup');

            const save = (askOnStartup, location) => {
                errDiv.style.display = 'none';
                return fetch(apiClient.getUrl('plugins/profiles/preferences'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...this.getAuthHeaders(masterState.masterToken)
                    },
                    body: JSON.stringify({ askOnStartup: askOnStartup, switcherLocation: location })
                })
                .then(res => res.ok ? res.json() : Promise.reject(new Error(t('errors.couldNotSaveThat'))))
                .then(saved => {
                    const ask = (saved.askOnStartup !== undefined ? saved.askOnStartup : saved.AskOnStartup) === true;
                    const loc = (saved.switcherLocation || saved.SwitcherLocation) === 'menu' ? 'menu' : 'button';
                    this._cacheSwitcherPrefs(ask, loc, masterState.masterUserId);
                    return { askOnStartup: ask, location: loc };
                })
                .catch(err => {
                    errDiv.textContent = err.message || t('errors.couldNotSaveThat');
                    errDiv.style.display = 'block';
                    return null;
                });
            };

            // getSwitcherPrefs() returns null until the account's preferences have loaded,
            // which is reachable here — init() runs before sign-in on a fresh browser, and
            // only the home route retries. Fall back to what the form was rendered with
            // rather than throwing and leaving the controls looking dead.
            const currentPrefs = () => this.getSwitcherPrefs() || prefs;

            if (askCb) {
                askCb.addEventListener('change', () => {
                    save(askCb.checked, currentPrefs().location).then(applied => {
                        // Revert the box if the server refused, so it cannot sit there
                        // showing a state that was never stored.
                        if (!applied) askCb.checked = !askCb.checked;
                    });
                });
            }

            content.querySelectorAll('.switcher-location-option').forEach(el => {
                const choose = () => {
                    const location = el.getAttribute('data-location');
                    if (location === currentPrefs().location) return;

                    save(askCb ? askCb.checked : prefs.askOnStartup, location).then(applied => {
                        if (!applied) return;
                        // Re-render so the selected card updates, and so the floating button
                        // appears or disappears immediately rather than on the next poll.
                        this.showSwitcherModeModal();
                        this.checkRoute();
                    });
                };

                el.addEventListener('click', choose);
                el.addEventListener('keydown', (e) => {
                    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); choose(); }
                });
            });

            this.initTVCheckboxes(content);

            setTimeout(() => {
                const first = content.querySelector('#switcher-ask-startup');
                if (first) first.focus();
            }, 250);
        },

        executeProfileDeletion: function (profileId) {
            const apiClient = ApiClient;
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            if (!masterState) return;

            const url = apiClient.getUrl('plugins/profiles/delete');

            fetch(url, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    ...this.getAuthHeaders(masterState.masterToken)
                },
                body: JSON.stringify({ 
                    profileId: profileId,
                    masterPin: this.masterPin
                })
            })
            .then(res => {
                if (!res.ok) throw new Error(t('errors.failedDeleteProfile'));
                this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.masterToken, /* forceRefresh */ true);
            })
            .catch(err => this.showAlert(t('errors.error'), t('errors.deletingProfile', { message: err.message })));
        },

        loadBonfireStatus: function (content, apiClient, masterToken) {
            const container = content.querySelector('#bonfire-container');
            if (!container) return;

            // Whatever owns the screen right now. If anything claims it before this
            // returns, the response is dropped rather than drawn over the newer screen —
            // the same guard the profile forms use.
            const ticket = this._navTicket;

            const statusUrl = apiClient.getUrl('plugins/profiles/bonfire/status');
            fetch(statusUrl, { headers: this.getAuthHeaders(masterToken) })
            .then(res => {
                if (res.status === 401) {
                    this.handleSessionExpired();
                    throw new Error(t('errors.unauthorized'));
                }
                return res.json();
            })
            .then(status => {
                if (!this.navIsCurrent(ticket)) return;
                this.renderBonfireStatus(container, content, status, apiClient, masterToken);
            })
            .catch(err => {
                if (!this.navIsCurrent(ticket)) return;
                container.innerHTML = `<div style="color: var(--jpf-danger); font-size: 0.9rem;">${t('bonfire.failedLoadStatus', { message: err.message })}</div>`;
            });
        },

        renderBonfireStatus: function (container, content, status, apiClient, masterToken) {
            const isOwner = status.isOwner !== undefined ? status.isOwner : status.IsOwner;
            const isMember = status.isMember !== undefined ? status.isMember : status.IsMember;
            const ownedCode = status.ownedCode || status.OwnedCode || '';
            const ownedMembers = status.ownedMembers || status.OwnedMembers || [];
            const joinedOwnerName = status.joinedOwnerName || status.JoinedOwnerName || '';

            let hostSectionHtml = '';
            if (isOwner) {
                hostSectionHtml = `
                    <div style="display: flex; flex-direction: column; gap: var(--jpf-gap-lg); border-bottom: 1px solid rgba(255,255,255,0.08); padding-bottom: 1.5rem;">
                        <div class="bonfire-form-group">
                            <label style="font-size: 1.1rem; font-weight: 700; display: block; margin-bottom: 4px;">${t('bonfire.hostedTitle')}</label>
                            <span style="font-size: 0.88rem; opacity: 0.75; display: block;">${t('bonfire.shareCode')}</span>
                            <div style="font-size: 2rem; font-weight: 700; letter-spacing: 4px; margin: 12px 0; font-family: monospace; text-align: center; background: rgba(0,0,0,0.3); padding: 12px; border-radius: var(--jpf-r-md); border: 1px solid var(--jpf-accent-a30);">${ownedCode}</div>
                        </div>

                        <div class="bonfire-form-group">
                            <label style="font-size: 1rem; font-weight: 600; color: #fff; display: block; margin-bottom: 8px;">${t('bonfire.members', { count: ownedMembers.length })}</label>
                            <div style="display: flex; flex-direction: column; gap: 8px; max-height: 180px; overflow-y: auto; background: rgba(0,0,0,0.2); border: 1px solid rgba(255,255,255,0.06); border-radius: var(--jpf-r-md); padding: 8px;">
                                ${ownedMembers.length > 0 ? ownedMembers.map(m => {
                                    const mUserId = m.userId || m.UserId;
                                    const mUsername = m.username || m.Username || t('bonfire.unknownUser');
                                    return `
                                    <div style="display: flex; align-items: center; justify-content: space-between; padding: 8px 12px; background: rgba(255,255,255,0.03); border-radius: var(--jpf-r-sm);">
                                        <span style="font-size: 0.95rem; font-weight: 500;">${mUsername}</span>
                                        <button type="button" class="bonfire-kick-btn" data-id="${mUserId}" style="background: var(--jpf-danger) !important; border: none !important; color: #fff !important; padding: 6px 12px !important; border-radius: var(--jpf-r-sm) !important; font-size: 0.85rem !important; cursor: pointer !important; font-weight: 600 !important; transition: background-color 0.2s !important; margin: 0 !important; box-sizing: border-box !important;">${t('bonfire.kick')}</button>
                                    </div>
                                    `;
                                }).join('') : '<div style="font-size: 0.9rem; opacity: 0.5; font-style: italic; text-align: center; padding: 12px;">' + t('bonfire.noMembersYet') + '</div>'}
                            </div>
                        </div>
                        <div style="display: flex; justify-content: flex-end;">
                            <button type="button" id="bonfire-delete-btn" class="profiles-btn btn-danger-quiet" style="padding: 10px 20px !important; font-size: 0.95rem !important; box-sizing: border-box !important; margin: 0 !important; display: inline-block !important;">${t('bonfire.deleteGroup')}</button>
                        </div>
                    </div>
                `;
            } else {
                hostSectionHtml = `
                    <div class="bonfire-form-group" style="border-bottom: 1px solid rgba(255,255,255,0.08); padding-bottom: 1.5rem;">
                        <label style="font-size: 1.1rem; font-weight: 700; display: block; margin-bottom: 4px;">${t('bonfire.hostTitle')}</label>
                        <span style="font-size: 0.88rem; opacity: 0.75; display: block; margin-bottom: 12px;">${t('bonfire.hostBody')}</span>
                        <button type="button" id="bonfire-generate-btn" class="profiles-btn btn-primary" style="width: 100% !important; padding: 12px !important; font-weight: 600 !important; box-sizing: border-box !important; display: block !important; margin: 8px 0 !important;">${t('bonfire.generateJoinCode')}</button>
                    </div>
                `;
            }

            let guestSectionHtml = '';
            if (isMember) {
                guestSectionHtml = `
                    <div style="display: flex; flex-direction: column; gap: var(--jpf-gap-lg); border-bottom: 1px solid rgba(255,255,255,0.08); padding-bottom: 1.5rem;">
                        <div class="bonfire-form-group">
                            <label style="font-size: 1.1rem; font-weight: 700; display: block; margin-bottom: 4px;">${t('bonfire.joinedTitle')}</label>
                            <span style="font-size: 0.88rem; opacity: 0.75;">${t('bonfire.joinedOwnedBy')}</span>
                            <div style="font-size: 1.25rem; font-weight: 700; color: var(--jpf-accent); margin: 12px 0; background: rgba(0,0,0,0.2); padding: 12px; border-radius: var(--jpf-r-md); border: 1px solid rgba(255,255,255,0.05); text-align: center;">${joinedOwnerName}</div>
                            <span style="font-size: 0.85rem; opacity: 0.6; display: block; margin-top: -4px;">${t('bonfire.accessEachOther')}</span>
                        </div>
                        <div style="display: flex; justify-content: flex-end;">
                            <button type="button" id="bonfire-leave-btn" class="profiles-btn btn-danger-quiet" style="padding: 10px 20px !important; font-size: 0.95rem !important; box-sizing: border-box !important; margin: 0 !important; display: inline-block !important;">${t('bonfire.leaveGroup')}</button>
                        </div>
                    </div>
                `;
            } else {
                guestSectionHtml = `
                    <div class="bonfire-form-group" style="border-bottom: 1px solid rgba(255,255,255,0.08); padding-bottom: 1.5rem;">
                        <label style="font-size: 1.1rem; font-weight: 700; display: block; margin-bottom: 4px;">${t('bonfire.joinTitle')}</label>
                        <span style="font-size: 0.88rem; opacity: 0.75; display: block; margin-bottom: 12px;">${t('bonfire.joinBody')}</span>
                        <div style="display: flex !important; gap: var(--jpf-gap) !important; align-items: center !important; width: 100% !important; box-sizing: border-box !important; margin: 12px 0 !important;">
                            <input type="text" id="bonfire-join-input" placeholder="${t('bonfire.joinCodePlaceholder')}" maxlength="6" style="flex: 1 1 0% !important; min-width: 0 !important; text-align: center !important; text-transform: uppercase !important; font-family: monospace !important; letter-spacing: 2px !important; height: 44px !important; box-sizing: border-box !important; margin: 0 !important; padding: 10px !important;" />
                            <button type="button" id="bonfire-join-btn" class="profiles-btn btn-primary" style="flex: 0 0 auto !important; padding: 0 24px !important; height: 44px !important; display: inline-flex !important; align-items: center !important; justify-content: center !important; font-weight: 600 !important; margin: 0 !important; box-sizing: border-box !important;">${t('common.join')}</button>
                        </div>
                        <div id="bonfire-join-error" style="display: none; color: var(--jpf-danger); font-size: 0.85rem; font-weight: 600; margin-top: 8px; text-align: center;"></div>
                    </div>
                `;
            }

            const hideMine = status.hideMySubProfilesFromOthers || status.HideMySubProfilesFromOthers || false;
            const hideOthers = status.hideOthersSubProfilesFromMe || status.HideOthersSubProfilesFromMe || false;
            const lanBypass = status.allowHouseholdLanBypass || status.AllowHouseholdLanBypass || false;
            const isAdmin = status.isAdministrator || status.IsAdministrator || false;
            const hasPinSet = status.hasPin || status.HasPin || false;

            // Only worth showing once there is somebody to share with — on a standalone
            // account the setting has nothing to act on and just reads as a scary toggle.
            const lanBypassSectionHtml = (isOwner || isMember) ? `
                <div class="bonfire-form-group" style="gap: 4px; border-top: 1px solid rgba(255,255,255,0.08); padding-top: 16px;">
                    <label class="library-check-label" style="display: inline-flex !important; align-items: center !important; gap: 0.5rem !important; cursor: pointer !important; user-select: none !important; font-size: 0.9rem !important; font-weight: 600 !important; position: relative !important;">
                        <input type="checkbox" id="bonfire-lan-bypass-checkbox" ${lanBypass ? 'checked' : ''} style="cursor: pointer !important; accent-color: var(--jpf-warn) !important; position: relative !important; opacity: 1 !important; width: 18px !important; height: 18px !important; margin: 0 !important; padding: 0 !important; flex-shrink: 0 !important;" />
                        <span>${t('bonfire.lanSwitchLabel')}</span>
                    </label>
                    <div class="form-hint" style="margin-left: 1.6rem !important; opacity: 0.5 !important; font-size: 0.75rem !important; position: relative !important; display: block !important;">
                        ${t('bonfire.lanSwitchHint', { extra: hasPinSet ? '' : t('bonfire.lanSwitchHintExtra') })}
                    </div>
                    ${isAdmin ? `
                    <div style="margin-left: 1.6rem; margin-top: 8px; padding: 10px 12px; background: rgba(255,153,0,0.08); border-left: 3px solid var(--jpf-warn); border-radius: var(--jpf-r-sm); font-size: 0.75rem; line-height: 1.5; color: rgba(255,255,255,0.8);">
                        <strong style="color: var(--jpf-warn);">${t('bonfire.adminAccountWarning')}</strong> ${t('bonfire.adminAccountWarningBody')}
                    </div>` : ''}
                    <div style="margin-left: 1.6rem; margin-top: 8px; font-size: 0.72rem; line-height: 1.5; opacity: 0.45;">
                        ${t('bonfire.proxyHint')}
                    </div>
                </div>
            ` : '';

            const settingsSectionHtml = `
                <div class="bonfire-form-group" style="margin-top: 5px; display: flex; flex-direction: column; gap: var(--jpf-gap);">
                    <div class="bonfire-form-group" style="gap: 4px;">
                        <label class="library-check-label" style="display: inline-flex !important; align-items: center !important; gap: 0.5rem !important; cursor: pointer !important; user-select: none !important; font-size: 0.9rem !important; font-weight: 600 !important; position: relative !important;">
                            <input type="checkbox" id="bonfire-hide-mine-checkbox" ${hideMine ? 'checked' : ''} style="cursor: pointer !important; accent-color: var(--jpf-accent) !important; position: relative !important; opacity: 1 !important; width: 18px !important; height: 18px !important; margin: 0 !important; padding: 0 !important; flex-shrink: 0 !important;" />
                            <span>${t('bonfire.hideMine')}</span>
                        </label>
                        <div class="form-hint" style="margin-left: 1.6rem !important; opacity: 0.5 !important; font-size: 0.75rem !important; position: relative !important; display: block !important;">${t('bonfire.hideMineHint')}</div>
                    </div>

                    <div class="bonfire-form-group" style="gap: 4px;">
                        <label class="library-check-label" style="display: inline-flex !important; align-items: center !important; gap: 0.5rem !important; cursor: pointer !important; user-select: none !important; font-size: 0.9rem !important; font-weight: 600 !important; position: relative !important;">
                            <input type="checkbox" id="bonfire-hide-others-checkbox" ${hideOthers ? 'checked' : ''} style="cursor: pointer !important; accent-color: var(--jpf-accent) !important; position: relative !important; opacity: 1 !important; width: 18px !important; height: 18px !important; margin: 0 !important; padding: 0 !important; flex-shrink: 0 !important;" />
                            <span>${t('bonfire.hideOthers')}</span>
                        </label>
                        <div class="form-hint" style="margin-left: 1.6rem !important; opacity: 0.5 !important; font-size: 0.75rem !important; position: relative !important; display: block !important;">${t('bonfire.hideOthersHint')}</div>
                    </div>

                    ${lanBypassSectionHtml}

                    <!-- Saving these is a fetch that can fail. Without somewhere to say so,
                         a failure left the box sitting in its new position with the server
                         still holding the old value — the one case where that matters is the
                         LAN bypass, where the box can read "on" while nobody can actually
                         switch in, or the reverse. aria-live so a screen reader hears it
                         without the focus having to move. -->
                    <div id="bonfire-settings-status" role="status" aria-live="polite"
                         style="display: none; margin-left: 1.6rem; font-size: 0.78rem; line-height: 1.4;"></div>
                </div>
            `;

            container.innerHTML = `
                <div style="display: flex; flex-direction: column; gap: 1.5rem; width: 100%;">
                    ${hostSectionHtml}
                    ${guestSectionHtml}
                    ${settingsSectionHtml}
                    <div class="bonfire-dialog-actions" style="margin-top: 2rem !important; display: flex !important; justify-content: center !important; width: 100% !important; box-sizing: border-box !important; position: relative !important; bottom: auto !important; left: auto !important; right: auto !important; top: auto !important;">
                        <button id="bonfire-back-btn" class="profiles-btn btn-secondary" style="padding: 10px 24px !important; font-size: 1rem !important; box-sizing: border-box !important; margin: 0 !important; display: inline-block !important; width: auto !important; flex: 0 0 auto !important; position: relative !important; bottom: auto !important; left: auto !important; right: auto !important; top: auto !important;">${t('common.back')}</button>
                    </div>
                </div>
            `;

            // Event Listener: Back Button
            const backBtn = container.querySelector('#bonfire-back-btn');
            if (backBtn) {
                backBtn.addEventListener('click', () => {
                    // Back to Settings, which is where this was opened from.
                    this.showSettingsMenu();
                });
            }

            // Event Listeners: Kick Members
            container.querySelectorAll('.bonfire-kick-btn').forEach(btn => {
                btn.addEventListener('click', () => {
                    const mId = btn.getAttribute('data-id');
                    this.showConfirmDialog(t('bonfire.kickConfirmTitle'), t('bonfire.kickConfirmBody'), () => {
                        fetch(apiClient.getUrl('plugins/profiles/bonfire/kick'), {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json', ...this.getAuthHeaders(masterToken) },
                            body: JSON.stringify({ memberId: mId })
                        })
                        .then(res => {
                            if (res.ok) this.loadBonfireStatus(content, apiClient, masterToken);
                            else this.showAlert(t('errors.error'), t('bonfire.failedKick'));
                        })
                        .catch(err => this.showAlert(t('errors.error'), t('errors.withMessage', { message: err.message })));
                    });
                });
            });

            // Event Listeners: Delete Group
            const deleteBtn = container.querySelector('#bonfire-delete-btn');
            if (deleteBtn) {
                deleteBtn.addEventListener('click', () => {
                    this.showConfirmDialog(t('bonfire.deleteGroupConfirmTitle'), t('bonfire.deleteGroupConfirmBody'), () => {
                        fetch(apiClient.getUrl('plugins/profiles/bonfire/delete-group'), {
                            method: 'POST',
                            headers: this.getAuthHeaders(masterToken)
                        })
                        .then(res => {
                            if (res.ok) this.loadBonfireStatus(content, apiClient, masterToken);
                            else this.showAlert(t('errors.error'), t('bonfire.failedDeleteGroup'));
                        })
                        .catch(err => this.showAlert(t('errors.error'), t('errors.withMessage', { message: err.message })));
                    });
                });
            }

            // Event Listeners: Leave Group
            const leaveBtn = container.querySelector('#bonfire-leave-btn');
            if (leaveBtn) {
                leaveBtn.addEventListener('click', () => {
                    this.showConfirmDialog(t('bonfire.leaveGroupConfirmTitle'), t('bonfire.leaveGroupConfirmBody'), () => {
                        fetch(apiClient.getUrl('plugins/profiles/bonfire/leave'), {
                            method: 'POST',
                            headers: this.getAuthHeaders(masterToken)
                        })
                        .then(res => {
                            if (res.ok) this.loadBonfireStatus(content, apiClient, masterToken);
                            else this.showAlert(t('errors.error'), t('bonfire.failedLeaveGroup'));
                        })
                        .catch(err => this.showAlert(t('errors.error'), t('errors.withMessage', { message: err.message })));
                    });
                });
            }

            // Event Listeners: Generate Code
            const generateBtn = container.querySelector('#bonfire-generate-btn');
            if (generateBtn) {
                generateBtn.addEventListener('click', () => {
                    // Disable immediately to prevent double-fire on slow connections
                    generateBtn.disabled = true;
                    generateBtn.textContent = t('bonfire.generating');
                    fetch(apiClient.getUrl('plugins/profiles/bonfire/generate'), {
                        method: 'POST',
                        headers: masterToken ? this.getAuthHeaders(masterToken) : {}
                    })
                    .then(res => {
                        if (res.ok) this.loadBonfireStatus(content, apiClient, masterToken);
                        else return res.text().then(text => { throw new Error(text); });
                    })
                    .catch(err => {
                        generateBtn.disabled = false;
                        generateBtn.textContent = t('bonfire.generateJoinCode');
                        this.showAlert(t('errors.error'), t('bonfire.failedGenerateCode', { message: err.message }));
                    });
                });
            }

            // Event Listeners: Join Group
            const joinInput = container.querySelector('#bonfire-join-input');
            const joinBtn = container.querySelector('#bonfire-join-btn');
            const errDiv = container.querySelector('#bonfire-join-error');
            if (joinBtn && joinInput && errDiv) {
                const performJoin = () => {
                    const code = joinInput.value.trim();
                    errDiv.style.display = 'none';
                    if (!code || code.length !== 6) {
                        errDiv.textContent = t('bonfire.pleaseEnterCode');
                        errDiv.style.display = 'block';
                        return;
                    }
                    // The server allows membership in only one group, so joining silently
                    // drops the current one. Confirm first rather than surprising the user.
                    if (isMember) {
                        this.showConfirmDialog(
                            t('bonfire.leaveCurrentTitle'),
                            t('bonfire.leaveCurrentBody'),
                            () => submitJoin(code));
                        return;
                    }
                    submitJoin(code);
                };

                const submitJoin = (code) => {
                    errDiv.style.display = 'none';
                    // Disable to prevent double-submit; re-enable on all error paths
                    joinBtn.disabled = true;
                    joinBtn.textContent = t('bonfire.joining');
                    fetch(apiClient.getUrl('plugins/profiles/bonfire/join'), {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json', ...this.getAuthHeaders(masterToken) },
                        body: JSON.stringify({ code: code })
                    })
                    .then(res => {
                        if (res.status === 429) {
                            errDiv.textContent = t('bonfire.tooManyJoinAttempts');
                            errDiv.style.display = 'block';
                            joinBtn.disabled = false;
                            joinBtn.textContent = t('common.join');
                            return;
                        }
                        if (!res.ok) return res.text().then(text => { throw new Error(text); });
                        this.loadBonfireStatus(content, apiClient, masterToken);
                    })
                    .catch(err => {
                        errDiv.textContent = err.message || t('bonfire.failedToJoin');
                        errDiv.style.display = 'block';
                        joinBtn.disabled = false;
                        joinBtn.textContent = t('common.join');
                    });
                };

                joinBtn.addEventListener('click', performJoin);
                joinInput.addEventListener('keydown', (e) => {
                    if (e.key === 'Enter') performJoin();
                });
            }

            // Settings checkbox listeners — debounced 300ms to prevent race conditions
            // when the user toggles both checkboxes in quick succession.
            const hideMineCb = container.querySelector('#bonfire-hide-mine-checkbox');
            const hideOthersCb = container.querySelector('#bonfire-hide-others-checkbox');
            const lanBypassCb = container.querySelector('#bonfire-lan-bypass-checkbox');
            let _settingsDebounceTimer = null;
            const statusDiv = container.querySelector('#bonfire-settings-status');

            // The last state the server acknowledged. A failed save is reverted to this, so
            // the boxes always show what is actually stored rather than what was attempted.
            // Seeded from the values this panel was rendered with, which came from the server.
            const lastSaved = {
                hideMine: hideMineCb ? hideMineCb.checked : false,
                hideOthers: hideOthersCb ? hideOthersCb.checked : false,
                lanBypass: lanBypassCb ? lanBypassCb.checked : false
            };

            const settingsBoxes = [hideMineCb, hideOthersCb, lanBypassCb].filter(Boolean);

            const setStatus = (message, ok) => {
                if (!statusDiv) return;
                statusDiv.textContent = message;
                statusDiv.style.color = ok ? 'var(--jpf-accent)' : 'var(--jpf-danger)';
                statusDiv.style.display = 'block';
                statusDiv.style.opacity = ok ? '0.75' : '1';
            };

            let _statusClearTimer = null;
            const flashSaved = () => {
                setStatus(t('bonfire.settingsSaved'), true);
                clearTimeout(_statusClearTimer);
                _statusClearTimer = setTimeout(() => {
                    if (statusDiv) statusDiv.style.display = 'none';
                }, 2500);
            };

            const saveSettings = () => {
                clearTimeout(_settingsDebounceTimer);
                _settingsDebounceTimer = setTimeout(() => {
                    const hideMineVal = hideMineCb ? hideMineCb.checked : false;
                    const hideOthersVal = hideOthersCb ? hideOthersCb.checked : false;
                    const lanBypassVal = lanBypassCb ? lanBypassCb.checked : false;

                    const body = {
                        hideMySubProfilesFromOthers: hideMineVal,
                        hideOthersSubProfilesFromMe: hideOthersVal
                    };
                    // Omitted rather than sent as false when the toggle is not on screen, so a
                    // standalone account saving the hide flags cannot clear a setting it never
                    // rendered. The server treats a missing value as "leave alone".
                    if (lanBypassCb) body.allowHouseholdLanBypass = lanBypassVal;

                    // Locked while the request is in flight. Toggling again mid-save would
                    // queue a second write whose result arrives in whatever order the network
                    // decides, and a revert would then restore a state neither click asked for.
                    settingsBoxes.forEach(cb => { cb.disabled = true; });
                    clearTimeout(_statusClearTimer);

                    const finish = () => { settingsBoxes.forEach(cb => { cb.disabled = false; }); };

                    fetch(apiClient.getUrl('plugins/profiles/bonfire/settings'), {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json',
                            ...this.getAuthHeaders(masterToken)
                        },
                        body: JSON.stringify(body)
                    })
                    .then(res => {
                        if (!res.ok) throw new Error('HTTP ' + res.status);
                        lastSaved.hideMine = hideMineVal;
                        lastSaved.hideOthers = hideOthersVal;
                        lastSaved.lanBypass = lanBypassVal;
                        finish();
                        flashSaved();
                    })
                    .catch(err => {
                        // This used to be a console.error and nothing else. The box stayed
                        // where the user put it while the server still held the old value —
                        // and for the LAN bypass that means the panel can read "household
                        // switching allowed" when it is not, or the other way round. Whichever
                        // way it lands, the person believes a security setting that is not true.
                        console.error('Error saving Bonfire settings:', err);
                        if (hideMineCb) hideMineCb.checked = lastSaved.hideMine;
                        if (hideOthersCb) hideOthersCb.checked = lastSaved.hideOthers;
                        if (lanBypassCb) lanBypassCb.checked = lastSaved.lanBypass;
                        finish();
                        setStatus(t('bonfire.settingsSaveFailed'), false);
                    });
                }, 300);
            };

            if (hideMineCb) hideMineCb.addEventListener('change', saveSettings);
            if (hideOthersCb) hideOthersCb.addEventListener('change', saveSettings);

            if (lanBypassCb) {
                lanBypassCb.addEventListener('change', () => {
                    // Turning it off is always safe and saves straight away. Turning it on
                    // widens who can reach the account, so it is confirmed first — the checkbox
                    // reverts if the user backs out, otherwise it would look enabled while the
                    // server still had it off.
                    if (!lanBypassCb.checked) {
                        saveSettings();
                        return;
                    }

                    const adminLine = isAdmin
                        ? `<br><br><strong style="color:var(--jpf-warn);">${t('bonfire.adminAccountWarning')}</strong> ${t('bonfire.adminAccountWarningBody')}`
                        : '';

                    this.showConfirmDialog(
                        t('bonfire.allowHouseholdTitle'),
                        t('bonfire.allowHouseholdBody') + adminLine,
                        () => saveSettings(),
                        () => { lanBypassCb.checked = lastSaved.lanBypass; }
                    );
                });
            }

            this.initTVCheckboxes(container);

            // TV D-pad Auto-focus helper
            setTimeout(() => {
                const target = container.querySelector('input, button');
                if (target) target.focus();
            }, 100);
        },


        // ── Native-mode entry points ───────────────────────────────────────────────
        // Native mode drops the forced gate, so the switcher has to be reachable from
        // Jellyfin's own interface instead. Two places, deliberately:
        //
        //   1. The user menu, which is what issue #8 actually asks for.
        //   2. The profile page, as the fallback — the menu is built dynamically by React
        //      and every selector below is a guess about markup we do not control. When a
        //      theme defeats the menu injection, the profile page still works.
        //
        // Both are additive: nothing is removed from Jellyfin's own UI, so a failed match
        // costs a missing entry rather than a broken menu.

        // Jellyfin 10.11's user menu is AppUserMenu.tsx — a MUI <Menu id="app-user-menu">
        // of <MenuItem> rows, NOT the .actionSheet component older builds used. It also
        // sets keepMounted, so the menu lives in the DOM from first render even while
        // closed, which is why this can be a plain periodic check rather than an observer
        // racing the menu open.
        //
        // Everything below degrades to "no entry appears" if the markup moves. The
        // #/userprofile section is the deliberate fallback for that case.
        USER_MENU_SELECTOR: '#app-user-menu',

        /// Re-asserts the "Switch Profile" row in Jellyfin's user menu. Cheap and
        /// idempotent — React re-renders the menu freely, so this runs from checkRoute
        /// rather than trying to catch every rebuild.
        syncUserMenuEntry: function () {
            // Cheap guard first, for the same reason as syncPreferencesMenuEntry: this runs
            // from the route poll, and in button mode the only reason to touch the menu is
            // to remove a row a previous menu-mode session left behind.
            const menuMode = this.isMenuLocation();
            if (!menuMode && !this._userEntryPlaced) return;

            const menu = document.querySelector(this.USER_MENU_SELECTOR);
            if (!menu) return;

            const existing = menu.querySelector('#profiles-user-menu-item');

            if (!menuMode) {
                // Location switched back to the floating button — take the row out again.
                if (existing) existing.remove();
                this._userEntryPlaced = false;
                return;
            }
            if (existing && menu.contains(existing)) return;

            const items = Array.from(menu.querySelectorAll('li.MuiMenuItem-root, [role="menuitem"]'));
            if (!items.length) return;

            // Anchor on the Sign out row. It is the only item in this menu with no
            // navigation target, and matching it by position instead would break the
            // moment an optional row (Quick Connect, Select Server, Exit) appears.
            // Translated UIs are the normal case — issue #14 came with a Polish
            // screenshot — so text matching alone is not enough; the MUI Logout icon
            // is language-independent and checked first.
            const signOut = items.find(el => el.querySelector('[data-testid="LogoutIcon"]'))
                || items.find(el => {
                    if (el.getAttribute('href') || el.querySelector('a[href]')) return false;
                    const text = (el.textContent || '').trim().toLowerCase();
                    return text === 'sign out' || text === 'log out' || text === 'logout';
                });
            if (!signOut) return;

            // Clone a real row so the entry inherits the active theme's MUI classes
            // instead of carrying styling of our own that would not match. Cloning does
            // not copy listeners, so React's logout handler does not come with it.
            const entry = signOut.cloneNode(true);
            entry.id = 'profiles-user-menu-item';
            entry.removeAttribute('aria-controls');
            entry.removeAttribute('data-testid');

            const iconSlot = entry.querySelector('.MuiListItemIcon-root');
            if (iconSlot) {
                // Replace the MUI SVG with a Material Icons glyph — Jellyfin already
                // loads that font, and we cannot construct a MUI icon component here.
                // The glyph comes from the class, not the text: jellyfin-web bundles
                // material-design-icons-iconfont, whose CSS sets :before content per name.
                iconSlot.innerHTML = '';
                const glyph = document.createElement('span');
                glyph.className = 'material-icons switch_account';
                glyph.setAttribute('aria-hidden', 'true');
                glyph.style.fontSize = '1.5rem';
                iconSlot.appendChild(glyph);
            }

            const label = entry.querySelector('.MuiListItemText-primary') || entry.querySelector('.MuiListItemText-root');
            if (label) label.textContent = t('switcher.switchProfile');
            else entry.textContent = t('switcher.switchProfile');

            entry.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                this.closeUserMenu();
                // Let MUI finish its close transition before the overlay mounts, so the
                // menu is not still fading out on top of the switcher.
                setTimeout(() => this.handleBubbleClick(), 80);
            });
            entry.addEventListener('keydown', (e) => {
                if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); entry.click(); }
            });

            signOut.parentNode.insertBefore(entry, signOut);
            this._userEntryPlaced = true;
        },

        /// Re-asserts the "Switch Profile" row on the Settings page (#/mypreferencesmenu),
        /// which is the list users actually mean by "the menu" — the header dropdown above
        /// is a different component and only some layouts show it.
        ///
        /// Source of truth: src/apps/legacy/routes/user/settings/index.tsx, page id
        /// myPreferencesMenuPage. The row goes in the "User" section above Sign out.
        syncPreferencesMenuEntry: function () {
            // Cheap guard first. This runs from the route poll, so in button mode — the
            // default — it used to query the document and read offsetParent (which forces
            // layout) twice a second purely to discover it had nothing to do. Reading a
            // cached preference costs nothing, and the only reason to look at the page in
            // button mode is to take away a row a previous menu-mode session left behind,
            // which _prefsEntryPlaced already knows about.
            const menuMode = this.isMenuLocation();
            if (!menuMode && !this._prefsEntryPlaced) return;

            // Jellyfin keeps previous views in the DOM, so match the visible one or the
            // row lands on a page nobody is looking at.
            const page = Array.from(document.querySelectorAll('#myPreferencesMenuPage'))
                .find(el => el.offsetParent !== null || el.classList.contains('is-active'));
            if (!page) return;

            const existing = page.querySelector('#profiles-preferences-menu-item');

            if (!menuMode) {
                if (existing) existing.remove();
                this._prefsEntryPlaced = false;
                return;
            }
            if (existing) return;

            const signOut = page.querySelector('.userSection .btnLogout') || page.querySelector('.btnLogout');
            if (!signOut) return;

            // Clone a real row so it inherits the active theme's classes. React dispatches
            // through fiber props stored on the node, which cloneNode does not copy, so the
            // logout handler does not come along.
            const entry = signOut.cloneNode(true);
            entry.id = 'profiles-preferences-menu-item';
            entry.classList.remove('btnLogout');
            entry.removeAttribute('href');
            // An <a> with no href is not focusable, and this row has none — Jellyfin's own
            // Sign out is a click handler rather than a link. Without this the keyboard and
            // D-pad can never reach the entry and its Enter handler is dead code.
            entry.setAttribute('tabindex', '0');
            entry.setAttribute('role', 'button');

            const icon = entry.querySelector('.listItemIcon');
            if (icon) {
                icon.classList.remove('exit_to_app');
                icon.classList.add('switch_account');
                icon.textContent = '';
            }

            const label = entry.querySelector('.listItemBodyText');
            if (label) label.textContent = t('switcher.switchProfile');
            else entry.textContent = t('switcher.switchProfile');

            entry.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                this.handleBubbleClick();
            });
            entry.addEventListener('keydown', (e) => {
                if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); entry.click(); }
            });

            signOut.parentNode.insertBefore(entry, signOut);
            this._prefsEntryPlaced = true;
        },

        /// Closes the MUI menu. Our cloned row has no React handler, so nothing would
        /// dismiss it otherwise. Escape is what MUI itself listens for; clicking the
        /// backdrop is the fallback for builds where that listener is not attached.
        closeUserMenu: function () {
            document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }));

            setTimeout(() => {
                const backdrop = document.querySelector('#app-user-menu .MuiBackdrop-root, .MuiModal-root .MuiBackdrop-root');
                if (backdrop) backdrop.click();
            }, 60);
        },

        /// Adds a Profiles section to Jellyfin's own profile page (#/userprofile).
        /// Re-checked on every route change because React replaces the view wholesale.
        injectProfilePageSection: function () {
            if (document.getElementById('profiles-userprofile-section')) return;

            // The visible page, not a cached off-screen view: Jellyfin keeps previous views
            // in the DOM, and appending to a hidden one puts the section nowhere.
            const page = Array.from(document.querySelectorAll('#userProfilePage, .userProfilePage, .page'))
                .find(el => el.offsetParent !== null || el.classList.contains('is-active'));
            if (!page) return;

            const host = page.querySelector('.readOnlyContent, .padded-left, form') || page;

            const activeInfo = this.getCachedActiveProfile();
            const section = document.createElement('div');
            section.id = 'profiles-userprofile-section';
            section.className = 'verticalSection';
            section.style.cssText = 'margin: 2em 0; max-width: 44em;';
            section.innerHTML = `
                <h2 class="sectionTitle" style="display:flex; align-items:center; gap:0.4em;">
                    <span class="material-icons local_fire_department" aria-hidden="true" style="color:var(--jpf-warn);"></span>
                    ${t('profilePage.title')}
                </h2>
                <p style="opacity:0.7; margin:0 0 1em 0; line-height:1.5;">
                    ${t('profilePage.body', { name: escapeHtml(activeInfo.name) || t('profilePage.thisAccount') })}
                </p>
                <button is="emby-button" type="button" id="profiles-userprofile-switch-btn" class="raised button-submit block">
                    <span>${t('switcher.switchProfile')}</span>
                </button>
            `;

            host.appendChild(section);

            const btn = section.querySelector('#profiles-userprofile-switch-btn');
            btn.addEventListener('click', (e) => {
                e.preventDefault();
                this.handleBubbleClick();
            });
            btn.addEventListener('keydown', (e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    btn.click();
                }
            });
        },

        // ── Bubble visibility helpers ──────────────────────────────────────────
        _bubbleHide: function (bubble) {
            if (!bubble || bubble.dataset.profilesHiding === '1') return;
            bubble.dataset.profilesHiding = '1';
            bubble.classList.add('profiles-bubble-hiding');
            setTimeout(() => {
                // Only commit display:none if still in hiding state
                if (bubble.dataset.profilesHiding === '1') {
                    bubble.style.display = 'none';
                    bubble.classList.remove('profiles-bubble-hiding');
                    delete bubble.dataset.profilesHiding;
                }
            }, 160);
        },
        _bubbleShow: function (bubble) {
            if (!bubble) return;
            delete bubble.dataset.profilesHiding;
            bubble.style.display = '';
            // Tick so the browser has a chance to paint display:'' before removing
            // the opacity class, triggering the CSS transition.
            requestAnimationFrame(() => bubble.classList.remove('profiles-bubble-hiding'));
        },

        evaluateFloatingBubbleVisibility: function (viewType) {
            let bubble = document.getElementById('profiles-floating-bubble');

            // With the switcher in Jellyfin's menu, keeping the floating button as well
            // would be two controls doing one job — and the second portrait beside the
            // native user icon is precisely what issue #14 reported as confusing.
            if (this.isMenuLocation()) {
                if (bubble) bubble.remove();
                return;
            }

            // Hide during active playback/OSD or on any server-management page.
            if (viewType === 'videoosd' || viewType === 'dashboard') {
                this._bubbleHide(bubble);
                return;
            }

            if (!this.isProfileSessionActive()) {
                // No active session — remove entirely (will be re-created when needed)
                if (bubble) bubble.remove();
                return;
            }

            // ── Strategy 1: find the header button container by class name ─────────
            const headerContainer = this._findHeaderContainer();

            if (headerContainer) {
                if (bubble && bubble.classList.contains('profiles-floating-fallback')) {
                    bubble.remove();
                    bubble = null;
                }
                if (bubble) {
                    if (!document.contains(bubble)) {
                        bubble.remove();
                        bubble = null;
                    } else if (!headerContainer.contains(bubble)) {
                        // Button is in the DOM but drifted outside the header — re-insert.
                        this._insertBeforeUserBtn(headerContainer, bubble);
                    }
                }
                if (!bubble) {
                    bubble = this._buildHeaderBubble();
                    this._insertBeforeUserBtn(headerContainer, bubble);
                    this.attachBubbleClickHandler(bubble);
                }

            } else {
                // ── Strategy 2: geometry-based anchor ────────────────────────────────
                // If no named container matched (e.g. a custom Skin Manager theme),
                // find the rightmost visible button in the top 80px of the viewport
                // and insert next to it.  Works for ANY theme regardless of class names.
                // Cached like the header container, and for a sharper reason: this walks
                // every button and link in the document and reads getBoundingClientRect on
                // each one, which forces layout. Running it twice a second was the most
                // expensive thing the plugin did, and it only ever runs on themes where no
                // named container matched — so the worst case fell on the people already
                // worst served. Re-found when it leaves the page or the viewport changes.
                if (this._geoAnchor && !document.contains(this._geoAnchor)) {
                    this._geoAnchor = null;
                }
                //
                // A failed search backs off for the same three seconds the named search
                // does. Without it, a page with no buttons at all in the top 80px — which
                // is where this ends up — would re-run the whole walk on every tick
                // forever, having already established there is nothing to find.
                const geoNow = Date.now();
                const geoStale = !this._geoSearchedAt || geoNow - this._geoSearchedAt >= 3000;
                if (!this._geoAnchor && (geoStale || this._fallbackPosDirty)) {
                    this._geoSearchedAt = geoNow;
                    this._geoAnchor = this._findGeometricHeaderAnchor();
                } else if (this._fallbackPosDirty) {
                    this._geoSearchedAt = geoNow;
                    this._geoAnchor = this._findGeometricHeaderAnchor();
                }
                const anchor = this._geoAnchor;
                if (anchor) {
                    if (bubble && bubble.classList.contains('profiles-floating-fallback')) {
                        bubble.remove();
                        bubble = null;
                    }
                    if (bubble) {
                        if (!document.contains(bubble)) {
                            bubble.remove(); bubble = null;
                        } else if (!anchor.parentElement.contains(bubble)) {
                            bubble.remove(); bubble = null;
                        }
                    }
                    if (!bubble) {
                        bubble = this._buildHeaderBubble();
                        anchor.parentElement.insertBefore(bubble, anchor);
                        this.attachBubbleClickHandler(bubble);
                    }

                } else {
                    // ── Strategy 3: true corner-pill fallback ────────────────────────
                    // Appended to <html> (outside the body transform chain) so
                    // position:fixed works correctly regardless of CSS transforms.
                    if (bubble && !bubble.classList.contains('profiles-floating-fallback')) {
                        bubble.remove();
                        bubble = null;
                    }
                    if (!bubble) {
                        bubble = this._buildFallbackBubble();
                        document.documentElement.appendChild(bubble);
                        this.attachBubbleClickHandler(bubble);
                        // _buildFallbackBubble positions it as it builds it, so it starts
                        // clean rather than asking for the same computation twice.
                        this._fallbackPosDirty = false;
                    }
                }
            }

            if (bubble && bubble.classList.contains('profiles-floating-fallback')
                && this._fallbackPosDirty !== false) {
                // Where the corner pill goes depends on the viewport, not on the route.
                // This ran on every tick — a document-wide button query plus several
                // elementFromPoint calls, each one forcing layout — twice a second, on
                // precisely the themes where the header search had already failed and was
                // therefore costing the most. Recomputed when the pill is built and when
                // the viewport changes, which is when the answer can actually differ.
                const pos = this._findBestFallbackPosition();
                bubble.style.top = pos.top;
                bubble.style.bottom = pos.bottom;
                bubble.style.left = pos.left;
                bubble.style.right = pos.right;
                this._fallbackPosDirty = false;
            }

            this._bubbleShow(bubble);

            // Pre-fetch the profile list while the button is visible so the overlay
            // appears instantly (no network wait) when the user clicks it.
            if (viewType === 'home' && !this._profilePrefetchPending) {
                this._prefetchProfiles();
            }
        },

        // Fetches /list using the master token and caches the result in this.cachedProfiles.
        // Called proactively by evaluateFloatingBubbleVisibility; the cached result is
        // consumed by fetchAndRenderProfiles for instant, flash-free overlay display.
        _prefetchProfiles: function () {
            if (this._profilePrefetchPending || (this.cachedProfiles && this.cachedProfiles.length)) return;
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            if (!masterState || !masterState.masterToken) return;

            this._profilePrefetchPending = true;
            const url = ApiClient.getUrl('plugins/profiles/list');
            fetch(url, { headers: this.getAuthHeaders(masterState.masterToken) })
                .then(res => {
                    if (!res.ok) throw new Error();
                    return res.json();
                })
                .then(profiles => {
                    const normalized = this.normalizeProfiles(profiles);
                    this.cachedProfiles = normalized;
                    localStorage.setItem('jellyfin_profiles_cached_list', JSON.stringify(normalized));
                    this._profilePrefetchPending = false;

                    // Sync sessionStorage if matches current active user
                    const currentUserId = ApiClient.getCurrentUserId();
                    if (currentUserId) {
                        const currentProfile = normalized.find(p => this.normalizeGuid(p.profileUserId) === this.normalizeGuid(currentUserId));
                        if (currentProfile) {
                            const info = {
                                name: currentProfile.profileName,
                                color: currentProfile.avatarColor || DEFAULT_AVATAR_COLOR,
                                initial: currentProfile.avatarInitial || (currentProfile.profileName ? currentProfile.profileName.charAt(0).toUpperCase() : 'P'),
                                profileImage: currentProfile.profileImage || null,
                                transparent: !!currentProfile.transparentAvatar
                            };
                            this._sessionSet('jellyfin_profiles_active_info', JSON.stringify(info));
                        }
                    }

                    // Re-render bubble with the fetched info
                    const currentRouteType = this._lastRouteType || 'other';
                    this.evaluateFloatingBubbleVisibility(currentRouteType);
                })
                .catch(() => { this._profilePrefetchPending = false; });
        },

        // ── Header-container detection ───────────────────────────────────────────

        /**
         * The container the floating switcher button is placed in.
         *
         * Cached, because this is reached from evaluateFloatingBubbleVisibility on every
         * route tick while a profile is active — which is essentially always — and the
         * header is built once per page. It was re-running the entire search twice a
         * second to arrive at the same answer: six document queries on a stock install and
         * ten on a theme where nothing matched, two of them full-document walks.
         * tests/js/routetick.js measures it.
         *
         * The cache is invalidated by the element leaving the document, which is what
         * React rebuilding the header looks like from out here.
         */
        _findHeaderContainer: function () {
            if (this._headerContainer && document.contains(this._headerContainer)) {
                return this._headerContainer;
            }

            // A failed search is remembered too, briefly. Caching only successes would
            // leave the full cost in place on exactly the themes where no strategy
            // matches — the case that was already the most expensive. Three seconds is
            // far below the time anyone takes to notice a missing button, and far above
            // a React re-render.
            const now = Date.now();
            if (this._headerContainer === null
                && this._headerSearchedAt
                && now - this._headerSearchedAt < 3000) {
                return null;
            }
            this._headerSearchedAt = now;

            this._headerContainer = this._searchForHeaderContainer();
            return this._headerContainer;
        },

        _searchForHeaderContainer: function () {
            // Strategy A: Jellyfin's own right-hand button container.
            //
            // Five other class names were tried before this one — .headerRightButtons,
            // .headerSelfView, .skinHeader-rightButtons, .headerButtons-right and
            // .viewHeaderRight. Every one is absent from jellyfin-web 10.11, so on a
            // stock install the browser resolved five selectors that could not match
            // before reaching the one that does. They are recorded in
            // tests/upstream-selectors.json.
            const byClass = document.querySelector('.headerRight');
            if (byClass) return byClass;

            // Strategy B: the parent of a header button. A theme that renames the
            // container usually keeps Jellyfin's own buttons inside it.
            //
            // .headerButton rather than [class*="headerButton"]: every header button
            // upstream carries the bare class alongside its specific one
            // (headerCastButton castButton headerButton headerButtonRight), so the plain
            // class selector matches exactly the same elements while letting the engine
            // use the class index instead of walking the document. .btnCurrentUser,
            // .headerButtonUser, .headerButton-user, .btnCast and .headerButton-cast were
            // also tried here and none of them exists upstream either — the cast button
            // is .headerCastButton.
            const knownBtn = document.querySelector(
                '.headerButton:not(#profiles-floating-bubble)'
            );
            if (knownBtn) return knownBtn.parentElement;

            // Strategy C: find the button cluster inside a custom skin/theme header.
            // ElegantFin and Skin Manager themes wrap everything in .skinHeader or
            // a similarly named element; we pick the child that contains the most
            // icon buttons (likely the right-side group).
            const skinHeader = document.querySelector(
                '.skinHeader, .jellyfinHeader, [class*="skinHeader"], [class*="topBar"]'
            );
            if (!skinHeader) return null;

            // Two queries, not one per candidate. This used to ask for every div, nav, ul
            // and span under the header and then run a second query *inside each one*, so
            // the same buttons were counted once for every ancestor they had. Counting
            // upwards from each button gives the identical answer in one pass.
            const counts = new Map();
            for (const btn of skinHeader.querySelectorAll('button, a[role="button"]')) {
                for (let el = btn.parentElement; el && el !== skinHeader; el = el.parentElement) {
                    counts.set(el, (counts.get(el) || 0) + 1);
                }
            }

            // Walked in document order, and ties go to the first — the same result the
            // nested version produced, since it iterated the same node list.
            let best = null, bestCount = 0;
            for (const el of skinHeader.querySelectorAll('div, nav, ul, span')) {
                const n = counts.get(el) || 0;
                if (n > bestCount && n >= 2) {
                    bestCount = n;
                    best = el;
                }
            }
            return best;
        },

        // Finds the rightmost visible button within the top 80px of the viewport.
        // Theme-agnostic: works even when class names are completely non-standard.
        _findGeometricHeaderAnchor: function () {
            const candidates = document.querySelectorAll(
                'button:not(#profiles-floating-bubble), a[role="button"]:not(#profiles-floating-bubble)'
            );
            let rightmost = null, rightmostX = -Infinity;
            for (const el of candidates) {
                const r = el.getBoundingClientRect();
                if (r.top >= 0 && r.bottom <= 80 && r.width > 0 && r.right > rightmostX) {
                    rightmostX = r.right;
                    rightmost = el;
                }
            }
            return rightmost;
        },

        // Builds the icon-style button for header insertion.
        _buildHeaderBubble: function () {
            const b = document.createElement('button');
            b.id = 'profiles-floating-bubble';
            // `focusable` is what Jellyfin's own focusManager looks for on TV layouts
            // (focusableQuery in src/components/focusManager.js is the standard tags plus
            // `.focusable`). A bare <button> qualifies on paper; carrying the class as well
            // costs nothing and is what every other header control does.
            b.className = 'paper-icon-button-light headerButton focusable';
            b.title = t('switcher.switchProfile');
            b.setAttribute('aria-label', t('switcher.switchProfile'));

            const activeInfo = this.getCachedActiveProfile();
            b.innerHTML = `
                <div class="profiles-header-avatar" style="background-color: ${
                    activeInfo.profileImage && activeInfo.transparent ? 'transparent' : safeColor(activeInfo.color)
                }; width: 28px; height: 28px; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 0.85rem; font-weight: 700; color: #fff; text-shadow: 0 1px 2px rgba(0,0,0,0.5); border: 1.5px solid rgba(255,255,255,0.25); box-sizing: border-box; overflow: hidden; position: relative;">
                    ${avatarInner(activeInfo.profileImage, activeInfo.initial, /* useThumb */ true)}
                </div>
            `;
            return b;
        },

        // Builds the corner pill button (last-resort fallback).
        _buildFallbackBubble: function () {
            const b = document.createElement('button');
            b.id = 'profiles-floating-bubble';
            b.className = 'profiles-floating-fallback focusable';
            b.title = t('switcher.switchProfile');
            b.setAttribute('aria-label', t('switcher.switchProfile'));

            // Set initial position dynamically
            const pos = this._findBestFallbackPosition();
            b.style.top = pos.top;
            b.style.bottom = pos.bottom;
            b.style.left = pos.left;
            b.style.right = pos.right;

            const activeInfo = this.getCachedActiveProfile();
            b.innerHTML = `
                <div class="profiles-header-avatar" style="background-color: ${
                    activeInfo.profileImage && activeInfo.transparent ? 'transparent' : safeColor(activeInfo.color)
                }; width: 28px; height: 28px; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 0.85rem; font-weight: 700; color: #fff; text-shadow: 0 1px 2px rgba(0,0,0,0.5); border: 1.5px solid rgba(255,255,255,0.25); box-sizing: border-box; overflow: hidden; position: relative;">
                    ${avatarInner(activeInfo.profileImage, activeInfo.initial, /* useThumb */ true)}
                </div>
            `;
            return b;
        },

        _findBestFallbackPosition: function () {
            const corners = [
                // Top-Right
                { top: '80px', bottom: 'auto', left: 'auto', right: '24px', x: () => window.innerWidth - 60, y: () => 95 },
                // Top-Left
                { top: '80px', bottom: 'auto', left: '24px', right: 'auto', x: () => 60, y: () => 95 },
                // Bottom-Left
                { top: 'auto', bottom: '24px', left: '24px', right: 'auto', x: () => 60, y: () => window.innerHeight - 40 },
                // Bottom-Right
                { top: 'auto', bottom: '24px', left: 'auto', right: '24px', x: () => window.innerWidth - 60, y: () => window.innerHeight - 40 }
            ];

            for (const corner of corners) {
                const cx = corner.x();
                const cy = corner.y();
                if (cx < 0 || cy < 0 || cx > window.innerWidth || cy > window.innerHeight) continue;

                const el = document.elementFromPoint(cx, cy);
                if (!el) return corner;

                if (!el.closest('button, a, [role="button"], input, select, textarea, .headerButton, .paper-icon-button-light')) {
                    return corner;
                }
            }

            return corners[0];
        },

        // Inserts bubble before the user-account button, or appends if not found.
        _insertBeforeUserBtn: function (container, bubble) {
            // .headerUserButton is the real class. This asked for .headerButton-user,
            // .btnCurrentUser and .headerButtonUser, none of which exists in jellyfin-web,
            // so the query never matched and the function has always fallen through to
            // lastElementChild — placing the switcher at the end of the header rather than
            // beside the account icon it is named for. Verified in
            // src/scripts/libraryMenu.js: "headerButton headerButtonRight headerUserButton"
            // and the headerUserButtonRound variant.
            const userBtn =
                container.querySelector('.headerUserButton, .headerUserButtonRound') ||
                container.lastElementChild;
            if (userBtn) {
                userBtn.parentNode.insertBefore(bubble, userBtn);
            } else {
                container.appendChild(bubble);
            }
        },

        /// Opens the profile selector over whatever page the user is on.
        ///
        /// Every entry point routes through here — the header bubble, the sidebar link, the
        /// user-menu item and the profile page — so they cannot drift apart. There is no
        /// page reload: the overlay is drawn on top of the current view, which is what
        /// removed the white flash the old reload-based button caused.
        handleBubbleClick: function () {
            // Every entry point into the switcher funnels through here, so this is where the
            // emergency disable has to hold. An entry that survived the teardown — a menu row
            // a theme rebuilt, say — must not be able to raise the gate again.
            if (this._panicDisabled) return;

            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey) || 'null');

            if (masterState && masterState.masterToken) {
                // Opening the switcher deliberately is a reversible act, so remember enough to
                // undo it. The profile's token is still valid — nothing has been signed out —
                // so backing out costs no PIN and no reload. Without this there is no way off
                // the picker at all once it is open, which on a TV means the Back button lands
                // on a dead end.
                const priorUserId = ApiClient.getCurrentUserId();
                const priorToken = this._sessionGet(this.config.activeSessionKey);
                this._resumeState = (priorToken && priorUserId)
                    ? {
                        token: priorToken,
                        userId: priorUserId,
                        info: this._sessionGet('jellyfin_profiles_active_info')
                    }
                    // No profile session to restore, which is the normal case in menu mode:
                    // the master never passed through the gate. Closing is still a valid
                    // answer, and without it there is no way off the picker at all.
                    : { closeOnly: true };

                // Put the master's credentials back in memory before listing profiles — the
                // active sub-profile's token cannot see its siblings.
                this.clearProfileSession();
                this.updateStoredCredentials(masterState.masterToken, masterState.masterUserId);
                ApiClient.setAuthenticationInfo(masterState.masterToken, masterState.masterUserId);
            } else {
                // Signed in as the master with nothing stored: closing is all that is
                // needed to get back to where they were.
                this._resumeState = { closeOnly: true };
            }

            // With no stored master state we are still signed in as the master — that is the
            // normal case in native mode, where the user never passes through the gate.
            // interceptHomeAndShowProfiles() records the state and takes it from there.
            this.interceptHomeAndShowProfiles();
        },

        /// Puts back the profile that was active before the switcher was opened.
        ///
        /// Nothing was signed out to get here — handleBubbleClick only swapped the master's
        /// credentials into memory so the profile list could be fetched — so the profile's
        /// own token is still good and this needs neither a PIN nor a reload.
        resumePreviousProfile: function () {
            const prior = this._resumeState;
            if (!prior) return;
            this._resumeState = null;

            // closeOnly means there was no profile session to put back — the credentials
            // in play are already the right ones.
            if (!prior.closeOnly) {
                this._sessionSet(this.config.activeSessionKey, prior.token);
                if (prior.info) this._sessionSet('jellyfin_profiles_active_info', prior.info);

                this.updateStoredCredentials(prior.token, prior.userId);
                ApiClient.setAuthenticationInfo(prior.token, prior.userId);
            }

            this.isManageMode = false;
            this.masterPin = null;
            this.removeProfileOverlay();
            this.checkRoute();
        },

        attachBubbleClickHandler: function (bubble) {
            const activate = (e) => {
                e.preventDefault();
                e.stopPropagation();

                bubble.disabled = true;
                bubble.style.opacity = '0.45';
                bubble.style.cursor = 'wait';

                this.handleBubbleClick();

                // Re-enable after the overlay has appeared so the button is ready again if
                // the user dismisses it.
                setTimeout(() => {
                    bubble.disabled = false;
                    bubble.style.opacity = '';
                    bubble.style.cursor = '';
                }, 400);
            };

            bubble.addEventListener('click', activate);

            // Explicit D-pad/keyboard Enter+Space handler.
            // Native <button> fires click on Enter in most browsers, but some TV browsers
            // (notably older Tizen and webOS) skip this for non-focused or injected elements.
            bubble.addEventListener('keydown', (e) => {
                if (e.key === 'Enter' || e.key === ' ') activate(e);
            });
        },

        injectStyles: function () {
            // Idempotent: a second evaluation of this script (a re-injected tag, a client
            // that loads it twice) would otherwise append a second full copy of the sheet.
            if (document.getElementById('jpf-styles')) return;

            const style = document.createElement('style');
            style.id = 'jpf-styles';
            // textContent, not innerHTML. A <style> element's contents are CSS, not markup,
            // so HTML-parsing them buys nothing and can only misread the text.
            style.textContent = BONFIRE_STYLES;
            document.head.appendChild(style);
        }
    };

    ProfilesPlugin.init();
})();
