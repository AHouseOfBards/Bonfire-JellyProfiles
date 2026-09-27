/*
 * No token for the master account is kept in the browser, and nothing puts one back.
 *
 * The switcher kept the master's token in localStorage for as long as a profile was in
 * use, so it could list profiles, manage them, and "revert" to the master when the tab was
 * closed or a profile's inactivity lock fired. Anybody at that browser with the developer
 * tools — or any other script on the page — therefore had the master account, and every
 * PIN and parental control on the profiles was a picture drawn over it. The lock and a
 * closed tab were worse: they put the master account back in use on their own.
 *
 * The server never needed it. /list and /switch resolve the household from whatever
 * session calls them, and entering the master goes through /switch with its PIN.
 *
 * This runs the shipped file (init() swapped for an export) against a browser holding a
 * profile's session and, as an earlier version would have left it, the master's token, and
 * checks what each path does with it. Point it at an older build to watch it fail:
 *
 *     node tests/js/mastertoken.js /path/to/old/profiles.js
 */
'use strict';

const fs = require('fs');
const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
}

let src = fs.readFileSync(L.profilesPath(), 'utf8');
src = src.replace('ProfilesPlugin.init();', 'globalThis.__PROFILES = ProfilesPlugin;');

const MASTER = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const KID = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
const MASTER_TOKEN = 'master-token';
const KID_TOKEN = 'kid-token';
const STATE_KEY = 'jellyfin_profiles_master_state';

function storage(initial) {
    const data = Object.assign({}, initial || {});
    return {
        data,
        getItem: k => (Object.prototype.hasOwnProperty.call(data, k) ? data[k] : null),
        setItem: (k, v) => { data[k] = String(v); },
        removeItem: k => { delete data[k]; }
    };
}

/** A browser signed in as the kid's profile, holding what an earlier version stored. */
function browser(opts) {
    opts = opts || {};
    const creds = { Servers: [{ Id: 'server', AccessToken: KID_TOKEN, UserId: KID }] };
    const local = storage({
        'jellyfin_credentials': JSON.stringify(creds),
        [STATE_KEY]: JSON.stringify(opts.state || { masterUserId: MASTER, masterToken: MASTER_TOKEN })
    });
    const session = storage(opts.session || {});

    const auth = { token: KID_TOKEN, userId: KID, sets: [] };
    const requests = [];
    const reloads = [];

    const ApiClient = {
        accessToken: () => auth.token,
        getCurrentUserId: () => auth.userId,
        setAuthenticationInfo: (t, u) => { auth.sets.push([t, u]); auth.token = t; auth.userId = u; },
        getUrl: p => '/' + p,
        serverId: () => 'server',
        serverAddress: () => 'http://jf'
    };

    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage: local, sessionStorage: session,
        navigator: { userAgent: 'Mozilla/5.0', language: 'en' },
        setTimeout() {}, clearTimeout() {}, setInterval() {}, clearInterval() {},
        requestAnimationFrame() {},
        fetch: (url, init) => {
            requests.push({ url, init: init || {} });
            if (opts.respond) return opts.respond(url, init || {});
            return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve([]), text: () => Promise.resolve('') });
        },
        location: { hash: '#/home', pathname: '/web/', reload: () => reloads.push('reload'), replace: () => reloads.push('replace') },
        document: {
            addEventListener() {}, removeEventListener() {},
            querySelector: () => null, querySelectorAll: () => [], getElementById: () => null,
            createElement: () => ({ style: {}, classList: { add() {}, remove() {} }, appendChild() {}, setAttribute() {} }),
            head: { appendChild() {} },
            body: { classList: { add() {}, remove() {}, contains: () => false }, appendChild() {} },
            documentElement: { style: { cssText: '', removeProperty() {}, setProperty() {} },
                               classList: { add() {}, remove() {}, contains: () => false } }
        },
        getComputedStyle: () => ({ backgroundColor: 'rgb(16, 16, 16)' }),
        ApiClient,
        JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise, Set, Map
    };
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;
    vm.createContext(sandbox);
    new vm.Script(src, { filename: 'profiles.js' }).runInContext(sandbox);

    const plugin = sandbox.__PROFILES;
    // Drawing the gate is a different unit; what matters is who is signed in when it opens.
    plugin.interceptHomeAndShowProfiles = () => { plugin.__gateOpened = (plugin.__gateOpened || 0) + 1; };
    plugin.reloadAtHome = () => reloads.push('reloadAtHome');

    return { plugin, local, session, auth, requests, reloads, ApiClient };
}

