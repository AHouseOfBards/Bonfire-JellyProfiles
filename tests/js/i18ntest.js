// The client half of translations, against the real profiles.js.
//
// Same technique as busy.js: evaluate the shipped file with the init() call swapped for
// an export. Here the export also reaches the i18n closure — t, detectLocale, loadLocale
// and the SUPPORTED_LOCALES binding — none of which hang off ProfilesPlugin, and all of
// which decide whether a translation is ever loaded at all.
const fs = require('fs');
const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
}

const SRC_PATH = L.profilesPath();
const src = fs.readFileSync(SRC_PATH, 'utf8');

const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) {
    console.error('could not find the single init() call to swap for an export');
    process.exit(1);
}
const runnable = src.replace(INIT,
    'globalThis.__PROFILES = ProfilesPlugin;' +
    'globalThis.__I18N = {' +
    '  t: t, detectLocale: detectLocale, loadLocale: loadLocale,' +
    // typeof, so the harness still runs against a build from before the watcher and
    // reports it missing rather than dying on a ReferenceError.
    '  watch: typeof watchJellyfinLanguage === "function" ? watchJellyfinLanguage : function () {},' +
    '  en: EN_STRINGS,' +
    '  setLocales: function (l) { SUPPORTED_LOCALES = l; },' +
    '  getLocales: function () { return SUPPORTED_LOCALES; },' +
    '  setStrings: function (s) { activeStrings = s; }' +
    '};');

// The language inputs are modelled on what jellyfin-web actually leaves behind
// (src/lib/globalize and src/scripts/settings, release-10.11.z):
//
//   setting   userSettings.language(), stored as localStorage['<userId>-language'];
//             '' is "Auto"
//   userId    ApiClient.getCurrentUserId(); omit for "nobody signed in yet"
//   htmlLang  <html lang>, which globalize writes
//   culture   <html data-culture>, set by the native app shells
//   language / languages   the browser's own
function build(opts) {
    opts = opts || {};
    const fetched = [];
    const store = Object.assign({}, opts.storage || {});
    if (opts.setting !== undefined) store[(opts.userId || 'u1') + '-language'] = opts.setting;
    const attrs = { lang: opts.htmlLang, 'data-culture': opts.culture };
    const observers = [];
    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage: {
            getItem: k => (Object.prototype.hasOwnProperty.call(store, k) ? store[k] : null),
            setItem(k, v) { store[k] = String(v); }, removeItem(k) { delete store[k]; }
        },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        navigator: {
            userAgent: 'Mozilla/5.0',
            languages: opts.languages,
            language: opts.language
        },
        MutationObserver: function (cb) {
            this.observe = (el, o) => observers.push({ cb, filter: o && o.attributeFilter });
        },
        setTimeout() {}, clearTimeout() {}, setInterval() {}, clearInterval() {},
        fetch(url) {
            fetched.push(url);
            if (opts.fetchImpl) return opts.fetchImpl(url);
            return Promise.resolve({ ok: false, json: () => Promise.resolve(null) });
        },
        location: { hash: '', pathname: '/web/', search: '', reload() {}, replace() {} },
        document: {
            addEventListener() {}, removeEventListener() {},
            querySelector() { return null; }, querySelectorAll() { return []; },
            getElementById: () => null,
            createElement: () => ({ style: {} }),
            head: { appendChild() {} },
            body: { classList: { add() {}, remove() {}, contains: () => false, toggle: () => false } },
            documentElement: {
                style: { cssText: '' }, classList: { add() {}, remove() {}, toggle: () => false },
                getAttribute: n => (attrs[n] === undefined ? null : attrs[n])
            }
        },
        JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set
    };
    if (opts.userId !== undefined || opts.setting !== undefined) {
        sandbox.ApiClient = { getCurrentUserId: () => opts.userId === undefined ? 'u1' : opts.userId };
    }
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;
    vm.createContext(sandbox);
    new vm.Script(runnable, { filename: 'profiles.js' }).runInContext(sandbox);
    // What jellyfin-web does when the Display language is saved: store it, then rewrite
    // <html lang>, which is the only thing a script outside it can observe.
    const changeLanguage = (setting, lang) => {
        store[(opts.userId || 'u1') + '-language'] = setting;
        attrs.lang = lang === undefined ? setting : lang;
        observers.filter(o => !o.filter || o.filter.indexOf('lang') !== -1).forEach(o => o.cb([]));
    };
    return { i18n: sandbox.__I18N, fetched, sandbox, observers, changeLanguage };
}

