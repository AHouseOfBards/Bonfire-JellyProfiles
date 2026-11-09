/*
 * The maintenance and import confirmations say what they do (P2-307, P2-308).
 *
 *  - Closing the server with "Stop what people are watching" ticked must say playback stops.
 *  - Editing while closed used to skip the question, so ticking the box on a server that was
 *    already closed cut off playback unasked. It asks when the box is newly ticked, and only
 *    then: editing a message stays quick.
 *  - The import confirmation tells the administrator to export first.
 *
 * Runs the real bindMaintenance against stub elements, then reads the import wording from
 * the page's own strings. Point it at an older dashboard with argv[3]:
 *
 *     node tests/js/adminmaintconfirm.js "" /path/to/old/profilesDashboard.html
 */
'use strict';

const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

const html = L.readDashboard();
const script = L.dashboardScript(html);
const source = L.dashboardI18n(script) + '\n' + L.extractFunction(script, 'bindMaintenance');

function run(opts) {
    const asked = [];
    const saved = [];
    const handlers = {};
    const els = {
        '#mntClose': { dataset: {}, addEventListener: function (e, f) { handlers.close = f; } },
        '#mntOpen': {
            classList: { contains: function () { return !opts.closed; } },
            addEventListener: function () {}
        },
        '#mntStop': { checked: !!opts.stop },
        '#mntReopen': { checked: !!opts.reopen },
        '#mntUntil': { value: opts.until || '' }
    };
    const page = { _mntStopWas: !!opts.stopWas, querySelector: function (s) { return els[s]; } };
    const askFirst = function (message) { asked.push(message); return { then: function (f) { f(); } }; };
    const saveMaintenance = function (p, enabled) { saved.push(enabled); };
    const formatWhen = function (iso) { return 'WHEN(' + iso + ')'; };
    const localInputToIso = function (v) { return v ? v + 'Z' : null; };
    new Function('askFirst', 'saveMaintenance', 'formatWhen', 'localInputToIso', 'page',
        source + '; bindMaintenance(page);')(askFirst, saveMaintenance, formatWhen, localInputToIso, page);
    handlers.close();
    return { asked: asked, saved: saved };
}

const closeStop = run({ stop: true });
ok('closing with Stop ticked asks, and the question says playback stops',
   closeStop.asked.length === 1 && /watching/i.test(closeStop.asked[0]) && /stop/i.test(closeStop.asked[0]));

const closePlain = run({ stop: false });
ok('closing without Stop still asks', closePlain.asked.length === 1);
ok('and does not claim anything stops', !/stop/i.test(closePlain.asked[0] || ''));

const editNew = run({ closed: true, stop: true, stopWas: false });
ok('editing a closed server asks when Stop is newly ticked', editNew.asked.length === 1);
ok('and saves only after the answer', editNew.saved.length === 1);

const editSame = run({ closed: true, stop: true, stopWas: true });
ok('editing a closed server that already stops playback does not ask again', editSame.asked.length === 0 && editSame.saved.length === 1);

const editPlain = run({ closed: true, stop: false });
ok('editing the message of a closed server does not ask', editPlain.asked.length === 0 && editPlain.saved.length === 1);

// An automatic reopen is part of what the administrator is agreeing to.
const closeAuto = run({ reopen: true, until: '2026-11-12T03:00' });
ok('closing to reopen by itself says when, in the question',
   closeAuto.asked.length === 1 && /WHEN\(2026-11-12T03:00Z\)/.test(closeAuto.asked[0]) && /by itself/i.test(closeAuto.asked[0]), closeAuto.asked[0]);
ok('and a plain close does not', !/by itself/i.test(closePlain.asked[0] || ''));
const tickNoTime = run({ reopen: true, until: '' });
ok('a tick with no time says nothing about reopening in the question', !/by itself/i.test(tickNoTime.asked[0] || ''));

const imp = /'admin\.backup\.confirm':\s*'([^']|\\')*'/.exec(script);
ok('the import confirmation tells the administrator to export first', !!imp && /export first/i.test(imp[0]));

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
