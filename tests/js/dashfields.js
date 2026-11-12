/*
 * Dashboard: no field is left unstyled white (owner, 1.9.0.4 eye test: the Report a problem box
 * flashed bright white; the date and filter boxes on Switches were white too).
 *
 * A plain <textarea> or a date/search <input> that no stylesheet touches takes the browser's
 * own white. jellyfin-web only styles what upgrades (is="emby-input" and friends), and the
 * report box is not one. So the page itself has to give every field a background and a text
 * colour that read together on the theme in force.
 *
 * The fixture carries a blanket dark rule for text-like fields, which would hide exactly this
 * bug; it is removed first so only the page's own rules (and the browser's defaults) are
 * left to decide. Then, for every field on the page, in the dark and the light theme: the text
 * has at least 4.5:1 against the field's background (composited over what is behind it), and
 * on dark the field is not light.
 *
 *     node tests/js/dashfields.js [path/to/profilesDashboard.html]
 *
 * The optional path points the fixture at an older copy of the page (red-first).
 */
'use strict';
const fs = require('fs');
const path = require('path');
const fixture = require('../screens/fixture');
const chrome = require('../screens/chrome');

const ICONS = path.join(__dirname, '..', 'screens', '.cache', 'material-icons.woff2');
let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

const MEASURE = "(function(theme){"
    + "document.documentElement.setAttribute('data-theme',theme);"
    + "if(!window.__stripped){window.__stripped=1;[].forEach.call(document.styleSheets,function(sh){try{"
    + "for(var i=sh.cssRules.length-1;i>=0;i--){var r=sh.cssRules[i];if(r.selectorText&&/textarea/.test(r.selectorText)&&!/profilesConfigurationPage/.test(r.selectorText))sh.deleteRule(i);}}catch(e){}});}"
    + "function parse(c){var m=c.match(/[\\d.]+/g).map(Number);return {r:m[0],g:m[1],b:m[2],a:m.length>3?m[3]:1};}"
    + "function over(f,b){return {r:f.r*f.a+b.r*(1-f.a),g:f.g*f.a+b.g*(1-f.a),b:f.b*f.a+b.b*(1-f.a),a:1};}"
    + "function behind(e){var chain=[];for(var n=e;n;n=n.parentElement)chain.push(parse(getComputedStyle(n).backgroundColor));"
    + "var acc={r:255,g:255,b:255,a:1};for(var i=chain.length-1;i>=0;i--)acc=over(chain[i],acc);return acc;}"
    + "function lum(c){function f(v){v/=255;return v<=0.03928?v/12.92:Math.pow((v+0.055)/1.055,2.4);}return 0.2126*f(c.r)+0.7152*f(c.g)+0.0722*f(c.b);}"
    + "var out=[];[].forEach.call(document.querySelectorAll('#profilesConfigurationPage input,#profilesConfigurationPage select,#profilesConfigurationPage textarea'),function(e){"
    + "var t=(e.type||'').toLowerCase();if(/^(checkbox|radio|file|hidden|button|submit|range|color|image)$/.test(t))return;"
    + "var cs=getComputedStyle(e);var bg=behind(e);var fg=over(parse(cs.color),bg);"
    + "var l1=lum(bg),l2=lum(fg);var cr=(Math.max(l1,l2)+0.05)/(Math.min(l1,l2)+0.05);"
    + "out.push({id:e.id||e.className||e.tagName,type:t||e.tagName.toLowerCase(),cr:cr,bgLum:l1});});"
    + "return out;})";

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine: nothing was measured\n\n  0 passed, 0 failed, skipped'); return 0;
    }
    const fopts = fs.existsSync(ICONS) ? { materialIcons: ICONS } : {};
    if (process.argv[2]) fopts.dashboard = process.argv[2];
    const s = await fixture.start(0, fopts);
    const c = await chrome.launch();
    try {
        const r = await c.open('http://127.0.0.1:' + s.port + '/dashboard?tab=troubleshoot&device=desktop', 'desktop');
        if (r.error) { ok('the page draws', false, r.error); return 1; }
        await new Promise(r2 => setTimeout(r2, 500));
        for (const theme of ['dark', 'light']) {
            const m = await c.evaluate(MEASURE + "('" + theme + "')");
            ok(theme + ': the page has fields to look at (' + m.length + ')', m.length >= 20);
            ok(theme + ': the report box is among them', m.some(x => x.id === 'reportText'));
            ok(theme + ': the date and filter boxes are among them', ['auditFrom', 'auditTo', 'auditFilter'].every(id => m.some(x => x.id === id)));
            m.forEach(x => {
                ok(theme + ': ' + x.id + ' (' + x.type + ') reads, ' + x.cr.toFixed(1) + ':1', x.cr >= 4.5);
                if (theme === 'dark') ok(theme + ': ' + x.id + ' is not a white box', x.bgLum < 0.3, 'luminance ' + x.bgLum.toFixed(2));
            });
        }
    } finally { await c.close(); await s.close(); }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}
main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
