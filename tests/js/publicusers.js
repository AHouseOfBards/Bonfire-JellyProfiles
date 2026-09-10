/*
 * Adding a household to the login screen's user list.
 *
 * `/Users/Public` is how every client on the server signs in. Editing it is a bigger
 * commitment than the script tag we splice into index.html: get the shape wrong and
 * nobody logs in, on any client, including the forty people who have never heard of
 * Bonfire. So this file checks the properties that make that impossible rather than the
 * happy path — the parts of PublicUserInjector that decide whether to touch a response at
 * all, and the parts of the middleware that decide whether to buffer one.
 *
 * The behaviour itself is exercised in C# against the real assembly; this is the source
 * gate for the invariants a runtime test cannot see, in the style of checkdash.
 *
 *   node tests/js/publicusers.js [path/to/Auth/PublicUserInjector.cs]
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

const injector = fs.readFileSync(
    process.argv[2] || path.join(L.ROOT, 'Auth', 'PublicUserInjector.cs'), 'utf8');
const middleware = fs.readFileSync(
    path.join(L.ROOT, 'ProfilesIndexMiddleware.cs'), 'utf8');
const config = fs.readFileSync(
    path.join(L.ROOT, 'Configuration', 'PluginConfiguration.cs'), 'utf8');

// Comments in these files discuss every hazard at length, and a check that searched raw
// text would be satisfied by the prose describing the rule rather than by the rule.
const strip = s => s.replace(/\/\/[^\n]*/g, '').replace(/\/\*[\s\S]*?\*\//g, '');
const inj = strip(injector);
const mw = strip(middleware);

console.log('\n── it is off until an administrator turns it on ────────────────');

ok('the configuration has its own flag, separate from PIN login',
   /public bool EnableClientProfileList/.test(config));
ok('and it defaults to off — no `= true` on it',
   !/EnableClientProfileList\s*\{\s*get;\s*set;\s*\}\s*=\s*true/.test(config));

// Two gates, deliberately: the middleware must not even buffer the body of an endpoint
// this important when the feature is off, and the injector must not act if it is somehow
// reached anyway. Checked separately because either one alone is a single point of failure.
ok('the middleware refuses the path when the flag is off',
   /EnableClientProfileList\s*!=\s*true/.test(mw));
ok('and the injector checks it again before doing anything',
   /!config\.EnableClientProfileList/.test(inj));

console.log('\n── it adds, and never removes ──────────────────────────────────');

// The whole safety argument. Filtering would mean every one of the server's users depends
// on our code being right; adding means the worst case is that nothing appears.
ok('the injector only ever appends to the array', /array\.Add\(/.test(inj));
ok('and never removes from it',
   !/array\.Remove|\.RemoveAt\(|array\.Clear\(/.test(inj),
   'removing from this list would decide who may sign in');

console.log('\n── every failure returns what Jellyfin produced ─────────────────');

// Enumerated, because "it handles errors" is exactly the aggregate that hides which case
// does not. Each of these is a distinct way the request can go wrong.
const handler = mw.slice(mw.indexOf('InjectPublicUsersAsync'));
[
    ['a household that cannot be resolved falls through', /catch[\s\S]{0,220}?await _next\(context\)/],
    ['an unrecognised device falls through before buffering', /household\.Count == 0[\s\S]{0,120}?await _next\(context\)/],
    ['a missing body feature falls through', /originalBody == null[\s\S]{0,120}?await _next\(context\)/],
    ['a non-200 response is passed through', /StatusCodes\.Status200OK/],
    ['an encoded body is passed through', /ContentEncoding/],
    ['a throw inside the injector is caught', /catch[\s\S]{0,200}?rewritten = null/],
    ['and a null result passes the original bytes through', /rewritten == null[\s\S]{0,160}?PassThroughAsync/]
].forEach(([name, re]) => ok(name, re.test(handler)));

console.log('\n── the shape it writes ─────────────────────────────────────────');

// Hand-rolling a UserDto is how a client ends up unable to parse its own login screen,
// and GetUserDto is also what fills HasPassword from the authentication provider.
ok('the DTO comes from Jellyfin, not from us', /userManager\.GetUserDto\(/.test(inj));
ok('and nothing constructs a UserDto by hand', !/new UserDto/.test(inj));

// Jellyfin serves this endpoint in two casings by content negotiation. Assuming one would
// hand some clients an array whose last entries they silently cannot read.
ok('casing is taken from the response, not assumed',
   /CamelCase/.test(inj) && /responseContentType|contentType/.test(inj));

ok('the household is resolved through the master, not the newest user directly',
   /MasterUserId/.test(inj),
   'resolving to the literal newest user empties the list after the first switch');

// One household. Two families on one television get the most recent, by decision.
ok('only one household is ever returned',
   /FirstOrDefault\(\)/.test(inj) && /OrderByDescending/.test(inj));

console.log('\n── and it only ever touches this one endpoint ──────────────────');

ok('the path is matched exactly', /EndsWith\("\/Users\/Public"/.test(mw),
   'a prefix match would buffer every other Users route on the server');

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
