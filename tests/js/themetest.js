// Guards the theme integration in Web/profiles.js.
//
// Two things are easy to get wrong here and neither shows up as an error:
//   - a new hardcoded #00a4dc creeping back in, so one control stays Jellyfin
//     blue on a themed server while everything around it follows the theme;
//   - the avatar swatch palette being caught by a global colour replace. Those
//     are DATA — a colour the user picked, persisted in configuration and
//     compared with toLowerCase() — and a var() reference written into config
//     would render as nothing at all.

const fs = require('fs');
const L = require('./_lib');
// Source plus stylesheet as plain text — this harness searches it for CSS as well as
// JS. Not the served bundle, where the CSS is JSON-encoded and unsearchable.
const SRC = L.readSourceAndStyles(fs.readFileSync(L.profilesPath(), 'utf8'));

let pass = 0, fail = 0;
function check(name, actual, expected) {
    const ok = Object.is(actual, expected);
    if (ok) { pass++; console.log('  PASS  ' + name); }
    else {
        fail++;
        console.log('  FAIL  ' + name + '\n          expected: ' + expected + '\n          actual:   ' + actual);
    }
}

// The single <style> block, pulled out by locating its template literal.
const CSS = L.extractCss(SRC);

// The definitions live at the top; everything after them is consuming CSS.
const defsEnd = CSS.indexOf('@supports');
const supportsEnd = CSS.indexOf('}', CSS.indexOf('}', CSS.indexOf('--jpf-accent-a60: color-mix')) + 1) + 1;
const DEFS = CSS.slice(0, supportsEnd);
const CONSUMERS = CSS.slice(supportsEnd);

const ALPHAS = ['a08', 'a18', 'a30', 'a40', 'a45', 'a50', 'a60'];

