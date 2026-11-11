// "Copy rules from" on a profile's Edit form (2.0).
//
// The route is checked in tests/cs/copyrules; this is the screen that calls it. The control
// has to be there, under the closed More fold with the rest of the rarely-used settings; it
// has to draw all of its words through t(); it must list only the household's own other
// profiles, and offer nothing when there are none; and a confirmed choice must send the two
// ids and the main PIN to the one route.
//
//   node tests/js/copyrules.js [path/to/profiles.js]
//
// Against the build before the control existed (5b126c9) every check fails.

'use strict';
const L = require('./_lib');
const D = require('./_stubdom');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const SRC = L.readProfiles();

(async () => {
    const env = D.loadPlugin(SRC);
    const { PP, content, posts, settle } = env;
    PP.beginNavigation = () => 1;
    PP.navIsCurrent = () => true;
    PP.readMasterState = () => ({ masterUserId: 'main', token: 'tok' });
    let refreshed = 0;
    PP.fetchAndRenderProfiles = () => { refreshed++; };
    const alerts = [];
    PP.showAlert = (title, text) => { alerts.push(text); };
    const confirms = [];
    PP.showConfirmDialog = (title, message, onConfirm) => { confirms.push({ title, message }); onConfirm(); };
    PP.masterPin = '2468';
    PP._themeSettings = { theme: 'bonfire', defaultTheme: 'bonfire', allow: true };
    const kid = { profileUserId: 'kid', profileName: 'Kid', isMaster: false, isBonfire: false, avatarInitial: 'K', avatarColor: '#E50914' };
    const sib = { profileUserId: 'sib', profileName: 'Sib <b>', isMaster: false, isBonfire: false };
    const main = { profileUserId: 'main', profileName: 'Bard', isMaster: true, isBonfire: false };
    const linked = { profileUserId: 'lnk', profileName: 'Added', isMaster: false, isBonfire: true, isLinked: true };
    const otherMain = { profileUserId: 'om', profileName: 'Their main', isMaster: true, isBonfire: true };
    PP.fetchSharedFormData = () => Promise.resolve({
        // No libraries: the stub has no DOM for the artwork rows, and a throw there, before the
        // wiring under test, is swallowed by the form's own catch and looks like a dead button.
        libraries: [],
        devices: [], libraryTags: [], avatarLibrary: { allowCustomUploads: true, avatars: [] }, ratings: null
    });
    let status = 200;
    env.setRespond((url) => {
        if (/Users\//.test(url)) return { status: 200, body: { Policy: {} } };
        if (/copy-rules/.test(url)) return { status: status, body: status === 200 ? {} : 'Only the main account can do that.' };
        return { status: 200, body: {} };
    });

    console.log('\n-- The control ------------------------------------------------');
    PP.currentProfiles = [main, kid, sib, linked, otherMain];
    PP.showEditProfileModal(kid); await settle();
    const html = content.innerHTML;
    const fold = (/<details\b[^>]*class="jpf-more[^"]*"[^>]*>[\s\S]*?<\/details>/.exec(html) || [''])[0];
    ok('the form has a "Copy rules from" list and a Copy button', /id="edit-copy-from"/.test(html) && /id="edit-copy-btn"/.test(html));
    ok('both sit under the More fold, not in the default view', /id="edit-copy-from"/.test(fold) && /id="edit-copy-btn"/.test(fold));
    ok('the fold starts closed', !/\bopen\b/.test((/<details\b[^>]*>/.exec(fold) || [''])[0].replace(/"[^"]*"/g, '')));
    const sel = (/<select id="edit-copy-from">[\s\S]*?<\/select>/.exec(html) || [''])[0];
    const values = (sel.match(/value="([^"]*)"/g) || []).map(v => v.slice(7, -1));
    ok('it lists the household\'s other profiles and nothing else (not itself, the main account, an added account or another household)',
        values.join(',') === ',sib', values.join(','));
    ok('a name is escaped', sel.indexOf('Sib &lt;b&gt;') !== -1 && sel.indexOf('<b>') === -1, sel);
    ok('the button starts disabled', /id="edit-copy-btn"[^>]*\bdisabled\b/.test(html));

    console.log('\n-- Its words --------------------------------------------------');
    // Through t(): every word on the control is a key, none a literal. The function's own
    // source has no text between tags once its ${...} are taken out.
    const fnStart = SRC.indexOf('renderCopyRulesSection: function');
    const fnEnd = SRC.indexOf('bindCopyRules: function');
    const fn = fnStart === -1 ? '' : SRC.slice(fnStart, fnEnd === -1 ? fnStart + 2000 : fnEnd);
    let bare = fn.replace(/\$\{(?:[^{}]|\{[^{}]*\})*\}/g, '').replace(/\/\/\/.*$/gm, '');
    const literal = (bare.match(/>[^<>\n]*[A-Za-z][^<>\n]*</g) || []);
    ok('no word between tags that is not a key', fn !== '' && literal.length === 0, literal.join(' | '));
    ['copyTitle', 'copyFrom', 'copyChoose', 'copyHint', 'copyButton'].forEach(k => {
        ok('it uses t(\'profileForm.' + k + '\')', fn.indexOf("t('profileForm." + k + "')") !== -1);
    });
    ok('and the English is in EN_STRINGS', /'profileForm\.copyConfirm':/.test(SRC) && /'profileForm\.copyTitle':/.test(SRC));
    ok('the heading reads "Copy rules from"', /<label for="edit-copy-from">Copy rules from<\/label>/.test(html));

    console.log('\n-- When it is not drawn ---------------------------------------');
    PP.currentProfiles = [main, kid];
    PP.showEditProfileModal(kid); await settle();
    ok('a profile with no sibling gets no control', !/edit-copy-from/.test(content.innerHTML));
    PP.currentProfiles = [main, kid, sib];
    PP.showEditProfileModal(main); await settle();
    ok('the main account\'s own form gets none', !/edit-copy-from/.test(content.innerHTML));

    console.log('\n-- Using it ---------------------------------------------------');
    PP.currentProfiles = [main, kid, sib];
    PP.showEditProfileModal(kid); await settle();
    const select = content.querySelector('#edit-copy-from');
    const btn = content.querySelector('#edit-copy-btn');
    ok('the nodes are found', !!select && !!btn);
    if (select && btn) {
        posts.length = 0;
        btn.click();
        ok('nothing chosen: nothing asked, nothing sent', confirms.length === 0 && posts.length === 0);
        select.value = 'sib';
        select.fire('change');
        ok('choosing a profile enables the button', btn.disabled === false);
        btn.click();
        await settle();
        ok('it asks first, naming both profiles', confirms.length === 1 && /Kid/.test(confirms[0].message) && /Sib &lt;b&gt;/.test(confirms[0].message), JSON.stringify(confirms));
        const sent = posts.filter(p => /copy-rules/.test(p.url));
        ok('and sends one request to copy-rules', sent.length === 1, posts.map(p => p.url).join(','));
        ok('with the source, the target and the main PIN', sent.length === 1
            && sent[0].body.sourceProfileId === 'sib' && sent[0].body.targetProfileId === 'kid' && sent[0].body.masterPin === '2468',
            JSON.stringify(sent[0] && sent[0].body));
        ok('then goes back to the profiles', refreshed >= 1);

        status = 401;
        refreshed = 0;
        select.value = 'sib';
        btn.click();
        await settle();
        ok('a refusal is shown, and the screen is not refreshed as if it had worked', alerts.length === 1 && refreshed === 0, alerts.join('|') + ' refreshed=' + refreshed);
    }

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
