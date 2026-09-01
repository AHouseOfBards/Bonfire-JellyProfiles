// A profile switch that does not take: the two ways the last part of one can fail on a
// television and report nothing.
//
// The reload is the first. The credentials write is the second, and it is upstream of
// everything here — see the section at the foot of this file.
//
// By the time reloadAtHome() is called the switch is complete everywhere except the
// screen: the profile's token is in localStorage and in ApiClient, and all that is left
// is for the document to be rebuilt under the new identity. If the reload does nothing,
// jellyfin-web goes on rendering the page it already built for the previous profile — its
// home sections, its Continue Watching, its Next Up — while the header avatar shows the
// profile that was picked, because Bonfire redraws that part itself.
//
// That is the Samsung Tizen report: "it lets you pick a profile, but nothing changes once
// you load in". The bundled television clients serve jellyfin-web from a local file://
// origin, which is where location.reload() has been seen to do nothing at all.
//
// This models a runtime where navigation is a no-op, which is a thing browsers really do,
// rather than a sequence invented to make an assertion pass — the mistake that left
// session.js green for three releases over a fix that had never executed.
//
//     node tests/js/switchreload.js /path/to/old/profiles.js   # must fail

const fs = require('fs');
const vm = require('vm');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

const SRC_PATH = process.argv[2] || L.profilesPath();
let src = fs.readFileSync(SRC_PATH, 'utf8');
const INIT = 'ProfilesPlugin.init();';
if (src.split(INIT).length - 1 !== 1) {
    console.error('could not find the single init() call to swap for an export');
    process.exit(1);
}
src = src.replace(INIT, 'globalThis.__PROFILES = ProfilesPlugin;');

/// A page whose navigation does nothing at all, and a timer queue we drive by hand.
function makePage(opts) {
    opts = opts || {};
    const navigations = [];
    const timers = [];

    const location = {
        pathname: '/index.html',
        search: '',
        hash: opts.hash === undefined ? '#!/somewhere' : opts.hash,
        reload() { navigations.push({ kind: 'reload' }); },
        replace(url) { navigations.push({ kind: 'replace', url }); }
    };
    // href has to be a real accessor: assigning to it is a different navigation mechanism
    // from replace(), and telling them apart is the whole point of the ladder.
    let hrefValue = 'file:///opt/usr/apps/x/res/wgt/index.html#!/somewhere';
    Object.defineProperty(location, 'href', {
        get() { return hrefValue; },
        set(v) { navigations.push({ kind: 'href', url: v }); hrefValue = v; }
    });

    const removedProps = [];
    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        localStorage: (() => {
            const d = {};
            return {
                data: d,
                getItem: k => (k in d ? d[k] : null),
                setItem: (k, v) => { d[k] = String(v); },
                removeItem: k => { delete d[k]; }
            };
        })(),
        sessionStorage: {
            getItem: () => null, setItem() {}, removeItem() {}
        },
        navigator: { userAgent: 'Mozilla/5.0 (SMART-TV; Tizen 6.0)' },
        addEventListener() {}, removeEventListener() {},
        ApiClient: {
            serverId: () => (opts.serverId === undefined ? 'A1B2C3D4E5F6478899AABBCCDDEEFF00' : opts.serverId),
            serverAddress: () => (opts.serverAddress === undefined ? 'http://192.168.1.9:8096' : opts.serverAddress),
            setAuthenticationInfo() {}
        },
        setTimeout(fn, ms) { timers.push({ fn, ms }); return timers.length; },
        clearTimeout() {}, setInterval() {}, clearInterval() {},
        requestAnimationFrame(fn) { fn(); },
        fetch() { return Promise.resolve({ ok: false, text: () => Promise.resolve('') }); },
        document: {
            addEventListener() {}, removeEventListener() {},
            querySelector() { return null; }, querySelectorAll() { return []; },
            getElementById(id) { return opts.overlay && id === 'profiles-gate-overlay' ? opts.overlay : null; },
            createElement() {
                return {
                    style: {}, classList: { add() {}, remove() {} },
                    appendChild() {}, addEventListener() {},
                    querySelector() { return { addEventListener() {}, focus() {}, click() {} }; },
                    remove() {}
                };
            },
            head: { appendChild() {} },
            body: { classList: { add() {}, remove() {} }, appendChild() {}, removeChild() {} },
            documentElement: {
                style: { cssText: '', removeProperty(p) { removedProps.push(p); } },
                classList: { add() {}, remove() {} }
            }
        },
        JSON, Date, Math, Object, Array, String, Number, Boolean, RegExp, Error, Promise
    };
    sandbox.window = sandbox;
    sandbox.globalThis = sandbox;
    sandbox.location = location;

    vm.createContext(sandbox);
    new vm.Script(src, { filename: 'profiles.js' }).runInContext(sandbox);

    const plugin = sandbox.__PROFILES;
    // Drain one scheduled timer, which is what "the page is still here" looks like.
    function tick() {
        const t = timers.shift();
        if (!t) return false;
        t.fn();
        return true;
    }
    return { plugin, navigations, timers, tick, removedProps, sandbox };
}

