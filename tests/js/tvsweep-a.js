// Remote-control sweep, part A: the picker and the PIN screens, drawn in real Chrome at
// 1920x1080 with layout-tv and driven only with arrows, Enter, Back (461) and Escape.
//
// For each screen, with the real key handler on the real layout:
//   1. focus STARTS on the control named below, and it is visible, on top, and ringed;
//   2. every control the screen draws that a person can see is REACHED by arrow chains
//      from the start (explore: focus each reached control, press each arrow once);
//   3. every control reached is visible, on top of the page and ringed, and no arrow
//      sends focus to the body;
//   4. Back clicks the control the screen names as its way out (or, on a required choice,
//      clicks nothing and leaves focus on something visible); Escape does the same on the
//      full screens.
//
// tvinput.js runs the same questions on a stub DOM with no layout; this is what it cannot
// see (a control covered by another layer, one scrolled off, a ring that is not drawn).
//
// Needs Chrome and the Material Icons font; says it was skipped without them.
//
//     node tests/js/tvsweep-a.js [/path/to/old/profiles.js]

'use strict';

const fs = require('fs');
const path = require('path');
const https = require('https');
const ROOT = path.resolve(__dirname, '..', '..');
const tw = require('./_tvwalk');
const chrome = require('../screens/chrome');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
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

