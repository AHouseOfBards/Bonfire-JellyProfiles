/*
 * The API reference has to still describe the API.
 *
 * docs/developer-api.md is the contract other people build against, and nothing has
 * ever checked it against the controller. The plan says "41 routes"; there are 42 —
 * P2 added POST admin/settings and the count in the plan, in the working notes and in the
 * document itself all drifted apart without anyone noticing. That is the whole failure
 * mode: documentation rots silently, and the only signal is a confused reader.
 *
 * So this enumerates both sides and matches them pairwise. Not counts — a count going
 * green while one route was swapped for another is exactly the aggregate mistake the
 * house rules warn about.
 *
 * It checks three things:
 *   1. every route in the controller is documented, and every documented route exists;
 *   2. every route states its authorisation level, in a fixed place;
 *   3. the level the document claims is the level the code actually enforces.
 *
 * (3) is the one worth having. A route documented as admin-only that in fact only
 * checks for a signed-in user is a security bug wearing a documentation bug's clothes,
 * and this is a controller with a class-level [AllowAnonymous] and hand-rolled checks —
 * a new route without an explicit check is silently public.
 *
 *   node tests/js/apidocs.js
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

// The extractor lives in _routes.js, shared with openapi.js.
const { PREFIX, controllerFiles, routes } = require('./_routes');
const doc = fs.readFileSync(path.join(L.ROOT, 'docs', 'developer-api.md'), 'utf8');


const R = routes();

{
    const stray = [];
    (function walk(dir) {
        for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
            const full = path.join(dir, e.name);
            if (e.isDirectory()) {
                // .claude holds other checkouts of the repo, half-edited, with routes of their own.
                if (!/^(bin|obj|tests|node_modules|\.git|\.claude)$/.test(e.name)) walk(full);
            } else if (e.name.endsWith('.cs') && !controllerFiles.includes(e.name)
                       && /\[Http(Get|Post|Put|Delete|Patch)\b/.test(fs.readFileSync(full, 'utf8'))) {
                stray.push(path.relative(L.ROOT, full));
            }
        }
    })(L.ROOT);
    ok('every route is in a ProfilesController*.cs file this reads (' + controllerFiles.join(', ') + ')',
       stray.length === 0, stray.join(', '));
}

console.log();
console.log('── the controller ─────────────────────────────────────────────');
ok('routes found', R.length > 30, R.length + ' routes');
const dupes = R.map(r => r.method + ' ' + r.path)
    .filter((v, i, a) => a.indexOf(v) !== i);
ok('no two routes share a method and path', dupes.length === 0, dupes.join(', '));

const byLevel = R.reduce((a, r) => { a[r.level] = (a[r.level] || 0) + 1; return a; }, {});
console.log('        admin ' + (byLevel.admin || 0)
    + ' · user ' + (byLevel.user || 0)
    + ' · anonymous ' + (byLevel.anonymous || 0));

/* ── the routes the document describes ───────────────────────────────────────── */

/* Headings of the form `### GET /plugins/profiles/...`, and the route index table. */
const documented = new Map();
const headingRe = /^#{2,4}\s+`?(GET|POST|PUT|DELETE|PATCH)\s+(\/plugins\/profiles[^\s`]*)`?/gm;
let d;
while ((d = headingRe.exec(doc)) !== null) {
    documented.set(d[1] + ' ' + d[2], d.index);
}

console.log();
console.log('── every route is documented, and every entry is real ─────────');
ok('the document describes some routes', documented.size > 30, documented.size + ' entries');

const missing = R.filter(r => !documented.has(r.method + ' ' + r.path));
ok('no route is undocumented', missing.length === 0,
    missing.map(r => r.method + ' ' + r.path + '  (' + r.name + ')').join('; '));

const codePaths = new Set(R.map(r => r.method + ' ' + r.path));
const ghosts = [...documented.keys()].filter(k => !codePaths.has(k));
ok('no documented route is a ghost', ghosts.length === 0, ghosts.join('; '));

/* The anonymous set, matched pairwise against the table that promises what it is.
 *
 * This is the assertion worth having in the whole file. The controller carries a
 * class-level [AllowAnonymous] and every check is hand-rolled, so a new route with no
 * check is silently public — and the only place that fact is written down is a table a
 * human maintains. If the two disagree, either a route lost its check or the table is
 * lying to integrators about what is safe to expose. */
