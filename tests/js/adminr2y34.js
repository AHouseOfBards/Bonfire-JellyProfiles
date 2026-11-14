/*
 * r2-34-01: the email-needed note said to set email up under Notices, but it is in the Setup
 * tab. Every language must point at the tab the language itself calls Setup (the first six
 * letters are compared, which lets a case ending differ).
 *
 *     node tests/js/adminr2y34.js "" /path/to/old/profilesDashboard.html
 */
'use strict';
const fs = require('fs');
const path = require('path');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const html = L.readDashboard();
const en = /'admin\.email\.needsEmail': '([^']*)'/.exec(html);
const tab = /data-i18n="admin\.tab\.setup">([^<]*)</.exec(html);
ok('English names the Setup tab', !!en && !!tab && en[1].indexOf(tab[1]) !== -1, en ? en[1] : 'no key');
const dir = L.i18nDir();
fs.readdirSync(dir).filter(f => /^[a-z]{2,3}(-[A-Za-z]+)?\.json$/.test(f)).forEach(f => {
    const j = JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8'));
    const stem = String(j['admin.tab.setup'] || '').slice(0, 6);
    ok(f + ' names its Setup tab', stem.length > 0 && String(j['admin.email.needsEmail'] || '').indexOf(stem) !== -1, j['admin.email.needsEmail']);
});
console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
