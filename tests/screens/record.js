/*
 * NOT A HARNESS. Records the decoration videos in docs/themes/: each decoration in
 * docs/themes.md running in the web client, for anyone building it elsewhere to check
 * their version against.
 *
 *   node tests/screens/record.js                  all three
 *   node tests/screens/record.js pattern          one
 *
 * Frames come from Chrome's screencast and are encoded to WebM by Chrome itself
 * (MediaRecorder on a canvas, the frames drawn at the times they were taken), so this
 * needs nothing but Chrome. A frame only arrives when the page changes, which is what a
 * still screen should cost.
 */
'use strict';

const fs = require('fs');
const path = require('path');
const fixture = require('./fixture');
const chrome = require('./chrome');

const ROOT = path.resolve(__dirname, '..', '..');
const OUT = path.join(ROOT, 'docs', 'themes');
const ICONS = path.join(__dirname, '.cache', 'material-icons.woff2');

// What each video shows. `act` runs in the page once the scene is ready, and may return a
// promise; the recording runs for `seconds` from the moment the scene is ready.
const VIDEOS = {
    // Snowfall: the pattern coming down, one tile a loop.
    pattern: { theme: 'snowfall', scene: 'picker', device: 'tablet', seconds: 10 },
    // Nightfall: each profile its own ring, and focus moving along them.
    rings: {
        theme: 'nightfall', scene: 'picker', device: 'tv', seconds: 6,
        act: 'var cards=[].slice.call(document.querySelectorAll(".jpf-profile-card"));'
            + 'cards.forEach(function(c,i){setTimeout(function(){c.focus();},700+i*1100);});'
    },
    // Glass: the settings panel opening over the gate, blurring what is behind it, and the
    // frosted second-kind buttons.
    frost: {
        theme: 'glass', scene: 'picker', device: 'tablet', seconds: 4,
        act: 'setTimeout(function(){window.__bonfire.showSettingsMenu();},1200);'
    }
};

const ENCODE = function (frames, fps) {
    return new Promise(function (resolve, reject) {
        var canvas = document.createElement('canvas');
        var first = new Image();
        first.onload = function () {
            canvas.width = first.width; canvas.height = first.height;
            var ctx = canvas.getContext('2d');
            ctx.drawImage(first, 0, 0);
            var stream = canvas.captureStream(fps);
            var rec = new MediaRecorder(stream, { mimeType: 'video/webm;codecs=vp9', videoBitsPerSecond: 1500000 });
            var parts = [];
            rec.ondataavailable = function (e) { if (e.data.size) parts.push(e.data); };
            rec.onstop = function () {
                var r = new FileReader();
                r.onload = function () { resolve(r.result.split(',')[1]); };
                r.readAsDataURL(new Blob(parts, { type: 'video/webm' }));
            };
            rec.start();
            var t0 = frames[0].t;
            var images = frames.map(function (f) { var i = new Image(); i.src = f.src; return i; });
            Promise.all(images.map(function (i) { return i.decode(); })).then(function () {
                var start = performance.now();
                var n = 1;
                (function next() {
                    var now = (performance.now() - start) / 1000;
                    while (n < frames.length && frames[n].t - t0 <= now) { ctx.drawImage(images[n], 0, 0, canvas.width, canvas.height); n++; }
                    if (now >= frames[frames.length - 1].t - t0 + 0.5) { rec.stop(); return; }
                    requestAnimationFrame(next);
                })();
            }, reject);
        };
        first.onerror = reject;
        first.src = frames[0].src;
    });
}.toString();

async function record(c, s, name, v) {
    const url = 'http://127.0.0.1:' + s.port + '/?theme=' + v.theme + '&scene=' + v.scene + '&device=' + v.device;
    const r = await c.open(url, v.device);
    if (r.error) throw new Error(name + ': ' + r.error);
    const frames = [];
    const onFrame = msg => {
        if (msg.method !== 'Page.screencastFrame' || msg.sessionId !== c.sessionId) return;
        frames.push({ src: 'data:image/jpeg;base64,' + msg.params.data, t: msg.params.metadata.timestamp });
        c.page('Page.screencastFrameAck', { sessionId: msg.params.sessionId }).catch(() => {});
    };
    c.listeners.push(onFrame);
    const size = chrome.DEVICES[v.device];
    await c.page('Page.startScreencast', { format: 'jpeg', quality: 88, everyNthFrame: 1, maxWidth: size.width * size.scale, maxHeight: size.height * size.scale });
    if (v.act) await c.evaluate(v.act);
    await new Promise(res => setTimeout(res, v.seconds * 1000));
    await c.page('Page.stopScreencast');
    c.listeners.splice(c.listeners.indexOf(onFrame), 1);
    if (frames.length < 2) throw new Error(name + ': only ' + frames.length + ' frames');
    // A last frame at the end, so a still ending is held for the full length.
    frames.push({ src: frames[frames.length - 1].src, t: frames[0].t + v.seconds });
    await c.page('Page.navigate', { url: 'http://127.0.0.1:' + s.port + '/blank' });
    await new Promise(res => setTimeout(res, 300));
    const b64 = await c.evaluate('(' + ENCODE + ')(' + JSON.stringify(frames) + ', 30)');
    fs.mkdirSync(OUT, { recursive: true });
    const file = path.join(OUT, name + '.webm');
    fs.writeFileSync(file, Buffer.from(b64, 'base64'));
    console.log(name + ': ' + frames.length + ' frames, ' + Math.round(fs.statSync(file).size / 1024) + ' KB -> ' + path.relative(ROOT, file));
}

async function main() {
    const only = process.argv[2];
    const s = await fixture.start(0, { materialIcons: ICONS });
    const c = await chrome.launch();
    try {
        for (const name of Object.keys(VIDEOS)) {
            if (only && only !== name) continue;
            await record(c, s, name, VIDEOS[name]);
        }
    } finally {
        await c.close();
        await s.close();
    }
}

main().catch(e => { console.error(e.stack || e); process.exit(1); });
