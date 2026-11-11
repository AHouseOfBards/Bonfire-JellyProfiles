// R-1294: tests/run.sh must fail a harness that exits 0 having asserted nothing.
// Points the runner at a scratch directory of stub harnesses (BONFIRE_JS_DIR) and checks
// each verdict. Against a runner without the check, the silent and zero-count stubs pass.
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');
const cp = require('child_process');

const root = path.resolve(__dirname, '..', '..');
const runner = process.argv[2] || path.join(root, 'tests', 'run.sh');

let pass = 0, fail = 0;
function check(name, ok) { if (ok) pass++; else { fail++; console.log('FAIL: ' + name); } }

function run(body) {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'runner-'));
    fs.writeFileSync(path.join(dir, 'stub.js'), body);
    const r = cp.spawnSync('bash', [runner, 'js'], {
        cwd: root, encoding: 'utf8', env: Object.assign({}, process.env, { BONFIRE_JS_DIR: dir })
    });
    fs.rmSync(dir, { recursive: true, force: true });
    const out = (r.stdout || '') + (r.stderr || '');
    // Only the stub's own line matters; the shared-copy check also runs and is not at issue.
    const line = out.split('\n').filter(function (l) { return /^\s+stub\s/.test(l); })[0] || '';
    return { line: line, out: out };
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

console.log(pass + ' passed, ' + fail + ' failed');
process.exit(fail === 0 ? 0 : 1);
