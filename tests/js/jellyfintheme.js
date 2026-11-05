/*
 * Jellyfin Dark and Jellyfin Light offer to match jellyfin-web's own theme, and never
 * set it unasked.
 *
 * jellyfin-web keeps each user's theme in this device's localStorage as
 * "<userId>-appTheme" ('dark' when unset). Bonfire writes that key only when the person
 * takes the offer in Settings → Theme. This runs the real functions out of profiles.js:
 * the key, the decision of what to offer, and the offer itself against a small stand-in
 * for the page, through both answers and both ways localStorage can refuse.
 *
 *   node tests/js/jellyfintheme.js [path/to/profiles.js]
 */
'use strict';

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

const SRC = L.readProfiles();

function pick(re) { const m = re.exec(SRC); return m ? m[0] : null; }
function grab(name) { try { return L.extractFunction(SRC, name); } catch (e) { return null; } }

// The method, brace-matched out of the object literal.
function method(name) {
    const at = SRC.indexOf('        ' + name + ': function (');
    if (at === -1) return null;
    const open = SRC.indexOf('{', at);
    let depth = 0;
    for (let i = open; i < SRC.length; i++) {
        if (SRC[i] === '{') depth++;
        else if (SRC[i] === '}' && --depth === 0) {
            return { params: SRC.slice(SRC.indexOf('(', at) + 1, SRC.indexOf(')', at)), body: SRC.slice(open + 1, i) };
        }
    }
    return null;
}

console.log('\n── The pieces are in the client ──────────────────────────────');
const parts = {
    THEMES: pick(/const THEMES = \[[^\]]*\];/),
    JELLYFIN_WEB_THEMES: pick(/const JELLYFIN_WEB_THEMES = \{[^}]*\};/),
    normalizeTheme: grab('normalizeTheme'),
    themeName: grab('themeName'),
    escapeHtml: grab('escapeHtml'),
    jellyfinWebThemeKey: grab('jellyfinWebThemeKey'),
    jellyfinWebOffer: grab('jellyfinWebOffer'),
    _offerJellyfinTheme: method('_offerJellyfinTheme')
};
Object.keys(parts).forEach(k => ok(k + ' is in profiles.js', !!parts[k]));
if (Object.keys(parts).some(k => !parts[k])) finish();

