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
            if (d && /prompt/.test(dr[0])) {
                // 18. An empty name is refused out loud.
                const box = d.querySelector('#profiles-prompt-input');
                box.value = '  ';
                d.fire('keydown', { key: 'Enter' });
                ok('a prompt refused for an empty name marks the box invalid', box.attrs['aria-invalid'] === 'true', JSON.stringify(box.attrs));
            }
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

    // 14. A profile list that did not load says so, instead of leaving home with no
    // explanation, and takes the note down when it is stale.
    {
        const env14 = D.loadPlugin(SRC);
        const P14 = env14.PP;
        const sb = env14.sandbox;
        const timers = [];
        sb.setTimeout = (fn, ms) => { timers.push({ fn, ms }); return timers.length; };
        sb.clearTimeout = id => { if (timers[id - 1]) timers[id - 1].dead = true; };
        const page = [];
        sb.document.createElement = () => D.makeNode({});
        sb.document.body.appendChild = n => { page.push(n); };
        const noteNode = () => page.filter(n => n.id === 'jpf-list-notice' && !n.removed)[0];
        sb.document.getElementById = id => (id === 'jpf-list-notice' ? noteNode() || null : null);
        P14.navIsCurrent = () => true;
        P14.handleSessionExpired = () => { P14._expired = true; };
        P14.invalidateProfileCache();
        page.push = function (n) { n.parentNode = { removeChild: c => { c.removed = true; } }; return Array.prototype.push.call(this, n); };

        sb.fetch = () => Promise.reject(new TypeError('Failed to fetch'));
        P14.fetchAndRenderProfiles(sb.ApiClient, 'main', 'tok', true);
        await env14.settle();
        const note = noteNode();
        ok('an unreachable server leaves a note', !!note, String(page.length));
        ok('the note is a status line, in plain words',
            !!note && note.attrs.role === 'status' && note.textContent === "Can't load profiles right now.",
            note && note.textContent);
        ok('and it goes by itself', timers.some(t => t.ms === 8000 && !t.dead));
        timers.filter(t => t.ms === 8000 && !t.dead).forEach(t => t.fn());
        ok('after a few seconds it is gone', !noteNode());

        // An expired session has its own screen; no note over it.
        page.length = 0;
        sb.fetch = () => Promise.resolve({ ok: false, status: 401, text: () => Promise.resolve('') });
        P14.fetchAndRenderProfiles(sb.ApiClient, 'main', 'tok', true);
        await env14.settle();
        ok('an expired session is not announced as an unreachable server', !noteNode() && P14._expired === true);

        // A list that arrives later makes the note wrong.
        sb.fetch = () => Promise.reject(new TypeError('Failed to fetch'));
        P14.fetchAndRenderProfiles(sb.ApiClient, 'main', 'tok', true);
        await env14.settle();
        ok('another failure brings it back', !!noteNode());
        try { P14.showProfileOverlay([]); } catch (e) { /* the drawing is not under test */ }
        ok('a gate drawn afterwards takes it down', !noteNode());
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

    // 12. Focus is a ring, not only a tint. The cascade is resolved: a rule that says
    // `outline: ring` is no use if a later rule of the same weight says `outline: none`.
    // Only plain compound selectors ending in :focus are read (every control below is
    // one); media blocks and descendant rules are not, which is why the television's own
    // thicker ring is not asserted here.
    {
        const css = require('fs').readFileSync(process.argv[3] || require('path').join(L.ROOT, 'Web', 'styles.css'), 'utf8')
            .replace(/\/\*[\s\S]*?\*\//g, '');
        const rules = [];
        let depth = 0, buf = '', order = 0;
        for (let i = 0; i < css.length; i++) {
            const ch = css[i];
            if (ch === '{') {
                depth++;
                if (depth === 1) { rules.cur = buf.trim(); buf = ''; continue; }
            } else if (ch === '}') {
                depth--;
                if (depth === 0) {
                    const sels = rules.cur.charAt(0) === '@' ? [] : rules.cur.split(',').map(s => s.trim());
                    sels.forEach(s => rules.push({ sel: s, body: buf, order: order++ }));
                    buf = ''; continue;
                }
            }
            if (depth >= 1) buf += ch; else if (depth === 0) buf += ch;
        }
        const outlineOf = (el) => {
            let best = null;
            rules.forEach(r => {
                const m = /^(#[\w-]+)?((?:\.[\w-]+)*):focus$/.exec(r.sel);
                if (!m) return;
                if (m[1] && m[1].slice(1) !== el.id) return;
                const cls = m[2] ? m[2].slice(1).split('.') : [];
                if (!cls.every(c => (el.classes || []).indexOf(c) !== -1)) return;
                const d = /(?:^|;|\s)outline\s*:\s*([^;}]+)/.exec(r.body);
                if (!d) return;
                const spec = (m[1] ? 100 : 0) + (cls.length + 1) * 10;
                if (!best || spec > best.spec || (spec === best.spec && r.order > best.order)) best = { spec, order: r.order, value: d[1].trim() };
            });
            return best ? best.value : null;
        };
        const controls = [
            ['the primary button', { classes: ['profiles-btn', 'btn-primary'] }],
            ['the secondary button', { classes: ['profiles-btn', 'btn-secondary'] }],
            ['a colour swatch', { classes: ['color-dot'] }],
            ['a library picture', { classes: ['avatar-library-item'] }],
            ['a library tick-box', { classes: ['library-check-label'] }],
            ['the devices dropdown', { classes: ['devices-dropdown-trigger'] }],
            ['the profile PIN box', { id: 'profile-pin-input' }],
            ['the floating button', { id: 'profiles-floating-bubble', classes: ['profiles-floating-fallback'] }]
        ];
        controls.forEach(c => {
            const v = outlineOf(c[1]);
            ok(c[0] + ' shows a ring when focused (outline: ' + v + ')', !!v && /^\d+px solid /.test(v));
        });
    }

    // 15. Reduced motion stops the always-on and the hover motion, not only the drift.
    {
        const css = require('fs').readFileSync(process.argv[3] || require('path').join(L.ROOT, 'Web', 'styles.css'), 'utf8');
        const blocks = [];
        css.replace(/@media \(prefers-reduced-motion: reduce\)\s*\{((?:[^{}]|\{[^{}]*\})*)\}/g, (m, b) => { blocks.push(b); return m; });
        const all = blocks.join('\n');
        ok('the crown stops bobbing', /\.profile-crown[^{]*\{[^}]*animation:\s*none/.test(all));
        ok('the picker does not zoom in', /\.anim-fade-in[^{]*\{[^}]*animation:\s*none/.test(all));
        ok('tiles do not scale under the pointer or focus',
            /\.profile-card:focus \.profile-avatar[^{]*\{[^}]*transform:\s*none/.test(all));
        ok('swatches and buttons do not scale', /\.color-dot:focus[^{]*\{[^}]*transform:\s*none/.test(all));
    }

    // 19. Each dashboard panel is named by its tab, and each tab points at its panel.
    {
        const html = require('fs').readFileSync(require('path').join(L.ROOT, 'Web', 'profilesDashboard.html'), 'utf8');
        const panels = html.match(/<div class="jpf-tab-panel"[^>]*>/g) || [];
        ok('the dashboard panels were found (' + panels.length + ')', panels.length >= 10);
        panels.forEach(p => {
            const id = (/\bid="([^"]+)"/.exec(p) || [])[1];
            const by = (/aria-labelledby="([^"]+)"/.exec(p) || [])[1];
            const tab = by ? (html.match(new RegExp('<button[^>]*\\bid="' + by + '"[^>]*>')) || [])[0] : null;
            ok(id + ' is named by a tab that controls it', !!tab && new RegExp('aria-controls="' + id + '"').test(tab), p);
        });
    }

    // 10. The letter on an avatar is readable on every palette colour (3:1 at least, it is
    // large text; most are well past it).
    {
        const P6 = D.loadPlugin(SRC).PP;
        const hexes = (SRC.match(/AVATAR_PALETTE\s*=\s*\[([^\]]*)\]/) || ['', ''])[1].match(/#[0-9A-Fa-f]{6}/g) || [];
        ok('the avatar palette was found (' + hexes.length + ' colours)', hexes.length >= 18);
        const lin = x => { x /= 255; return x <= 0.03928 ? x / 12.92 : Math.pow((x + 0.055) / 1.055, 2.4); };
        const lum = h => 0.2126 * lin(parseInt(h.slice(1, 3), 16)) + 0.7152 * lin(parseInt(h.slice(3, 5), 16)) + 0.0722 * lin(parseInt(h.slice(5, 7), 16));
        const ratio = (a, b) => (Math.max(lum(a), lum(b)) + 0.05) / (Math.min(lum(a), lum(b)) + 0.05);
        const gate = D.makeContent();
        P6.currentProfiles = [];
        hexes.forEach(h => {
            gate.innerHTML = '';
            const prof = { profileUserId: 'a', profileName: 'A', isMaster: false, isBonfire: false, requiresPin: false, avatarColor: h, avatarInitial: 'Z' };
            P6.beginNavigation = () => 1; P6.navIsCurrent = () => true;
            P6.isManageMode = false;
            try { P6.renderOverlayContent(gate, [prof]); } catch (e) { /* drawn or not, read below */ }
            const m = /class="profile-avatar[^"]*"\s+style="[^"]*?;\s*color:\s*(#[0-9a-fA-F]{6})/.exec(gate.innerHTML);
            const r = m ? ratio(m[1], h) : 0;
            ok(h + ': the initial is ' + (m ? m[1] : 'not set') + ', ' + r.toFixed(2) + ':1 (3 needed)', r >= 3);
        });
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
