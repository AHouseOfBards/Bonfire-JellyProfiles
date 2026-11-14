/*
 * What a phone or tablet user does, driven by taps in WebKit (Safari, the iPhone and iPad
 * app), at both sizes:
 *
 *   1. Pick a profile that has a PIN. The PIN screen opens with its box focused and set to
 *      the numeric keyboard; digits reach the box; a wrong PIN shows "Wrong PIN. Try again."
 *      and empties the box; the right one sends the switch with those digits and hands over
 *      to the reload. The server is stubbed the way the fixture stubs it (tests/screens/
 *      scenes.js): verify-pin and switch answer from the PIN, nothing else is real.
 *   2. Open Edit profile. Each text box takes what is typed and keeps focus (the 1.5.x bug
 *      was a form where buttons worked and text boxes did not), each dropdown takes a choice,
 *      a checkbox toggles.
 *
 * Red against a build where the PIN box or the form's fields cannot take input (see the
 * commit that added this file for the runs):
 *
 *     node tests/js/webkitflows.js [path/to/profiles.js]
 *
 * Says SKIP and exits 0 when Playwright's WebKit is not installed (see _webkit.js).
 */
'use strict';

const W = require('./_webkit');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

const RIGHT_PIN = '1234';

// The server, as far as these two screens ask it anything. Runs in the page.
function stubServer(rightPin) {
    window.__log = [];
    // What jellyfin-web keeps, which a switch rewrites so the reload signs in as the profile.
    localStorage.setItem('jellyfin_credentials', JSON.stringify({ Servers: [{ Id: 'fixture', UserId: '11111111111111111111111111111111', AccessToken: 'fixture-token' }] }));
    const real = window.fetch;
    window.fetch = function (input, init) {
        const url = new URL(typeof input === 'string' ? input : input.url, location.href);
        const body = init && init.body ? JSON.parse(init.body) : {};
        if (url.pathname === '/plugins/profiles/verify-pin') {
            window.__log.push('verify ' + body.pin);
            return Promise.resolve(new Response('{}', { status: body.pin === rightPin ? 200 : 403 }));
        }
        if (url.pathname === '/plugins/profiles/switch') {
            window.__log.push('switch ' + body.profileId + ' ' + body.pin);
            return Promise.resolve(body.pin === rightPin
                ? new Response(JSON.stringify({ activeProfileToken: 'token-for-sam', jellyfinUserId: body.profileId }),
                    { status: 200, headers: { 'Content-Type': 'application/json' } })
                : new Response('', { status: 403 }));
        }
        return real.apply(this, arguments);
    };
    // The page would navigate away here; the harness wants to see that it was asked to.
    window.__bonfire.reloadAtHome = function () { window.__reloaded = true; };
}

