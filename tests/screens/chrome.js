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
    tv: { width: 1920, height: 1080, mobile: false, scale: 1 },
    // The review's extra shapes (not in the goldens): a laptop with a mouse, a phone on its
    // side, and a 720p television, where jellyfin-web's root is 20px rather than 27.
    desktop: { width: 1440, height: 900, mobile: false, scale: 1 },
    'phone-land': { width: 844, height: 390, mobile: true, scale: 2 },
    tv720: { width: 1280, height: 720, mobile: false, scale: 1 }
};

/** How many tabs map() uses when not told: 8: viewwide took 104s at 4, 60s at 8, 56s at 12, 57s at 16 on a 16-core PC; override with CHROME_POOL. */
const POOL = Number(process.env.CHROME_POOL) || 8;

// Profiles a run left behind: one killed before close(), or one Chrome still held when close()
// tried. 4,222 of them had filled a C: drive by 2026-11-14. An hour old is never in use, since no
// harness runs that long; a folder Chrome still holds just fails to delete and is tried next time.
function sweepOldProfiles() {
    const cutoff = Date.now() - 60 * 60 * 1000;
    let names = [];
    try { names = fs.readdirSync(os.tmpdir()); } catch (e) { return; }
    for (const n of names) {
        if (!n.startsWith('bonfire-chrome-')) continue;
        const p = path.join(os.tmpdir(), n);
        try { if (fs.statSync(p).mtimeMs < cutoff) fs.rmSync(p, { recursive: true, force: true }); } catch (e) { /* in use */ }
    }
}

let swept = false;

async function launch() {
    if (!swept) { swept = true; sweepOldProfiles(); }
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'bonfire-chrome-'));
    const proc = spawn(findChrome(), [
        '--headless=new', '--remote-debugging-port=0', '--user-data-dir=' + dir,
        '--hide-scrollbars', '--no-first-run', '--no-default-browser-check', '--mute-audio',
        '--disable-gpu-vsync', '--font-render-hinting=none',
        // Several tabs at once: a tab that is not in front must still run its frames and timers.
        '--disable-renderer-backgrounding', '--disable-background-timer-throttling', '--disable-backgrounding-occluded-windows', 'about:blank'
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

    /**
     * One tab: its own target and session, so its own device emulation and its own console.
     * open() waits for the scene's ready signal by event. The page's __ready is given a setter
     * before any script runs that calls a binding, so the wait ends the moment the scene says
     * so, not up to 100ms later; the old poll stays behind it as the fallback (a page that
     * never sets __ready still times out, and one that set it some other way is still noticed).
     */
    async function tab() {
        // A browser context of its own: scenes clear and set localStorage, which tabs in one
        // context share, so one scene's setup would wipe another's mid-draw.
        const { browserContextId } = await send('Target.createBrowserContext', {});
        const { targetId } = await send('Target.createTarget', { url: 'about:blank', browserContextId });
        const { sessionId } = await send('Target.attachToTarget', { targetId, flatten: true });
        const page = (method, params) => send(method, params, sessionId);
        await page('Page.enable');
        await page('Runtime.enable');
        await page('Emulation.setFocusEmulationEnabled', { enabled: true });
        await page('Runtime.addBinding', { name: '__bonfireReady' });
        await page('Page.addScriptToEvaluateOnNewDocument', { source:
            "(function () { var v = false; Object.defineProperty(window, '__ready', { configurable: true, get: function () { return v; }," +
            " set: function (x) { v = x; if (x === true && window.__bonfireReady) window.__bonfireReady('1'); } }); })();" });
        const consoleLines = [];
        let onReady = null;
        listeners.push(msg => {
            if (msg.sessionId !== sessionId) return;
            if (msg.method === 'Runtime.bindingCalled' && msg.params.name === '__bonfireReady' && onReady) onReady();
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
            const fired = new Promise(res => { onReady = res; });
            await page('Page.navigate', { url });
            const until = Date.now() + 20000;
            for (;;) {
                let ready = false;
                try { ready = await evaluate('window.__ready === true'); } catch (e) { /* navigating */ }
                if (ready) break;
                if (Date.now() > until) { onReady = null; throw new Error('timed out waiting for the scene: ' + url + '\n' + consoleLines.join('\n')); }
                await Promise.race([fired, new Promise(r => setTimeout(r, 100))]);
            }
            onReady = null;
            const state = await evaluate('({ error: window.__error, misses: window.__misses })');
            return { error: state.error, misses: state.misses, console: consoleLines.slice() };
        }

        async function shot(format, quality) {
            const r = await page('Page.captureScreenshot', { format: format || 'webp', quality: quality || 82, captureBeyondViewport: false });
            return Buffer.from(r.data, 'base64');
        }

        return { page, evaluate, open, shot, sessionId };
    }

    const first = await tab();
    const tabs = [first];

    /**
     * Runs `fn(tab, item, index)` for every item, `n` at a time, each in its own tab, and
     * returns the results in item order whatever order they finished in. Tabs are reused, so a
     * tab is made once per slot, not once per item. A tab is one scene at a time: fn must not
     * keep hold of it after it returns. The scenes must be independent of each other.
     */
    async function map(items, fn, n) {
        const width = Math.max(1, Math.min(n || POOL, items.length));
        while (tabs.length < width) tabs.push(await tab());
        const out = new Array(items.length);
        let next = 0;
        await Promise.all(tabs.slice(0, width).map(async t => {
            for (;;) {
                const i = next++;
                if (i >= items.length) return;
                out[i] = await fn(t, items[i], i);
            }
        }));
        return out;
    }

    /**
     * map() for scene checks. `fn(tab, item, check, index)` calls check(name, cond, detail) as it
     * goes, exactly as a harness calls its own ok(), and may return early. Returns every check
     * in item order, then call order, as [name, cond, detail], for the harness to feed to its
     * own ok() - so the output and the assertion list are the same as a serial loop gives.
     */
    async function checks(items, fn, n) {
        const lists = await map(items, async (t, item, i) => {
            const out = [];
            await fn(t, item, (name, cond, detail) => out.push([name, cond, detail]), i);
            return out;
        }, n);
        return [].concat.apply([], lists);
    }

    async function close() {
        try { ws.close(); } catch (e) { /* gone */ }
        // Wait for the process to be gone, not a fixed 300ms: on Windows Chrome's helpers hold the
        // profile for longer, the delete failed silently, and every run left its profile behind.
        const exited = proc.exitCode !== null ? Promise.resolve() : new Promise(r => proc.once('exit', r));
        proc.kill();
        await Promise.race([exited, new Promise(r => setTimeout(r, 5000))]);
        for (let i = 0; i < 10; i++) {
            try { fs.rmSync(dir, { recursive: true, force: true }); break; }
            catch (e) { await new Promise(r => setTimeout(r, 200)); /* a helper still holds a file */ }
        }
    }

    return { page: first.page, evaluate: first.evaluate, open: first.open, shot: first.shot, close, listeners, sessionId: first.sessionId, tab, map, checks };
}

module.exports = { launch, DEVICES, findChrome, POOL };
