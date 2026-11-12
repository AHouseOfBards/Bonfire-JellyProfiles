/*
 * Which runtimes the plugin is built for, and which of those builds users actually get.
 *
 * Jellyfin 10.11.x runs on .NET 9; 12.0 runs on .NET 10. The plugin multi-targets both,
 * but the two builds are NOT interchangeable and only one of them can ship:
 *
 *   net9.0   loads on 10.11.x AND on 12.0 — the .NET 10 runtime accepts a net9 assembly,
 *            which is what 1.6.0.1 through 1.6.1 shipped and what a live 12.0 server has
 *            been confirmed running.
 *   net10.0  loads on 12.0 only.
 *
 * So the packaging step in release.yml naming net9.0 is not an implementation detail, it
 * is the line that decides whether half the userbase can install the plugin at all. And
 * the failure is silent from this side: a net10.0 artefact builds, packages, uploads,
 * passes its checksum, installs on 12.0, and simply never loads on 10.11 — where the only
 * symptom is the plugin not appearing, with the reason buried in the server log.
 *
 * `targetAbi` cannot express the difference, because it filters on the Jellyfin version
 * and the constraint is on the runtime.
 *
 *   node tests/js/buildtargets.js
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

const csproj = fs.readFileSync(path.join(L.ROOT, 'Jellyfin.Profiles.csproj'), 'utf8');
const release = fs.readFileSync(path.join(L.ROOT, '.github', 'workflows', 'release.yml'), 'utf8');
const ci = fs.readFileSync(path.join(L.ROOT, '.github', 'workflows', 'ci.yml'), 'utf8');

// Comments in all three files talk about net9.0 and net10.0 at length, and a check that
// searched the raw text would be satisfied by the prose explaining the rule rather than by
// the rule. Strip them first — the same mistake a CSS check made when it went red on the
// comment saying the value it wanted gone was gone.
const csprojCode = csproj.replace(/<!--[\s\S]*?-->/g, '');
// Whole-line comments, and a trailing ` # ...` after a command: `run: echo skipped # run.sh cs10`
// satisfied the cs10 check below while running nothing.
const stripYamlComments = y => y.split('\n').filter(l => !/^\s*#/.test(l))
    .map(l => l.replace(/\s#\s.*$/, '')).join('\n');
const releaseCode = stripYamlComments(release);
const ciCode = stripYamlComments(ci);

console.log('\n── the plugin is built for both runtimes ───────────────────────');

const tfms = (/<TargetFrameworks>([^<]+)<\/TargetFrameworks>/.exec(csprojCode) || [])[1];
ok('the csproj declares <TargetFrameworks> (' + (tfms || 'none') + ')', !!tfms,
   'a single <TargetFramework> means one of the two servers is unbuilt');

const list = (tfms || '').split(';').map(t => t.trim()).filter(Boolean);
// Enumerated rather than counted: "two frameworks are listed" is satisfied by the wrong
// two, and an aggregate would not say which one went missing.
ok('net9.0 is one of them, for Jellyfin 10.11.x', list.indexOf('net9.0') !== -1, list.join(', '));
ok('net10.0 is one of them, for Jellyfin 12.0', list.indexOf('net10.0') !== -1, list.join(', '));

console.log('\n── but only the net9.0 build is shipped ────────────────────────');

const zipLine = (releaseCode.split('\n').find(l => /zip .*Jellyfin\.Profiles\.zip/.test(l)) || '').trim();
ok('release.yml has a packaging step at all', !!zipLine);
ok('and it packages the net9.0 output', /bin\/Release\/net9\.0\/Jellyfin\.Profiles\.dll/.test(zipLine),
   zipLine || 'no zip line found');
ok('not the net10.0 one, which would not load on 10.11.x',
   zipLine.indexOf('net10.0') === -1, zipLine);

// Only the FIRST zip line was read, so a second one adding the net10.0 assembly to the
// same archive stayed green. And the whole file is searched, because the build step can
// narrow itself to one framework (`dotnet build -f net9.0`) and leave the other unbuilt
// while the csproj above still lists both.
ok('no other line of release.yml, packaging or otherwise, names net10.0',
   !/net10\.0/.test(releaseCode),
   releaseCode.split('\n').filter(l => /net10\.0/.test(l)).join(' | '));
const narrowed = [].concat.apply([], [['release.yml', releaseCode], ['ci.yml', ciCode]].map(([n, body]) =>
    body.split('\n').filter(l => /\bdotnet\s+(build|publish|test|run|pack)\b/.test(l)
        && /(\s-f\b|--framework\b|TargetFramework)/.test(l)).map(l => n + ': ' + l.trim())));
ok('neither workflow narrows a dotnet build to one framework', narrowed.length === 0,
   narrowed.join(' | '));

// The reason has to travel with the line. Someone bumping frameworks a year from now
// reads the step, not this harness, and "net9.0" looks exactly like something left behind.
ok('and the reason it is net9.0 is written next to it',
   /net9\.0 DELIBERATELY/.test(release) && /10\.11/.test(release));

console.log('\n── both SDKs are installed, or one build never happens ─────────');

// setup-dotnet silently installs only what it is given, and `dotnet build` on a project
// multi-targeting a framework whose SDK is absent fails at restore — loudly in CI, but
// only for the framework nobody is watching.
[['release.yml', releaseCode], ['ci.yml', ciCode]].forEach(([name, body]) => {
    ok(name + ' installs the .NET 9 SDK', /9\.0\.x/.test(body));
    ok(name + ' installs the .NET 10 SDK', /10\.0\.x/.test(body));
});

// Compiling the net10.0 build proves nothing about whether it runs — that distinction
// shipped 1.5.2 and 1.5.3-beta dead on arrival. `tests/run.sh cs10` loads the net10.0
// assembly and reflects on it.
ok('CI actually runs the harnesses against the net10.0 build',
   /tests\/run\.sh cs10/.test(ciCode));
// Present is not the same as running: the step can be gated off or allowed to fail.
{
    const steps = ciCode.split(/\n\s*- (?=name:|uses:|run:)/);
    const cs10Step = steps.filter(s => /tests\/run\.sh cs10/.test(s));
    ok('and that step is not gated by an if: or allowed to fail',
       cs10Step.length >= 1 && cs10Step.every(s => !/^\s*(if|continue-on-error):/m.test(s)),
       cs10Step.join(' || '));
}
// And the runners must really map cs10 to net10.0, and pass the choice to every harness.
{
    const sh = fs.readFileSync(path.join(L.ROOT, 'tests', 'run.sh'), 'utf8');
    const ps = fs.readFileSync(path.join(L.ROOT, 'tests', 'run.ps1'), 'utf8');
    ok('run.sh runs net9.0 by default and net10.0 for cs10',
       /^TFM="net9\.0"\s*$/m.test(sh) && /^\[ "\$WHICH" = "cs10" \] && TFM="net10\.0"\s*$/m.test(sh));
    ok('run.ps1 runs net9.0 by default and net10.0 for cs10',
       /\$tfm = if \(\$Which -eq 'cs10'\) \{ 'net10\.0' \} else \{ 'net9\.0' \}/.test(ps));
    ok('run.sh hands the framework to every harness it runs',
       sh.split('\n').filter(l => /dotnet run --project/.test(l)).every(l => /-p:BonfireTfm="\$TFM"/.test(l)));
    ok('run.ps1 hands the framework to every harness it runs',
       ps.split('\n').filter(l => /dotnet run --project/.test(l)).every(l => /-p:BonfireTfm=\$tfm/.test(l)));
}

console.log('\n── the harnesses can be pointed at either build ────────────────');

const csDir = path.join(L.ROOT, 'tests', 'cs');
const projects = fs.readdirSync(csDir).filter(d => fs.statSync(path.join(csDir, d)).isDirectory());

// The first version of this asked "does net9.0 appear in the file", which every project
// answered yes to — because the overridable default is spelled net9.0, and correctly so.
// That is the coarser-question failure this repository keeps finding, caught by its own
// harness on the first run. What actually matters is whether net9.0 is *pinned*: fixed
// where `cs10` cannot move it. So the three ways to pin it are named, and the one
// sanctioned mention is not among them.
const PINS = [
    ['<TargetFramework> hardcoded', /<TargetFramework>\s*net9\.0\s*<\/TargetFramework>/],
    ['a literal net9.0 in a reference path', /bin[\\/]Release[\\/]net9\.0[\\/]/],
    ['a literal "net9.0" in C#', /"net9\.0"/]
];

// One project at a time, and one way at a time. A count would pass with the right total
// and the wrong members, and would not say which pin to go and remove.
const pinned = [];
projects.forEach(name => {
    const dir = path.join(csDir, name);
    fs.readdirSync(dir).filter(f => /\.(csproj|cs)$/.test(f)).forEach(f => {
        const body = fs.readFileSync(path.join(dir, f), 'utf8')
            .replace(/<!--[\s\S]*?-->/g, '')
            .split('\n').filter(l => !/^\s*\/\//.test(l)).join('\n');
        PINS.forEach(([why, re]) => {
            if (re.test(body)) pinned.push(name + '/' + f + ' (' + why + ')');
        });
    });
});

// The three pins above name three spellings. A fourth (`<TargetFrameworks>net9.0`,
// `bin\$(Configuration)\net9.0`, `<BonfireTfm>net9.0` set after the default, a hardcoded
// net10.0) got past them, so the rule is also stated the other way round: outside the one
// sanctioned default, no harness project names either framework in code.
const SANCTIONED = /<BonfireTfm Condition="'\$\(BonfireTfm\)' == ''">net9\.0<\/BonfireTfm>/;
const stray = [];
const wrongTfm = [];
projects.forEach(name => {
    const dir = path.join(csDir, name);
    fs.readdirSync(dir).filter(f => /\.(csproj|cs)$/.test(f)).forEach(f => {
        const raw = fs.readFileSync(path.join(dir, f), 'utf8');
        const body = raw.replace(/<!--[\s\S]*?-->/g, '')
            .split('\n').filter(l => !/^\s*\/\//.test(l)).join('\n')
            .replace(SANCTIONED, '').replace(/\/\/.*$/gm, '');
        if (/net(9|10)\.0/.test(body)) stray.push(name + '/' + f);
        if (/\.csproj$/.test(f)) {
            const tf = (/<TargetFramework>([^<]*)<\/TargetFramework>/.exec(body) || [])[1];
            if (tf !== '$(BonfireTfm)') wrongTfm.push(name + '/' + f + ' (' + tf + ')');
        }
    });
});
ok('no harness names net9.0 or net10.0 in code except the one sanctioned default',
   stray.length === 0, stray.join('; '));
ok('every harness project is built for exactly $(BonfireTfm)', wrongTfm.length === 0,
   wrongTfm.join('; '));

ok('there are C# harnesses to check (' + projects.length + ')', projects.length >= 9);
ok('and none of them pins itself to net9.0', pinned.length === 0, pinned.join('; '));

// The other half of the same question: they must actually honour the switch. A project
// with no BonfireTfm at all would pass the pin check above by saying nothing.
const notWired = projects.filter(name => {
    const dir = path.join(csDir, name);
    const proj = fs.readdirSync(dir).find(f => /\.csproj$/.test(f));
    return !proj || !/\$\(BonfireTfm\)/.test(fs.readFileSync(path.join(dir, proj), 'utf8'));
});
ok('and every one of them reads $(BonfireTfm)', notWired.length === 0, notWired.join(', '));

console.log('\n── a release runs the whole suite before it builds anything ────');

// release.yml built, packaged and published on a tag with no test run at all: CI had
// passed on the commit, but a tag can be cut from a commit nobody ran CI on, and the
// workflow has no way to know. Every step that produces or publishes something has to come
// after every step that can say no.
{
    const rsteps = releaseCode.split(/\n\s*- (?=name:|uses:|run:)/);
    const idx = re => rsteps.findIndex(s => re.test(s));
    const gated = s => /^\s*(if|continue-on-error):/m.test(s);
    const gates = [
        ['tests/run.sh (the same command CI runs)', /^\s*run:\s*bash tests\/run\.sh\s*$/m],
        ['tests/run.sh cs10', /^\s*run:\s*bash tests\/run\.sh cs10\s*$/m],
        ['node Web/i18n/validate.js', /^\s*run:\s*node Web\/i18n\/validate\.js\s*$/m]
    ];
    const producers = [
        ['the build', /dotnet build/],
        ['the packaging step', /zip .*Jellyfin\.Profiles\.zip/],
        ['the manifest push', /git push origin/],
        ['the GitHub release', /action-gh-release/]
    ];
    // Pairwise, one gate and one producer at a time: "the tests come first" as a single
    // aggregate would pass with one gate in the right place and two missing.
    gates.forEach(([gname, gre]) => {
        const gi = idx(gre);
        ok('release.yml runs ' + gname, gi !== -1);
        ok('and that step is not gated by an if: or allowed to fail',
           gi !== -1 && !gated(rsteps[gi]));
        producers.forEach(([pname, pre]) => {
            const pi = idx(pre);
            ok(gname + ' runs before ' + pname, gi !== -1 && pi !== -1 && gi < pi,
               'gate step ' + gi + ', ' + pname + ' step ' + pi);
        });
    });
    ok('release.yml installs Node, which validate.js and the JS harnesses need',
       /setup-node/.test(releaseCode));
}

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
