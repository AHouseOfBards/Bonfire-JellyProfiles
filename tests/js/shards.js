/*
 * tests/run.sh --shard K/N must hand every harness to exactly one shard, however many
 * shards there are, and the split must not depend on anything but tests/timings.txt.
 * CI runs one shard per matrix job; a harness in no shard is a check that silently stops
 * running, and one in two is wasted time that hides which job went red.
 *
 *   node tests/js/shards.js [checkout-root]
 *
 * The checkout root is the argument so a copy with a broken shard.awk can be pointed at
 * and shown to go red. It only ever calls `run.sh --list`, which takes no lock and runs
 * nothing, so it is safe beside a full run.
 */
'use strict';

const fs = require('fs');
const path = require('path');
const cp = require('child_process');
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

const root = path.resolve(process.argv[2] || L.ROOT);
const runSh = path.join(root, 'tests', 'run.sh');
ok('there is a tests/run.sh under ' + root, fs.existsSync(runSh));
if (!fs.existsSync(runSh)) { console.log('0 passed, 1 failed'); process.exit(1); }

function list(section, shard, env) {
    const args = [runSh, section];
    if (shard) args.push('--shard', shard);
    args.push('--list');
    const r = cp.spawnSync('bash', args, { cwd: root, encoding: 'utf8', timeout: 60000,
        env: Object.assign({}, process.env, env || {}) });
    return { status: r.status, names: (r.stdout || '').split('\n').map(s => s.trim()).filter(Boolean), err: r.stderr };
}

// What is on disk, worked out here from the files rather than asked of run.sh.
const jsDir = path.join(root, 'tests', 'js');
const onDiskJs = fs.readdirSync(jsDir).filter(f => /\.js$/.test(f) && f[0] !== '_' && !/\.(verify|scan)\.js$/.test(f))
    .map(f => 'js/' + f.replace(/\.js$/, '')).concat(['js/sharedcopy']);
const csDir = path.join(root, 'tests', 'cs');
const onDiskCs = fs.readdirSync(csDir).filter(d => fs.statSync(path.join(csDir, d)).isDirectory()).map(d => 'cs/' + d);

console.log('\n── the unsharded run is the whole tree ─────────────────────────');
const allJs = list('js');
const allCs = list('cs');
ok('js lists every harness on disk (' + onDiskJs.length + ')',
   allJs.status === 0 && allJs.names.slice().sort().join() === onDiskJs.slice().sort().join(),
   'listed ' + allJs.names.length + ', on disk ' + onDiskJs.length);
ok('cs lists every C# harness on disk (' + onDiskCs.length + ')',
   allCs.status === 0 && allCs.names.slice().sort().join() === onDiskCs.slice().sort().join(),
   'listed ' + allCs.names.length + ', on disk ' + onDiskCs.length);

console.log('\n── every harness is in exactly one shard, for N = 1..8 ─────────');
// One assertion per N and section, and the detail names the harness, so a failure says which.
function cover(section, expected, N, env) {
    const seen = {};
    const empty = [];
    for (let k = 1; k <= N; k++) {
        const r = list(section, k + '/' + N, env);
        if (r.status !== 0) return 'shard ' + k + '/' + N + ' exited ' + r.status + ': ' + (r.err || '').trim();
        if (!r.names.length) empty.push(k);
        r.names.forEach(n => { (seen[n] = seen[n] || []).push(k); });
    }
    const missing = expected.filter(n => !seen[n]);
    const twice = Object.keys(seen).filter(n => seen[n].length > 1).map(n => n + ' in ' + seen[n].join('+'));
    const stray = Object.keys(seen).filter(n => expected.indexOf(n) === -1);
    const bad = [];
    if (missing.length) bad.push('in no shard: ' + missing.join(', '));
    if (twice.length) bad.push('in two shards: ' + twice.join(', '));
    if (stray.length) bad.push('not in the tree: ' + stray.join(', '));
    if (empty.length && N <= 8 && section === 'js') bad.push('empty shards: ' + empty.join(', '));
    return bad.join(' | ');
}
for (let N = 1; N <= 8; N++) {
    const a = cover('js', onDiskJs, N);
    ok('js, N=' + N, a === '', a);
    const b = cover('cs', onDiskCs, N);
    ok('cs, N=' + N, b === '', b);
}
{
    // cs10 runs the same set against the other build: it must shard the same way.
    const a = cover('cs10', onDiskCs, 3);
    ok('cs10, N=3', a === '', a);
}

console.log('\n── the split depends on the timings file and nothing else ──────');
{
    const a = list('js', '2/5'), b = list('js', '2/5');
    ok('the same shard twice is the same list', a.names.join() === b.names.join() && a.names.length > 0);
    // BONFIRE_JOBS is how wide the machine is; the shards must not move with it.
    const c = list('js', '2/5', { BONFIRE_JOBS: '1', BONFIRE_CHROME_JOBS: '3' });
    ok('and BONFIRE_JOBS / BONFIRE_CHROME_JOBS do not move it', a.names.join() === c.names.join());
    // A harness the file has not heard of is counted at the default, and still placed.
    const empty = path.join(require('os').tmpdir(), 'bonfire-empty-timings-' + process.pid + '.txt');
    fs.writeFileSync(empty, '# nothing known\n');
    try {
        const a = cover('js', onDiskJs, 4, { BONFIRE_TIMINGS_FILE: empty });
        ok('with no timings at all every harness is still in exactly one shard', a === '', a);
    } finally { fs.unlinkSync(empty); }
}

console.log('\n── the split is balanced ───────────────────────────────────────');
{
    // Greedy longest-first is within one item of even; the cost is read from the file the
    // same way run.sh reads it, a parallel harness counting a quarter.
    const t = {};
    fs.readFileSync(path.join(root, 'tests', 'timings.txt'), 'utf8').split('\n').forEach(l => {
        const m = /^(\S+)\s+([0-9.]+)\s+(serial|parallel)/.exec(l);
        if (m) t[m[1]] = Number(m[2]) / (m[3] === 'parallel' ? 4 : 1);
    });
    const N = 6, loads = [];
    let biggest = 0, total = 0;
    for (let k = 1; k <= N; k++) {
        const names = list('js', k + '/' + N).names;
        const load = names.reduce((s, n) => s + (t[n] === undefined ? 10 : t[n]), 0);
        loads.push(load); total += load;
        names.forEach(n => { biggest = Math.max(biggest, t[n] === undefined ? 10 : t[n]); });
    }
    const max = Math.max.apply(null, loads), min = Math.min.apply(null, loads);
    ok('no js shard of 6 is more than one harness heavier than the lightest (' + loads.map(x => x.toFixed(0)).join('/') + ')',
       max - min <= biggest + 0.01, 'max ' + max + ', min ' + min + ', biggest harness ' + biggest);
}

console.log('\n── bad arguments are refused, and run nothing ──────────────────');
['0/4', '5/4', '1/0', 'x', '1/', '/4', '2'].forEach(s => {
    const r = cp.spawnSync('bash', [runSh, 'js', '--shard', s, '--list'], { cwd: root, encoding: 'utf8', timeout: 60000 });
    ok('--shard ' + s + ' exits 2', r.status === 2, 'exit ' + r.status);
});

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