// ── The catalogue ───────────────────────────────────────────────────────────
console.log();
console.log('── English is the built-in fallback ───────────────────────────');

const base = build({ setting: 'en-GB', language: 'en-GB' });
const I = base.i18n;

ok('the i18n layer exists', I && typeof I.t === 'function');
ok('English is inline, not fetched', Object.keys(I.en).length > 100);
ok('nothing is fetched for an English browser', base.fetched.length === 0);

ok('a known key resolves', I.t('gate.whosWatching') === "Who's Watching?");
ok('an unknown key degrades to the key itself, never to blank',
    I.t('nope.not.here') === 'nope.not.here');

console.log();
console.log('── Placeholders ───────────────────────────────────────────────');

const withVar = Object.keys(I.en).filter(k => /\{[a-z]+\}/i.test(I.en[k]));
ok('some strings take placeholders (' + withVar.length + ')', withVar.length > 0);
ok('a placeholder is filled in',
    I.t('profilePage.body', { name: 'Bard' }).includes('Bard'));
ok('an unfilled placeholder is left alone rather than printed as undefined',
    I.t('profilePage.body', {}).includes('{name}'));
ok('a repeated placeholder is filled at every occurrence', (() => {
    const e = build({ setting: 'en' });
    e.i18n.setStrings({ 'x': '{n} of {n}' });
    return e.i18n.t('x', { n: '3' }) === '3 of 3';
})());

// ── Locale detection ────────────────────────────────────────────────────────
console.log();
console.log('── Choosing a language ────────────────────────────────────────');

// The Display language set in Jellyfin, on a browser that wants something else, so a
// pass cannot come from reading the browser.
function detectWith(setting, locales) {
    const env = build({ setting, language: 'de-DE', languages: ['de-DE', 'tr'] });
    env.i18n.setLocales(locales);
    return env.i18n.detectLocale();
}
function detectIn(opts, locales) {
    const env = build(opts);
    env.i18n.setLocales(locales);
    return env.i18n.detectLocale();
}
const ALL = ['ca', 'fr', 'pl', 'tr', 'de'];

ok('the shipped list is empty until the server fills it in',
    Array.isArray(I.getLocales()) && I.getLocales().length === 0);

// The reason this section exists. Until 1.7.0 the browser's language list decided, so
// Jellyfin's own Display language setting did nothing to Bonfire's screens.
ok("Jellyfin's Display language decides, not the browser's",
    detectIn({ setting: 'fr', language: 'de-DE', languages: ['de-DE'] }, ALL) === 'fr');
ok('Jellyfin set to English stays English on a French browser',
    detectIn({ setting: 'en-US', language: 'fr-FR', languages: ['fr-FR', 'fr'] }, ALL) === null);
ok('a second browser preference Jellyfin does not show is not used',
    detectIn({ setting: 'en-GB', language: 'en-GB', languages: ['en-GB', 'pl'] }, ALL) === null);
ok("the setting is read under the signed-in user, not another user's",
    detectIn({ userId: 'u2', storage: { 'u1-language': 'fr' }, language: 'tr-TR' }, ALL) === 'tr');
ok("the setting wins over <html lang>, which lags it until Jellyfin's sign-in event",
    detectIn({ setting: 'pl', htmlLang: 'de-de', language: 'de-DE' }, ALL) === 'pl');

// "Auto" is an empty setting, and Jellyfin then shows getDefaultLanguage().
ok('Auto follows the language Jellyfin falls back to: navigator.language',
    detectIn({ setting: '', language: 'ca-ES', languages: ['de', 'ca-ES'] }, ALL) === 'ca');
ok('Auto takes data-culture from a native app shell first',
    detectIn({ setting: '', culture: 'tr', language: 'fr-FR' }, ALL) === 'tr');
ok('a user who never opened the setting is on Auto too',
    detectIn({ userId: 'u1', language: 'pl-PL' }, ALL) === 'pl');
ok('before anyone is signed in, <html lang> is what Jellyfin is showing',
    detectIn({ htmlLang: 'fr', language: 'de-DE' }, ALL) === 'fr');
ok('and with no <html lang> either, Jellyfin\'s own default',
    detectIn({ language: 'ca' }, ALL) === 'ca');
ok('nothing at all stays on English', detectIn({}, ALL) === null);
ok('en_US is read the way globalize normalises it',
    detectIn({ setting: 'fr_CA', language: 'de' }, ALL) === 'fr');

