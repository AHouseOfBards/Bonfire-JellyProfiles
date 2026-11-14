/*
 * Sub-profiles must never reach a login screen.
 *
 * Bonfire creates each profile as a real Jellyfin user with `IsHidden = true`. That one
 * flag keeps a household's profiles out of every client's user picker. It matters more on
 * a server that shows its users publicly, because there the master's policy has
 * `IsHidden = false` and every place that copies the master's policy onto a profile is
 * one assignment from publishing the whole household.
 *
 * `CopyUserPolicy(masterPolicy, targetPolicy)` does that copy twice and is correct only
 * because an explicit `targetPolicy.IsHidden = true` follows both. Delete one of those
 * lines and nothing else in the suite goes red: the profiles still work, still switch,
 * still enforce their libraries, and are simply visible to everyone on the sign-in page.
 *
 * So every write is enumerated and checked one at a time. A profile's policy may be
 * written only when one of three things is true:
 *
 *   1. `IsHidden = true` is asserted in the same method;
 *   2. the policy being written was read from the profile's OWN current policy, so
 *      whatever it had is preserved;
 *   3. the write disables the account, which removes it from the public list anyway.
 *
 * Enumerated, not counted, each call named with its line: "all the writes are fine" is
 * the shape of check this repository has shipped three bugs past.
 *
 *   node tests/js/profilevisibility.js [path/to/ProfilesController.cs] [path/to/ProfilePolicySync.cs]
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
// The switch's policy write moved to Auth/ProfilePolicySync.cs, which every sign-in runs,
// so it is read with the controller. Line numbers past the controller's last line are
// reported against that file.
const SYNC = process.argv[3]
    || path.join(L.ROOT, 'Auth', 'ProfilePolicySync.cs');
const controllerSrc = fs.readFileSync(CONTROLLER, 'utf8');
const syncSrc = fs.existsSync(SYNC) ? fs.readFileSync(SYNC, 'utf8') : '';
const src = controllerSrc + '\n' + syncSrc;
const controllerLines = controllerSrc.split('\n').length;
const lineOf = idx => {
    const n = src.slice(0, idx).split('\n').length;
    return n <= controllerLines ? String(n) : 'ProfilePolicySync.cs:' + (n - controllerLines);
};

console.log('\n── a new profile is hidden the moment it exists ────────────────');

// The creation path. Without this the profile is public from its first second, when a
// login screen is most likely to be looked at.
const createIdx = src.indexOf('targetPolicy.IsHidden = true');
ok('the profile creation path hides the user', createIdx !== -1,
   'no `targetPolicy.IsHidden = true` anywhere in the controller');

// Both CopyUserPolicy calls, one at a time. Each copies the MASTER's policy over the
// profile's, including the master's IsHidden, which on a public server is false.
const copies = [];
let ci = src.indexOf('CopyUserPolicy(');
while (ci !== -1) { copies.push(ci); ci = src.indexOf('CopyUserPolicy(', ci + 1); }

// Two today. Asserted so a third without cover fails here, not on someone's server.
ok('every CopyUserPolicy call is accounted for (' + copies.length + ')', copies.length >= 2);

copies.forEach(idx => {
    // The re-assert must come AFTER the copy, or the copy overwrites it.
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

    // 2. The policy came from the profile's own DTO, so nothing was overwritten, but ONLY
    //    if nothing copies the master's policy over it afterwards. First written without
    //    that half; a mutation showed why: the creation path reads the profile's own policy
    //    and then calls CopyUserPolicy, so it satisfied a bare own-policy rule while being
    //    the bug. Only the CopyUserPolicy check above caught it, one check covering for
    //    another's blind spot.
    const ownPolicy = /var\s+targetPolicy\s*=\s*targetUserDto\.Policy/.test(body)
        && !/CopyUserPolicy\(/.test(body);

    // 3. The account is being disabled, which drops it from the public list regardless.
    const disables = /\.IsDisabled\s*=\s*true/.test(body);

    // 4. It is no longer a profile: the method removed its mapping BEFORE this write.
    //    Making a profile its own account (MakeStandalone) shows it on the sign-in screen
    //    on purpose. The removal must precede the write, which `body` guarantees since it
    //    ends at the write. A write before the removal still fails.
    const unmapped = /\.Mappings\.Remove\(/.test(body);

    const why = asserts ? 'asserts IsHidden = true'
        : ownPolicy ? 'writes the profile\'s own policy back'
        : disables ? 'disables the account'
        : unmapped ? 'is no longer a profile: its mapping was removed first'
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

// Named again as a set, so a failure reads as a list of places to look.
ok('no policy write can publish a profile', unguarded.length === 0,
   unguarded.length ? 'lines ' + unguarded.join(', ') : '');

console.log('\n── nothing ever un-hides one ───────────────────────────────────');

// ProfileMapping.IsHidden is Bonfire's own "leave this profile out of the switcher grid"
// flag, unrelated to the Jellyfin user policy. Only the policy form concerns the login
// screen, so the two are told apart rather than grepped together.
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
