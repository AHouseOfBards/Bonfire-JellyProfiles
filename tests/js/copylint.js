/*
 * The writing rules, as a check.
 *
 * The owner's rule for every plugin is plain, normal wording: no taglines, no tooltips that
 * repeat their label, nothing that sounds generated (Campground-Coordination/methodology/
 * writing.md). A rule that is only written down gets forgotten the next time someone adds a
 * string, so this reads every word a person is shown and fails on the mechanical part of it.
 *
 * It reads the English only, from the three places English lives:
 *   - EN_STRINGS in Web/profiles.js (the households' screens),
 *   - Web/profilesDashboard.html (data-i18n markup plus ADMIN_EN),
 *   - Mail/EmailText.cs (email.* and notice.*).
 * Those are read the way Web/i18n/validate.js reads them, with the same patterns, and then
 * the harness proves it saw every string two ways: each catalogue's key lines match what
 * the pattern pulled out, and the counts equal what validate.js prints for the same tree.
 * An extractor that quietly skips a string would turn every rule below into a rule about
 * some of the strings.
 *
 * One named check per rule, each listing the key and the text it objects to:
 *   1. British spellings            4. old names the owner replaced
 *   2. Title Case                   5. the wording writing.md bans
 *   3. uncontracted negatives       6. a tooltip that repeats its label
 *
 * There is no list of current offenders to excuse. If a string fails, change the string.
 * The only exceptions in this file are WORDS that are not what the rule is about (a proper
 * noun is not Title Case), and each says why.
 *
 *   node tests/js/copylint.js [profiles.js [profilesDashboard.html [EmailText.cs]]]
 *
 * Without the second and third, they are looked for beside the first, so pointing it at an
 * old checkout's Web/profiles.js reads that checkout's whole copy.
 */
'use strict';

const fs = require('fs');
const path = require('path');
const cp = require('child_process');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); return; }
    fails.push(name);
    console.log('  FAIL  ' + name + (detail ? '  (' + detail + ')' : ''));
}

// ── Reading the English ───────────────────────────────────────────────────────

const profilesFile = L.profilesPath();
const webDir = path.dirname(profilesFile);
const dashboardFile = process.argv[3] || path.join(webDir, 'profilesDashboard.html');
const mailFile = process.argv[4] || path.join(webDir, '..', 'Mail', 'EmailText.cs');

// The same pattern validate.js uses: 'key': 'value' or "value", the double-quoted form being
// how any English with an apostrophe in it is written.
const KEYED = /'([^']+)':\s*(?:'((?:[^'\\]|\\.)*)'|"((?:[^"\\]|\\.)*)")/g;

