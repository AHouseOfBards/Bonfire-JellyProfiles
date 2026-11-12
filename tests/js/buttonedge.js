/*
 * Dashboard: every button stays inside the row it is the end of (owner, 1.9.0.4 eye test: on
 * Sessions the Sign out button sat past the row's right edge, outside its box).
 *
 * For every tab, at 2900, 1366 and 360px, each visible button or button-link is measured
 * against every ancestor up to its tab panel: its right edge may not pass the ancestor's
 * right edge, and where the ancestor is a list row (class listItem, *-row, *-item or *-card)
 * it may not pass the ancestor's right edge less its right padding either. A row may not be
 * wider than the list it is in (the cause on Sessions: width: 100% and padding with
 * content-box sizing, the same as the Switches log).
 *
 * The fixture does not carry jellyfin-web's .listItem, so what it does to a row is added
 * here: it is a flex box with its own padding and the box model the browser gives it.
 *
 *     node tests/js/buttonedge.js [path/to/profilesDashboard.html]
 *
 * The optional path points the fixture at an older copy of the page (red-first).
 */
'use strict';
const fs = require('fs');
const path = require('path');
const fixture = require('../screens/fixture');
const chrome = require('../screens/chrome');

const ICONS = path.join(__dirname, '..', 'screens', '.cache', 'material-icons.woff2');
const SIZES = [['wide', 2900, 1200, false, 'desktop'], ['laptop', 1366, 768, false, 'desktop'], ['phone', 360, 740, true, 'phone']];
const TABS = ['status', 'households', 'devices', 'security', 'look', 'setup', 'clients', 'messages', 'activity', 'troubleshoot'];
let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; if (process.env.VERBOSE) console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

