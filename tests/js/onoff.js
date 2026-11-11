// Bonfire turned off from the Status tab (2.0).
//
// Off, the server serves the same script with one line rewritten (PublishOff in
// ProfilesController), not the emergency code's inert script: the settings page's theme
// preview runs on window.BonfireThemePreview from this script, and must keep working.
// Checked here:
//
//  - the line the server rewrites is in profiles.js, exactly as PublishOff spells it;
//  - off, init() tears down and stops: no events bound, no styles, no preferences
//    request, no route check, the startup cover down and its cache gone;
//  - off, BonfireThemePreview is still there and still draws a theme;
//  - on, init() still does all of it, so the checks above are not vacuous.
//
// Red against 14c94bb, which has no such line.
//
//   node tests/js/onoff.js [path/to/profiles.js]

'use strict';
const fs = require('fs');
const path = require('path');
const L = require('./_lib');
const D = require('./_stubdom');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const OFF_LINE = 'const BONFIRE_OFF = false; // __BONFIRE_OFF__';
const ON_LINE = 'const BONFIRE_OFF = true; // __BONFIRE_OFF__';
const src = L.readProfiles();

console.log('\n-- The line the server rewrites --------------------------------');
ok('profiles.js has the off marker exactly once', src.split(OFF_LINE).length === 2, (src.split(OFF_LINE).length - 1) + ' found');
const ctl = fs.readFileSync(path.join(__dirname, '..', '..', 'Controllers', 'ProfilesController.cs'), 'utf8');
const publish = (/internal static string PublishOff\(string js\)([\s\S]*?);\s*\n/.exec(ctl) || [])[1] || '';
ok('PublishOff replaces that exact line', publish.includes('"' + OFF_LINE + '"'), publish.trim().slice(0, 120));
ok('with the same line set to true', publish.includes('"' + ON_LINE + '"'));

function boot(off) {
    // The script as served (themes spliced in), then PublishOff's rewrite.
    const served = L.readClientBundle(src);
    const env = D.loadPlugin(off ? served.replace(OFF_LINE, ON_LINE) : served);
    // What a browser has and the shared stub leaves out: without removeProperty the
    // teardown threw halfway, and without navigator two on-path steps log noise.
    env.sandbox.document.documentElement.style.removeProperty = () => {};
    env.sandbox.navigator = { userAgent: 'test', language: 'en' };
    const PP = env.PP;
    const calls = {};
    ['bindEvents', 'injectStyles', 'loadSwitcherPrefs', 'checkRoute', 'loadLocale', '_startPageChecks', 'applyPanicDisable']
        .forEach(name => {
            const real = PP[name];
            calls[name] = 0;
            if (typeof real !== 'function') return;
            PP[name] = function () {
                calls[name]++;
                // The teardown runs for real; the rest only counts, so nothing reaches past the stub DOM.
                return name === 'applyPanicDisable' ? real.apply(this, arguments) : undefined;
            };
        });
    return { env, PP, calls };
}

(async () => {
    console.log('\n-- Off ---------------------------------------------------------');
    {
        const { env, PP, calls } = boot(true);
        const html = env.sandbox.document.documentElement;
        html.setAttribute('data-jpf-cover', '');
        env.sandbox.localStorage.setItem(PP.config.switcherModeKey, '{"askOnStartup":true}');
        env.sandbox.localStorage.setItem(PP.config.switchingKey, '1');
        PP.init();
        await env.settle();
        ok('the teardown ran to its last line', env.sandbox.localStorage.getItem(PP.config.switchingKey) == null);
        ok('init tears down as the emergency code does', calls.applyPanicDisable === 1, String(calls.applyPanicDisable));
        ok('and is then inert', PP._panicDisabled === true);
        ok('it does not remember it, so turning back on needs no restart',
            env.sandbox.localStorage.getItem(PP.config.panicKey) == null);
        ok('no events are bound', calls.bindEvents === 0, String(calls.bindEvents));
        ok('no styles are injected', calls.injectStyles === 0, String(calls.injectStyles));
        ok('no preferences request', calls.loadSwitcherPrefs === 0, String(calls.loadSwitcherPrefs));
        ok('no route check', calls.checkRoute === 0, String(calls.checkRoute));
        ok('no page checks', calls._startPageChecks === 0, String(calls._startPageChecks));
        ok('the head script\'s cover is down', html.getAttribute('data-jpf-cover') === null);
        ok('and the cache it raises the cover from is gone',
            env.sandbox.localStorage.getItem(PP.config.switcherModeKey) == null);
        ok('nothing was fetched', env.posts.length === 0, env.posts.map(p => p.url).join(', '));

        const preview = env.sandbox.window.BonfireThemePreview;
        ok('the settings page still has its theme preview', !!preview && typeof preview.css === 'function');
        const builtIns = preview ? preview.builtIns() : [];
        const css = builtIns.length ? preview.css(builtIns[0]) : '';
        ok('and it still draws a theme', typeof css === 'string' && css.length > 0, builtIns.length + ' built-ins');
    }

    console.log('\n-- On ----------------------------------------------------------');
    {
        const { env, PP, calls } = boot(false);
        PP.init();
        await env.settle();
        ok('init binds events', calls.bindEvents === 1, String(calls.bindEvents));
        ok('injects the styles', calls.injectStyles === 1, String(calls.injectStyles));
        ok('asks for the preferences', calls.loadSwitcherPrefs === 1, String(calls.loadSwitcherPrefs));
        ok('and does not tear down', calls.applyPanicDisable === 0 && PP._panicDisabled !== true);
    }

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.log('  FAIL  harness threw: ' + (e && e.stack || e)); process.exit(1); });
