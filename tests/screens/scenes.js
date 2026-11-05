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
    var PROFILES = people.map(function (p) {
        return {
            profileUserId: p.id, profileName: p.name, masterUserId: MAIN,
            isMaster: !!p.master, isBonfire: false, isLinked: false,
            requiresPin: !!p.pin, avatarColor: p.colour, avatarInitial: p.name.charAt(0),
            profileImage: null, transparentAvatar: false, maxSubProfiles: 5,
            viewing: p.viewing || null
        };
    });

    // Plugin routes the screens ask for, by path after /plugins/profiles/. A function gets
    // the URL and the request options.
    // The gate warms the profile form's data while the grid is read; empty lists are what
    // a household with nothing set up yet gets.
    var empty = function () { return []; };
    var ROUTES = {
        'libraries': empty, 'devices': empty, 'avatars': empty, 'ratings': empty,
        'announcements': function () {
            return SCENE === 'announcement'
                ? [{ id: 'a1', severity: 'info', message: 'The server restarts at 2am for an update.' }]
                : [];
        }
    };

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
    document.documentElement.classList.add(DEVICE === 'tv' ? 'layout-tv' : 'layout-mobile');
    try {
        localStorage.clear();
        localStorage.setItem('jellyfin_profiles_master_state', JSON.stringify({ masterUserId: MAIN }));
    } catch (e) { /* storage blocked: the scenes still draw */ }

    // ── The scenes ────────────────────────────────────────────────────────────────────
    // Each draws one screen with the client's own methods, then resolves.
    function gate(P) {
        P.isManageMode = false;
        P.showProfileOverlay(PROFILES);
        return Promise.resolve();
    }

    var SCENES = {
        picker: gate,
        announcement: gate,
        // The server's logo, top right: a picture, then a line of text.
        logo: function (P) {
            P.useCustomThemes({ logo: { image: '/plugins/profiles/themes/logo/' + '9be1'.repeat(8) + '.jpg', text: null, alt: 'The Hayes House' } }, false);
            return gate(P);
        },
        logotext: function (P) {
            P.useCustomThemes({ logo: { image: null, text: 'The Hayes House', alt: null } }, false);
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
    window.__scenes = Object.keys(SCENES);

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
                    // The anim-fade-in on the content, and any dialog's own entrance.
                    setTimeout(settle, 900);
                });
            }); });
        };
        try {
            if (!P) throw new Error('the client script did not load');
            P.injectStyles();
            P.applyDeviceClasses();
            // What loadSwitcherPrefs does with the server's answer: the theme, the fonts.
            P._setThemeSettings({ theme: THEME, defaultTheme: 'bonfire', allowProfileThemes: true });
            var run = SCENES[SCENE];
            if (!run) throw new Error('no scene "' + SCENE + '"; have ' + Object.keys(SCENES).join(', '));
            run(P).then(function () { done(null); }, done);
        } catch (e) { done(e); }
    };
})();