function load(store, opts) {
    opts = opts || {};
    const box = {
        hidden: true, html: '', handlers: {},
        set innerHTML(v) { this.html = v; this.handlers = {}; },
        get innerHTML() { return this.html; },
        querySelector(sel) {
            const id = sel.replace(/^#/, '');
            if (this.html.indexOf('id="' + id + '"') === -1) return null;
            return { addEventListener: (ev, fn) => { if (ev === 'click') this.handlers[id] = fn; } };
        }
    };
    const document = { querySelector: sel => (sel === '#theme-settings-match' && !opts.noBox ? box : null) };
    const localStorage = {
        getItem: k => { if (opts.getThrows) throw new Error('blocked'); return k in store ? store[k] : null; },
        setItem: (k, v) => { if (opts.setThrows) throw new Error('quota'); store[k] = String(v); }
    };
    const t = (key, vars) => key + (vars ? JSON.stringify(vars) : '');
    const factory = new Function('document', 'localStorage', 't',
        [parts.THEMES, parts.JELLYFIN_WEB_THEMES, parts.normalizeTheme, parts.themeName, parts.escapeHtml,
            parts.jellyfinWebThemeKey, parts.jellyfinWebOffer].join('\n')
        + '\nconst obj = {};\nobj._offerJellyfinTheme = function (' + parts._offerJellyfinTheme.params + ') {'
        + parts._offerJellyfinTheme.body + '};\n'
        + 'return { obj, key: jellyfinWebThemeKey, offer: jellyfinWebOffer };');
    return Object.assign({ box, store }, factory(document, localStorage, t));
}

const ID = '4F3A9C2E-1B7D-4E8A-9C3F-2A1B0C9D8E7F';
const KEY = '4f3a9c2e1b7d4e8a9c3f2a1b0c9d8e7f-appTheme';
const person = { profileUserId: ID, profileName: 'Riley' };

console.log('\n── The key is jellyfin-web\'s ────────────────────────────────');
{
    const w = load({});
    ok('a dashed id becomes 32 hex digits and -appTheme', w.key(ID) === KEY, w.key(ID));
    ok('an id already without dashes is the same key', w.key('4f3a9c2e1b7d4e8a9c3f2a1b0c9d8e7f') === KEY);
    ok('anything that is not an id has no key', w.key('') === null && w.key(null) === null
        && w.key('../x') === null && w.key('4f3a9c2e') === null);
}

console.log('\n── What is offered ───────────────────────────────────────────');
{
    const w = load({});
    const cases = [
        ['jellyfin-light', null, 'light', 'Light, nothing stored here: light'],
        ['jellyfin-dark', null, null, 'Dark, nothing stored: nothing, jellyfin-web is already dark'],
        ['jellyfin-dark', 'dark', null, 'Dark over dark: nothing'],
        ['jellyfin-dark', 'light', 'dark', 'Dark over light: dark'],
        ['jellyfin-light', 'light', null, 'Light over light: nothing'],
        ['jellyfin-light', 'purplehaze', 'light', 'Light over another jellyfin-web theme: light'],
        ['bonfire', 'light', null, 'Bonfire: nothing, it has no match'],
        ['crimson', null, null, 'Crimson: nothing'],
        ['high-contrast', null, null, 'High contrast: nothing']
    ];
    cases.forEach(([theme, stored, want, name]) => ok(name, w.offer(theme, stored) === want, String(w.offer(theme, stored))));
}

console.log('\n── The offer, taken and declined ─────────────────────────────');
{
    const w = load({});
    w.obj._offerJellyfinTheme(person, 'jellyfin-light');
    ok('choosing Jellyfin Light shows the offer', w.box.hidden === false && /themeSettings\.matchJellyfin/.test(w.box.html));
    ok('naming the theme', /theme\.jellyfin-light/.test(w.box.html));
    ok('and writes nothing until it is answered', Object.keys(w.store).length === 0);
    ok('with a yes and a no', !!w.box.handlers['theme-match-yes'] && !!w.box.handlers['theme-match-no']);
    w.box.handlers['theme-match-yes']();
    ok('yes stores light under the profile\'s own key', w.store[KEY] === 'light', JSON.stringify(w.store));
    ok('and nothing else', Object.keys(w.store).length === 1);
    ok('and says it is done', /themeSettings\.matchDone/.test(w.box.html));
}
{
    const w = load({});
    w.obj._offerJellyfinTheme(person, 'jellyfin-light');
    w.box.handlers['theme-match-no']();
    ok('no stores nothing', Object.keys(w.store).length === 0);
    ok('and puts the offer away', w.box.hidden === true && w.box.html === '');
}
{
    const w = load({ [KEY]: 'light' });
    w.obj._offerJellyfinTheme(person, 'jellyfin-dark');
    ok('Jellyfin Dark over a stored light offers dark', w.box.hidden === false);
    w.box.handlers['theme-match-yes']();
    ok('and yes stores dark', w.store[KEY] === 'dark');
}
[['jellyfin-dark', {}], ['jellyfin-light', { [KEY]: 'light' }], ['crimson', {}], ['bonfire', { [KEY]: 'light' }]].forEach(([theme, store]) => {
    const w = load(Object.assign({}, store));
    w.obj._offerJellyfinTheme(person, theme);
    ok('no offer for ' + theme + ' over ' + (store[KEY] || 'nothing stored'), w.box.hidden === true && w.box.html === '');
});
{
    const w = load({});
    w.obj._offerJellyfinTheme({ profileUserId: 'not-an-id' }, 'jellyfin-light');
    ok('no offer for a profile without a real id', w.box.hidden === true);
}

console.log('\n── When the device refuses ───────────────────────────────────');
{
    const w = load({}, { getThrows: true });
    let threw = false;
    try { w.obj._offerJellyfinTheme(person, 'jellyfin-light'); } catch (e) { threw = true; }
    ok('storage that cannot be read: no offer, nothing thrown', !threw && w.box.hidden === true);
}
{
    const w = load({}, { setThrows: true });
    w.obj._offerJellyfinTheme(person, 'jellyfin-light');
    let threw = false;
    try { w.box.handlers['theme-match-yes'](); } catch (e) { threw = true; }
    ok('storage that refuses the write: an error, not "Done"', !threw && /is-error/.test(w.box.html)
        && /errors\.couldNotSaveThat/.test(w.box.html) && !/matchDone/.test(w.box.html));
}
{
    const w = load({}, { noBox: true });
    let threw = false;
    try { w.obj._offerJellyfinTheme(person, 'jellyfin-light'); } catch (e) { threw = true; }
    ok('a page without the offer\'s place: nothing thrown', !threw);
}

console.log('\n── Wiring ────────────────────────────────────────────────────');
{
    const show = method('showThemeSettings');
    const body = show ? show.body : '';
    ok('Settings → Theme has a place for the offer, hidden', /id="theme-settings-match" hidden/.test(body));
    const redraw = body.indexOf('this.showThemeSettings(person.profileUserId);');
    const offer = body.indexOf('this._offerJellyfinTheme(person, shownNow);');
    ok('the offer is made after a save, into the page drawn for it', redraw !== -1 && offer > redraw);
    ok('and nothing else in the client writes jellyfin-web\'s theme',
        (SRC.match(/'-appTheme'/g) || []).length === 1 && (SRC.match(/jellyfinWebThemeKey\(/g) || []).length === 2);
}

finish();

function finish() {
    console.log('');
    if (fails.length) {
        console.log(pass + ' passed, ' + fails.length + ' failed');
        process.exit(1);
    }
    console.log(pass + ' passed, 0 failed');
    process.exit(0);
}