const anonTable = new Set();
{
    const sec = doc.slice(doc.indexOf('| Endpoint | Why it is anonymous |'));
    const rows = sec.slice(0, sec.indexOf('\n\n')).split('\n');
    for (const row of rows) {
        const m = /^\|\s*`(GET|POST|PUT|DELETE|PATCH)\s+(\/[^`]*)`/.exec(row.trim());
        if (m) anonTable.add(m[1] + ' ' + PREFIX + m[2]);
    }
}
const anonCode = new Set(R.filter(r => r.level === 'anonymous').map(r => r.method + ' ' + r.path));
const undocumentedAnon = [...anonCode].filter(k => !anonTable.has(k));
const overclaimedAnon = [...anonTable].filter(k => !anonCode.has(k));

ok('the anonymous table is not empty', anonTable.size > 0, anonTable.size + ' rows');
ok('every anonymous route is in the table', undocumentedAnon.length === 0,
    undocumentedAnon.join('; ') + '  <- reachable without a token and not declared');
ok('and the table claims no route that is actually gated', overclaimedAnon.length === 0,
    overclaimedAnon.join('; '));

/* ── every entry states its level, and states it correctly ───────────────────── */

console.log();
console.log('── authorisation, as documented and as enforced ───────────────');

/* The level line sits in a fixed slot directly under the heading. A fixed slot is the
 * point: 17 of 42 stated their level somewhere in prose, so for the other 25 a reader
 * had to infer it from a sentence. */
const LEVEL_RE = /^\*\*Authorization:\*\*\s+(administrator|signed-in user|anonymous)\b/im;

let stated = 0;
const unstated = [];
const wrong = [];
const entries = [...documented.entries()].sort((a, b) => a[1] - b[1]);
for (let i = 0; i < entries.length; i++) {
    const [key, at] = entries[i];
    const end = i + 1 < entries.length ? entries[i + 1][1] : doc.length;
    // Only the first few lines under the heading count, so a level mentioned in prose
    // further down does not satisfy this.
    const head = doc.slice(at, Math.min(end, at + 400));
    const m = LEVEL_RE.exec(head);
    if (!m) { unstated.push(key); continue; }
    stated++;
    const claimed = { 'administrator': 'admin', 'signed-in user': 'user', 'anonymous': 'anonymous' }[m[1].toLowerCase()];
    const real = R.find(r => r.method + ' ' + r.path === key);
    if (real && real.level !== claimed) {
        wrong.push(key + ': documented ' + claimed + ', enforced ' + real.level);
    }
}

ok('every entry states its authorisation level', unstated.length === 0,
    unstated.length ? unstated.length + ' without one: ' + unstated.slice(0, 6).join('; ') : '');
ok('and the stated level is the enforced one', wrong.length === 0, wrong.join('; '));

/* ── the index, and the shape ────────────────────────────────────────────────── */

console.log();
console.log('── the reader-facing structure ────────────────────────────────');

const indexRows = (doc.match(/^\|\s*`?(?:GET|POST|PUT|DELETE|PATCH)\s+\/plugins\/profiles/gm) || []).length;
ok('there is a route index table', indexRows > 30, indexRows + ' rows');
ok('the index lists every route', indexRows >= R.length,
    indexRows + ' rows for ' + R.length + ' routes');

