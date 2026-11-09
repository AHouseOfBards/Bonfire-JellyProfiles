/*
 * Every confirmation on the settings page has to reach the action behind it (#44).
 *
 * jellyfin-web's Dashboard.confirm is confirm(message, title, callback) and returns nothing
 * (src/utils/dashboard.js, the same on release-10.11.z and master). The page called it with
 * two arguments and chained .then on the result. The dialog still appeared, because the
 * chain only throws after it is on screen, so Reset PIN looked as though it worked: OK did
 * nothing, and the same went for every other button that asks first.
 *
 * Runs the page's real helper against a stub that behaves like upstream's, then goes through
 * every call site one at a time.
 *
 *   node tests/js/adminconfirm.js [path/to/profilesDashboard.html]
 */
'use strict';

const fs = require('fs');
const path = require('path');

const FILE = process.argv[2] || path.join(__dirname, '..', '..', 'Web', 'profilesDashboard.html');
const SRC = fs.readFileSync(FILE, 'utf8').replace(/\r\n/g, '\n');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '  — ' + detail : '')); }
}

/** The index just past the bracket that closes the one at `open`, skipping strings. */
function closeOf(text, open) {
    let depth = 0;
    let quote = null;
    for (let i = open; i < text.length; i++) {
        const c = text[i];
        if (quote) {
            if (c === '\\') i++;
            else if (c === quote) quote = null;
        } else if (c === '\'' || c === '"' || c === '`') quote = c;
        else if (c === '(' || c === '{' || c === '[') depth++;
        else if (c === ')' || c === '}' || c === ']') {
            depth--;
            if (depth === 0) return i + 1;
        }
    }
    return -1;
}

/** The arguments between two brackets, split on the commas at the top level. */
function argsOf(inner) {
    const out = [];
    let start = 0;
    let depth = 0;
    let quote = null;
    for (let i = 0; i < inner.length; i++) {
        const c = inner[i];
        if (quote) {
            if (c === '\\') i++;
            else if (c === quote) quote = null;
        } else if (c === '\'' || c === '"' || c === '`') quote = c;
        else if (c === '(' || c === '{' || c === '[') depth++;
        else if (c === ')' || c === '}' || c === ']') depth--;
        else if (c === ',' && depth === 0) { out.push(inner.slice(start, i).trim()); start = i + 1; }
    }
    out.push(inner.slice(start).trim());
    return out;
}

function callsOf(name) {
    const found = [];
    const re = new RegExp('\\b' + name.replace('.', '\\.') + '\\(', 'g');
    let m;
    while ((m = re.exec(SRC))) {
        const open = m.index + m[0].length - 1;
        const end = closeOf(SRC, open);
        found.push({
            line: SRC.slice(0, m.index).split('\n').length,
            args: argsOf(SRC.slice(open + 1, end - 1)),
            after: SRC.slice(end).replace(/^\s+/, '').slice(0, 5)
        });
    }
    return found;
}

// Upstream's, to the letter: the promise is its own, and nothing comes back.
function upstreamConfirm(answer, seen) {
    return function (message, title, callback) {
        seen.push({ message, title });
        Promise.resolve().then(function () {
            if (typeof callback !== 'function') throw new TypeError('callback is not a function');
            callback(answer);
        }).catch(function () { /* upstream's promise is not ours to see */ });
    };
}

const settle = () => new Promise(r => setTimeout(r, 5));

(async () => {
    console.log('\n── The helper, against the real shape of Dashboard.confirm ─────');

    const at = SRC.search(/function askFirst\s*\(/);
    ok('the page has one helper for asking first', at >= 0);

    if (at >= 0) {
        const brace = SRC.indexOf('{', at);
        const source = SRC.slice(at, closeOf(SRC, brace));

        for (const answer of [true, false]) {
            const seen = [];
            const Dashboard = { confirm: upstreamConfirm(answer, seen) };
            const askFirst = new Function('Dashboard', source + '\nreturn askFirst;')(Dashboard);
            let ran = 0;
            let threw = null;
            try {
                askFirst('Clear this PIN?', 'Reset PIN').then(function () { ran++; });
            } catch (e) { threw = e; }
            await settle();
            ok('does not throw when the answer is ' + (answer ? 'OK' : 'Cancel'), !threw, threw && threw.message);
            ok(answer ? 'OK runs the action, once' : 'Cancel runs nothing', ran === (answer ? 1 : 0), 'ran ' + ran);
            ok('message and title reach the dialog',
               seen.length === 1 && seen[0].message === 'Clear this PIN?' && seen[0].title === 'Reset PIN',
               JSON.stringify(seen));
        }
    }

    console.log('\n── Every place the page asks ───────────────────────────────────');

    const direct = callsOf('Dashboard.confirm');
    direct.forEach(c => {
        ok('line ' + c.line + ': Dashboard.confirm is given a callback', c.args.length === 3, c.args.length + ' argument(s)');
        ok('line ' + c.line + ': nothing is chained on what it returns', c.after.indexOf('.then') !== 0, c.after);
    });

    const asks = callsOf('askFirst').filter(c => c.args[0] !== 'message');
    ok('the buttons that ask first still do (at least 3)', asks.length >= 3, asks.length + ' found');
    asks.forEach(c => {
        ok('line ' + c.line + ': the action hangs off askFirst', c.after.indexOf('.then') === 0, c.after);
    });

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
