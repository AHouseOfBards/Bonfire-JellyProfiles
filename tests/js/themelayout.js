/*
 * The layout choices a custom theme may make (2.0): profiles as tiles or a list, centred or
 * aligned left, small, medium or large. Each one is drawn by the real script and stylesheet
 * in Chrome (tests/screens), on every device, and measured, not read from the source:
 *
 *   - the defaults written out are the same screen, to the pixel, as leaving them out, so no
 *     built-in and no saved theme changes;
 *   - nothing scrolls sideways, no tile sits on another, a name stays inside its tile, and
 *     the title and the buttons stay off the tiles;
 *   - a list is one column with the name beside the picture; tiles share rows;
 *   - left puts the title, the profiles and the buttons on one edge near the screen's left;
 *     centred keeps them on the middle line;
 *   - small < medium < large, and medium is today's size;
 *   - a household of four still fits a screen wherever the default does;
 *   - on a television the remote walks a list with Down and Up, one profile at a time, the
 *     one it is on in view and drawn with a ring, through the add tile to the buttons.
 *
 *     node tests/js/themelayout.js [/path/to/old/profiles.js]
 */
'use strict';

const fs = require('fs');
const path = require('path');
const https = require('https');

const ROOT = path.resolve(__dirname, '..', '..');
const fixture = require('../screens/fixture');
const chrome = require('../screens/chrome');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; if (process.env.VERBOSE) console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

const ICONS = path.join(ROOT, 'tests', 'screens', '.cache', 'material-icons.woff2');
const ICONS_URL = 'https://cdn.jsdelivr.net/npm/material-icons@1.13.12/iconfont/material-icons.woff2';
function fetchIcons() {
    if (fs.existsSync(ICONS)) return Promise.resolve(true);
    fs.mkdirSync(path.dirname(ICONS), { recursive: true });
    return new Promise(resolve => https.get(ICONS_URL, res => {
        if (res.statusCode !== 200) return resolve(false);
        const chunks = [];
        res.on('data', c => chunks.push(c));
        res.on('end', () => { fs.writeFileSync(ICONS, Buffer.concat(chunks)); resolve(true); });
    }).on('error', () => resolve(false)));
}

const DEVICES = ['phone', 'phone-land', 'tablet', 'desktop', 'tv720', 'tv'];
// Bonfire draws households in panels, Bloom without: the two ways the grid is laid out.
const THEMES = ['bonfire', 'bloom'];
const PROFILES = ['tiles', 'list'];
const ALIGN = ['center', 'left'];
const SIZES = ['small', 'medium', 'large'];

const MEASURE = `(function () {
  var root = document.getElementById('profiles-gate-overlay');
  if (!root) return { found: false };
  function r(e) { var b = e.getBoundingClientRect(); return { l: b.left, t: b.top, r: b.right, b: b.bottom, w: b.width, h: b.height }; }
  function textBox(e) { var g = document.createRange(); g.selectNodeContents(e); var b = g.getBoundingClientRect(); return { l: b.left, t: b.top, r: b.right, b: b.bottom, w: b.width, h: b.height }; }
  var content = root.querySelector('.profiles-modal-content');
  var title = root.querySelector('.profiles-title');
  var cards = [].slice.call(root.querySelectorAll('.jpf-profile-card')).map(function (c) {
    var a = c.querySelector('.jpf-profile-avatar-container'), n = c.querySelector('.jpf-profile-name');
    var lines = [].slice.call(n.children).filter(function (s) { return !s.classList.contains('jpf-sr'); }).map(function (s) { return getComputedStyle(s).overflow === 'hidden' ? r(s) : textBox(s); });
    return { box: r(c), pic: r(a), name: r(n), lines: lines };
  });
  // The buttons where the layout puts them. While the households overflow, the row is pinned
  // to the bottom of the screen and is meant to lie over the tiles scrolled behind it
  // (pickerbar.js is what judges that), so it is measured in the flow, with the pin off.
  var footer = root.querySelector('.profiles-footer');
  var pinned = !!footer && footer.classList.contains('jpf-footer-stuck');
  if (pinned) footer.classList.remove('jpf-footer-stuck');
  var buttons = [].slice.call(root.querySelectorAll('.profiles-footer .profiles-btn')).map(r);
  if (pinned) footer.classList.add('jpf-footer-stuck');
  return { found: true, vw: innerWidth, vh: innerHeight, root: parseFloat(getComputedStyle(document.documentElement).fontSize),
    sideways: Math.max(document.documentElement.scrollWidth - innerWidth, root.scrollWidth - root.clientWidth),
    fits: root.scrollHeight <= root.clientHeight + 1,
    fitted: !!(content && content.classList.contains('jpf-fitted')),
    content: content ? r(content) : null, title: title ? textBox(title) : null, cards: cards, buttons: buttons };
})()`;

