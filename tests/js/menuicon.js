/*
 * The admin page's entry in the dashboard menu shows the Bonfire flame, not a folder.
 *
 * Jellyfin 12's drawer draws `<Icon>{pageInfo.MenuIcon ?? 'folder'}</Icon>`
 * (jellyfin-web src/apps/dashboard/components/drawer/sections/PluginDrawerSection.tsx), a
 * ligature from material-design-icons-iconfont 6.7.0. 10.11's drawer always draws a folder,
 * so there it changes nothing. "local_fire_department" is in that font's ligature list
 * (dist/fonts/MaterialIcons-Regular.json, checked 2026-11-14); a name that is not shows as text.
 *
 *     node tests/js/menuicon.js /path/to/old/Plugin.cs
 */
'use strict';
const fs = require('fs');
const path = require('path');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const file = process.argv[2] || path.join(__dirname, '..', '..', 'Configuration', 'Plugin.cs');
const src = fs.readFileSync(file, 'utf8');

// Every `new PluginPageInfo { ... }` initializer, one at a time.
const pages = [];
const re = /new\s+PluginPageInfo\s*\{([^}]*)\}/g;
let m;
while ((m = re.exec(src))) pages.push(m[1]);

ok('there is exactly one plugin page', pages.length === 1, pages.length + ' found');
pages.forEach((body, i) => {
    const name = (/\bName\s*=\s*"([^"]*)"/.exec(body) || [])[1];
    const icon = (/\bMenuIcon\s*=\s*"([^"]*)"/.exec(body) || [])[1];
    ok('page ' + (name || i) + ' is in the main menu', /\bEnableInMainMenu\s*=\s*true\b/.test(body));
    ok('page ' + (name || i) + ' uses the flame icon', icon === 'local_fire_department', icon === undefined ? 'no MenuIcon' : icon);
});

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
