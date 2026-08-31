// Issues #22, #23 and #24, against the real profiles.js.
//
// Run against the shipped 1.5.0 file first: every assertion here must FAIL there, or it
// is not testing the fix. Same technique as busy.js and session.js — evaluate the file
// with init() swapped for an export, so these are the methods that ship.
//
//   node issues151.js                 # the working tree
//   node issues151.js path/to/old.js  # a checkout of 1.5.0, expected to fail
const fs = require('fs');
const vm = require('vm');
const path = require('path');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
}

const SRC_PATH = L.profilesPath();
const REPO = process.argv[3] || '.';
// The bundle, not the bare file: this harness both EVALUATES the script and searches
// it for CSS rules, and the stylesheet is Web/styles.css now.
let src = L.readClientBundle(fs.readFileSync(SRC_PATH, 'utf8'));

const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) {
    console.error('could not find the single init() call to swap for an export');
    process.exit(1);
}
const runnable = src.replace(INIT,
    'globalThis.__PROFILES = ProfilesPlugin;' +
    'globalThis.__I18N = { t: t, en: EN_STRINGS };');

// ── Sandbox ─────────────────────────────────────────────────────────────────
// A fake image carries its own alpha bytes; drawImage records which one was drawn and
// getImageData hands those same bytes back. That makes _alphaProfile testable on
// synthetic shapes without a real canvas.
function makeClassList() {
    const set = new Set();
    return {
        add: c => set.add(c),
        remove: c => set.delete(c),
        contains: c => set.has(c),
        toggle: (c, force) => {
            const on = force === undefined ? !set.has(c) : !!force;
            if (on) set.add(c); else set.delete(c);
            return on;
        }
    };
}

function makeCanvas() {
    const ctx = {
        __src: null,
        drawImage(src) { ctx.__src = src; },
        getImageData() {
            if (!ctx.__src || !ctx.__src.__pixels) throw new Error('tainted');
            return { data: ctx.__src.__pixels };
        }
    };
    return {
        width: 0, height: 0,
        getContext: () => ctx,
        toDataURL: () => 'data:image/png;base64,AAAA'
    };
}

function build() {
    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        sessionStorage: { getItem: () => null, setItem() {}, removeItem() {} },
        navigator: { userAgent: 'Mozilla/5.0' },
        setTimeout() {}, clearTimeout() {}, setInterval() {}, clearInterval() {},
        fetch() { return Promise.resolve({ ok: false, text: () => Promise.resolve('') }); },
        location: { hash: '', pathname: '/web/', search: '', reload() {}, replace() {} },
        document: {
            addEventListener() {}, removeEventListener() {},
            querySelector() { return null; }, querySelectorAll() { return []; },
            getElementById: () => null,
            createElement: tag => (tag === 'canvas' ? makeCanvas() : { style: {}, classList: makeClassList() }),
            head: { appendChild() {} },
            body: { classList: makeClassList() },
            documentElement: { style: { cssText: '' }, classList: makeClassList() }
        },
        JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set,
        Uint8ClampedArray
    };
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;
    vm.createContext(sandbox);
    new vm.Script(runnable, { filename: 'profiles.js' }).runInContext(sandbox);
    return { plugin: sandbox.__PROFILES, i18n: sandbox.__I18N, sandbox };
}

const env = build();
const P = env.plugin;

// ── #22: switching lands on the home screen ─────────────────────────────────
console.log();
console.log('── #22  Switching profiles goes home ──────────────────────────');

ok('the home-route test is shared, not inlined in checkRoute',
    typeof P.isHomeRoute === 'function');
ok('there is a way to name the home URL', typeof P.homeUrl === 'function');
ok('and a single place that reloads onto it', typeof P.reloadAtHome === 'function');

function at(loc) {
    const calls = [];
    env.sandbox.location = Object.assign({
        hash: '', pathname: '/web/', search: '',
        reload() { calls.push(['reload']); },
        replace(u) { calls.push(['replace', u]); }
    }, loc);
    return calls;
}

