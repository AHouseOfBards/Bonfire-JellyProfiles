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
    // The step that writes the finished entry: the only place a checksum goes into the
    // manifest. Nothing writes a placeholder any more, so a release is offered to servers
    // only once the zip it names can be downloaded.
    const add = idx(/checksum: \$c/);
    const push = idx(/git push origin/);
    const release = idx(/action-gh-release/);
    const verify = idx(/name: Verify published asset/);
    const check = idx(/same checksum as/);
    ok('a step publishes the release', release !== -1);
    ok('a step adds the manifest entry', add !== -1);
    ok('the release comes before the manifest entry is added', release !== -1 && add !== -1 && release < add,
       'release ' + release + ', add ' + add);
    ok('the release comes before the manifest is pushed', release !== -1 && push !== -1 && release < push,
       'release ' + release + ', push ' + push);
    ok('the entry is pushed by the step that adds it', add !== -1 && add === push, 'add ' + add + ', push ' + push);
    ok('the verify step comes after the push', push !== -1 && verify > push);
    ok('the checks that can refuse a release come before it', check !== -1 && check < release,
       'check ' + check + ', release ' + release);
    ok('the release step does not push or write the manifest itself',
       release !== -1 && !/git push|checksum: \$c/.test(steps[release]));
    ok('the manifest push is not in the same step as the checks (a failed release pushes nothing)',
       check !== -1 && push !== -1 && check !== push);
    // Pairwise, one step at a time: nothing ahead of the release may touch the manifest
    // or the repository. This is what made a version installable-in-name-only for the
    // whole of a run, when the entry was committed to the channel branch before the tag.
    steps.slice(0, release === -1 ? 0 : release + 1).forEach((s, i) => {
        const nm = (/name:\s*([^\n]*)/.exec(s) || [])[1] || ('step ' + i);
        ok('step "' + nm + '" (before the release) writes nothing to the manifest or the branch',
           !/git push|git commit|mv manifest\.tmp\.json|> manifest\.json/m.test(s));
    });
}

