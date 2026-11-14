// Review round 3, client fixes: the account email section, the picture crop dialog, the
// profile editor (r2-29-01..06, r2-30-01..04).
//
// Runs the real code against the stub DOM. Red against the build before the fixes:
//   node tests/js/r3i.js /path/to/old/profiles.js
//   node tests/js/r3i.js [path/to/profiles.js]

'use strict';
const L = require('./_lib');
const D = require('./_stubdom');

const SRC = L.readProfiles();

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const env = D.loadPlugin(SRC);
const { PP, content, posts, settle, sandbox } = env;
let respond = () => ({ status: 200, body: {} });
env.setRespond((url, body) => respond(url, body));

PP.beginNavigation = () => 1;
PP.navIsCurrent = () => true;
PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
PP.handleSessionExpired = () => {};
PP.masterPin = '1234';
const alerts = [];
PP.showAlert = (title, body) => alerts.push(body);

const realFetch = sandbox.fetch;
const emailPosts = () => posts.filter(p => p.url === 'plugins/profiles/account/email' && p.body);

(async () => {
    // ── r2-29-01: the browser's own words never reach the screen ─────────────
    console.log('\n── Account email: failures and double taps ─────────────────────');

    respond = (url, body) => (url === 'plugins/profiles/account/email' && !body
        ? { status: 200, body: { available: true, address: '', pendingAddress: '' } }
        : { status: 200, body: { available: true, address: '', pendingAddress: 'a@example.com' } });
    let slot = D.makeContent();
    PP._bindAccountEmail(slot);
    await settle();
    const q = sel => slot.querySelector(sel);

    sandbox.fetch = () => Promise.reject(new TypeError('Failed to fetch'));
    q('#account-email-input').value = 'a@example.com';
    q('#account-email-send').click();
    await settle();
    const said = q('#account-email-status').textContent;
    ok('an unreachable server is not reported as "Failed to fetch"', said !== '' && said.indexOf('Failed to fetch') === -1, said);
    ok('it says what Bonfire says everywhere else', said === PP._failText(new TypeError('x'), ''), said);
    sandbox.fetch = realFetch;

    // ── r2-29-02: one tap, one email ────────────────────────────────────────
    slot = D.makeContent();
    PP._bindAccountEmail(slot);
    await settle();
    posts.length = 0;
    q('#account-email-input').value = 'a@example.com';
    q('#account-email-send').click();
    q('#account-email-send').click();
    await settle();
    ok('a double tap on Send posts once', emailPosts().length === 1, String(emailPosts().length));

    // The guard lets go when the call is over: a refused send can be tried again.
    slot = D.makeContent();
    respond = (url, body) => (!body ? { status: 200, body: { available: true, address: '', pendingAddress: '' } }
        : { status: 400, body: 'No.' });
    PP._bindAccountEmail(slot);
    await settle();
    posts.length = 0;
    q('#account-email-input').value = 'a@example.com';
    q('#account-email-send').click();
    await settle();
    q('#account-email-send').click();
    await settle();
    ok('and after a refusal it tries again', emailPosts().length === 2, String(emailPosts().length));

    // ── r2-29-03, r2-29-04: the crop dialog ─────────────────────────────────
    console.log('\n── Crop dialog ─────────────────────────────────────────────────');

    sandbox.window.removeEventListener = () => {};
    const dialogs = [];
    let taint = false;
    sandbox.document.createElement = (tag) => {
        if (tag === 'canvas') {
            return {
                width: 0, height: 0, style: {},
                getContext: () => ({ drawImage() {}, clearRect() {}, fillRect() {}, getImageData: () => ({ data: [] }) }),
                toDataURL: () => { if (taint) throw new Error('SecurityError'); return 'data:image/png;base64,AA=='; }
            };
        }
        const node = D.makeContent();
        node.remove = () => { node.removed = true; };
        node.classList = { add() {}, remove() {}, contains: () => false, toggle: () => false };
        node.removeEventListener = () => {};
        const find = node.querySelector;
        node.querySelector = (sel) => {
            const n = find(sel);
            if (n) n.removeEventListener = () => {};
            if (n && sel === '#profiles-crop-canvas') {
                n.getContext = () => ({ drawImage() {}, clearRect() {}, fillRect() {} });
            }
            return n;
        };
        dialogs.push(node);
        return node;
    };
    PP._hasTransparency = () => false;
    PP._alphaProfile = () => ({ cutout: false });
    PP.imageSpec = () => ({ masterSize: 100, thumbSize: 20, jpegQuality: 0.8 });
    const img = { width: 300, height: 300 };

    let done = null;
    PP.showCropDialog(img, r => { done = r; });
    const first = dialogs[dialogs.length - 1];
    PP.showCropDialog(img, () => {});
    ok('a second pick closes the first dialog', first.removed === true);
    PP._cropClose();

    alerts.length = 0;
    taint = true;
    PP.showCropDialog(img, r => { done = r; });
    const tainted = dialogs[dialogs.length - 1];
    let threw = null;
    try { tainted.querySelector('#profiles-crop-save').click(); } catch (e) { threw = e; }
    ok('a picture that cannot be read back does not throw', threw === null, String(threw));
    ok('the dialog closes', tainted.removed === true);
    ok('and says so, through t()', alerts.length === 1 && /can't be used/.test(alerts[0]), JSON.stringify(alerts));
    ok('nothing is handed on', done === null);
    taint = false;

    PP.showCropDialog(img, r => { done = r; });
    dialogs[dialogs.length - 1].querySelector('#profiles-crop-save').click();
    ok('an ordinary crop still hands its pictures on', done && /^data:/.test(done.image) && /^data:/.test(done.thumb), JSON.stringify(done));

    // ── Profile editor ──────────────────────────────────────────────────────
    console.log('\n── Profile editor ──────────────────────────────────────────────');

    // r2-30-02: the colour-dot listener is added once per element, however often a form opens.
    let added = 0;
    const el = D.makeContent();
    const add = el.addEventListener;
    el.addEventListener = (...a) => { added++; return add(...a); };
    ok('there is one place that binds the colour-dot keys', typeof PP._bindColorDotKeys === 'function');
    if (typeof PP._bindColorDotKeys === 'function') {
        PP._bindColorDotKeys(el);
        PP._bindColorDotKeys(el);
        ok('visiting it twice adds one listener', added === 1, String(added));
    }
    ok('Create and Edit no longer add their own', (SRC.match(/content\.addEventListener\('keydown'/g) || []).length === 1, 'only the helper binds it');

    // r2-30-03: a library id is escaped where it is written into an attribute.
    ok('a library id is escaped in value= and data-lib=', !/(value|data-lib)="\$\{lib\.id\}"/.test(SRC));

    // r2-30-01: a failed artwork save is its own message and the hours and refresh still run.
    const from = SRC.indexOf('return artworkEditor.save(profile.profileUserId)');
    const seg = from < 0 ? '' : SRC.slice(from, from + 900);
    ok('the artwork save has its own catch before the hours step', /artworkEditor\.save\(profile\.profileUserId\)[\s\S]{0,400}?\.catch\([\s\S]*?viewingEditor\.save\(\)/.test(seg));

    // r2-30-04: a refused auto-lock goes back to what was shown, never to "undefined".
    PP.showSettingsMenu = () => {};
    PP.fetchSharedFormData = () => Promise.resolve({ devices: [] });
    PP._sharedForm = null;
    PP.currentProfiles = [
        { profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false, hasPin: true,
          askPinToOpen: true, bypassPinOnLocalNetwork: false, seenOnLocalNetwork: true, isAdministrator: false }
    ];
    respond = () => ({ status: 400, body: 'No.' });
    PP.showAccountSettings();
    await settle();
    const lock = content.querySelector('#account-lockout-select');
    const shown = lock.value;
    lock.value = '30';
    lock.fire('change');
    await settle();
    ok('a refused auto-lock puts the select back to what it showed', lock.value === shown && lock.value !== 'undefined', 'was ' + shown + ', now ' + lock.value);

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
