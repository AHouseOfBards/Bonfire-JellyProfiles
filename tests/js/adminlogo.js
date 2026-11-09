/*
 * The server logo control on the Look tab (under Advanced): an image, a line of text and
 * alt text, through the routes that already exist (PUT admin/themes/logo, POST and DELETE
 * admin/themes/logo/image). Nothing new on the server.
 *
 * Runs the dashboard's real functions against stubs and reads back what they send and what
 * they tell the administrator. Red against any dashboard from before it:
 *
 *     node tests/js/adminlogo.js "" /path/to/old/profilesDashboard.html
 */
'use strict';

const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

const html = L.readDashboard();
const script = L.dashboardScript(html);

console.log('\n── Where it is ─────────────────────────────────────────────────');

const look = (/<div class="jpf-tab-panel" id="jpfPanelLook"[\s\S]*?<\/div><!-- \/look -->/.exec(html) || [''])[0];
const fold = (/<details class="jpf-advanced[\s\S]*?<\/details>/.exec(look) || [''])[0];
const IDS = ['jpfLogo', 'logoChoose', 'logoRemove', 'logoFile', 'logoText', 'logoAlt', 'logoSave', 'logoResult'];
ok('every control is on the Look tab, in its Advanced fold',
    IDS.every(id => fold.indexOf('id="' + id + '"') !== -1), IDS.filter(id => fold.indexOf('id="' + id + '"') === -1).join());
ok('and none of it is outside the fold', IDS.every(id => look.split('id="' + id + '"').length === 2 && look.indexOf('id="' + id + '"') >= look.indexOf(fold)));
const logoMarkup = fold.slice(fold.indexOf('id="jpfLogo"'), fold.indexOf('The administrator\'s own themes'));
const bareWords = [];
logoMarkup.replace(/<(button|label|h2|div|span)([^>]*)>([^<]+)<\/\1>/g, (all, tag, attrs, text) => {
    if (text.trim() && !/data-i18n="admin\.logo\./.test(attrs)) bareWords.push(text.trim());
    return all;
});
ok('every word in the markup is a key', logoMarkup.length > 0 && /data-i18n="admin\.logo\.title"/.test(logoMarkup) && bareWords.length === 0, bareWords.join(' | '));
ok('the ids are in the page once each, so a key can find its element',
    IDS.every(id => html.split('id="' + id + '"').length === 2));
ok('the image control takes PNG, JPEG and WebP only', /id="logoFile" accept="image\/png,image\/jpeg,image\/webp"/.test(logoMarkup));

const have = n => new RegExp('function ' + n + '\\s*\\(').test(script);
const names = ['renderLogo', 'saveLogoText', 'uploadLogoImage', 'removeLogoImage', 'bindLogo'];
if (!names.every(have)) {
    ok('this dashboard has the logo functions', false, names.filter(n => !have(n)).join());
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}

const consts = ['LOGO_TYPES', 'LOGO_MAX_TEXT', 'LOGO_MAX_ALT'].map(n => {
    const m = new RegExp('const ' + n + ' = [^\\n]*;').exec(script);
    return m ? m[0] : '';
}).join('\n');
const source = L.dashboardI18n(script) + '\n' + consts + '\n'
    + L.extractFunction(script, 'refusalCode') + '\n' + L.extractFunction(script, 'refusalMessage') + '\n'
    + L.extractFunction(script, 'readThemeError') + '\n'
    + names.map(n => L.extractFunction(script, n)).join('\n') + '\n';

// ── A page, a server and a fetch ─────────────────────────────────────────────
function node(initial) {
    const n = { value: initial || '', attrs: {}, _hidden: false, dataset: {}, listeners: {},
        classList: { toggle: (c, on) => { if (c === 'jpf-hidden') n._hidden = !!on; }, add() {}, remove() {} },
        setAttribute: (k, v) => { n.attrs[k] = v; }, removeAttribute: k => { delete n.attrs[k]; },
        addEventListener: (type, fn) => { n.listeners[type] = fn; }, click() { if (n.listeners.click) n.listeners.click(); },
        files: null };
    return n;
}
function world(data) {
    const state = { sent: [], fetched: [], statuses: [], reloads: 0, refreshes: 0 };
    const els = {};
    ['#logoCurrent', '#logoCurrentImage', '#logoRemove', '#logoText', '#logoAlt', '#logoChoose', '#logoFile', '#logoSave']
        .forEach(s => { els[s] = node(); });
    const page = { _themes: { data: data || {} }, querySelector: s => { if (!els[s]) throw new Error('no ' + s); return els[s]; } };
    let reply = () => Promise.resolve({});
    const ApiClient = {
        getUrl: u => '/' + u,
        ajax: o => { state.sent.push({ type: o.type, url: o.url, body: o.data ? JSON.parse(o.data) : undefined }); return reply(o); }
    };
    let fetchReply = () => Promise.resolve({ ok: true, json: () => Promise.resolve({ image: '/x', width: 10, height: 10 }) });
    const fetch = (url, init) => { state.fetched.push({ url, init }); return fetchReply(url, init); };
    const api = new Function('page', 'ApiClient', 'fetch', 'showThemeStatus', 'loadThemes', 'refreshClientThemes', 'authorizationHeader',
        source + '\nreturn { ' + names.join(', ') + ', setAdminStrings: function (s) { adminStrings = s; } };')(
        page, ApiClient, fetch,
        (p, sel, msg, good) => state.statuses.push({ sel, msg, good }),
        () => { state.reloads++; }, () => { state.refreshes++; }, () => 'MediaBrowser Token="abc"');
    return { state, els, page, api,
        replyWith: fn => { reply = fn; }, fetchWith: fn => { fetchReply = fn; } };
}
const refusal = (status, code, text) => ({
    status, ok: false, headers: { get: h => (h === 'X-Bonfire-Error' ? code : null) }, text: () => Promise.resolve(text || '')
});
const settle = () => new Promise(r => setImmediate(r)).then(() => new Promise(r => setImmediate(r)));

(async function main() {
    console.log('\n── What the server holds is shown ──────────────────────────────');
    let w = world({ logo: { image: '/plugins/profiles/themes/logo/abc.png', text: 'Hearth', alt: 'The Hearth' } });
    w.api.renderLogo(w.page);
    ok('the current image is drawn', w.els['#logoCurrentImage'].attrs.src === '/plugins/profiles/themes/logo/abc.png', JSON.stringify(w.els['#logoCurrentImage'].attrs));
    ok('Remove is offered', w.els['#logoRemove']._hidden === false && w.els['#logoCurrent']._hidden === false);
    ok('and the text and alt text are filled in', w.els['#logoText'].value === 'Hearth' && w.els['#logoAlt'].value === 'The Hearth');
    w = world({ logo: { image: null, text: null, alt: null } });
    w.api.renderLogo(w.page);
    ok('with no image there is no Remove and nothing drawn', w.els['#logoRemove']._hidden === true && w.els['#logoCurrent']._hidden === true && !w.els['#logoCurrentImage'].attrs.src);

    console.log('\n── Text and alt text ───────────────────────────────────────────');
    w = world({});
    w.els['#logoText'].value = '  Hearth  ';
    w.els['#logoAlt'].value = '  The Hearth logo ';
    w.replyWith(() => Promise.resolve({}));
    w.api.saveLogoText(w.page);
    await settle();
    const put = w.state.sent[0];
    ok('saving PUTs both, trimmed, to the existing route',
        !!put && put.type === 'PUT' && put.url === '/plugins/profiles/admin/themes/logo' && put.body.text === 'Hearth' && put.body.alt === 'The Hearth logo', JSON.stringify(w.state.sent));
    ok('and says so, then reloads', w.state.statuses.length === 1 && w.state.statuses[0].good === true && w.state.statuses[0].msg === 'Logo text saved.' && w.state.reloads === 1);
    w = world({});
    w.els['#logoText'].value = '';
    w.els['#logoAlt'].value = '';
    w.api.saveLogoText(w.page);
    await settle();
    ok('empty fields are sent empty, which clears them', w.state.sent[0] && w.state.sent[0].body.text === '' && w.state.sent[0].body.alt === '');

    w = world({});
    w.els['#logoText'].value = 'x'.repeat(41);
    w.api.saveLogoText(w.page);
    ok('text over 40 characters is refused here, and nothing is sent',
        w.state.sent.length === 0 && w.state.statuses[0] && w.state.statuses[0].good === false && /40/.test(w.state.statuses[0].msg), JSON.stringify(w.state.statuses));
    w = world({});
    w.els['#logoAlt'].value = 'x'.repeat(101);
    w.api.saveLogoText(w.page);
    ok('so is alt text over 100', w.state.sent.length === 0 && w.state.statuses[0] && /100/.test(w.state.statuses[0].msg));

    w = world({});
    w.els['#logoText'].value = 'Hearth';
    w.replyWith(() => Promise.reject(refusal(400, 'logo_invalid', 'Logo text is one line of up to 40 characters.')));
    w.api.saveLogoText(w.page);
    await settle();
    // Replaced behaviour: English used to show the server's sentence; it is the code's sentence now.
    ok("the refusal is shown by its code, not swallowed",
        w.state.statuses.length === 1 && w.state.statuses[0].good === false && w.state.statuses[0].msg === 'Use one line of text, within the limit.', JSON.stringify(w.state.statuses));
    ok('and nothing is reloaded as if it had saved', w.state.reloads === 0);
    w.api.setAdminStrings({ 'admin.logo.err.invalid': 'Utilisez une seule ligne.' });
    w.api.saveLogoText(w.page);
    await settle();
    ok('a translated page says it in its own words', w.state.statuses[1] && w.state.statuses[1].msg === 'Utilisez une seule ligne.', JSON.stringify(w.state.statuses[1]));

    console.log('\n── The image ───────────────────────────────────────────────────');
    w = world({ maxLogoBytes: 1048576 });
    w.api.uploadLogoImage(w.page, { name: 'logo.gif', type: 'image/gif', size: 1000 });
    ok('a GIF is refused here', w.state.fetched.length === 0 && w.state.statuses[0] && w.state.statuses[0].good === false && /PNG, JPEG or WebP/.test(w.state.statuses[0].msg));
    w.api.uploadLogoImage(w.page, { name: 'logo.png', type: 'image/png', size: 2 * 1048576 });
    ok('so is one over 1 MB', w.state.fetched.length === 0 && /1 MB/.test(w.state.statuses[1].msg), JSON.stringify(w.state.statuses[1]));
    w.api.uploadLogoImage(w.page, null);
    ok('and choosing nothing', w.state.fetched.length === 0 && w.state.statuses.length === 3);

    const png = { name: 'logo.png', type: 'image/png', size: 5000 };
    w = world({ maxLogoBytes: 1048576 });
    w.api.uploadLogoImage(w.page, png);
    await settle();
    const post = w.state.fetched[0];
    ok('a PNG is POSTed as the body itself, to the existing route',
        !!post && post.url === '/plugins/profiles/admin/themes/logo/image' && post.init.method === 'POST' && post.init.body === png);
    ok('with its type and the admin token', !!post && post.init.headers['Content-Type'] === 'image/png' && post.init.headers.Authorization === 'MediaBrowser Token="abc"');
    ok('then says so and reloads the themes', w.state.statuses[0] && w.state.statuses[0].good === true && w.state.statuses[0].msg === 'Logo image saved.' && w.state.reloads === 1 && w.state.refreshes === 1);

    w = world({ maxLogoBytes: 1048576 });
    w.fetchWith(() => Promise.resolve(refusal(400, 'image_invalid', 'That image could not be read as a PNG, JPEG or WebP.')));
    w.api.uploadLogoImage(w.page, png);
    await settle();
    ok("the refusal of the picture is shown by its code", w.state.statuses.length === 1 && w.state.statuses[0].good === false
        && w.state.statuses[0].msg === 'Use a PNG, JPEG or WebP image up to 1 MB, 16 to 4096 pixels a side.', JSON.stringify(w.state.statuses));
    w = world({});
    w.fetchWith(() => Promise.reject(new Error('offline')));
    w.api.uploadLogoImage(w.page, png);
    await settle();
    ok('a dropped connection says the upload failed', w.state.statuses.length === 1 && w.state.statuses[0].msg === 'Couldn\'t upload the logo.', JSON.stringify(w.state.statuses));

    console.log('\n── Remove ──────────────────────────────────────────────────────');
    w = world({});
    w.api.removeLogoImage(w.page);
    await settle();
    ok('DELETEs the image at the existing route', w.state.sent[0] && w.state.sent[0].type === 'DELETE' && w.state.sent[0].url === '/plugins/profiles/admin/themes/logo/image');
    ok('and says so', w.state.statuses[0] && w.state.statuses[0].good === true && w.state.statuses[0].msg === 'Logo image removed.' && w.state.reloads === 1);
    w = world({});
    w.replyWith(() => Promise.reject(refusal(500, '', '')));
    w.api.removeLogoImage(w.page);
    await settle();
    ok('a failed removal is shown', w.state.statuses[0] && w.state.statuses[0].good === false);

    console.log('\n── Wiring ──────────────────────────────────────────────────────');
    w = world({});
    w.api.bindLogo(w.page);
    let picked = 0;
    w.els['#logoFile'].click = () => { picked++; };
    w.els['#logoChoose'].click();
    ok('Choose opens the file picker', picked === 1);
    w.els['#logoFile'].files = [png];
    w.els['#logoFile'].listeners.change();
    await settle();
    ok('picking a file uploads it at once', w.state.fetched.length === 1);
    w.els['#logoRemove'].click();
    w.els['#logoSave'].click();
    await settle();
    ok('Remove and Save text are wired', w.state.sent.some(s => s.type === 'DELETE') && w.state.sent.some(s => s.type === 'PUT'));
    w.api.bindLogo(w.page);
    ok('binding twice adds no second handler', w.els['#logoChoose'].dataset.bound === '1');
    ok('the themes load draws it', /renderLogo\(page\);/.test(script.slice(script.indexOf('function renderThemes'))));
    ok('and the page binds it with the rest of the themes', /bindLogo\(page\);/.test(script.slice(script.indexOf('function bindThemes'))));
    ok('the logo refusals have their own wording, not the theme ones',
        /logo_invalid: 'admin\.logo\.err\.invalid'/.test(script) && /image_invalid: 'admin\.logo\.err\.image'/.test(script));

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
