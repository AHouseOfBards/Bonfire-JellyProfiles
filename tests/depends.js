/*
 * The record of what each harness depends on, and the two things done with it.
 *
 *   node tests/depends.js merge <observed-dir> <record> [--cs a,b] [--always x,y]
 *       Fold what tests/record-reads.js observed into the record. A harness that was
 *       observed replaces its old lines; the others are kept. --cs lists C# harnesses
 *       recorded statically (their own directory and every product source, which is what
 *       the built DLL is made of); --always lists harnesses to run on every --changed.
 *   node tests/depends.js select <record> <changed-files> < labels
 *       Print "RUN<TAB>label<TAB>reason" or "SKIP<TAB>label" for each label on stdin.
 *
 * Record format, one line per dependency, sorted, in tests/depends.txt:
 *   <section/name> <path>     a file
 *   <section/name> <path>/    a directory listing: any file directly inside counts
 *   <section/name> <path>/**  a tree: any file beneath counts
 *   <section/name> *          not narrowable: always run
 *
 * A change selects a harness when it is exactly one of its files, is directly inside one
 * of its directories or beneath one of its trees. A harness with no lines at all is
 * selected too: a record that has not heard of a harness must not be a reason to skip it.
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const HEADER = [
    '# What each harness reads, observed by `tests/run.sh --record` (tests/record-reads.js).',
    '# <section/name> <path> | <path>/ (directory listing) | <path>/** (tree) | * (always run).',
    '# `tests/run.sh --changed` runs the harnesses whose lines match a changed file. Do not edit',
    '# by hand: tests/js/changedsafe.js fails when a harness reads a file its lines lack.',
    '# A harness with only "*" has not been recorded yet (Chrome, C# and those that start',
    '# other programs); it runs on every --changed.'
];

// Everything the plugin DLL is built from: every top-level directory that is not tests,
// documentation or tooling, and the project files at the top.
// release-notes/ is read by the release workflow only. Only directories git tracks count:
// an untracked build folder (dist/) made the record look stale on one machine only.
const NOT_PRODUCT = new Set(['tests', 'docs', 'images', '_includes', '.git', '.github', 'node_modules', 'scratch', 'bin', 'obj', 'release-notes']);
function trackedTop(root) {
    try {
        const out = require('child_process').execFileSync('git', ['ls-files'], { cwd: root || ROOT, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
        const set = new Set();
        out.split('\n').forEach(p => { const i = p.indexOf('/'); if (i > 0) set.add(p.slice(0, i)); });
        return set;
    } catch (e) { return null; }
}
function productTrees(root) {
    const out = [];
    const tracked = trackedTop(root);
    for (const e of fs.readdirSync(root || ROOT, { withFileTypes: true })) {
        if (e.isDirectory() && !NOT_PRODUCT.has(e.name) && !e.name.startsWith('.') && (!tracked || tracked.has(e.name))) out.push(e.name + '/**');
        else if (e.isFile() && /\.(csproj|props|targets|sln)$/.test(e.name)) out.push(e.name);
    }
    return out.sort();
}
function csDeps(name, root) {
    return ['tests/cs/' + name + '/**', 'tests/cs/build-all.proj'].concat(productTrees(root));
}

function parse(text) {
    const m = new Map();                       // label -> Set of deps
    for (const raw of text.split(/\r?\n/)) {
        const line = raw.trim();
        if (!line || line[0] === '#') continue;
        const i = line.indexOf(' ');
        if (i < 0) continue;
        const label = line.slice(0, i), dep = line.slice(i + 1).trim();
        if (!m.has(label)) m.set(label, new Set());
        m.get(label).add(dep);
    }
    return m;
}
function format(m) {
    const lines = [];
    for (const label of Array.from(m.keys()).sort()) {
        const deps = m.get(label);
        if (deps.has('*')) { lines.push(label + ' *'); continue; }
        for (const d of Array.from(deps).sort()) lines.push(label + ' ' + d);
    }
    return HEADER.join('\n') + '\n' + lines.join('\n') + '\n';
}

function depMatches(dep, file) {
    if (dep === '*') return true;
    if (dep.endsWith('/**')) return file.startsWith(dep.slice(0, -2));
    if (dep.endsWith('/')) return file.startsWith(dep) && file.indexOf('/', dep.length) < 0;
    return file === dep;
}

// Observed lines ("F path", "D path/", "*") not covered by the record's deps for one label.
function missing(deps, observed) {
    if (!deps || deps.has('*')) return deps ? [] : observed.slice();
    const out = [];
    for (const o of observed) {
        if (o === '*') { out.push('* (starts a program the record cannot watch)'); continue; }
        const p = o.slice(2);
        let hit = false;
        for (const d of deps) {
            if (d === p) { hit = true; break; }
            if (d.endsWith('/**') && p.startsWith(d.slice(0, -2))) { hit = true; break; }
        }
        if (!hit) out.push(p);
    }
    return out;
}

// What forces every harness: the mechanism itself.
const EVERYTHING = new Set(['tests/depends.txt', 'tests/run.sh', 'tests/js/_lib.js', 'tests/screens/fixture.js',
    'tests/screens/chrome.js', 'tests/record-reads.js', 'tests/depends.js']);

function select(m, changed, labels) {
    // Nothing changed: nothing to run, not even the harnesses that always run.
    if (!changed.length) return labels.map(label => ({ label, run: false }));
    const forcing = changed.filter(f => EVERYTHING.has(f));
    return labels.map(label => {
        if (forcing.length) return { label, run: true, reason: 'everything runs: ' + forcing.join(', ') + ' changed' };
        const deps = m.get(label);
        if (!deps) return { label, run: true, reason: 'not in the record' };
        if (deps.has('*')) return { label, run: true, reason: 'not recorded yet (always runs)' };
        const hits = [];
        for (const f of changed) for (const d of deps) if (depMatches(d, f)) { hits.push(f); break; }
        if (hits.length) return { label, run: true, reason: hits.slice(0, 3).join(', ') + (hits.length > 3 ? ' +' + (hits.length - 3) + ' more' : '') };
        return { label, run: false };
    });
}

function merge(dir, recordFile, opts) {
    const m = fs.existsSync(recordFile) ? parse(fs.readFileSync(recordFile, 'utf8')) : new Map();
    const byLabel = new Map();
    for (const f of fs.readdirSync(dir)) {
        const mm = /^(.+)\.\d+\.txt$/.exec(f);
        if (!mm) continue;
        const label = mm[1].replace(/__/g, '/');
        if (!byLabel.has(label)) byLabel.set(label, new Set());
        for (const l of fs.readFileSync(path.join(dir, f), 'utf8').split('\n')) {
            if (!l) continue;
            byLabel.get(label).add(l === '*' ? '*' : l[0] === 'D' ? l.slice(2) : l.slice(2));
        }
    }
    for (const [label, deps] of byLabel) m.set(label, deps);
    for (const n of opts.cs) {
        const label = 'cs/' + n, deps = new Set(csDeps(n));
        for (const d of byLabel.get(label) || []) deps.add(d);
        m.set(label, deps);
    }
    for (const label of opts.always) m.set(label, new Set(['*']));
    fs.writeFileSync(recordFile, format(m));
    return m.size;
}

module.exports = { parse, format, select, missing, depMatches, csDeps, productTrees, merge, EVERYTHING };

if (require.main === module) {
    const [cmd, a, b] = process.argv.slice(2);
    const flag = n => { const i = process.argv.indexOf(n); return i < 0 ? [] : (process.argv[i + 1] || '').split(',').filter(Boolean); };
    if (cmd === 'merge') {
        const n = merge(a, b, { cs: flag('--cs'), always: flag('--always') });
        console.log('record now has ' + n + ' harnesses: ' + b);
    } else if (cmd === 'select') {
        const m = parse(fs.readFileSync(a, 'utf8'));
        const changed = fs.readFileSync(b, 'utf8').split(/\r?\n/).map(s => s.trim()).filter(Boolean);
        const labels = fs.readFileSync(0, 'utf8').split(/\r?\n/).map(s => s.trim()).filter(Boolean);
        for (const r of select(m, changed, labels)) console.log(r.run ? 'RUN\t' + r.label + '\t' + r.reason : 'SKIP\t' + r.label);
    } else {
        console.error('usage: depends.js merge <dir> <record> | select <record> <changed-file>');
        process.exit(2);
    }
}
