// Structural checks for the 1.4.3 UI pass. Nothing renders here — these pin the
// facts that broke silently last time: a control that lost its remote-reachable
// wrapper, a handler left bound to markup that no longer exists, an unbalanced
// container, a CSS rule an inline style outranks.
const fs = require('fs');
const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(cond, label) {
    if (cond) { pass++; } else { fails.push(label); }
}
function has(hay, needle, label) { ok(hay.indexOf(needle) >= 0, label); }
function hasnt(hay, needle, label) { ok(hay.indexOf(needle) < 0, label); }
function count(hay, needle) { return hay.split(needle).length - 1; }

const js = L.readProfiles();
const html = L.readDashboard();

// ── Both files still parse ─────────────────────────────────────────────────
try { new vm.Script(js, { filename: 'profiles.js' }); ok(true, 'profiles.js parses'); }
catch (e) { fails.push('profiles.js parses: ' + e.message); }

const scriptBody = html.slice(
    html.indexOf('>', html.indexOf('<script type="text/javascript">')) + 1,
    html.lastIndexOf('</script>'));
try { new vm.Script(scriptBody, { filename: 'dashboard-inline' }); ok(true, 'dashboard script parses'); }
catch (e) { fails.push('dashboard script parses: ' + e.message); }

// Line endings: consistent, and no doubled CR. A '\r\r\n' parses but breaks every
// later anchor in the file, which is the real hazard here — a scripted edit writing the
// wrong ending into a file that used the other one.
//
// Consistency, not CRLF specifically: this used to assert every LF was part of a CRLF,
// which is true on a Windows checkout and false on Linux, so it failed the first time CI
// ran on ubuntu. Which ending a checkout gets is git's business now — see .gitattributes.
const pureCrlf = count(js, '\r\n') === count(js, '\n');
const pureLf = count(js, '\r') === 0;
ok(pureCrlf || pureLf, 'profiles.js line endings are consistent (mixed: '
    + count(js, '\r\n') + ' CRLF of ' + count(js, '\n') + ' LF)');
ok(count(js, '\r\r') === 0, 'profiles.js has no doubled CR');
ok(count(html, '\r\r') === 0, 'dashboard has no doubled CR');

// ── Add Profile is management, not a face on the gate ──────────────────────
// Markup, the "everything but the Add card" selector, and the handler.
ok(count(js, 'action-add-profile') === 3, 'the Add card has exactly its three references');
const addBlock = js.slice(js.indexOf('// Manage Profiles only.'), js.indexOf('sectionsHtml +='));
has(addBlock, 'if (this.isManageMode) {', 'Add card is gated on manage mode');
has(addBlock, 'action-add-profile', 'the Add card is inside that gate');
has(addBlock, 'profiles-limit-notice', 'the limit notice is inside it too');
hasnt(addBlock, '!this.isManageMode', 'nothing in the block renders on the gate');

// ── The picture picker ─────────────────────────────────────────────────────
hasnt(js, 'profile-image-url', 'the URL input is gone');
hasnt(js, 'Paste image URL', 'so is its placeholder');
hasnt(js, 'urlInput', 'and every reference to it');
hasnt(js, 'form-divider', 'the OR divider went with it, markup and CSS');

has(js, 'id="${prefix}-clear-profile-image-btn"', 'Remove is rendered by the picker');
ok(count(js, 'clear-profile-image-btn') === 2, 'rendered once, queried once');
has(js, 'const removeBtn = container.querySelector(`#${prefix}-clear-profile-image-btn`)',
    'the picker owns the Remove button');
has(js, "if (removeBtn) removeBtn.style.display = src ? 'inline-flex' : 'none';",
    'Remove follows the preview, including the first paint');
has(js, 'if (removeBtn) removeBtn.addEventListener(\'click\', clearPicture);', 'Remove clears');
has(js, 'clear: clearPicture', 'the returned handle clears the same way');
// Change picture and Remove must be the same class, or they are two different shapes.
const actionsBlock = js.slice(js.indexOf('<div class="image-upload-actions">'),
    js.indexOf('id="${prefix}-picture-sources"'));
