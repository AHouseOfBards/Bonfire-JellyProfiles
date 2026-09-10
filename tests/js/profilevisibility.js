/*
 * Sub-profiles must never reach a login screen.
 *
 * Bonfire creates each profile as a real Jellyfin user with `IsHidden = true`, and that
 * one flag is the whole reason a household's profiles do not appear in every client's
 * user picker. It matters more on a server that shows its users publicly — plenty do —
 * because there the master's own policy has `IsHidden = false`, and every place that
 * copies the master's policy onto a profile is one assignment away from publishing the
 * whole household.
 *
 * `CopyUserPolicy(masterPolicy, targetPolicy)` does exactly that copy, twice, and is
 * correct only because an explicit `targetPolicy.IsHidden = true` follows it both times.
 * Delete one of those two lines and nothing else in the suite goes red: the profiles
 * still work, still switch, still enforce their libraries — they are simply visible to
 * everyone who opens the sign-in page, on a server the author may not run.
 *
 * So every write is enumerated here and checked one at a time. A profile's policy may be
 * written only when one of three things is true:
 *
 *   1. `IsHidden = true` is asserted in the same method;
 *   2. the policy being written was read from the profile's OWN current policy, so
 *      whatever it had is preserved;
 *   3. the write disables the account, which removes it from the public list anyway.
 *
 * Enumerated rather than counted, and each call named with its line, because "all the
 * writes are fine" is the shape of check this repository has shipped three bugs past.
 *
 *   node tests/js/profilevisibility.js [path/to/ProfilesController.cs]
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

const CONTROLLER = process.argv[2]
    || path.join(L.ROOT, 'Controllers', 'ProfilesController.cs');
const src = fs.readFileSync(CONTROLLER, 'utf8');
const lineOf = idx => src.slice(0, idx).split('\n').length;

console.log('\n── a new profile is hidden the moment it exists ────────────────');

// The creation path. Without this the profile is public from its first second, which is
// the window a login screen is most likely to be looked at.
const createIdx = src.indexOf('targetPolicy.IsHidden = true');
ok('the profile creation path hides the user', createIdx !== -1,
   'no `targetPolicy.IsHidden = true` anywhere in the controller');

// Both CopyUserPolicy calls, one at a time. Each copies the MASTER's policy over the
// profile's — including the master's IsHidden, which on a public server is false.
const copies = [];
let ci = src.indexOf('CopyUserPolicy(');
while (ci !== -1) { copies.push(ci); ci = src.indexOf('CopyUserPolicy(', ci + 1); }

// Two today. Asserted so that a third arriving without cover is a failure here rather
// than a discovery on someone's server.
ok('every CopyUserPolicy call is accounted for (' + copies.length + ')', copies.length >= 2);

copies.forEach(idx => {
    // The re-assert has to be AFTER the copy, or the copy overwrites it.
    const after = src.slice(idx, idx + 2500);
    ok('CopyUserPolicy at line ' + lineOf(idx) + ' is followed by IsHidden = true',
       /targetPolicy\.IsHidden\s*=\s*true/.test(after),
       'the master\'s IsHidden is copied onto the profile and never corrected');
});

console.log('\n── and stays hidden through every policy write ─────────────────');

/** The enclosing method's source, so a rule is judged against the block it applies to. */
function enclosingMethod(idx) {
    const before = src.slice(0, idx);
    // Methods on this controller sit at eight spaces of indent.
    const start = Math.max(
        before.lastIndexOf('\n        public '),
        before.lastIndexOf('\n        private ')
    );
    return src.slice(start === -1 ? Math.max(0, idx - 4000) : start, idx);
}

const writes = [];
let wi = src.indexOf('UpdatePolicyAsync(');
while (wi !== -1) { writes.push(wi); wi = src.indexOf('UpdatePolicyAsync(', wi + 1); }

ok('there are policy writes to check (' + writes.length + ')', writes.length > 0);

const unguarded = [];
writes.forEach(idx => {
    const body = enclosingMethod(idx);
    const line = lineOf(idx);

    // 1. Hidden is asserted outright in this method.
    const asserts = /\.IsHidden\s*=\s*true/.test(body);

    // 2. The policy came from the profile's own DTO, so nothing was overwritten — but
    //    ONLY if nothing copies the master's policy over it afterwards. Written first
    //    without that second half, and a mutation proved why: the creation path reads the
    //    profile's own policy and *then* calls CopyUserPolicy, so it satisfied a bare
    //    own-policy rule while being exactly the bug. It stayed red only because the
    //    CopyUserPolicy check above caught it, which is one check quietly covering for
    //    another's blind spot — the arrangement this repository keeps finding too late.
    const ownPolicy = /var\s+targetPolicy\s*=\s*targetUserDto\.Policy/.test(body)
        && !/CopyUserPolicy\(/.test(body);

    // 3. The account is being disabled, which drops it from the public list regardless.
    const disables = /\.IsDisabled\s*=\s*true/.test(body);

    const why = asserts ? 'asserts IsHidden = true'
        : ownPolicy ? 'writes the profile\'s own policy back'
        : disables ? 'disables the account'
        : null;

    if (why) {
        pass++;
        console.log('  PASS  line ' + line + ' — ' + why);
    } else {
        unguarded.push(line);
        fails.push('line ' + line + ' writes a profile policy with nothing keeping it hidden');
        console.log('  FAIL  line ' + line + ' — nothing in this method keeps the profile hidden');
    }
});

// Named again as a set, so the failure reads as a list of places to go and look rather
// than as scattered individual results.
ok('no policy write can publish a profile', unguarded.length === 0,
   unguarded.length ? 'lines ' + unguarded.join(', ') : '');

console.log('\n── nothing ever un-hides one ───────────────────────────────────');

// ProfileMapping.IsHidden is Bonfire's own "leave this profile out of the switcher grid"
// flag and is unrelated to the Jellyfin user policy. Only the policy form is a login
// screen concern, so the two are told apart rather than grepped together.
const unhides = [];
const re = /targetPolicy\.IsHidden\s*=\s*false/g;
let m;
while ((m = re.exec(src)) !== null) unhides.push(lineOf(m.index));
ok('no code path sets a profile policy IsHidden = false', unhides.length === 0,
   unhides.length ? 'lines ' + unhides.join(', ') : '');

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