async function pinFlow(browser, srv, device) {
    const tag = device + ' PIN: ';
    const s = await W.openScene(browser, srv, 'picker', device);
    const p = s.page;
    await p.evaluate(stubServer, RIGHT_PIN);

    await p.locator('.jpf-profile-card', { hasText: 'Sam' }).tap();
    const box = p.locator('#jpf-profile-pin-input');
    await box.waitFor({ state: 'visible', timeout: 5000 });
    const open = await p.evaluate(() => {
        const b = document.getElementById('jpf-profile-pin-input'), r = b.getBoundingClientRect();
        return { inputmode: b.getAttribute('inputmode'), focused: document.activeElement === b,
            w: r.width, inView: r.left >= 0 && r.right <= innerWidth && r.top >= 0 && r.bottom <= innerHeight };
    });
    ok(tag + 'tapping a profile with a PIN opens the PIN screen with the numeric keyboard', open.inputmode === 'numeric' && open.w > 0, JSON.stringify(open));
    ok(tag + 'the PIN box is on screen', open.inView, JSON.stringify(open));
    ok(tag + 'the PIN box has focus without a tap', open.focused, JSON.stringify(open));

    // Wrong: a tap on the box, digits, then Unlock.
    await box.tap();
    await p.keyboard.type('9999');
    ok(tag + 'digits reach the PIN box', (await box.inputValue()) === '9999', 'value: ' + await box.inputValue());
    await p.locator('#pin-submit-btn').tap();
    await p.waitForFunction(() => { const e = document.getElementById('pin-error-msg'); return e && e.style.display !== 'none' && e.textContent; }, null, { timeout: 5000 }).catch(() => {});
    const wrong = await p.evaluate(() => ({ msg: document.getElementById('pin-error-msg').textContent, value: document.getElementById('jpf-profile-pin-input').value, reloaded: !!window.__reloaded }));
    ok(tag + 'a wrong PIN says so', wrong.msg === 'Wrong PIN. Try again.', JSON.stringify(wrong));
    ok(tag + 'a wrong PIN empties the box and does not switch', wrong.value === '' && !wrong.reloaded, JSON.stringify(wrong));

    // Right: the box is refocused by the page; tap it anyway, as a person would.
    await box.tap();
    await p.keyboard.type(RIGHT_PIN);
    await p.waitForFunction(() => window.__reloaded === true, null, { timeout: 8000 }).catch(() => {});
    const done = await p.evaluate(() => ({ log: window.__log, reloaded: !!window.__reloaded,
        stored: JSON.parse(localStorage.getItem('jellyfin_credentials')).Servers[0] }));
    ok(tag + 'the right PIN stores the new session for the reload',
        done.stored.AccessToken === 'token-for-sam' && done.stored.UserId === '22222222222222222222222222222222', JSON.stringify(done.stored));
    ok(tag + 'the right PIN sends the switch with those digits', done.log.some(l => l === 'switch 22222222222222222222222222222222 ' + RIGHT_PIN), JSON.stringify(done));
    ok(tag + 'the right PIN hands over to the reload', done.reloaded, JSON.stringify(done));
    ok(tag + 'no console error or uncaught exception', s.errors.length === 0, s.errors.join(' | '));
    await s.close();
}

async function editFlow(browser, srv, device) {
    const tag = device + ' edit form: ';
    const s = await W.openScene(browser, srv, 'edit', device);
    const p = s.page;
    const focused = id => p.evaluate(i => document.activeElement && document.activeElement.id === i, id);

    for (const id of ['edit-name-input', 'jpf-edit-pin-input']) {
        const box = p.locator('#' + id);
        await box.tap();
        const stays = await focused(id);
        await p.keyboard.type(id === 'edit-name-input' ? 'Sammy' : '4321');
        const v = await box.inputValue();
        ok(tag + id + ' takes a tap and keeps focus', stays && (await focused(id)), 'focus after tap: ' + stays);
        ok(tag + id + ' takes typing', /(Sammy|4321)$/.test(v) && v.length >= 4, 'value: ' + v);
    }
    const name = await p.locator('#edit-name-input').inputValue();
    ok(tag + 'the name box added to what was there', name.indexOf('Sammy') > 0 || name === 'Sammy', 'value: ' + name);

    for (const id of ['edit-rating-select', 'viewing-daily']) {
        const sel = p.locator('#' + id);
        const options = await sel.evaluate(e => Array.from(e.options).map(o => o.value));
        const before = await sel.inputValue();
        const pick = options.filter(v => v !== before).pop();
        await sel.selectOption(pick);
        const after = await sel.inputValue();
        ok(tag + id + ' takes a choice', pick !== undefined && after === pick, 'was "' + before + '", picked "' + pick + '", now "' + after + '"');
    }

    const check = p.locator('#edit-select-all-libraries');
    const was = await check.isChecked();
    await check.evaluate(e => e.closest('label') ? e.closest('label').scrollIntoView() : e.scrollIntoView());
    await check.tap({ force: true }).catch(() => {});
    ok(tag + 'a checkbox toggles on tap', (await check.isChecked()) !== was, 'was ' + was);
    ok(tag + 'no console error or uncaught exception', s.errors.length === 0, s.errors.join(' | '));
    await s.close();
}

(async () => {
    const browser = await W.launch();
    const src = W.sources();
    const srv = await W.serve(src.js, src.cssPath);
    try {
        for (const device of ['phone', 'tablet']) {
            await pinFlow(browser, srv, device);
            await editFlow(browser, srv, device);
        }
    } finally {
        await browser.close();
        await srv.close();
    }
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.log('FAIL  harness error: ' + (e && e.stack || e)); process.exit(1); });
