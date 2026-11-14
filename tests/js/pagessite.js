#!/usr/bin/env node
/*
 * The docs site and the update feed share one GitHub Pages site.
 *
 * Pages builds the root of main with Jekyll, and that build also serves manifest.json
 * (polled by every install) and llms.txt. The site's _config.yml opened three ways to
 * break the feed without failing any plugin test:
 *
 *   - manifest.json in `exclude`: the site builds green and every install gets 404.
 *   - front matter in manifest.json or llms.txt: Jekyll renders a page, not the file.
 *   - Liquid ({{ or {%) in any Markdown page, code blocks included: the build fails and
 *     the feed stays on the last good build.
 *
 * Also a quiet one: a doc renamed or added without a defaults entry drops out of the
 * navigation, or an entry outlives its path.
 *
 * Usage: node tests/js/pagessite.js [path/to/_config.yml]
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '  — ' + detail : '')); }
}

const CONFIG_PATH = process.argv[2] || path.join(L.ROOT, '_config.yml');
const CONFIG = fs.existsSync(CONFIG_PATH) ? fs.readFileSync(CONFIG_PATH, 'utf8').replace(/\r\n/g, '\n') : '';
ok('the site has a _config.yml', CONFIG.length > 0, CONFIG_PATH);

// The block under a top-level key, as lines.
function block(key) {
    const m = new RegExp('^' + key + ':\\s*\\n((?:[ \\t]+.*\\n|\\s*\\n)*)', 'm').exec(CONFIG + '\n');
    return m ? m[1] : '';
}
const excluded = block('exclude').split('\n').map(l => (/^\s*-\s*(.+?)\s*$/.exec(l) || [])[1]).filter(Boolean)
    .map(s => s.replace(/^["']|["']$/g, ''));

console.log('\n── The update feed survives the docs site ─────────────────────');
// What must be served as committed. An exclude entry covers a path if it names it,
// a folder above it, or a matching pattern.
const MUST_SERVE = ['manifest.json', 'llms.txt', 'images/icon.jpg', 'README.md', 'docs/'];
const covers = (entry, p) => {
    const e = entry.replace(/\/$/, '');
    if (p === e || p.startsWith(e + '/')) return true;
    if (/[*?]/.test(e)) {
        const re = new RegExp('^' + e.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '[^/]*').replace(/\?/g, '.') + '(/|$)');
        return re.test(p);
    }
    return false;
};
MUST_SERVE.forEach(p => {
    const by = excluded.filter(e => covers(e, p));
    ok(p + ' is not excluded from the site' + (by.length ? ' (by ' + by.join(', ') + ')' : ''), by.length === 0);
});
['manifest.json', 'llms.txt'].forEach(f => {
    const head = fs.readFileSync(path.join(L.ROOT, f), 'utf8').slice(0, 3);
    ok(f + ' has no front matter, so Jekyll copies it byte for byte', head !== '---');
});
ok('the theme is pinned to a release, so a theme update cannot change the build under us',
   /^remote_theme:\s*\S+@v\d+\.\d+\.\d+\s*$/m.test(CONFIG));

console.log('\n── Every Markdown page builds ─────────────────────────────────');
// Markdown files Jekyll turns into pages: tracked, not under a dot or underscore folder,
// not excluded.
function walk(dir, out) {
    fs.readdirSync(dir, { withFileTypes: true }).forEach(d => {
        if (d.name.startsWith('.') || d.name.startsWith('_') || d.name === 'node_modules') return;
        const full = path.join(dir, d.name);
        const rel = path.relative(L.ROOT, full).split(path.sep).join('/');
        if (excluded.some(e => covers(e, rel))) return;
        if (d.isDirectory()) walk(full, out);
        else if (/\.md$/i.test(d.name)) out.push(rel);
    });
    return out;
}
const pages = walk(L.ROOT, []);
ok('there are pages to build (' + pages.length + ')', pages.length > 0);
pages.forEach(p => {
    const text = fs.readFileSync(path.join(L.ROOT, p), 'utf8');
    const lines = text.split('\n');
    const bad = [];
    lines.forEach((l, i) => { if (/\{\{|\{%/.test(l)) bad.push(i + 1); });
    ok(p + ' has no Liquid, which would fail the whole Pages build' + (bad.length ? ' (line ' + bad.join(', ') + ')' : ''),
       bad.length === 0);
});

console.log('\n── Source is not served, and what pages link to is ────────────');
// Tracked C# files and projects are source, not pages. New folders (Mail/, Campground.Shared/)
// were being published.
const tracked = require('child_process').execFileSync('git', ['ls-files'], { cwd: L.ROOT, encoding: 'utf8' })
    .split('\n').filter(Boolean);
const servedSource = tracked.filter(p => /\.(cs|csproj|proj|sln)$/i.test(p) && !excluded.some(e => covers(e, p)));
const servedDirs = Array.from(new Set(servedSource.map(p => p.split('/')[0])));
ok('no C# source is on the site' + (servedDirs.length ? ' (served: ' + servedDirs.join(', ') + ')' : ''),
   servedSource.length === 0);
// A relative link to a non-Markdown file is served as that file, so excluding its folder
// 404s it on the site while it works on GitHub.
const deadLinks = [];
pages.forEach(p => {
    const text = fs.readFileSync(path.join(L.ROOT, p), 'utf8');
    text.replace(/\]\(([^)\s#]+)(?:#[^)]*)?\)/g, (m, href) => {
        if (/^[a-z]+:|^\/|\.md$/i.test(href)) return m;
        const target = path.posix.normalize(path.posix.join(path.posix.dirname(p), href)).replace(/\/$/, '');
        if (excluded.some(e => covers(e, target))) deadLinks.push(p + ' -> ' + href);
        return m;
    });
});
ok('every file a page links to is served' + (deadLinks.length ? ' (excluded: ' + deadLinks.join('; ') + ')' : ''),
   deadLinks.length === 0);

console.log('\n── Every page is in the navigation, and nothing else is ───────');
const scoped = [];
block('defaults').replace(/path:\s*(\S+)/g, (m, p) => { scoped.push(p.replace(/^["']|["']$/g, '')); return m; });
pages.forEach(p => ok(p + ' has a title and a place in the navigation', scoped.indexOf(p) !== -1));
scoped.filter(p => p).forEach(p => ok('the navigation entry for ' + p + ' is a page that exists', pages.indexOf(p) !== -1));

console.log('');
if (fails.length) {
    fails.forEach(f => console.log('   - ' + f));
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