function readKeyed(body) {
    const out = {};
    let m;
    KEYED.lastIndex = 0;
    while ((m = KEYED.exec(body)) !== null) {
        out[m[1]] = (m[2] !== undefined ? m[2] : m[3]).replace(/\\(['"\\])/g, '$1');
    }
    return out;
}

function between(src, start, end, what) {
    const a = src.indexOf(start);
    if (a === -1) throw new Error('cannot find ' + what);
    const b = src.indexOf(end, a);
    if (b === -1) throw new Error('cannot find the end of ' + what);
    return src.slice(a, b);
}

function decodeEntities(s) {
    return s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"')
        .replace(/&#0?39;/g, "'").replace(/&nbsp;/g, '\u00a0').replace(/&amp;/g, '&');
}

const VOID = new Set(['input', 'br', 'img', 'hr', 'meta', 'link', 'source', 'wbr']);

function tokenize(html) {
    const end = html.indexOf('<script');
    const markup = end === -1 ? html : html.slice(0, end);
    const toks = [];
    const re = /<!--[\s\S]*?-->|<(\/?)([a-zA-Z][a-zA-Z0-9-]*)((?:[^>"']|"[^"]*"|'[^']*')*)>|[^<]+/g;
    let m;
    while ((m = re.exec(markup)) !== null) {
        if (m[0].startsWith('<!--')) continue;
        if (m[2]) toks.push({ tag: m[2].toLowerCase(), close: m[1] === '/', attrs: m[3] || '', raw: m[0], at: m.index });
        else toks.push({ text: m[0], at: m.index });
    }
    return { markup, toks };
}

function attr(attrs, name) {
    const m = new RegExp('(?:^|\\s)' + name + '="([^"]*)"').exec(attrs);
    return m ? m[1] : null;
}

/** key -> English for every data-i18n element and attribute in the dashboard's markup. */
function readAdminMarkup(html) {
    const { markup, toks } = tokenize(html);
    const out = {};
    for (let i = 0; i < toks.length; i++) {
        const tk = toks[i];
        if (tk.text !== undefined || tk.close) continue;
        ['placeholder', 'aria-label'].forEach(name => {
            const key = attr(tk.attrs, 'data-i18n-' + name);
            if (key) out[key] = decodeEntities(attr(tk.attrs, name) || '');
        });
        const key = attr(tk.attrs, 'data-i18n');
        if (!key || VOID.has(tk.tag)) continue;
        let depth = 0, j = i + 1, hasTags = false;
        for (; j < toks.length; j++) {
            const x = toks[j];
            if (x.text !== undefined) continue;
            if (VOID.has(x.tag) || x.raw.endsWith('/>')) { hasTags = true; continue; }
            if (!x.close) { depth++; hasTags = true; continue; }
            if (depth === 0) break;
            depth--;
        }
        if (j >= toks.length) continue;
        const inner = markup.slice(tk.at + tk.raw.length, toks[j].at);
        out[key] = hasTags ? inner.replace(/\s+/g, ' ').trim() : decodeEntities(inner).replace(/\s+/g, ' ').trim();
    }
    return out;
}

const profilesSrc = fs.readFileSync(profilesFile, 'utf8');
const dashboardSrc = fs.readFileSync(dashboardFile, 'utf8');
const mailSrc = fs.readFileSync(mailFile, 'utf8');

const enBody = between(profilesSrc, 'const EN_STRINGS = {', '\n    };', 'EN_STRINGS');
const adminBody = between(dashboardSrc, 'const ADMIN_EN = {', '\n            };', 'ADMIN_EN');

const EN = readKeyed(enBody);
const ADMIN_SCRIPT = readKeyed(adminBody);
const ADMIN_MARKUP = readAdminMarkup(dashboardSrc);
const ADMIN = Object.assign({}, ADMIN_MARKUP, ADMIN_SCRIPT);
const MAIL = {};
{
    const re = /^[ \t]*\["([^"]+)"\]\s*=\s*"((?:[^"\\]|\\.)*)"/gm;
    let m;
    while ((m = re.exec(mailSrc)) !== null) MAIL[m[1]] = m[2].replace(/\\n/g, '\n').replace(/\\(["\\])/g, '$1');
}

// What a reader sees of a string: tags gone, entities decoded, placeholders blanked, curly
// apostrophes straightened. Rules read this, and the report prints the original.
function seen(text) {
    return decodeEntities(text.replace(/<[^>]*>/g, ' ')).replace(/\{[A-Za-z0-9_]+\}/g, ' ')
        .replace(/[\u2018\u2019]/g, "'").replace(/\s+/g, ' ').trim();
}

const ENTRIES = [];
[['EN_STRINGS', EN], ['dashboard', ADMIN], ['mail', MAIL]].forEach(pair => {
    Object.keys(pair[1]).forEach(k => ENTRIES.push({ source: pair[0], key: k, text: pair[1][k] }));
});

// ── 0. Did it read everything? ────────────────────────────────────────────────
console.log('\n── The extractor sees every string ────────────────────────────');
{
    const countLines = (body, re) => body.split('\n').filter(l => re.test(l)).length;
    const enLines = countLines(enBody, /^\s*'[^']+':/);
    const adminLines = countLines(adminBody, /^\s*'[^']+':/);
    const mailLines = countLines(mailSrc, /^[ \t]*\["[^"]+"\]\s*=/);
    ok('every EN_STRINGS key line was read', enLines === Object.keys(EN).length,
       enLines + ' key lines, ' + Object.keys(EN).length + ' read');
    ok('every ADMIN_EN key line was read', adminLines === Object.keys(ADMIN_SCRIPT).length,
       adminLines + ' key lines, ' + Object.keys(ADMIN_SCRIPT).length + ' read');
    ok('every EmailText.cs key line was read', mailLines === Object.keys(MAIL).length,
       mailLines + ' key lines, ' + Object.keys(MAIL).length + ' read');
    const stray = Object.keys(ADMIN).filter(k => k.indexOf('admin.') !== 0);
    ok('the dashboard\'s keys are all admin.*', stray.length === 0, stray.join(', '));
    ok('the three catalogues are not empty',
       Object.keys(EN).length > 100 && Object.keys(ADMIN).length > 100 && Object.keys(MAIL).length > 10);

    // validate.js prints how many keys it finds. It reads its own neighbours, so compare only when
    // all three files are the ones beside it.
    const validate = path.join(webDir, 'i18n', 'validate.js');
    const sameTree = fs.existsSync(validate)
        && path.resolve(dashboardFile) === path.resolve(webDir, 'profilesDashboard.html')
        && path.resolve(mailFile) === path.resolve(webDir, '..', 'Mail', 'EmailText.cs');
    if (!sameTree) {
        console.log('  skip  validate.js count (files are not from one tree)');
    } else {
        const r = cp.spawnSync(process.execPath, [validate], { encoding: 'utf8' });
        const line = (r.stdout || '').split('\n').filter(l => /English catalogue:/.test(l))[0] || '';
        const m = /(\d+) keys for households, (\d+) for the settings page, (\d+) for email/.exec(line);
        ok('the counts equal validate.js', !!m && +m[1] === Object.keys(EN).length
            && +m[2] === Object.keys(ADMIN).length && +m[3] === Object.keys(MAIL).length,
           'validate.js says "' + line.trim() + '"; read ' + Object.keys(EN).length + '/'
           + Object.keys(ADMIN).length + '/' + Object.keys(MAIL).length);
    }
}

// ── The rules ─────────────────────────────────────────────────────────────────

// Spelling is American. Stems that take -ize in American English: -ise forms are only
// reported for these, because "advise", "exercise" and "otherwise" are the same word on
// both sides of the Atlantic. Grown from what the tree actually had (organis-, recognis-,
// normalis-, initialis-, authoris-) plus the usual suspects.
const ISE_STEMS = ['organis', 'recognis', 'normalis', 'initialis', 'authoris', 'customis', 'personalis',
    'summaris', 'minimis', 'maximis', 'synchronis', 'realis', 'utilis', 'optimis', 'categoris', 'prioritis',
    'finalis', 'apologis', 'emphasis', 'visualis', 'localis', 'digitis', 'standardis', 'serialis', 'sanitis',
    'specialis', 'generalis', 'memoris', 'criticis', 'familiaris', 'capitalis', 'centralis', 'mobilis',
    'stabilis', 'harmonis', 'analys', 'paralys', 'catalys'];
const BRITISH = new RegExp('\\b(?:colour|favourite|cancell(?:ed|ing|ation)|behaviour|licence|grey|greys|greyed|'
    + 'centre|centred|centres|labelled|labelling|travell(?:ed|ing|er)|modell(?:ed|ing)|neighbour|honour|flavour|'
    + 'programme|theatre|fibre|ageing|whilst|learnt|judgement|enrol|enrolment|practise|metre|defence|offence)\\w*\\b|'
    + '\\b\\w*(?:' + ISE_STEMS.join('|') + ')(?:e|ed|es|ing|ation|ations|able|er|ers)\\b', 'gi');

// A proper noun is not Title Case. Each is here because it is a real name, and not because
// a string needed excusing.
const PROPER = [
    ['Bonfire', 'the plugin'],
    ['Jellyfin', 'the server'],
    ['Quick Connect', 'Jellyfin\'s own feature'],
    ['Moonfin', 'a client the proxy switcher serves'],
    ['Android TV', 'a platform'],
    ['Fire TV', 'a platform'],
    ['Apple TV', 'a platform'],
    ['Google TV', 'a platform'],
    ['Fluent Emoji', 'the artwork set\'s own name'],
    ['Home Assistant', 'a product'],
    ['Fluent UI', 'a product'],
    ['Docker', 'a product'],
];
// Short upper-case words (PIN, TV, MB, OK, URL) are acronyms, not capitalised words; the
// test below only counts a capital followed by lower-case.

function titleCaseRuns(text) {
    let s = seen(text);
    PROPER.forEach(p => { s = s.split(p[0]).join('\u0001'); });
    const toks = s.match(/\u0001|[A-Za-z][A-Za-z'\u2019]*|[^\sA-Za-z]/g) || [];
    const hits = [];
    let run = [];
    const flush = () => { if (run.length >= 2) hits.push(run.join(' ')); run = []; };
    toks.forEach(w => {
        if (w === '\u0001') return;                       // a proper noun neither starts nor ends a run
        if (/^[A-Z][a-z]+(?:'[a-z]+)?$/.test(w)) run.push(w);
        else flush();
    });
    flush();
    return hits;
}

// The owner asked for contractions everywhere, security texts included (2026-11-10). "must
// not" and "need not" are left out: "mustn't" and "needn't" read as stiffer, not plainer.
const NEGATIVES = /\b(?:could not|cannot|do not|does not|did not|is not|was not|were not|will not|would not|should not|are not|has not|have not|had not)\b/gi;
// Whether one of these is a reason to keep the long form in a legal or security text is
// the owner's call, not this file's. They are all flagged; the report lists any that sit in
// a terms, privacy or warning text so he can decide.

const OLD_NAMES = [
    // "Look" named the theme screen before it was called Theme. A verb at the start of a
    // sentence ("Look for ...") is a different word.
    ['"Look" as the theme screen', /\bLook\b(?! (?:for|at|up|in|out|into|again|closely|over|after)\b)/],
    ['"picture" for an avatar', /\bpictures?\b/i],
    ['"Viewing hours"', /\bviewing hours\b/i],
    ['"Enabled Libraries"', /\benabled libraries\b/i],
    ['"Profile sign-in"', /\bprofile sign-in\b/i],
    // Where it means the server's network. A string about the person's own home is the
    // owner's to tell apart; every one is listed.
    ['"home network"', /\bhome network\b/i],
];

// Strings where an old name is the right word, by key, each with the owner's reason. A key
// listed here is excused from that one name only, never from the other rules.
const OLD_NAME_KEYS = {
    '"picture" for an avatar': {
        // Library artwork's "Picture" mode puts an image behind a library. It isn't an
        // avatar, and the owner kept the word (2026-11-10).
        'profileForm.artworkPicture': 'library artwork mode, not an avatar',
        'profileForm.artworkExplainer': 'explains that mode',
    },
};

// writing.md's lists, as written there. Nothing here is from memory: the "What to cut"
// section's words, plus the two taglines and the button names it gives by name.
const BANNED = [
    ['"Don\'t worry"', /\bdon't worry\b/i],
    ['"Nothing is broken"', /\bnothing is broken\b/i],
    ['"worth noting"', /\bworth (?:noting|mentioning)\b/i],
    ['"Simply"', /\bsimply\b/i],
    ['"Just"', /\bjust\b/i],
    ['"Easily"', /\beasily\b/i],
    ['"Handy for"', /\bhandy for\b/i],
    ['hype word', /\b(?:seamless(?:ly)?|robust|powerful|effortless(?:ly)?|comprehensive|cutting-edge|leverag(?:e|es|ed|ing)|empower(?:s|ed|ing)?|elevat(?:e|es|ed|ing)|unlock(?:s|ed|ing)?|streamlin(?:e|es|ed|ing)|delve|delves|journey)\b/i],
    ['tagline', /\byour media, your way\b|\bseamless streaming\b/i],
    ['"Whether you\'re ..."', /\bwhether you're\b/i],
    ['"It\'s not just ..."', /\bit's not just\b/i],
    ['a button called Submit or Let\'s go', /^(?:submit|let's go)$/i],
];

function find(re, text) {
    const s = seen(text);
    const g = new RegExp(re.source, re.flags.indexOf('g') === -1 ? re.flags + 'g' : re.flags);
    const out = [];
    let m;
    while ((m = g.exec(s)) !== null) { out.push(m[0]); if (m[0] === '') g.lastIndex++; }
    return out;
}

// Each rule: name, and a function from text to the list of things wrong in it.
const RULES = [
    ['1. British spellings', t => find(BRITISH, t)],
    ['2. Title Case', t => titleCaseRuns(t)],
    ['3. uncontracted negatives', t => find(NEGATIVES, t)],
    ['4. old names the owner replaced', (t, key) => {
        const out = [];
        OLD_NAMES.forEach(o => {
            const excused = OLD_NAME_KEYS[o[0]];
            if (key && excused && excused[key]) return;
            if (find(o[1], t).length) out.push(o[0]);
        });
        return out;
    }],
    ['5. wording writing.md bans', t => {
        const out = [];
        BANNED.forEach(b => { find(b[1], t).forEach(x => out.push(b[0] + ' (' + x + ')')); });
        return out;
    }],
];

// ── 6. A tooltip that repeats its label ───────────────────────────────────────
// A title or aria-label on an element that has visible text of its own, saying that text
// again. An icon-only button has no text, and its name is needed, so it is not looked at.

function norm(s) { return seen(s).toLowerCase().replace(/[.:!?…]+$/, '').trim(); }

/// Looks up a catalogue's English for the keys named in a piece of source.
function tooltipRepeats(src, dict, fname) {
    const found = [];
    found.sites = 0;          // how many title/aria-label sites it looked at, so "none found" can be seen not to be "looked at nothing"
    const lineOf = i => src.slice(0, i).split('\n').length;
    const T = "t\\('([^']+)'";
    // (a) in markup written as a template or a concatenation:
    //     title="${t('k')}"   or   title="' + escapeHtml(t('k')) + '"   next to the text.
    const attrRe = new RegExp('\\b(title|aria-label)="(?:\\$\\{(?:escapeHtml\\()?|\' \\+ (?:escapeHtml\\()?)' + T + '[^)]*\\)', 'g');
    let m;
    while ((m = attrRe.exec(src)) !== null) {
        const label = dict[m[2]];
        if (label === undefined) continue;
        found.sites++;
        // The element's own text: from the end of its opening tag to the next closing tag.
        const gt = src.indexOf('>', m.index);
        if (gt === -1) continue;
        const lt = src.indexOf('</', gt);
        if (lt === -1 || lt - gt > 600) continue;
        // Only where the opening tag really ends here: no other tag opens before the '>'.
        const tagStart = src.lastIndexOf('<', m.index);
        if (tagStart === -1 || m.index - tagStart > 800) continue;
        let inner = src.slice(gt + 1, lt);
        if (/</.test(inner)) continue;                    // has child elements: not a plain label
        const parts = [];
        let any = false;
        inner.replace(new RegExp(T + '[^)]*\\)', 'g'), (x, k) => { any = true; parts.push(dict[k] === undefined ? '' : dict[k]); return x; });
        // Plain text in a template that is not a t() call.
        const literal = inner.replace(new RegExp(T + '[^)]*\\)', 'g'), ' ').replace(/\$\{[^}]*\}/g, ' ')
            .replace(/'\s*\+\s*|\s*\+\s*'|escapeHtml\(|[()'"`+]/g, ' ');
        if (!any || /[A-Za-z]{2}/.test(literal)) continue;
        const text = parts.join(' ');
        if (norm(text) !== '' && norm(text) === norm(label)) {
            found.push({ where: fname + ':' + lineOf(m.index), key: m[2], text: label, attr: m[1] });
        }
    }
    // (b) in script that sets both on one element:
    //     b.title = t('k');  b.setAttribute('aria-label', t('k'));  ...  b.textContent = t('k');
    const setRe = new RegExp('\\b(\\w+)\\.(?:title = |setAttribute\\(\'(?:title|aria-label)\', )' + T, 'g');
    while ((m = setRe.exec(src)) !== null) {
        const v = m[1], key = m[2];
        if (dict[key] === undefined) continue;
        const win = src.slice(Math.max(0, m.index - 1500), m.index + 1500);
        const textRe = new RegExp('\\b' + v + '\\.(?:textContent|innerText) = [^;\\n]*' + T, 'g');
        let x;
        while ((x = textRe.exec(win)) !== null) {
            if (dict[x[1]] !== undefined && norm(dict[x[1]]) === norm(dict[key])) {
                found.push({ where: fname + ':' + lineOf(m.index), key, text: dict[key], attr: 'title' });
                break;
            }
        }
    }
    return found;
}

// ── Run the rules over the tree ───────────────────────────────────────────────
const report = [];            // [{name, items: [{source, key, text, detail}]}]

console.log('\n── The writing rules ──────────────────────────────────────────');
RULES.forEach(rule => {
    const items = [];
    ENTRIES.forEach(e => {
        const detail = rule[1](e.text, e.key);
        if (detail.length) items.push({ source: e.source, key: e.key, text: e.text, detail: detail.join(', ') });
    });
    report.push({ name: rule[0], items });
    ok(rule[0] + ': none found', items.length === 0, items.length + ' string' + (items.length === 1 ? '' : 's'));
});
{
    const items = [];
    const seenKey = {};
    let sites = 0;
    [[profilesSrc, EN, 'profiles.js'], [dashboardSrc, ADMIN, 'profilesDashboard.html']].forEach(p => {
        const r = tooltipRepeats(p[0], p[1], p[2]);
        sites += r.sites;
        r.forEach(f => {
            const id = f.where + f.key;
            if (seenKey[id]) return;
            seenKey[id] = 1;
            items.push({ source: f.where, key: f.key, text: f.text, detail: f.attr + ' repeats the visible text' });
        });
    });
    // The dashboard's own markup: an element with title= or aria-label= whose text says the same.
    {
        const { markup, toks } = tokenize(dashboardSrc);
        for (let i = 0; i < toks.length; i++) {
            const tk = toks[i];
            if (tk.text !== undefined || tk.close || VOID.has(tk.tag)) continue;
            ['title', 'aria-label'].forEach(name => {
                const v = attr(tk.attrs, name);
                if (!v) return;
                let depth = 0, j = i + 1;
                for (; j < toks.length; j++) {
                    const x = toks[j];
                    if (x.text !== undefined) continue;
                    if (VOID.has(x.tag) || x.raw.endsWith('/>')) continue;
                    if (!x.close) { depth++; continue; }
                    if (depth === 0) break;
                    depth--;
                }
                if (j >= toks.length) return;
                const inner = markup.slice(tk.at + tk.raw.length, toks[j].at);
                const key = attr(tk.attrs, 'data-i18n-' + name) || attr(tk.attrs, 'data-i18n') || '(no key)';
                if (norm(inner) !== '' && norm(inner) === norm(decodeEntities(v))) {
                    items.push({ source: 'profilesDashboard.html:' + (markup.slice(0, tk.at).split('\n').length),
                        key, text: v, detail: name + ' repeats the visible text' });
                }
            });
        }
    }
    ok('6. it looked at tooltips (' + sites + ' title/aria-label sites)', sites > 10, String(sites));
    report.push({ name: '6. a tooltip that repeats its label', items });
    ok('6. a tooltip that repeats its label: none found', items.length === 0,
       items.length + ' element' + (items.length === 1 ? '' : 's'));
}

// ── The detectors can go red ──────────────────────────────────────────────────
// Each rule is shown a string that breaks it and a string that does not. Without this, a
// rule whose pattern stopped matching would pass the tree and nobody would know.
console.log('\n── Each rule catches what it is for, and passes clean copy ────');
{
    const planted = [
        ['1. British spellings', ['Pick a colour', 'Your favourite profile', 'Request cancelled', 'Set behaviour',
            'MIT licence', 'Organise profiles', 'Normalised name', 'Grey text'], ['Pick a color', 'Request canceled', 'Authorize the app', 'Advise your server administrator']],
        ['2. Title Case', ['Switch Profile', "Who's Watching?", 'Edit Your Profile'], ['Switch profile', 'Open Jellyfin', 'Quick Connect code', 'Set the PIN', 'OK', 'Pick a profile. Then Bonfire opens.']],
        ['3. uncontracted negatives', ['It could not be saved', 'You cannot do this', 'We do not store it', 'It does not work',
            'This is not allowed', 'It was not found', 'It will not open', 'They are not set', 'It did not load',
            'They were not saved', 'It would not open', 'You should not see this', 'It has not started',
            'You have not set one', 'It had not finished'], ["It couldn't be saved", "Can't do this", "Doesn't work", 'Do this now']],
        ['4. old names the owner replaced', ['Look', 'Open the Look tab', 'Name and picture', 'Viewing hours',
            'Enabled Libraries', 'Profile sign-in', 'On your home network'], ['Theme', 'Look for it', 'Avatar', 'Allowed times', 'Libraries']],
        ['5. wording writing.md bans', ["Don't worry about it", 'Nothing is broken', "It's worth noting that", 'Simply pick one',
            'Just tap', 'Easily switch', 'Handy for kids', 'A powerful tool', 'Seamless', 'Unlock your library', 'Your media, your way',
            "Whether you're new or old", "It's not just a PIN", 'Submit', "Let's go"], ['Pick a profile.', 'Save', 'Remove profile', 'Couldn\'t save. Try again.']],
    ];
    planted.forEach(p => {
        const rule = RULES.filter(r => r[0] === p[0])[0];
        const missed = p[1].filter(s => rule[1](s).length === 0);
        ok(p[0] + ' flags each planted example', missed.length === 0, 'missed: ' + missed.join(' | '));
        const wrong = p[2].filter(s => rule[1](s).length !== 0);
        ok(p[0] + ' passes clean copy', wrong.length === 0, 'flagged: ' + wrong.join(' | '));
    });
    // An excused key is excused from its one name and nothing else.
    {
        const rule4 = RULES.filter(r => r[0] === '4. old names the owner replaced')[0][1];
        ok('4. the artwork key may say "picture"', rule4('Picture', 'profileForm.artworkPicture').length === 0);
        ok('4. but not another old name', rule4('Picture on the home network', 'profileForm.artworkPicture').length === 1);
        ok('4. and another key still may not say "picture"', rule4('Picture', 'avatar.usePicture').length === 1);
    }
    // 6 on source text: the same label as title and as text is caught; an icon button, and a hint that says more, are not.
    const dict = { 'a.save': 'Save', 'a.hint': 'Saves the profile' };
    const bad = tooltipRepeats('<button title="${t(\'a.save\')}">${t(\'a.save\')}</button>', dict, 'x');
    const badAria = tooltipRepeats('<button aria-label="${t(\'a.save\')}">${t(\'a.save\')}</button>', dict, 'x');
    const badJs = tooltipRepeats("b.title = t('a.save'); b.textContent = t('a.save');", dict, 'x');
    const badCat = tooltipRepeats('\'<button title="\' + escapeHtml(t(\'a.save\')) + \'">\' + escapeHtml(t(\'a.save\')) + \'</button>\'', dict, 'x');
    const iconOnly = tooltipRepeats('<button title="${t(\'a.save\')}" aria-label="${t(\'a.save\')}"><span class="material-icons">save</span></button>', dict, 'x');
    const differs = tooltipRepeats('<button title="${t(\'a.hint\')}">${t(\'a.save\')}</button>', dict, 'x');
    ok('6. a tooltip that repeats its label flags a title, an aria-label, a script pair and a concatenation',
       bad.length === 1 && badAria.length === 1 && badJs.length === 1 && badCat.length === 1,
       [bad.length, badAria.length, badJs.length, badCat.length].join(','));
    ok('6. a tooltip that repeats its label passes an icon-only button and a hint that says more',
       iconOnly.length === 0 && differs.length === 0, iconOnly.length + ',' + differs.length);
}

// ── What it found ─────────────────────────────────────────────────────────────
const total = report.reduce((n, r) => n + r.items.length, 0);
if (total) {
    console.log('\n── What the rules object to ───────────────────────────────────');
    report.forEach(r => {
        if (!r.items.length) return;
        console.log('\n' + r.name + '  (' + r.items.length + ')');
        r.items.forEach(i => console.log('    ' + i.source + '  ' + i.key + '  [' + i.detail + ']  "'
            + i.text.replace(/\s+/g, ' ').slice(0, 140) + '"'));
    });
}

console.log('');
if (fails.length) {
    console.log(pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}
console.log(pass + ' passed, 0 failed');