console.log('── The accent is read from the theme ─────────────────────────');
check('--jpf-accent is defined', /--jpf-accent:\s*var\(--accent,\s*#00a4dc\)/.test(DEFS), true);
check('declared on :root AND body', /:root,\s*body\s*\{/.test(DEFS), true);
// Reading --accent only at :root would miss every theme that sets it on body,
// because a custom property resolves against the element declaring it.
check('two declaring blocks (floor + @supports)',
    (DEFS.match(/:root,\s*body\s*\{/g) || []).length, 2);
check('falls back to Jellyfin stock blue', DEFS.includes('var(--accent, #00a4dc)'), true);

console.log();
console.log('── Tints degrade instead of disappearing ─────────────────────');
check('@supports guards color-mix', /@supports \(color: color-mix\(/.test(DEFS), true);
for (const a of ALPHAS) {
    // The literal floor must come first so a browser without color-mix — Tizen 6
    // is Chromium 76 — keeps the glow rather than resolving to nothing.
    const floor = new RegExp('--jpf-accent-' + a + ':\\s*rgba\\(0, 164, 220');
    const mixed = new RegExp('--jpf-accent-' + a + ':\\s*color-mix\\(in srgb, var\\(--jpf-accent\\)');
    check(a + ' has a literal floor', floor.test(DEFS), true);
    check(a + ' upgrades under @supports', mixed.test(DEFS), true);
    check(a + ' floor precedes the upgrade',
        DEFS.search(floor) < DEFS.search(mixed), true);
}

console.log();
console.log('── Nothing is left hardcoded ─────────────────────────────────');
check('no #00a4dc outside the definitions', /#00a4dc/i.test(CONSUMERS.replace(/#00A4DC/g, '')), false);
check('no raw accent rgba outside the definitions',
    /rgba\(\s*0,\s*164,\s*220/.test(CONSUMERS), false);

// Inline style="" attributes live outside the <style> block entirely; they are
// the ones most easily missed.
// CSS is a contiguous slice of SRC, so removing it leaves exactly the source that is
// not inside the <style> block. (Was two slices around the literal's offsets, which
// _lib.extractCss deliberately does not expose.)
const OUTSIDE = SRC.replace(CSS, '');
check('no lowercase #00a4dc in inline styles', /#00a4dc/.test(OUTSIDE), false);

console.log();
console.log('── Every tint used is a tint defined ─────────────────────────');
const used = new Set((SRC.match(/var\(--jpf-accent-(a\d+)\)/g) || [])
    .map(m => m.replace(/var\(--jpf-accent-|\)/g, '')));
check('all used tints are declared', [...used].every(u => ALPHAS.includes(u)), true);
check('no tint is declared but unused', ALPHAS.every(a => used.has(a)), true);

console.log();
console.log('── Avatar swatches stay literal data ─────────────────────────');
// A profile's avatar colour is stored in configuration and compared as a
// string. It must never become a var() reference.
check('palette entries untouched', (SRC.match(/#00A4DC/g) || []).length, 11);
check('DEFAULT_AVATAR_COLOR is still a colour',
    /const DEFAULT_AVATAR_COLOR = '#00A4DC';/.test(SRC), true);
check('the palette array is still literals',
    /'#00A4DC', '#E50914', '#22C55E'/.test(SRC), true);
check('no var() reached the palette', /palette[\s\S]{0,400}var\(--/.test(SRC), false);

console.log();
console.log('── Signals mean what they look like ──────────────────────────');
// The pills were inverted: "PIN Protected" red, "No PIN" green. Neither state is
// an error, so neither may carry an error colour.
const locked = CSS.slice(CSS.indexOf('.profile-pin-badge.locked'));
const lockedRule = locked.slice(0, locked.indexOf('}'));
const unlocked = CSS.slice(CSS.indexOf('.profile-pin-badge.unlocked'));
const unlockedRule = unlocked.slice(0, unlocked.indexOf('}'));

check('protected is not red', /230,\s*0,\s*0|#ff6b6b/.test(lockedRule), false);
check('unprotected is not green', /0,\s*230,\s*0|#51cf66/.test(unlockedRule), false);
check('protected reads more present than unprotected',
    lockedRule.includes('rgba(255, 255, 255, 0.88)')
    && unlockedRule.includes('rgba(255, 255, 255, 0.45)'), true);

console.log();
console.log('── Destructive triggers are quieter than confirmations ───────');
// btn-danger stays filled: it is the Confirm button on the dialog these open, and
// the panic Disable button, where loud red IS the primary action.
const danger = CSS.slice(CSS.indexOf('.btn-danger {'));
const dangerRule = danger.slice(0, danger.indexOf('}'));
check('btn-danger is still filled', /background:\s*rgba\(230,0,0,0\.85\)/.test(dangerRule), true);

const quiet = CSS.slice(CSS.indexOf('.btn-danger-quiet {'));
const quietRule = quiet.slice(0, quiet.indexOf('}'));
check('btn-danger-quiet exists', CSS.includes('.btn-danger-quiet {'), true);
check('and is not filled', /background:\s*transparent/.test(quietRule), true);
check('but keeps a red edge', /border:.*rgba\(230,0,0/.test(quietRule), true);

for (const id of ['edit-delete-btn', 'bonfire-delete-btn', 'bonfire-leave-btn']) {
    const re = new RegExp('id="' + id + '"[^>]*class="profiles-btn btn-danger-quiet"');
    check(id + ' uses the quiet style', re.test(SRC), true);
}
// The confirm dialog must NOT have been demoted along with them.
check('the confirm button stays loud',
    /id="dialog-confirm-btn"[^>]*class="profiles-btn btn-danger"/.test(SRC), true);
check('panic disable stays loud',
    /id="profiles-panic-submit"[^>]*class="profiles-btn btn-danger"/.test(SRC), true);

console.log();
console.log('── The avatar colour stays off the border ────────────────────');
const avStart = CSS.search(/\n\s*\.profile-avatar \{/);
const av = CSS.slice(avStart);
const avRule = av.slice(0, av.indexOf('}'));
check('background is clipped to the padding box', /background-clip:\s*padding-box/.test(avRule), true);
check('the transparent border is still there', /border:\s*3px solid transparent/.test(avRule), true);

console.log();
console.log('── Bonfire headings carry no colour of their own ─────────────');
for (const h of ['Your Hosted Bonfire', 'Join a Bonfire', 'Host a Bonfire', 'Joined Bonfire']) {
    const re = new RegExp('<label style="[^"]*color:[^"]*">' + h.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '</label>');
    check(h + ' is plain', re.test(SRC), false);
}
// The admin warning rail and the flame keep their amber — those are semantic.
check('the admin warning keeps its caution colour',
    SRC.includes('border-left: 3px solid #ff9900'), true);

console.log();
console.log('── Shape and spacing are tokenised ───────────────────────────');
for (const t of ['--jpf-r-sm: 6px', '--jpf-r-md: 12px', '--jpf-r-lg: 20px',
                 '--jpf-gap: 12px', '--jpf-gap-lg: 1.25rem',
                 '--jpf-w-narrow: 420px', '--jpf-w-form: 560px', '--jpf-w-wide: 960px']) {
    check(t.split(':')[0] + ' is defined', DEFS.includes(t), true);
}
// Nothing may go back to a literal radius.
check('no literal px radius survives',
    (CSS.match(/border-radius: ?\d+px/g) || []).filter(m => !m.includes('999')).length, 0);
// 50% and 999px are shapes, not sizes, and must NOT have been tokenised.
//
// A bare expected count used to sit here, and deleting one dead function that happened to
// carry an inline `border-radius: 50%` failed it — reporting a tokenisation regression
// when nothing had been tokenised. A count answers "how many" when the question is "did
// any circle become a token"; it fails on a deletion and, worse, would stay green if one
// circle were tokenised while another was added.
//
// Checked as a floor plus a rule instead: circles still exist in quantity, and no
// element that should be round carries a radius token.
// Named, not counted. Each of these is round because being round is what it means — a
// lock badge, a colour swatch, a spinner. If one is tokenised its name disappears from
// this list and the failure says which. A total cannot: it fails when a dead rule is
// deleted (which is how this was found), and stays green when one circle is tokenised
// while another is added.
const circleSelectors = (function () {
    const out = [];
    const re = /border-radius: ?50%/g;
    let m;
    while ((m = re.exec(SRC)) !== null) {
        const before = SRC.slice(Math.max(0, m.index - 900), m.index);
        const sels = before.match(/([.#][A-Za-z][\w .:#>()-]*?)\s*\{/g) || [];
        out.push(sels.length ? sels[sels.length - 1].replace(/\s*\{$/, '').trim() : '(inline)');
    }
    return out;
})();

for (const sel of [
    '.avatar-library-item',
    '.profile-card.is-switching .profile-avatar-container::after',
    '.profiles-btn.is-busy::after',
    '.profile-avatar.is-transparent .profile-avatar-overlay-svg',
    '.profile-lock-indicator',
    '.profile-bonfire-indicator',
    '#profiles-floating-bubble.profiles-floating-fallback',
    '.color-dot',
    '.image-upload-preview',
    '.tag-chip-remove'
]) {
    check(sel + ' is still a circle', circleSelectors.includes(sel), true);
}
check('the pill kept its 999px', (SRC.match(/border-radius: ?999px/g) || []).length, 1);

console.log();
console.log('── One control looks like the others ─────────────────────────');
// Allowed Devices cannot become a <select> — it is multi-select with checkboxes —
// so it has to borrow the natives' appearance instead. It sat directly above two
// real selects, which is what made the mismatch obvious.
const trig = CSS.slice(CSS.indexOf('.devices-dropdown-trigger {'));
const trigRule = trig.slice(0, trig.indexOf('}'));
const sel = CSS.slice(CSS.indexOf('.form-group select {'));
const selRule = sel.slice(0, sel.indexOf('}'));

for (const prop of ['background-size: 20px', 'border-radius: var(--jpf-r-md)',
                    'font-size: 1rem', 'padding-right: 36px']) {
    check('trigger matches the natives on ' + prop.split(':')[0],
        trigRule.includes(prop) && selRule.includes(prop), true);
}
check('both draw the same chevron',
    trigRule.includes("M7 10l5 5 5-5H7z") && selRule.includes("M7 10l5 5 5-5H7z"), true);
check('the text arrow is gone', SRC.includes('>▼<'), false);

console.log();
console.log('── Choosing a picture is one action, not six ─────────────────');
check('the sources panel exists', /id="\$\{prefix\}-picture-sources"/.test(SRC), true);
check('it is collapsed by default', /\.picture-sources \{[^}]*display: none/.test(CSS), true);
check('and opens on a class', /\.picture-sources\.is-open \{[^}]*display: flex/.test(CSS), true);
check('one button controls it', /id="\$\{prefix\}-change-picture"/.test(SRC), true);
check('the button announces its state', /aria-expanded="\$\{sourcesOpen\}"/.test(SRC), true);
check('it opens when there is no picture yet',
    SRC.includes('const sourcesOpen = !currentImage;'), true);

// Every id the picker binds to must still exist — they only moved inside the panel.
for (const id of ['image-upload-preview', 'image-error', 'profile-image-file',
                  'profile-image-label', 'avatar-library']) {
    check(id + ' survived the move', SRC.includes('${prefix}-' + id), true);
}
// profile-image-url is deliberately absent now: a pasted link could not be cropped,
// put nothing on the server, and died silently when the far end went away.
check('the URL source is gone', SRC.includes('profile-image-url'), false);
// Remove must NOT be inside the collapsed panel — it belongs beside Change picture,
// which is where the picker now renders it. See uitest.js for the rest.
check('Remove sits in the action row, not the panel',
    SRC.indexOf('${prefix}-clear-profile-image-btn') < SRC.indexOf('id="${prefix}-picture-sources"'), true);

console.log();
console.log('── Gate footer button spacing ────────────────────────────────');
const footer = CSS.slice(CSS.indexOf('.profiles-footer {'));
const footerRule = footer.slice(0, footer.indexOf('}'));
check('.profiles-footer has a gap', /gap:\s*var\(--jpf-gap-lg\)/.test(footerRule), true);
check('matches .pin-actions, the other gate row',
    /\.pin-actions\s*\{[^}]*gap:\s*var\(--jpf-gap-lg\)/.test(CSS), true);
check('wraps rather than crowding a phone', /flex-wrap:\s*wrap/.test(footerRule), true);

console.log();
console.log(pass + ' passed, ' + fail + ' failed');
process.exit(fail === 0 ? 0 : 1);