// start: what `at` must match. back: the control Back clicks, or null on a required
// choice. arrows: false where the arrows belong to Jellyfin's own header, not to us.
const SCREENS = [
    { scene: 'picker', start: /^div\.jpf-profile-card/, back: null },
    { scene: 'picker&count=21', start: /^div\.jpf-profile-card/, back: null },
    { scene: 'picker&layout=profiles:list', start: /^div\.jpf-profile-card/, back: null },
    { scene: 'picker&layout=profiles:list,size:large&count=21', start: /^div\.jpf-profile-card/, back: null },
    { scene: 'announcement', start: /^div\.jpf-profile-card/, back: null },
    { scene: 'timerequest', start: /^button\.profiles-time-request/, back: null },
    { scene: 'manage', start: /^div\.jpf-profile-card/, back: null },
    { scene: 'pin', start: /^#jpf-profile-pin-input$/, back: '#pin-cancel-btn' },
    { scene: 'mainpin', start: /^#jpf-main-pin-new$/, back: '#main-pin-skip' },
    { scene: 'masterpin', start: /^#jpf-master-pin-input$/, back: '#master-pin-cancel-btn' },
    { scene: 'pinreset', start: /^#jpf-pin-reset-code$/, back: '#pin-reset-back' },
    { scene: 'switcher', start: /^#switcher-ask-startup$/, back: '#switcher-mode-back-btn' },
    { scene: 'timeup', start: /^button\.profiles-btn/, back: '#actions-cancel-btn' },
    { scene: 'panic', start: /^#profiles-panic-input$/, back: '#profiles-panic-cancel' },
    { scene: 'maintenance', start: /^button\.profiles-btn/, back: null },
    { scene: 'header', start: /^#profiles-floating-bubble$/, arrows: false },
    { scene: 'headerpill', start: /^#profiles-floating-bubble$/, arrows: false }
];

const EXITS = '#profiles-crop-cancel, #profiles-panic-cancel, #dialog-cancel-btn, #dialog-close-btn, #pin-cancel-btn, #master-pin-cancel-btn, #create-cancel-btn, #edit-cancel-btn, #select-cancel-btn, #prompt-cancel-btn, #actions-cancel-btn, #bonfire-back-btn, #switcher-mode-back-btn, #settings-back-btn, #theme-settings-back-btn, #account-back-btn, #self-cancel-btn, #profiles-resume-btn, #profiles-libart-cancel, #onboarding-skip, #main-pin-skip, #pin-reset-back';
const COUNT_CLICKS = '(function(){window.__clicked=[];[].slice.call(document.querySelectorAll(' + JSON.stringify(EXITS) + ')).forEach(function(b){b.addEventListener("click",function(){window.__clicked.push("#"+b.id)},true)});return true})()';

async function main() {
    const given = process.argv[2];
    if (!(await fetchIcons()) || !(() => { try { chrome.findChrome(); return true; } catch (e) { return false; } })()) {
        console.log('  SKIP  needs Chrome and the Material Icons font');
        return;
    }
    const tv = await tw.start({ source: given ? fs.readFileSync(given, 'utf8') : undefined });
    try {
        for (const s of SCREENS) {
            const label = s.scene;
            const e = await tv.explore(s.scene);
            const first = await tv.walk(s.scene, []);
            const f = first[0];
            ok(label + ': focus starts on the first control', s.start.test(f.at), 'at ' + f.at);
            ok(label + ': the start is visible, on top and ringed', f.visible && f.onTop && f.ring, JSON.stringify(f));
            if (s.arrows !== false) {
                ok(label + ': the arrows reach every visible control', e.unreached.length === 0, 'unreached: ' + e.unreached.join(' ; '));
                ok(label + ': no arrow sends focus to the body', !e.edges.some(x => / --> body$/.test(x)), e.edges.filter(x => / --> body$/.test(x)).join(' ; '));
                const bad = [];
                for (const at of e.reached) {
                    const r = await tv.evaluate('(function(){var e=window.__tw.find(' + JSON.stringify(at) + ');if(!e)return null;e.focus();return window.__tw.state()})()');
                    if (r && (!r.ring || !r.visible || !r.onTop)) bad.push(at + (r.ring ? '' : ' no ring') + (r.visible ? '' : ' hidden') + (r.onTop ? '' : ' covered'));
                }
                ok(label + ': every control reached is visible, on top and ringed', bad.length === 0, bad.join(' ; '));
            }
            if (s.back !== undefined) {
                for (const key of ['Back', 'Escape']) {
                    // Escape is the dialogs' own on a dialog, so it is only asked of the gate screens.
                    if (key === 'Escape' && /^(timeup|panic|maintenance)$/.test(s.scene)) continue;
                    await tv.load(s.scene);
                    await tv.evaluate(COUNT_CLICKS);
                    const before = await tv.state();
                    await tv.press(key);
                    const clicked = await tv.evaluate('window.__clicked');
                    const after = await tv.state();
                    if (s.back) {
                        ok(label + ': ' + key + ' clicks ' + s.back, clicked.indexOf(s.back) !== -1, 'clicked ' + JSON.stringify(clicked));
                    } else {
                        ok(label + ': ' + key + ' is absorbed on a required choice, focus stays visible', clicked.length === 0 && after.visible && after.onTop, JSON.stringify({ clicked, before: before.at, after }));
                    }
                }
            }
        }
        // The picker opened on purpose (the header button): Back and Escape close it and
        // put focus back on the button. A profile change must not happen on the way out.
        for (const key of ['Back', 'Escape']) {
            await tv.load('pickeropen');
            const open = await tv.evaluate("!!document.getElementById('profiles-gate-overlay')");
            ok('opened from the header button: the picker is up', open === true);
            await tv.evaluate('window.__switched=0;var P=window.__bonfire;var o=P.switchToProfile;if(o)P.switchToProfile=function(){window.__switched++;return o.apply(P,arguments)};true');
            await tv.press(key);
            const gone = await tv.evaluate("!document.getElementById('profiles-gate-overlay')");
            const at = await tv.evaluate("document.activeElement&&document.activeElement.id");
            const switched = await tv.evaluate('window.__switched');
            ok('opened from the header button: ' + key + ' closes the picker', gone === true);
            ok('opened from the header button: after ' + key + ' focus is on the header button', at === 'profiles-floating-bubble', 'focus on ' + at);
            ok('opened from the header button: ' + key + ' switches nothing', switched === 0);
        }
    } finally { await tv.close(); }
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    if (fails.length) process.exitCode = 1;
}

main().catch(e => { console.error(e.stack || e); process.exit(1); });
