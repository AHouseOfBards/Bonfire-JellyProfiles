/*
 * NOT A HARNESS. Which golden screenshots a change moved.
 *
 *   node tests/screens/diff.js                 shoot everything afresh and compare
 *   node tests/screens/diff.js <dir>           compare an existing shoot.js --out <dir>
 *
 * Run by hand before a release, and on purpose after any change to the stylesheet, the
 * theme files or a screen. It lists every screen that moved and by how much; it does not
 * pass or fail, because a pixel can move between two Chrome versions with nothing changed.
 * A screen that moved because you meant it to: run shoot.js and commit the new pictures
 * with the change, so the goldens and the code never disagree in the history.
 *
 * The comparison runs in Chrome, which decodes WebP; Node does not without a library.
 * A pixel counts as moved when any channel differs by more than 24 of 255, which is past
 * what WebP's own loss does to an unchanged picture.
 */
'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync } = require('child_process');
const chrome = require('./chrome');

const ROOT = path.resolve(__dirname, '..', '..');
const GOLD = path.join(ROOT, 'docs', 'themes', 'screens');

function list(dir) {
    const out = [];
    (function walk(d) {
        fs.readdirSync(d, { withFileTypes: true }).forEach(e => {
            const p = path.join(d, e.name);
            if (e.isDirectory()) walk(p);
            else if (/\.webp$/.test(e.name)) out.push(path.relative(dir, p));
        });
    })(dir);
    return out.sort();
}

const COMPARE = function (a, b) {
    function load(src) {
        return new Promise(function (res, rej) { var i = new Image(); i.onload = function () { res(i); }; i.onerror = rej; i.src = src; });
    }
    return Promise.all([load(a), load(b)]).then(function (imgs) {
        if (imgs[0].width !== imgs[1].width || imgs[0].height !== imgs[1].height) {
            return { size: imgs[0].width + 'x' + imgs[0].height + ' -> ' + imgs[1].width + 'x' + imgs[1].height };
        }
        var w = imgs[0].width, h = imgs[0].height;
        var data = imgs.map(function (img) {
            var c = document.createElement('canvas'); c.width = w; c.height = h;
            var x = c.getContext('2d'); x.drawImage(img, 0, 0);
            return x.getImageData(0, 0, w, h).data;
        });
        var moved = 0;
        for (var i = 0; i < data[0].length; i += 4) {
            if (Math.abs(data[0][i] - data[1][i]) > 24 || Math.abs(data[0][i + 1] - data[1][i + 1]) > 24
                || Math.abs(data[0][i + 2] - data[1][i + 2]) > 24) moved++;
        }
        return { moved: moved, of: w * h };
    });
}.toString();

async function main() {
    let fresh = process.argv[2];
    if (!fresh) {
        fresh = fs.mkdtempSync(path.join(os.tmpdir(), 'bonfire-screens-'));
        execFileSync(process.execPath, [path.join(__dirname, 'shoot.js'), '--out', fresh], { stdio: 'inherit' });
    }
    const old = list(GOLD);
    const now = list(fresh);
    const gone = old.filter(f => now.indexOf(f) === -1);
    const added = now.filter(f => old.indexOf(f) === -1);
    const c = await chrome.launch();
    const moved = [];
    try {
        for (const f of old.filter(x => now.indexOf(x) !== -1)) {
            const a = 'data:image/webp;base64,' + fs.readFileSync(path.join(GOLD, f)).toString('base64');
            const b = 'data:image/webp;base64,' + fs.readFileSync(path.join(fresh, f)).toString('base64');
            const r = await c.evaluate('(' + COMPARE + ')(' + JSON.stringify(a) + ',' + JSON.stringify(b) + ')');
            if (r.size || r.moved > 0) moved.push({ f, r });
        }
    } finally {
        await c.close();
    }
    moved.sort((x, y) => (y.r.moved || Infinity) - (x.r.moved || Infinity));
    moved.forEach(m => console.log('  moved  ' + m.f + '  ' + (m.r.size || (m.r.moved + ' px, ' + (100 * m.r.moved / m.r.of).toFixed(2) + '%'))));
    gone.forEach(f => console.log('  gone   ' + f));
    added.forEach(f => console.log('  new    ' + f));
    console.log(moved.length + ' moved, ' + gone.length + ' gone, ' + added.length + ' new, of ' + old.length + ' goldens. Fresh shots: ' + fresh);
}

main().catch(e => { console.error(e.stack || e); process.exit(1); });
