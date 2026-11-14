/*
 * docs/openapi.json has to still describe the API.
 *
 * The file is written by hand, because Jellyfin's own Swagger reads XML docs only from
 * its own folder and many of these routes build their JSON by hand. That makes it a second
 * description of every route, next to docs/developer-api.md, and a second description
 * rots exactly as the first one did before apidocs.js. This matches it to the controller
 * pairwise, route by route. Not counts: a count stays green while one route is swapped
 * for another.
 *
 * It fails on:
 *   - a route in code that the file lacks, and a path in the file with no route;
 *   - a method on a path that the code does not have (and the reverse);
 *   - an `x-bonfire-auth` that differs from what the code enforces, read by the same
 *     extractor apidocs.js uses (_routes.js), and `security: []` on anything but an
 *     anonymous route;
 *   - an operation with no summary, no operationId, or no response, or a duplicate
 *     operationId;
 *   - a `{parameter}` in a path that the operation does not declare;
 *   - a `$ref` that points at nothing;
 *   - the ReDoc page not loading a pinned build of ReDoc, or not pointing at the file.
 *
 *   node tests/js/openapi.js [path to another checkout]
 */
'use strict';

const fs = require('fs');
const path = require('path');
const R = require('./_routes');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else {
        fails.push(name + (detail ? '  — ' + detail : ''));
        console.log('  FAIL  ' + name + (detail ? '  — ' + detail : ''));
    }
}
const list = a => a.slice(0, 8).join('; ') + (a.length > 8 ? '; … ' + (a.length - 8) + ' more' : '');

const code = R.routes();
const SPEC_PATH = path.join(R.ROOT, 'docs', 'openapi.json');
const METHODS = ['get', 'post', 'put', 'delete', 'patch'];
const LEVEL = { admin: 'admin', user: 'user', anonymous: 'anon' };

console.log('── the file ───────────────────────────────────────────────────');
ok('docs/openapi.json exists', fs.existsSync(SPEC_PATH));
let spec = null;
if (fs.existsSync(SPEC_PATH)) {
    try { spec = JSON.parse(fs.readFileSync(SPEC_PATH, 'utf8')); ok('it parses', true); }
    catch (e) { ok('it parses', false, e.message); }
}