function over(a, b) { return a.l < b.r - 1 && a.r > b.l + 1 && a.t < b.b - 1 && a.b > b.t + 1; }
const R = v => Math.round(v);

function check(where, m, lay) {
    ok(where + ': the gate draws', m.found && m.cards.length > 0);
    if (!m.found || !m.cards.length) return;
    ok(where + ': nothing scrolls sideways', m.sideways <= 1, m.sideways + 'px');
    const bad = [];
    m.cards.forEach((c, i) => {
        if (c.box.l < -1 || c.box.r > m.vw + 1) bad.push('tile ' + (i + 1) + ' off the screen');
        m.cards.forEach((d, j) => { if (j > i && over(c.box, d.box)) bad.push('tiles ' + (i + 1) + ' and ' + (j + 1) + ' overlap'); });
        c.lines.forEach(L => { if (L.w && (L.l < c.box.l - 1 || L.r > c.box.r + 1)) bad.push('name ' + (i + 1) + ' leaves its tile'); });
        if (m.title && over(m.title, c.box)) bad.push('the title is on tile ' + (i + 1));
        m.buttons.forEach((b, k) => { if (over(b, c.box)) bad.push('button ' + (k + 1) + ' is on tile ' + (i + 1)); });
    });
    ok(where + ': tiles, names, title and buttons each keep to themselves', !bad.length, bad.slice(0, 4).join('; '));

    const rows = {};
    m.cards.forEach(c => { const k = R(c.box.t / 4); rows[k] = (rows[k] || 0) + 1; });
    const widest = Math.max.apply(null, Object.keys(rows).map(k => rows[k]));
    if (lay.profiles === 'list') {
        const stacked = m.cards.every((c, i) => i === 0 || c.box.t >= m.cards[i - 1].box.b - 1);
        ok(where + ': a list is one profile a row', stacked && widest === 1, 'widest row ' + widest);
        const beside = m.cards.every(c => c.name.l >= c.pic.r - 1 && c.name.t < c.pic.b && c.name.b > c.pic.t);
        ok(where + ': the name is beside the picture', beside);
    } else if (m.cards.length >= 4) {
        ok(where + ': tiles share rows', widest >= 2, 'widest row ' + widest);
    }

    const lefts = [m.title.l, Math.min.apply(null, m.cards.map(c => c.box.l))].concat(m.buttons.length ? [m.buttons[0].l] : []);
    if (lay.align === 'left') {
        const edge = m.content.l;
        ok(where + ': aligned left, the column starts near the left of the screen', edge <= m.vw * 0.05 + 1 && edge >= 0, 'content at ' + R(edge) + ' of ' + m.vw);
        // A household panel pads its tiles by 2rem (54px on a 1080p television).
        const far = lefts.filter(l => l - edge > 2.5 * m.root + 1);
        ok(where + ': the title, the first profile and the buttons start on that edge', !far.length, lefts.map(R).join(', ') + ' against ' + R(edge));
    } else {
        const mid = m.vw / 2;
        const tc = (m.title.l + m.title.r) / 2;
        const cl = Math.min.apply(null, m.cards.map(c => c.box.l)), cr = Math.max.apply(null, m.cards.map(c => c.box.r));
        ok(where + ': centred, the title and the profiles are on the middle line',
            Math.abs(tc - mid) < 3 && Math.abs((cl + cr) / 2 - mid) < 3, 'title ' + R(tc) + ', profiles ' + R((cl + cr) / 2) + ', middle ' + mid);
    }
}

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine (set CHROME to its path): nothing was measured');
        console.log('\n  0 passed, 0 failed, skipped');
        return 0;
    }
    if (!await fetchIcons()) {
        console.log('  SKIPPED  the Material Icons font could not be fetched: nothing was measured');
        console.log('\n  0 passed, 0 failed, skipped');
        return 0;
    }
    const given = process.argv[2];
    const server = await fixture.start(0, { materialIcons: ICONS, source: given ? fs.readFileSync(given, 'utf8') : undefined });
    const c = await chrome.launch();
    const base = 'http://127.0.0.1:' + server.port + '/?';
    // refit: fit the gate again once it has settled, as a resize would. A gate fitted while
    // its fade-in is still running comes out a pixel or two different from one draw to the
    // next (269 to 273px on a 1080p set, with nothing changed), so two screens can only be
    // compared to the pixel once both are fitted at rest.
    const draw = async (q, device, refit) => {
        const r = await c.open(base + q + '&device=' + device, device);
        if (r.error) return { found: false, error: r.error };
        if (refit) await c.evaluate('(function () { window.__bonfire.fitGateToScreen(document.getElementById("profiles-gate-overlay")); return true; })()');
        return c.evaluate(MEASURE);
    };
    try {
        for (const theme of THEMES) {
            for (const device of DEVICES) {
                // The defaults written out are the theme as it was.
                // Both as custom copies, so the only difference is the four fields.
                const plain = await draw('theme=' + theme + '&scene=picker&layout=profiles:-,align:-,size:-,logo:-', device, true);
                const spelled = await draw('theme=' + theme + '&scene=picker&layout=profiles:tiles,align:center,size:medium,logo:top-right', device, true);
                ok(theme + ' ' + device + ': the defaults written out draw the same screen', plain.found && spelled.found
                    && JSON.stringify(plain) === JSON.stringify(spelled), spelled.error || '');

                for (const count of theme === 'bonfire' ? [0, 21] : [0]) {
                    const width = {};
                    for (const profiles of PROFILES) {
                        for (const align of ALIGN) {
                            for (const size of SIZES) {
                                const lay = { profiles, align, size };
                                const where = theme + ' ' + device + (count ? ' ' + count + ' profiles' : '') + ' ' + profiles + '/' + align + '/' + size;
                                const m = await draw('theme=' + theme + '&scene=picker' + (count ? '&count=' + count : '')
                                    + '&layout=profiles:' + profiles + ',align:' + align + ',size:' + size, device);
                                if (!m.found) { ok(where + ': the gate draws', false, m.error); continue; }
                                check(where, m, lay);
                                width[profiles + align + size] = { w: m.cards[0].pic.w, fitted: m.fitted };
                                // A list is taller than rows of tiles: where four will not fit even
                                // at its smallest picture (a phone on its side), it scrolls, and only
                                // up and down (the sideways check above).
                                if (!count && plain.fits) ok(where + ': four profiles fit the screen, as the default does'
                                    + (profiles === 'list' ? ', or the pictures are at the smallest a list draws' : ''),
                                    m.fits || (profiles === 'list' && m.fitted && m.cards[0].pic.w <= (/^tv/.test(device) ? 72 : 48) + 0.5),
                                    'picture ' + R(m.cards[0].pic.w) + 'px' + (m.fitted ? ', fitted' : ''));
                                // Fitted, the size wanders by a few pixels with when the fit ran
                                // (see draw); the spelled-out defaults above cover that case.
                                if (profiles === 'tiles' && size === 'medium' && align === 'center' && !count && !m.fitted && !plain.fitted) {
                                    ok(where + ': medium tiles are today\'s size', R(m.cards[0].pic.w) === R(plain.cards[0].pic.w),
                                        R(m.cards[0].pic.w) + ' vs ' + R(plain.cards[0].pic.w));
                                }
                            }
                            const s = width[profiles + align + 'small'], md = width[profiles + align + 'medium'], lg = width[profiles + align + 'large'];
                            const where = theme + ' ' + device + (count ? ' ' + count + ' profiles' : '') + ' ' + profiles + '/' + align;
                            if (s && md && lg) {
                                // A gate too full for the screen shrinks every size to what fits,
                                // so only an unfitted pair has to differ.
                                // Fitted, a size is the largest that fits, which is all that can be
                                // asked of it: large tiles take more room, so they may fit a little
                                // smaller than medium ones that never needed fitting.
                                ok(where + ': small <= medium <= large', s.w <= md.w + 0.5 && (lg.fitted || md.w <= lg.w + 0.5),
                                    [s, md, lg].map(x => R(x.w) + (x.fitted ? ' fitted' : '')).join(' / '));
                                if (!s.fitted && !md.fitted) ok(where + ': small is smaller than medium', s.w < md.w - 1, R(s.w) + ' vs ' + R(md.w));
                                if (!md.fitted && !lg.fitted) ok(where + ': large is larger than medium', lg.w > md.w + 1, R(lg.w) + ' vs ' + R(md.w));
                            }
                        }
                    }
                }
            }
        }

        // The remote on a list: Down and Up, one profile at a time, in view, with a ring.
        const key = async (k, code) => {
            await c.page('Input.dispatchKeyEvent', { type: 'rawKeyDown', key: k, code: k, windowsVirtualKeyCode: code, nativeVirtualKeyCode: code });
            await c.page('Input.dispatchKeyEvent', { type: 'keyUp', key: k, code: k, windowsVirtualKeyCode: code, nativeVirtualKeyCode: code });
            await new Promise(r => setTimeout(r, 60));
        };
        const FOCUS = `(function () {
          var a = document.activeElement, root = document.getElementById('profiles-gate-overlay');
          var cards = [].slice.call(root.querySelectorAll('.jpf-profile-card'));
          var i = cards.indexOf(a);
          var out = { index: i, button: !!(a && a.classList && a.classList.contains('profiles-btn')), add: !!(a && a.classList && a.classList.contains('action-add-profile')) };
          if (a && a.getBoundingClientRect) { var b = a.getBoundingClientRect(); out.inView = b.top >= -1 && b.bottom <= innerHeight + 1; }
          var av = a && a.querySelector && a.querySelector('.jpf-profile-avatar');
          if (av) { var cs = getComputedStyle(av); out.ring = parseFloat(cs.borderTopWidth); out.ringColour = cs.borderTopColor; }
          out.count = cards.length;
          return out;
        })()`;
        for (const device of ['tv720', 'tv']) {
            for (const scene of ['picker', 'manage']) {
                for (const count of [0, 21]) {
                    const where = 'remote on a list, ' + device + ' ' + scene + (count ? ' ' + count + ' profiles' : '');
                    const r = await c.open(base + 'theme=bonfire&scene=' + scene + (count ? '&count=' + count : '') + '&layout=profiles:list&device=' + device, device);
                    if (r.error) { ok(where + ': draws', false, r.error); continue; }
                    // The fixture never runs init(), which is what binds the remote's keys.
                    await c.evaluate(`(function () { window.__bonfire._bindOverlayFocusTrap();
                      var f = document.querySelector('#profiles-gate-overlay .jpf-profile-card'); if (f) f.focus(); return true; })()`);
                    let f = await c.evaluate(FOCUS);
                    const n = f.count;
                    const walk = [];
                    for (let i = 1; i < n; i++) {
                        await key('ArrowDown', 40);
                        f = await c.evaluate(FOCUS);
                        if (f.index !== i) { walk.push('Down ' + i + ' landed on ' + (f.button ? 'a button' : 'profile ' + (f.index + 1))); break; }
                        if (!f.inView) walk.push('profile ' + (i + 1) + ' out of view');
                        if (!(f.ring >= 4) || f.ringColour === 'rgba(0, 0, 0, 0)') walk.push('profile ' + (i + 1) + ' has no ring (' + f.ring + 'px ' + f.ringColour + ')');
                    }
                    ok(where + ': Down walks every profile in turn, each in view with a ring', !walk.length, walk.slice(0, 3).join('; '));
                    // At the limit (21) Manage shows a notice instead of the add tile.
                    if (scene === 'manage' && !count) ok(where + ': the last one is the add tile', f.add);
                    await key('ArrowDown', 40);
                    f = await c.evaluate(FOCUS);
                    ok(where + ': Down from the last reaches the buttons', f.button && f.inView);
                    await key('ArrowUp', 40 - 2);
                    f = await c.evaluate(FOCUS);
                    ok(where + ': Up comes back to the last profile', f.index === n - 1, 'on ' + f.index);
                    await key('ArrowUp', 38);
                    f = await c.evaluate(FOCUS);
                    ok(where + ': and Up again to the one above it', f.index === n - 2 && f.inView, 'on ' + f.index);
                }
            }
        }
    } finally {
        await c.close();
        await server.close();
    }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}

main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