console.log();
console.log('── The reload is attempted, and then checked ──────────────────');

const page = makePage();
if (typeof page.plugin._reloadWithFallback !== 'function') {
    console.log('  FAIL  _reloadWithFallback() does not exist — a reload that does nothing '
        + 'leaves the previous profile on screen with no way to notice');
    fails.push('_reloadWithFallback missing');
} else {
    page.plugin.reloadAtHome();

    // The first step is replace() followed by reload() when the target carries a
    // fragment: a fragment-only change does not reload the document, so the pair is one
    // step rather than two attempts.
    ok('the first attempt navigates', page.navigations.length >= 1 && page.navigations.length <= 2,
        'made ' + page.navigations.length + ' navigation(s)');
    ok('and it is the replace/reload pair a fragment target needs',
        page.navigations[0].kind === 'replace' && page.navigations[1].kind === 'reload',
        page.navigations.map(n => n.kind).join(' then '));
    ok('and a check is scheduled in case it did nothing', page.timers.length === 1);

    // On a runtime where the reload works, the document unloads and that timer never
    // fires. Nothing below happens, which is why the ladder costs a working client
    // nothing at all.
    const afterFirst = page.navigations.length;

    page.tick();
    ok('a reload that did nothing escalates', page.navigations.length > afterFirst);

    page.tick();
    ok('and escalates again', page.navigations.length > 2);

    const kinds = page.navigations.map(n => n.kind);
    ok('each step is a different mechanism, not the same call three times',
        new Set(kinds).size >= 2, 'attempts were: ' + kinds.join(', '));
    ok('one of them assigns location.href', kinds.includes('href'),
        'attempts were: ' + kinds.join(', '));

    const busted = page.navigations.filter(n => n.url && n.url.indexOf('jpf=') !== -1);
    ok('one of them changes the URL, for a webview that ignores an identical one',
        busted.length === 1, 'urls: ' + page.navigations.map(n => n.url || '(reload)').join(' | '));

    // The last rung. Leaving somebody looking at another account's Continue Watching and
    // believing it is theirs is worse than admitting the reload failed.
    let alerted = null;
    page.plugin.showAlert = (title, body) => { alerted = { title, body }; };
    page.tick();

    ok('giving up tells the user rather than failing silently', alerted !== null);
    ok('and the message says what to do',
        alerted !== null && /reopen/i.test(alerted.body || ''),
        alerted ? JSON.stringify(alerted) : 'no alert');
    ok('the hidden page is revealed so the message can be seen',
        page.removedProps.includes('opacity'),
        'removeProperty called with: ' + page.removedProps.join(', '));
    ok('and the switching flag is cleared, so the next load is not treated as a switch',
        page.sandbox.localStorage.getItem('jpf-sw') === null);
}

console.log();
console.log('── A working client pays nothing for this ─────────────────────');
{
    const p = makePage({ hash: '' });
    let alerted = false;
    p.plugin.showAlert = () => { alerted = true; };
    p.plugin.reloadAtHome();
    // The document unloads: the scheduled check never runs. Modelled by simply not
    // firing it, which is exactly what happens.
    ok('one navigation when there is no fragment to work around', p.navigations.length === 1,
        p.navigations.map(n => n.kind).join(', '));
    ok('nothing escalates while the timer has not fired', p.navigations.length === 1);
    ok('and no alert is shown on the way out', alerted === false);
}

