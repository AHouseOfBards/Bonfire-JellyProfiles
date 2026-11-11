/*
 * The admin page under Jellyfin's light themes (R-1238).
 *
 * Text and hairlines were written as white (#fff, rgba(255,255,255,a)), which is right on the
 * dark dashboard and invisible on light and appletv. dashwarm.js measures the ember and the
 * status colours but treats white and black overlays as neutral, so it never saw them. This
 * checks the other half, one literal and one token at a time:
 *
 *   - no white literal outside a token declaration, except white drawn on a fill the page
 *     paints itself (the red badge and button), and the contrast helpers in the theme editor;
 *   - every --jpf-wNN or --jpf-ink-hi the page uses is defined on the dark page and in both
 *     light blocks, and in the light ones it is black-based, not white.
 *
 *     node tests/js/dashlight.js "" /path/to/old/profilesDashboard.html
 */
'use strict';

const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; return; }
    fails.push(name);
    console.log('  FAIL  ' + name + (detail ? ' — ' + detail : ''));
}

const html = L.readDashboard();
const lines = html.split(/\r?\n/);

console.log('\n── White literals ──────────────────────────────────────────────');
const white = /#fff\b|#ffffff\b|rgba?\(\s*255\s*,\s*255\s*,\s*255/i;
const allowed = /^\s*--jpf-|jpf-badge-bad|background:#d23f3f|themeContrast|themeMix|const on = dark/;
const stray = [];
lines.forEach((l, i) => { if (white.test(l) && !allowed.test(l)) stray.push((i + 1) + ': ' + l.trim().slice(0, 80)); });
ok('no white text or hairline is written as a literal', stray.length === 0, stray.length + ' — ' + stray.slice(0, 4).join(' | '));

console.log('\n── Tokens ──────────────────────────────────────────────────────');
const used = {};
(html.match(/var\(--jpf-(?:w\d+|ink-hi)\)/g) || []).forEach(v => { used[v.slice(4, -1)] = true; });
const names = Object.keys(used);
ok('the page uses the tokens at all', names.length > 5, String(names.length));

function block(open) {
    const at = html.indexOf(open);
    if (at < 0) return '';
    return html.slice(at, html.indexOf('}', at));
}
const dark = block('#profilesConfigurationPage {');
const light = block('html[data-theme="light"] #profilesConfigurationPage');
names.forEach(n => {
    const re = new RegExp(n + ':\\s*([^;]+);');
    const d = re.exec(dark), l = re.exec(light);
    ok(n + ' is defined for the dark page', !!d);
    ok(n + ' is defined for the light themes', !!l);
    if (d) ok(n + ' is white on the dark page', /255,\s*255,\s*255|#fff/.test(d[1]), d[1]);
    if (l) ok(n + ' is black on the light themes', /rgba\(0,\s*0,\s*0/.test(l[1]), l[1]);
});
ok('light and appletv share one block', /html\[data-theme="light"\] #profilesConfigurationPage,\s*html\[data-theme="appletv"\] #profilesConfigurationPage/.test(html));

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
