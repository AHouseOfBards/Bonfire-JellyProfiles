/*
 * Shared plumbing for the WebKit harnesses (webkit*.js). Not a harness: run.sh skips _*.js.
 *
 * Why: the official Jellyfin app for iPhone and iPad, and Safari, run WebKit. Every other
 * client harness drives Chrome, so nothing had run the client script in WebKit.
 *
 * Playwright lives in tests/webkit/package.json (a devDependency for this alone, never
 * shipped). It is the only way to get a WebKit that runs on a Linux CI box and on this
 * Windows desk. When Playwright or its WebKit build is missing, a harness says SKIP and
 * exits 0, as the Chrome harnesses do. CI sets BONFIRE_WEBKIT_REQUIRED=1 in its WebKit
 * job, where a missing install is a failure: that job must never skip.
 *
 *     cd tests/webkit && npm ci && npx playwright-core install webkit
 *
 * Every harness takes the client script as argv[2] and, where the stylesheet is what is
 * under test, a stylesheet as argv[3], so it can be pointed at a build carrying the bug.
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');
const fixture = require('../screens/fixture');

const REQUIRED = process.env.BONFIRE_WEBKIT_REQUIRED === '1';

function skip(why) {
    if (REQUIRED) {
        console.log('FAIL  WebKit is required here and is not usable: ' + why);
        process.exit(1);
    }
    console.log('SKIPPED  ' + why + ' (run: cd tests/webkit && npm ci && npx playwright-core install webkit)');
    process.exit(0);
}

/** Playwright's webkit launcher, or a SKIP. */
function loadWebkit() {
    try {
        return require(path.join(__dirname, '..', 'webkit', 'node_modules', 'playwright-core')).webkit;
    } catch (e) {
        return skip('playwright-core is not installed');
    }
}

async function launch() {
    const webkit = loadWebkit();
    try {
        return await webkit.launch();
    } catch (e) {
        return skip('Playwright WebKit would not start: ' + String(e.message).split('\n')[0]);
    }
}

/** Phone and tablet as the Chrome harnesses size them (tests/screens/chrome.js DEVICES). */
const DEVICES = {
    phone: { width: 390, height: 844, scale: 2 },
    tablet: { width: 820, height: 1180, scale: 1 }
};

/** The client script's text: argv[2] if given, with argv[3]'s stylesheet swapped in for the tree's. */
function sources() {
    const jsPath = process.argv[2] || path.join(L.ROOT, 'Web', 'profiles.js');
    return { js: fs.readFileSync(jsPath, 'utf8'), cssPath: process.argv[3] || null };
}

/**
 * Starts the fixture serving `js`; with `cssPath`, the served bundle carries that
 * stylesheet instead of Web/styles.css. Throws if the swap found nothing to swap, so a
 * harness pointed at a stylesheet cannot quietly test the tree's own.
 */
async function serve(js, cssPath) {
    const srv = await fixture.start(0, { source: js });
    if (!cssPath) return srv;
    const swapped = fixture.bundle(js).split(JSON.stringify(L.stylesOnly()));
    if (swapped.length !== 2) throw new Error('stylesheet swap: expected one copy of the stylesheet in the bundle, found ' + (swapped.length - 1));
    const body = swapped.join(JSON.stringify(fs.readFileSync(cssPath, 'utf8')));
    // Same fixture, but /bonfire.js is the swapped bundle: wrap its handler.
    const handlers = srv.server.listeners('request');
    srv.server.removeAllListeners('request');
    srv.server.on('request', (req, res) => {
        if (new URL(req.url, 'http://x').pathname === '/bonfire.js') {
            res.writeHead(200, { 'Content-Type': 'text/javascript', 'Cache-Control': 'no-store' });
            return res.end(body);
        }
        handlers.forEach(h => h.call(srv.server, req, res));
    });
    return srv;
}

/**
 * Opens one fixture scene at a device size, as a touch device, and waits for it to settle.
 * Returns { page, errors, close } where errors holds every uncaught exception and every
 * console error the page produced, the same two signals inject.test.js watches for.
 */
async function openScene(browser, srv, scene, device, extra) {
    const d = DEVICES[device];
    const context = await browser.newContext({
        viewport: { width: d.width, height: d.height }, deviceScaleFactor: d.scale, hasTouch: true, isMobile: true
    });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', e => errors.push('uncaught: ' + (e && e.message || e)));
    page.on('console', m => {
        if (m.type() !== 'error') return;
        // The Material Icons font is not served by the bare fixture (opts.materialIcons).
        if (/\/fixture\/material-icons/.test(m.location().url || '') || /material-icons/.test(m.text())) return;
        errors.push('console: ' + m.text());
    });
    await page.goto('http://127.0.0.1:' + srv.port + '/?theme=bonfire&scene=' + scene + '&device=' + device + (extra || ''));
    await page.waitForFunction('window.__ready === true', null, { timeout: 15000 });
    return { page, errors, context, close: () => context.close() };
}

module.exports = { launch, sources, serve, openScene, skip, DEVICES };