ok('an exact match is chosen', detectWith('fr', ['fr']) === 'fr');
ok('a regional setting falls back to its base language',
    detectWith('fr-CA', ['fr']) === 'fr');
ok('an unsupported language stays on English', detectWith('es', ['fr']) === null);
ok('an empty list stays on English', detectWith('fr', []) === null);

// The two-character slice this started as could not tell pt-BR from pt-PT, and would
// have handed a Brazilian reader a European file neither of them chose.
ok('a regional file is preferred over its base language',
    detectWith('pt-BR', ['pt', 'pt-BR']) === 'pt-BR');
ok('and the base is still used when there is no regional file',
    detectWith('pt-BR', ['pt']) === 'pt');
ok('matching is case-insensitive both ways',
    detectWith('PT-br', ['pt-BR']) === 'pt-BR');
ok('a longer subtag works', detectWith('zh-Hans-CN', ['zh-Hans']) === 'zh-Hans');

// A code reaches a URL. It comes from the server, but a malformed one must not.
ok('a malformed code is refused rather than fetched',
    detectWith('fr', ['../../etc/passwd']) === null);
ok('so is one carrying a slash', detectWith('fr', ['fr/x']) === null);
ok('and one that is not a string', detectWith('fr', [{ toString: () => 'fr' }]) === null);

// ── Loading ─────────────────────────────────────────────────────────────────
console.log();
console.log('── Fetching a translation ─────────────────────────────────────');

function loadWith(setting, locales, fetchImpl) {
    const env = build({ setting, fetchImpl });
    env.i18n.setLocales(locales);
    return env.i18n.loadLocale().then(() => env);
}

const checks = [];

checks.push(loadWith('fr', ['fr'],
    () => Promise.resolve({ ok: true, json: () => Promise.resolve({ 'gate.whosWatching': 'Qui regarde ?' }) })
).then(env => {
    ok('the right file is requested',
        env.fetched.length === 1 && env.fetched[0].includes('/plugins/profiles/i18n/fr.json'));
    ok('the translation is applied', env.i18n.t('gate.whosWatching') === 'Qui regarde ?');
    ok('a key the file omits still resolves, in English',
        env.i18n.t('common.cancel') === 'Cancel');
}));

checks.push(loadWith('tr-TR', ['tr'],
    () => Promise.resolve({
        ok: true,
        json: () => Promise.resolve(JSON.parse(fs.readFileSync('Web/i18n/tr.json', 'utf8')))
    })
).then(env => {
    ok('Jellyfin set to Turkish (Turkey) requests the Turkish file',
        env.fetched.length === 1 && env.fetched[0].includes('/plugins/profiles/i18n/tr.json'));
    ok('the Turkish translation is applied',
        env.i18n.t('gate.whosWatching') === 'Kim izliyor?');
}));

checks.push(loadWith('de', ['fr']).then(env => {
    ok('an unsupported language fetches nothing at all', env.fetched.length === 0);
    ok('and stays on English', env.i18n.t('common.cancel') === 'Cancel');
}));

checks.push(loadWith('fr', ['fr'],
    () => Promise.resolve({ ok: false, json: () => Promise.resolve(null) })
).then(env => {
    ok('a 404 leaves English in place', env.i18n.t('common.cancel') === 'Cancel');
}));

checks.push(loadWith('fr', ['fr'],
    () => Promise.reject(new Error('offline'))
).then(env => {
    ok('a network failure leaves English in place', env.i18n.t('common.cancel') === 'Cancel');
}));

checks.push(loadWith('fr', ['fr'],
    () => Promise.resolve({ ok: true, json: () => Promise.resolve('not an object') })
).then(env => {
    ok('a garbage payload is ignored', env.i18n.t('common.cancel') === 'Cancel');
}));

// ── Changing the Display language without a reload ──────────────────────────
// jellyfin-web applies a new Display language in place: it stores it and rewrites
// <html lang>. Bonfire has to follow it there, and back to English, which until now it
// never had to do because nothing ever changed after startup.
const FILES = {
    fr: { 'common.cancel': 'Annuler' },
    pl: { 'common.cancel': 'Anuluj' }
};
function serveFiles(env, delays) {
    return url => {
        const code = /i18n\/([^/.]+)\.json/.exec(url)[1];
        const body = { ok: true, json: () => Promise.resolve(FILES[code]) };
        const wait = (delays && delays[code]) || 0;
        return new Promise(r => setTimeout(() => r(body), wait));
    };
}
function liveEnv(setting) {
    let impl = null;
    const env = build({ setting, fetchImpl: url => impl(url) });
    env.setFetch = f => { impl = f; };
    env.setFetch(serveFiles(env));
    env.i18n.setLocales(['fr', 'pl']);
    let ready = env.i18n.loadLocale();
    env.i18n.watch(() => { ready = env.i18n.loadLocale(); });
    env.ready = () => ready;
    return env;
}

