#!/usr/bin/env node
/*
 * Every relative link in the repo's Markdown points at a file that exists,
 * and, when it carries a #anchor, at a heading that exists in that file.
 *
 * README and CHANGELOG linked BETA-CHANNEL.md for months after the page became
 * docs/beta-channel.md. Nothing opened the links, so nothing noticed.
 *
 * External links (http, https, mailto) are not followed; this needs no network.
 * Anchors follow GitHub's rule: lower-case, punctuation dropped, spaces to
 * hyphens, a repeated heading gets -1, -2.
 *
 * Usage: node tests/js/mdlinks.js [repo root]
 */
'use strict';
const fs = require('fs');
const path = require('path');
const L = require('./_lib');

const ROOT = path.resolve(process.argv[2] || L.ROOT);
const SKIP = new Set(['node_modules', 'bin', 'obj', '.git']);

function walk(dir, out) {
    for (const d of fs.readdirSync(dir, { withFileTypes: true })) {
        if (SKIP.has(d.name)) continue;
        if (d.name.startsWith('.') && d.name !== '.github') continue;
        const p = path.join(dir, d.name);
        if (d.isDirectory()) walk(p, out);
        else if (/\.md$/i.test(d.name)) out.push(p);
    }
    return out;
}

// Lines outside code fences, with their 1-based numbers.
function proseLines(text) {
    const out = [];
    let fence = null;
    text.split(/\r?\n/).forEach((line, i) => {
        const m = /^\s*(`{3,}|~{3,})/.exec(line);
        if (m) {
            if (!fence) fence = m[1][0];
            else if (m[1][0] === fence) fence = null;
            return;
        }
        if (!fence) out.push([i + 1, line]);
    });
    return out;
}

function slugs(text) {
    const seen = {};
    const set = new Set();
    for (const [, line] of proseLines(text)) {
        const m = /^#{1,6}\s+(.*?)\s*#*\s*$/.exec(line);
        if (!m) continue;
        let s = m[1]
            .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
            .replace(/[`*_~]/g, c => (c === '_' ? '_' : ''))
            .toLowerCase()
            .replace(/[^\p{L}\p{N}\s_-]/gu, '')
            .trim()
            .replace(/\s/g, '-');
        const n = seen[s] || 0;
        seen[s] = n + 1;
        set.add(n ? s + '-' + n : s);
    }
    return set;
}

const files = walk(ROOT, []);
const slugCache = {};
const slugsOf = f => slugCache[f] || (slugCache[f] = slugs(fs.readFileSync(f, 'utf8')));

let checked = 0;
const bad = [];
for (const file of files) {
    const rel = path.relative(ROOT, file).split(path.sep).join('/');
    for (const [no, rawLine] of proseLines(fs.readFileSync(file, 'utf8'))) {
        const line = rawLine.replace(/`[^`]*`/g, '');
        const re = /\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)/g;
        let m;
        while ((m = re.exec(line))) {
            const target = m[1];
            if (/^[a-z][a-z0-9+.-]*:/i.test(target) || target.startsWith('//')) continue;
            checked++;
            const hash = target.indexOf('#');
            const pathPart = decodeURIComponent(hash < 0 ? target : target.slice(0, hash));
            const anchor = hash < 0 ? '' : decodeURIComponent(target.slice(hash + 1));
            const dest = pathPart ? path.resolve(path.dirname(file), pathPart) : file;
            if (!fs.existsSync(dest)) {
                bad.push(rel + ':' + no + '  ' + target + '  (no such file)');
                continue;
            }
            if (anchor && /\.md$/i.test(dest) && !slugsOf(dest).has(anchor.toLowerCase())) {
                bad.push(rel + ':' + no + '  ' + target + '  (no such heading)');
            }
        }
    }
}

console.log('mdlinks: ' + files.length + ' Markdown files, ' + checked + ' relative links');
if (bad.length) {
    bad.forEach(b => console.log('  BROKEN ' + b));
    console.log('FAIL: ' + bad.length + ' broken');
    console.log((checked - bad.length) + ' passed, ' + bad.length + ' failed');
    process.exit(1);
}
// tests/run.sh fails a harness whose last line is not a totals line.
console.log(checked + ' passed, 0 failed');