const MEASURE = "(function(){"
    + "var st=document.createElement('style');st.textContent='.content-primary{max-width:none!important} .listItem{display:flex;align-items:center;justify-content:center;text-align:center}';document.head.appendChild(st);"
    + "var panel=document.querySelector('.jpf-tab-panel:not([hidden])');if(!panel)return {error:'no open panel'};"
    + "var ROW=/(^|\\s)(listItem|[a-z-]*-row|[a-z-]*-item|[a-z-]*-card)(\\s|$)/;"
    + "function name(e){return (e.id?'#'+e.id:'')+(e.className&&e.className.baseVal===undefined?'.'+String(e.className).trim().split(/\\s+/).join('.'):'');}"
    + "var bad=[],seen=0;"
    + "[].forEach.call(panel.querySelectorAll('button,a.button-link,a.raised,[is=\"emby-button\"]'),function(b){"
    + "var br=b.getBoundingClientRect();if(!br.width||!br.height)return;seen++;"
    + "for(var a=b.parentElement;a&&a!==panel.parentElement;a=a.parentElement){"
    + "var ar=a.getBoundingClientRect();if(!ar.width)continue;var cs=getComputedStyle(a);"
    + "if(cs.overflowX!=='visible'&&cs.overflowX!=='clip'&&a.scrollWidth>a.clientWidth+1&&a!==panel){}"
    + "if(br.right>ar.right+0.5){bad.push({btn:name(b),why:'past the right edge of',of:name(a),by:Math.round(br.right-ar.right)});break;}"
    + "if(ROW.test(a.className)&&br.right>ar.right-parseFloat(cs.paddingRight)-parseFloat(cs.borderRightWidth)+0.5){bad.push({btn:name(b),why:'inside the padding of',of:name(a),by:Math.round(br.right-(ar.right-parseFloat(cs.paddingRight)))});break;}}"
    + "});"
    + "var wide=[];[].forEach.call(panel.querySelectorAll('*'),function(e){if(!ROW.test(e.className)||!e.parentElement)return;"
    + "var r=e.getBoundingClientRect(),p=e.parentElement.getBoundingClientRect();if(!r.width)return;if(r.right>p.right+0.5)wide.push({row:name(e),by:Math.round(r.right-p.right)});});"
    + "var lead=[],gaps=[];var pcs=getComputedStyle(panel);var panelLeft=panel.getBoundingClientRect().left+parseFloat(pcs.paddingLeft)+parseFloat(pcs.borderLeftWidth);"
    + "[].forEach.call(panel.querySelectorAll('button,a.button-link,a.raised,[is=\"emby-button\"]'),function(b){"
    + "var br=b.getBoundingClientRect();if(!br.width||!br.height)return;var p=b.parentElement,cs=getComputedStyle(p);"
    + "if(/center|space|flex-end|end|right/.test(cs.justifyContent)||cs.textAlign==='center'||cs.textAlign==='right'||cs.display==='grid'||p.closest('.jpf-te-preview-actions,.jpf-ann-buttons,[role=\"dialog\"]'))return;"
    + "var prev=b.previousElementSibling;while(prev&&!prev.getBoundingClientRect().width)prev=prev.previousElementSibling;"
    + "if(prev){var pr=prev.getBoundingClientRect();if(pr.bottom>br.top+1&&pr.right<=br.left+0.5)return;}"
    + "var left=p.getBoundingClientRect().left+parseFloat(cs.paddingLeft)+parseFloat(cs.borderLeftWidth);"
    + "lead.push({btn:name(b),off:Math.round((br.left-left)*10)/10});"
    + "if(p.parentElement===panel)lead.push({btn:name(b)+' (its block)',off:Math.round((p.getBoundingClientRect().left-panelLeft)*10)/10});});"
    + "[].forEach.call(panel.querySelectorAll('.jpf-actions,.buttonContainer'),function(row){var k=[].filter.call(row.children,function(e){return e.getBoundingClientRect().width>0;});"
    + "for(var i=1;i<k.length;i++){var a=k[i-1].getBoundingClientRect(),c=k[i].getBoundingClientRect();if(c.top<a.bottom-1&&c.left>=a.right-0.5)gaps.push({row:name(row),gap:Math.round(c.left-a.right)});}});"
    + "var sa=document.getElementById('onboardingShowAgain'),hint=null;if(sa){var n=sa.nextElementSibling;hint=n?(function(r){return {l:r.left,t:r.top};})(n.getBoundingClientRect()):null;sa=sa.getBoundingClientRect();sa={l:sa.left,b:sa.bottom};}"
    + "return {lead:lead,gaps:gaps,showAgain:sa,showAgainHint:hint,seen:seen,bad:bad,wide:wide,sw:document.documentElement.scrollWidth,cw:document.documentElement.clientWidth};})()";

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine: nothing was measured\n\n  0 passed, 0 failed, skipped'); return 0;
    }
    const fopts = fs.existsSync(ICONS) ? { materialIcons: ICONS } : {};
    if (process.argv[2]) fopts.dashboard = process.argv[2];
    const s = await fixture.start(0, fopts);
    const c = await chrome.launch();
    try {
        for (const z of SIZES) {
            for (const tab of TABS) {
                const r = await c.open('http://127.0.0.1:' + s.port + '/dashboard?tab=' + tab + '&device=' + z[4], z[4]);
                if (r.error) { ok(z[0] + ' ' + tab + ': the page draws', false, r.error); continue; }
                await c.page('Emulation.setDeviceMetricsOverride', { width: z[1], height: z[2], deviceScaleFactor: 1, mobile: z[3] });
                await new Promise(r2 => setTimeout(r2, 350));
                const m = await c.evaluate(MEASURE);
                if (m.error) { ok(z[0] + ' ' + tab + ': a panel is open', false, m.error); continue; }
                const tag = z[0] + ' ' + tab;
                ok(tag + ': buttons were measured (' + m.seen + ')', m.seen > 0);
                m.bad.forEach(b => ok(tag + ': ' + b.btn + ' is inside ' + b.of, false, b.why + ' by ' + b.by + 'px'));
                m.wide.forEach(w => ok(tag + ': ' + w.row + ' is no wider than its list', false, 'by ' + w.by + 'px'));
                m.lead.forEach(l => ok(tag + ': ' + l.btn + ' starts at its content edge', Math.abs(l.off) < 0.5, l.off + 'px'));
                m.gaps.forEach(g => ok(tag + ': buttons in ' + g.row + ' sit together', g.gap <= 40, g.gap + 'px apart'));
                if (m.showAgain && m.showAgain.b > 0) {
                    ok(tag + ': "Show again" has its hint under it, at its left edge',
                        m.showAgainHint && Math.abs(m.showAgainHint.l - m.showAgain.l) < 1 && m.showAgainHint.t >= m.showAgain.b - 1, JSON.stringify([m.showAgain, m.showAgainHint]));
                }
                ok(tag + ': the page does not scroll sideways', m.sw <= m.cw + 1, m.sw + ' in ' + m.cw);
                if (!m.bad.length && !m.wide.length) ok(tag + ': every button is inside its row', true);
            }
        }
    } finally { await c.close(); await s.close(); }
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}
main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
