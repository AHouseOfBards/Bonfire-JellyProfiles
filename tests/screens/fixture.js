/*
 * NOT A HARNESS. The web client's switcher screens drawn with no Jellyfin server, so they
 * can be photographed: the golden screenshots and the decoration videos in docs/themes/.
 *
 * tests/run.sh globs tests/js and tests/cs, so nothing here is ever run by it. It needs a
 * real Chrome, which CI does not have, and pixel output moves between Chrome versions, so
 * a pixel gate would go red for nothing (Logan's call, 2026-11-05: a reference and a diff
 * tool, not a gate).
 *
 * What is real: the client script exactly as the server serves it (readClientBundle, so the
 * stylesheet and the theme files are spliced in the same way), the served fonts, and the
 * pattern pictures. What is not: Jellyfin. ApiClient is a stub that answers from
 * scenes.js's fixture data, and every request it cannot answer is logged, so a screen
 * drawn from a missing answer shows up as a warning rather than as a quietly wrong picture.
 *
 * init() never runs. It would start the route poll, read the URL, and ask the server who
 * is signed in. Each scene calls the methods that draw one screen, the way the gate does.
 *
 *   node tests/screens/fixture.js [port]     serve it, for a look in a browser
 */
'use strict';

const fs = require('fs');
const http = require('http');
const path = require('path');
const lib = require('../js/_lib');

const ROOT = path.resolve(__dirname, '..', '..');
const BOOT = 'ProfilesPlugin.init();';

/** The served script, with the bootstrap swapped for a handle the scenes can call. */
function bundle(src) {
    const js = lib.readClientBundle(src || fs.readFileSync(path.join(ROOT, 'Web', 'profiles.js'), 'utf8'));
    if (js.indexOf(BOOT) === -1) throw new Error('bootstrap call "' + BOOT + '" not found — has init() been renamed?');
    return js.replace(BOOT, 'window.__bonfire = ProfilesPlugin;');
}

const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.woff2': 'font/woff2', '.svg': 'image/svg+xml', '.png': 'image/png' };

function start(port, options) {
    const opts = options || {};
    const misses = [];
    const server = http.createServer((req, res) => {
        const url = new URL(req.url, 'http://fixture');
        const send = (status, type, body) => {
            res.writeHead(status, { 'Content-Type': type, 'Cache-Control': 'no-store' });
            res.end(body);
        };
        if (url.pathname === '/' || url.pathname === '/index.html') {
            return send(200, TYPES['.html'], fs.readFileSync(path.join(__dirname, 'page.html')));
        }
        if (url.pathname === '/blank') return send(200, TYPES['.html'], '<!doctype html><title>blank</title>');
        // The dashboard page, inside a stand-in for jellyfin-web's dashboard (dashboard.js).
        if (url.pathname === '/dashboard') {
            const page = fs.readFileSync(path.join(__dirname, 'dashboard.html'), 'utf8');
            const plugin = fs.readFileSync(path.join(ROOT, 'Web', 'profilesDashboard.html'), 'utf8');
            return send(200, TYPES['.html'], page.replace('{{DASHBOARD}}', () => plugin));
        }
        if (url.pathname === '/dashboard.js') {
            return send(200, TYPES['.js'], fs.readFileSync(path.join(__dirname, 'dashboard.js')));
        }
        // GET /themes, built from the files the server reads: the built-ins, no custom ones.
        if (url.pathname === '/fixture/themes.json') {
            const dir = path.join(ROOT, 'Web', 'themes');
            const themes = fs.readdirSync(dir).filter(f => /\.json$/.test(f) && f !== 'fonts.json').sort()
                .map(f => JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')));
            const fonts = (JSON.parse(fs.readFileSync(path.join(dir, 'fonts.json'), 'utf8')).fonts || [])
                .map(f => ({ family: f.family, fallback: f.fallback, files: [] }));
            return send(200, 'application/json', JSON.stringify({ schema: 1, 'default': 'bonfire', profilesCanChoose: true,
                hidden: [], themes, fonts, patterns: [], logo: { image: null, text: null, alt: null } }));
        }
        if (url.pathname === '/scenes.js') {
            return send(200, TYPES['.js'], fs.readFileSync(path.join(__dirname, 'scenes.js')));
        }
        if (url.pathname === '/bonfire.js') {
            try { return send(200, TYPES['.js'], bundle(opts.source)); } catch (e) { return send(500, 'text/plain', String(e)); }
        }
        const font = /^\/plugins\/profiles\/themes\/fonts\/([a-z0-9-]+\.woff2)$/.exec(url.pathname);
        if (font) {
            const file = path.join(ROOT, 'Web', 'themes', 'fonts', font[1]);
            if (fs.existsSync(file)) return send(200, TYPES['.woff2'], fs.readFileSync(file));
        }
        // The logo scene's picture: the plugin's own icon, standing in for a server's logo.
        if (/^\/plugins\/profiles\/themes\/logo\/[0-9a-f]{32}\.jpg$/.test(url.pathname)) {
            return send(200, 'image/jpeg', fs.readFileSync(path.join(ROOT, 'images', 'icon.jpg')));
        }
        const material =/^\/fixture\/(material-icons\.woff2)$/.exec(url.pathname);
        if (material && opts.materialIcons && fs.existsSync(opts.materialIcons)) {
            return send(200, TYPES['.woff2'], fs.readFileSync(opts.materialIcons));
        }
        misses.push(req.method + ' ' + url.pathname + url.search);
        return send(404, 'application/json', '{}');
    });
    return new Promise(resolve => server.listen(port || 0, '127.0.0.1', () => resolve({
        server, misses, port: server.address().port,
        close: () => new Promise(r => server.close(r))
    })));
}

module.exports = { start, bundle };

if (require.main === module) {
    start(+process.argv[2] || 8737).then(s => console.log('http://127.0.0.1:' + s.port + '/?theme=bonfire&scene=picker&device=tablet'));
}
