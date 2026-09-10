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
const stripYamlComments = y => y.split('\n').filter(l => !/^\s*#/.test(l)).join('\n');
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

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
