/*
 * `tests/run.sh --changed` skips a harness when none of the files it reads changed. That is
 * only safe while the record (tests/depends.txt) lists every file the harness reads, so
 * this fails when one does not: a stale record would otherwise skip a test silently.
 *
 *   node tests/js/changedsafe.js [record]
 *
 * The record is the argument so a copy with a file removed can be pointed at and shown
 * to go red. It checks:
 *  1. the observer sees what it must (files, directories, programs it cannot watch, Node
 *     children even when the harness passes its own env), on a harness written here;
 *  2. a record missing a file that harness read is reported as stale;
 *  3. selection: a changed file picks the harnesses that read it and only those, a harness
 *     the record has not heard of is picked, and a change to the mechanism picks all;
 *  4. a sample of the real recorded harnesses, run under the observer, reads nothing the
 *     record lacks;
 *  5. each statically recorded C# harness covers every product directory there is now.
 *
 * Part 4 is a sample, not the lot: all of them is the whole JavaScript suite again. A full
 * check is `tests/run.sh --record` followed by `git diff tests/depends.txt` being empty.
 */
'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const cp = require('child_process');
const L = require('./_lib');
const D = require('../depends');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else {
        fails.push(name + (detail ? '  — ' + detail : ''));
        console.log('  FAIL  ' + name + (detail ? '  — ' + detail : ''));
    }
}

const ROOT = L.ROOT;
const recordFile = path.resolve(process.argv[2] || path.join(ROOT, 'tests', 'depends.txt'));
const PRELOAD = path.join(ROOT, 'tests', 'record-reads.js').split(path.sep).join('/');
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'bonfire-changedsafe-'));
process.on('exit', () => { try { fs.rmSync(tmp, { recursive: true, force: true }); } catch (e) { /* ignore */ } });

// Run a script under the observer; resolves to the lines it observed, per process merged.
function observe(label, script, args, env) {
    return new Promise(resolve => {
        const dir = fs.mkdtempSync(path.join(tmp, 'rec-'));
        const e = Object.assign({}, process.env, env || {}, {
            BONFIRE_RECORD_DIR: dir, BONFIRE_RECORD_LABEL: label,
            NODE_OPTIONS: ((process.env.NODE_OPTIONS || '') + ' --require ' + JSON.stringify(PRELOAD)).trim()
        });
        const p = cp.spawn(process.execPath, [script].concat(args || []), { cwd: ROOT, env: e, stdio: 'ignore' });
        const t = setTimeout(() => p.kill(), 120000);
        p.on('exit', code => {
            clearTimeout(t);
            const lines = new Set();
            for (const f of fs.readdirSync(dir)) for (const l of fs.readFileSync(path.join(dir, f), 'utf8').split('\n')) if (l) lines.add(l);
            resolve({ code, lines: Array.from(lines) });
        });
    });
}

