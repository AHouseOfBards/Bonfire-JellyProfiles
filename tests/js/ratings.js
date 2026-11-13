/*
 * The rating limit offers the server's own country's ratings, and only those.
 *
 * The form had four US choices fixed in the script. Jellyfin rates a library by the
 * server's Preferred metadata country and turns every rating into an age score, and a
 * profile's limit is a score too, so enforcement was already international — but a
 * Canadian owner was asked to choose between "PG-13 / TV-14" and "R / TV-MA", and a
 * German one could not pick FSK 16 at all.
 *
 * The fixtures are Jellyfin's own tables (tests/fixtures/ratings, copied from
 * release-10.11.z Emby.Server.Implementations/Localization/Ratings). The server reads the
 * same tables (Configuration/RatingSystems.cs, checked against these fixtures by tests/cs/ratings);
 * serverAnswer() below is the shape the ratings part of GET plugins/profiles/form-options holds for one of them.
 *
 * Not /Localization/ParentalRatings, which the first version of this used: Jellyfin pads
 * that list with generic ages ("10", "13", "14", "21") the country lacks, and France has a
 * real rating named "10" at age 10, so no filter over it can be right for every country.
 *
 *   node tests/js/ratings.js [profiles.js]
 */
'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else {
        fails.push(name + (detail ? '  — ' + detail : ''));
        console.log('  FAIL  ' + name + (detail ? '  — ' + detail : ''));
    }
}

// ── What Jellyfin sends ───────────────────────────────────────────────────────
const FIXTURES = path.join(L.ROOT, 'tests', 'fixtures', 'ratings');

/** What the ratings part of GET plugins/profiles/form-options holds for a server in `code`. */
function serverAnswer(code) {
    const table = JSON.parse(fs.readFileSync(path.join(FIXTURES, code + '.json'), 'utf8'));
    const byScore = {};
    table.ratings.forEach(r => {
        const sc = r.ratingScore.score;
        if (sc >= 1000) return;
        const g = byScore[sc] || (byScore[sc] = { plain: [], all: [] });
        const plain = r.ratingScore.subScore === null || r.ratingScore.subScore === 0;
        r.ratingStrings.forEach(n => {
            if (g.all.indexOf(n) === -1) g.all.push(n);
            if (plain && g.plain.indexOf(n) === -1) g.plain.push(n);
        });
    });
    const ratings = Object.keys(byScore).map(Number).sort((a, b) => a - b)
        .map(sc => ({ score: sc, labels: byScore[sc].plain.length ? byScore[sc].plain : byScore[sc].all }));
    return { table: table, answer: { country: code.toUpperCase(), ratings: ratings } };
}

// ── The client, executed ──────────────────────────────────────────────────────
const src = L.readClientBundle(fs.readFileSync(L.profilesPath(), 'utf8'));
const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) {
    console.error('could not find the single init() call to swap for an export');
    process.exit(1);
}
const sandbox = {
    console: { log() {}, warn() {}, error() {} },
    localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
    sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
    navigator: { userAgent: 'Mozilla/5.0', languages: ['en'] },
    setTimeout() {}, clearTimeout() {}, setInterval() {}, clearInterval() {},
    fetch() { return Promise.resolve({ ok: false, json: () => Promise.reject(new Error('no')) }); },
    location: { hash: '', pathname: '/web/', search: '', reload() {}, replace() {} },
    document: {
        addEventListener() {}, removeEventListener() {},
        querySelector: () => null, querySelectorAll: () => [],
        getElementById: () => null,
        createElement: () => ({ style: {}, classList: { add() {}, remove() {}, contains: () => false, toggle: () => false } }),
        head: { appendChild() {} },
        body: { classList: { add() {}, remove() {}, contains: () => false } },
        documentElement: { style: {}, classList: { add() {}, remove() {}, contains: () => false, toggle: () => false } },
    },
    JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set,
    Uint8ClampedArray,
};
sandbox.ApiClient = { appName: 'Jellyfin Web', deviceName: 'Test', deviceId: 'dev', appVersion: '10.11.0' };
sandbox.window = sandbox;
sandbox.globalThis = sandbox;
vm.createContext(sandbox);
new vm.Script(src.replace(INIT, 'globalThis.__PROFILES = ProfilesPlugin;'), { filename: 'profiles.js' }).runInContext(sandbox);
const P = sandbox.__PROFILES;

/** [{ value, text, selected }] for every option the select offers. */
function options(html) {
    const out = [];
    const re = /<option value="([^"]*)"\s*(selected)?\s*>([^<]*)<\/option>/g;
    let m;
    while ((m = re.exec(html)) !== null) out.push({ value: m[1], selected: !!m[2], text: m[3] });
    return out;
}

const COUNTRIES = ['us', 'ca', 'gb', 'de', 'fr', 'au'];
const US_ONLY = ['PG-13', 'TV-MA', 'TV-14'];

