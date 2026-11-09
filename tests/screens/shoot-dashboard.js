/*
 * NOT A HARNESS. Dashboard → Bonfire, one picture per tab, each tab shot whole (the window
 * grows to the page's height). For reviewing layout, density and words; not a golden,
 * because jellyfin-web's own controls are only approximated (see dashboard.js).
 *
 *   node tests/screens/shoot-dashboard.js --out <dir> [--tab look] [--device desktop,phone]
 */
'use strict';

const fs = require('fs');
const path = require('path');
const fixture = require('./fixture');
const chrome = require('./chrome');

const ICONS = path.join(__dirname, '.cache', 'material-icons.woff2');

function args() {
    const a = {};
    const v = process.argv.slice(2);
    for (let i = 0; i < v.length; i += 2) a[v[i].replace(/^--/, '')] = v[i + 1];
    if (!a.out) throw new Error('--out <dir> is required; these are not goldens');
    return a;
}

async function main() {
    const a = args();
    const s = await fixture.start(0, { materialIcons: ICONS });
    const c = await chrome.launch();
    let problems = 0;
    try {
        await c.open('http://127.0.0.1:' + s.port + '/dashboard?tab=status', 'desktop');
        const tabs = a.tab ? a.tab.split(',') : await c.evaluate('window.__tabs');
        const devices = a.device ? a.device.split(',') : ['desktop', 'tablet', 'phone'];
        fs.mkdirSync(a.out, { recursive: true });
        for (const tab of tabs) {
            for (const device of devices) {
                const r = await c.open('http://127.0.0.1:' + s.port + '/dashboard?tab=' + tab + '&device=' + device, device);
                const height = await c.evaluate('Math.ceil(document.documentElement.scrollHeight)');
                const d = chrome.DEVICES[device];
                await c.page('Emulation.setDeviceMetricsOverride', { width: d.width, height: Math.min(Math.max(height, d.height), 12000), deviceScaleFactor: d.scale, mobile: d.mobile });
                await c.evaluate('new Promise(function(r){requestAnimationFrame(function(){requestAnimationFrame(r)})})');
                const said = [].concat(r.error ? ['error: ' + r.error] : [], r.misses.map(m => 'unanswered: ' + m), r.console);
                if (said.length) { problems++; console.log('  ' + tab + ' ' + device + '\n    ' + said.join('\n    ')); }
                fs.writeFileSync(path.join(a.out, 'dashboard-' + tab + '-' + device + '.webp'), await c.shot('webp', 82));
            }
            console.log(tab);
        }
    } finally {
        await c.close();
        await s.close();
    }
    process.exitCode = problems ? 1 : 0;
}

main().catch(e => { console.error(e.stack || e); process.exit(1); });
