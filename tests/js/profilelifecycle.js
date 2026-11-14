// Creating and deleting a profile (r2-11-05, -06, -08), read from the controller source.
//
// These are shape checks, not behavior: the C# harnesses cannot make a mapping write throw
// or watch the order of two calls, so order and placement are read from the text. Each
// targets one method and names what it found.
//
//  - Delete removes the mapping first and the picture after, so a failure part way cannot
//    leave a mapping that points at an account that is gone.
//  - No picture is written or removed inside a ConfigLock block (create wrote one inside it).
//  - Create's rollback removes the picture it wrote.
//  - Delete has no loop that revokes the same tokens once per session and breaks after one.
//  - The avatar initial is the first text element, upper-cased invariantly.
//
// Red against the build before the fix (pass an older ProfilesController.cs as argv[2]).
//
//   node tests/js/profilelifecycle.js [path/to/ProfilesController.cs]

'use strict';
const fs = require('fs');
const path = require('path');

const file = process.argv[2] || path.join(__dirname, '..', '..', 'Controllers', 'ProfilesController.cs');
const SRC = fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

// The text from `start` up to (not including) `end`.
function region(start, end) {
    const a = SRC.indexOf(start);
    if (a < 0) throw new Error('no such place: ' + start);
    const b = SRC.indexOf(end, a + start.length);
    if (b < 0) throw new Error('no end for: ' + start);
    return SRC.slice(a, b);
}

// The body of every `lock (ConfigLock)` block in a region, brace matched.
function lockBodies(text) {
    const out = [];
    const re = /lock \(ConfigLock\)\s*\{/g;
    let m;
    while ((m = re.exec(text))) {
        let depth = 1, i = re.lastIndex;
        while (i < text.length && depth > 0) {
            if (text[i] === '{') depth++;
            else if (text[i] === '}') depth--;
            i++;
        }
        out.push(text.slice(re.lastIndex, i - 1));
    }
    return out;
}

const create = region('private async Task<ActionResult<object>> CreateProfileCore(', '[HttpPost("delete")]');
const del = region('public async Task<ActionResult> DeleteProfile(', '[HttpPost("switch")]');
const DISK = /SaveProfileImage\(|CopyLibraryAvatarToProfile\(|DeleteImageFiles\(|System\.IO\.File\.|Directory\./;

console.log('── Delete ───────────────────────────────────────────────────────');
{
    const removeAt = del.indexOf('config.Mappings.Remove(');
    const imageAt = del.indexOf('SaveProfileImage(request.ProfileId, null)');
    ok('the mapping is removed before the picture', removeAt > 0 && imageAt > removeAt,
        'mapping at ' + removeAt + ', picture at ' + imageAt);
    const locked = lockBodies(del).filter((b) => DISK.test(b));
    ok('no picture work inside ConfigLock in DeleteProfile', locked.length === 0, locked.length + ' block(s) do disk work');
    ok('no loop over sessions that breaks after the first', !/foreach \(var session in activeSessions\)/.test(del));
}

console.log('── Create ───────────────────────────────────────────────────────');
{
    const locked = lockBodies(create).filter((b) => DISK.test(b));
    ok('no picture work inside a ConfigLock block in CreateProfileCore', locked.length === 0,
        locked.length + ' block(s) do disk work');
    const catchAt = create.indexOf('catch\n            {');
    ok('the rollback exists', catchAt > 0);
    ok('and removes the picture it wrote', catchAt > 0 && /SaveProfileImage\(targetUser\.Id, null\)/.test(create.slice(catchAt)));
}

console.log('── The avatar initial ───────────────────────────────────────────');
{
    const bad = SRC.split('\n').map((l, i) => [i + 1, l]).filter(([, l]) => /AvatarInitial\s*=/.test(l) && /Substring\(|\.ToUpper\(\)/.test(l));
    ok('no AvatarInitial is cut with Substring or upper-cased by culture', bad.length === 0,
        bad.map(([n]) => 'line ' + n).join(', '));
    const at = SRC.indexOf('internal static string AvatarInitialOf(');
    const helper = at < 0 ? '' : SRC.slice(at, SRC.indexOf('[HttpPost("delete")]', at));
    ok('the helper takes the first text element and upper-cases it invariantly',
        /GetNextTextElement\(/.test(helper) && /ToUpperInvariant\(\)/.test(helper));
}

console.log();
if (fails.length) {
    fails.forEach((f) => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
