// The library-artwork picker dialog (R-1191, R-1195, R-1197).
//
//  - R-1191: closing it without a choice left the row on "Picture"; Save then got
//    "Choose a picture first." The dialog has to tell its caller it was dismissed.
//  - R-1195: it is a dialog, takes Escape, and hands focus back.
//  - R-1197: a failed picture list drew an empty grid with no word said.
//
//   node tests/js/artworkpick.js [path/to/profiles.js]

'use strict';
const L = require('./_lib');
const S = require('./_stubdom');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

async function open(failedList) {
    const env = S.loadPlugin(L.readProfiles());
    const { PP, sandbox } = env;
    const dialogs = [];
    sandbox.document.createElement = () => {
        const d = S.makeContent();
        d.remove = () => { d.removed = true; };
        d.classList = { add() {}, remove() {}, contains: () => false };
        dialogs.push(d);
        return d;
    };
    const opener = { focused: 0, focus() { this.focused++; } };
    sandbox.document.activeElement = opener;
    sandbox.document.getElementById = () => null;
    PP.readMasterState = () => ({ token: 'm' });
    PP.getAuthHeaders = () => ({});
    PP.renderAvatarPicker = () => '';
    PP.initAvatarPicker = () => ({ get: () => ({}) });
    PP.fetchAvatarLibrary = () => Promise.resolve(failedList
        ? { allowCustomUploads: true, avatars: [], failed: true }
        : { allowCustomUploads: true, avatars: [] });
    let cancelled = 0;
    PP.pickLibraryArtwork(() => {}, () => { cancelled++; });
    await env.settle();
    return { dialog: dialogs[0], opener, cancelled: () => cancelled };
}

(async function () {
    let r = await open(false);
    ok('the picker draws a dialog', !!r.dialog);
    ok('it is marked as a dialog', r.dialog && r.dialog.attrs.role === 'dialog', r.dialog && JSON.stringify(r.dialog.attrs));
    r.dialog.querySelector('#profiles-libart-cancel').click();
    ok('Cancel closes it and tells the caller', r.dialog.removed === true && r.cancelled() === 1, 'cancelled ' + r.cancelled());
    ok('and focus goes back to where it was', r.opener.focused === 1, r.opener.focused + ' focus calls');

    r = await open(false);
    r.dialog.fire('keydown', { key: 'Escape' });
    ok('Escape closes it and tells the caller', r.dialog.removed === true && r.cancelled() === 1);

    r = await open(true);
    const err = r.dialog.querySelector('#profiles-libart-error');
    ok('a failed list says so', err && err.style.display === 'block' && /\S/.test(err.textContent), err && err.textContent);

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
