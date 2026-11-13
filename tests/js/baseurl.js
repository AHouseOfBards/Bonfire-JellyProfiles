/*
 * A Jellyfin Base URL (Dashboard, Networking, e.g. /jellyfin) is supported, so no address
 * the plugin builds in a browser may be root-relative.
 *
 * The server hands back the plugin's own picture addresses as "/plugins/profiles/..." and
 * stores them that way. Under a Base URL the browser resolves that against the origin, not
 * the Base URL, so the request lands outside Jellyfin and every avatar-library tile and
 * every picture in the missing-pictures check came up as missing. ApiClient.getUrl knows
 * the Base URL; profiles.js routes through it with pluginUrl(), and the dashboard did not.
 *
 * Two kinds of check, because they fail differently:
 *
 *   - Behaviour. The real render functions run against a getUrl that adds "/jellyfin/", and
 *     what they put in the page must start with it. This is the question that matters.
 *   - A sweep, so the next root-relative address goes red when it is written. It reads
 *     every line that makes an address (an img src, a background image) or spells a plugin
 *     path in a string, and fails any that is neither routed through getUrl/pluginUrl nor
 *     listed below with the reason it is safe. A listed line that disappears fails too, so
 *     the list cannot rot into permission.
 *
 * Point it at older files to watch it fail (argv[2] client script, argv[3] dashboard):
 *
 *     node tests/js/baseurl.js /old/profiles.js /old/profilesDashboard.html
 */
'use strict';

const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name); }
}

const PREFIX = '/jellyfin/';
const dash = L.readDashboard();
const script = L.dashboardScript(dash);

// ── Behaviour: the dashboard ─────────────────────────────────────────────────

console.log('\n── The dashboard under a Base URL ──────────────────────────────');

const flush = () => new Promise(resolve => setTimeout(resolve, 0));

function optional(name) {
    try { return L.extractFunction(script, name); } catch (e) { return ''; }
}

