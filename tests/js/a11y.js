// Screen reader and remote behaviour of the client screens (P2 accessibility findings).
//
// Each block draws a real screen through the stub DOM and reads the markup a screen
// reader would read. Point it at an older profiles.js to see it fail:
//
//   node tests/js/a11y.js [path/to/profiles.js]

'use strict';
const L = require('./_lib');
const D = require('./_stubdom');

const SRC = L.readProfiles();

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const env = D.loadPlugin(SRC);
const { PP, content, settle } = env;
PP.beginNavigation = () => 1;
PP.navIsCurrent = () => true;
PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
PP.currentProfiles = [{ profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false, hasPin: true }];

function tagOf(id) {
    const m = new RegExp('<[a-z]+[^>]*\\bid="' + id + '"[^>]*>').exec(content.innerHTML);
    return m ? m[0] : '';
}

(async () => {
    // 2. A wrong PIN is spoken, and the box says it is invalid.
    const pinScreens = [
        ['the profile PIN', 'profile-pin-input', 'pin-error-msg', () => PP.promptPinEntry('kid')],
        ['the main PIN', 'master-pin-input', 'master-pin-error-msg', () => PP.promptMasterPinEntry('manage', () => {})]
    ];
    for (const s of pinScreens) {
        s[3]();
        await settle();
        const err = tagOf(s[2]);
        ok(s[0] + ': the error line is an alert', /role="alert"/.test(err), err);
        const box = tagOf(s[1]);
        ok(s[0] + ': the box points at its error', new RegExp('aria-describedby="' + s[2] + '"').test(box), box);
    }

    // 3, 4, 13. A switch that fails, or never answers, puts the person back somewhere
    // they can act, and says why in words that are not the browser's.
    {
        const sb = env.sandbox;
        const timers = [];
        sb.setTimeout = (fn, ms) => { timers.push({ fn, ms }); return timers.length; };
        sb.clearTimeout = (id) => { if (timers[id - 1]) timers[id - 1].dead = true; };
        const fireAll = () => timers.splice(0).forEach(x => { if (!x.dead) x.fn(); });
        PP.interceptHomeAndShowProfiles = () => { PP._redrawn = true; };
        let alerted = null;
        PP.showAlert = (title, body) => { alerted = { title, body }; };
        PP.setSwitchBusy = (id, busy) => { PP._busy = busy; };
        const UNREACHABLE = "Can't reach the server. Try again.";

        // A request that never answers.
        let release;
        sb.fetch = () => new Promise(r => { release = r; });
        PP._switchLock = false;
        let msg = null;
        PP.executeProfileSwitch('kid', '1234', m => { msg = m; });
        await settle();
        ok('a hung switch is still waiting before the timer fires', msg === null && PP._switchLock === true);
        fireAll();
        await settle();
        ok('a hung switch gives the controls back', PP._switchLock === false && PP._busy === false,
            'lock=' + PP._switchLock + ' busy=' + PP._busy);
        ok('and says the server cannot be reached', msg === UNREACHABLE, String(msg));
        // The reply turning up late must not carry on into a reload nobody is waiting for.
        let stored = false;
        PP.updateStoredCredentials = () => { stored = true; return false; };
        release({ ok: true, json: () => Promise.resolve({ activeProfileToken: 't', jellyfinUserId: 'u' }) });
        await settle();
        ok('a reply after the timeout is ignored', stored === false);

        // The browser's own words.
        sb.fetch = () => Promise.reject(new TypeError('Failed to fetch'));
        msg = null;
        PP.executeProfileSwitch('kid', '1234', m => { msg = m; });
        await settle();
        ok('"Failed to fetch" is not shown to anyone', msg === UNREACHABLE, String(msg));

        // The server's own words still go through.
        sb.fetch = () => Promise.resolve({ ok: false, status: 403, text: () => Promise.resolve('Too many attempts') });
        msg = null;
        PP.executeProfileSwitch('kid', '1234', m => { msg = m; });
        await settle();
        ok('a refusal from the server is shown as written', msg === 'Too many attempts', String(msg));

        // A tap on a profile with no PIN has no screen to report on.
        PP._redrawn = false;
        alerted = null;
        PP.executeProfileSwitch('kid', null);
        await settle();
        ok('a refused tap puts the picker back', PP._redrawn === true);
        ok('and tells the person why', alerted !== null && /Too many attempts/.test(alerted.body), JSON.stringify(alerted));
        sb.fetch = () => Promise.reject(new TypeError('Load failed'));
        alerted = null;
        PP.executeProfileSwitch('kid', null);
        await settle();
        ok('an unreachable server on a tap is said in plain words',
            alerted !== null && alerted.body.replace('&#039;', "'") === UNREACHABLE, JSON.stringify(alerted));
    }

    // 5. A dialog says it is one, is named by its heading, and gives focus back.
    {
        const env2 = D.loadPlugin(SRC);
        const PP = env2.PP;
        const sb = env2.sandbox;
        const D2 = D;
        const made = [];
        const heading = { id: '' };
        sb.document.createElement = () => {
            const n = D2.makeContent();
            n.remove = () => { n.removed = true; };
            n.select = () => {};
            const own = n.querySelector;
            n.querySelector = sel => {
                if (/^h1, h2, h3$/.test(sel)) return heading;
                const r = own(sel);
                if (r && !r.select) r.select = () => {};
                return r;
            };
            made.push(n);
            return n;
        };
        const opener = { focused: 0, focus() { this.focused++; } };
        sb.document.activeElement = opener;
        sb.document.body.contains = () => true;
        sb.setTimeout = fn => { fn(); return 0; };
        const drawers = [
            ['an alert', () => PP.showAlert('Title', 'Body')],
            ['a confirm', () => PP.showConfirmDialog('Title', 'Body', () => {})],
            ['a prompt', () => PP.showPromptDialog('Title', 'Label', '', () => {})]
        ];
        for (const dr of drawers) {
            made.length = 0; opener.focused = 0; heading.id = '';
            dr[1]();
            const d = made[0];
            ok(dr[0] + ': is a dialog', !!d && d.attrs.role === 'dialog', d && JSON.stringify(d.attrs));
            ok(dr[0] + ': is modal', !!d && d.attrs['aria-modal'] === 'true');
            ok(dr[0] + ': is named by its heading', !!d && !!heading.id && d.attrs['aria-labelledby'] === heading.id,
                d && JSON.stringify(d.attrs));
            if (d) d.remove();
            ok(dr[0] + ': closing hands focus back to what opened it', opener.focused === 1, 'focused ' + opener.focused);
        }
    }

    // 6. A profile tile is a button, and says what it will ask for.
    {
        const env3 = D.loadPlugin(SRC);
        const P3 = env3.PP;
        P3.beginNavigation = () => 1;
        P3.navIsCurrent = () => true;
        const overlay = D.makeContent();
        const profiles = [
            { profileUserId: 'aaaa', profileName: 'Sam', isMaster: true, isBonfire: false, requiresPin: false, avatarColor: '#E50914', avatarInitial: 'S' },
            { profileUserId: 'bbbb', profileName: 'Kit', isMaster: false, isBonfire: true, requiresPin: true, avatarColor: '#00A4DC', avatarInitial: 'K' }
        ];
        P3.currentProfiles = profiles;
        P3.isManageMode = false;
        try { P3.renderOverlayContent(overlay, profiles); } catch (e) { console.log('  (render threw: ' + e.message + ')'); }
        const html = overlay.innerHTML;
        const cards = html.match(/<div class="profile-card[^>]*>/g) || [];
        ok('the tiles are drawn', cards.length === 2, 'drew ' + cards.length);
        ok('each tile is a button to a screen reader', cards.length > 0 && cards.every(c => /role="button"/.test(c)), cards[0]);
        const kit = html.slice(html.indexOf('data-id="bbbb"'));
        ok('a PIN-protected tile says so in words', /class="jpf-sr">PIN Protected</.test(kit.slice(0, kit.indexOf('profile-card') > 0 ? kit.length : 2000)));
        // 17. Opening a profile is announced, not only drawn as a spinner.
        env3.sandbox.document.getElementById = id => overlay.querySelector('#' + id);
        const live = overlay.querySelector('#jpf-switch-live');
        ok('there is a live region to announce in', !!live && /role="status"/.test(html), String(live));
        if (live) {
            P3.setSwitchBusy('bbbb', true);
            ok('opening a profile is said in words', live.textContent === 'Opening Kit', live.textContent);
            P3.setSwitchBusy('bbbb', false);
            ok('and the line is cleared when it is over', live.textContent === '', live.textContent);
        }
        ok('the picture badges are hidden from a screen reader',
            !/<div class="profile-lock-indicator">/.test(html) && !/<div class="profile-bonfire-indicator" title/.test(html));
    }

    // 7. The colour swatches are a radio group, named by position, with a state.
    {
        const P4 = D.loadPlugin(SRC).PP;
        const html = P4.renderColorPicker('#E50914');
        const dots = html.match(/<div class="color-dot[^>]*>/g) || [];
        ok('the swatches are drawn', dots.length > 1, dots.length + ' drawn');
        ok('they sit in a labelled group', /role="radiogroup" aria-label="[^"]+"/.test(html));
        ok('each says whether it is chosen', dots.length > 0 && dots.every(d => /aria-checked="(true|false)"/.test(d)));
        ok('exactly the chosen colour is checked', dots.filter(d => /aria-checked="true"/.test(d)).length === 1);
        ok('none is announced as a hex code', dots.every(d => !/aria-label="#/.test(d)), dots[0]);
        ok('and each is still a tab stop, for the remote', dots.every(d => /tabindex="0"/.test(d)));
    }

    // 16. A label over a custom widget is tied to it. A bare <label> names nothing.
    {
        const bare = [];
        SRC.replace(/<label>\$\{t\('([^']+)'\)\}<\/label>/g, (m, key) => { bare.push(key); return m; });
        // The colour picker is a radiogroup with its own name; its label is a caption.
        const unnamed = bare.filter(k => k !== 'profileForm.avatarColor');
        ok('every label over a widget is tied to it' + (unnamed.length ? ' (bare: ' + unnamed.join(', ') + ')' : ''),
            unnamed.length === 0);
        const P5 = D.loadPlugin(SRC).PP;
        const tags = P5.renderTagEditor('x-tags', [], 'ph', 'sug', 'x-label');
        ok('a tag editor names its box after the label', /<input[^>]*aria-labelledby="x-label"/.test(tags), tags);
    }

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
