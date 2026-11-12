/*
 * The startup cover's first frame is the theme that will be shown, or nothing themed.
 *
 * Reported on 1.9.0.4-beta: "Loading Jellyfin for the first time shows the profile
 * screen's cover briefly in the Bonfire theme, empty, before it switches to that user's
 * chosen theme." The cover's fallback was Bonfire's gradient, so anything that left the
 * theme unknown for a moment (no cache for this account, someone else's cache, a theme
 * the stylesheet has no block for yet) painted Bonfire. With no theme known it is now
 * jellyfin-web's own plain page colour.
 *
 * Asked in a real browser, so the cascade is Chrome's and not a reading of the rules:
 *   1. the head script from WebInjection.cs, run while <head> parses with a given cache,
 *      and the cover's ::before read before anything else has run — what the first frame
 *      is painted with;
 *   2. profiles.js's copy of the rule (GATE_COVER_CSS) under the real stylesheet and theme
 *      blocks, as the Tizen package and a switch use it: with no theme on, plain; with
 *      each theme on, exactly the gate's own background.
 *
 *     node tests/js/coverframe.js [profiles.js] [WebInjection.cs]
 */
'use strict';
const fs = require('fs');
const http = require('http');
const path = require('path');
const L = require('./_lib');
const chrome = require('../screens/chrome');

const JS_PATH = process.argv[2] || L.profilesPath();
const CS_PATH = process.argv[3] || path.join(L.ROOT, 'Injection', 'WebInjection.cs');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

