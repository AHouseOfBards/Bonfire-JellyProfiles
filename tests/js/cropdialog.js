// The picture-position dialog: a pinch that ends with one finger down, and a dialog that
// outlives the gate it was opened from (R-1204, R-1218).
//
//   node tests/js/cropdialog.js [path/to/profiles.js]

'use strict';
const L = require('./_lib');
const D = require('./_stubdom');

const SRC = L.readProfiles();

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const env = D.loadPlugin(SRC);
const PP = env.PP;

// R-1204. Pinch with two fingers, lift one, keep dragging with the other. The finger left
// down was last measured while it was part of the pinch, so the first move after the lift
// must be measured from where it is now and not from where the drag began.
{
    ok('the gesture tracker exists', typeof PP._cropGesture === 'function');
    if (typeof PP._cropGesture === 'function') {
        const pans = [];
        let zoom = 1;
        const g = PP._cropGesture(() => zoom, (dx, dy) => pans.push([dx, dy]), z => { zoom = z; }, () => {});

        g.begin('a', 100, 100);
        g.begin('b', 200, 100);         // pinch starts, 100 apart
        g.move('b', 300, 100);          // 200 apart: zoom doubles
        g.move('a', 50, 100);           // 250 apart
        ok('a pinch zooms and does not pan', zoom > 1 && pans.length === 0, JSON.stringify({ zoom: zoom, pans: pans }));

        g.end('b');                     // one finger left, at 50,100
        g.move('a', 55, 100);
        ok('the first move after a finger lifts is measured from where the finger is',
            pans.length === 1 && pans[0][0] === 5 && pans[0][1] === 0, JSON.stringify(pans));

        g.end('a');
        g.begin('c', 10, 10);
        g.move('c', 12, 13);
        ok('a fresh drag after letting go starts from its own touch-down',
            pans.length === 2 && pans[1][0] === 2 && pans[1][1] === 3, JSON.stringify(pans));

        // A third finger neither pans nor zooms from a stale baseline.
        const before = pans.length;
        g.begin('d', 40, 40);
        g.begin('e', 80, 40);
        g.move('c', 20, 20);
        ok('with two or more fingers down the picture does not pan', pans.length === before, JSON.stringify(pans));
    }
}

// R-1218. The dialog sits on the body, not in the gate, so tearing the gate down (an idle
// lock, a signed-out session) must take it too, and Escape must close it.
{
    let closed = 0;
    PP._cropClose = () => { closed++; };
    PP.removeProfileOverlay();
    ok('closing the gate closes a crop dialog left open', closed === 1, String(closed));

    const from = SRC.indexOf('showCropDialog: function');
    const body = from < 0 ? '' : SRC.slice(from, SRC.indexOf('fetchAvatarLibrary: function', from));
    ok('the dialog registers its closer', /this\._cropClose\s*=\s*close\b/.test(body));
    ok('and forgets it once closed', /this\._cropClose\s*=\s*null/.test(body));
    ok('Escape closes the dialog', /dialog\.addEventListener\('keydown'[\s\S]{0,200}'Escape'[\s\S]{0,120}close\(\)/.test(body));
}

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
