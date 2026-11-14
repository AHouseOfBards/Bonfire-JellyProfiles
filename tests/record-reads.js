/*
 * Preloaded into a harness (node --require, via NODE_OPTIONS) by `tests/run.sh --record`.
 * Observes which repository files the process reads and writes them, one per line, to
 * $BONFIRE_RECORD_DIR when the process exits. tests/depends.js folds those into
 * tests/depends.txt, which `tests/run.sh --changed` uses to pick harnesses.
 *
 * Lines written:
 *   F <path>   a file read, opened for reading, stat'ed or tested for existence
 *   D <path>/  a directory listed (a new file in it is a change this harness can see)
 *   *          the process started something this cannot watch (bash, git, dotnet...), so
 *              the harness cannot be narrowed and always runs
 *
 * Paths are relative to the repository root with forward slashes. Child Node processes
 * get the same preload through NODE_OPTIONS, including when a harness passes its own env.
 * Chrome is allowed: it only sees what the fixture server in this process serves, and that
 * is read through fs here.
 *
 * Plain Node 18+ and no dependencies; it must not throw, whatever the harness does.
 */
'use strict';

const fs = require('fs');
const path = require('path');
const cp = require('child_process');

const DIR = process.env.BONFIRE_RECORD_DIR;
const LABEL = process.env.BONFIRE_RECORD_LABEL;
if (DIR && LABEL) {
    const ROOT = path.resolve(__dirname, '..');
    const seen = new Set();
    const writeFile = fs.appendFileSync;
    const IGNORED = /^(\.git|node_modules)(\/|$)|(^|\/)(bin|obj|node_modules)(\/|$)/;

    const rel = p => {
        try {
            if (p instanceof URL) p = require('url').fileURLToPath(p);
            if (Buffer.isBuffer(p)) p = p.toString();
            if (typeof p !== 'string' || !p) return null;
            const r = path.relative(ROOT, path.resolve(p)).split(path.sep).join('/');
            if (!r || r.startsWith('..') || path.isAbsolute(r) || IGNORED.test(r)) return null;
            return r;
        } catch (e) { return null; }
    };
    const file = p => { const r = rel(p); if (r) seen.add('F ' + r); };
    const dir = p => { const r = rel(p); if (r) seen.add('D ' + r + '/'); };

    // Wrap fs.<name>, calling note(args) first. Never lets a recording error reach the harness.
    function wrap(obj, name, note) {
        const orig = obj[name];
        if (typeof orig !== 'function') return;
        obj[name] = function () { try { note(arguments); } catch (e) { /* ignore */ } return orig.apply(this, arguments); };
    }
    const readsOnly = a => { const f = a[1]; return f === undefined || f === null || f === 'r' || f === 'rs' || f === 0 || typeof f === 'object'; };
    for (const n of ['readFileSync', 'readFile', 'createReadStream', 'existsSync', 'statSync', 'lstatSync', 'accessSync', 'exists', 'stat', 'lstat', 'access', 'readlinkSync', 'realpathSync'])
        wrap(fs, n, a => file(a[0]));
    for (const n of ['openSync', 'open']) wrap(fs, n, a => { if (readsOnly(a)) file(a[0]); });
    for (const n of ['readdirSync', 'readdir', 'opendirSync', 'opendir']) wrap(fs, n, a => dir(a[0]));
    for (const n of ['readFile', 'stat', 'lstat', 'access']) wrap(fs.promises, n, a => file(a[0]));
    wrap(fs.promises, 'readdir', a => dir(a[0]));
    wrap(fs.promises, 'open', a => { if (readsOnly(a)) file(a[0]); });
    wrap(fs.promises, 'opendir', a => dir(a[0]));

    // Children. Node and Chrome are fine; anything else is opaque.
    const hook = process.env.NODE_OPTIONS || '';
    const mine = '--require ' + JSON.stringify(__filename.split(path.sep).join('/'));
    const isNode = c => { const b = path.basename(String(c)).toLowerCase().replace(/\.exe$/, ''); return b === 'node' || String(c) === process.execPath; };
    const isChrome = c => /(chrome|chromium|msedge)/i.test(path.basename(String(c)));
    const first = s => String(s).trim().replace(/^"([^"]*)".*$/, '$1').split(/\s+/)[0];
    function withEnv(args) {
        const out = Array.prototype.slice.call(args);
        let i = -1;
        for (let k = out.length - 1; k >= 1; k--) {
            const v = out[k];
            if (v && typeof v === 'object' && !Array.isArray(v)) { i = k; break; }
        }
        if (i < 0) return out;               // no options: the environment is inherited
        const o = Object.assign({}, out[i]);
        const env = Object.assign({}, o.env || process.env);
        const cur = env.NODE_OPTIONS || '';
        if (cur.indexOf(__filename.split(path.sep).join('/')) < 0) env.NODE_OPTIONS = (cur + ' ' + mine).trim();
        // A harness that runs another recording itself (tests/js/changedsafe.js) names its own.
        if (!env.BONFIRE_RECORD_DIR) env.BONFIRE_RECORD_DIR = DIR;
        if (!env.BONFIRE_RECORD_LABEL) env.BONFIRE_RECORD_LABEL = LABEL;
        o.env = env; out[i] = o;
        return out;
    }
    const spawnLike = ['spawn', 'spawnSync', 'execFile', 'execFileSync', 'fork'];
    const shellLike = ['exec', 'execSync'];
    for (const n of spawnLike.concat(shellLike)) {
        const orig = cp[n];
        if (typeof orig !== 'function') continue;
        cp[n] = function (cmd) {
            let args = arguments;
            try {
                const c = shellLike.indexOf(n) >= 0 ? first(cmd) : cmd;
                if (n === 'fork' || isNode(c)) args = withEnv(arguments);
                else if (!isChrome(c)) seen.add('*');
            } catch (e) { /* ignore */ }
            return orig.apply(this, args);
        };
    }

    process.on('exit', () => {
        try {
            file(process.argv[1]);
            const name = LABEL.replace(/\//g, '__') + '.' + process.pid + '.txt';
            writeFile(path.join(DIR, name), Array.from(seen).sort().join('\n') + '\n');
        } catch (e) { /* ignore */ }
    });
}
