/*
 * An administrator sets one household's PIN prompts from the admin page (issue #42, part 3).
 *
 *  - Every main account that is not an administrator's gets a "PIN prompts" control; an
 *    administrator's own household gets none, because the server refuses it.
 *  - Its switches start as the server said: "Ask PIN to open" on the main account and "No PIN
 *    on the server's network" on each of its profiles.
 *  - Save posts those two kinds of field and nothing else: not a PIN, a rule or a library.
 *
 * Runs the real loadMappings and bindAccountExtras against a stub page; the boxes the
 * binder is handed are read back out of the HTML loadMappings drew, so what is drawn and
 * what is posted are checked against each other. Older dashboard: argv[3].
 *
 *     node tests/js/adminpinprompts.js "" /path/to/old/profilesDashboard.html
 */
'use strict';

const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const script = L.dashboardScript(L.readDashboard());
const source = L.dashboardI18n(script) + '\n'
    + ['escapeHtml', 'namedAria', 'field', 'normId', 'loadMappings', 'bindAccountExtras'].map(function (n) { return L.extractFunction(script, n); }).join('\n');

const MAPPINGS = {
    MasterUsers: [
        { ProfileUserId: 'aaaa', ProfileName: 'Elder', RequiresPin: true, AskPinToOpen: false, IsAdministrator: false, Added: [] },
        { ProfileUserId: 'bbbb', ProfileName: 'Solo', RequiresPin: false, AskPinToOpen: true, IsAdministrator: false, Added: [] },
        { ProfileUserId: 'cccc', ProfileName: 'Owner', RequiresPin: true, AskPinToOpen: true, IsAdministrator: true, Added: [] }
    ],
    SubProfiles: [
        { ProfileUserId: 'a1', ProfileName: 'Gran', MasterUserId: 'aaaa', RequiresPin: true, BypassPinOnLocalNetwork: true },
        { ProfileUserId: 'a2', ProfileName: 'Pip <b>', MasterUserId: 'aaaa', RequiresPin: false, BypassPinOnLocalNetwork: false },
        { ProfileUserId: 'c1', ProfileName: 'OwnerKid', MasterUserId: 'cccc', RequiresPin: false, BypassPinOnLocalNetwork: false }
    ]
};

function element() {
    const classes = {};
    return {
        value: '', checked: false, textContent: '', innerHTML: '', dataset: {}, style: {},
        classList: {
            add: function (c) { classes[c] = true; }, remove: function (c) { delete classes[c]; },
            toggle: function () {}, contains: function (c) { return !!classes[c]; }
        },
        setAttribute: function () {}, getAttribute: function () { return null; }, addEventListener: function () {},
        insertAdjacentHTML: function () {}
    };
}

// What loadMappings drew, cut into one chunk per account's extras, with fake controls read out
// of each chunk so the click handler has something to read.
function boxesFrom(html) {
    const parts = html.split('class="jpf-account-extras" data-id="').slice(1);
    return parts.map(function (chunk) {
        const id = chunk.slice(0, chunk.indexOf('"'));
        const clicks = {};
        const ask = /class="pin-prompt-ask"( checked)?/.exec(chunk);
        const askEl = ask ? { checked: !!ask[1] } : null;
        const bypass = [];
        const re = /class="pin-prompt-bypass" data-id="([^"]*)"( checked)?/g;
        let m;
        while ((m = re.exec(chunk))) {
            (function (pid, on) { bypass.push({ checked: on, getAttribute: function () { return pid; } }); })(m[1], !!m[2]);
        }
        const result = element();
        return {
            id: id, ask: askEl, bypass: bypass, result: result, clicks: clicks,
            getAttribute: function () { return id; },
            querySelector: function (sel) {
                if (sel === '.extras-result') return result;
                if (sel === '.pin-prompts-go') return chunk.indexOf('pin-prompts-go') === -1 ? null : { addEventListener: function (e, fn) { clicks.go = fn; } };
                if (sel === '.pin-prompt-ask') return askEl;
                return null;
            },
            querySelectorAll: function (sel) { return sel === '.pin-prompt-bypass' ? bypass : []; }
        };
    });
}

async function drain() { for (let i = 0; i < 6; i++) await new Promise(function (r) { setImmediate(r); }); }

