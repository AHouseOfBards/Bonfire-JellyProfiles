/*
 * The household every screenshot shows, and the screens. Loaded before the client script.
 *
 * The page's address says what to draw: ?theme=harvest&scene=picker&device=phone. The
 * scene runs once the client script has loaded, then sets window.__ready, which is what
 * shoot.js waits for. Anything the stub server could not answer is in window.__misses.
 *
 * The people are invented and the same in every shot, so two shots differ only by theme or
 * screen. Names are chosen for width: one long, one short, one with an accent.
 */
(function () {
    'use strict';

    var q = new URLSearchParams(location.search);
    var THEME = q.get('theme') || 'bonfire';
    var SCENE = q.get('scene') || 'picker';
    // ?layout=profiles:list,align:left,size:large,logo:bottom-left — the theme named by
    // ?theme, copied as a custom theme with those layout choices, and worn instead of it.
    // A value of - leaves that field out.
    var CUSTOM = [];
    if (q.get('layout')) {
        var x = new XMLHttpRequest();
        x.open('GET', '/fixture/themes.json', false);
        x.send();
        var base = JSON.parse(x.responseText).themes.filter(function (t) { return t.id === THEME; })[0];
        var copy = JSON.parse(JSON.stringify(base));
        copy.id = 'layout-' + THEME;
        q.get('layout').split(',').forEach(function (kv) { var p = kv.split(':'); if (p[1] === '-') delete copy.layout[p[0]]; else copy.layout[p[0]] = p[1]; });
        CUSTOM.push(copy);
        THEME = copy.id;
    }
    var withLogo = function (logo) { if (q.get('corners')) logo.corners = q.get('corners'); return { themes: CUSTOM, logo: logo }; };
    var DEVICE = q.get('device') || 'tablet';

    var MAIN = '11111111111111111111111111111111';
    var people = [
        { id: MAIN, name: 'Alex', colour: '#e07a5f', master: true },
        { id: '22222222222222222222222222222222', name: 'Sam', colour: '#3d8bfd', pin: true },
        { id: '33333333333333333333333333333333', name: 'Rosalind', colour: '#81b29a',
          viewing: { allowed: true, binding: 'limit', minutesLeft: 45 } },
        { id: '44444444444444444444444444444444', name: 'Zoë', colour: '#f2cc8f',
          viewing: { allowed: false, reason: 'limit' } }
    ];
    // ?count=N: a household of N tiles, to see the grid at its limit: the main account
    // and 20 profiles, so 21. The limit is set to what is drawn, so Manage is at it.
    // Some names are long on purpose, since that is what a crowded grid has to wrap or cut.
    var EXTRA = ['Jordan', 'Maximilian', 'Priya', 'Grandma Josephine', 'Theo', 'Kids room',
        'Olu', 'Charlotte-Anne', 'Mateo', 'Guest', 'Wei', 'Bartholomew', 'Isla', 'Noor',
        'Dad', 'Evangeline', 'Sebastian'];
    var COLOURS = ['#e07a5f', '#3d8bfd', '#81b29a', '#f2cc8f', '#9b5de5', '#f15bb5', '#00bbf9', '#00f5d4'];
    var COUNT = Math.min(parseInt(q.get('count'), 10) || 0, people.length + EXTRA.length);
    for (var n = people.length; n < COUNT; n++) {
        var hex = ('00000000' + (n + 1).toString(16)).slice(-8);
        people.push({ id: 'aaaaaaaaaaaaaaaaaaaaaaaa' + hex, name: EXTRA[n - 4], colour: COLOURS[n % COLOURS.length], pin: n % 3 === 0 });
    }
    var PROFILES = people.map(function (p) {
        return {
            profileUserId: p.id, profileName: p.name, masterUserId: MAIN,
            isMaster: !!p.master, isBonfire: false, isLinked: false,
            requiresPin: !!p.pin, avatarColor: p.colour, avatarInitial: p.name.charAt(0),
            profileImage: null, transparentAvatar: false, maxSubProfiles: Math.max(5, COUNT - 1),
            viewing: (SCENE === 'timerequest' && p.id === people[2].id)
                ? Object.assign({}, p.viewing, { askedUtc: '2026-11-12T19:30:00Z' }) : (p.viewing || null)
        };
    });

    // Plugin routes the screens ask for, by path after /plugins/profiles/. A function gets
    // the URL and the request options.
    // The gate warms the profile form's data while the grid is read; empty lists are what
    // a household with nothing set up yet gets.
    var empty = function () { return []; };
    // The goldens keep the empty household they were shot with; the review scenes (forms,
    // Settings) get a household with something in it, shaped as docs/developer-api.md says.
    var GOLDEN = ['picker', 'announcement', 'logo', 'logotext', 'manage', 'pin', 'dialog', 'timeup', 'settings', 'themes'];
    var FULL = GOLDEN.indexOf(SCENE) === -1;
    var full = function (rich) { return function () { return FULL ? rich : []; }; };
    var ROUTES = {
        'libraries': full([
            { id: 'aaaa0000000000000000000000000001', name: 'Movies', collectionType: 'movies' },
            { id: 'aaaa0000000000000000000000000002', name: 'TV Shows', collectionType: 'tvshows' },
            { id: 'aaaa0000000000000000000000000003', name: 'Kids', collectionType: 'movies' },
            { id: 'aaaa0000000000000000000000000004', name: 'Music', collectionType: 'music' }
        ]),
        'devices': full([
            { deviceId: 'dddd0000000000000000000000000001', deviceName: 'Living Room TV', client: 'Jellyfin webOS', lastSeen: '2026-11-04T19:00:00Z', masterUserId: MAIN },
            { deviceId: 'dddd0000000000000000000000000002', deviceName: "Sam's iPad", client: 'Jellyfin iOS', lastSeen: '2026-11-03T08:12:00Z', masterUserId: MAIN }
        ]),
        'avatars': empty,
        // What the picker asks for when it is opened from a button.
        'list': function () { return PROFILES; },
        'ratings': function () {
            return FULL ? { country: 'US', ratings: [
                { score: 0, labels: ['G', 'TV-G', 'TV-Y'] }, { score: 7, labels: ['TV-Y7'] }, { score: 10, labels: ['PG', 'TV-PG'] },
                { score: 13, labels: ['PG-13'] }, { score: 14, labels: ['TV-14'] }, { score: 17, labels: ['R', 'TV-MA'] }] } : [];
        },
        'time-requests': function () {
            return { requests: [{ profileId: people[2].id, profileName: 'Rosalind', askedUtc: '2026-11-12T19:30:00Z', timeZone: 'America/Chicago', grantMinutesLeft: 120 }] };
        },
        'announcements': function () {
            return SCENE === 'announcement'
                ? [{ id: 'a1', severity: 'info', message: 'The server restarts at 2am for an update.' }]
                : [];
        },
        'me': function () {
            return { userId: MAIN, name: 'Alex', isProfile: false, mainAccountId: MAIN, mainAccountName: 'Alex',
                isAdministrator: true, mainHasPin: true, profileCount: 3, maxProfiles: 5, pinResetByEmail: true };
        },
        'account/email': function () {
            return { available: true, address: 'alex@example.com', pendingAddress: '', notifyLockout: true,
                notifyNewDevice: true, notifyTimeAsked: true, notifyLimitReached: false, pinResetEnabled: true };
        },
        'account/pin-reset/send': function () { return { sentTo: 'a•••@example.com' }; },
        'timezones': function () {
            return { zones: ['America/Chicago', 'America/New_York', 'Europe/London', 'Europe/Paris'], server: 'America/Chicago',
                'default': '', household: '', effective: 'America/Chicago' };
        },
        'accounts/links': function () {
            return { added: [{ userId: '55555555555555555555555555555555', name: 'grandma', hasPin: true, byAdministrator: false, linkedUtc: '2026-11-01T18:00:00Z' }], shownIn: [] };
        },
        'bonfire/status': function () {
            return { isOwner: false, ownedCode: null, ownedMembers: [], isMember: false, joinedOwnerName: null, joinedOwnerId: null,
                hideMySubProfilesFromOthers: false, hideOthersSubProfilesFromMe: false, allowHouseholdLanBypass: false,
                isAdministrator: true, hasPin: true };
        },
        'preferences': function () {
            return { askOnStartup: true, switcherLocation: 'button', masterUserId: MAIN,
                emergencyCodeConfigured: false, theme: THEME, defaultTheme: 'bonfire', allowProfileThemes: true, onboardingPending: false };
        }
    };
    // The profile form's four reads are one request since 2.0.
    var PARTS = { libraries: ROUTES.libraries, ratings: ROUTES.ratings, avatars: ROUTES.avatars, timeZones: ROUTES.timezones };
    delete ROUTES.libraries; delete ROUTES.ratings; delete ROUTES.avatars; delete ROUTES.timezones;
    ROUTES['form-options'] = function () {
        return { libraries: PARTS.libraries(), ratings: PARTS.ratings(), avatars: PARTS.avatars(), timeZones: PARTS.timeZones() };
    };
    // Who the caller is, the announcements and the first-time setup are parts of /startup since 2.0.
    var STARTUP = { announcements: ROUTES.announcements, me: ROUTES.me };
    delete ROUTES.announcements; delete ROUTES.me;
    ROUTES.startup = function () {
        return { announcements: STARTUP.announcements(), me: STARTUP.me(), onboarding: { pending: false, generation: 1, steps: [] } };
    };
    // Routes with an id in them: Rosalind's limits are what the edit scene shows.
    var PATTERNS = [
        [/^limits\/(\w+)$/, function (m) {
            var p = people.filter(function (x) { return x.id === m[1]; })[0];
            if (!p || !p.viewing) return { profileId: m[1], limited: false };
            return { profileId: m[1], limited: true, allowed: p.viewing.allowed, reason: p.viewing.reason || '',
                binding: p.viewing.binding || '', minutesLeft: p.viewing.minutesLeft || 0, untilUtc: null, nextStartUtc: null,
                usedMinutes: 15, limitMinutes: 60, extraUntilUtc: null, askedUtc: null, timeZone: 'America/Chicago',
                rules: { hoursEnabled: true, windows: [{ days: 62, start: '15:00', end: '20:00' }], dailyMinutes: 60, weekendMinutes: 120 } };
        }],
        [/^library-artwork\/(\w+)$/, function () { return []; }]
    ];
    // Jellyfin's own user record, which the edit form reads a profile's policy from.
    function jellyfinUser(id) {
        var p = people.filter(function (x) { return x.id === id; })[0] || people[0];
        return { Id: p.id, Name: p.name, HasPassword: true, Configuration: {},
            Policy: { IsAdministrator: !!p.master, EnableAllFolders: false,
                EnabledFolders: ['aaaa0000000000000000000000000001', 'aaaa0000000000000000000000000003'],
                MaxParentalRating: 10, BlockedTags: [], AllowedTags: [], EnableAllDevices: true, EnabledDevices: [] } };
    }

    window.__misses = [];
    window.__fixture = { THEME: THEME, SCENE: SCENE, DEVICE: DEVICE, PROFILES: PROFILES, MAIN: MAIN };

    function answer(body, status) {
        var text = JSON.stringify(body === undefined ? {} : body);
        return Promise.resolve(new Response(text, { status: status || 200, headers: { 'Content-Type': 'application/json' } }));
    }

    var realFetch = window.fetch.bind(window);
    window.fetch = function (input, init) {
        var url = new URL(typeof input === 'string' ? input : input.url, location.href);
        var m = /^\/plugins\/profiles\/(.+)$/.exec(url.pathname);
        if (m && Object.prototype.hasOwnProperty.call(ROUTES, m[1])) return answer(ROUTES[m[1]](url, init || {}));
        if (m) {
            for (var i = 0; i < PATTERNS.length; i++) {
                var pm = PATTERNS[i][0].exec(m[1]);
                if (pm) return answer(PATTERNS[i][1](pm, url, init || {}));
            }
        }
        var u = /^\/Users\/(\w+)$/.exec(url.pathname);
        if (u) return answer(jellyfinUser(u[1]));
        if (url.pathname === '/Items/Filters2') return answer({ Tags: [], Genres: [] });
        if (url.pathname.indexOf('/plugins/profiles/themes/fonts/') === 0) return realFetch(input, init);
        window.__misses.push(((init && init.method) || 'GET') + ' ' + url.pathname + url.search);
        return answer({}, 404);
    };

    var known = {
        getCurrentUserId: function () { return MAIN; },
        getUrl: function (p) { return location.origin + '/' + String(p).replace(/^\//, ''); },
        serverAddress: function () { return location.origin; },
        serverId: function () { return 'fixture'; },
        accessToken: function () { return 'fixture-token'; },
        deviceId: function () { return 'fixture-device'; },
        deviceName: function () { return 'Fixture'; },
        appName: function () { return 'Jellyfin Web'; },
        appVersion: function () { return '10.11.5'; },
        setAuthenticationInfo: function () {},
        getCurrentUser: function () { return Promise.resolve({ Id: MAIN, Name: 'Alex', Policy: { IsAdministrator: true }, Configuration: {} }); },
        getUser: function () { return Promise.resolve({ Id: MAIN, Name: 'Alex', Policy: { IsAdministrator: true }, Configuration: {} }); },
        getPluginConfiguration: function () { return Promise.resolve({}); }
    };
    window.ApiClient = new Proxy(known, {
        get: function (target, name) {
            if (name in target) return target[name];
            if (typeof name !== 'string' || name === 'then') return undefined;
            return function () {
                window.__misses.push('ApiClient.' + name);
                return Promise.resolve({});
            };
        }
    });

    // What jellyfin-web's layoutManager writes on <html>, which the client reads to decide
    // it is on a television.
    // A desktop browser gets neither (jellyfin-web's layout-desktop sets no font size).
    document.documentElement.classList.add(/^tv/.test(DEVICE) ? 'layout-tv' : DEVICE === 'desktop' ? 'layout-desktop' : 'layout-mobile');
    try {
        localStorage.clear();
        localStorage.setItem('jellyfin_profiles_master_state', JSON.stringify({ masterUserId: MAIN }));
    } catch (e) { /* storage blocked: the scenes still draw */ }

    // ── The scenes ────────────────────────────────────────────────────────────────────
    // Each draws one screen with the client's own methods, then resolves.
    function gate(P) {
        P.isManageMode = false;
        P.showProfileOverlay(PROFILES);
        // A screen drawn over the gate comes after the gate's own first focus (a 100 ms
        // timer on a television). Without the wait that timer takes focus back from the
        // dialog, which no real household sees: the dialog opens long after the gate.
        return new Promise(function (resolve) { setTimeout(resolve, 250); });
    }

    var SCENES = {
        picker: gate,
        announcement: gate,
        // The server's logo, top right: a picture, then a line of text.
        logo: function (P) {
            P.useCustomThemes(withLogo({ image: '/plugins/profiles/themes/logo/' + '9be1'.repeat(8) + '.jpg', text: null, alt: 'The Hayes House' }), false);
            return gate(P);
        },
        // A see-through logo: white, near-black, or orange (fixture.js draws them).
        logowhite: function (P) {
            P.useCustomThemes(withLogo({ image: '/plugins/profiles/themes/logo/' + 'ff'.repeat(16) + '.png', text: null, alt: 'The Hayes House' }), false);
            return gate(P);
        },
        logodark: function (P) {
            P.useCustomThemes(withLogo({ image: '/plugins/profiles/themes/logo/' + '11'.repeat(16) + '.png', text: null, alt: 'The Hayes House' }), false);
            return gate(P);
        },
        logoorange: function (P) {
            P.useCustomThemes(withLogo({ image: '/plugins/profiles/themes/logo/' + 'e8'.repeat(16) + '.png', text: null, alt: 'The Hayes House' }), false);
            return gate(P);
        },
        logotext: function (P) {
            P.useCustomThemes(withLogo({ image: null, text: 'The Hayes House', alt: null }), false);
            return gate(P);
        },
        manage: function (P) {
            P.isManageMode = true;
            P.showProfileOverlay(PROFILES);
            return Promise.resolve();
        },
        pin: function (P) {
            return gate(P).then(function () { P.promptPinEntry(PROFILES[1].profileUserId); });
        },
        dialog: function (P) {
            return gate(P).then(function () {
                P.showConfirmDialog('Delete Sam?', 'Their watch history and settings go too.', function () {}, function () {});
            });
        },
        timeup: function (P) {
            return gate(P).then(function () { P.showTimeUp(PROFILES[3], false); });
        },
        settings: function (P) {
            return gate(P).then(function () { P.showSettingsMenu(); });
        },
        themes: function (P) {
            return gate(P).then(function () { P.showThemeSettings(THEME); });
        }
    };
    // The goldens: every theme draws these. The review scenes below are drawn on request
    // (shoot.js --scene review), in fewer themes, because they are about layout and words.
    window.__scenes = Object.keys(SCENES);

    function manage(P) {
        P.isManageMode = true;
        P.masterPin = '1234';
        P.showProfileOverlay(PROFILES);
        return Promise.resolve();
    }
    // Waits for a screen that draws after a fetch: until `selector` exists, or 3 s.
    function when(selector) {
        return new Promise(function (resolve) {
            var until = Date.now() + 3000;
            (function look() {
                if (document.querySelector(selector) || Date.now() > until) return resolve();
                setTimeout(look, 50);
            })();
        });
    }
    function onboarding(step) {
        return function (P) {
            return gate(P).then(function () {
                P._onboarding = {
                    steps: ['welcome', 'mainPin', 'resetEmail', 'firstProfile', 'switcher', 'theme'].map(function (id) {
                        return { id: id, required: false, done: false, given: false };
                    }),
                    welcome: 'Welcome to the Hayes House. Set up your profiles to get started.',
                    index: step,
                    waiting: false
                };
                P.showOnboardingStep();
            });
        };
    }
    // Opens the More section and scrolls the form to it. Opening it alone left the form at
    // the top, where the shot was the same as the form's own (P2-513).
    function openMore() {
        var d = document.querySelector('details.jpf-more');
        if (!d) return;
        d.open = true;
        var form = d.closest('.jpf-create-profile-container');
        // After the form's own first focus, which scrolls it back to the name field.
        return new Promise(function (resolve) {
            setTimeout(function () {
                if (form) form.scrollTop += d.getBoundingClientRect().top - form.getBoundingClientRect().top - 8;
                resolve();
            }, 400);
        });
    }
    var REVIEW = {
        'add': function (P) { return manage(P).then(function () { P.showAddProfileModal(); return when('#create-name-input'); }); },
        'add-more': function (P) {
            return manage(P).then(function () { P.showAddProfileModal(); return when('details.jpf-more'); })
                .then(openMore);
        },
        'edit': function (P) { return manage(P).then(function () { P.showEditProfileModal(PROFILES[2]); return when('#edit-name-input'); }); },
        'edit-more': function (P) {
            return manage(P).then(function () { P.showEditProfileModal(PROFILES[2]); return when('details.jpf-more'); })
                .then(openMore);
        },
        'account': function (P) { return manage(P).then(function () { P.showAccountSettings(); return when('#account-email-input, .jpf-create-profile-container'); }); },
        'switcher': function (P) { return manage(P).then(function () { P.showSwitcherModeModal(); return when('.jpf-create-profile-container'); }); },
        'sharing': function (P) { return manage(P).then(function () { P.showBonfireModal(); return when('.jpf-create-profile-container'); }); },
        // The crop dialog and the library artwork picker open from a file chooser and a
        // button press; a remote cannot drive the chooser, so these call the opener the
        // chooser's change handler calls, over the edit form.
        'crop': function (P) {
            return manage(P).then(function () {
                P.showEditProfileModal(PROFILES[2]);
                return when('#edit-name-input');
            }).then(function () {
                var c = document.createElement('canvas');
                c.width = 400; c.height = 300;
                var g = c.getContext('2d');
                g.fillStyle = '#c0572b'; g.fillRect(0, 0, 400, 300);
                g.fillStyle = '#f2c14e'; g.fillRect(120, 80, 160, 140);
                return new Promise(function (resolve) {
                    var img = new Image();
                    img.onload = function () { P.showCropDialog(img, function () {}); resolve(); };
                    img.src = c.toDataURL('image/png');
                });
            });
        },
        // A profile's own name-and-picture form (the "me" route's screen), reached from the
        // picker's own-profile button; that button needs a session the fixture has not got.
        'own': function (P) {
            return gate(P).then(function () {
                P._resumeState = { token: 'fixture-token', userId: PROFILES[2].profileUserId };
                P.showOwnProfileForm(PROFILES[2]);
                return when('#self-name-input');
            });
        },
        'libart': function (P) {
            return manage(P).then(function () {
                P.showEditProfileModal(PROFILES[2]);
                return when('#edit-name-input');
            }).then(function () {
                P.pickLibraryArtwork(function () {}, function () {});
                return when('#profiles-libart-dialog');
            });
        },
        'mainpin': function (P) { return gate(P).then(function () { P.promptSetMainPin(PROFILES, false, function () {}); }); },
        'masterpin': function (P) { return gate(P).then(function () { P.promptMasterPinEntry('manage', function () {}); }); },
        'pinreset': function (P) { return gate(P).then(function () { P.showPinReset(function () {}, function () {}); return when('#jpf-pin-reset-code'); }); },
        'maintenance': function (P) {
            return gate(P).then(function () { P.showMaintenance({ message: 'Updating the server. Back soon.', untilUtc: '2026-11-06T03:00:00Z' }, true, false); });
        },
        'panic': function (P) { return gate(P).then(function () { P.showPanicPrompt(); }); },
        // The picker with a request for more time above the tiles (the main account's own
        // household, signed in as the main account).
        'timerequest': function (P) { return gate(P).then(function () { return when('.profiles-time-request'); }); },
        // Jellyfin's header with the Switch Profile button in it, and the corner pill that
        // stands in when no header is found. No picker: the button is what is on screen.
        'header': function (P) {
            document.body.insertAdjacentHTML('afterbegin', '<header class="skinHeader"><div class="headerRight">' +
                '<button class="headerButton headerButton-btn paper-icon-button-light focusable"><span class="material-icons">search</span></button>' +
                '<button class="headerButton headerUserButton paper-icon-button-light focusable"><span class="material-icons">account_circle</span></button>' +
                '</div></header>');
            var right = document.querySelector('.headerRight');
            var bubble = P._buildHeaderBubble();
            P._insertBeforeUserBtn(right, bubble);
            bubble.focus();
            return Promise.resolve();
        },
        // The picker opened on purpose: the header button pressed by its own click
        // handler, so what Back must return to is the button.
        'pickeropen': function (P) {
            document.body.insertAdjacentHTML('afterbegin', '<header class="skinHeader"><div class="headerRight">' +
                '<button class="headerButton headerUserButton paper-icon-button-light focusable"><span class="material-icons">account_circle</span></button>' +
                '</div></header>');
            var bubble = P._buildHeaderBubble();
            P._insertBeforeUserBtn(document.querySelector('.headerRight'), bubble);
            P.attachBubbleClickHandler(bubble);
            // Someone already in a profile, which is who presses the button.
            P._sessionSet(P.config.activeSessionKey, 'fixture-profile-token');
            bubble.focus();
            bubble.click();
            return new Promise(function (resolve) { setTimeout(resolve, 700); });
        },
        'headerpill': function (P) {
            var pill = P._buildFallbackBubble();
            document.documentElement.appendChild(pill);
            pill.focus();
            return Promise.resolve();
        },
        'ob-welcome': onboarding(0),
        'ob-mainpin': onboarding(1),
        'ob-email': onboarding(2),
        'ob-profile': onboarding(3),
        'ob-switcher': onboarding(4),
        'ob-theme': onboarding(5)
    };
    Object.keys(REVIEW).forEach(function (k) { SCENES[k] = REVIEW[k]; });
    window.__reviewScenes = Object.keys(REVIEW);

    window.__scene = function () {
        var P = window.__bonfire;
        var done = function (error) {
            window.__error = error ? String(error && error.stack || error) : null;
            // Two frames: the gate fades in from opacity 0 over two animation frames.
            requestAnimationFrame(function () { requestAnimationFrame(function () {
                var overlay = document.getElementById('profiles-gate-overlay');
                if (overlay) overlay.style.opacity = '';
                var settle = function () { window.__ready = true; };
                (document.fonts && document.fonts.ready ? document.fonts.ready : Promise.resolve()).then(function () {
                    // The jpf-anim-fade-in on the content, and any dialog's own entrance.
                    setTimeout(settle, 900);
                });
            }); });
        };
        try {
            if (!P) throw new Error('the client script did not load');
            P.injectStyles();
            P.applyDeviceClasses();
            // ?photo=<file>&dim=0.8: the theme as a custom one with that background picture,
            // given to the client the way GET themes?part=custom gives it. fixture.js serves
            // a solid picture for a name made of one repeated hex pair ("ff"... is white) and
            // nothing for any other, which is a picture that fails.
            if (q.get('photo')) {
                var base = window.BonfireThemePreview.builtIns().filter(function (t) { return t.id === THEME; })[0];
                base.id = 'photo-' + THEME;
                base.name = { text: 'Photo' };
                base.aliases = [];
                base.background.image = { file: q.get('photo'), dim: Number(q.get('dim') || 0.8) };
                P.useCustomThemes({ themes: [base], fonts: [], hidden: [] }, false);
                THEME = base.id;
            }
            // What loadSwitcherPrefs does with the server's answer: the theme, the fonts.
            if (CUSTOM.length) P.useCustomThemes(withLogo(null), false);
            P._setThemeSettings({ theme: THEME, defaultTheme: 'bonfire', allowProfileThemes: true });
            var run = SCENES[SCENE];
            if (!run) throw new Error('no scene "' + SCENE + '"; have ' + Object.keys(SCENES).join(', '));
            run(P).then(function () { done(null); }, done);
        } catch (e) { done(e); }
    };
})();
