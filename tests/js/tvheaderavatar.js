/*
 * The Switch Profile avatar in the legacy header, on a television. It was a fixed 28px
 * circle while its neighbours grow with the root, which jellyfin-web raises for the TV
 * layout (27px at 1080p, 20px at 720p): a header icon is 1.67em of that
 * (emby-button.scss, .paper-icon-button-light > .material-icons). On a 1080p set the
 * avatar sat at about 60% of the icons beside it.
 *
 * Asks the browser what size the avatar is drawn at, with the real stylesheet and the
 * button's own inline 28px style, rather than whether a rule is present.
 *
 *   node tests/js/tvheaderavatar.js                    # the working tree
 *   node tests/js/tvheaderavatar.js path/to/styles.css # an older stylesheet: must fail
 */
'use strict';

const fs = require('fs');
const L = require('./_lib');
const chrome = require('../screens/chrome');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

const css = process.argv[2] ? fs.readFileSync(process.argv[2], 'utf8') : L.stylesOnly();

// The button as _buildHeaderBubble writes it: the classes, and the avatar's inline size.
const BUTTON = '<button id="profiles-floating-bubble" class="paper-icon-button-light headerButton focusable">' +
    '<div class="profiles-header-avatar" style="background-color:#3d8bfd;width:28px;height:28px;border-radius:50%;' +
    'display:flex;align-items:center;justify-content:center;font-size:0.85rem;font-weight:700;box-sizing:border-box;' +
    'overflow:hidden;position:relative;">S</div></button>';

function measure(rootPx, tv, modern) {
    return '(function(){ var h = document.documentElement; h.style.fontSize = "' + rootPx + 'px";' +
        ' h.className = ' + JSON.stringify(tv ? 'jpf-tv' : '') + ';' +
        ' document.body.innerHTML = ' + JSON.stringify('<div class="headerRight">' + BUTTON + '</div>') + ';' +
        (modern ? ' document.getElementById("profiles-floating-bubble").classList.add("jpf-modern-toolbar-btn");' : '') +
        ' var a = document.querySelector(".profiles-header-avatar").getBoundingClientRect();' +
        ' return Math.round(a.width) + "x" + Math.round(a.height); })()';
}

(async () => {
    const c = await chrome.launch();
    try {
        await c.evaluate('(function(){ var s = document.createElement("style"); s.textContent = ' + JSON.stringify(css) + '; document.head.appendChild(s); return 1; })()');
        const icon = r => 1.67 * r;

        const tv1080 = await c.evaluate(measure(27, true, false));
        const w1080 = parseInt(tv1080, 10);
        ok('on a 1080p television the avatar is near the size of the header icons (' + Math.round(icon(27)) + 'px)',
            w1080 >= 0.85 * icon(27) && w1080 <= 1.15 * icon(27), tv1080);
        ok('and it stays round', /^(\d+)x\1$/.test(tv1080), tv1080);

        const tv720 = await c.evaluate(measure(20, true, false));
        const w720 = parseInt(tv720, 10);
        ok('on a 720p television it follows the smaller root (' + Math.round(icon(20)) + 'px)',
            w720 >= 0.85 * icon(20) && w720 <= 1.15 * icon(20), tv720);

        const desk = await c.evaluate(measure(15, false, false));
        ok('off a television it is the 28px it always was', desk === '28x28', desk);

        const modern = await c.evaluate(measure(27, true, true));
        ok('in the modern toolbar it is still 40px, beside the 40px MUI avatar', modern === '40x40', modern);
    } finally { await c.close(); }

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