(async () => {
    const thumbs = [
        '/plugins/profiles/avatars/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa?size=thumb',
        '/plugins/profiles/avatars/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
    ];
    const stored = '/plugins/profiles/image/11111111111111111111111111111111?v=1';
    const remote = 'https://example.com/me.png';

    const ApiClient = {
        getUrl: (p, q) => PREFIX + p + (q ? '?' + Object.keys(q).map(k => k + '=' + q[k]).join('&') : ''),
        ajax: opts => Promise.resolve(
            /\/form-options$/.test(opts.url)
                ? { avatars: { avatars: thumbs.map((u, i) => ({ id: 'id' + i, displayName: 'A' + i, thumbUrl: u, isStarter: false })) } }
                : {
                    masterUsers: [{ profileName: 'Mum', profileImage: stored }],
                    subProfiles: [{ profileName: 'Kid', profileImage: remote }],
                }),
    };

    const probes = [];
    function ImageProbe() {
        probes.push(this);
        let src = '';
        Object.defineProperty(this, 'src', { get: () => src, set: v => { src = v; if (this.onload) this.onload(); } });
    }

    const grid = { innerHTML: '', querySelectorAll: () => [] };
    const page = { querySelector: s => (s === '#avatarLibraryGrid' ? grid : null) };
    const healthPage = {};
    let reported = null;

    const names = ['escapeHtml', 'namedAria', 'field', 'loadAvatarLibrary', 'loadAvatarHealth'];
    const body = L.dashboardI18n(script)
        + optional('pluginAsset') + '\n'
        + names.map(n => L.extractFunction(script, n)).join('\n')
        + '\nreturn { loadAvatarLibrary, loadAvatarHealth };';
    const renderAvatarHealth = (p, missing) => { reported = missing; };
    // Declared by name so the page's own call resolves to it.
    const run = new Function('ApiClient', 'Image', 'renderAvatarHealth', 'deleteAvatar', 'console',
        body);
    const fns = run(ApiClient, ImageProbe, renderAvatarHealth, () => {}, { error() {}, log() {} });

    fns.loadAvatarLibrary(page);
    await flush();
    const srcs = (grid.innerHTML.match(/<img src="([^"]*)"/g) || []).map(s => s.slice(10, -1));
    ok('the library drew a tile per picture', srcs.length === thumbs.length);
    srcs.forEach((s, i) => ok('tile ' + (i + 1) + ' starts with the Base URL (got ' + s + ')', s.indexOf(PREFIX + 'plugins/profiles/avatars/') === 0));
    ok('the size parameter survives', srcs[0] && srcs[0].indexOf('size=thumb') !== -1);

    fns.loadAvatarHealth(healthPage);
    await flush();
    ok('the missing-pictures check probed the stored picture', probes.length === 1);
    ok('and the probe starts with the Base URL (got ' + (probes[0] && probes[0].src) + ')',
        probes.length === 1 && probes[0].src.indexOf(PREFIX + 'plugins/profiles/image/') === 0);
    ok('the probe still asks for the small rendering', probes.length === 1 && /[?&]size=thumb$/.test(probes[0].src));
    ok('a remote picture is not probed at all', probes.every(p => p.src.indexOf('example.com') === -1));
    ok('nothing is reported missing when the pictures load', Array.isArray(reported) && reported.length === 0);

    // ── The sweep ─────────────────────────────────────────────────────────────

    console.log('\n── Every address the plugin builds goes through getUrl ─────────');

    const sources = [
        { name: 'Web/profilesDashboard.html', text: script },
        { name: 'Web/profiles.js', text: L.readProfiles() },
    ];

    // A line that spells an API path as a string, or makes an address a browser will load.
    const PLUGIN_PATH = /['"`]\/plugins\/profiles\//;
    const MAKES_ADDRESS = /\.src\s*=|\bsrc=\\?["']|\bsrc="\$\{|backgroundImage\s*=|background-image\s*:\s*url\(|\burl\(\\?"/;
    // Routed, or only comparing (a prefix test cannot build a wrong address).
    const ROUTES = /pluginUrl\(|getUrl\(|urlFor\(|pluginAsset\(/;
    const ROUTED = /pluginUrl\(|getUrl\(|urlFor\(|pluginAsset\(|startsWith\(|indexOf\(|\.test\(|\.match\(/;

    // Lines that make an address from something already resolved or not from the server.
    // [file, text the line contains, why it is safe]. Changing the line re-asks.
    const SAFE = [
        ['Web/profilesDashboard.html', 'img.src = event.target.result;', 'a data: URL from FileReader'],
        ['Web/profiles.js', 'img.src = event.target.result;', 'a data: URL from FileReader'],
        ['Web/profiles.js', 'img.src = resolved;', 'resolved by pluginUrl() two lines up'],
        ['Web/profiles.js', '`<img src="${resolved}" alt="" onerror="this.remove()" ` +', 'resolved by safeImageSrc, which routes through pluginUrl()'],
        ['Web/profiles.js', "thumb.style.backgroundImage = preview ? (\"url(\\\"\" + preview + \"\\\")\") : \"\";", 'preview comes from _libraryArtUrl, which routes through pluginUrl()'],
        ['Web/profiles.js', "['--jpf-deco', patternImage ? 'url(\"' + patternImage + '\")' : 'none'],", 'patternImage comes from patternPicture(): an inline data: URL from the theme table, or pluginUrl() for an uploaded file'],
        ['Web/profiles.js', "vars.push(['--jpf-deco2', 'url(\"' + pattern2Image + '\")'],", 'pattern2Image comes from patternPicture(), the same as patternImage: an inline data: URL or pluginUrl()'],
        ['Web/profiles.js', "const chevron = 'url(\"data:image/svg+xml,", 'an inline SVG data: URL drawn in the theme text colour (R-1269)'],
        ['Web/profiles.js', "src: url(\"' + url + '\") format(\"woff2\");", 'url is urlFor(), which is pluginUrl()'],
        ['Web/profiles.js', "return 'linear-gradient(' + veil + ', ' + veil + '), url(\"' + url + '\") center / cover no-repeat, ' + gateBg;", 'url is pluginUrl(), on the line above'],
        ['Web/profilesDashboard.html', "src: url(\"' + url + '\") format(\"", 'url is themeFontUrl(), which is ApiClient.getUrl()'],
        ['Web/profiles.js', '<img src="${safeImageSrc(a.thumbUrl)}"', 'safeImageSrc routes through pluginUrl()'],
        ['Web/profiles.js', "css += selector + '{background-image:url(\"' + url + '\")!important;' +", 'url is _libraryArtUrl(), which routes through pluginUrl()'],
    ];
    const used = new Set();

    for (const src of sources) {
        const lines = src.text.split('\n');
        lines.forEach((raw, i) => {
            const line = raw.trim();
            if (line.startsWith('//') || line.startsWith('*')) return;
            const spells = PLUGIN_PATH.test(line);
            const makes = MAKES_ADDRESS.test(line);
            if (!spells && !makes) return;
            // A line that makes an address must route it; one that only spells a path may
            // also be a comparison.
            if (makes ? ROUTES.test(line) : ROUTED.test(line)) return;
            const hit = SAFE.findIndex(s => s[0] === src.name && line.indexOf(s[1]) !== -1);
            if (hit !== -1) { used.add(hit); return; }
            ok(src.name + ':' + (i + 1) + ' is routed through getUrl or pluginUrl: ' + line.slice(0, 90), false);
        });
    }
    SAFE.forEach((s, i) => ok('the allowed line still exists: ' + s[1].slice(0, 70), used.has(i)));
    ok('the sweep ran over both files', sources.every(s => s.text.length > 1000));

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    fails.forEach(f => console.log('  FAIL  ' + f));
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.error(e); process.exit(2); });