// The reason #22 could not simply reuse the old test: on a path-routed 10.11 build the
// hash is empty on every page, and `hash === ''` was the first clause — so checkRoute
// believed every page was home there.
if (typeof P.isHomeRoute === 'function') {
    at({ pathname: '/web/mypreferencesmenu' });
    ok('a path route that is not home is not home', P.isHomeRoute() === false);

    at({ pathname: '/web/home' });
    ok('/web/home is home', P.isHomeRoute() === true);

    at({ pathname: '/web/' });
    ok('/web/ is still home', P.isHomeRoute() === true);

    at({ pathname: '/web/index.html' });
    ok('/web/index.html is still home', P.isHomeRoute() === true);

    at({ pathname: '/web/index.html', hash: '#/home.html' });
    ok('hash-routed home is unchanged', P.isHomeRoute() === true);

    at({ pathname: '/web/index.html', hash: '#/movies.html' });
    ok('hash-routed elsewhere is unchanged', P.isHomeRoute() === false);

    at({ pathname: '/web/dashboard', hash: '' });
    ok('the dashboard is not home', P.isHomeRoute() === false);

    at({ pathname: '/jellyfin/web/home' });
    ok('a base path does not break it', P.isHomeRoute() === true);

    at({ pathname: '/web/login.html' });
    ok('login is still excluded', P.isHomeRoute() === false);
}

if (typeof P.homeUrl === 'function') {
    at({ pathname: '/web/mypreferencesmenu' });
    ok('path-routed 10.11: /web/mypreferencesmenu -> /web/home',
        P.homeUrl() === '/web/home');

    at({ pathname: '/jellyfin/web/mypreferencesmenu' });
    ok('a server on a base path keeps it',
        P.homeUrl() === '/jellyfin/web/home');

    at({ pathname: '/web/index.html', hash: '#/mypreferencesmenu.html' });
    ok('hash-routed keeps the document and moves the fragment',
        P.homeUrl() === '/web/index.html#/home.html');

    at({ pathname: '/web/index.html', hash: '#!/mypreferencesmenu.html' });
    ok('an older #!/ build keeps its own prefix',
        P.homeUrl() === '/web/index.html#!/home.html');

    at({ pathname: '/something/else' });
    ok('an unrecognised shape refuses to guess', P.homeUrl() === null);
}

if (typeof P.reloadAtHome === 'function') {
    let calls = at({ pathname: '/web/mypreferencesmenu' });
    P.reloadAtHome();
    ok('from the settings page it navigates rather than reloading in place',
        calls.length === 1 && calls[0][0] === 'replace' && calls[0][1] === '/web/home');

    calls = at({ pathname: '/web/home' });
    P.reloadAtHome();
    ok('already home, it reloads exactly as before',
        calls.length === 1 && calls[0][0] === 'reload');

    calls = at({ pathname: '/web/', hash: '' });
    P.reloadAtHome();
    ok('a hash-routed build at /web/ is treated as home, not sent to /web/home',
        calls.length === 1 && calls[0][0] === 'reload');

    calls = at({ pathname: '/web/index.html', hash: '#/mypreferencesmenu.html' });
    P.reloadAtHome();
    ok('a fragment-only move is followed by a reload, or the app never re-initialises',
        calls.length === 2 && calls[0][0] === 'replace' && calls[1][0] === 'reload');

    calls = at({ pathname: '/something/else' });
    P.reloadAtHome();
    ok('an unrecognised shape falls back to reloading in place',
        calls.length === 1 && calls[0][0] === 'reload');
}

// Both reload sites must actually call it. A helper nothing uses is the shape the
// 1.4.6 session fix took, which sat inert for three releases.
function countOutsideHelper(needle) {
    return src.split(needle).length - 1;
}
ok('the switch calls reloadAtHome',
    /localStorage\.setItem\(this\.config\.switchingKey, '1'\);\s*\r?\n\s*this\.reloadAtHome\(\);/.test(src));
ok('so does the revert-to-master path',
    countOutsideHelper('this.reloadAtHome();') >= 2);