// The head script, reassembled from the C# constant as tests/js/gatecover.js does.
function headScriptTag() {
    const cs = fs.readFileSync(CS_PATH, 'utf8');
    const at = cs.indexOf('internal const string HeadScript =');
    if (at === -1) throw new Error('HeadScript not found in ' + CS_PATH);
    const end = cs.indexOf('";', at);
    const region = cs.slice(at, end + 2).split('\n')
        .map(line => { const c = line.indexOf('//'); return c === -1 ? line : line.slice(0, c); })
        .join('\n');
    return (region.match(/"(?:[^"\\]|\\.)*"/g) || [])
        .map(p => p.slice(1, -1).replace(/\\(["'\\])/g, '$1')).join('');
}

const JS = fs.readFileSync(JS_PATH, 'utf8');
const COVER_CSS = (/const GATE_COVER_CSS = '([^']*)';/.exec(JS) || [])[1] || '';
// The sheet and every built-in theme's block, as the browser has them.
const SHEET = L.extractCss(JS);
const THEME_IDS = fs.readdirSync(path.join(L.ROOT, 'Web', 'themes'))
    .filter(f => /\.json$/.test(f) && f !== 'fonts.json').map(f => f.replace(/\.json$/, ''));

const MASTER = '8e3cdfa5-79a8-4bb9-bd9a-0e96b7dc974a';
const MASTER_N = MASTER.replace(/-/g, '');
const signedIn = {
    jellyfin_credentials: JSON.stringify({ Servers: [{ Id: 's', AccessToken: 't', UserId: MASTER }] }),
    jellyfin_profiles_master_state: JSON.stringify({ masterUserId: MASTER }),
    jellyfin_profiles_switcher_mode: JSON.stringify({ askOnStartup: true, location: 'button', masterUserId: MASTER })
};

// What the first frame is painted with: read in a script straight after the head script,
// before the body exists and before anything else can run.
const PROBE = '<script>(function(){var h=document.documentElement,c=getComputedStyle(h,"::before");'
    + 'window.__first={cover:h.getAttribute("data-jpf-cover")!==null,theme:h.getAttribute("data-jpf-theme"),'
    + 'color:c.backgroundColor,image:c.backgroundImage};window.__ready=true;})();</script>';

function serve() {
    const server = http.createServer((req, res) => {
        const u = new URL(req.url, 'http://x');
        let body;
        if (u.pathname === '/setup') {
            // Sets this origin's storage, then says it is ready.
            body = '<!doctype html><script>localStorage.clear();var s=' + (u.searchParams.get('s') || '{}')
                + ';for(var k in s)localStorage.setItem(k,s[k]);window.__ready=true;</script>';
        } else if (u.pathname === '/web/home') {
            body = '<!doctype html><html><head>' + headScriptTag() + PROBE + '</head><body></body></html>';
        } else if (u.pathname === '/sheet') {
            const theme = u.searchParams.get('theme');
            body = '<!doctype html><html data-jpf-cover=""' + (theme ? ' data-jpf-theme="' + theme + '"' : '') + '><head>'
                + '<style>' + SHEET + '</style><style id="jpf-cover-css">' + COVER_CSS + '</style></head>'
                + '<body><div id="profiles-gate-overlay"></div><script>(function(){'
                + 'var c=getComputedStyle(document.documentElement,"::before"),g=getComputedStyle(document.getElementById("profiles-gate-overlay"));'
                + 'window.__first={color:c.backgroundColor,image:c.backgroundImage,gateColor:g.backgroundColor,gateImage:g.backgroundImage};'
                + 'window.__ready=true;})();</script></body></html>';
        } else {
            res.writeHead(404); res.end(); return;
        }
        res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
        res.end(body);
    });
    return new Promise(r => server.listen(0, '127.0.0.1', () => r(server)));
}

const PLAIN = 'rgb(16, 16, 16)';

async function main() {
    try { chrome.findChrome(); } catch (e) {
        console.log('  SKIPPED  no Chrome on this machine: nothing was measured\n\n  0 passed, 0 failed, skipped'); return 0;
    }
    ok('profiles.js carries the cover rule', !!COVER_CSS);
    const server = await serve();
    const base = 'http://127.0.0.1:' + server.address().port;
    const c = await chrome.launch();
    try {
        async function firstFrame(storage) {
            await c.open(base + '/setup?s=' + encodeURIComponent(JSON.stringify(storage)), 'desktop');
            await c.open(base + '/web/home', 'desktop');
            return c.evaluate('window.__first');
        }
        const show = f => 'theme=' + f.theme + ' color=' + f.color + ' image=' + f.image;

        console.log('\n── 1. The head script\'s first frame ─────────────────────────');
        const MINE_BG = 'linear-gradient(180deg, rgb(32, 48, 64) 0%, rgb(32, 48, 64) 100%)';
        // As Chrome computes it: 180deg is the default direction, so it is left out.
        const MINE_SEEN = 'linear-gradient(rgb(32, 48, 64) 0%, rgb(32, 48, 64) 100%)';
        const mine = await firstFrame(Object.assign({}, signedIn, { jellyfin_profiles_theme:
            JSON.stringify({ last: 'mine', users: { [MASTER_N]: 'mine' }, bg: { mine: MINE_BG } }) }));
        ok('this account\'s cached theme: the cover is up in it from the first frame',
            mine.cover && mine.theme === 'mine' && mine.image === MINE_SEEN, show(mine));

        const none = await firstFrame(signedIn);
        ok('no theme cached: the cover is plain, in jellyfin-web\'s #101010, not Bonfire',
            none.cover && none.image === 'none' && none.color === PLAIN, show(none));

        const theirs = await firstFrame(Object.assign({}, signedIn, { jellyfin_profiles_theme:
            JSON.stringify({ last: 'mine', users: { bbbb2222: 'mine' }, bg: { mine: MINE_BG } }) }));
        ok('only someone else\'s theme cached: plain, not theirs and not Bonfire',
            theirs.cover && theirs.image === 'none' && theirs.color === PLAIN, show(theirs));

        const older = await firstFrame(Object.assign({}, signedIn, { jellyfin_profiles_theme:
            JSON.stringify({ last: 'glass', users: { [MASTER_N]: 'glass' } }) }));
        ok('a cached theme with no cached background: plain',
            older.cover && older.image === 'none' && older.color === PLAIN, show(older));

        const light = await firstFrame(Object.assign({}, signedIn, { 'jpf-bg': 'rgb(240, 240, 240)|light' }));
        ok('no theme, and jellyfin-web\'s page colour stored at the last switch: that colour',
            light.cover && light.image === 'none' && light.color === 'rgb(240, 240, 240)', show(light));

        const lightMine = await firstFrame(Object.assign({}, signedIn, { 'jpf-bg': 'rgb(240, 240, 240)|light',
            jellyfin_profiles_theme: JSON.stringify({ users: { [MASTER_N]: 'mine' }, bg: { mine: MINE_BG } }) }));
        ok('and a cached theme still wins over that colour', lightMine.image === MINE_SEEN, show(lightMine));

        console.log('\n── 2. profiles.js\'s copy, under the real stylesheet ─────────');
        await c.open(base + '/sheet', 'desktop');
        const bare = await c.evaluate('window.__first');
        ok('no theme on the page: plain #101010, though the sheet gives :root Bonfire\'s tokens',
            bare.image === 'none' && bare.color === PLAIN, 'color=' + bare.color + ' image=' + bare.image);
        const off = [];
        for (const id of THEME_IDS) {
            await c.open(base + '/sheet?theme=' + id, 'desktop');
            const f = await c.evaluate('window.__first');
            if (f.image !== f.gateImage || f.color !== f.gateColor) off.push(id + ': cover ' + f.color + ' ' + f.image + ' / gate ' + f.gateColor + ' ' + f.gateImage);
        }
        ok('each theme on the page (' + THEME_IDS.length + '): the cover is exactly the gate\'s background',
            THEME_IDS.length > 5 && off.length === 0, off.join('\n          '));
    } finally { await c.close(); server.close(); }

    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    return fails.length ? 1 : 0;
}
main().then(code => process.exit(code), e => { console.error(e); process.exit(1); });