ok(count(actionsBlock, 'profiles-btn btn-secondary image-upload-btn') === 2,
    'both actions carry the same button class');

// The disclosure has to be visible, not just announced. Both states key off the same
// aria-expanded the click handler already maintains, so there is no second source of
// truth to drift.
has(js, 'class="material-icons picture-caret" aria-hidden="true">expand_more',
    'the button carries a chevron');
has(js, '.picture-change-btn[aria-expanded="true"] .picture-caret', 'which turns over when open');
has(js, '.picture-change-btn[aria-expanded="true"] {', 'and the button stays visibly held');
has(js, "changeBtn.setAttribute('aria-expanded', open ? 'true' : 'false');",
    'aria-expanded is what drives both, and the handler sets it');
ok(/@media \(prefers-reduced-motion: reduce\)[^}]*\{\s*\.picture-caret/.test(js.replace(/\r\n/g, '\n')),
    'the rotation is dropped for reduced motion');

has(js, '.picture-source-block + .picture-source-block', 'sources are separated by a rule');
has(js, '.picture-sources .avatar-library-grid', 'the grid drops its own frame inside the panel');
ok(count(js, 'picture-source-title') === 3, 'two source headings and one style rule');

// ── The library list ───────────────────────────────────────────────────────
// The wrapper is what makes a checkbox reachable by remote: initTVCheckboxes binds
// to .library-check-label and OK arrives as Enter, which no native checkbox acts on.
const rowBlock = js.slice(js.indexOf('<div class="libart-row"'), js.indexOf('libart-choose">Choose'));
has(rowBlock, '<label class="library-check-label libart-check">', 'library rows keep the label wrapper');
has(rowBlock, '<input type="checkbox" class="library-checkbox"', 'the checkbox is inside it');
has(js, '<label class="library-check-label libart-toggle">',
    'the artwork switch is remote-reachable too');

has(js, 'row.classList.toggle("libart-has-art", entry.mode !== "inherit");',
    'the thumbnail slot is class-driven, not an inline display');
has(js, '.libart-list.show-artwork .libart-row.libart-has-art .libart-thumb',
    'a thumbnail shows only with artwork on and set');
// Presence of the hiding rule is not enough, and asserting only that is what let the
// placeholder ship: .libart-thumb declared display:flex in its own block further down
// the sheet, same specificity, so the later one won. Resolve the cascade instead.
// Comments out first: a "why" comment sits directly above most rules here, and the
// naive block regex below would otherwise capture it as part of the selector list —
// which is what made this check report a false failure on its first run.
const CSS = L.extractCss(js).replace(/\/\*[\s\S]*?\*\//g, '');
function lastDisplayFor(selector) {
    // Rules whose selector list names this class on its own — a descendant rule like
    // '.libart-list.show-artwork .libart-thumb' is more specific and does not count.
    const re = /([^{}]+)\{([^{}]*)\}/g;
    let m, winner = null;
    while ((m = re.exec(CSS)) !== null) {
        const sels = m[1].split(',').map(function (x) { return x.trim(); });
        if (!sels.includes(selector)) continue;
        const d = /(?:^|;)\s*display\s*:\s*([^;]+)/.exec(m[2]);
        if (d) winner = d[1].trim();
    }
    return winner;
}
['.libart-thumb', '.libart-mode', '.libart-choose'].forEach(function (sel) {
    ok(lastDisplayFor(sel) === 'none', sel + ' resolves to display:none with artwork off');
});
ok(lastDisplayFor('.picture-sources') === 'none', '.picture-sources resolves to display:none');

// width:100% plus padding overflows a content-box parent. This sheet has no global
// box-sizing rule, so any padded full-width block has to declare its own.
ok(/\.picture-sources \{[^}]*box-sizing: border-box;[^}]*padding:/.test(CSS.replace(/\r\n/g, '\n')),
    'the sources panel is border-box, so padding does not push it out of its card');
