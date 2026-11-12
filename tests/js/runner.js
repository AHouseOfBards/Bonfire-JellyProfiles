// R-1294: tests/run.sh must fail a harness that exits 0 having asserted nothing.
// Points the runner at a scratch directory of stub harnesses (BONFIRE_JS_DIR) and checks
// each verdict. Against a runner without the check, the silent and zero-count stubs pass.
//
// It also covers the runner's own machinery, each against a stub set that fails if the
// machinery is missing: the parallel run (stubs that can only pass if they overlap), the
// order of the output, the harnesses held back to run alone, and the one-run-at-a-time lock.
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');
const cp = require('child_process');

const root = path.resolve(__dirname, '..', '..');
const runner = process.argv[2] || path.join(root, 'tests', 'run.sh');

let pass = 0, fail = 0;
function check(name, ok) { if (ok) pass++; else { fail++; console.log('FAIL: ' + name); } }

const fwd = (p) => p.replace(/\\/g, '/');

// Run the runner over a set of stub harnesses. A private TMPDIR keeps the lock this run
// takes away from the real one: this harness is itself run by tests/run.sh, which holds it.
function runSet(stubs, opts) {
    opts = opts || {};
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'runner-'));
    const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'runner-tmp-'));
    Object.keys(stubs).forEach(function (n) { fs.writeFileSync(path.join(dir, n + '.js'), stubs[n]); });
    const env = Object.assign({}, process.env, {
        BONFIRE_JS_DIR: fwd(dir), STUBDIR: fwd(dir), TMPDIR: fwd(tmp)
    }, opts.env || {});
    delete env.BONFIRE_RUN_LOCKED;
    if (opts.env && opts.env.BONFIRE_RUN_LOCKED) env.BONFIRE_RUN_LOCKED = opts.env.BONFIRE_RUN_LOCKED;
    if (opts.before) opts.before(fwd(tmp), dir);
    const t0 = Date.now();
    const r = cp.spawnSync('bash', [runner, 'js'], { cwd: root, encoding: 'utf8', env: env, timeout: 120000 });
    const ms = Date.now() - t0;
    const lockLeft = fs.existsSync(path.join(tmp, 'bonfire-tests.lock'));
    const left = fs.readdirSync(tmp);
    fs.rmSync(dir, { recursive: true, force: true });
    fs.rmSync(tmp, { recursive: true, force: true });
    const out = (r.stdout || '') + (r.stderr || '');
    // Only the stubs' own lines matter; the shared-copy check also runs and is not at issue.
    const lines = out.split('\n').filter(function (l) {
        const m = /^ {2}(\S+) /.exec(l);
        return m && Object.prototype.hasOwnProperty.call(stubs, m[1]);
    });
    return { out: out, lines: lines, status: r.status, ms: ms, lockLeft: lockLeft, left: left };
}

function run(body) {
    const r = runSet({ stub: body });
    const line = r.out.split('\n').filter(function (l) { return /^\s+stub\s/.test(l); })[0] || '';
    return { line: line, out: r.out };
}

const silent = run('// asserts nothing\n');
check('silent harness is reported FAILED', /FAILED/.test(silent.line));
const zero = run("console.log('0 passed, 0 failed');\n");
check('zero-count harness is reported FAILED', /FAILED/.test(zero.line));
const zero2 = run("console.log('-- pass 0, fail 0');\n");
check('"pass 0" harness is reported FAILED', /FAILED/.test(zero2.line));
const good = run("console.log('12 passed, 0 failed');\n");
check('a harness with counted checks is not failed', good.line !== '' && !/FAILED/.test(good.line));
const tenpass = run("console.log('10 passed, 0 failed');\n");
check('"10 passed" is not mistaken for zero', tenpass.line !== '' && !/FAILED/.test(tenpass.line));
const bad = run("console.log('3 passed, 1 failed'); process.exit(1);\n");
check('a failing harness is still FAILED', /FAILED/.test(bad.line));

// A harness that prints PASS lines and then stops, exiting 0, never reached its totals. The
// line that says "passed" earlier in its output must not rescue it.
const noTotals = run("console.log('  PASS  one'); console.log('  PASS  two');\n");
check('PASS lines with no totals line are reported FAILED', /FAILED \(no totals line\)/.test(noTotals.line));
const earlyTotals = run("console.log('5 passed, 0 failed'); console.log('  PASS  trailing');\n");
check('a totals line that is not the last line does not count', /FAILED \(no totals line\)/.test(earlyTotals.line));
const dashTotals = run("console.log('  PASS  one'); console.log('-- pass 4, fail 0');\n");
check('"-- pass N, fail M" is a totals line', dashTotals.line !== '' && !/FAILED/.test(dashTotals.line));
const trailingBlank = run("console.log('7 passed, 0 failed'); console.log('');\n");
check('a blank line after the totals is ignored', trailingBlank.line !== '' && !/FAILED/.test(trailingBlank.line));
const skipped = run("console.log('  SKIPPED  no Chrome on this machine');\n");
check('a harness that says it was skipped is not failed', skipped.line !== '' && !/FAILED/.test(skipped.line));

