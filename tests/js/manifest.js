/*
 * The manifest on this branch.
 *
 * Jellyfin polls it, reads the checksum, downloads the artifact and refuses to install if
 * the two disagree. Everything here has cost a release:
 *
 *  - two releases shipped with the "0" placeholder still in place and could not be
 *    installed at all;
 *  - 1.1.2, 1.1.4 and 1.1.6 all carry 0b6481f7f56de24f3c9757d67a73f181: three tags, three
 *    sets of release notes, one binary. Every release-workflow check asked whether a
 *    checksum had been written, never whether it was new. The workflow now refuses a
 *    checksum that already belongs to another version in the same manifest; this is the
 *    standing check that the manifest in the tree is clean.
 *
 * Run against the file on whichever branch is checked out, so it covers whichever channel
 * is about to be released.
 *
 *   node tests/js/manifest.js [release.yml [_ [manifest.json]]]
 *
 * A manifest entry is no longer written by hand. The release notes live in
 * release-notes/<version>.md, and release.yml adds the finished entry (real checksum
 * included) after the release is published, so no server is offered a version before it can
 * be installed. A placeholder checksum in the tree is therefore always a bug.
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
}

const WF = process.argv[2] || path.join(L.ROOT, '.github', 'workflows', 'release.yml');
const MANIFEST = process.argv[4] || path.join(L.ROOT, 'manifest.json');
const raw = fs.readFileSync(MANIFEST, 'utf8').replace(/^﻿/, '');

let doc = null;
try { doc = JSON.parse(raw); } catch (e) { /* reported below */ }

console.log('\n── The manifest is a manifest ──────────────────────────────────');

ok('it parses as JSON', doc !== null);
ok('it is a list of one plugin', Array.isArray(doc) && doc.length === 1);

const versions = (doc && doc[0] && doc[0].versions) || [];
ok('with versions (' + versions.length + ')', versions.length > 0);

// Three releases sharing one artifact went unnoticed for months. This is the only place
// that would have said so before it shipped.
console.log('\n── No two versions ship the same artefact ──────────────────────');

// The three below already shipped and cannot be unshipped. They are named so the check
// still reports every other collision loudly, and so removing one from the manifest also
// removes it here.
const KNOWN_HISTORICAL = {
    '0b6481f7f56de24f3c9757d67a73f181': ['1.1.2.0', '1.1.4.0', '1.1.6.0']
};

const byChecksum = {};
versions.forEach(function (v) {
    (byChecksum[v.checksum] = byChecksum[v.checksum] || []).push(v.version);
});

let collisions = 0;
Object.keys(byChecksum).forEach(function (sum) {
    const who = byChecksum[sum];
    if (who.length < 2) return;

    const known = KNOWN_HISTORICAL[sum];
    // Matched pairwise against the recorded set, not just by count: a new version joining
    // a known collision is a new bug wearing an old bug's checksum.
    const isExactlyKnown = known
        && known.length === who.length
        && who.every(function (v) { return known.indexOf(v) !== -1; });

    if (isExactlyKnown) {
        console.log('  note  ' + sum + ' is shared by ' + who.join(', ')
                    + ' — already shipped, recorded, cannot be undone');
        return;
    }
    collisions++;
    ok('a checksum is shared by ' + who.join(', ') + ' (' + sum + ') — the build is '
       + 'byte-identical to one that already shipped, so the release notes describe '
       + 'changes that are not in the artefact', false);
});
if (collisions === 0) ok('every version has its own artefact', true);

console.log('\n── Every entry is installable ──────────────────────────────────');

// No entry may carry the "0" placeholder. The workflow used to need one, written before the
// tag and stamped afterwards, so every server on the channel was offered a version whose
// install failed. It now writes the entry whole after the release, so a placeholder in the
// tree means a hand-written entry, and the workflow will refuse the tag (the version is
// already in the manifest).
const csproj = fs.readFileSync(path.join(L.ROOT, 'Jellyfin.Profiles.csproj'), 'utf8');
const inFlight = (/<Version>([^<]+)<\/Version>/.exec(csproj) || [])[1];
ok('the csproj names a version to release (' + (inFlight || 'none found') + ')', !!inFlight);

// Enumerated one version at a time. An "all checksums look fine" aggregate is exactly the
// shape of check that let two uninstallable releases through.
versions.forEach(function (v) {
    const sum = String(v.checksum || '');
    const real = /^[0-9a-f]{32}$/i.test(sum) && !/^0+$/.test(sum);
    ok(v.version + ' has a real checksum, not the placeholder', real);
});

// One historical tag does not follow the convention: 1.1.0.0 was released as `v1.1`, before
// the three-part tag name settled. Its URL is right (it serves, and `gh release view v1.1`
// lists the asset), so the check's premise is wrong for that entry, not the entry. Named
// explicitly rather than loosening the pattern, so the next release still has to match.
//
// This surfaced when the harness first ran against main's manifest in the 1.6.0 cut; it had
// only seen beta's before.
const TAG_EXCEPTIONS = { '1.1.0.0': 'v1.1' };