if (spec) {
    ok('it is OpenAPI 3.1', /^3\.1\.\d+$/.test(String(spec.openapi)), String(spec.openapi));
    ok('it has a title and a version', !!(spec.info && spec.info.title && spec.info.version));

    // The server URL carries the prefix, so paths in the file are what the controller's
    // attribute says, without it.
    const server = spec.servers && spec.servers[0] && spec.servers[0].url || '';
    ok('the server URL ends in ' + R.PREFIX, server.replace(/\/$/, '').endsWith(R.PREFIX), server);

    console.log();
    console.log('── every route, matched pairwise ──────────────────────────────');
    const specOps = new Map();
    for (const [p, item] of Object.entries(spec.paths || {})) {
        for (const m of Object.keys(item)) {
            if (METHODS.includes(m)) specOps.set(m.toUpperCase() + ' ' + R.PREFIX + (p === '/' ? '' : p), { path: p, item, op: item[m] });
        }
    }
    const codeKeys = new Set(code.map(r => r.method + ' ' + r.path));
    const missing = code.filter(r => !specOps.has(r.method + ' ' + r.path));
    ok('no route is missing from the file (' + code.length + ' in code)', missing.length === 0,
       list(missing.map(r => r.method + ' ' + r.path + '  (' + r.name + ')')));
    const ghosts = [...specOps.keys()].filter(k => !codeKeys.has(k));
    ok('no operation in the file is a ghost', ghosts.length === 0, list(ghosts));
    // A path whose method differs is both of the above; say it once more, plainly.
    const codePaths = new Set(code.map(r => r.path));
    const wrongMethod = ghosts.filter(k => codePaths.has(k.slice(k.indexOf(' ') + 1)));
    ok('no path carries a method the code does not', wrongMethod.length === 0, list(wrongMethod));

    console.log();
    console.log('── authorization, as written and as enforced ──────────────────');
    const badAuth = [], badSec = [];
    for (const r of code) {
        const e = specOps.get(r.method + ' ' + r.path);
        if (!e) continue;
        const want = LEVEL[r.level];
        const got = e.op['x-bonfire-auth'];
        if (got !== want) badAuth.push(r.method + ' ' + r.path + ': file says ' + got + ', code enforces ' + want);
        const sec = e.op.security;
        const open = Array.isArray(sec) && sec.length === 0;
        if (want === 'anon' && !open) badSec.push(r.method + ' ' + r.path + ': anonymous without security: []');
        if (want !== 'anon' && (open || (sec === undefined && !(spec.security && spec.security.length))))
            badSec.push(r.method + ' ' + r.path + ': ' + want + ' but no security requirement');
    }
    ok('every x-bonfire-auth is the one the code enforces', badAuth.length === 0, list(badAuth));
    ok('security: [] is on the anonymous routes and only there', badSec.length === 0, list(badSec));

    console.log();
    console.log('── every operation is complete ────────────────────────────────');
    const noSummary = [], noId = [], noResp = [], noParam = [];
    const ids = new Map();
    for (const [key, e] of specOps) {
        const op = e.op;
        if (!op.summary || !String(op.summary).trim()) noSummary.push(key);
        if (!op.operationId) noId.push(key);
        else ids.set(op.operationId, (ids.get(op.operationId) || []).concat(key));
        if (!op.responses || Object.keys(op.responses).length === 0) noResp.push(key);
        const declared = new Set([].concat(e.item.parameters || [], op.parameters || [])
            .map(p => resolve(p)).filter(p => p && p.in === 'path').map(p => p.name));
        const used = (e.path.match(/\{(\w+)\}/g) || []).map(s => s.slice(1, -1));
        const lacking = used.filter(u => !declared.has(u));
        if (lacking.length) noParam.push(key + ' (' + lacking.join(', ') + ')');
    }
    const dupIds = [...ids].filter(([, v]) => v.length > 1).map(([k, v]) => k + ' on ' + v.join(' and '));
    ok('every operation has a summary', noSummary.length === 0, list(noSummary));
    ok('every operation has an operationId', noId.length === 0, list(noId));
    ok('and no operationId is used twice', dupIds.length === 0, list(dupIds));
    ok('every operation has a response', noResp.length === 0, list(noResp));
    ok('every {parameter} in a path is declared', noParam.length === 0, list(noParam));

    console.log();
    console.log('── every $ref resolves ────────────────────────────────────────');
    function pointer(ref) {
        if (!ref.startsWith('#/')) return undefined;
        let node = spec;
        for (const seg of ref.slice(2).split('/')) {
            node = node && node[seg.replace(/~1/g, '/').replace(/~0/g, '~')];
        }
        return node;
    }
    function resolve(x) { return x && x.$ref ? pointer(x.$ref) : x; }
    const refs = [];
    (function walk(n) {
        if (Array.isArray(n)) n.forEach(walk);
        else if (n && typeof n === 'object') {
            for (const [k, v] of Object.entries(n)) {
                if (k === '$ref' && typeof v === 'string') refs.push(v);
                else walk(v);
            }
        }
    })(spec);
    const dead = [...new Set(refs.filter(r => pointer(r) === undefined))];
    ok('there are references to resolve (' + refs.length + ')', refs.length > 0);
    ok('none points at nothing', dead.length === 0, list(dead));

    console.log();
    console.log('── no shape is left as a bare object ──────────────────────────');
    // A schema that says `type: object` and nothing else tells a client nothing, and it is
    // what a shape looks like before anyone reads the code that builds it. A shape that is
    // too internal to promise says so with x-bonfire-not-api and a description that ends in
    // "Not part of the API.", which is a decision written down and not a gap.
    // UNTYPED lists schema paths that may stay bare, and is empty: a new one is a gap.
    const UNTYPED = [];
    const bare = [], badNotApi = [];
    (function walk(n, p) {
        if (Array.isArray(n)) { n.forEach((x, i) => walk(x, p + '/' + i)); return; }
        if (!n || typeof n !== 'object') return;
        const types = [].concat(n.type === undefined ? [] : n.type);
        if (types.includes('object') && !n.properties && !n.$ref && !n.allOf && !n.oneOf && !n.anyOf
            && !(n.additionalProperties && typeof n.additionalProperties === 'object')) {
            if (n['x-bonfire-not-api'] === true) {
                if (!/Not part of the API\.\s*$/.test(String(n.description || ''))) badNotApi.push(p);
            } else if (!UNTYPED.includes(p)) bare.push(p);
        }
        for (const [k, v] of Object.entries(n)) if (k !== 'example' && k !== 'examples') walk(v, p + '/' + k);
    })(spec, '');
    ok('every object schema has properties, or a reference, or says it is not part of the API', bare.length === 0, list(bare));
    ok('and each one that says so ends its description with "Not part of the API."', badNotApi.length === 0, list(badNotApi));
}

console.log();
console.log('── the reference page ─────────────────────────────────────────');
{
    const html = path.join(R.ROOT, 'docs', 'api.html');
    const text = fs.existsSync(html) ? fs.readFileSync(html, 'utf8') : '';
    ok('docs/api.html exists', text !== '');
    ok('it has front matter, so the site lists it', /^---\r?\n[\s\S]*?\btitle:\s*API reference\b[\s\S]*?\n---/.test(text));
    // An unpinned CDN URL is a script that can change under the page. The version is exact.
    const m = /cdn\.jsdelivr\.net\/npm\/redoc@(\d+\.\d+\.\d+)\/bundles\/redoc\.standalone\.js/.exec(text);
    ok('it loads ReDoc at an exact version from jsDelivr', !!m, m ? m[1] : '');
    // An exact version is still a file the CDN can change; the hash is what refuses it (r3-04-03).
    const tag = (/<script\b[^>]*redoc\.standalone\.js[^>]*>/.exec(text) || [''])[0];
    ok('and only runs the file whose hash it names', /\bintegrity="sha(384|512)-[A-Za-z0-9+/=]{40,}"/.test(tag) && /\bcrossorigin="anonymous"/.test(tag), tag);
    ok('and points it at openapi.json', /openapi\.json/.test(text));
    const dev = fs.readFileSync(path.join(R.ROOT, 'docs', 'developer-api.md'), 'utf8');
    const readme = fs.readFileSync(path.join(R.ROOT, 'README.md'), 'utf8');
    ok('developer-api.md links to it', /\]\((?:\.\/)?api\.html\)/.test(dev));
    ok('the README links to it', /\]\(docs\/api\.html\)/.test(readme));
}

console.log();
console.log('  ' + pass + ' passed, ' + fails.length + ' failed');
if (fails.length) {
    console.log();
    for (const f of fails) console.log('   FAILED: ' + f);
    process.exit(1);
}