checks.push((() => {
    const env = liveEnv('en-US');
    return env.ready().then(() => {
        ok('the watcher observes <html lang> and only that',
            env.observers.length === 1 && String(env.observers[0].filter) === 'lang');
        env.changeLanguage('fr');
        return env.ready();
    }).then(() => {
        ok('switching Jellyfin to French switches Bonfire to French',
            env.i18n.t('common.cancel') === 'Annuler');
        env.changeLanguage('fr-CA', 'fr-ca');
        return env.ready();
    }).then(() => {
        ok('a change that lands on the same file fetches nothing',
            env.fetched.filter(u => /fr\.json/.test(u)).length === 1);
        env.changeLanguage('en-US');
        return env.ready();
    }).then(() => {
        ok('switching back to English goes back to English',
            env.i18n.t('common.cancel') === 'Cancel');
        env.changeLanguage('fr');
        return env.ready();
    }).then(() => {
        ok('and back to French fetches it again',
            env.i18n.t('common.cancel') === 'Annuler');
    });
})());

checks.push((() => {
    const env = liveEnv('en-US');
    // French is slow to arrive, and the user has moved on to Polish before it does.
    env.setFetch(serveFiles(env, { fr: 60 }));
    env.changeLanguage('fr');
    const slow = env.ready();
    env.changeLanguage('pl');
    return Promise.all([slow, env.ready()]).then(() => {
        ok('a slow response for a language already left does not land over the new one',
            env.i18n.t('common.cancel') === 'Anuluj');
    });
})());

// ── Coverage of the shipped translations ────────────────────────────────────
Promise.all(checks).then(() => {
    console.log();
    console.log('── The shipped translation files ──────────────────────────────');

    const dir = 'Web/i18n';
    const files = fs.existsSync(dir)
        ? fs.readdirSync(dir).filter(f => f.endsWith('.json'))
        : [];

    ok('there is at least one translation to check (' + files.length + ')', files.length > 0);

    const enKeys = Object.keys(I.en);
    files.forEach(f => {
        const code = f.replace(/\.json$/, '');
        let data = null;
        try { data = JSON.parse(fs.readFileSync(dir + '/' + f, 'utf8')); } catch (e) { /* reported below */ }

        ok(f + ' is valid JSON', data !== null && typeof data === 'object');
        if (!data) return;

        ok(f + ' is named as a locale code a browser would send',
            /^[a-z]{2,3}(-[a-z0-9]{2,8})*$/i.test(code));

        const missing = enKeys.filter(k => !(k in data));
        const unknown = Object.keys(data).filter(k => !(k in I.en));
        ok(f + ' has no keys the catalogue does not (' + unknown.length + ')', unknown.length === 0);
        if (unknown.length) console.log('        ' + unknown.slice(0, 6).join(', '));

        ok(f + ' covers every key (' + (enKeys.length - missing.length) + '/' + enKeys.length + ')',
            missing.length === 0);
        if (missing.length) console.log('        missing: ' + missing.slice(0, 6).join(', '));

        // A translation that drops a {token} prints the literal brace to a user; one that
        // drops a tag breaks the layout, because these land in innerHTML.
        const tok = s => (String(s).match(/\{[a-zA-Z0-9_]+\}/g) || []).sort().join(' ');
        const tag = s => (String(s).match(/<\/?[a-zA-Z][^>]*>/g) || []).map(x => x.toLowerCase()).sort().join(' ');
        const badTok = Object.keys(data).filter(k => k in I.en && tok(data[k]) !== tok(I.en[k]));
        const badTag = Object.keys(data).filter(k => k in I.en && tag(data[k]) !== tag(I.en[k]));
        ok(f + ' keeps every placeholder', badTok.length === 0);
        if (badTok.length) console.log('        ' + badTok.slice(0, 6).join(', '));
        ok(f + ' keeps every HTML tag', badTag.length === 0);
        if (badTag.length) console.log('        ' + badTag.slice(0, 6).join(', '));
    });

    console.log();
    if (fails.length) {
        fails.forEach(f => console.log('   - ' + f));
        console.log(pass + ' passed, ' + fails.length + ' failed');
        process.exit(1);
    }
    console.log(pass + ' passed, 0 failed');
});
