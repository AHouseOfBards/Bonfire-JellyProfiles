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

// A see-through logo in one colour, as a server might hold one: a wordmark-like block of
// bars on a clear ground, about 40% of it painted, with soft (half-alpha) edges. Built here
// so no binary sits in the repo. Served for logo ids made of one repeated hex pair.
function clearLogoPng(rgb) {
    const zlib = require('zlib');
    // Drawn at four times 160x60, so the stylesheet's height cap sizes it, not its own pixels.
    const W = 640, H = 240;
    const raw = Buffer.alloc((W * 4 + 1) * H);
    const paint = (X, Y) => { const x = X >> 2, y = Y >> 2; return paint1(x, y); };
    const paint1 = (x, y) => (y >= 12 && y < 48) && ((x >= 8 && x < 40) || (x >= 52 && x < 64) || (x >= 76 && x < 108) || (x >= 120 && x < 152)) && !(y >= 24 && y < 36 && x >= 16 && x < 32);
    for (let y = 0; y < H; y++) {
        raw[y * (W * 4 + 1)] = 0;
        for (let x = 0; x < W; x++) {
            const o = y * (W * 4 + 1) + 1 + x * 4;
            const a = paint(x, y) ? 255 : (paint(x - 1, y) || paint(x + 1, y) || paint(x, y - 1) || paint(x, y + 1)) ? 128 : 0;
            raw[o] = rgb[0]; raw[o + 1] = rgb[1]; raw[o + 2] = rgb[2]; raw[o + 3] = a;
        }
    }
    const crcTable = [];
    for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; crcTable[n] = c >>> 0; }
    const crc = b => { let c = 0xffffffff; for (const x of b) c = crcTable[(c ^ x) & 255] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
    const chunk = (type, data) => {
        const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
        const td = Buffer.concat([Buffer.from(type, 'ascii'), data]);
        const c = Buffer.alloc(4); c.writeUInt32BE(crc(td));
        return Buffer.concat([len, td, c]);
    };
    const ihdr = Buffer.alloc(13);
    ihdr.writeUInt32BE(W, 0); ihdr.writeUInt32BE(H, 4); ihdr[8] = 8; ihdr[9] = 6;
    return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr),
        chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}

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
            // opts.dashboard: another checkout's copy, so a harness can be pointed at the old page.
            const plugin = fs.readFileSync(opts.dashboard || path.join(ROOT, 'Web', 'profilesDashboard.html'), 'utf8');
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
        // The see-through logos: "ff"... is white, "11"... near-black, "e8"... the accent orange.
        const clear = /^\/plugins\/profiles\/themes\/logo\/(([0-9a-f]{2})\2{15})\.png$/.exec(url.pathname);
        if (clear) {
            const v = parseInt(clear[2], 16);
            return send(200, 'image/png', clearLogoPng(v === 0xe8 ? [232, 120, 48] : [v, v, v]));
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