versions.forEach(function (v) {
    const tag = TAG_EXCEPTIONS[v.version] || ('v' + v.version.replace(/\.0$/, ''));
    ok(v.version + ' points its sourceUrl at its own tag (' + tag + ')',
       typeof v.sourceUrl === 'string' && v.sourceUrl.indexOf('/' + tag + '/') !== -1);
});

console.log('\n── The workflow refuses what this file must never contain ──────');

// The manifest being clean today does not stop the next release dirtying it; the workflow
// guard does. Checked here because a green manifest looks the same with the guard deleted.
const wf = fs.readFileSync(WF, 'utf8');
ok('the release workflow rejects a checksum that already belongs to another version',
   /select\(\.checksum == \$c and \.version != \$v\)/.test(wf));
ok('and says so as an error rather than a warning',
   /same checksum as/.test(wf) && /::error::/.test(wf));

console.log('\n── The release notes are in the file the workflow reads ─────────');

// CHANGELOG.md stays a manual step, done in the same commit as the notes file: a workflow
// edit would need a second push to a branch (main) the beta channel does not run from, and
// people read the changelog and want it reviewed. This holds that the two agree.
const NOTES_DIR = path.join(L.ROOT, 'release-notes');
const changelog = fs.readFileSync(path.join(L.ROOT, 'CHANGELOG.md'), 'utf8').replace(/\r\n/g, '\n');
const noteFiles = fs.existsSync(NOTES_DIR) ? fs.readdirSync(NOTES_DIR).filter(n => !/^\./.test(n)) : [];
noteFiles.forEach(function (n) {
    ok('release-notes/' + n + ' is named for a four-part version', /^\d+\.\d+\.\d+\.\d+\.md$/.test(n));
    const text = fs.readFileSync(path.join(NOTES_DIR, n), 'utf8').replace(/\r\n/g, '\n');
    ok('release-notes/' + n + ' is not empty', text.trim() !== '');
    // The "**Beta release** - please report issues on GitHub." banner is for the update
    // dialog; CHANGELOG.md says which versions were betas in its own header and leaves it
    // out of every section, so it is the one line not looked for.
    const missing = text.split('\n').map(l => l.trim())
        .filter(l => l && !/^\*\*Beta release\*\*/.test(l) && changelog.indexOf(l) === -1);
    ok('every line of release-notes/' + n + ' is in CHANGELOG.md', missing.length === 0,
       'missing: ' + missing.slice(0, 2).join(' | '));
    const inManifest = versions.some(v => v.version === n.replace(/\.md$/, ''));
    if (inManifest) {
        const entry = versions.find(v => v.version === n.replace(/\.md$/, ''));
        ok('release-notes/' + n + ' is what the ' + entry.version + ' manifest entry says',
           String(entry.changelog).trim() === text.trim());
    }
});
ok('release.yml reads the notes from release-notes/<version>.md and refuses when missing',
   /release-notes\/\$\{V\}\.md/.test(wf) && /-s "\$NOTES"/.test(wf));
ok('and writes the entry itself, checksum included',
   /--rawfile notes/.test(wf) && /checksum: \$c/.test(wf));
ok('and refuses a version that is already in either manifest',
   /for B in main beta/.test(wf) && /already in the manifest on/.test(wf));

console.log('\n── Campground hears about stable releases, and only those ──────');

// Campground offers everything it is told about to every server on its link, so a beta
// reaching it is a beta offered to everyone. It also copies the entry it is told about,
// so it must not hear before the bot's checksum commit and the download are verified.
const steps = wf.split(/\n(?=      - name: )/);
const tellIdx = steps.findIndex(function (s) { return /Campground-Jellyfin\/dispatches/.test(s); });
const tell = tellIdx === -1 ? '' : steps[tellIdx];
const verifyIdx = steps.findIndex(function (s) { return /name: Verify published asset/.test(s); });
ok('a release tells Campground', tellIdx !== -1);
ok('only a stable release does', /\n\s+if: steps\.version\.outputs\.channel == 'stable'\n/.test(tell));
ok('and only after the published download is verified', verifyIdx !== -1 && tellIdx > verifyIdx);
// The release is already public by now. A dead token must not paint it red.
ok('a failure there does not fail the release', /\n\s+continue-on-error: true\n/.test(tell));
ok('it names the plugin Campground lists it under', /client_payload\[plugin\]=Bonfire/.test(tell));
ok('and the version, passed through env rather than pasted into the script',
   /\n\s+V: \$\{\{ steps\.version\.outputs\.version \}\}\n/.test(tell) &&
   /client_payload\[version\]=\$V/.test(tell));

console.log('\n── Withdrawn versions stay withdrawn ───────────────────────────');

// 1.6.1.2 and 1.6.1.3 blanked every sub-profile's authentication provider id
// (ProfilesBootstrapTask, gone at 1.6.1.4), which took down user lookup for a whole
// 40-user server and needed manual SQL to undo. Jellyfin's plugin page offers every listed
// version as a rollback, so a version that can do that must not be listed (R-1317).
const WITHDRAWN = ['1.6.1.2', '1.6.1.3'];
WITHDRAWN.forEach(function (bad) {
    ok(bad + ' is not offered', !versions.some(function (v) { return v.version === bad; }));
});

console.log('');
if (fails.length) {
    fails.forEach(function (f) { console.log('   - ' + f); });
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
