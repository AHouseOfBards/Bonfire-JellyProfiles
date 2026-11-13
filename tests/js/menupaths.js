/*
 * Menu paths in the docs.
 *
 * Every path through Jellyfin's or Bonfire's screens is written one way: bold, with " → "
 * between the steps, the labels exactly as the screen shows them:
 *
 *   **Dashboard → Plugins → Bonfire → Security**
 *
 * The docs had drifted to five forms (backticked, "->", ">", plain text, "the X tab of Y")
 * and two homes for the admin page ("Dashboard → Bonfire" and "Dashboard → Plugins →
 * Bonfire"). The page is a plugin page with EnableInMainMenu, which Jellyfin lists under
 * Plugins, so the second is the real one. Its tabs are the buttons in
 * Web/profilesDashboard.html, and a path or "the X tab" naming one that is not there
 * (Look, Apps: both renamed) is a dead instruction.
 *
 * What fails:
 *   - an arrow ("→" or "->") inside a code span
 *   - "->" in prose
 *   - a "→" that is not inside bold
 *   - "Label > Label" in prose
 *   - "Dashboard → Bonfire" (no Plugins)
 *   - a path under Dashboard → Plugins → Bonfire whose next step is not a tab label
 *   - "the X tab" / "X tab" with a capitalized X that is not a tab label
 *
 * Fenced blocks are skipped. CHANGELOG.md is left out on purpose: published release text
 * is history.
 *
 *   node tests/js/menupaths.js [repo root]
 */
'use strict';
const fs = require('fs');
const path = require('path');

const root = path.resolve(process.argv[2] || path.join(__dirname, '..', '..'));

function walk(dir, out) {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
        const p = path.join(dir, e.name);
        if (e.isDirectory()) walk(p, out); else out.push(p);
    }
    return out;
}

const files = [];
['README.md', 'CONTRIBUTING.md', 'SECURITY.md'].forEach(function (f) {
    if (fs.existsSync(path.join(root, f))) files.push(path.join(root, f));
});
['docs', '.github', 'Web/i18n'].forEach(function (d) {
    const p = path.join(root, d);
    if (fs.existsSync(p)) walk(p, []).forEach(function (f) {
        if (/\.(md|ya?ml)$/i.test(f)) files.push(f);
    });
});

const html = fs.readFileSync(path.join(root, 'Web', 'profilesDashboard.html'), 'utf8');
const TABS = [];
html.replace(/data-i18n="admin\.tab\.(?!label)[a-z]+">([^<]+)</g, function (_, label) { TABS.push(label.trim()); });

// The problems in one file's text. The text has its fenced blocks blanked and keeps its
// newlines, so a bold span or a path may run over a line break, as it does in the docs.
function problems(text, tabs) {
    const out = [];
    const add = function (index, why) {
        out.push({ line: text.slice(0, index).split('\n').length, why: why });
    };
    let m;
    const code = /`[^`\n]*`/g;
    while ((m = code.exec(text))) {
        if (/→|->|\b[A-Z][A-Za-z]+ > [A-Z]/.test(m[0])) add(m.index, 'path in a code span: ' + m[0]);
    }
    const noCode = text.replace(code, function (s) { return s.replace(/[^\n]/g, ' '); });
    const arrow = /\S[ \t]*->[ \t]*\S/g;
    while ((m = arrow.exec(noCode))) add(m.index, 'ASCII arrow: ' + m[0]);
    const gt = /\b[A-Z][A-Za-z]+(?: [A-Z][A-Za-z]+)* > [A-Z][A-Za-z]+/g;
    while ((m = gt.exec(noCode))) add(m.index, '">" between labels: ' + m[0]);

    // Bold spans, then every arrow left outside them.
    const bold = /\*\*[^*]+?\*\*/g;
    const unbold = noCode.replace(bold, function (s) { return s.replace(/[^\n]/g, ' '); });
    const bare = /→/g;
    while ((m = bare.exec(unbold))) add(m.index, 'arrow outside bold: ' + noCode.slice(Math.max(0, m.index - 25), m.index + 25).replace(/\s+/g, ' '));

    const flat = noCode.replace(/\s+/g, ' ');
    if (/Dashboard → Bonfire/.test(flat)) add(noCode.search(/Dashboard\s*→\s*Bonfire/), 'Dashboard → Bonfire has no Plugins step');
    const under = /Dashboard\s*→\s*Plugins\s*→\s*Bonfire\s*→\s*([^*→]+?)\s*(?:→|\*\*)/g;
    while ((m = under.exec(noCode))) {
        if (tabs.indexOf(m[1]) < 0) add(m.index, 'not a tab of the admin page: ' + m[1]);
    }
    const tabWord = /\b([A-Z][a-z]+)\*{0,2} tab\b/g;
    while ((m = tabWord.exec(noCode))) {
        if (tabs.indexOf(m[1]) < 0 && m[1] !== 'The' && m[1] !== 'A' && m[1] !== 'Console') add(m.index, 'not a tab of the admin page: ' + m[1] + ' tab');
    }
    return out;
}

let pass = 0, fail = 0;
if (TABS.length < 8) { fail++; console.log('FAIL read only ' + TABS.length + ' tab labels from profilesDashboard.html'); }

files.forEach(function (file) {
    let fence = false;
    const text = fs.readFileSync(file, 'utf8').split(/\r?\n/).map(function (line) {
        if (/^\s*```/.test(line)) { fence = !fence; return ''; }
        return fence ? '' : line;
    }).join('\n');
    const found = problems(text, TABS);
    if (found.length) {
        fail += found.length;
        found.forEach(function (p) { console.log('FAIL ' + path.relative(root, file) + ':' + p.line + '  ' + p.why); });
    } else pass++;
});

// The detector must be able to go red: lines it has to catch and lines it has to leave.
const CATCH = ['Open `Dashboard -> Plugins -> Bonfire`.', 'Open Dashboard -> Users.', 'Open Dashboard → Users.',
    'Open **Dashboard > Users** now.', 'Open Dashboard > Users now.', 'Open **Dashboard → Bonfire → Status**.',
    'Open **Dashboard → Plugins → Bonfire → Look**.', 'Open **Dashboard → Plugins → Bonfire → Apps**.',
    'On the Look tab, tick it.', 'Open `Dashboard → Users`.'];
const LEAVE = ['Open **Dashboard → Plugins → Bonfire → Clients**.', 'Open **Dashboard → Users**.',
    'On the Status tab, tick it.', 'Run `a > b.txt` in a shell.', 'Open **Dashboard → Users →**\nthe user **→ Authentication provider**.'];
CATCH.forEach(function (s) {
    if (problems(s, TABS).length) pass++; else { fail++; console.log('FAIL detector missed: ' + s); }
});
LEAVE.forEach(function (s) {
    if (!problems(s, TABS).length) pass++; else { fail++; console.log('FAIL detector flagged: ' + s); }
});

console.log(fail ? fail + ' failed' : 'ok: ' + files.length + ' docs read, tabs: ' + TABS.join(', '));
process.exit(fail ? 1 : 0);
