/*
 * Every route that asks for the main PIN has a client that sends it.
 *
 * mainpin.js checks nine fetch sites it was told about. The controller guards 30
 * routes with ManagementAllowed, so a route could be added (or its client call moved)
 * without any check noticing, and the first sign was a household's save failing as a
 * missing PIN. Here the list comes from the controller, the way apidocs.js reads the
 * routes:
 *
 *   - Each route that calls ManagementAllowed must have at least one client call, or an
 *     exemption below that gives the reason. An exemption nothing uses fails, so the
 *     list cannot go stale.
 *   - Every client call to such a route must send the PIN: in the body (masterPin) where
 *     the route reads one, otherwise in the header (manageHeaders). A route that reads
 *     only the header is not satisfied by a body field.
 *   - Calls through a helper are checked at the helper (what it sends) and at each call
 *     (which route, and which body it passes).
 *
 *   node tests/js/mainpinroutes.js [path/to/profiles.js]
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else {
        fails.push(name + (detail ? '  — ' + detail : ''));
        console.log('  FAIL  ' + name + (detail ? '  — ' + detail : ''));
    }
}

const SRC_PATH = process.argv[2] || L.profilesPath();
const raw = fs.readFileSync(SRC_PATH, 'utf8');
const CTRL_DIR = path.resolve(path.dirname(SRC_PATH), '..', 'Controllers');

// ── The routes, from the controller ───────────────────────────────────────
function guardedRoutes() {
    const files = fs.readdirSync(CTRL_DIR).filter(f => /^ProfilesController(\.\w+)?\.cs$/.test(f)).sort();
    const cs = files.map(f => fs.readFileSync(path.join(CTRL_DIR, f), 'utf8')).join('\n');
    const out = [];
    const re = /\[Http(Get|Post|Put|Delete|Patch)(?:\("([^"]*)"\))?\]/g;
    let m;
    while ((m = re.exec(cs)) !== null) {
        const sigRe = /\n\s*public\s+(?:async\s+)?[^\n]*?\s(\w+)\s*\(/g;
        sigRe.lastIndex = m.index;
        const sig = sigRe.exec(cs);
        const open = cs.indexOf('{', sig.index + sig[0].length - 1);
        let depth = 0, end = cs.length;
        for (let i = open; i < cs.length; i++) {
            if (cs[i] === '{') depth++;
            else if (cs[i] === '}') { depth--; if (depth === 0) { end = i; break; } }
        }
        const body = cs.slice(open, end + 1);
        const mm = /ManagementAllowed\(\s*[^,]+,\s*[^,]+,\s*([^,]+),/.exec(body);
        if (!mm) continue;
        out.push({
            method: m[1].toUpperCase(), route: m[2] || '',
            // null: the route has no body field for it, so only the header can carry it.
            bodyPin: mm[1].trim() !== 'null'
        });
    }
    return out;
}

// ── Reading the client ────────────────────────────────────────────────────
// The text of a balanced (...) or {...} starting at open, quotes skipped.
function balanced(src, open) {
    const pair = { '(': ')', '{': '}' }[src[open]];
    let depth = 0;
    for (let i = open; i < src.length; i++) {
        const c = src[i];
        if (c === "'" || c === '"' || c === '`') {
            for (i++; i < src.length && src[i] !== c; i++) if (src[i] === '\\') i++;
            continue;
        }
        if (c === src[open]) depth++;
        else if (c === pair) { depth--; if (depth === 0) return src.slice(open, i + 1); }
    }
    return src.slice(open);
}
const lineOf = i => raw.slice(0, i).split('\n').length;
const BODY_PIN = /masterPin:\s*this\.masterPin/;
const HEADER_PIN = /this\.manageHeaders\(/;

// A helper's definition: where it starts, and its body.
function defOf(re) {
    const m = re.exec(raw);
    if (!m) return null;
    const open = raw.indexOf('{', raw.indexOf(')', m.index));
    return { at: m.index, text: balanced(raw, open) };
}

// What each helper sends, read from its definition. emailCall's POST puts masterPin in
// the body; its GET sends the header. deviceAction posts with the header.
const HELPERS = {
    _emailCall: {
        def: defOf(/_emailCall:\s*function\s*\(/),
        sends: (def, post) => post ? BODY_PIN.test(def) : HEADER_PIN.test(def),
        // Whether this call is a POST: it passed a body.
        isPost: args => args.length > 1 && args[1].trim() !== 'undefined'
    },
    run: {
        // The email form's local wrapper: run(path, body, done) goes to _emailCall.
        def: (() => {
            const m = /const run = \(path, body, done\) =>/.exec(raw);
            return m && { at: m.index, text: raw.slice(m.index, m.index + 160) };
        })(),
        sends: (def) => /this\._emailCall\(path, body\)/.test(def),
        isPost: () => true,
        // It is a call to _emailCall and is judged as one.
        viaHelper: '_emailCall'
    },
    deviceAction: {
        def: defOf(/const deviceAction = \(path, body, failKey\) =>/),
        sends: (def) => HEADER_PIN.test(def) && /method: 'POST'/.test(def),
        isPost: () => true,
        prefix: 'devices/'
    },
    _limitsCall: {
        def: defOf(/_limitsCall:\s*function\s*\(/),
        // The helper passes the body through; the PIN is judged at each call.
        sends: () => true,
        isPost: args => args.length > 2 && args[2].trim() !== 'null',
        perCall: true,
        template: /^limits\/\{profileId\}/
    }
};

// Split a call's argument text on top-level commas.
function splitArgs(text) {
    const inner = text.slice(1, -1);
    const args = []; let depth = 0, cur = '';
    for (let i = 0; i < inner.length; i++) {
        const c = inner[i];
        if (c === "'" || c === '"' || c === '`') {
            let j = i + 1;
            for (; j < inner.length && inner[j] !== c; j++) if (inner[j] === '\\') j++;
            cur += inner.slice(i, j + 1); i = j; continue;
        }
        if (c === '(' || c === '{' || c === '[') depth++;
        if (c === ')' || c === '}' || c === ']') depth--;
        if (c === ',' && depth === 0) { args.push(cur); cur = ''; } else cur += c;
    }
    args.push(cur);
    return args;
}

// Every client call to one route. Each is { line, how, sends }.
function clientCalls(r) {
    const calls = [];
    const lit = r.route.split('{')[0].replace(/\/$/, '');
    const isTemplate = r.route.indexOf('{') !== -1;

    // Direct fetch of plugins/profiles/<route> followed by a closing quote.
    if (!isTemplate) {
        const needle = 'plugins/profiles/' + lit;
        let i = -1;
        while ((i = raw.indexOf(needle, i + 1)) !== -1) {
            const q = raw[i + needle.length];
            if (q !== "'" && q !== '"') continue;
            // The fetch around the URL, or the next one when the URL is put in a
            // variable first (const url = ...getUrl(...); fetch(url, {).
            let start = raw.lastIndexOf('fetch(', i);
            if (start === -1 || i - start > 80) {
                start = raw.indexOf('fetch(', i);
                if (start === -1 || start - i > 120) continue;
            }
            const call = balanced(raw, start + 5);
            let opts = call;
            // fetch(url, init): the options were built just above.
            if (/,\s*init\s*\)$/.test(call)) {
                const built = raw.lastIndexOf('const init = {', start);
                opts = start - built < 700 ? raw.slice(built, start) + call : call;
            }
            const method = /method:\s*['"](\w+)['"]/.exec(opts);
            if ((method ? method[1].toUpperCase() : 'GET') !== r.method) continue;
            calls.push({
                line: lineOf(i), how: 'fetch', text: opts,
                sends: r.bodyPin ? BODY_PIN.test(opts) || HEADER_PIN.test(opts) : HEADER_PIN.test(opts)
            });
        }
    }

    // Through a helper: helper('<route>', ...), or helper(id, 'path', body) for a template.
    Object.keys(HELPERS).forEach(name => {
        const H = HELPERS[name];
        const re = new RegExp('\\b' + name + '\\(');
        let i = -1;
        const all = new RegExp(re.source, 'g');
        let m;
        while ((m = all.exec(raw)) !== null) {
            i = m.index;
            // The definition itself is not a call.
            if (H.def && i >= H.def.at - 40 && i <= H.def.at + 60) continue;
            if (/(?:const |:\s*function\s*)$/.test(raw.slice(Math.max(0, i - 20), i)) || /const $/.test(raw.slice(i - 6, i))) continue;
            const text = balanced(raw, i + name.length);
            const args = splitArgs(text);
            let routeArg, post;
            if (H.template) {
                // (profileId, path, body)
                const p = /^\s*'([^']*)'\s*$/.exec(args[1] || '');
                if (!H.template.test(r.route)) continue;
                const sub = p ? p[1] : null;
                const want = r.route.replace(/^limits\/\{profileId\}\/?/, '');
                if (sub === null || sub !== want) continue;
                post = H.isPost(args);
                if ((post ? 'POST' : 'GET') !== r.method) continue;
                calls.push({
                    line: lineOf(i), how: name, text,
                    sends: BODY_PIN.test(args.slice(2).join(',')) || /masterPin: this\.masterPin/.test(args.slice(2).join(','))
                });
                continue;
            }
            const s = /^\s*'([^']*)'\s*$/.exec(args[0] || '');
            if (!s) continue;
            routeArg = s[1].split('?')[0];
            const want = (H.prefix || '') + routeArg;
            if (want !== r.route) continue;
            post = H.isPost(args);
            if ((post ? 'POST' : 'GET') !== r.method) continue;
            const via = H.viaHelper ? HELPERS[H.viaHelper] : H;
            const defText = H.viaHelper ? via.def.text : H.def.text;
            let sends = H.def ? H.sends(H.def.text, post) : false;
            if (H.viaHelper) sends = sends && via.sends(via.def.text, true);
            // A body route is satisfied by either; a header-only route by the header.
            if (!r.bodyPin) sends = sends && (H.viaHelper ? HEADER_PIN.test(defText) || false : HEADER_PIN.test(defText));
            calls.push({ line: lineOf(i), how: name, text, sends });
        }
    });
    return calls;
}

// ── What is allowed not to send it ────────────────────────────────────────
// A route, or one call to it, that is allowed to send no PIN, and why. Each must match
// something, or the check fails: an exemption nothing needs is a hole left open.
const EXEMPT_ROUTES = {
    'account/verify-pin':
        'it is the PIN check itself: the body carries the PIN being checked, as pin'
};
const EXEMPT_CALLS = [
    {
        route: 'account', contains: 'JSON.stringify({ pin: pin })',
        reason: 'setting the first main PIN: ManagementAllowed is called with settingOwnPin, and there is none to send yet'
    }
];

// ── Run ───────────────────────────────────────────────────────────────────
console.log('\n── Routes that ask for the main PIN, from the controller ───────');
const routes = guardedRoutes();
ok('the controller was read', routes.length >= 25, routes.length + ' routes');
ok('no route is listed twice', new Set(routes.map(r => r.method + ' ' + r.route)).size === routes.length);
ok('the helpers were found', Object.keys(HELPERS).every(k => HELPERS[k].def), Object.keys(HELPERS).filter(k => !HELPERS[k].def).join(', '));

const usedRouteEx = {}, usedCallEx = new Array(EXEMPT_CALLS.length).fill(false);
let bodyN = 0, headerN = 0;
routes.forEach(r => {
    if (r.bodyPin) bodyN++; else headerN++;
    const label = r.method + ' ' + r.route + (r.bodyPin ? '' : ' (header only)');
    if (Object.prototype.hasOwnProperty.call(EXEMPT_ROUTES, r.route)) {
        usedRouteEx[r.route] = true;
        ok(label + ' is exempt: ' + EXEMPT_ROUTES[r.route], true);
        return;
    }
    const calls = clientCalls(r);
    if (!calls.length) { ok(label + ' has a client call', false, 'none found: add the call, or an exemption with a reason'); return; }
    calls.forEach(c => {
        const exIdx = EXEMPT_CALLS.findIndex(e => e.route === r.route && c.text.indexOf(e.contains) !== -1);
        if (exIdx !== -1) {
            usedCallEx[exIdx] = true;
            ok(label + ' at line ' + c.line + ' is exempt: ' + EXEMPT_CALLS[exIdx].reason, true);
            return;
        }
        ok(label + ' sends the PIN at line ' + c.line + ' (' + c.how + ')', c.sends,
           c.text.replace(/\s+/g, ' ').slice(0, 160));
    });
});

console.log('\n── The exemptions are all in use ───────────────────────────────');
Object.keys(EXEMPT_ROUTES).forEach(k => ok('exempt route ' + k + ' is still a guarded route', usedRouteEx[k] === true));
EXEMPT_CALLS.forEach((e, i) => ok('exempt call to ' + e.route + ' still exists', usedCallEx[i]));
// The two kinds the plan counted, so a change in either is noticed.
ok('both kinds of route are present', bodyN > 0 && headerN > 0, bodyN + ' with a body field, ' + headerN + ' header only');

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
