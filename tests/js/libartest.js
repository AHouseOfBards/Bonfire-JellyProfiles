// Exercises the library-artwork substitution (issue #19): which rules get written, what
// is refused before it reaches a stylesheet, and how the per-account cache behaves.

const fs = require('fs');
const vm = require('vm');
const L = require('./_lib');

const src = fs.readFileSync(L.profilesPath(), 'utf8')
    .replace('ProfilesPlugin.init();', 'globalThis.__PP = ProfilesPlugin;');

if (!src.includes('globalThis.__PP')) {
    console.error('Could not find the bootstrap line to replace - harness is out of date.');
    process.exit(1);
}

function makeStorage() {
    const map = new Map();
    return {
        getItem: k => (map.has(k) ? map.get(k) : null),
        setItem: (k, v) => map.set(k, String(v)),
        removeItem: k => map.delete(k),
        clear: () => map.clear()
    };
}

const noopEl = { style: {}, classList: { add() {}, remove() {}, contains: () => false }, appendChild() {}, addEventListener() {} };

// A head that records whatever style element gets appended.
const head = { children: [], appendChild(el) { head.children.push(el); } };
const byId = {};

const sandbox = {
    console,
    localStorage: makeStorage(),
    sessionStorage: makeStorage(),
    setTimeout: () => 0,
    clearTimeout: () => {},
    setInterval: () => 0,
    requestAnimationFrame: () => 0,
    fetch: () => Promise.reject(new Error('no network in harness')),
    MutationObserver: function () { this.observe = () => {}; this.disconnect = () => {}; },
    document: {
        head: head,
        body: noopEl,
        createElement: () => ({
            style: {}, textContent: '', id: '',
            remove() { const i = head.children.indexOf(this); if (i >= 0) head.children.splice(i, 1); delete byId[this.id]; },
            querySelector: () => null, querySelectorAll: () => []
        }),
        querySelector: () => null,
        querySelectorAll: () => [],
        getElementById: id => byId[id] || null,
        addEventListener: () => {},
        documentElement: noopEl,
        activeElement: null
    },
    window: { location: { hash: '', pathname: '/web/', href: 'https://tv.example.org/web/', origin: 'https://tv.example.org' }, addEventListener: () => {}, PointerEvent: function () {} },
    history: { pushState: () => {}, replaceState: () => {} },
    URL: URL,
    Image: function () {}
};
sandbox.globalThis = sandbox;
sandbox.window.localStorage = sandbox.localStorage;

vm.createContext(sandbox);
vm.runInContext(src, sandbox);

const PP = sandbox.__PP;
if (!PP) { console.error('profiles.js did not export ProfilesPlugin'); process.exit(1); }

let pass = 0, fail = 0;
function check(name, actual, expected) {
    const a = JSON.stringify(actual), e = JSON.stringify(expected);
    if (a === e) { pass++; console.log(`  PASS  ${name}`); }
    else { fail++; console.log(`  FAIL  ${name} - expected ${e}, got ${a}`); }
}

// applyLibraryArtwork appends the style element through document.createElement, so mirror
// what getElementById would find afterwards.
const origApply = PP.applyLibraryArtwork.bind(PP);
PP.applyLibraryArtwork = function (entries) {
    const before = head.children.length;
    origApply(entries);
    head.children.forEach(el => { if (el.id) byId[el.id] = el; });
    return before;
};

function styleText() {
    const el = byId[PP.LIBRARY_ART_STYLE_ID];
    return el ? el.textContent : null;
}

const LIB_A = '11111111-1111-1111-1111-111111111111';
const LIB_B = '22222222-2222-2222-2222-222222222222';
const USER = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const OTHER = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const ART = '/plugins/profiles/library-art/' + USER + '/' + LIB_A + '?v=123';
// The selector uses the dashless form, because that is what Jellyfin puts in data-id.
const SEL_A = LIB_A.replace(/-/g, '');
const SEL_B = LIB_B.replace(/-/g, '');

sandbox.ApiClient = {
    getUrl: p => 'https://media.example.org/' + p,
    getCurrentUserId: () => USER,
    accessToken: () => 'token'
};

console.log('\nRule generation');
console.log('---------------');

PP.applyLibraryArtwork([{ libraryId: LIB_A, mode: 'custom', url: ART }]);
let css = styleText();
check('a custom picture produces a rule for that library',
    css.indexOf('.card[data-id="' + SEL_A + '"] .cardImageContainer{') === 0, true);
