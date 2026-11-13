// Email in the web client (1.7.2): Your account → Email, "Forgot PIN?" on the main PIN
// prompts, and first-time setup's email step.
//
// Runs the real code against the stub DOM and a recording fetch, then reads back what was
// drawn and what was sent. Red against ea03064, whose client has none of it.
//
//   node tests/js/accountemail.js [path/to/profiles.js]

'use strict';
const L = require('./_lib');
const D = require('./_stubdom');

const SRC = L.readProfiles();

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

const env = D.loadPlugin(SRC);
const { PP, content, posts, settle } = env;

const has = ['_bindAccountEmail', 'showPinReset', '_offerPinReset', '_onboardingEmail']
    .filter(n => typeof PP[n] === 'function');
ok('this build has email in the client', has.length === 4, has.join(','));
if (has.length !== 4) {
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}

// ── The server, as a table the tests change ──────────────────────────────────
const server = {
    me: { pinResetByEmail: true },
    email: { available: true, address: '', pendingAddress: '', notifyLockout: true, notifyNewDevice: true, pinResetEnabled: true },
    refuse: {}     // url → [status, text] for the next call
};
env.setRespond((url, body) => {
    if (server.refuse[url]) { const r = server.refuse[url]; delete server.refuse[url]; return { status: r[0], body: r[1], headers: r[2] ? { 'X-Bonfire-Error': r[2] } : undefined }; }
    if (url === 'plugins/profiles/startup') return { status: 200, body: { me: server.me } };
    if (url === 'plugins/profiles/account/email' && !body) return { status: 200, body: server.email };
    if (url === 'plugins/profiles/account/email') {
        // One route since 2.0: address '' removes, an address is sent a code, a switch is stored.
        if (body.address === '') server.email = Object.assign({}, server.email, { address: '', pendingAddress: '' });
        else if (body.address !== undefined) server.email = Object.assign({}, server.email, { pendingAddress: body.address });
        server.email = Object.assign({}, server.email, body.notifyLockout !== undefined ? { notifyLockout: body.notifyLockout } : {},
            body.notifyNewDevice !== undefined ? { notifyNewDevice: body.notifyNewDevice } : {});
        return { status: 200, body: server.email };
    }
    if (url === 'plugins/profiles/account/email/confirm') {
        server.email = Object.assign({}, server.email, { address: server.email.pendingAddress, pendingAddress: '' });
        return { status: 200, body: server.email };
    }
    if (url === 'plugins/profiles/account/pin-reset/send') return { status: 200, body: { sentTo: 'p•••@example.com' } };
    if (url === 'plugins/profiles/account/pin-reset') return { status: 200, body: '' };
    return { status: 200, body: {} };
});

PP.beginNavigation = () => 1;
PP.navIsCurrent = () => true;
PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
PP.handleSessionExpired = () => {};
PP.showProfileOverlay = () => { content.innerHTML = '<div id="the-grid"></div>'; };
PP.checkRoute = () => {};
PP.currentProfiles = [
    { profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false, hasPin: true },
    { profileUserId: 'kid', profileName: 'Kid', isMaster: false, isBonfire: false, hasPin: true }
];
PP.masterPin = '1234';

const sent = url => posts.filter(p => p.url === url);
const last = url => sent(url).pop();