// ── Parallel run ─────────────────────────────────────────────────────────────────────
const PRE = "const fs = require('fs'), d = process.env.STUBDIR, NAME = require('path').basename(__filename, '.js');\n";
// Three stubs that each wait to see the other two start. They can only pass if the runner
// really runs them at the same time; run one at a time, the first never sees the others.
const meet = PRE
    + "fs.writeFileSync(d + '/started.' + NAME, '');\n"
    + "const t = Date.now();\n"
    + "(function w() {\n"
    + "  const c = fs.readdirSync(d).filter(function (f) { return f.indexOf('started.') === 0; }).length;\n"
    + "  if (c >= 3) { console.log('1 passed, 0 failed'); return; }\n"
    + "  if (Date.now() - t > 2500) { console.log('never met the others'); process.exit(1); }\n"
    + "  setTimeout(w, 20);\n"
    + "})();\n";
const meeters = { m1: meet, m2: meet, m3: meet };
const par = runSet(meeters, { env: { BONFIRE_JOBS: '3' } });
check('plain harnesses run at the same time with BONFIRE_JOBS=3', par.status === 0 && par.lines.length === 3 && !/FAILED/.test(par.lines.join('')));
const defaultJobs = runSet(meeters, { env: { BONFIRE_JOBS: '' } });
check('four at a time is the default', defaultJobs.status === 0 && !/FAILED/.test(defaultJobs.lines.join('')));
const one = runSet(meeters, { env: { BONFIRE_JOBS: '1' } });
check('BONFIRE_JOBS=1 runs them one after another', one.status === 1 && one.lines.length === 3 && /FAILED/.test(one.lines.join('')));

// Output is in name order however the workers finish: the first stub is the slowest.
const slow = (ms) => "setTimeout(function () { console.log('1 passed, 0 failed'); }, " + ms + ");\n";
const ordered = runSet({ aa: slow(700), bb: slow(10), cc: slow(300), dd: slow(10), ee: slow(10) }, { env: { BONFIRE_JOBS: '4' } });
const order = ordered.lines.map(function (l) { return l.trim().split(/\s+/)[0]; }).join(',');
check('results print in name order: ' + order, order === 'aa,bb,cc,dd,ee');
check('the same label and last-line format is printed', /^ {2}aa[ ]+1 passed, 0 failed$/m.test(ordered.out));

// A failure keeps its tail, and does not take the others down with it.
const failing = runSet({
    aa: "console.log('1 passed, 0 failed');\n",
    bb: "console.log('the evidence'); console.log('1 passed, 1 failed'); process.exit(1);\n",
    cc: "console.log('1 passed, 0 failed');\n"
}, { env: { BONFIRE_JOBS: '4' } });
check('a failing parallel harness is FAILED with its tail', /bb[ ]+FAILED\n\s+the evidence\n\s+1 passed, 1 failed/.test(failing.out));
check('and the run exits 1 naming it', failing.status === 1 && /3 harnesses|4 harnesses/.test(failing.out) && /failed: bb/.test(failing.out));
check('the others still ran green', /aa[ ]+1 passed/.test(failing.out) && /cc[ ]+1 passed/.test(failing.out));

// ── Harnesses that run alone ─────────────────────────────────────────────────────────
// Each marks that it is running and fails if anything else is. Named to sort before the
// plain stub, so only the runner's own rule can put them after it.
const alone = PRE
    + "const others = fs.readdirSync(d).filter(function (f) { return /^(plain|excl)\\./.test(f); });\n"
    + "if (others.length) { console.log('overlap with ' + others.join(', ')); process.exit(1); }\n"
    + "fs.writeFileSync(d + '/excl.' + NAME, '');\n"
    + "setTimeout(function () { fs.unlinkSync(d + '/excl.' + NAME); console.log('1 passed, 0 failed'); }, 300);\n";
const plainSlow = PRE
    + "fs.writeFileSync(d + '/plain.' + NAME, '');\n"
    + "setTimeout(function () { fs.unlinkSync(d + '/plain.' + NAME); console.log('1 passed, 0 failed'); }, 900);\n";