(async () => {
    ok('there is a record at ' + recordFile, fs.existsSync(recordFile));
    if (!fs.existsSync(recordFile)) { console.log('0 passed, 1 failed'); process.exit(1); }
    const record = D.parse(fs.readFileSync(recordFile, 'utf8'));

    console.log('\n── the observer sees what a harness reads ──────────────────────');
    const child = path.join(tmp, 'child.js').split(path.sep).join('/');
    fs.writeFileSync(child, "require('fs').readFileSync(" + JSON.stringify(path.join(ROOT, 'CONTRIBUTING.md')) + ");\n");
    const probe = path.join(tmp, 'probe.js');
    fs.writeFileSync(probe, [
        "const fs = require('fs'), cp = require('child_process'), path = require('path');",
        "const R = " + JSON.stringify(ROOT) + ";",
        "fs.readFileSync(path.join(R, 'LICENSE'));",
        "fs.readdirSync(path.join(R, 'docs'));",
        "fs.existsSync(path.join(R, 'does-not-exist.txt'));",
        "fs.readFileSync(path.join(require('os').tmpdir(), 'x.txt'), 'utf8');",
        // Node child with its own env: must still be observed.
        "cp.spawnSync(process.execPath, [" + JSON.stringify(child) + "], { env: { PATH: process.env.PATH } });",
        "cp.spawnSync('bash', ['-c', 'true']);"
    ].join('\n'));
    fs.writeFileSync(path.join(os.tmpdir(), 'x.txt'), 'x');
    const seen = await observe('js/probe', probe);
    ok('a file read is observed', seen.lines.includes('F LICENSE'), seen.lines.join(' | '));
    ok('a directory listing is observed', seen.lines.includes('D docs/'));
    ok('an existence check on a missing file is observed', seen.lines.includes('F does-not-exist.txt'));
    ok('a file outside the repository is not recorded', !seen.lines.some(l => /x\.txt/.test(l)));
    ok('a Node child with its own env is observed', seen.lines.includes('F CONTRIBUTING.md'), seen.lines.join(' | '));
    ok('a program it cannot watch makes the harness opaque', seen.lines.includes('*'));

    console.log('\n── a stale record is caught ────────────────────────────────────');
    const readOnly = seen.lines.filter(l => l !== '*');
    const full = new Set(['LICENSE', 'docs/', 'does-not-exist.txt', 'CONTRIBUTING.md']);
    ok('a record that lists everything it read has nothing missing', D.missing(full, readOnly).length === 0,
       D.missing(full, readOnly).join(', '));
    ok('an opaque harness is reported against a record that does not say so', D.missing(full, seen.lines).length === 1);
    const noLicense = new Set(['docs/', 'does-not-exist.txt', 'CONTRIBUTING.md']);
    const gaps = D.missing(noLicense, readOnly);
    ok('a record without LICENSE is reported as missing LICENSE', gaps.length === 1 && gaps[0] === 'LICENSE', gaps.join(', '));
    ok('a harness the record has never heard of is reported', D.missing(undefined, ['F LICENSE']).length === 1);
    ok('a tree entry covers what is beneath it', D.missing(new Set(['docs/**']), ['F docs/a/b.md', 'D docs/']).length === 0);

    console.log('\n── selection ───────────────────────────────────────────────────');
    const m = D.parse([
        'js/a Web/styles.css', 'js/a tests/js/a.js', 'js/b Web/profiles.js', 'js/b tests/js/b.js', 'js/b docs/',
        'js/c *', 'cs/d tests/cs/d/**', 'cs/d Web/**'
    ].join('\n'));
    const labels = ['js/a', 'js/b', 'js/c', 'js/new', 'cs/d'];
    const pick = changed => D.select(m, changed, labels).filter(r => r.run).map(r => r.label).join(',');
    ok('a CSS change picks the harness that reads it, the unrecorded ones and the C# one beneath Web',
       pick(['Web/styles.css']) === 'js/a,js/c,js/new,cs/d', pick(['Web/styles.css']));
    ok('a new file directly in a listed directory picks that harness', pick(['docs/new.md']) === 'js/b,js/c,js/new', pick(['docs/new.md']));
    ok('a file below a listed directory (not directly in it) does not', pick(['docs/sub/x.md']) === 'js/c,js/new', pick(['docs/sub/x.md']));
    ok('a file nothing reads picks only the unrecorded', pick(['README.md']) === 'js/c,js/new', pick(['README.md']));
    ok('no change picks nothing', pick([]) === '', pick([]));
    for (const f of Array.from(D.EVERYTHING)) {
        ok('a change to ' + f + ' picks every harness', pick([f]) === labels.join(','), pick([f]));
    }

    console.log('\n── the real record against the real harnesses ──────────────────');
    const timings = new Map();
    for (const l of fs.readFileSync(path.join(ROOT, 'tests', 'timings.txt'), 'utf8').split('\n')) {
        const p = l.split(/\s+/); if (p[0] && p[0][0] !== '#' && /^[0-9.]+$/.test(p[1])) timings.set(p[0], Number(p[1]));
    }
    const candidates = Array.from(record.keys())
        .filter(l => /^js\//.test(l) && !record.get(l).has('*') && l !== 'js/changedsafe'
                     && fs.existsSync(path.join(ROOT, 'tests', 'js', l.slice(3) + '.js'))
                     && (timings.get(l) || 99) <= 2.5)
        .sort();
    const N = 12, sample = [];
    for (let i = 0; i < N && candidates.length; i++) sample.push(candidates[Math.floor(i * candidates.length / N)]);
    ok('there is a sample to run (' + sample.length + ' of ' + candidates.length + ' fast recorded harnesses)', sample.length >= 1);
    let next = 0;
    async function worker() {
        while (next < sample.length) {
            const label = sample[next++];
            const r = await observe(label, path.join(ROOT, 'tests', 'js', label.slice(3) + '.js'));
            const gaps = D.missing(record.get(label), r.lines);
            ok(label + ' reads nothing its record lacks', gaps.length === 0,
               'not in the record: ' + gaps.slice(0, 5).join(', ') + (gaps.length > 5 ? ' (+' + (gaps.length - 5) + ')' : ''));
        }
    }
    await Promise.all([worker(), worker(), worker(), worker()]);

    console.log('\n── C# harnesses cover what they are built from ─────────────────');
    for (const label of Array.from(record.keys()).filter(l => /^cs\//.test(l))) {
        const deps = record.get(label);
        if (deps.has('*')) continue;
        const need = D.csDeps(label.slice(3)).filter(d => !deps.has(d));
        ok(label + ' lists its directory and every product source', need.length === 0, 'lacks ' + need.join(', '));
    }

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    if (fails.length) { console.log('\nFAILED:\n  ' + fails.join('\n  ')); process.exit(1); }
})();