console.log();
console.log('── The credentials write, which is what survives the reload ───');
//
// Everything else about a switch can succeed and the profile still not be the one that
// comes back: the token the reload reads is the one in jellyfin-web's own
// `jellyfin_credentials`, and nothing else. Two silent no-ops lived in that write.
{
    const creds = (servers) => JSON.stringify({ Servers: servers });
    const run = (stored, opts) => {
        const p = makePage(opts || {});
        if (stored !== null) p.sandbox.localStorage.setItem('jellyfin_credentials', stored);
        const result = p.plugin.updateStoredCredentials('NEW-TOKEN', 'uuu');
        const after = JSON.parse(p.sandbox.localStorage.getItem('jellyfin_credentials') || 'null');
        return { result, servers: after ? after.Servers : null };
    };

    // The GUID that comes back from ApiClient.serverId() is not always spelt the way the
    // stored copy is. Every other id comparison in profiles.js goes through normalizeGuid;
    // this one used ===, so with two servers stored — an address and a hostname for the
    // same box is the ordinary way that happens — nothing was written and nothing said so.
    let r = run(creds([
        { Id: 'a1b2c3d4-e5f6-4788-99aa-bbccddeeff00', AccessToken: 'OLD', UserId: 'master' },
        { Id: '00000000-0000-0000-0000-000000000009', AccessToken: 'OTHER', UserId: 'someone' }
    ]));
    ok('a server id spelt with dashes still matches',
        r.servers && r.servers[0].AccessToken === 'NEW-TOKEN' && r.servers[0].UserId === 'uuu',
        'entry 0 is ' + JSON.stringify(r.servers && r.servers[0]));
    ok('and the other server is left alone',
        r.servers && r.servers[1].AccessToken === 'OTHER');
    ok('and it reports that it wrote', r.result === true);

    // No id to match on: the address ApiClient is talking to names the entry instead.
    r = run(creds([
        { Id: 'zzz', ManualAddress: 'http://192.168.1.9:8096/', AccessToken: 'OLD' },
        { Id: 'yyy', ManualAddress: 'http://10.0.0.2:8096', AccessToken: 'OTHER' }
    ]), { serverId: '' });
    ok('an unmatched id falls back to the server address',
        r.servers && r.servers[0].AccessToken === 'NEW-TOKEN' && r.servers[1].AccessToken === 'OTHER');

    // Nothing matches. Writing every entry would point another server at a token it will
    // not accept, so the only honest answer is no.
    r = run(creds([
        { Id: 'zzz', ManualAddress: 'http://elsewhere:8096', AccessToken: 'OLD' },
        { Id: 'yyy', ManualAddress: 'http://10.0.0.2:8096', AccessToken: 'OTHER' }
    ]), { serverId: 'ffffffffffffffffffffffffffffffff', serverAddress: 'http://192.168.1.9:8096' });
    ok('no match at all writes nothing', r.servers && r.servers.every(x => x.AccessToken !== 'NEW-TOKEN'));
    ok('and says so rather than letting the reload happen', r.result === false);

    ok('no stored credentials at all is also a no', run(null).result === false);

    // Storage that accepts a write and drops it. A television in a private-browsing-like
    // mode does this, and the reload would sign back in as whoever was there before.
    {
        const p = makePage();
        p.sandbox.localStorage.setItem('jellyfin_credentials', creds([{ Id: 'a1b2c3d4e5f6478899aabbccddeeff00', AccessToken: 'OLD' }]));
        const frozen = p.sandbox.localStorage.getItem('jellyfin_credentials');
        p.sandbox.localStorage.setItem = () => {};
        p.sandbox.localStorage.getItem = () => frozen;
        ok('a write that is accepted and discarded is caught on read-back',
            p.plugin.updateStoredCredentials('NEW-TOKEN', 'uuu') === false);
    }
}

console.log();
if (fails.length) {
    console.log('  Failures:');
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
