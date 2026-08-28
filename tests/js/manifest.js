/*
 * The manifest on this branch.
 *
 * Jellyfin polls it, reads the checksum, downloads the artefact and refuses to install if
 * the two disagree. Everything here has cost a release:
 *
 *  - two releases shipped with the "0" placeholder still in place and could not be
 *    installed at all;
 *  - 1.1.2, 1.1.4 and 1.1.6 all carry 0b6481f7f56de24f3c9757d67a73f181 — three tags,
 *    three sets of release notes, one binary. Nothing noticed, and nothing could have:
 *    every check in the release workflow asked whether a checksum had been written, never
 *    whether it was new. The workflow now refuses a checksum that already belongs to
 *    another version in the same manifest; this is the standing check that the manifest
 *    in the tree is clean.
 *
 * Run against the file on whichever branch is checked out, so it covers whichever channel
 * is about to be released.
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

const MANIFEST = process.argv[4] || path.join(L.ROOT, 'manifest.json');
const raw = fs.readFileSync(MANIFEST, 'utf8').replace(/^﻿/, '');

let doc = null;
try { doc = JSON.parse(raw); } catch (e) { /* reported below */ }

console.log('\n── The manifest is a manifest ──────────────────────────────────');

ok('it parses as JSON', doc !== null);
ok('it is a list of one plugin', Array.isArray(doc) && doc.length === 1);

const versions = (doc && doc[0] && doc[0].versions) || [];
ok('with versions (' + versions.length + ')', versions.length > 0);

// Three releases sharing one artefact went unnoticed for months. This is the only place
// that would have said so before it shipped.
console.log('\n── No two versions ship the same artefact ──────────────────────');

// The three below already shipped and cannot be unshipped. They are listed by name so
// that this check reports every *other* collision loudly rather than being switched off,
// and so that removing one of them from the manifest also removes it from here.
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

// Enumerated one version at a time. A "all checksums look fine" aggregate is exactly the
// shape of check that let two uninstallable releases through.
versions.forEach(function (v) {
    const sum = String(v.checksum || '');
    ok(v.version + ' has a real checksum, not the placeholder',
       /^[0-9a-f]{32}$/i.test(sum) && !/^0+$/.test(sum));
});

versions.forEach(function (v) {
    ok(v.version + ' points its sourceUrl at its own tag',
       typeof v.sourceUrl === 'string'
       && v.sourceUrl.indexOf('/v' + v.version.replace(/\.0$/, '') + '/') !== -1);
});

console.log('\n── The workflow refuses what this file must never contain ──────');

// The manifest being clean today does not stop the next release dirtying it, so the
// guard in the workflow is what keeps it clean. Checked here because a green manifest
// with the guard deleted looks exactly like a green manifest with it.
const wf = fs.readFileSync(path.join(L.ROOT, '.github', 'workflows', 'release.yml'), 'utf8');
ok('the release workflow rejects a checksum that already belongs to another version',
   /select\(\.checksum == \$c and \.version != \$v\)/.test(wf));
ok('and says so as an error rather than a warning',
   /same checksum as/.test(wf) && /::error::/.test(wf));

console.log('');
if (fails.length) {
    fails.forEach(function (f) { console.log('   - ' + f); });
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