check('the rule is marked important so it beats the inline style',
    /background-image:url\("[^"]+"\)!important/.test(css), true);
check('the url goes through the server address',
    css.indexOf('https://media.example.org/plugins/profiles/library-art/') >= 0, true);

PP.applyLibraryArtwork([{ libraryId: LIB_A, mode: 'none' }]);
check('hidden clears the background instead of setting one',
    styleText().indexOf('background-image:none!important') >= 0, true);

PP.applyLibraryArtwork([{ libraryId: LIB_A, mode: 'inherit' }]);
check('inherit writes no rule at all', styleText(), null);

PP.applyLibraryArtwork([
    { libraryId: LIB_A, mode: 'custom', url: ART },
    { libraryId: LIB_B, mode: 'none' }
]);
check('several libraries share one style element', head.children.length, 1);
check('both rules are present',
    [styleText().indexOf(SEL_A) >= 0, styleText().indexOf(SEL_B) >= 0], [true, true]);

// PascalCase, as the server actually serialises it.
PP.applyLibraryArtwork([{ LibraryId: LIB_A, Mode: 'custom', Url: ART }]);
check('server-cased fields are accepted', styleText().indexOf(SEL_A) >= 0, true);

console.log('\nLibrary id forms');
console.log('-----------------');

// Jellyfin serialises GUIDs without dashes and puts that form in data-id, so it is the
// form the selector has to use. The first cut required 36 characters and matched nothing.
const DASHLESS = SEL_A;

PP.applyLibraryArtwork([{ libraryId: DASHLESS, mode: 'custom', url: ART }]);
check('a dashless id produces a rule',
    styleText().indexOf('.card[data-id="' + DASHLESS + '"]') === 0, true);

PP.applyLibraryArtwork([{ libraryId: LIB_A, mode: 'custom', url: ART }]);
check('a dashed id is normalised to the dashless selector',
    styleText().indexOf('.card[data-id="' + DASHLESS + '"]') === 0, true);

PP.applyLibraryArtwork([{ libraryId: LIB_A.toUpperCase(), mode: 'custom', url: ART }]);
check('an upper-case id is lowered to match data-id',
    styleText().indexOf(DASHLESS) >= 0, true);

PP.applyLibraryArtwork([{ libraryId: DASHLESS.slice(0, 31), mode: 'custom', url: ART }]);
check('a short id is still refused', styleText(), null);

console.log('\nRefusals');
console.log('--------');

const bad = (entry, label) => {
    PP.applyLibraryArtwork([entry]);
    check(label, styleText(), null);
};

bad({ libraryId: 'not-a-guid', mode: 'custom', url: ART }, 'a non-GUID library id is skipped');
bad({ libraryId: LIB_A + '"] , body {display:none} .x[a="', mode: 'custom', url: ART },
    'a selector-breaking library id is skipped');
bad({ libraryId: LIB_A, mode: 'custom', url: 'https://evil.example/x.png' },
    'an off-site url is refused');
bad({ libraryId: LIB_A, mode: 'custom', url: '/plugins/profiles/library-art/x") ; background:red; z("' },
    'a url that would break out of the rule is refused');
bad({ libraryId: LIB_A, mode: 'custom' }, 'custom with no url writes nothing');
bad({ libraryId: LIB_A, mode: 'sideways', url: ART }, 'an unknown mode writes nothing');

PP.applyLibraryArtwork([]);
check('an empty set removes the style element', head.children.length, 0);

console.log('\nPer-account cache');
console.log('-----------------');

sandbox.localStorage.clear();
PP.cacheLibraryArtwork(USER, [{ libraryId: LIB_A, mode: 'custom', url: ART }]);
PP.applyCachedLibraryArtwork();
check('the cache applies for the account it belongs to', styleText().indexOf(SEL_A) >= 0, true);

// Somebody else signs in on the same device.
PP.applyLibraryArtwork([]);
sandbox.ApiClient.getCurrentUserId = () => OTHER;
PP.applyCachedLibraryArtwork();
check('another account does not inherit the cached rules', styleText(), null);

sandbox.ApiClient.getCurrentUserId = () => USER;
PP.applyCachedLibraryArtwork();
check('the owner still gets them back', styleText().indexOf(SEL_A) >= 0, true);

PP.applyLibraryArtwork([]);
sandbox.localStorage.setItem(PP.config.libraryArtKey, 'not json');
PP.applyCachedLibraryArtwork();
check('an unreadable cache is ignored rather than thrown', styleText(), null);

console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
