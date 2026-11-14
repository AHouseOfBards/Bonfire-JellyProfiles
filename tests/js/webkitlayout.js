/*
 * The profile screen laid out by WebKit at an iPhone (390x844) and an iPad (820x1180): every
 * tile inside the viewport, nothing overflowing sideways, every name readable. Asked of each
 * tile and each name separately (an aggregate such as "the page is not wider than the
 * screen" can pass while one tile hangs off the edge inside a clipped container).
 *
 * Run twice per size: the four-profile household, and a crowded one of 21 (the limit),
 * which has long names to wrap.
 *
 * Red against: the stylesheet with the tile grid's wrapping removed, so the tiles stay on
 * one line and run off the side:
 *
 *     node tests/js/webkitlayout.js [profiles.js [styles.css]]
 *
 * Says SKIP and exits 0 when Playwright's WebKit is not installed (see _webkit.js).
 */
'use strict';

const W = require('./_webkit');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

// Measured in the page, so the numbers are WebKit's own.
function measure() {
    const vw = document.documentElement.clientWidth;
    const rect = el => { const r = el.getBoundingClientRect(); return { l: r.left, r: r.right, w: r.width, h: r.height }; };
    const cards = Array.from(document.querySelectorAll('#profiles-gate-overlay .jpf-profile-card')).map((el, i) => {
        const r = rect(el);
        const nameEl = el.querySelector('.jpf-profile-name > span') || el.querySelector('.jpf-profile-name');
        const cs = nameEl ? getComputedStyle(nameEl) : null;
        const nr = nameEl ? rect(nameEl) : null;
        const color = cs ? /rgba?\(([^)]*)\)/.exec(cs.color) : null;
        const alpha = color && color[1].split(',').length === 4 ? Number(color[1].split(',')[3]) : 1;
        return {
            i, card: r, text: nameEl ? nameEl.textContent.trim() : '',
            size: cs ? parseFloat(cs.fontSize) : 0,
            shown: cs ? cs.visibility === 'visible' && cs.display !== 'none' && Number(cs.opacity) > 0 && alpha > 0 : false,
            name: nr
        };
    });
    const overlay = document.getElementById('profiles-gate-overlay');
    return {
        vw, docW: document.documentElement.scrollWidth, bodyW: document.body.scrollWidth,
        overlayScrollW: overlay ? overlay.scrollWidth : -1, overlayW: overlay ? overlay.clientWidth : -1,
        cards
    };
}

(async () => {
    const browser = await W.launch();
    const src = W.sources();
    const srv = await W.serve(src.js, src.cssPath);
    try {
        for (const device of ['phone', 'tablet']) {
            for (const count of [0, 21]) {
                const tag = device + (count ? ' crowded' : '') + ': ';
                const s = await W.openScene(browser, srv, 'picker', device, count ? '&count=' + count : '');
                const m = await s.page.evaluate(measure);
                const want = count || 4;
                ok(tag + 'the tiles are there', m.cards.length >= want, m.cards.length + ' tiles, wanted ' + want);
                ok(tag + 'no sideways scroll (page, body, gate)',
                    m.docW <= m.vw && m.bodyW <= m.vw && m.overlayScrollW <= m.overlayW,
                    'viewport ' + m.vw + ', page ' + m.docW + ', body ' + m.bodyW + ', gate ' + m.overlayScrollW + '/' + m.overlayW);
                const out = m.cards.filter(c => c.card.w <= 0 || c.card.l < -0.5 || c.card.r > m.vw + 0.5);
                ok(tag + 'every tile sits inside the viewport', out.length === 0,
                    out.map(c => '#' + c.i + ' ' + c.text + ' ' + Math.round(c.card.l) + '..' + Math.round(c.card.r)).join(', '));
                const bad = m.cards.filter(c => !c.text || !c.shown || c.size < 12 || !c.name || c.name.w <= 0
                    || c.name.l < -0.5 || c.name.r > m.vw + 0.5);
                ok(tag + 'every name is readable (text, 12px or more, visible, on screen)', bad.length === 0,
                    bad.map(c => '#' + c.i + ' "' + c.text + '" ' + c.size + 'px shown=' + c.shown + (c.name ? ' ' + Math.round(c.name.l) + '..' + Math.round(c.name.r) : '')).join(', '));
                ok(tag + 'no console error', s.errors.length === 0, s.errors.join(' | '));
                await s.close();
            }
        }
    } finally {
        await browser.close();
        await srv.close();
    }
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.log('FAIL  harness error: ' + (e && e.stack || e)); process.exit(1); });
