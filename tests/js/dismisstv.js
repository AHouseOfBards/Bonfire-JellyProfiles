// The announcement's dismiss button, for a finger and a remote (P2-207).
//
// It was about 20px square, and its focus rule says "outline: none" with a 2px shadow, so
// the television's thick focus ring (which is scoped to .profile-section) never reached it.
// Resolved, not string-matched: the television rule has to win the cascade over the
// base rule, which means more specificity or equal and later, for the one property that
// matters here, outline.
//
//   node tests/js/dismisstv.js [path/to/profiles.js]

'use strict';
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

const css = L.stylesOnly().replace(/\/\*[\s\S]*?\*\//g, '');

// Every rule, in order, with the selectors split out.
const rules = [];
const re = /([^{}]+)\{([^{}]*)\}/g;
let m;
while ((m = re.exec(css)) !== null) {
    rules.push({ selectors: m[1].split(',').map(s => s.trim()), body: m[2], at: m.index });
}

function specificity(sel) {
    const ids = (sel.match(/#[\w-]+/g) || []).length;
    const classes = (sel.match(/\.[\w-]+|\[[^\]]*\]|:(?!:)[\w-]+(\([^)]*\))?/g) || []).length;
    const tags = (sel.replace(/\.[\w-]+|#[\w-]+|\[[^\]]*\]|::?[\w-]+(\([^)]*\))?/g, ' ').match(/\b[a-z][\w-]*/gi) || []).length;
    return ids * 10000 + classes * 100 + tags;
}

// What the winning declaration of a property is for an element matching `selector` when
// it also matches the ancestor class `ancestor` ('' for none) and is focused.
function winner(prop, ancestor) {
    let best = null;
    for (const r of rules) {
        const decl = new RegExp('(^|[;\\s])' + prop + '\\s*:\\s*([^;]+)').exec(r.body);
        if (!decl) continue;
        for (const sel of r.selectors) {
            if (sel.indexOf('profiles-announcement-dismiss') === -1) continue;
            if (/:hover/.test(sel) && !/:focus/.test(sel)) continue;
            if (/\.jpf-[\w-]+/.test(sel.replace('profiles-announcement-dismiss', ''))) {
                const wants = (sel.match(/\.jpf-[\w-]+/g) || []).filter(c => c !== '.jpf-r-sm');
                if (wants.some(c => c !== ancestor)) continue;
            }
            const s = specificity(sel);
            if (!best || s > best.s || (s === best.s && r.at > best.at)) best = { s: s, at: r.at, value: decl[2].trim(), sel: sel };
        }
    }
    return best;
}

const base = winner('outline', '');
ok('on a phone or a desktop the focused button has no outline, only its ring', !!base && /^none/.test(base.value), base && base.sel + ' ' + base.value);
const tv = winner('outline', '.jpf-tv');
ok('on a television the outline is thick', !!tv && /^4px solid/.test(tv.value), tv && tv.sel + ' ' + tv.value);

const sizeRule = rules.find(r => r.selectors.indexOf('.profiles-announcement-dismiss') !== -1 && /min-width/.test(r.body));
const num = (prop) => {
    const d = sizeRule && new RegExp(prop + '\\s*:\\s*([\\d.]+)rem').exec(sizeRule.body);
    return d ? parseFloat(d[1]) * 16 : 0;
};
ok('it is at least 44px wide', num('min-width') >= 44, num('min-width') + 'px');
ok('and 44px tall', num('min-height') >= 44, num('min-height') + 'px');

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