(async () => {
    // ── Your account → Email ────────────────────────────────────────────────
    console.log('\n── Your account → Email ────────────────────────────────────────');

    let slot = D.makeContent();
    const q = sel => slot.querySelector(sel);
    server.email.available = false;
    PP._bindAccountEmail(slot);
    await settle();
    // Was "no section is drawn". P2-607: a hint that email is off and who can turn it on.
    ok('without email on the server, the section says it is off and draws no controls',
        /account-email-off/.test(slot.innerHTML) && !/<input|<button/.test(slot.innerHTML));
    const get = last('plugins/profiles/account/email');
    ok('the address is asked for with the main PIN', get && !get.body && get.init.headers['X-Bonfire-Master-Pin'] === '1234');

    server.email.available = true;
    slot = D.makeContent();
    PP._bindAccountEmail(slot);
    await settle();
    ok('with email, an empty address field and Send', !!q('#account-email-input') && !!q('#account-email-send'));
    ok('under its own heading', slot.innerHTML.indexOf('For a forgotten PIN and notices') !== -1);

    let before = posts.length;
    q('#account-email-input').value = 'not an address';
    q('#account-email-send').click();
    await settle();
    ok('a malformed address is refused without a request', posts.length === before
       && q('#account-email-status').textContent === 'Enter an email address.');

    q('#account-email-input').value = ' parent@example.com ';
    q('#account-email-send').click();
    await settle();
    let p = last('plugins/profiles/account/email');
    ok('Send posts the address with the main PIN and the language', p && p.body.address === 'parent@example.com'
       && p.body.masterPin === '1234' && 'language' in p.body);
    ok('then asks for the code', !!q('#account-email-code') && !q('#account-email-input'));
    ok('saying where it went', q('#account-email-sent').textContent === 'Enter the code sent to parent@example.com.');

    before = posts.length;
    q('#account-email-code').value = '12';
    q('#account-email-confirm').click();
    await settle();
    ok('a short code is refused without a request', posts.length === before
       && q('#account-email-status').textContent === 'Enter the 6-digit code.');

    server.refuse['plugins/profiles/account/email/confirm'] = [400, "That code isn't right.", 'code_wrong'];
    q('#account-email-code').value = '000000';
    q('#account-email-confirm').click();
    await settle();
    // Replaced behaviour: a refusal used to be shown in the server's English words; it is said by its X-Bonfire-Error code now.
    ok("the refusal is shown by its code, still on the code", !!q('#account-email-code')
       && q('#account-email-status').textContent === "That code isn't right. Check it and try again.");

    q('#account-email-code').value = '123 456';
    q('#account-email-confirm').click();
    await settle();
    p = last('plugins/profiles/account/email/confirm');
    ok('the code is sent without its space', p && p.body.code === '123456');
    ok('then the address is shown, with Change and Remove', slot.innerHTML.indexOf('parent@example.com') !== -1
       && !!q('#account-email-change') && !!q('#account-email-remove'));
    ok('and says it was saved', q('#account-email-status').textContent === 'Email saved.');
    ok('the reset hint shows while the server allows reset', slot.innerHTML.indexOf('If you forget your PIN') !== -1);
    ok('both notices are on', q('#account-email-lockout').checked && q('#account-email-device').checked);
    // The four notice boxes sit under a closed "More" fold; the address row does not.
    const inClosedFold = (id) => {
        const html = slot.innerHTML;
        const at = html.indexOf('id="' + id + '"');
        const open = html.lastIndexOf('<details', at);
        if (at < 0 || open < 0 || html.lastIndexOf('</details>', at) > open) return false;
        return !/^<details[^>]*\sopen[\s>]/.test(html.slice(open, html.indexOf('>', open) + 1));
    };
    ok('the four notice boxes are under a closed More fold',
        ['account-email-lockout', 'account-email-device', 'account-email-asked', 'account-email-limit'].every(inClosedFold));
    ok('and the address row is not', !inClosedFold('account-email-change'));

    const box = q('#account-email-lockout');
    box.checked = false;
    box.fire('change');
    await settle();
    p = last('plugins/profiles/account/email');
    ok('turning a notice off saves only that one, and leaves the address alone', p && p.body.notifyLockout === false
       && !('notifyNewDevice' in p.body) && !('address' in p.body));
    server.refuse['plugins/profiles/account/email'] = [400, 'No.', 'forbidden'];
    const dev = q('#account-email-device');
    dev.checked = false;
    dev.fire('change');
    await settle();
    ok('a refused change puts the box back', dev.checked === true && q('#account-email-status').textContent === "You can't do that here.");

    // An address that is markup must come out as text.
    server.email.address = 'a<img src=x>@example.com';
    slot = D.makeContent();
    PP._bindAccountEmail(slot);
    await settle();
    ok('the address is escaped', slot.innerHTML.indexOf('<img') === -1 && slot.innerHTML.indexOf('a&lt;img') !== -1);
    server.email.address = 'parent@example.com';

    slot = D.makeContent();
    PP._bindAccountEmail(slot);
    await settle();
    q('#account-email-change').click();
    ok('Change opens the field with the address in it, and Cancel', q('#account-email-input').value === 'parent@example.com'
       && !!q('#account-email-cancel'));
    q('#account-email-cancel').click();
    ok('Cancel goes back', !!q('#account-email-remove'));

    q('#account-email-remove').click();
    await settle();
    ok('Remove posts an empty address and goes back to an empty field', last('plugins/profiles/account/email').body.address === ''
       && !!q('#account-email-input') && q('#account-email-status').textContent === 'Email removed.');

    server.refuse['plugins/profiles/account/email'] = [429, 'Too many codes sent.', 'rate_limited'];
    q('#account-email-input').value = 'parent@example.com';
    q('#account-email-send').click();
    await settle();
    ok('a refused send says why and keeps the field', !!q('#account-email-input')
       && q('#account-email-status').textContent === 'Too many tries. Wait a moment and try again.');

    // The real page draws the slot and binds it.
    const page = D.makeContent();
    const ask = sent('plugins/profiles/account/email').filter(x => !x.body).length;
    PP._renderAccountSettings(page, PP.currentProfiles[0], [], { masterUserId: 'main', token: 'tok' });
    await settle();
    ok('Your account has the Email section, after sign-in', /jpf-account-pin-input[\s\S]*account-email-slot[\s\S]*devices/.test(page.innerHTML));
    ok('and asks for it when the page opens', sent('plugins/profiles/account/email').filter(x => !x.body).length === ask + 1);

    // ── Forgot PIN ──────────────────────────────────────────────────────────
    console.log('\n── Forgot PIN ──────────────────────────────────────────────────');

    const id = sel => content.querySelector(sel);
    let carriedOn = 0;
    server.me = { pinResetByEmail: false };
    PP.promptMasterPinEntry('manage', () => carriedOn++);
    await settle();
    ok('the main PIN prompt asks /startup with the main account', last('plugins/profiles/startup').init.headers
       && JSON.stringify(last('plugins/profiles/startup').init.headers).indexOf('tok') !== -1);
    ok('no link when the server cannot reset by email', id('#pin-forgot').hidden !== false);

    server.me = { pinResetByEmail: true }; PP._startupHeld = null;
    PP.promptMasterPinEntry('manage', () => carriedOn++);
    await settle();
    ok('a link when it can', id('#pin-forgot').hidden === false);

    before = sent('plugins/profiles/account/pin-reset/send').length;
    id('#pin-forgot').click();
    await settle();
    ok('it sends the code as the screen opens', sent('plugins/profiles/account/pin-reset/send').length === before + 1);
    ok('and says where, masked', id('#pin-reset-note').textContent === 'Enter the code sent to p•••@example.com, then a new PIN.');

    before = sent('plugins/profiles/account/pin-reset').length;
    id('#jpf-pin-reset-code').value = '12345';
    id('#jpf-pin-reset-new').value = '2468';
    id('#pin-reset-save').click();
    await settle();
    ok('a short code is refused here', sent('plugins/profiles/account/pin-reset').length === before
       && id('#pin-reset-error').textContent === 'Enter the 6-digit code.');
    id('#jpf-pin-reset-code').value = '123456';
    id('#jpf-pin-reset-new').value = '24';
    id('#pin-reset-save').click();
    await settle();
    ok('so is a short PIN', sent('plugins/profiles/account/pin-reset').length === before);

    server.refuse['plugins/profiles/account/pin-reset'] = [400, "That code isn't right.", 'code_wrong'];
    id('#jpf-pin-reset-new').value = '2468';
    id('#pin-reset-save').click();
    await settle();
    ok("the server's refusal is shown and nothing carries on", id('#pin-reset-error').textContent === "That code isn't right. Check it and try again."
       && carriedOn === 0 && PP.masterPin === '1234');

    const sends = sent('plugins/profiles/account/pin-reset/send').length;
    id('#pin-reset-again').click();
    await settle();
    ok('Send a new code sends again', sent('plugins/profiles/account/pin-reset/send').length === sends + 1);

    id('#jpf-pin-reset-code').value = '654321';
    id('#jpf-pin-reset-new').value = '2468';
    id('#pin-reset-save').click();
    await settle();
    p = last('plugins/profiles/account/pin-reset');
    ok('the code and the new PIN are posted', p && p.body.code === '654321' && p.body.newPin === '2468');
    ok('and it carries on with the new PIN, not asking for it again', carriedOn === 1 && PP.masterPin === '2468');

    PP.promptMasterPinEntry('manage', () => carriedOn++);
    await settle();
    id('#pin-forgot').click();
    await settle();
    id('#pin-reset-back').click();
    ok('Back returns to the PIN prompt', !!id('#jpf-master-pin-input'));

    // The picker's PIN prompt: the main account's tile only.
    const switched = [];
    PP.executeProfileSwitch = (pid, pin) => switched.push([pid, pin]);
    const meAsks = sent('plugins/profiles/startup').length;
    PP.promptPinEntry('kid');
    await settle();
    ok("a profile's PIN prompt has no link and asks nothing", sent('plugins/profiles/startup').length === meAsks
       && id('#pin-forgot').hidden !== false);
    PP.promptPinEntry('main');
    await settle();
    ok("the main account's tile does", id('#pin-forgot').hidden === false);
    id('#pin-forgot').click();
    await settle();
    id('#jpf-pin-reset-code').value = '111111';
    id('#jpf-pin-reset-new').value = '1357';
    id('#pin-reset-save').click();
    await settle();
    ok('and a reset there switches in with the new PIN', switched.length === 1 && switched[0][0] === 'main' && switched[0][1] === '1357');

    // ── First-time setup ────────────────────────────────────────────────────
    console.log('\n── First-time setup ────────────────────────────────────────────');

    ok('the email step is one the client knows', /ONBOARDING_STEPS\s*=\s*\[[^\]]*'resetEmail'/.test(SRC));

    let finished = 0;
    PP.finishOnboarding = () => { finished++; };
    server.email = { available: true, address: '', pendingAddress: '' };
    PP._onboarding = { steps: [{ id: 'resetEmail', required: false, done: false }], welcome: '', index: 0 };
    PP.showOnboardingStep();
    ok('it asks for an address, with Skip', !!id('#onboarding-email') && !!id('#onboarding-skip'));
    ok('and says what it is for', content.innerHTML.indexOf('If you forget your PIN, a code can be sent here.') !== -1);

    before = posts.length;
    id('#onboarding-email').value = 'nope';
    id('#onboarding-next').click();
    await settle();
    ok('a malformed address is refused without a request', posts.length === before
       && id('#onboarding-error').textContent === 'Enter an email address.');

    server.refuse['plugins/profiles/account/email'] = [400, "Email isn't set up on this server.", 'email_unavailable'];
    id('#onboarding-email').value = 'parent@example.com';
    id('#onboarding-next').click();
    await settle();
    ok('a refusal stays on the step, says why, and keeps the address', !!id('#onboarding-email')
       && id('#onboarding-error').textContent === 'Email isn\'t set up on this server. Ask an administrator.'
       && id('#onboarding-email').value === 'parent@example.com');

    id('#onboarding-next').click();
    await settle();
    ok('Send posts it and asks for the code', !!id('#onboarding-email-code')
       && last('plugins/profiles/account/email').body.address === 'parent@example.com');
    ok('saying where it went', id('#onboarding-email-sent').textContent === 'Enter the code sent to parent@example.com.');

    id('#onboarding-email-code').value = '42';
    id('#onboarding-next').click();
    await settle();
    ok('a short code is refused', !!id('#onboarding-email-code') && id('#onboarding-error').textContent === 'Enter the 6-digit code.');

    id('#onboarding-email-code').value = '424242';
    id('#onboarding-next').click();
    await settle();
    ok('the code confirms it and setup moves on', last('plugins/profiles/account/email/confirm').body.code === '424242' && finished === 1);

    PP._onboarding = { steps: [{ id: 'resetEmail', required: true, done: false }], welcome: '', index: 0 };
    PP.showOnboardingStep();
    // "Can't skip" is gone. A server that has not been updated yet still sends the flag; it
    // must not take Skip away.
    ok('a step an older server marks "required" still has Skip', !!id('#onboarding-email') && !!id('#onboarding-skip'));
    id('#onboarding-email').value = 'parent@example.com';
    id('#onboarding-next').click();
    await settle();
    id('#onboarding-email-other').click();
    ok('"Use a different address" goes back to the address', !!id('#onboarding-email'));
    PP._onboarding = null;

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
