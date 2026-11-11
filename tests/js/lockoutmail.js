// The email sent after too many wrong PINs says what happened and when they can try
// again, never that a PIN "was locked" (phase 3 wording, 2026-11-10). The client says
// "Too many wrong PINs. Try again in N minutes."; the email follows it in every language.
//
// node tests/js/lockoutmail.js [path/to/repo]
'use strict';
const fs = require('fs');
const path = require('path');
const root = process.argv[2] || path.join(__dirname, '..', '..');
let pass = 0, fail = 0;
function ok(name, cond, got) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fail++; console.log('  FAIL  ' + name + (got !== undefined ? '  — ' + got : '')); }
}

const cs = fs.readFileSync(path.join(root, 'Mail', 'EmailText.cs'), 'utf8');
function en(key) {
    const m = new RegExp('\\["' + key.replace(/\./g, '\\.') + '"\\]\\s*=\\s*"((?:[^"\\\\]|\\\\.)*)"').exec(cs);
    return m ? m[1] : null;
}
const subj = en('email.lockout.subject');
const body = en('email.lockout.body');
ok('English subject found', subj !== null);
ok('English subject says too many wrong PINs', !!subj && /Too many wrong PINs/.test(subj), subj);
ok('English subject never says lock', !!subj && !/lock/i.test(subj), subj);
ok('English body says when to try again', !!body && /try again in 15 minutes/.test(body), body);
ok('English body never says lock', !!body && !/lock/i.test(body), body);

// The word each language used for "locked" before; none may come back.
const OLD = { ca: /bloque/i, es: /bloque/i, fr: /bloqu/i, pl: /zablok/i, tr: /kilit/i };
Object.keys(OLD).forEach(lang => {
    const j = JSON.parse(fs.readFileSync(path.join(root, 'Web', 'i18n', lang + '.json'), 'utf8'));
    ['email.lockout.subject', 'email.lockout.body'].forEach(k => {
        const v = j[k];
        ok(lang + ' ' + k + ' is present', typeof v === 'string' && v.length > 0);
        ok(lang + ' ' + k + ' never says locked', typeof v === 'string' && !OLD[lang].test(v), v);
    });
    ok(lang + ' body keeps {name} and the 15', /\{name\}/.test(j['email.lockout.body'] || '') && /15/.test(j['email.lockout.body'] || ''));
});

console.log('\n' + pass + ' passed, ' + fail + ' failed');
process.exit(fail ? 1 : 0);