has(js, 'const anyArt = rows.some(', 'a profile that already uses artwork opens the section');
has(js, 'showArtwork(false);', 'and it is off otherwise');
has(js, 'Tick nothing and this profile sees the same libraries as your account.',
    'the list says what an empty list means');
has(js, 'A library tile takes its picture from whatever is inside the library',
    'and the artwork explainer is separate from it');
has(js, 'class="libart-head"', 'the artwork column is labelled');

// ── The admin page ─────────────────────────────────────────────────────────
const tabNames = ['general', 'avatars', 'accounts', 'activity', 'advanced'];
tabNames.forEach(function (n) {
    ok(count(html, 'data-tab="' + n + '"') === 1, 'one tab button for ' + n);
    ok(count(html, 'data-panel="' + n + '"') === 1, 'one panel for ' + n);
});
ok(count(html, 'role="tabpanel"') === 5, 'every panel is announced as one');
ok(count(html, 'jpf-tab-panel') === 5 + 3, 'five panels plus three style rules');
ok(count(html, ' hidden>') === 5, 'panels start hidden; show() opens exactly one');
// aria-controls must name a real element or the strip lies to a screen reader.
(html.match(/aria-controls="([^"]+)"/g) || []).forEach(function (m) {
    const id = m.slice(15, -1);
    ok(count(html, 'id="' + id + '"') === 1, 'aria-controls points at ' + id);
});
has(html, 'if (names.indexOf(start) < 0) start = names[0];',
    'a stored tab name is checked against the strip, never made into a selector');
has(html, 'initTabs(page);', 'tabs are initialised on pageshow');
has(html, 'if (!page.dataset.tabsBound)', 'and repeated pageshows do not stack handlers');
has(html, '#profilesConfigurationPage .jpf-tab-panel[hidden]', 'hidden panels are hidden explicitly');
// profiles.js is CRLF and this file is LF, so match across whatever separates them.
ok(/> \.sectionTitleContainer:first-child \{\s*margin-top: 0 !important;/.test(html),
    'the 3em section gap is beaten with !important, since it is an inline style');

// The stylesheet must live INSIDE the page element. Outside it, Jellyfin keeps the
// page and drops the rest, and every rule in it is silently dead — which is how five
// tabs shipped as bare user-agent buttons.
ok(html.indexOf('<style>') > html.indexOf('<div id="profilesConfigurationPage"'),
    'the stylesheet is inside the page element');
ok(html.indexOf('</style>') < html.indexOf('<div data-role="content">'),
    'and ahead of the content it styles');
has(html, 'background-color: transparent;', 'the tab reset kills the UA background');
has(html, '>General</button>', 'the first tab is General, not Settings inside Settings');

// The banners that say whether the plugin works at all stay outside the tabs.
const beforeTabs = html.slice(0, html.indexOf('<div class="jpf-tabs"'));
['injectionWarningContainer', 'staleVersionWarningContainer', 'betaNoticeContainer', 'injectionOkContainer']
    .forEach(function (id) { has(beforeTabs, 'id="' + id + '"', id + ' is above the tabs'); });

// Container balance across the page body: a missed </div> silently swallows a panel.
// The page's own closing </div> sits after the inline script, so the script is cut
// out of the middle rather than the tail being thrown away with it.
const body = html.slice(html.indexOf('<div id="profilesConfigurationPage"')).replace(
    html.slice(html.indexOf('<script type="text/javascript">'), html.lastIndexOf('</script>')), '');
const opens = (body.match(/<div\b/g) || []).length;
const closes = (body.match(/<\/div>/g) || []).length;
ok(opens === closes, 'div tags balance in the page body (' + opens + ' open, ' + closes + ' close)');

console.log(pass + ' passed, ' + fails.length + ' failed');
fails.forEach(function (f) { console.log('  FAIL  ' + f); });
process.exit(fails.length ? 1 : 0);