async function scenario(ajaxOutcome) {
    const calls = { ajax: [] };
    const container = element();
    const page = {
        dataset: {},
        querySelector: function (sel) { return sel === '#masterUsersList' ? container : element(); },
        // The same boxes every time for the same markup, so the click handler the binder
        // registers is the one this harness fires.
        querySelectorAll: function (sel) {
            if (sel !== '.jpf-account-extras') return [];
            if (!page._boxes || page._boxesHtml !== container.innerHTML) { page._boxesHtml = container.innerHTML; page._boxes = boxesFrom(container.innerHTML); }
            return page._boxes;
        }
    };
    const ApiClient = {
        getUrl: function (u) { return '/' + u; },
        getJSON: function () { return Promise.resolve(MAPPINGS); },
        ajax: function (o) { calls.ajax.push(o); return ajaxOutcome === 'refuse' ? Promise.reject(new Error('no')) : Promise.resolve({}); }
    };
    const noop = function () {};
    const names = ['renderTrusted', 'renderGettingStarted', 'renderInjectionStatus', 'setStatus', 'pinStatusRows', 'askFirst',
        'bindStandaloneForms', 'paintOutcome', 'errorText', 'Dashboard', 'console'];
    const stubs = [noop, noop, noop, noop, noop, function () { return Promise.resolve(); }, noop, noop,
        function (err, fallback) { return Promise.resolve(fallback); }, { hideLoadingMsg: noop, showLoadingMsg: noop },
        { error: noop, log: noop }];
    const api = new Function('ApiClient', names.join(','), source + '; return loadMappings;').apply(null, [ApiClient].concat(stubs));
    api(page);
    await drain();
    return { page: page, container: container, calls: calls, boxes: page.querySelectorAll('.jpf-account-extras') };
}

(async function main() {
    console.log('\n── What is drawn ───────────────────────────────────────────────');
    const s = await scenario('ok');
    const html = s.container.innerHTML;
    const forms = (html.match(/class="pin-prompts-form /g) || []).length;
    const toggles = (html.match(/data-form="pin-prompts-form"/g) || []).length;
    ok('a PIN prompts control for each main account that is not an administrator (' + forms + ' forms, ' + toggles + ' buttons)', forms === 2 && toggles === 2);
    const owner = s.boxes.filter(function (b) { return b.id === 'cccc'; })[0];
    ok('none on an administrator\'s own household', !!owner && !owner.ask && owner.bypass.length === 0);
    const elder = s.boxes.filter(function (b) { return b.id === 'aaaa'; })[0];
    const solo = s.boxes.filter(function (b) { return b.id === 'bbbb'; })[0];
    ok('"Ask PIN to open" starts as the server said, off for one and on for another', !!elder && !!solo && elder.ask && elder.ask.checked === false && solo.ask && solo.ask.checked === true);
    ok('one switch for each of its profiles, in the server\'s state', !!elder && elder.bypass.length === 2
        && elder.bypass[0].getAttribute() === 'a1' && elder.bypass[0].checked === true
        && elder.bypass[1].getAttribute() === 'a2' && elder.bypass[1].checked === false);
    ok('a household with no profiles has only the main switch', !!solo && solo.bypass.length === 0);
    ok('a profile name is escaped in the label', html.indexOf('Pip <b>') === -1 && html.indexOf('Pip &lt;b&gt;') !== -1);

    console.log('\n── What Save posts ─────────────────────────────────────────────');
    ok('Save is wired on the control', !!elder && typeof elder.clicks.go === 'function');
    if (elder && elder.clicks.go) {
        elder.ask.checked = true;
        elder.bypass[0].checked = false;
        elder.bypass[1].checked = true;
        elder.clicks.go();
        await drain();
        const call = s.calls.ajax[0];
        ok('one request, to the admin route', s.calls.ajax.length === 1 && !!call && call.type === 'POST' && call.url === '/plugins/profiles/admin/pin-prompts');
        const body = call ? JSON.parse(call.data) : {};
        ok('the body has the household, the main switch and the profiles, and nothing else',
            Object.keys(body).sort().join() === 'askPinToOpen,householdId,profiles', Object.keys(body).join());
        ok('with what the switches say', body.householdId === 'aaaa' && body.askPinToOpen === true
            && body.profiles && body.profiles.length === 2
            && body.profiles[0].profileId === 'a1' && body.profiles[0].bypassPinOnLocalNetwork === false
            && body.profiles[1].profileId === 'a2' && body.profiles[1].bypassPinOnLocalNetwork === true);
        ok('each profile carries only its id and the one field',
            !!body.profiles && body.profiles.every(function (p) { return Object.keys(p).sort().join() === 'bypassPinOnLocalNetwork,profileId'; }));
        ok('and says so when it is saved', elder.result.textContent === 'Saved.', elder.result.textContent);
    }

    console.log('\n── A refusal ───────────────────────────────────────────────────');
    const bad = await scenario('refuse');
    const b = bad.boxes.filter(function (x) { return x.id === 'aaaa'; })[0];
    if (b && b.clicks.go) { b.clicks.go(); await drain(); }
    ok('a refused save says so, and does not say saved', !!b && b.result.textContent !== '' && b.result.textContent !== 'Saved.', b ? b.result.textContent : '');

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
