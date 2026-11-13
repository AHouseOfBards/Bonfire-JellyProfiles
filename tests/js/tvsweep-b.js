// Remote-control sweep, part B: the profile forms, the crop dialog, the artwork picker,
// Sharing, the account screen and a profile's own form, drawn in real Chrome at 1920x1080
// with layout-tv and driven only with arrows, Enter, Back (461) and Escape.
//
// Same questions as tvsweep-a.js, per screen:
//   1. focus STARTS where named, visible, on top and ringed;
//   2. the arrows reach every visible control (a checkbox inside a check label is
//      reached through its label, which is the stop);
//   3. every control reached is visible, on top and ringed; no arrow sends focus to body;
//      no control is a dead end on all four arrows;
//   4. Back and Escape click the control the screen names as its way out.
//
// The crop dialog is walked with keys instead of explored: its view spends the arrows on
// panning, so the way on is OK, which a focus-and-press explorer does not model.
//
// Needs Chrome and the Material Icons font; says it was skipped without them.
//
//     node tests/js/tvsweep-b.js [/path/to/old/profiles.js]

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

// start: what `at` must match. back: the control Back and Escape click.
// explore: the scene to run the arrow walk on, where it differs (More opened).
const SCREENS = [
    { scene: 'add', explore: 'add-more', start: /^#create-name-input$/, back: '#create-cancel-btn' },
    { scene: 'edit', explore: 'edit-more', start: /^#edit-name-input$/, back: '#edit-cancel-btn' },
    { scene: 'sharing', start: /^#links-username$/, back: '#bonfire-back-btn' },
    { scene: 'account', start: /^#jpf-account-pin-input$/, back: '#account-back-btn' },
    { scene: 'own', start: /^#self-name-input$/, back: '#self-cancel-btn' },
    { scene: 'libart', start: /^#libart-change-picture$/, back: '#profiles-libart-cancel', escape: true }
];

const EXITS = '#profiles-crop-cancel, #profiles-libart-cancel, #create-cancel-btn, #edit-cancel-btn, #bonfire-back-btn, #account-back-btn, #self-cancel-btn';
const COUNT_CLICKS = '(function(){window.__clicked=[];[].slice.call(document.querySelectorAll(' + JSON.stringify(EXITS) + ')).forEach(function(b){b.addEventListener("click",function(){window.__clicked.push("#"+b.id)},true)});return true})()';

// The stop for a check box is its label, so a box is reached when its label is.
const LABEL_OF = '(function(at){var e=window.__tw.find(at);if(!e)return null;var l=e.closest&&e.closest("label.jpf-library-check-label");return l?window.__tw.describe(l):null})';

async function main() {
    const given = process.argv[2];
    const icons = path.join(ROOT, 'tests', 'screens', '.cache', 'material-icons.woff2');
    if (!fs.existsSync(icons) || !(() => { try { chrome.findChrome(); return true; } catch (e) { return false; } })()) {
        console.log('  SKIP  needs Chrome and the Material Icons font');
        return;
    }
    const tv = await tw.start({ source: given ? fs.readFileSync(given, 'utf8') : undefined });
    try {
        for (const s of SCREENS) {
            const label = s.scene;
            const first = (await tv.walk(s.scene, []))[0];
            ok(label + ': focus starts on the first control', s.start.test(first.at), 'at ' + first.at);
            ok(label + ': the start is visible, on top and ringed', first.visible && first.onTop && first.ring, JSON.stringify(first));

            const e = await tv.explore(s.explore || s.scene);
            const lost = [];
            for (const at of e.unreached) {
                const lab = await tv.evaluate(LABEL_OF + '(' + JSON.stringify(at) + ')');
                if (!lab || e.reached.indexOf(lab) === -1) lost.push(at);
            }
            ok(label + ': the arrows reach every visible control', lost.length === 0, 'unreached: ' + lost.join(' ; '));
            ok(label + ': no arrow sends focus to the body', !e.edges.some(x => / --> body$/.test(x)), e.edges.filter(x => / --> body$/.test(x)).join(' ; '));
            const dead = e.reached.filter(at => at !== 'body' && e.edges.filter(x => x.indexOf(at + ' --') === 0).length > 0
                && e.edges.filter(x => x.indexOf(at + ' --') === 0).every(x => x.split('--> ')[1] === at));
            ok(label + ': no control is a dead end on all four arrows', dead.length === 0, dead.join(' ; '));
            const bad = [];
            for (const at of e.reached) {
                const r = await tv.evaluate('(function(){var e=window.__tw.find(' + JSON.stringify(at) + ');if(!e)return null;window.__bonfire._focusVisibly(e);return window.__tw.state()})()');
                if (r && (!r.ring || !r.visible || !r.onTop)) bad.push(at + (r.ring ? '' : ' no ring') + (r.visible ? '' : ' hidden') + (r.onTop ? '' : ' covered'));
            }
            ok(label + ': every control reached is visible, on top and ringed', bad.length === 0, bad.join(' ; '));

            for (const key of ['Back', 'Escape']) {
                await tv.load(s.scene);
                await tv.evaluate(COUNT_CLICKS);
                await tv.press(key);
                const clicked = await tv.evaluate('window.__clicked');
                // A dialog's own Escape closes it without a click; the dialog going is the exit.
                const gone = s.escape && key === 'Escape' && await tv.evaluate('!document.getElementById("profiles-libart-dialog")');
                ok(label + ': ' + key + ' leaves by ' + s.back, clicked.indexOf(s.back) !== -1 || gone, 'clicked ' + JSON.stringify(clicked));
            }
        }

        // The More section opens with OK and focus stays on something drawn.
        for (const scene of ['add', 'edit']) {
            await tv.load(scene);
            await tv.evaluate('document.querySelector("summary.jpf-more-summary").focus()');
            await tv.press('Enter');
            const st = await tv.state();
            const open = await tv.evaluate('document.querySelector("details.jpf-more").open');
            ok(scene + ': OK on More opens it', open === true, 'open ' + open);
            ok(scene + ': focus stays visible after opening More', st.visible && st.onTop && st.ring, JSON.stringify(st));
        }

        // The sticky Save bar comes after every field. Focus each other control the way an
        // arrow would (scrolled clear of the bar) and press Down: it may not land on the
        // bar while a field lies below it, which the bar being nearer used to do. Then Down
        // from the last field reaches the bar, and Up from the bar goes back to it.
        const PICK = '(function(i){var b=window.__bonfire,root=b._activeTrapSurface();var bar=root.querySelector(".jpf-profile-dialog-actions");' +
            'var c=b._overlayFocusables(root).filter(function(e){return !bar.contains(e)});' +
            'function cy(e){var r=e.getBoundingClientRect();return r.top+r.height/2}' +
            'var L=c[0];c.forEach(function(e){if(cy(e)>cy(L))L=e});' +
            'var src=c.filter(function(e){return e!==L&&e.tagName!=="SELECT"});' +
            'if(i>=src.length)return{done:true,last:window.__tw.describe(L)};' +
            'var P=src[i];P.focus();' +
            'return{from:window.__tw.describe(P),usable:document.activeElement===P&&cy(L)>cy(P)+1,last:window.__tw.describe(L)}})';
        for (const scene of ['add-more', 'edit-more']) {
            await tv.load(scene);
            const wrong = [];
            let tried = 0, last = null;
            for (let i = 0; ; i++) {
                const s = await tv.evaluate(PICK + '(' + i + ')');
                last = s.last;
                if (s.done) break;
                if (!s.usable) continue;
                tried++;
                await tv.press('ArrowDown');
                if (await tv.evaluate('!!document.activeElement.closest(".jpf-profile-dialog-actions")')) wrong.push(s.from);
            }
            ok(scene + ': Down from a field lands on the Save bar only after the last field', tried > 5 && wrong.length === 0,
                tried + ' tried; to the bar from: ' + wrong.join(' ; '));
            await tv.evaluate('window.__bonfire._focusVisibly(window.__tw.find(' + JSON.stringify(last) + '))');
            await tv.press('ArrowDown');
            const onBar = await tv.evaluate('!!document.activeElement.closest(".jpf-profile-dialog-actions")');
            ok(scene + ': Down from the last field reaches the Save bar', onBar === true, 'at ' + (await tv.state()).at);
            await tv.press('ArrowUp');
            const at = (await tv.state()).at;
            ok(scene + ': Up from the Save bar returns to the last field', at === last, 'got ' + at + ', wanted ' + last);
        }

        // The crop dialog: the view takes the arrows, OK moves on to Use, the buttons are
        // reachable, and Back and Escape leave.
        {
            const steps = await tv.walk('crop', ['Enter', 'ArrowUp', 'ArrowLeft', 'ArrowRight', 'ArrowDown', 'ArrowRight', 'ArrowUp', 'ArrowUp']);
            ok('crop: focus starts on the picture', /^#profiles-crop-view$/.test(steps[0].at) && steps[0].visible && steps[0].ring, JSON.stringify(steps[0]));
            const names = steps.map(x => x.at);
            ok('crop: OK on the picture moves to Use picture', names[1] === '#profiles-crop-save', names.join(' > '));
            ok('crop: the zoom slider is reachable', names.indexOf('#profiles-crop-zoom') !== -1, names.join(' > '));
            ok('crop: Cancel is reachable', names.indexOf('#profiles-crop-cancel') !== -1, names.join(' > '));
            ok('crop: the picture can be got back to', names.slice(2).indexOf('#profiles-crop-view') !== -1, names.join(' > '));
            const stuck = steps.filter(x => x.key && (!x.visible || !x.onTop || !x.ring || x.at === 'body'));
            ok('crop: focus is always visible, on top and ringed', stuck.length === 0, JSON.stringify(stuck.map(x => [x.key, x.at, x.visible, x.onTop, x.ring])));
            for (const key of ['Back', 'Escape']) {
                await tv.load('crop');
                await tv.evaluate(COUNT_CLICKS);
                await tv.press(key);
                const gone = await tv.evaluate('!document.getElementById("profiles-crop-dialog")');
                const clicked = await tv.evaluate('window.__clicked');
                ok('crop: ' + key + ' closes the dialog', gone || clicked.indexOf('#profiles-crop-cancel') !== -1, 'gone ' + gone + ' clicked ' + JSON.stringify(clicked));
            }
        }
    } finally { await tv.close(); }
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    if (fails.length) process.exitCode = 1;
}

main().catch(e => { console.error(e.stack || e); process.exit(1); });
