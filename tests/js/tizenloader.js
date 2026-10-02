/*
 * The Tizen loader (Web/tizen/bonfire-loader.js).
 *
 * A Tizen .wgt carries its own jellyfin-web, so Bonfire used to be copied into the package
 * and froze there: every fix meant rebuilding the package. The loader goes in instead,
 * once, and pulls the live script from whichever server the app connects to. This runs it
 * against a stand-in page with a clock it controls, and checks the three things that
 * decide whether it works on a television:
 *
 *   - it parses on the oldest engine it has to (Tizen 4 is Chromium 56), via jsbaseline;
 *   - it asks the server for the route the plugin actually serves;
 *   - it waits for a server, loads exactly once, and recovers from a server that was not
 *     up yet — a television usually starts before its network.
 *
 *     node tests/js/tizenloader.js [path/to/bonfire-loader.js]
 */
'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');
const { spawnSync } = require('child_process');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
}

const LOADER = process.argv[2] || path.join(L.ROOT, 'Web', 'tizen', 'bonfire-loader.js');

console.log('\n── It exists, and parses on an old television ─────────────────');
const exists = fs.existsSync(LOADER);
ok('there is a loader to put in the package', exists);
if (!exists) finish();

const src = fs.readFileSync(LOADER, 'utf8');
const baseline = spawnSync(process.execPath, [path.join(__dirname, 'jsbaseline.js'), LOADER], { encoding: 'utf8' });
ok('nothing in it is newer than the engine floor jsbaseline holds', baseline.status === 0);
if (baseline.status !== 0) console.log(baseline.stdout.split('\n').filter(l => /FAIL/.test(l)).join('\n'));
// jsbaseline's floor is Chromium 68; Tizen 4 is 56. The gap is ES2017 and later, so the
// loader is held to ES5 outright: no arrow, no let/const, no template, no class.
const code = src.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '').replace(/'(?:[^'\\]|\\.)*'/g, "''");
ok('and it is plain ES5, which parses on Tizen 4 (Chromium 56)',
   !/=>|\blet\b|\bconst\b|`|\bclass\b|\basync\b|\bawait\b|\?\.|\?\?/.test(code));

console.log('\n── It asks for the route the plugin serves ─────────────────────');
const controller = fs.readFileSync(path.join(L.ROOT, 'Controllers', 'ProfilesController.cs'), 'utf8');
const prefix = (/\[Route\("([^"]+)"\)\]/.exec(controller) || [])[1];
const served = /\[HttpGet\("profiles\.js"\)\]/.test(controller);
const asked = (/SCRIPT_PATH\s*=\s*'([^']+)'/.exec(src) || [])[1];
ok('the loader asks for /' + prefix + '/profiles.js (' + asked + ')',
   served && !!prefix && asked === '/' + prefix + '/profiles.js');
const docs = fs.readFileSync(path.join(L.ROOT, 'docs', 'developer-api.md'), 'utf8');
ok('and that route is one the API reference lists as anonymous — the television is not signed in yet',
   /### `GET \/plugins\/profiles\/profiles\.js`\s+\*\*Authorisation:\*\* anonymous\./.test(docs));

// ── a stand-in page with a clock this file turns ────────────────────────────
function page() {
    const timers = [];
    const appended = [];
    const byId = {};
    const head = {
        appendChild(el) { appended.push(el); el.parentNode = head; if (el.id) byId[el.id] = el; return el; },
        removeChild(el) { el.parentNode = null; if (byId[el.id] === el) delete byId[el.id]; }
    };
    const window = {};
    const document = {
        head,
        documentElement: head,
        // Whatever the package's index.html already carries, plus anything appended.
        preexisting: [],
        getElementsByTagName: tag => tag === 'script'
            ? document.preexisting.concat(appended.filter(s => s.parentNode))
            : [],
        getElementById: id => byId[id] || null,
        createElement: tag => ({
            tagName: tag.toUpperCase(), id: '', src: '', onerror: null, parentNode: null,
            getAttribute(name) { return name === 'src' ? this.src : null; }
        })
    };
    window.document = document;
    const warnings = [];
    const sandbox = {
        window, document,
        console: { warn: m => warnings.push(m) },
        setTimeout: (fn, ms) => { timers.push({ fn, ms }); return timers.length; },
        String, RegExp
    };
    window.console = sandbox.console;
    vm.createContext(sandbox);
    return {
        sandbox, window, appended, document, warnings,
        run() { vm.runInContext(src, sandbox, { filename: LOADER }); },
        // One turn of the clock: run whatever was scheduled, return how many ran.
        turn() { const due = timers.splice(0); due.forEach(t => t.fn()); return due.length; },
        pending: () => timers.length,
        live: () => appended.filter(s => s.parentNode)
    };
}

console.log('\n── It waits for a server, then loads once ──────────────────────');
{
    const p = page();
    p.run();
    ok('on the server-selection screen nothing is loaded', p.appended.length === 0);
    ok('and it keeps looking', p.pending() === 1);
    for (let i = 0; i < 10; i++) p.turn();
    ok('for as long as no server is chosen', p.appended.length === 0 && p.pending() === 1);

    p.window.ApiClient = { serverAddress: () => 'http://jellyfin.lan:8096/' };
    p.turn();
    const s = p.live()[0];
    ok('once a server is chosen, Bonfire is loaded from it (' + (s && s.src) + ')',
       !!s && s.src === 'http://jellyfin.lan:8096/plugins/profiles/profiles.js');
    ok('and the looking stops', p.pending() === 0);

    // The loader included twice — a package patched by hand and then by a tool.
    p.run();
    p.turn();
    ok('included twice, it still loads Bonfire once', p.live().length === 1);
}

{
    const p = page();
    p.window.ApiClient = { serverAddress: () => 'https://media.example.org/jellyfin' };
    p.run();
    ok('a server under a base path is asked under it',
       p.live()[0] && p.live()[0].src === 'https://media.example.org/jellyfin/plugins/profiles/profiles.js');
}

{
    const p = page();
    p.window.ApiClient = { _serverAddress: 'http://10.0.0.5:8096' };
    p.run();
    ok('an older jellyfin-web that keeps the address in a field is read too',
       p.live()[0] && p.live()[0].src === 'http://10.0.0.5:8096/plugins/profiles/profiles.js');
}

{
    const p = page();
    p.window.ApiClient = { serverAddress: () => 'file:///opt/usr/apps/www' };
    p.run();
    ok('the package\'s own file:// origin is never mistaken for a server', p.appended.length === 0);
}

{
    const p = page();
    p.window.ApiClient = { serverAddress: () => { throw new Error('not ready'); } };
    p.run();
    ok('an ApiClient that throws while it starts up is waited out, not fatal', p.pending() === 1);
}

{
    // A package patched the old way: profiles.js copied in and tagged. Two copies would be
    // two gates, so the loader stands aside and says which tag to remove.
    const p = page();
    p.document.preexisting.push({ id: '', getAttribute: n => (n === 'src' ? 'plugin_cache/profiles.js' : null) });
    p.window.ApiClient = { serverAddress: () => 'http://jellyfin.lan:8096' };
    p.run();
    ok('a package that already carries its own copy is not given a second', p.appended.length === 0);
    ok('and the console says which tag to remove',
       p.warnings.length === 1 && p.warnings[0].indexOf('plugin_cache/profiles.js') !== -1);
}

console.log('\n── It recovers from a server that was not up yet ───────────────');
{
    const p = page();
    p.window.ApiClient = { serverAddress: () => 'http://jellyfin.lan:8096' };
    p.run();
    const first = p.live()[0];
    first.onerror();
    ok('a failed load removes its tag', p.live().length === 0);
    p.turn();
    ok('and tries again', p.live().length === 1 && p.live()[0] !== first);

    for (let i = 0; i < 10; i++) { const s = p.live()[0]; if (s) s.onerror(); p.turn(); }
    ok('but gives up after a few failures rather than hammering a server with no Bonfire',
       p.pending() === 0 && p.appended.length <= 6);
}

console.log('\n── It stops looking eventually ─────────────────────────────────');
{
    const p = page();
    p.run();
    let turns = 0;
    while (p.pending() && turns < 5000) { p.turn(); turns++; }
    ok('left on a screen with no server, it stops (' + turns + ' polls)', p.pending() === 0 && turns < 5000);
}

finish();

function finish() {
    console.log('\n  ' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
}