function storedCredentialToken(b) {
    return JSON.parse(b.local.data['jellyfin_credentials']).Servers[0].AccessToken;
}

function masterTokenNeverUsed(b) {
    return b.auth.token === KID_TOKEN
        && !b.auth.sets.some(s => s[0] === MASTER_TOKEN)
        && storedCredentialToken(b) === KID_TOKEN;
}

const settle = () => new Promise(r => setImmediate(r)).then(() => new Promise(r => setImmediate(r)));

(async function main() {

console.log();
console.log('── A tab reopened while a profile was in use ───────────────────');
{
    // sessionStorage went with the tab, so no profile is marked active.
    const b = browser();
    b.plugin.validateSessionState();
    await settle();

    ok('the profile stays signed in; the master is not put back', masterTokenNeverUsed(b));
    ok('and nothing reloads to do it', b.reloads.length === 0);
    ok('the master token is gone from the browser',
        !(JSON.parse(b.local.data[STATE_KEY] || '{}').masterToken));
    ok('the household is still known', JSON.parse(b.local.data[STATE_KEY] || '{}').masterUserId === MASTER);
    const logout = b.requests.find(r => /Sessions\/Logout/.test(r.url));
    ok('and the master session it held is ended on the server',
        !!logout && JSON.stringify(logout.init.headers || {}).indexOf(MASTER_TOKEN) !== -1);
}

console.log();
console.log('── A profile\'s inactivity lock ───────────────────────────────');
{
    const b = browser({ session: { 'jellyfin_profiles_active_token': KID_TOKEN } });
    b.plugin.lockActiveProfile();
    ok('the gate opens', b.plugin.__gateOpened === 1);
    ok('over the profile, not over the master account', masterTokenNeverUsed(b));
}

console.log();
console.log('── Opening the switcher from a profile ─────────────────────────');
{
    const b = browser({ session: { 'jellyfin_profiles_active_token': KID_TOKEN } });
    b.plugin.handleBubbleClick();
    ok('the gate opens', b.plugin.__gateOpened === 1);
    ok('with the profile still the one signed in', masterTokenNeverUsed(b));
    ok('and it can still be backed out of', !!b.plugin._resumeState && b.plugin._resumeState.token === KID_TOKEN);
}

console.log();
console.log('── Every request goes out as the session in use ────────────────');
{
    const b = browser({ state: { masterUserId: MASTER } });
    const state = b.plugin.readMasterState ? b.plugin.readMasterState() : null;
    ok('the stored state names the household', !!state && state.masterUserId === MASTER);
    ok('and hands out the current session, never a stored one', !!state && state.token === KID_TOKEN);
}

console.log();
console.log('── A switch ends the session it leaves ─────────────────────────');
{
    // Signed in as the master, going into the kid's profile.
    const b = browser({
        state: { masterUserId: MASTER },
        respond: (url) => /profiles\/switch/.test(url)
            ? Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ activeProfileToken: KID_TOKEN, jellyfinUserId: KID }) })
            : Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve([]), text: () => Promise.resolve('') })
    });
    b.auth.token = MASTER_TOKEN;
    b.auth.userId = MASTER;
    b.local.data['jellyfin_credentials'] = JSON.stringify({ Servers: [{ Id: 'server', AccessToken: MASTER_TOKEN, UserId: MASTER }] });
    b.plugin.currentProfiles = [];
    b.plugin.setSwitchBusy = () => {};
    b.plugin.cacheLibraryArtwork = () => {};
    b.plugin._captureLeavingBackground = () => 'rgb(16, 16, 16)';

    b.plugin.executeProfileSwitch(KID, null);
    await settle();

    ok('the switch completes', storedCredentialToken(b) === KID_TOKEN);
    ok('no master token is stored for later', !(JSON.parse(b.local.data[STATE_KEY] || '{}').masterToken));
    const logout = b.requests.find(r => /Sessions\/Logout/.test(r.url));
    ok('and the master session is ended on the server',
        !!logout && JSON.stringify(logout.init.headers || {}).indexOf(MASTER_TOKEN) !== -1);
}

console.log();
console.log('── Nothing in the file can do it another way ───────────────────');
{
    const text = fs.readFileSync(L.profilesPath(), 'utf8');
    ok('no code puts stored credentials of the master back into use',
        !/setAuthenticationInfo\(masterState\./.test(text) && !/updateStoredCredentials\(masterState\./.test(text));
    ok('and none writes a master token to storage', !/masterToken\s*=\s*[a-zA-Z]/.test(text));
}

console.log();
console.log(pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
})();
