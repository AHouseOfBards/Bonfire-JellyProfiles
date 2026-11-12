/*
 * What the release and CI workflows must keep doing, held by reading them. Nothing here
 * runs a workflow; it checks the text of the steps, in order.
 *
 *   node tests/js/workflows.js [release.yml [ci.yml]]
 *
 * Both paths are arguments so the checks can be pointed at an older copy and shown to go
 * red. buildtargets.js, betafeed.js and manifest.js hold the rest of release.yml.
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

const relPath = process.argv[2] || path.join(L.ROOT, '.github', 'workflows', 'release.yml');
const ciPath = process.argv[3] || path.join(L.ROOT, '.github', 'workflows', 'ci.yml');
const strip = y => y.split('\n').filter(l => !/^\s*#/.test(l)).map(l => l.replace(/\s#\s.*$/, '')).join('\n');
const rel = strip(fs.readFileSync(relPath, 'utf8'));
const ci = strip(fs.readFileSync(ciPath, 'utf8'));
const steps = rel.split(/\n\s*- (?=name:|uses:|run:)/);
const idx = re => steps.findIndex(s => re.test(s));

console.log('\n── release.yml: every check CI makes runs before the build ─────');
const build = idx(/run:\s*dotnet build/);
[['tests/binchk.sh', /tests\/binchk\.sh/], ['the Campground.Shared copy check', /check-shared\.sh/]].forEach(([n, re]) => {
    const i = idx(re);
    ok('release.yml runs ' + n, i !== -1);
    ok('before the build step', i !== -1 && build !== -1 && i < build, 'step ' + i + ' vs build ' + build);
    ok('and it is not gated or allowed to fail', i !== -1 && !/^\s*(if|continue-on-error):/m.test(steps[i]));
});
ok('the checkout comment no longer asks for "a token with push access"',
   !/token with push access/.test(fs.readFileSync(relPath, 'utf8')));

console.log('\n── release.yml: any pre-release label routes to beta ───────────');
{
    const ver = steps[idx(/INFORMATIONAL|InformationalVersion/)] || '';
    const m = /case "\$INFO" in([\s\S]*?)esac/.exec(ver);
    ok('there is a case on $INFO', !!m);
    // Run the pattern list the way the shell would, against labels both known and not.
    const arms = m ? m[1].split(/;;/).map(a => a.trim()).filter(Boolean) : [];
    const toRe = pat => new RegExp('^' + pat.split('|').map(p => p.trim()
        .replace(/[.+?^${}()\[\]\\]/g, '\\$&').replace(/\*/g, '.*')).join('|').replace(/\|/g, '$|^') + '$');
    const route = info => {
        for (const a of arms) {
            const mm = /^([^)]+)\)([\s\S]*)$/.exec(a);
            if (mm && toRe(mm[1]).test(info)) return /CHANNEL="beta"/.test(mm[2]) ? 'beta' : 'stable';
        }
        return 'none';
    };
    ['1.9.0.4-beta', '1.9.0.4-alpha.1', '1.9.0.4-rc1', '1.9.1-preview', '2.0.0-final', '1.9.1-x'].forEach(l =>
        ok(l + ' routes to beta', route(l) === 'beta', route(l)));
    ok('1.9.0.4 routes to stable', route('1.9.0.4') === 'stable', route('1.9.0.4'));
}

console.log('\n── release.yml: the release is published before the manifest ───');
{
    const stamp = idx(/map\(if \.version == \$v then \.checksum = \$c/);
    const push = idx(/git push origin/);
    const release = idx(/action-gh-release/);
    const verify = idx(/name: Verify published asset/);
    const check = idx(/same checksum as/);
    ok('a step publishes the release', release !== -1);
    ok('the release comes before the manifest is stamped', release !== -1 && stamp !== -1 && release < stamp,
       'release ' + release + ', stamp ' + stamp);
    ok('the release comes before the manifest is pushed', release !== -1 && push !== -1 && release < push,
       'release ' + release + ', push ' + push);
    ok('the verify step comes after the push', push !== -1 && verify > push);
    ok('the checks that can refuse a release come before it', check !== -1 && check < release,
       'check ' + check + ', release ' + release);
    ok('the release step does not push or stamp anything itself',
       release !== -1 && !/git push|checksum = \$c/.test(steps[release]));
    ok('the manifest push is not in the same step as the checks (a failed release pushes nothing)',
       check !== -1 && push !== -1 && check !== push);
}

console.log('\n── release.yml: release notes travel in a file ─────────────────');
ok('no fixed EOF delimiter is written to GITHUB_OUTPUT', !/<<\s*"?EOF/.test(rel) && !/echo "EOF"/.test(rel));
{
    const s = steps[idx(/action-gh-release/)] || '';
    ok('the release reads its notes from body_path', /^\s*body_path:\s*\S/m.test(s));
    ok('and does not pass them inline as body:', !/^\s*body:/m.test(s));
}

console.log('\n── release.yml: verify reads the pushed ref ────────────────────');
{
    const v = steps[idx(/name: Verify published asset/)] || '';
    ok('verify does not fetch the manifest from raw.githubusercontent.com', !/raw\.githubusercontent\.com/.test(v));
    ok('it reads origin/<branch>:manifest.json after a fetch',
       /git fetch origin[^\n]*BRANCH/.test(v) && /git show "origin\/\$\{BRANCH\}:manifest\.json"/.test(v));
    ok('and still compares the downloaded zip md5 with the manifest checksum',
       /md5sum published\.zip/.test(v) && /"\$ACTUAL" != "\$EXPECTED"/.test(v));
}

console.log('\n── ci.yml ──────────────────────────────────────────────────────');
ok('ci.yml sets top-level permissions: contents: read', /^permissions:\s*\n\s+contents:\s*read\s*$/m.test(ci));
ok('ci.yml gives the job a timeout', /^\s+timeout-minutes:\s*\d+/m.test(ci));
ok('ci.yml cancels a superseded run on the same ref',
   /^concurrency:\s*\n\s+group:[^\n]*github\.ref[^\n]*\n\s+cancel-in-progress:\s*true/m.test(ci));
{
    const uses = (fs.readFileSync(ciPath, 'utf8').match(/^\s*(?:-\s+)?uses:\s*\S+/gm) || []);
    const relUses = (fs.readFileSync(relPath, 'utf8').match(/^\s*(?:-\s+)?uses:\s*\S+/gm) || []);
    ok('ci.yml has actions to pin (' + uses.length + ')', uses.length > 0);
    uses.forEach(u => ok('ci.yml: ' + u.trim() + ' is pinned to a 40-hex SHA', /@[0-9a-f]{40}$/.test(u.trim())));
    // The same action pinned to a different commit in the two files is how one of them
    // gets bumped and the other forgotten.
    const norm = list => list.map(u => u.replace(/^\s*(-\s+)?uses:\s*/, '').trim());
    const rmap = {};
    norm(relUses).forEach(u => { rmap[u.split('@')[0]] = u; });
    norm(uses).forEach(u => {
        const r = rmap[u.split('@')[0]];
        if (r) ok('ci.yml and release.yml pin ' + u.split('@')[0] + ' to the same commit', r === u, r + ' vs ' + u);
    });
}

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
