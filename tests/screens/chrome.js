/*
 * Just enough of the Chrome DevTools protocol to load a page at a device size and take a
 * picture of it. No Puppeteer: one more dependency to install for a tool run by hand a few
 * times a release, and the protocol calls used here have not changed in years.
 */
'use strict';

const { spawn } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const CANDIDATES = [
    process.env.CHROME,
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    '/usr/bin/google-chrome', '/usr/bin/chromium', '/usr/bin/chromium-browser'
].filter(Boolean);

function findChrome() {
    const hit = CANDIDATES.find(p => { try { return fs.statSync(p).isFile(); } catch (e) { return false; } });
    if (!hit) throw new Error('no Chrome found; set CHROME to its path');
    return hit;
}

/** Devices as CSS pixels. TV is 1080p, which is what webOS and Tizen lay jellyfin-web out at. */
const DEVICES = {
    phone: { width: 390, height: 844, mobile: true, scale: 2 },
    tablet: { width: 820, height: 1180, mobile: true, scale: 1 },
    tv: { width: 1920, height: 1080, mobile: false, scale: 1 }
};

async function launch() {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'bonfire-chrome-'));
    const proc = spawn(findChrome(), [
        '--headless=new', '--remote-debugging-port=0', '--user-data-dir=' + dir,
        '--hide-scrollbars', '--no-first-run', '--no-default-browser-check', '--mute-audio',
        '--disable-gpu-vsync', '--font-render-hinting=none', 'about:blank'
    ], { stdio: ['ignore', 'ignore', 'pipe'] });
    const wsUrl = await new Promise((resolve, reject) => {
        let buf = '';
        const t = setTimeout(() => reject(new Error('Chrome did not start')), 20000);
        proc.stderr.on('data', d => {
            buf += d;
            const m = /DevTools listening on (ws:\/\/\S+)/.exec(buf);
            if (m) { clearTimeout(t); resolve(m[1]); }
        });
        proc.on('exit', c => reject(new Error('Chrome exited ' + c + ': ' + buf.slice(-400))));
    });
    const ws = new WebSocket(wsUrl);
    await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
    let id = 0;
    const pending = new Map();
    const listeners = [];
    ws.onmessage = ev => {
        const msg = JSON.parse(ev.data);
        if (msg.id && pending.has(msg.id)) {
            const { resolve, reject } = pending.get(msg.id);
            pending.delete(msg.id);
            msg.error ? reject(new Error(msg.error.message)) : resolve(msg.result);
        } else if (msg.method) {
            listeners.forEach(l => l(msg));
        }
    };
    const send = (method, params, sessionId) => new Promise((resolve, reject) => {
        const n = ++id;
        pending.set(n, { resolve, reject });
        ws.send(JSON.stringify({ id: n, method, params: params || {}, sessionId }));
    });

    const { targetId } = await send('Target.createTarget', { url: 'about:blank' });
    const { sessionId } = await send('Target.attachToTarget', { targetId, flatten: true });
    const page = (method, params) => send(method, params, sessionId);
    await page('Page.enable');
    await page('Runtime.enable');
    const consoleLines = [];
    listeners.push(msg => {
        if (msg.sessionId !== sessionId) return;
        if (msg.method === 'Runtime.exceptionThrown') consoleLines.push('exception: ' + (msg.params.exceptionDetails.exception || {}).description);
        if (msg.method === 'Runtime.consoleAPICalled' && (msg.params.type === 'error' || msg.params.type === 'warning')) {
            consoleLines.push(msg.params.type + ': ' + msg.params.args.map(a => a.value !== undefined ? a.value : a.description).join(' '));
        }
    });

    async function evaluate(expr) {
        const r = await page('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
        if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception ? r.exceptionDetails.exception.description : r.exceptionDetails.text);
        return r.result.value;
    }

    /** Opens `url` at a device size, with or without reduced motion, and waits for __ready. */
    async function open(url, device, opts) {
        const d = DEVICES[device];
        const o = opts || {};
        consoleLines.length = 0;
        await page('Emulation.setDeviceMetricsOverride', { width: d.width, height: d.height, deviceScaleFactor: d.scale, mobile: d.mobile });
        await page('Emulation.setTouchEmulationEnabled', { enabled: d.mobile });
        await page('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: o.reducedMotion ? 'reduce' : 'no-preference' }] });
        await page('Page.navigate', { url });
        const until = Date.now() + 20000;
        for (;;) {
            let ready = false;
            try { ready = await evaluate('window.__ready === true'); } catch (e) { /* navigating */ }
            if (ready) break;
            if (Date.now() > until) throw new Error('timed out waiting for the scene: ' + url + '\n' + consoleLines.join('\n'));
            await new Promise(r => setTimeout(r, 100));
        }
        const state = await evaluate('({ error: window.__error, misses: window.__misses })');
        return { error: state.error, misses: state.misses, console: consoleLines.slice() };
    }

    async function shot(format, quality) {
        const r = await page('Page.captureScreenshot', { format: format || 'webp', quality: quality || 82, captureBeyondViewport: false });
        return Buffer.from(r.data, 'base64');
    }

    async function close() {
        try { ws.close(); } catch (e) { /* gone */ }
        proc.kill();
        await new Promise(r => setTimeout(r, 300));
        try { fs.rmSync(dir, { recursive: true, force: true }); } catch (e) { /* Chrome still holds a file */ }
    }

    return { page, evaluate, open, shot, close, listeners, sessionId };
}

module.exports = { launch, DEVICES, findChrome };
