/*
 * NOT A HARNESS. Writes the golden screenshots: every built-in theme, every scene in
 * scenes.js, at phone, tablet and TV size, into docs/themes/screens/<theme>/.
 *
 *   node tests/screens/shoot.js                        all of them
 *   node tests/screens/shoot.js --theme glass --scene picker,pin --device tv
 *   node tests/screens/shoot.js --out <dir>            somewhere else, for diff.js
 *   node tests/screens/shoot.js --count 20 --out <dir> a household of 20 profiles
 *
 * They are the reference for anyone drawing these screens outside the web client, and
 * the thing diff.js compares against before a release. Not a gate: see fixture.js.
 *
 * "tv" is a television as the client treats one: jpf-tv on <html>, so nothing drifts. A
 * theme with frost also gets "tv-lowpower", drawn as a browser with no backdrop-filter
 * (webOS 4.5 and 5) sees it, because docs/themes.md says every theme must still look
 * right that way.
 *
 * Needs Chrome (or Edge; set CHROME to its path) and the Material Icons font, which
 * jellyfin-web ships and this repository does not: it is fetched once into .cache/.
 */
'use strict';

const fs = require('fs');
const path = require('path');
const https = require('https');
const fixture = require('./fixture');
const chrome = require('./chrome');

const ROOT = path.resolve(__dirname, '..', '..');
const ICONS = path.join(__dirname, '.cache', 'material-icons.woff2');
// The icon font jellyfin-web 10.11 uses (material-icons, Apache 2.0), pinned.
const ICONS_URL = 'https://cdn.jsdelivr.net/npm/material-icons@1.13.12/iconfont/material-icons.woff2';

function args() {
    const a = { out: path.join(ROOT, 'docs', 'themes', 'screens') };
    const v = process.argv.slice(2);
    for (let i = 0; i < v.length; i += 2) a[v[i].replace(/^--/, '')] = v[i + 1];
    return a;
}

function fetchOnce(url, file) {
    if (fs.existsSync(file)) return Promise.resolve();
    fs.mkdirSync(path.dirname(file), { recursive: true });
    return new Promise((resolve, reject) => https.get(url, res => {
        if (res.statusCode !== 200) return reject(new Error(url + ' answered ' + res.statusCode));
        const chunks = [];
        res.on('data', c => chunks.push(c));
        res.on('end', () => { fs.writeFileSync(file, Buffer.concat(chunks)); resolve(); });
    }).on('error', reject));
}

function themeFiles() {
    const dir = path.join(ROOT, 'Web', 'themes');
    return fs.readdirSync(dir).filter(f => /\.json$/.test(f) && f !== 'fonts.json')
        .map(f => JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')))
        .sort((x, y) => x.id < y.id ? -1 : 1);
}

const NO_BLUR = '*{-webkit-backdrop-filter:none!important;backdrop-filter:none!important}';

async function main() {
    const a = args();
    await fetchOnce(ICONS_URL, ICONS);
    const themes = themeFiles().filter(t => !a.theme || a.theme.split(',').indexOf(t.id) !== -1);
    const s = await fixture.start(0, { materialIcons: ICONS });
    const c = await chrome.launch();
    let problems = 0;
    let count = 0;
    try {
        // The scene list comes from scenes.js itself, so a new scene is shot without
        // being registered here too.
        await c.open('http://127.0.0.1:' + s.port + '/?scene=picker', 'tablet');
        const all = await c.evaluate('window.__scenes');
        const review = await c.evaluate('window.__reviewScenes || []');
        const scenes = !a.scene ? all : a.scene === 'review' ? review : a.scene === 'all' ? all.concat(review) : a.scene.split(',');
        for (const theme of themes) {
            const frost = (theme.decorations || []).some(d => d && d.name === 'frost');
            const devices = (a.device ? a.device.split(',') : ['phone', 'tablet', 'tv'].concat(frost ? ['tv-lowpower'] : []));
            const dir = path.join(a.out, theme.id);
            fs.mkdirSync(dir, { recursive: true });
            for (const scene of scenes) {
                for (const device of devices) {
                    const size = device === 'tv-lowpower' ? 'tv' : device;
                    const url = 'http://127.0.0.1:' + s.port + '/?theme=' + theme.id + '&scene=' + scene + '&device=' + size + (a.count ? '&count=' + a.count : '');
                    const r = await c.open(url, size);
                    if (device === 'tv-lowpower') {
                        await c.evaluate('(function(){var s=document.createElement("style");s.textContent=' + JSON.stringify(NO_BLUR) + ';document.head.appendChild(s);return new Promise(function(r){requestAnimationFrame(function(){requestAnimationFrame(r)})})})()');
                    }
                    // The same picture every run: anything that loops (a drifting pattern)
                    // is held at its first frame, and any entrance is run to its end.
                    await c.evaluate('document.getAnimations().forEach(function(a){var t=a.effect&&a.effect.getComputedTiming();if(t&&t.iterations===Infinity){a.pause();a.currentTime=0;}else{try{a.finish();}catch(e){}}})');
                    // --full yes: a screen taller than the device is shot whole, by growing the
                    // window to fit whatever scrolls. For reading a long form, not a golden:
                    // anything sized in vh grows with it.
                    if (a.full === 'yes') {
                        const extra = await c.evaluate('Math.max.apply(null,[0].concat([].slice.call(document.querySelectorAll("*")).filter(function(e){var o=getComputedStyle(e).overflowY;return (o==="auto"||o==="scroll")&&e.scrollHeight>e.clientHeight+1;}).map(function(e){return e.scrollHeight-e.clientHeight;})))');
                        if (extra > 0) {
                            const d = chrome.DEVICES[size];
                            await c.page('Emulation.setDeviceMetricsOverride', { width: d.width, height: Math.min(d.height + extra, 8000), deviceScaleFactor: d.scale, mobile: d.mobile });
                            await c.evaluate('new Promise(function(r){requestAnimationFrame(function(){requestAnimationFrame(r)})})');
                        }
                    }
                    const said = [].concat(r.error ? ['error: ' + r.error] : [], r.misses.map(m => 'unanswered: ' + m), r.console);
                    if (said.length) { problems++; console.log('  ' + theme.id + ' ' + scene + ' ' + device + '\n    ' + said.join('\n    ')); }
                    fs.writeFileSync(path.join(dir, scene + '-' + device + '.webp'), await c.shot('webp', 82));
                    count++;
                }
            }
            console.log(theme.id + ': ' + scenes.length * devices.length + ' screens');
        }
    } finally {
        await c.close();
        await s.close();
    }
    console.log(count + ' screens written to ' + path.relative(ROOT, a.out) + (problems ? ', ' + problems + ' with problems above' : ''));
    process.exitCode = problems ? 1 : 0;
}

main().catch(e => { console.error(e.stack || e); process.exit(1); });
