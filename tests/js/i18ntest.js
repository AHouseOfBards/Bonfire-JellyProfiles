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
    '  en: EN_STRINGS,' +
    '  setLocales: function (l) { SUPPORTED_LOCALES = l; },' +
    '  getLocales: function () { return SUPPORTED_LOCALES; },' +
    '  setStrings: function (s) { activeStrings = s; }' +
    '};');

function build(opts) {
    opts = opts || {};
    const fetched = [];
    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        navigator: {
            userAgent: 'Mozilla/5.0',
            languages: opts.languages,
            language: opts.language
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
            documentElement: { style: { cssText: '' }, classList: { add() {}, remove() {}, toggle: () => false } }
        },
        JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set
    };
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;
    vm.createContext(sandbox);
    new vm.Script(runnable, { filename: 'profiles.js' }).runInContext(sandbox);
    return { i18n: sandbox.__I18N, fetched, sandbox };
}

// ── The catalogue ───────────────────────────────────────────────────────────
console.log();
console.log('── English is the built-in fallback ───────────────────────────');

const base = build({ languages: ['en-GB', 'en'] });
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
    const e = build({ languages: ['en'] });
    e.i18n.setStrings({ 'x': '{n} of {n}' });
    return e.i18n.t('x', { n: '3' }) === '3 of 3';
})());

// ── Locale detection ────────────────────────────────────────────────────────
console.log();
console.log('── Choosing a language ────────────────────────────────────────');

function detectWith(languages, locales) {
    const env = build({ languages });
    env.i18n.setLocales(locales);
    return env.i18n.detectLocale();
}

ok('the shipped list is empty until the server fills it in',
    Array.isArray(I.getLocales()) && I.getLocales().length === 0);

ok('an exact match is chosen', detectWith(['fr'], ['fr']) === 'fr');
ok('a regional tag falls back to its base language',
    detectWith(['fr-CA'], ['fr']) === 'fr');
ok('the first supported language wins, not the first preferred',
    detectWith(['de', 'fr', 'es'], ['fr']) === 'fr');
ok('no supported language stays on English', detectWith(['de', 'es'], ['fr']) === null);
ok('an empty list stays on English', detectWith(['fr'], []) === null);

// The two-character slice this started as could not tell pt-BR from pt-PT, and would
// have handed a Brazilian reader a European file neither of them chose.
ok('a regional file is preferred over its base language',
    detectWith(['pt-BR', 'pt'], ['pt', 'pt-BR']) === 'pt-BR');
ok('and the base is still used when there is no regional file',
    detectWith(['pt-BR'], ['pt']) === 'pt');
ok('matching is case-insensitive both ways',
    detectWith(['PT-br'], ['pt-BR']) === 'pt-BR');
ok('a longer subtag works', detectWith(['zh-Hans-CN'], ['zh-Hans']) === 'zh-Hans');

ok('navigator.language is used when navigator.languages is empty',
    (() => { const e = build({ languages: [], language: 'fr-FR' }); e.i18n.setLocales(['fr']); return e.i18n.detectLocale(); })() === 'fr');

// A code reaches a URL. It comes from the server, but a malformed one must not.
ok('a malformed code is refused rather than fetched',
    detectWith(['fr'], ['../../etc/passwd']) === null);
ok('so is one carrying a slash', detectWith(['fr'], ['fr/x']) === null);
ok('and one that is not a string', detectWith(['fr'], [{ toString: () => 'fr' }]) === null);

// ── Loading ─────────────────────────────────────────────────────────────────
console.log();
console.log('── Fetching a translation ─────────────────────────────────────');

function loadWith(languages, locales, fetchImpl) {
    const env = build({ languages, fetchImpl });
    env.i18n.setLocales(locales);
    return env.i18n.loadLocale().then(() => env);
}

const checks = [];

checks.push(loadWith(['fr'], ['fr'],
    () => Promise.resolve({ ok: true, json: () => Promise.resolve({ 'gate.whosWatching': 'Qui regarde ?' }) })
).then(env => {
    ok('the right file is requested',
        env.fetched.length === 1 && env.fetched[0].includes('/plugins/profiles/i18n/fr.json'));
    ok('the translation is applied', env.i18n.t('gate.whosWatching') === 'Qui regarde ?');
    ok('a key the file omits still resolves, in English',
        env.i18n.t('common.cancel') === 'Cancel');
}));

checks.push(loadWith(['de'], ['fr']).then(env => {
    ok('an unsupported language fetches nothing at all', env.fetched.length === 0);
    ok('and stays on English', env.i18n.t('common.cancel') === 'Cancel');
}));

checks.push(loadWith(['fr'], ['fr'],
    () => Promise.resolve({ ok: false, json: () => Promise.resolve(null) })
).then(env => {
    ok('a 404 leaves English in place', env.i18n.t('common.cancel') === 'Cancel');
}));

checks.push(loadWith(['fr'], ['fr'],
    () => Promise.reject(new Error('offline'))
).then(env => {
    ok('a network failure leaves English in place', env.i18n.t('common.cancel') === 'Cancel');
}));

checks.push(loadWith(['fr'], ['fr'],
    () => Promise.resolve({ ok: true, json: () => Promise.resolve('not an object') })
).then(env => {
    ok('a garbage payload is ignored', env.i18n.t('common.cancel') === 'Cancel');
}));

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