ok('there is a table of contents', /^##\s+Contents/m.test(doc));
ok('there is an error-code table', /\|\s*`?4\d\d`?\s*\|/.test(doc));
ok('rate limits are documented', /rate limit/i.test(doc));
// All three limiters, named individually. "Rate limits are documented" is an aggregate
// and would pass with one of the three described.
for (const limiter of ['Bonfire', 'PIN', 'panic']) {
    ok('  the ' + limiter + ' limiter is in the table',
        new RegExp('\\|[^|\\n]*' + limiter + '[^|\\n]*\\|[^|\\n]*\\d', 'i').test(doc));
}
ok('there is a stability statement', /unknown field|stability|breaking change/i.test(doc));

console.log();
console.log('── entry shape (P7-17, reported, not yet a gate) ──────────────');

/*
 * Reported rather than asserted, deliberately.
 *
 * P7-17 wants every entry to show auth, request, response and errors. Auth is done and
 * IS gated above: all 42, in a fixed slot, cross-checked against the code. The other
 * three are not, and the honest reason is that filling them in means reading each
 * endpoint and writing what it really returns. A gate here would tempt whoever hits it
 * into inventing a response, and a documented response that the endpoint does not
 * return is worse than an acknowledged gap.
 *
 * The counts are printed so the gap stays visible and shrinks on purpose. Turn this
 * into a gate when it reaches zero.
 */
{
    const heads = [...doc.matchAll(/^#{3}\s+`(GET|POST|PUT|DELETE|PATCH) (\/plugins\/profiles[^`]*)`/gm)];
    let noResp = 0, noReq = 0, noErr = 0;
    for (let i = 0; i < heads.length; i++) {
        const a = heads[i].index;
        const b = i + 1 < heads.length ? heads[i + 1].index : doc.length;
        const body = doc.slice(a, b);
        if (!/Response|```json/.test(body)) noResp++;
        if (heads[i][1] !== 'GET' && !/Request|Body|\*\*Headers/.test(body)) noReq++;
        if (!/Error|4\d\d/.test(body)) noErr++;
    }
    console.log('        ' + heads.length + ' entries · '
        + noResp + ' without a response · '
        + noReq + ' without a request · '
        + noErr + ' without per-entry errors');
    console.log('        (status codes and rate limits are covered globally under Errors)');
}

console.log();
console.log('── the docs say true things about the API ─────────────────────');

const readme = fs.readFileSync(path.join(L.ROOT, 'README.md'), 'utf8');

// Both counts used to be asserted against the README. They moved to the API reference
// when the README was trimmed, and they are CHECKED THERE rather than dropped — a
// number in prose that nothing checks is a number that will be wrong, wherever it
// lives. "All 41 endpoints" was in the README while the controller had 42, and neither
// the plan nor the working notes agreed with either.
//
// Asserted against docs/developer-api.md, which is the document that now owns the
// claim. If a count is moved again, move the assertion with it; do not delete it.
const apiClaims = fs.readFileSync(path.join(L.ROOT, 'docs', 'developer-api.md'), 'utf8');

const claimed = /All (\d+) endpoints/.exec(apiClaims);
ok('the API reference names an endpoint count', !!claimed, claimed ? claimed[1] : '');
ok('and it is the real one', !!claimed && Number(claimed[1]) === R.length,
    claimed ? 'the doc says ' + claimed[1] + ', controller has ' + R.length : '');

// Whitespace-tolerant: the file is hard-wrapped, and this phrase can span two lines.
// A single-line regex found nothing and reported the document as silent on a count it
// states plainly — the checker being wrong reads exactly like the document being wrong.
const anonClaim = /(\w+)\s+routes\s+are\s+reachable\s+without\s+a\s+token/.exec(apiClaims);
// A word the table does not know reads as undefined, which never equals a count, so a
// count past the list fails loudly rather than passing; extend it as the set grows.
const WORDS = { five: 5, six: 6, seven: 7, eight: 8, nine: 9, ten: 10, eleven: 11, twelve: 12, thirteen: 13, fourteen: 14 };
ok('the API reference names the anonymous count', !!anonClaim);
ok('and it matches the code',
    !!anonClaim && WORDS[anonClaim[1].toLowerCase()] === anonCode.size,
    anonClaim ? 'the doc says ' + anonClaim[1] + ', code has ' + anonCode.size : '');

// The README no longer restates either count, so it cannot drift from them. If a count
// is put back into it, it has to be a real one.
const readmeCount = /All (\d+) endpoints/.exec(readme);
ok('the README does not restate a stale endpoint count',
    readmeCount === null || Number(readmeCount[1]) === R.length);

// The profile limit is configurable with per-user overrides; "up to five profiles"
// described a default as though it were a cap, in two places.
ok('the README does not present the default limit as a cap',
    !/up to five profiles/i.test(readme));

// P7-1 and P7-2 moved two blocks out of the README. A warning that is moved to a file
// that does not carry it has been deleted, whatever the commit says.
for (const [file, needle, what] of [
    ['docs/beta-channel.md', 'Beta builds are unfinished', 'the beta risk warning'],
    ['docs/troubleshooting.md', 'Bonfire does not edit `index.html`.', 'the injection headline'],
]) {
    const p = path.join(L.ROOT, file);
    ok(file + ' exists', fs.existsSync(p));
    ok('  and still carries ' + what,
        fs.existsSync(p) && fs.readFileSync(p, 'utf8').indexOf(needle) !== -1);
    ok('  and the README links to it', readme.indexOf('(' + file + ')') !== -1);
}

// The Stability section promises that a field left out keeps its stored value (R-1144,
// R-1159, R-1326). It used to carve out an exception, and three routes broke the promise
// without saying so: their request types had plain bool and int fields, which read a
// left-out field as false or 0. So the promise is checked against the types, field by
// field: on the routes that change stored settings, a value-type field must be nullable,
// or it cannot tell "left out" from "off". ProfileId is the route's key, not a setting.
const stability = (doc.split('## Stability')[1] || '').split('\n---')[0];
ok('Stability promises a left-out field keeps its value', /leave out[\s\S]{0,40}keep their stored value/.test(stability));
ok('  and names no exception to it', !/exception|unless the field/i.test(stability));
for (const [file, cls] of [
    ['UpdateProfileRequest.cs', 'UpdateProfileRequest'],
    ['UpdateBonfireSettingsRequest.cs', 'UpdateBonfireSettingsRequest'],
    ['ViewingRequests.cs', 'SetViewingRulesRequest'],
]) {
    const src = fs.readFileSync(path.join(L.ROOT, 'Models', file), 'utf8');
    const start = src.indexOf('class ' + cls);
    const body = start === -1 ? '' : src.slice(start, src.indexOf('\n    }', start));
    ok(cls + ' is where it was', start !== -1);
    // Only what JSON can set: `{ get; private set; }` (WeekendMinutesSent) is bookkeeping.
    const re = /public\s+(bool|int|long|double|decimal|DateTime)\s+(\w+)\s*\{\s*get;\s*set;/g;
    const plain = [];
    let m;
    while ((m = re.exec(body))) if (m[2] !== 'ProfileId') plain.push(m[1] + ' ' + m[2]);
    ok('  ' + cls + ': every value-type setting can be left out', plain.length === 0,
        plain.join(', ') + ' — make it nullable');
}

console.log();
console.log('  ' + pass + ' passed, ' + fails.length + ' failed');
if (fails.length) {
    console.log();
    for (const f of fails) console.log('   FAILED: ' + f);
    process.exit(1);
}
