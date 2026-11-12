/*
 * Three layout defects on the admin page, measured in a real browser, one element at a time.
 *
 *   1. A closed "Advanced" section sat on top of the Save (or Post) button it is not part of.
 *   2. Avatar library: Save came before the upload controls, which act at once.
 *   3. Email: the server row was a different width from the fields under it, and the
 *      Save / "Show again" row pushed its buttons to opposite edges.
 *
 * The test page does not style jellyfin-web's own controls, so only the plugin's own markup
 * and CSS are judged: positions and gaps, never how a control looks.
 *
 * Red against the page before the fix:
 *
 *     node tests/js/adminlayout.js /path/to/old/profilesDashboard.html
 */
'use strict';

const fs = require('fs');
const path = require('path');
const fixture = require('../screens/fixture');
const chrome = require('../screens/chrome');

const ROOT = path.join(__dirname, '..', '..');
const ICONS = path.join(ROOT, 'tests', 'screens', '.cache', 'material-icons.woff2');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

// Runs in the page. Reports numbers, never asserts.
const MEASURE = `(function () {
  function r(el) { return el.getBoundingClientRect(); }
  function visible(el) { return !!(el && el.offsetParent); }
  var out = { advanced: [], avatar: null, email: null, onboarding: null };
  var panel = document.querySelector('.jpf-tab-panel:not([hidden])');
  if (!panel) return { error: 'no tab is showing' };
  // 1. Every closed Advanced section: the space to whatever follows it.
  [].slice.call(panel.querySelectorAll('details.jpf-advanced')).forEach(function (d) {
    var next = d.nextElementSibling;
    if (!next || !visible(next)) return;
    var open = d.open;
    d.open = false;
    var gap = r(next).top - r(d).bottom;
    d.open = open;
    out.advanced.push({ id: d.id || d.parentNode.id, next: (next.id || next.className || next.tagName).toString().slice(0, 40),
        isButton: !!next.querySelector('button') || next.tagName === 'BUTTON', gap: Math.round(gap * 10) / 10 });
  });
  // 2. The avatar library: where the first Save sits against the controls that act at once.
  var up = panel.querySelector('#avatarUploadBtn');
  if (up) {
    var sect = panel.querySelector('#jpfAvatars');
    var saves = [].slice.call(panel.querySelectorAll('.jpf-save-settings')).filter(function (b) {
      return visible(b) && r(b).top > r(sect).top;
    });
    var grid = panel.querySelector('#avatarLibraryGrid');
    out.avatar = { upload: r(up).top, grid: r(grid).top, firstSave: saves.length ? r(saves[0]).top : -1 };
  }
  // 3. Email server row against the fields under it; the onboarding buttons against each other.
  var host = panel.querySelector('#emailHost');
  if (host) {
    var row = host.closest('.jpf-email-server');
    var user = panel.querySelector('#emailUsername').closest('.inputContainer');
    out.email = { server: Math.round(r(row).width), field: Math.round(r(user).width) };
  }
  var again = panel.querySelector('#onboardingShowAgain');
  if (again) {
    var save = again.closest('.jpf-actions').querySelector('.jpf-save-settings');
    out.onboarding = { between: Math.round(r(again).left - r(save).right) };
  }
  return out;
})()`;

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine (set CHROME to its path): nothing was measured');
        console.log('\n  0 passed, 0 failed, skipped');
        return 0;
    }
    const given = process.argv[2];
    const server = await fixture.start(0, { materialIcons: ICONS, dashboard: given || undefined });
    const c = await chrome.launch();
    try {
        for (const device of ['desktop', 'phone']) {
            const seen = {};
            for (const tab of ['security', 'clients', 'messages', 'look', 'setup']) {
                const r = await c.open('http://127.0.0.1:' + server.port + '/dashboard?tab=' + tab + '&device=' + device, device);
                const where = tab + ' ' + device;
                if (r.error) { ok(where + ': the tab draws', false, r.error); continue; }
                const m = await c.evaluate(MEASURE);
                if (m.error) { ok(where + ': the tab draws', false, m.error); continue; }
                // 1. Roughly a line and a half: clearly apart from the button, never fused with it.
                m.advanced.filter(a => a.isButton).forEach(a => {
                    seen.adv = true;
                    ok(where + ': a closed Advanced section keeps clear of the button after it (' + a.id + ' -> ' + a.next + ')',
                        a.gap >= 20, a.gap + 'px');
                });
                // 2.
                if (m.avatar) {
                    seen.avatar = true;
                    ok(where + ': the first Save in Avatar library comes after the upload controls and the pictures',
                        m.avatar.firstSave > m.avatar.upload && m.avatar.firstSave > m.avatar.grid,
                        'upload ' + Math.round(m.avatar.upload) + ', pictures ' + Math.round(m.avatar.grid) + ', Save ' + Math.round(m.avatar.firstSave));
                }
                // 3.
                if (m.email) {
                    seen.email = true;
                    ok(where + ': the email server row is as wide as the fields under it',
                        Math.abs(m.email.server - m.email.field) <= 1, 'server ' + m.email.server + 'px, field ' + m.email.field + 'px');
                }
                if (m.onboarding) {
                    seen.onboarding = true;
                    // Phone: the two buttons may stack, which puts the second far left of the first's edge.
                    if (device === 'desktop') ok(where + ': Save and "Show again" sit together, not at opposite edges',
                        m.onboarding.between >= 0 && m.onboarding.between <= 24, m.onboarding.between + 'px between them');
                }
            }
            ok(device + ': the harness reached an Advanced section above a button', !!seen.adv);
            ok(device + ': and the avatar library, the email server row and the onboarding buttons',
                !!(seen.avatar && seen.email && seen.onboarding));
        }
    } finally {
        await c.close();
        await server.close();
    }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}

main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
