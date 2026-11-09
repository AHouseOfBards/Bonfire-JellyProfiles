/*
 * The dashboard's fixture: Dashboard → Bonfire with no Jellyfin server, one tab at a time.
 * Loaded before the page's own markup and script. The address says what to draw:
 * /dashboard?tab=look&device=desktop.
 *
 * What is real: the page exactly as the plugin ships it (Web/profilesDashboard.html), its
 * script and its English. What is not: jellyfin-web around it. Jellyfin's dashboard styles
 * its form controls (emby-input, emby-checkbox, emby-select) and the page chrome; page.css
 * here only approximates them, so these pictures are for layout, density and words, not
 * for how a control looks inside a real dashboard (that is phase 5, R-0008).
 *
 * The answers are shaped as docs/developer-api.md says, for one server with two
 * households. Anything the stub cannot answer is in window.__misses.
 */
(function () {
    'use strict';

    var q = new URLSearchParams(location.search);
    var TAB = q.get('tab') || 'status';
    var DEVICE = q.get('device') || 'desktop';
    var ADMIN = 'aaaaaaaa000000000000000000000001';
    var ALEX = '11111111111111111111111111111111';
    var JO = '66666666666666666666666666666666';

    window.__misses = [];
    window.__fixture = { TAB: TAB, DEVICE: DEVICE };
    try { localStorage.clear(); localStorage.setItem('bonfire-admin-tab', TAB); } catch (e) { /* blocked */ }

    function profile(id, master, name, masterId, extra) {
        return Object.assign({ ProfileUserId: id, MasterUserId: masterId, ProfileName: name, IsMaster: master,
            HasPin: !master || name === 'Alex', AvatarColor: '#3d8bfd', LockoutMinutes: 5 }, extra || {});
    }
    var config = {
        MaxProfilesPerUser: 5, RequireMainPin: false, DisallowCustomAvatarUploads: false, EnableStarterAvatars: true,
        ProfileImageQuality: 'standard', DefaultAskOnStartup: true, EnableClientPinLogin: false,
        EnableClientProfileList: false, SkipQuickConnectOnKnownDevices: false, ClientPinLoginFromAnywhere: false, ProxyClients: [],
        QuickConnectSharedDeviceNames: [], EnablePinResetByEmail: false, DefaultSwitcherLocation: 'button',
        DefaultTheme: 'bonfire', AllowProfileThemes: true, DefaultTimeZone: '', IndexInjectionMode: 'middleware',
        Onboarding: { Enabled: true, RequireMainPin: false, WelcomeText: '' },
        UserProfileLimitOverrides: [], CustomThemes: [], HiddenThemes: [], CustomFonts: []
    };
    var mappings = [
        profile(ALEX, true, 'Alex', ALEX), profile('22222222222222222222222222222222', false, 'Sam', ALEX),
        profile('33333333333333333333333333333333', false, 'Rosalind', ALEX), profile('44444444444444444444444444444444', false, 'Zoë', ALEX),
        profile(JO, true, 'Jo', JO), profile('77777777777777777777777777777777', false, 'Kids', JO)
    ];
    var themes = null;

    var ROUTES = {
        'admin/mappings': function () {
            var main = mappings.filter(function (p) { return p.IsMaster; });
            return {
                masterUsers: main.map(function (p) { return { profileUserId: p.ProfileUserId, profileName: p.ProfileName, requiresPin: p.HasPin, maxProfiles: 5, limitOverride: null }; }),
                subProfiles: mappings.filter(function (p) { return !p.IsMaster; }).map(function (p) {
                    return { profileUserId: p.ProfileUserId, profileName: p.ProfileName, masterName: p.MasterUserId === ALEX ? 'Alex' : 'Jo', masterUserId: p.MasterUserId, requiresPin: p.HasPin };
                }),
                injectionSucceeded: true, isVersionStale: false, indexPath: '/usr/share/jellyfin/web/index.html', failureReason: null,
                serviceAccount: 'jellyfin', isWindows: false, pluginVersion: '1.7.2.0',
                mechanism: { mode: 'middleware', restartRequired: false, middlewareActive: true }
            };
        },
        'admin/sessions': function () { return []; },
        'admin/audit-logs': function () {
            return [
                { timestamp: '2026-11-04T19:02:00Z', masterUsername: 'Alex', targetUsername: 'Rosalind', deviceName: 'Living Room TV', client: 'Jellyfin webOS', ipAddress: '192.168.1.44' },
                { timestamp: '2026-11-04T18:40:00Z', masterUsername: 'Jo', targetUsername: 'Kids', deviceName: 'Chrome', client: 'Jellyfin Web', ipAddress: '192.168.1.50' }];
        },
        'avatars': function () { return []; },
        'admin/announcements': function () { return []; },
        'themes': function () { return themes; },
        'admin/themes': function () {
            return { 'default': 'bonfire', profilesCanChoose: true, maxCustomThemes: 40, maxCustomFonts: 20, maxFontBytes: 2097152,
                builtIn: (themes && themes.themes || []).map(function (t) { return { id: t.id, hidden: false }; }), custom: [], fonts: [],
                builtInFonts: (themes && themes.fonts || []).map(function (f) { return f.family; }) };
        },
        'admin/maintenance': function () { return { enabled: false, message: '', untilUtc: null, startedUtc: null, stopPlayback: false }; },
        'admin/email': function () {
            return { host: '', port: 587, security: 'starttls', username: '', hasPassword: false, fromAddress: '', fromName: '',
                configured: false, verified: false, pinResetByEmail: false, accounts: [] };
        },
        'admin/panic-status': function () { return { isConfigured: false, isCurrentlyDisabled: false }; },
        'timezones': function () {
            return { zones: ['America/Chicago', 'America/New_York', 'Europe/London', 'Europe/Paris'], server: 'America/Chicago',
                'default': '', household: '', effective: 'America/Chicago' };
        },
        'admin/cleanup-orphans': function () { return { applied: false, orphans: [], deadGroups: [], deadMembers: [] }; },
        'info': function () { return { version: '1.7.2.0', informationalVersion: '1.7.2-beta' }; }
    };

    function lookup(url, method) {
        var u = new URL(url, location.href);
        var m = /^\/plugins\/profiles\/(.+)$/.exec(u.pathname);
        var key = m && m[1].replace(/\.json$/, '');
        if (key && Object.prototype.hasOwnProperty.call(ROUTES, key)) return { ok: true, body: ROUTES[key](u, method) };
        if (m && /^i18n\//.test(m[1])) return { ok: false, status: 404 };
        window.__misses.push((method || 'GET') + ' ' + u.pathname + u.search);
        return { ok: false, status: 404 };
    }

    // The built-in themes, from the real catalogue route's files, read once at start.
    var ready = fetch('/fixture/themes.json').then(function (r) { return r.json(); }).then(function (t) { themes = t; }, function () { themes = []; });

    var realFetch = window.fetch.bind(window);
    window.fetch = function (input, init) {
        var url = typeof input === 'string' ? input : input.url;
        if (/^\/fixture\//.test(new URL(url, location.href).pathname)) return realFetch(input, init);
        var r = lookup(url, init && init.method);
        return Promise.resolve(new Response(JSON.stringify(r.ok ? r.body : {}), { status: r.ok ? 200 : r.status, headers: { 'Content-Type': 'application/json' } }));
    };

    var api = {
        getCurrentUserId: function () { return ADMIN; },
        getUrl: function (p, query) {
            var s = location.origin + '/' + String(p).replace(/^\//, '');
            if (query) s += '?' + new URLSearchParams(query).toString();
            return s;
        },
        accessToken: function () { return 'fixture-token'; },
        serverAddress: function () { return location.origin; },
        getPluginConfiguration: function () { return Promise.resolve(JSON.parse(JSON.stringify(config))); },
        updatePluginConfiguration: function () { return Promise.resolve({}); },
        getJSON: function (url) {
            var r = lookup(url, 'GET');
            return r.ok ? Promise.resolve(r.body) : Promise.reject({ status: r.status });
        },
        ajax: function (o) {
            var r = lookup(o.url, o.type || 'GET');
            if (!r.ok) return Promise.reject({ status: r.status });
            if (o.dataType === 'json') return Promise.resolve(r.body);
            return Promise.resolve(new Response(JSON.stringify(r.body), { status: 200 }));
        },
        getCurrentUser: function () { return Promise.resolve({ Id: ADMIN, Name: 'admin', Policy: { IsAdministrator: true } }); }
    };
    window.ApiClient = new Proxy(api, {
        get: function (t, name) {
            if (name in t) return t[name];
            if (typeof name !== 'string' || name === 'then') return undefined;
            return function () { window.__misses.push('ApiClient.' + name); return Promise.resolve({}); };
        }
    });
    window.Dashboard = {
        showLoadingMsg: function () {}, hideLoadingMsg: function () {},
        alert: function () {}, confirm: function () { return new Promise(function () {}); },
        processPluginConfigurationUpdateResult: function () {}
    };

    document.documentElement.classList.add(DEVICE === 'desktop' ? 'layout-desktop' : 'layout-mobile');

    // After the page's markup and script: what jellyfin-web does when it opens the page.
    window.__show = function () {
        ready.then(function () {
            var page = document.getElementById('profilesConfigurationPage');
            try {
                page.dispatchEvent(new Event('pageshow'));
                window.__error = null;
            } catch (e) { window.__error = String(e && e.stack || e); }
            // Every load is a promise on the stub; give them a moment, then the fonts.
            setTimeout(function () {
                (document.fonts ? document.fonts.ready : Promise.resolve()).then(function () { window.__ready = true; });
            }, 600);
        });
    };
    window.__tabs = ['status', 'households', 'devices', 'security', 'look', 'setup', 'apps', 'messages', 'activity', 'troubleshoot'];
})();
