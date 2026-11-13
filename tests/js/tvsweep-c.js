// Remote-control sweep, part C: Settings, the theme picker, onboarding and the layout choice,
// drawn in real Chrome at 1920x1080 with layout-tv and driven only with arrows, Enter,
// Back (461) and Escape. Same questions as tvsweep-a.js: where focus starts, whether the
// arrows reach every visible control, whether every control reached is visible, on top and
// ringed, and what Back does. Settings is also opened the way a person does it: Enter on
// its entries, then Back to return.
//
// The trusted-address PIN prompts, and the Settings pages for preferences, time zones and
// ratings, are not client screens: they live on the server (the prompts are the ordinary
// PIN screens, which tvsweep-a.js walks), so there is nothing here to draw.
//
// Needs Chrome and the Material Icons font; says it was skipped without them.
//
//     node tests/js/tvsweep-c.js [/path/to/old/profiles.js]

'use strict';

const fs = require('fs');
const path = require('path');
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

// back: the control Back clicks, or null on a required choice.
const SCREENS = [
    { scene: 'settings', start: /^#settings-account$/, back: '#settings-back-btn' },
    { scene: 'themes', start: /^button\.jpf-theme-option/, back: '#theme-settings-back-btn' },
    // The welcome has no Skip, so Back is absorbed there. The other steps start on Next on
    // purpose: it is the one control every step has.
    { scene: 'ob-welcome', start: /^#onboarding-next$/, back: null },
    { scene: 'ob-mainpin', start: /^#jpf-main-pin-new$/, back: '#main-pin-skip' },
    { scene: 'ob-email', start: /^#onboarding-next$/, back: '#onboarding-skip' },
    { scene: 'ob-profile', start: /^#onboarding-next$/, back: '#onboarding-skip' },
    { scene: 'ob-switcher', start: /^#onboarding-next$/, back: '#onboarding-skip' },
    { scene: 'ob-theme', start: /^#onboarding-next$/, back: '#onboarding-skip' }
];

const EXITS = '#theme-settings-back-btn, #settings-back-btn, #account-back-btn, #onboarding-skip, #main-pin-skip, #bonfire-back-btn, #switcher-mode-back-btn';
const COUNT_CLICKS = '(function(){window.__clicked=[];[].slice.call(document.querySelectorAll(' + JSON.stringify(EXITS) + ')).forEach(function(b){b.addEventListener("click",function(){window.__clicked.push("#"+b.id)},true)});return true})()';

async function main() {
    const given = process.argv[2];
    if (!fs.existsSync(ICONS) || !(() => { try { chrome.findChrome(); return true; } catch (e) { return false; } })()) {
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
            ok(label + ': the arrows reach every visible control', e.unreached.length === 0, 'unreached: ' + e.unreached.join(' ; '));
            ok(label + ': no arrow sends focus to the body', !e.edges.some(x => / --> body$/.test(x)), e.edges.filter(x => / --> body$/.test(x)).join(' ; '));
            const bad = [];
            for (const at of e.reached) {
                const r = await tv.evaluate('(function(){var e=window.__tw.find(' + JSON.stringify(at) + ');if(!e)return null;e.focus();return window.__tw.state()})()');
                if (r && (!r.ring || !r.visible || !r.onTop)) bad.push(at + (r.ring ? '' : ' no ring') + (r.visible ? '' : ' hidden') + (r.onTop ? '' : ' covered'));
            }
            ok(label + ': every control reached is visible, on top and ringed', bad.length === 0, bad.join(' ; '));
            for (const key of ['Back', 'Escape']) {
                await tv.load(s.scene);
                await tv.evaluate(COUNT_CLICKS);
                const before = await tv.state();
                await tv.press(key);
                const clicked = await tv.evaluate('window.__clicked');
                const after = await tv.state();
                if (s.back) {
                    ok(label + ': ' + key + ' clicks ' + s.back, clicked.indexOf(s.back) !== -1, 'clicked ' + JSON.stringify(clicked) + ' from ' + before.at + ' to ' + after.at);
                } else {
                    ok(label + ': ' + key + ' is absorbed on a required choice, focus stays visible', clicked.length === 0 && after.visible && after.onTop, JSON.stringify({ clicked, before: before.at, after }));
                }
            }
        }
        // Your account is a tall form. Down from the top must get to Back at the bottom, and
        // through the last More to the layout choice, as a person would: Down to the
        // summary, Enter, Down. A select that keeps Up and Down for its value strands focus
        // there, so everything below it is out of reach.
        {
            const keys = [];
            for (let i = 0; i < 14; i++) keys.push('ArrowDown');
            const at = (await tv.walk('account', keys)).map(x => x.at);
            ok('account: Down from the top reaches Back', at.indexOf('#account-back-btn') !== -1, at.join(' > '));
            let sum = -1;
            at.forEach((a, i) => { if (/^summary/.test(a) && i > sum) sum = i; });
            ok('account: Down reaches the last More summary', sum !== -1);
            if (sum !== -1) {
                const k2 = keys.slice(0, sum).concat(['Enter'], keys.slice(0, 5));
                const s2 = await tv.walk('account', k2);
                const sel = s2.findIndex(x => x.at === '#account-layout-select');
                ok('account: after Enter on the last More, Down reaches the layout choice', sel !== -1, s2.map(x => x.at).join(' > '));
                if (sel !== -1) ok('account: the layout choice is visible, on top and ringed', s2[sel].visible && s2[sel].onTop && s2[sel].ring, JSON.stringify(s2[sel]));
            }
        }
    } finally { await tv.close(); }
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    if (fails.length) process.exitCode = 1;
}

main().catch(e => { console.error(e.stack || e); process.exit(1); });
