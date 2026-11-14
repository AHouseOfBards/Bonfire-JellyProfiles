'use strict';
// While Bonfire is off, index.html carries no tag for profiles.js (WebInjection.SyncRegistration),
// but the settings page's theme previews run on window.BonfireThemePreview from that script.
// The dashboard loads it itself, once, and only when no tag brought it.
//
//   node tests/js/dashloader.js [path/to/profilesDashboard.html]

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const file = process.argv[2] || path.join(__dirname, '..', '..', 'Web', 'profilesDashboard.html');
const html = fs.readFileSync(file, 'utf8');

let pass = 0, fail = 0;
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; console.log('  FAIL  ' + name + (detail !== undefined ? '  - ' + detail : '')); }
}

// The loader is the first try block of the page's script.
const m = /try \{\s*(?:\/\/[^\n]*\n\s*)*if \(!window\.__jpLoaded[\s\S]*?\n\s*\} catch \(e\) \{[^}]*\}/.exec(html);
ok('the dashboard has a loader for profiles.js', !!m);

function run(win) {
    const added = [];
    const byId = {};
    const document = {
        createElement: () => ({}),
        getElementById: id => byId[id] || null,
        head: { appendChild: n => { added.push(n); if (n.id) byId[n.id] = n; } }
    };
    const ctx = Object.assign({ window: win, document }, win.ApiClient ? { ApiClient: win.ApiClient } : {});
    vm.createContext(ctx);
    const again = () => vm.runInContext(m[0], ctx);
    again();
    return { added, again };
}

if (m) {
    const api = { getUrl: p => '/base/' + p };

    const off = run({ ApiClient: api });
    ok('off (no tag brought it): it adds one script tag', off.added.length === 1, off.added.length);
    ok('for the plugin script, through ApiClient.getUrl', off.added[0] && off.added[0].src === '/base/plugins/profiles/profiles.js', off.added[0] && off.added[0].src);
    ok('loaded async, so it does not hold the page', off.added[0] && off.added[0].async === true);
    off.again();
    ok('coming back before it has loaded adds no second copy', off.added.length === 1, off.added.length);

    const on = run({ ApiClient: api, __jpLoaded: 1 });
    ok('on (the tag already ran it): nothing is added', on.added.length === 0, on.added.length);

    const preview = run({ ApiClient: api, BonfireThemePreview: {} });
    ok('a page that already has the preview adds nothing', preview.added.length === 0, preview.added.length);

    const noApi = run({});
    ok('with no ApiClient it adds nothing and does not throw', noApi.added.length === 0);
}

console.log(pass + ' passed, ' + fail + ' failed');
process.exit(fail ? 1 : 0);
