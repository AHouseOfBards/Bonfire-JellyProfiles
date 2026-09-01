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

// Exactly one entry may still carry the "0" placeholder: the release being prepared right
// now. The workflow stamps the real checksum when the tag is pushed, so between adding the
// entry and tagging it, the placeholder is the correct content — and refusing it here
// would make CI red on every release commit.
//
// Pinned to the version in the csproj rather than "the newest entry", so it cannot drift
// into a general excuse. Any other placeholder is the bug that made two releases
// uninstallable.
const csproj = fs.readFileSync(path.join(L.ROOT, 'Jellyfin.Profiles.csproj'), 'utf8');
const inFlight = (/<Version>([^<]+)<\/Version>/.exec(csproj) || [])[1];
ok('the csproj names a version to release (' + (inFlight || 'none found') + ')', !!inFlight);

const pending = inFlight ? inFlight + '.0' : null;

// Enumerated one version at a time. An "all checksums look fine" aggregate is exactly the
// shape of check that let two uninstallable releases through.
let placeholders = 0;
versions.forEach(function (v) {
    const sum = String(v.checksum || '');
    const real = /^[0-9a-f]{32}$/i.test(sum) && !/^0+$/.test(sum);

    if (!real && v.version === pending) {
        placeholders++;
        console.log('  note  ' + v.version + ' still has the placeholder — this is the '
                    + 'release being prepared; the workflow stamps it on tag push');
        return;
    }
    ok(v.version + ' has a real checksum, not the placeholder', real);
});

ok('at most one entry is waiting to be stamped (' + placeholders + ')', placeholders <= 1);

// One historical tag does not follow the convention: 1.1.0.0 was released as `v1.1`,
// before the three-part tag name settled. The URL is right — it serves, and `gh release
// view v1.1` lists the asset — so the check's premise is what is wrong for that one
// entry, not the entry. Named explicitly rather than loosening the pattern for all of
// them, so the next release still has to match exactly.
//
// This only surfaced when the harness first ran against main's manifest during the 1.6.0
// cut; until then it had only ever seen beta's.
const TAG_EXCEPTIONS = { '1.1.0.0': 'v1.1' };

versions.forEach(function (v) {
    const tag = TAG_EXCEPTIONS[v.version] || ('v' + v.version.replace(/\.0$/, ''));
    ok(v.version + ' points its sourceUrl at its own tag (' + tag + ')',
       typeof v.sourceUrl === 'string' && v.sourceUrl.indexOf('/' + tag + '/') !== -1);
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