// ── #24: the badge no longer reads as "someone is watching" ─────────────────
console.log();
console.log('── #24  The current-profile badge ─────────────────────────────');

ok('"Watching now" is gone from the catalogue', env.i18n.en['gate.watchingNow'] === undefined);
ok('the badge resolves to Signed in',
    env.i18n.t('gate.signedIn') === 'Signed in');
ok('and the gate renders it through the catalogue, not a literal',
    /profile-current-badge">\$\{t\('gate\.signedIn'\)\}/.test(src));

// ── #23: cut-out pictures ───────────────────────────────────────────────────
console.log();
console.log('── #23  Detecting a cut-out picture ───────────────────────────');

const S = 32;
function pixels(alphaAt) {
    const d = new Uint8ClampedArray(S * S * 4);
    for (let y = 0; y < S; y++) {
        for (let x = 0; x < S; x++) {
            const i = (y * S + x) * 4;
            d[i] = 120; d[i + 1] = 120; d[i + 2] = 120;
            d[i + 3] = alphaAt(x, y);
        }
    }
    return d;
}
const img = alphaAt => ({ __pixels: pixels(alphaAt) });

const OPAQUE = img(() => 255);
const CIRCLE = img((x, y) => {
    const dx = x - (S - 1) / 2, dy = y - (S - 1) / 2;
    return Math.sqrt(dx * dx + dy * dy) <= S / 2 - 1 ? 255 : 0;
});
// The case that must not trip it: a rectangular photo with one soft antialiased edge.
const SOFT_EDGE = img((x, y) => (x === 0 || y === 0 || x === S - 1 || y === S - 1) ? 200 : 255);
// A rectangle with gently rounded corners is still a rectangle.
const ROUNDED = img((x, y) => {
    const nearX = x < 2 || x > S - 3, nearY = y < 2 || y > S - 3;
    return (nearX && nearY) ? 0 : 255;
});
const THREE_CORNERS = img((x, y) => {
    const inTL = x < 6 && y < 6;
    const inTR = x > S - 7 && y < 6;
    const inBL = x < 6 && y > S - 7;
    return (inTL || inTR || inBL) ? 0 : 255;
});
const TWO_CORNERS = img((x, y) => {
    const inTL = x < 6 && y < 6;
    const inTR = x > S - 7 && y < 6;
    return (inTL || inTR) ? 0 : 255;
});

if (typeof P._alphaProfile !== 'function') {
    ok('there is a single reader for both alpha questions', false);
} else {
    ok('there is a single reader for both alpha questions', true);

    ok('an opaque photo has no alpha', P._alphaProfile(OPAQUE).hasAlpha === false);
    ok('and is not a cut-out', P._alphaProfile(OPAQUE).cutout === false);

    ok('a circular avatar has alpha', P._alphaProfile(CIRCLE).hasAlpha === true);
    ok('and IS a cut-out', P._alphaProfile(CIRCLE).cutout === true);

    ok('a soft antialiased edge still needs PNG', P._alphaProfile(SOFT_EDGE).hasAlpha === true);
    ok('but must NOT switch the background off',
        P._alphaProfile(SOFT_EDGE).cutout === false);

    ok('gently rounded corners are not a cut-out',
        P._alphaProfile(ROUNDED).cutout === false);

    ok('three clear corners count', P._alphaProfile(THREE_CORNERS).cutout === true);
    ok('two do not', P._alphaProfile(TWO_CORNERS).cutout === false);

    ok('a tainted canvas is treated as an opaque photo',
        P._alphaProfile({}).cutout === false && P._alphaProfile({}).hasAlpha === false);

    ok('the format check still answers the old question',
        P._hasTransparency(CIRCLE) === true && P._hasTransparency(OPAQUE) === false);
}

console.log();
console.log('── #23  Wiring ────────────────────────────────────────────────');

ok('the crop dialog reports what it detected',
    src.indexOf('onDone({ image: image, thumb: thumb, cutout: cutout });') !== -1);
ok('detection reads the cropped result, not the source',
    /const cutout = this\._alphaProfile\(thumbCanvas\)\.cutout;/.test(src));
ok('the picker exposes the choice', /transparent: state\.transparent/.test(src));
ok('create sends it',
    src.split('transparentAvatar: avatarPicker.get().transparent').length - 1 === 2);
ok('the DTO field is read back',
    src.indexOf("pick(p, 'transparentAvatar', false)") !== -1);
ok('the library-avatar path detects too',
    /state\.transparent = this\._alphaProfile\(img\)\.cutout;/.test(src));
ok('a late detection cannot overwrite a newer pick',
    src.indexOf('if (state.image !== pickedUrl) return;') !== -1);
ok('clearing the picture clears the choice',
    /state\.transparent = false;\s*\r?\n\s*setPreview\(null\);/.test(src));
ok('the libart dialog does not get the row',
    src.indexOf('renderTransparentToggle') !== -1
    && src.indexOf('host.innerHTML = this.renderAvatarPicker("libart"') !== -1);

// Follow-up on #23: the toggle governs the avatar COLOUR, so it belongs beside the
// swatches, not below the picture panel with the whole picture row in between.
console.log();
console.log('── #23  Follow-up: placement, wording, rings ──────────────────');

ok('the toggle is rendered by the form, not the picture panel',
    typeof P.renderTransparentToggle === 'function');
ok('it is worded as No background',
    env.i18n.t('profileForm.noBackground') === 'No background');
ok('"Transparent background" is gone from the form',
    src.indexOf('<span>Transparent background</span>') === -1);
ok('its hint is translatable too',
    typeof env.i18n.en['profileForm.noBackgroundHint'] === 'string');

function orderIn(haystack, a, b) {
    const ia = haystack.indexOf(a), ib = haystack.indexOf(b);
    return ia !== -1 && ib !== -1 && ia < ib;
}
const createForm = src.slice(src.indexOf('const createAppearance = `'), src.indexOf('// ── Section 2: getting into this profile'));
ok('create: name, then the toggle, then the colour swatches',
    orderIn(createForm, 'create-name-input', "renderTransparentToggle('create'")
    && orderIn(createForm, "renderTransparentToggle('create'", 'create-color-group'));
const editForm = src.slice(src.indexOf('const appearanceBody = `'), src.indexOf('const securityBody'));
ok('edit: same order',
    orderIn(editForm, 'edit-name-input', "renderTransparentToggle('edit'")
    && orderIn(editForm, "renderTransparentToggle('edit'", 'edit-color-group'));
ok('the picture panel no longer carries the row',
    src.slice(src.indexOf('renderAvatarPicker: function'), src.indexOf('initAvatarPicker: function'))
       .indexOf('transparent-row') === -1);

// The header avatar and the gate badge read who-is-active from two different places.
// Distance-based anchors have bitten before, so bound this to the enclosing branch
// rather than a character count: the clear must sit between the revert and its reload.
(function () {
    const i = src.indexOf('apiClient.setAuthenticationInfo(masterState.masterToken, masterState.masterUserId);');
    const j = src.indexOf('this.reloadAtHome();', i);
    const branch = (i === -1 || j === -1) ? '' : src.slice(i, j);
    ok('reverting to the master clears the cached active profile, before it reloads',
        branch.indexOf('this.clearProfileSession();') !== -1);
})();

console.log();
console.log('── #23  Rendering ─────────────────────────────────────────────');

ok('the gate card can go transparent',
    /p\.profileImage && p\.transparentAvatar \? ' is-transparent' : ''/.test(src));
ok('and stops clipping so the outline can show',
    /p\.profileImage && p\.transparentAvatar \? 'visible' : 'hidden'/.test(src));

// The CSS lives in one big template string. Resolve the cascade the way the browser
// would rather than merely asserting a rule is present — uitest.js was green for
// three releases on rules that were being overridden.
function ruleBody(selector) {
    // Anchored on a line start plus any indentation, rather than on exactly sixteen
    // spaces. The stylesheet used to live inside a template literal, so every rule
    // carried the indentation of the JavaScript around it; it is Web/styles.css now
    // and carries none. A locator that encodes formatting stops working the moment
    // the formatting moves, and it fails by finding nothing — which reads as "the
    // rule is missing" rather than "the locator is wrong".
    //
    // It also reads the CSS itself rather than `src`. In the bundle the stylesheet is
    // a JSON-encoded string, so its newlines are the two characters backslash-n and a
    // search for a real line break finds nothing. A CSS lookup belongs in the CSS.
    const css = L.extractCss(src);
    const needle = '\n' + selector + ' {';
    let i = css.indexOf(needle);
    if (i === -1) {
        // The indented form, for an older checkout where the CSS is still inline.
        i = css.indexOf('\n                ' + selector + ' {');
    }
    if (i === -1) return null;
    const start = css.indexOf('{', i);
    const end = css.indexOf('}', start);
    return css.slice(start + 1, end);
}

const transparentRule = ruleBody('.profile-avatar.is-transparent');
ok('.profile-avatar.is-transparent exists', transparentRule !== null);
if (transparentRule) {
    ok('it paints no colour', /background-color:\s*transparent/.test(transparentRule));
    ok('it draws no square ring', /border-color:\s*transparent/.test(transparentRule));
    ok('it drops the box shadow', /box-shadow:\s*none/.test(transparentRule));
    ok('it lets the outline spread', /overflow:\s*visible/.test(transparentRule));
}

// The hover block sets box-shadow and border-color on .profile-avatar and sits later
// in the sheet, so the transparent variant must be more specific or it loses.
const hoverIdx = src.indexOf('.profile-card:hover .profile-avatar,');
const overrideIdx = src.indexOf('.profile-card:hover .profile-avatar.is-transparent,');
ok('the transparent variant outranks the hover block',
    overrideIdx !== -1 && hoverIdx !== -1 && overrideIdx < hoverIdx);

ok('the current-profile ring follows the silhouette',
    /\.profile-card\.is-current \.profile-avatar\.is-transparent img/.test(src)
    && /drop-shadow\(3px 0 0 var\(--jpf-accent\)\)/.test(src));

// The bug the first pass missed: `.profile-card.is-current .profile-avatar` is three
// classes and `.profile-avatar.is-transparent` is two, so the accent border won and drew
// a rounded square around a circular picture while the silhouette drew a second ring
// inside it. Checking the hover override alone did not catch it, so resolve the cascade
// for every state that touches border-color or box-shadow on .profile-avatar.
// A guard has to answer the SAME state. An earlier version of this check only compared
// specificity totals, so the high-specificity hover guard counted as cover for the
// is-current rule and the check passed against the build that actually had the bug —
// the exact false green this file is supposed to make impossible.
function selectorsTouching(prop) {
    const out = [];
    const re = /\n {16}([^\n{]+)\{([^}]*)\}/g;
    let m;
    while ((m = re.exec(src)) !== null) {
        const selList = m[1].trim();
        if (!/\.profile-avatar/.test(selList)) continue;
        if (!new RegExp('(^|;|\\s)' + prop + '\\s*:').test(m[2])) continue;
        selList.split(',').forEach(s => {
            const sel = s.trim().replace(/\s+/g, ' ');
            if (sel && /\.profile-avatar/.test(sel)) out.push(sel);
        });
    }
    return out;
}
['border-color', 'box-shadow'].forEach(prop => {
    const sels = selectorsTouching(prop);
    const guards = new Set(sels.filter(s => s.includes('.is-transparent')));
    // The bare base rule is answered by the bare `.profile-avatar.is-transparent` rule,
    // which is one class more specific and later in the sheet.
    const plain = sels.filter(s => !s.includes('.is-transparent') && s !== '.profile-avatar');

    const unguarded = plain.filter(s => {
        // The state this rule targets, restated for a cut-out avatar.
        const wanted = s.replace(/\.profile-avatar(?![\w-])/, '.profile-avatar.is-transparent');
        return !guards.has(wanted);
    });

    ok('every ' + prop + ' state on .profile-avatar is answered for cut-outs '
        + '(' + plain.length + ' states, ' + guards.size + ' guards)',
        unguarded.length === 0);
    if (unguarded.length) unguarded.forEach(u => console.log('        unguarded: ' + u));
});

ok('the current-profile square ring is off for cut-outs',
    /\.profile-card\.is-current \.profile-avatar\.is-transparent \{/.test(src));
ok('manage mode does not drop a square scrim on a cut-out',
    /\.profile-avatar\.is-transparent \.profile-avatar-overlay-wrap \{[^}]*background:\s*transparent/.test(src));

// Every custom property used must be declared somewhere, or the whole declaration is
// invalid at computed-value time and silently does nothing.
const used = new Set((src.match(/var\((--jpf-[a-z0-9-]+)\)/g) || [])
    .map(m => m.replace(/^var\(|\)$/g, '')));
const declared = new Set((src.match(/(--jpf-[a-z0-9-]+)\s*:/g) || [])
    .map(m => m.replace(/\s*:$/, '')));
const undeclared = [...used].filter(v => !declared.has(v));
ok('every --jpf- custom property used is declared (' + used.size + ' used)',
    undeclared.length === 0);
if (undeclared.length) console.log('        undeclared: ' + undeclared.join(', '));

// Same for keyframes: animation: spin named nothing for a whole release.
const animNames = new Set((src.match(/animation:\s*([a-zA-Z][\w-]*)/g) || [])
    .map(m => m.replace(/animation:\s*/, '')));
const keyframes = new Set((src.match(/@keyframes\s+([a-zA-Z][\w-]*)/g) || [])
    .map(m => m.replace(/@keyframes\s+/, '')));
const missingKf = [...animNames].filter(n => !keyframes.has(n));
ok('every animation names a keyframe that exists', missingKf.length === 0);
if (missingKf.length) console.log('        missing: ' + missingKf.join(', '));

console.log();
console.log('── #23  The hint that caused the issue ────────────────────────');

ok('the colour picker no longer claims to be unused behind a picture',
    src.indexOf('Not used while a picture is set.') === -1);
ok('it says where the colour actually shows',
    src.indexOf('Shows behind the picture, wherever it is transparent.') !== -1);
ok('and dims only when the colour is genuinely dead',
    /const unused = !!hasPicture && !!isTransparent;/.test(src));
ok('the picker owns that state, so the two forms cannot race it',
    src.indexOf('const syncColorGroup = () => {') !== -1);

// ── Server side ─────────────────────────────────────────────────────────────
console.log();
console.log('── #23  Server side ───────────────────────────────────────────');

function readRepo(rel) {
    try { return fs.readFileSync(path.join(REPO, rel), 'utf8'); } catch (e) { return ''; }
}
const cfg = readRepo('Configuration/PluginConfiguration.cs');
const ctrl = readRepo('Controllers/ProfilesController.cs');
const createReq = readRepo('Models/CreateProfileRequest.cs');
const updateReq = readRepo('Models/UpdateProfileRequest.cs');

ok('the mapping stores it', /public bool TransparentAvatar \{ get; set; \}/.test(cfg));
ok('create accepts it', /public bool\? TransparentAvatar \{ get; set; \}/.test(createReq));
ok('update accepts it', /public bool\? TransparentAvatar \{ get; set; \}/.test(updateReq));
ok('create writes it', /TransparentAvatar = request\.TransparentAvatar \?\? false,/.test(ctrl));
ok('update leaves it alone when absent, rather than clearing it',
    /if \(request\.TransparentAvatar\.HasValue\)/.test(ctrl));
ok('the master DTO carries it',
    /TransparentAvatar = linkedMapping\?\.TransparentAvatar \?\? false,/.test(ctrl));
ok('the sub-profile DTO carries it', /m\.TransparentAvatar,/.test(ctrl));

// ── Television / D-pad ──────────────────────────────────────────────────────
// Everything new has to be reachable and legible from a remote at three metres.
console.log();
console.log('── Remote navigation and 10-foot legibility ───────────────────');

// initTVCheckboxes binds tabindex and Enter/Space to .library-check-label. A new
// checkbox that does not carry that class is invisible to a remote: no focus, no way
// to tick it, and nothing on screen saying so.
const toggleBlock = src.slice(src.indexOf('renderTransparentToggle: function'),
                              src.indexOf('renderAvatarPicker: function'));
ok('the No background toggle is a .library-check-label, so a remote can focus it',
    /class="library-check-label picture-transparent-toggle"/.test(toggleBlock));
ok('and Enter/Space reaches it through initTVCheckboxes',
    /container\.querySelectorAll\('\.library-check-label'\)/.test(src));
ok('that class has a visible focus state',
    /\.library-check-label:focus[^{]*\{[^}]*background:/.test(src));

// Hidden rows must not be focus stops. display:none is what keeps it out of the
// tab order while there is no picture to describe.
ok('the toggle is hidden rather than disabled when there is no picture',
    /id="\$\{prefix\}-transparent-row"[\s\S]{0,200}display: \$\{currentImage \? 'block' : 'none'\}/.test(src));

// Focus is the D-pad cursor. For a cut-out avatar the border and box-shadow are both
// off, so the replacement has to carry the whole job.
function filterOf(selector) {
    const i = src.indexOf(selector);
    if (i === -1) return null;
    const open = src.indexOf('{', i);
    const close = src.indexOf('}', open);
    const body = src.slice(open + 1, close);
    const m = body.match(/filter:\s*([^;]+);/);
    return m ? m[1] : null;
}
const focusFilter = filterOf('.profile-card:focus-within .profile-avatar.is-transparent img');
ok('the cut-out focus indicator exists', focusFilter !== null);
if (focusFilter) {
    const widths = (focusFilter.match(/drop-shadow\((-?\d+)px \d+px 0|drop-shadow\(\d+ (-?\d+)px 0/g) || []);
    ok('its outline is at least as thick as the 3px border it replaces',
        /drop-shadow\(3px 0 0/.test(focusFilter) && /drop-shadow\(0 3px 0/.test(focusFilter));
    ok('it covers all four directions, not just two',
        /drop-shadow\(3px 0 0/.test(focusFilter) && /drop-shadow\(-3px 0 0/.test(focusFilter)
        && /drop-shadow\(0 3px 0/.test(focusFilter) && /drop-shadow\(0 -3px 0/.test(focusFilter));
    ok('and it keeps a wide accent glow, since box-shadow is off for cut-outs',
        /drop-shadow\(0 \d+px \d\dpx var\(--jpf-accent\)\)/.test(focusFilter));
}
const currentFilter = filterOf('.profile-card.is-current .profile-avatar.is-transparent img');
ok('the current-profile ring is 3px too, matching the border elsewhere',
    currentFilter !== null && /drop-shadow\(3px 0 0 var\(--jpf-accent\)\)/.test(currentFilter));

// A translated label must wrap inside its card, not widen it. The card floor is 140px
// and "PIN Protected" becomes "Protégé par code PIN" in French — half again as long.
const nameRule = filterOf === null ? '' : (() => {
    const i = src.indexOf('.profile-name {');
    return i === -1 ? '' : src.slice(src.indexOf('{', i) + 1, src.indexOf('}', i));
})();
ok('the name block cannot grow wider than its card', /max-width:\s*100%/.test(nameRule));
ok('and a long word breaks rather than overflowing',
    /overflow-wrap:\s*break-word/.test(nameRule));

const badgeRule = (() => {
    const i = src.indexOf('.profile-pin-badge {');
    return i === -1 ? '' : src.slice(src.indexOf('{', i) + 1, src.indexOf('}', i));
})();
ok('the PIN badge is bounded by the card too', /max-width:\s*100%/.test(badgeRule));

// Manage mode: taking the scrim away also took away what the white pencil was legible
// against. On a pale cut-out it would otherwise vanish.
// Resolved, not spelled: P6 moved that literal onto --jpf-scrim, and matching the
// spelling would fail for a token holding exactly the right colour while passing for
// one redefined to transparent. What has to be true is that the backing is dark and
// substantially opaque, whatever it is written as.
ok('the manage-mode pencil keeps a backing it can be seen against', (() => {
    const m = /\.profile-avatar\.is-transparent \.profile-avatar-overlay-svg \{([^}]*)\}/.exec(src);
    if (!m) return false;
    const bg = /background:\s*([^;]+);/.exec(m[1]);
    if (!bg) return false;
    const value = L.resolveCssValue(src, bg[1].trim());
    return /rgba?\(\s*(?:0|1?\d|2[0-5])\s*,\s*(?:0|1?\d|2[0-5])\s*,/.test(value)
        && L.alphaOf(value) >= 0.5;
})());
ok('and that backing is round, not the square that was removed',
    /\.profile-avatar\.is-transparent \.profile-avatar-overlay-svg \{[^}]*border-radius:\s*50%/.test(src));

// ── #25: updated on a running server ────────────────────────────────────────
console.log();
console.log('── #25  Plugin updated, restart pending ───────────────────────');

const boot = readRepo('ProfilesBootstrapTask.cs');
const dash = readRepo('Web/profilesDashboard.html');

ok('there is a signal for "loaded after Jellyfin started"',
    /internal static bool RestartRequired => !ProfilesIndexMiddleware\.IsRegistered;/.test(boot));
ok('status refresh reports it instead of leaving the flags at their defaults',
    /if \(RestartRequired\)[\s\S]{0,80}InjectionSucceeded = false;/.test(boot));
ok('the reason names a restart, not a permission fix',
    boot.indexOf('Restart Jellyfin to') !== -1
    && boot.indexOf("Jellyfin's own Restart") !== -1);
ok('it warns that the admin Restart button is not enough on Docker',
    /restart the container/i.test(boot));
ok('the mechanism payload carries it',
    /RestartRequired = ProfilesBootstrapTask\.RestartRequired,/.test(ctrl));

// TryUnpatchIndex still trusted IsRegistered, the signal 1.4.8 removed everywhere else.
ok('unpatching no longer reports success merely because the plugin loaded',
    boot.indexOf('InjectionSucceeded = ProfilesIndexMiddleware.IsRegistered;') === -1);

ok('the stale comment claiming a Both fallback is gone',
    !/Falls back to[\s\S]{0,40}IndexInjectionModes\.Both/.test(boot));

ok('the dashboard has a restart banner', dash.indexOf('id="restartRequiredContainer"') !== -1);
ok('it suppresses the failure banner rather than stacking with it',
    /const failed = !restartRequired && \(succeeded === false\);/.test(dash));
ok('and the green "all good" banner too',
    /okContainer\.style\.display = \(!restartRequired && !failed && !stale\)/.test(dash));
ok('the chown block is hidden when the mode does not touch the file',
    /const hideFileFix = failed && !patchesFile;/.test(dash));
ok('the failure headline changes with the mode',
    dash.indexOf('id="injectionFailureHeadline"') !== -1
    && dash.indexOf('index.html is not involved') !== -1);
ok('the account row cannot be re-shown after being hidden',
    dash.indexOf('#injectionFailureAccountRow') !== -1
    && dash.indexOf('if (failAccountRow && hideFileFix)') !== -1);

// The gating must run after the block that fills in the commands, or that block's own
// show/hide wins and the chown row comes back.
const gateIdx = dash.indexOf('const hideFileFix');
const fillIdx = dash.indexOf("page.querySelectorAll('.detectedAccountRow')");
ok('gating runs after the command-filling block, not before',
    gateIdx !== -1 && fillIdx !== -1 && gateIdx > fillIdx);

// ── Result ──────────────────────────────────────────────────────────────────
console.log();
if (fails.length) {
    console.log('FAILED  ' + fails.length + ' of ' + (pass + fails.length));
    fails.forEach(f => console.log('   - ' + f));
    process.exit(1);
}
console.log('All ' + pass + ' passed.');