console.log('\n── release.yml: the entry is complete and the refusals survive ─');
{
    const add = steps[idx(/checksum: \$c/)] || '';
    const chk = steps[idx(/same checksum as/)] || '';
    ['version: \\$v', 'changelog: \\$notes', 'targetAbi: \\$abi', 'sourceUrl: \\$url', 'checksum: \\$c', 'timestamp: \\$ts']
        .forEach(f => ok('the entry carries ' + f.replace('\\', '').split(':')[0],
            new RegExp('\\{[^}]*' + f + '[^}]*\\}').test(add)));
    ok('the changelog is read from the notes file, not typed into the workflow',
       /--rawfile notes "\$RUNNER_TEMP\/release-notes\.md"/.test(add));
    ok('targetAbi is the newest existing entry\'s', /\.targetAbi/.test(add) && /sort_by\(\.version/.test(add));
    ok('the checksum is refused if it is all zeros', /\*\) echo "::error::Checksum is all zeros/.test(add));
    ok('a refusal when the notes file is missing or empty, before the release',
       /release-notes\/\$\{V\}\.md/.test(chk) && /-s "\$NOTES"/.test(chk) && /::error::\$\{NOTES\} is missing/.test(chk));
    ok('a refusal when the version is already in either manifest',
       /for B in main beta/.test(chk) && /already in the manifest on/.test(chk) && /select\(\.version == \$v\)/.test(chk));
    ok('a refusal when the checksum belongs to another version in the channel',
       /select\(\.checksum == \$c and \.version != \$v\)/.test(chk));
    ok('the tag is still checked against <Version>', /Tag v\$\{TAG\} does not match/.test(steps[idx(/does not match <Version>/)] || ''));
    ok('a retry rebuilds the entry on the new branch head instead of rebasing a manifest edit',
       /add_entry/.test(add) && !/git rebase/.test(add));
    ok('the beta feed is trimmed in the same step, on beta only',
       /if \[ "\$BRANCH" = "beta" \]/.test(add) && /\.\[:3\]/.test(add));
    ok('and stable is never trimmed (the only \.[:N] in the step sits inside the beta block)',
       (add.match(/\.\[:\d+\]/g) || []).length === 1 &&
       add.indexOf('.[:3]') > add.indexOf('if [ "$BRANCH" = "beta" ]'));
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

// ── The jobs of a workflow, by name ────────────────────────────────────────────────────
// Just enough YAML for these two files: a job is a two-space key under `jobs:`, and its
// text runs to the next one. `needs` is a name or a flow list.
function jobsOf(y) {
    const at = y.search(/^jobs:\s*$/m);
    const out = {};
    if (at === -1) return out;
    const body = y.slice(at).split('\n').slice(1);
    let cur = null;
    body.forEach(l => {
        const m = /^  ([A-Za-z0-9_-]+):\s*$/.exec(l);
        if (m) { cur = m[1]; out[cur] = { name: cur, text: '' }; }
        else if (cur) out[cur].text += l + '\n';
    });
    Object.keys(out).forEach(k => {
        const t = out[k].text;
        const nm = /^    needs:\s*(.*)$/m.exec(t);
        out[k].needs = nm ? nm[1].replace(/[\[\]]/g, '').split(',').map(s => s.trim()).filter(Boolean) : [];
        out[k].uses = ((/^    uses:\s*(\S+)/m.exec(t)) || [])[1] || '';
        out[k].jobIf = /^    if:/m.test(t);
        out[k].jobContinue = /^    continue-on-error:/m.test(t);
        out[k].timeout = /^    timeout-minutes:\s*\d+/m.test(t);
    });
    return out;
}
const relJobs = jobsOf(rel);
const ciJobs = jobsOf(ci);

console.log('\n── release.yml: nothing is built from a commit whose suite did not pass ─');
{
    const t = relJobs.tests;
    ok('release.yml has a `tests` job', !!t);
    ok('which calls ci.yml, the same workflow a push runs',
       !!t && /^\.\/\.github\/workflows\/ci\.yml$/.test(t.uses), t && t.uses);
    ok('and is not gated or allowed to fail', !!t && !t.jobIf && !t.jobContinue);
    ok('and asks for no more than read access', !!t && /^    permissions:\s*\n\s+contents:\s*read\s*$/m.test(t.text));
    // Pairwise: every other job, so a second job added later cannot build or publish
    // without it. A job that needs `tests` and is also `if: always()` runs after a red
    // suite, so the condition is what is looked for as well as the name.
    const others = Object.keys(relJobs).filter(n => n !== 'tests');
    ok('there is a job that builds and publishes', others.length > 0);
    others.forEach(n => {
        const j = relJobs[n];
        ok('job `' + n + '` needs tests', j.needs.indexOf('tests') !== -1, 'needs: ' + j.needs.join(', '));
        ok('job `' + n + '` has no job-level if: (always() would run it after a red suite)', !j.jobIf);
        ok('job `' + n + '` is not allowed to fail', !j.jobContinue);
    });
    // The build, the package, the push and the release are all in jobs that need `tests`,
    // and none of them sits in the `tests` job itself.
    [['the build', /dotnet build -c Release\s*$/m], ['the packaging step', /zip .*Jellyfin\.Profiles\.zip/],
     ['the manifest push', /git push origin/], ['the GitHub release', /action-gh-release/]].forEach(([pn, re]) => {
        const owner = Object.keys(relJobs).filter(n => re.test(relJobs[n].text));
        ok(pn + ' is in a job that needs tests', owner.length > 0 && owner.every(n => n !== 'tests' && relJobs[n].needs.indexOf('tests') !== -1),
           'in: ' + (owner.join(', ') || 'no job'));
    });
}

console.log('\n── ci.yml: callable, and every check is one of its jobs ────────');
{
    ok('ci.yml can be called (workflow_call)', /^  workflow_call:/m.test(ci));
    ok('ci.yml still runs on pull requests and on pushes to main and beta',
       /^  pull_request:/m.test(ci) && /^  push:\s*\n\s+branches:\s*\[main, beta\]/m.test(ci));
    // Every job but the last has a timeout and none can be made optional.
    Object.keys(ciJobs).forEach(n => {
        const j = ciJobs[n];
        ok('ci job `' + n + '` has a timeout', j.timeout);
        ok('ci job `' + n + '` is not allowed to fail', !j.jobContinue);
        if (n !== 'passed') ok('ci job `' + n + '` is not gated by an if:', !j.jobIf);
        const stepGated = j.text.split(/\n\s*- (?=name:|uses:|run:)/).slice(1)
            .filter(s => /^\s*(if|continue-on-error):/m.test(s));
        ok('no step of ci job `' + n + '` is gated or allowed to fail', stepGated.length === 0, stepGated.join(' || ').slice(0, 120));
    });
    // The last job: needs every other one, runs even when one failed, and says so.
    const fin = ciJobs.passed;
    ok('ci.yml has a final job named passed', !!fin);
    const rest = Object.keys(ciJobs).filter(n => n !== 'passed');
    rest.forEach(n => {
        ok('the final job needs `' + n + '`', !!fin && fin.needs.indexOf(n) !== -1, fin && ('needs: ' + fin.needs.join(', ')));
        ok('and reads needs.' + n + '.result and requires "success"',
           !!fin && new RegExp('needs\\.' + n + '\\.result').test(fin.text) && /= "success"/.test(fin.text));
    });
    ok('the final job runs under always(), or a failed job would skip it and read as green',
       !!fin && /^    if:\s*\$\{\{\s*always\(\)\s*\}\}\s*$/m.test(fin.text));
    ok('its name is the one to require: "CI passed"', !!fin && /^    name:\s*CI passed\s*$/m.test(fin.text));
}

console.log('\n── ci.yml: every shard, both C# builds, binchk and the static checks ─');
{
    // Every `--shard K/N` names one N, the matrix lists exactly 1..N, and the job title agrees.
    const shardCmds = (ci.match(/tests\/run\.sh js --shard [^\n]+/g) || []);
    ok('ci.yml runs the JavaScript harnesses by shard', shardCmds.length >= 1, 'none');
    const Ns = shardCmds.map(c => (/\/(\d+)\s*$/.exec(c) || [])[1]);
    const N = Number(Ns[0]);
    ok('every --shard command uses the same N (' + Ns.join(',') + ')', Ns.length > 0 && Ns.every(x => x === Ns[0]) && N >= 1);
    ok('and takes K from the matrix', shardCmds.every(c => /--shard \$\{\{ matrix\.shard \}\}\/\d+\s*$/.test(c)), shardCmds.join(' | '));
    const mm = /^        shard:\s*\[([^\]]*)\]\s*$/m.exec(ci);
    const listed = mm ? mm[1].split(',').map(s => Number(s.trim())) : [];
    for (let k = 1; k <= N; k++) ok('the matrix runs shard ' + k + '/' + N, listed.indexOf(k) !== -1, 'matrix: ' + listed.join(','));
    ok('and lists nothing outside 1..' + N + ', once each',
       listed.length === N && listed.every(k => k >= 1 && k <= N) && new Set(listed).size === N, 'matrix: ' + listed.join(','));
    ok('the shard job runs on the matrix, not once', !!ciJobs.js && /^    strategy:\s*\n\s+fail-fast:\s*false\s*\n\s+matrix:/m.test(ciJobs.js.text));
    // fail-fast would cancel the other shards on the first red one, hiding what else is red.
    ok('and a red shard does not cancel the others', !!ciJobs.js && /fail-fast:\s*false/.test(ciJobs.js.text));

    const cs = ciJobs.csharp ? ciJobs.csharp.text : '';
    ok('ci.yml runs the C# harnesses against net9.0 (run.sh cs)',
       /- tfm: net9\.0\s*\n\s+section: cs\s*\n/.test(cs));
    ok('and against net10.0 (run.sh cs10)', /- tfm: net10\.0\s*\n\s+section: cs10\s*\n/.test(cs));
    ok('with the section passed to run.sh', /run:\s*bash tests\/run\.sh \$\{\{ matrix\.section \}\}\s*$/m.test(cs));
    ok('and the matrix is only those two', (cs.match(/- tfm:/g) || []).length === 2);
    ok('ci.yml checks the plugin against newer Jellyfin (binchk)', !!ciJobs.binchk && /run:\s*bash tests\/binchk\.sh\s*$/m.test(ciJobs.binchk.text));
    ok('with the build binchk reads made by itself, not an unbuilt DLL', !!ciJobs.binchk && !/SKIP_BUILD/.test(ciJobs.binchk.text));
    ok('ci.yml validates translations', !!ciJobs.static && /run:\s*node Web\/i18n\/validate\.js\s*$/m.test(ciJobs.static.text));
    ok('ci.yml syntax-checks the client script', !!ciJobs.static && /run:\s*node --check Web\/profiles\.js\s*$/m.test(ciJobs.static.text));
    // Every job that builds or runs a harness installs what it needs.
    ['js', 'csharp', 'binchk', 'static', 'webkit'].forEach(n => ok('ci job `' + n + '` installs Node', !!ciJobs[n] && /setup-node/.test(ciJobs[n].text)));
    // The WebKit harnesses skip when Playwright is missing; the job that exists to run them must not.
    const wk = ciJobs.webkit ? ciJobs.webkit.text : '';
    ok('ci job `webkit` installs the pinned Playwright WebKit', /npm ci/.test(wk) && /playwright-core install[^\n]*webkit/.test(wk));
    ok('and makes a missing install a failure, not a skip', /BONFIRE_WEBKIT_REQUIRED:\s*'1'/.test(wk));
    ['webkitstartup', 'webkitlayout', 'webkitflows'].forEach(h => ok('and runs ' + h, new RegExp('js/' + h + '\\b').test(wk)));
    ['csharp', 'binchk'].forEach(n => {
        ok('ci job `' + n + '` installs the .NET 9 SDK', !!ciJobs[n] && /9\.0\.x/.test(ciJobs[n].text));
        ok('ci job `' + n + '` installs the .NET 10 SDK', !!ciJobs[n] && /10\.0\.x/.test(ciJobs[n].text));
    });
}

console.log('\n── ci.yml: the live job starts a real Jellyfin, pinned ─────────');
{
    const live = ciJobs.live ? ciJobs.live.text : '';
    ok('ci.yml has a `live` job', !!ciJobs.live);
    // The matrix, entry by entry: a total would let one unpinned entry hide behind the rest.
    const entries = live.split(/^\s+- name: /m).slice(1).filter(e => /^\s+os:/m.test(e)).map(e => 'name: ' + e);
    const docker = entries.filter(e => /^\s+image:/m.test(e));
    const windows = entries.filter(e => /^\s+zip:/m.test(e));
    ok('every matrix entry is a Docker image or a Windows zip, not neither', entries.length >= 1 && docker.length + windows.length === entries.length, entries.length + ' entries');
    docker.forEach(e => {
        const nm = /name:\s*(.*)/.exec(e)[1].trim();
        ok(nm + ': the image is jellyfin/jellyfin pinned by sha256 digest',
           /^\s+image:\s*jellyfin\/jellyfin@sha256:[0-9a-f]{64}\s*$/m.test(e));
        ok(nm + ': runs on a Linux runner', /^\s+os:\s*ubuntu-/m.test(e));
    });
    windows.forEach(e => {
        const nm = /name:\s*(.*)/.exec(e)[1].trim();
        const zip = /^\s+zip:\s*(\S+)\s*$/m.exec(e);
        const v = zip && /\/stable\/v([\d.]+)\/amd64\/jellyfin_([\d.]+)-amd64\.zip$/.exec(zip[1]);
        ok(nm + ': the zip is an exact version on repo.jellyfin.org (the folder and file agree)',
           !!zip && /^https:\/\/repo\.jellyfin\.org\/files\/server\/windows\/stable\//.test(zip[1]) && !!v && v[1] === v[2], zip && zip[1]);
        ok(nm + ': carries the SHA-256 it is verified against', /^\s+sha256:\s*[0-9a-f]{64}\s*$/m.test(e));
        ok(nm + ': runs on a Windows runner', /^\s+os:\s*windows-/m.test(e));
        ok(nm + ': is named for the version it downloads', !!v && nm.indexOf(v[1]) !== -1, nm);
    });
    ok('both server lines are covered on both platforms (10.11.x and 12.x, Docker and Windows)',
       [docker, windows].every(list => list.some(e => /10\.11\./.test(e)) && list.some(e => /\b12\./.test(e))));
    // The checksum is checked, not just recorded.
    ok('the Windows leg verifies the zip with sha256sum -c before unpacking it',
       /sha256sum -c/.test(live) && live.indexOf('sha256sum -c') < live.indexOf('unzip'));
    ok('the job installs the plugin build that ships and runs tests/live/smoke.sh with it',
       /tests\/live\/smoke\.sh --plugin bin\/Release\/net9\.0 --image/.test(live) && /tests\/live\/smoke\.sh --plugin bin\/Release\/net9\.0 --server/.test(live));
    ok('the job installs Node and both SDKs, because it builds both frameworks and runs inject.test.js',
       /setup-node/.test(live) && /9\.0\.x/.test(live) && /10\.0\.x/.test(live));
    ok('a red leg does not cancel the others', /fail-fast:\s*false/.test(live));
    ok('the script exists', fs.existsSync(path.join(L.ROOT, 'tests', 'live', 'smoke.sh')));
}

console.log('\n── ci.yml ──────────────────────────────────────────────────────');
ok('ci.yml sets top-level permissions: contents: read', /^permissions:\s*\n\s+contents:\s*read\s*$/m.test(ci));
ok('ci.yml gives every job a timeout', Object.keys(ciJobs).length > 0 && Object.keys(ciJobs).every(n => ciJobs[n].timeout));
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
