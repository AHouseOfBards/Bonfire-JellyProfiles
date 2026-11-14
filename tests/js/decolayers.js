// The second pattern layer and a pattern's opacity (2.0). Runs the real themeCss.
//
//   1. a theme without them is written exactly as before: no new variable, no ::after rule;
//   2. pattern2 gets its own variables and an ::after layer for the gate and the preview;
//   3. opacity writes one variable, and only below 1;
//   4. pattern2 drifts on ::after, by its own size and seconds;
//   5. twinkle keeps working: scaled by the first layer's opacity, and with a second
//      pattern it does not draw the shifted copy on ::after, which is that pattern's layer;
//   6. a pattern2 with no pattern under it draws nothing.
//
//   node tests/js/decolayers.js [path/to/profiles.js]

'use strict';
const fs = require('fs');
const path = require('path');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

const gen = L.themeGenerator(L.readProfiles());
const patterns = L.themesPayload().patterns || {};
const names = Object.keys(patterns);
const load = id => JSON.parse(fs.readFileSync(path.join(L.themesDir(), id + '.json'), 'utf8'));
const css = t => gen.themeCss(t, patterns);
const edit = (id, f) => { const t = load(id); f(t); return t; };
const patternOf = t => t.decorations.filter(d => d.name === 'pattern')[0];

ok('there are pictures to use', names.length >= 2, names.join(','));

// 1. Every built-in theme, as it is.
const builtIn = fs.readdirSync(L.themesDir()).filter(f => /\.json$/.test(f)).map(f => f.replace(/\.json$/, ''));
const touched = builtIn.filter(id => { const s = css(load(id)); return /--jpf-deco2|--jpf-deco-opacity|::after/.test(s.replace(/jpf-twinkle-b[\s\S]*?\}\n\}/, '')) && !/jpf-twinkle/.test(s); });
ok('no built-in theme writes a second layer or an opacity (' + builtIn.length + ' checked)', builtIn.length > 5 && touched.length === 0, touched.join(','));
ok('and Starlight still twinkles as it did', /@keyframes jpf-twinkle \{\s*0%, 100% \{ opacity: 1; \}\s*50% \{ opacity: 0\.45; \}/.test(css(load('starlight'))));

// 2. A second pattern.
const second = edit('bloom', t => t.decorations.push({ name: 'pattern2', image: names[1], size: 96, drift: 'none', opacity: 0.4 }));
const s2 = css(second);
ok('pattern2 sets its own picture, size and opacity', /--jpf-deco2: url\("data:image\/svg/.test(s2) && /--jpf-deco2-size: 96px;/.test(s2) && /--jpf-deco2-opacity: 0\.4;/.test(s2));
ok('and draws on ::after for the gate and for the preview',
    /#profiles-gate-overlay::after,\s*\.jpf-theme-preview[^{]*::after \{[^}]*background-image: var\(--jpf-deco2\)/.test(s2));
ok('the first layer is untouched by it', /--jpf-deco: url\(/.test(s2) && !/--jpf-deco-opacity/.test(s2));

// 3. Opacity.
const faint = edit('bloom', t => { patternOf(t).opacity = 0.5; });
ok('a pattern\'s opacity below 1 is one variable', /--jpf-deco-opacity: 0\.5;/.test(css(faint)));
ok('an opacity of 1 writes nothing', !/--jpf-deco-opacity/.test(css(edit('bloom', t => { patternOf(t).opacity = 1; }))));
ok('the stylesheet reads it, with 1 when it is not there', /opacity: var\(--jpf-deco-opacity, 1\)/.test(fs.readFileSync(path.join(__dirname, '..', '..', 'Web', 'styles.css'), 'utf8')));

// 4. Drift of the second layer.
const drifting = edit('bloom', t => t.decorations.push({ name: 'pattern2', image: names[1], size: 100, drift: 'down', seconds: 50 }));
const s4 = css(drifting);
ok('a drifting second pattern animates ::after by its own size and seconds',
    /#profiles-gate-overlay::after \{[^}]*top: -100px;[^}]*animation: jpf-drift-100 50s linear infinite/.test(s4));
ok('and its keyframes travel its own size', /@keyframes jpf-drift-100 \{[^}]*\}\s*to \{ transform: translateY\(100px\)/.test(s4));
ok('while the first still drifts on ::before', /#profiles-gate-overlay::before \{[^}]*animation: jpf-drift-\d+ /.test(s4));

// 5. Twinkle.
const twink = edit('starlight', t => { patternOf(t).opacity = 0.5; });
const s5 = css(twink);
ok('twinkle scales its fades by the pattern\'s opacity',
    /@keyframes jpf-twinkle-o05 \{\s*0%, 100% \{ opacity: 0\.5; \}\s*50% \{ opacity: 0\.225; \}/.test(s5) && /animation: jpf-twinkle-o05 /.test(s5), s5.slice(s5.indexOf('twinkle') - 40, s5.indexOf('twinkle') + 200));
const both = edit('starlight', t => t.decorations.push({ name: 'pattern2', image: names[1], size: 100, drift: 'none' }));
const s5b = css(both);
ok('with a second pattern the shifted copy is not drawn on ::after', /jpf-twinkle/.test(s5b) && !/jpf-twinkle-b/.test(s5b));
ok('but the first layer still fades', /::before \{\s*will-change: opacity;[^}]*animation: jpf-twinkle /.test(s5b));

// 6. Nothing under it.
const orphan = edit('bloom', t => { t.decorations = [{ name: 'pattern2', image: names[1], size: 96, drift: 'none' }]; });
ok('a second pattern with no first draws nothing', !/--jpf-deco2|::after/.test(css(orphan)));

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