console.log('── each country offers its own ratings ─────────────────────────');
COUNTRIES.forEach(code => {
    const { table, answer: list } = serverAnswer(code);
    const html = P.renderRatingSelect('edit', null, list);
    const opts = options(html);
    const scores = opts.filter(o => o.value !== '').map(o => Number(o.value));

    ok(code + ': "No restrictions" comes first and is selected',
       opts.length > 0 && opts[0].value === '' && opts[0].selected);

    // Every age the country actually has, once — enumerated, not counted.
    const own = Array.from(new Set(table.ratings.map(r => r.ratingScore.score)))
        .filter(sc => sc < 1000).sort((a, b) => a - b);
    own.forEach(sc => ok(code + ': offers its age ' + sc, scores.indexOf(sc) !== -1));
    ok(code + ': each age once (' + scores.join(', ') + ')', new Set(scores).size === scores.length);
    ok(code + ': in order', scores.every((sc, i) => i === 0 || scores[i - 1] < sc));

    // Nothing the country does not have: no filler, no adult or banned, no unrated.
    scores.forEach(sc => ok(code + ': age ' + sc + ' is one of its own', own.indexOf(sc) !== -1));
    ok(code + ': no Unrated, XXX or Banned', !opts.some(o => /Unrated|XXX|Banned/.test(o.text)));

    // Labels come from the country's own names.
    opts.filter(o => o.value !== '').forEach(o => {
        const names = table.ratings.filter(r => r.ratingScore.score === Number(o.value))
            .reduce((a, r) => a.concat(r.ratingStrings), []);
        const shown = o.text.replace(/\s*\(\d+\+\)$/, '').split(' / ');
        ok(code + ': "' + o.text + '" is labelled with its own names',
           shown.length > 0 && shown.every(n => names.indexOf(n) !== -1));
    });

    if (code !== 'us' && code !== 'ca') {
        US_ONLY.forEach(n => ok(code + ': no US label ' + n, !opts.some(o => o.text.indexOf(n) !== -1)));
    }
});

console.log('── a country\'s own rating that looks like a filler stays ────────');
{
    const fr = options(P.renderRatingSelect('edit', null, serverAnswer('fr').answer));
    ok('France offers its own "10"', fr.some(o => o.value === '10' && /^10 /.test(o.text)));
    ok('a PascalCase answer reads the same',
       options(P.renderRatingSelect('edit', null, { Ratings: [{ Score: 12, Labels: ['FSK 12'] }] }))
           .some(o => o.value === '12' && /FSK 12/.test(o.text)));
}

console.log('── content descriptors fold into their age ─────────────────────');
{
    const opts = options(P.renderRatingSelect('create', null, serverAnswer('us').answer));
    const pg = opts.find(o => o.value === '10');
    ok('US 10 is "PG / TV-PG", not fifteen TV-PG-x variants (' + (pg && pg.text) + ')',
       !!pg && /PG/.test(pg.text) && !/TV-PG-/.test(pg.text));
    ok('no label is longer than four names', opts.every(o => o.text.split(' / ').length <= 4));
}

console.log('── a stored limit is never lost ────────────────────────────────');
{
    const de = serverAnswer('de').answer;
    const kept = options(P.renderRatingSelect('edit', 16, de));
    ok('a German FSK 16 profile has FSK 16 selected',
       kept.filter(o => o.selected).map(o => o.value).join() === '16');
    // A limit saved under the US list (14, "PG-13 / TV-14") on a server now in Germany,
    // which has no 14. It must stay selectable as itself, or the next save clears it.
    const moved = options(P.renderRatingSelect('edit', 14, de));
    ok('a limit the country has no rating for is kept and selected',
       moved.filter(o => o.selected).map(o => o.value).join() === '14'
       && moved.some(o => o.value === '14' && /14/.test(o.text)));
}

console.log('── without the server list, the old choices ────────────────────');
[null, undefined, [], { error: 'nope' }, [{ Name: 'Unrated', RatingScore: null }]].forEach((bad, i) => {
    const opts = options(P.renderRatingSelect('create', null, bad));
    ok('unusable answer ' + i + ' falls back to the four US choices',
       opts.map(o => o.value).join() === ',6,10,14,17');
});

console.log('── the forms fetch it and pass it on ───────────────────────────');
{
    const raw = fs.readFileSync(L.profilesPath(), 'utf8');
    ok('the shared form data asks the plugin for the country\'s ratings, in the one form-options read',
       /getUrl\('plugins\/profiles\/form-options'\)/.test(raw) && /ratings: this\._field\(options, 'ratings'\)/.test(raw));
    ok('and not Jellyfin\'s padded list', !/Localization\/ParentalRatings'\)/.test(raw));
    ok('the create form passes them in', /renderRatingSelect\('create', null, ratings\)/.test(raw));
    ok('the edit form passes them in', /renderRatingSelect\('edit', maxRating, ratings\)/.test(raw));
}

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
