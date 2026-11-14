/*
 * The beta feed holds the newest three entries, and the release job keeps it that way.
 *
 * Everyone subscribed to the beta repository downloads the manifest on every refresh, and
 * the Jellyfin plugin page lists every entry in it. Seventy-odd entries going back to 1.1
 * is not a feed, it is an archive; the GitHub releases are the archive and stay. Three is
 * enough to roll back one or two builds.
 *
 * Two halves, because either alone is a promise:
 *   - the manifest on a beta line has at most three entries now;
 *   - release.yml prunes to three right after it adds the new entry, on the beta channel
 *     only, before anything is committed or pushed, so the oldest entry is the one that
 *     goes. A one-off prune of the file would be back to 4 at the next tag.
 *
 * main's stable manifest is a curated milestone list, not a feed, so the first half only
 * applies when the csproj says this tree is a beta (InformationalVersion, the one place
 * the channel lives).
 *
 *   node tests/js/betafeed.js [manifest.json [release.yml]]
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

const LIMIT = 3;
const csproj = fs.readFileSync(path.join(L.ROOT, 'Jellyfin.Profiles.csproj'), 'utf8').replace(/<!--[\s\S]*?-->/g, '');
const info = (/<InformationalVersion>([^<]*)<\/InformationalVersion>/.exec(csproj) || [])[1] || '';
const isBeta = /-(beta|alpha|rc)/.test(info);

console.log('\n── the manifest on a beta line is a feed of ' + LIMIT + ' ─────────────────────');
if (isBeta) {
    const doc = JSON.parse(fs.readFileSync(process.argv[2] || path.join(L.ROOT, 'manifest.json'), 'utf8').replace(/^﻿/, ''));
    const n = doc[0].versions.length;
    ok('this tree is a beta (' + info + ') and its manifest has at most ' + LIMIT + ' entries (' + n + ')', n <= LIMIT);
    // Newest first is what both Jellyfin and the prune rely on; pruning a mis-sorted file
    // would keep the wrong three.
    const key = v => v.version.split('.').map(Number);
    const newer = (a, b) => { for (let j = 0; j < Math.max(a.length, b.length); j++) { const d = (a[j] || 0) - (b[j] || 0); if (d) return d > 0; } return false; };
    const sorted = doc[0].versions.every((v, i, a) => i === 0 || !newer(key(v), key(a[i - 1])));
    ok('and they are listed newest first', sorted);
} else {
    console.log('  note  InformationalVersion is "' + info + '", a stable tree: its manifest is a curated list, not the feed');
}

console.log('\n── release.yml prunes the beta feed after adding to it ─────────');

const yml = fs.readFileSync(process.argv[3] || path.join(L.ROOT, '.github', 'workflows', 'release.yml'), 'utf8');
const code = yml.split('\n').filter(l => !/^\s*#/.test(l)).join('\n');
const steps = code.split(/\n\s*- (?=name:|uses:|run:)/);
const upd = steps.find(s => /checksum: \$c/.test(s)) || '';
ok('there is a step that adds the manifest entry', upd !== '');

const pruneAt = upd.search(/\.\[0\]\.versions\s*\|=[^\n]*(\[:3\]|\[0:3\]|limit\(3|\.\[:3\])/);
const stampAt = upd.search(/checksum: \$c/);
const commitAt = upd.search(/git commit/);
const pushAt = upd.search(/git push origin/);
ok('it keeps ' + LIMIT + ' entries with jq', pruneAt !== -1);
ok('after the new entry is added, so the oldest one is what goes', pruneAt !== -1 && stampAt !== -1 && pruneAt > stampAt);
ok('before the commit', pruneAt !== -1 && commitAt !== -1 && pruneAt < commitAt);
ok('before the push', pruneAt !== -1 && pushAt !== -1 && pruneAt < pushAt);
ok('sorted by version first, so it keeps the newest and not the first three in the file',
   /sort_by\([^)]*version[^)]*\)/.test(upd.slice(Math.max(0, pruneAt - 10), pruneAt + 300)));
// The prune is the beta channel's. main's manifest is the milestone list Campground and
// every stable install read, and trimming it would withdraw releases people are on.
const before = upd.slice(0, pruneAt);
const lastIf = before.lastIndexOf('if [');
const guarded = lastIf !== -1 && /"\$BRANCH"\s*=\s*"beta"/.test(before.slice(lastIf))
    && !/\bfi\b/.test(before.slice(lastIf));
ok('and only when the channel is beta', pruneAt !== -1 && guarded,
   'the prune is not inside an if [ "$BRANCH" = "beta" ] block');
ok('the released version is confirmed to still be in the manifest afterwards',
   /select\(\.version == \$v\)/.test(upd.slice(pruneAt)));

console.log('');
if (fails.length) {
    fails.forEach(x => console.log('   - ' + x));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