const chromeStub = "// A harness that drives Chrome names its helper: require('../screens/chrome').\n" + alone;
const mixed = runSet({ a1: chromeStub, a2: chromeStub, a3: chromeStub, p1: plainSlow, p2: plainSlow }, { env: { BONFIRE_JOBS: '4' } });
check('a harness that requires the Chrome helper never overlaps another one', mixed.status === 0 && !/FAILED/.test(mixed.out));
const findChrome = runSet({ a1: "// chrome.findChrome();\n" + alone, a2: "// chrome.findChrome();\n" + alone, p1: plainSlow }, { env: { BONFIRE_JOBS: '4' } });
check('one that only calls findChrome is held back too', findChrome.status === 0 && !/FAILED/.test(findChrome.out));
// The same stubs without the marker overlap, so the check above is the rule's doing.
const unmarked = runSet({ a1: alone, a2: alone, a3: alone, p1: plainSlow, p2: plainSlow }, { env: { BONFIRE_JOBS: '4' } });
check('without the Chrome marker the same stubs do overlap', unmarked.status === 1 && /overlap with/.test(unmarked.out));
// The explicit list: serverinfo is on it, and nothing in its source says Chrome.
const listed = runSet({ serverinfo: alone, i18ntest: alone, p1: plainSlow, p2: plainSlow }, { env: { BONFIRE_JOBS: '4' } });
check('harnesses on the explicit serial list run alone', listed.status === 0 && !/FAILED/.test(listed.out));

// ── One full run at a time ───────────────────────────────────────────────────────────
const quick = { q: "console.log('1 passed, 0 failed');\n" };

function deadPid() {
    const r = cp.spawnSync('bash', ['-c', 'echo $$'], { encoding: 'utf8' });
    return r.stdout.trim();
}
function lockWith(pid) {
    return function (tmp) {
        fs.mkdirSync(tmp + '/bonfire-tests.lock');
        fs.writeFileSync(tmp + '/bonfire-tests.lock/pid', pid + '\n');
    };
}
// A bash process that stays alive for a while, so there is a pid the runner can see.
function holder(seconds) {
    const f = path.join(os.tmpdir(), 'runner-holder-' + process.pid + '-' + seconds);
    try { fs.unlinkSync(f); } catch (e) { /* none yet */ }
    const child = cp.spawn('bash', ['-c', 'echo $$ > "' + fwd(f) + '"; exec sleep ' + seconds], { stdio: 'ignore' });
    let pid = '';
    for (let i = 0; i < 200 && !pid; i++) {
        try { pid = fs.readFileSync(f, 'utf8').trim(); } catch (e) { cp.spawnSync('bash', ['-c', 'sleep 0.05']); }
    }
    return { pid: pid, child: child, stop: function () { try { child.kill(); } catch (e) { /* gone */ } try { fs.unlinkSync(f); } catch (e) { /* gone */ } } };
}

const clean = runSet(quick, {});
check('a run takes the lock and removes it when done', clean.status === 0 && !clean.lockLeft);
const failedRun = runSet({ q: "console.log('1 passed, 1 failed'); process.exit(1);\n" }, {});
check('and removes it when the run fails', failedRun.status === 1 && !failedRun.lockLeft);

const stale = runSet(quick, { before: lockWith(deadPid()) });
check('a lock whose process is gone is taken over', stale.status === 0 && /q[ ]+1 passed/.test(stale.out) && !stale.lockLeft);
const empty = runSet(quick, { before: function (tmp) { fs.mkdirSync(tmp + '/bonfire-tests.lock'); } });
check('a lock with no pid in it is taken over', empty.status === 0 && !empty.lockLeft);

const h1 = holder(30);
const refused = runSet(quick, { before: lockWith(h1.pid), env: { BONFIRE_LOCK_WAIT: '0' } });
h1.stop();
check('a live lock refuses the second run, saying so', refused.status === 3 && /already running/.test(refused.out) && !/q[ ]+1 passed/.test(refused.out));
check('and leaves the other run\'s lock alone', refused.lockLeft === true);

const h2 = holder(30);
const nested = runSet(quick, { before: lockWith(h2.pid), env: { BONFIRE_LOCK_WAIT: '0', BONFIRE_RUN_LOCKED: '1' } });
h2.stop();
check('a nested run, told the lock is held, does not take it', nested.status === 0 && /q[ ]+1 passed/.test(nested.out));

const h3 = holder(3);
const waited = runSet(quick, { before: lockWith(h3.pid), env: { BONFIRE_LOCK_WAIT: '30' } });
h3.stop();
check('a run waits for the lock and then goes ahead', waited.status === 0 && /waiting/.test(waited.out) && /q[ ]+1 passed/.test(waited.out) && waited.ms > 1500);

console.log(pass + ' passed, ' + fail + ' failed');
process.exit(fail === 0 ? 0 : 1);
