// r2-24-01: the main PIN typed on the gate for one act (more time for a profile) must not
// stay cached when the act is cancelled or refused, or the next person at the screen skips
// the PIN. Runs the real chooseMoreTime and answerTimeRequest and reads back whether
// masterPin survived each way out. Before the fix, cancel and failure leave it set.
//
//   node tests/js/gatepin.js [path/to/profiles.js]

'use strict';
const L = require('./_lib');
const D = require('./_stubdom');

const env = D.loadPlugin(L.readProfiles());
const { PP, settle } = env;

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

let select = null;
let actions = null;
PP.showSelectDialog = (title, message, options, onSubmit, onCancel) => { select = { options, onSubmit, onCancel }; };
PP.showActionsDialog = (title, message, list, onDismiss) => { actions = { list, onDismiss }; };
PP.showAlert = (title, message, done) => { if (done) done(); };

const profile = { profileUserId: 'kid', profileName: 'Kid' };
const request = left => ({ profileId: 'kid', profileName: 'Kid', askedUtc: '2030-01-09T19:00:00Z', timeZone: 'UTC', grantMinutesLeft: left });

// What a cancel does is whatever the dialog is told to run on dismissal.
function dismiss(d) { if (d && typeof d.onCancel === 'function') d.onCancel(); if (d && typeof d.onDismiss === 'function') d.onDismiss(); }

(async () => {
    PP.isManageMode = false;

    env.setRespond(() => ({ status: 200, body: {} }));

    PP.masterPin = '1234'; select = null;
    PP.chooseMoreTime(profile, () => {});
    dismiss(select);
    ok('cancelling the amount drops the PIN', PP.masterPin === null);

    PP.masterPin = '1234'; select = null;
    PP.chooseMoreTime(profile, () => {});
    select.onSubmit('30');
    await settle();
    ok('giving the time drops the PIN', PP.masterPin === null);

    env.setRespond(() => ({ status: 400, body: 'No.' }));
    PP.masterPin = '1234'; select = null;
    PP.chooseMoreTime(profile, () => {});
    select.onSubmit('30');
    await settle();
    ok('a refused grant drops the PIN', PP.masterPin === null);
    env.setRespond(() => ({ status: 200, body: {} }));

    PP.masterPin = '1234'; actions = null;
    PP.answerTimeRequest(request(240), () => {});
    dismiss(actions);
    ok('cancelling an answer drops the PIN', PP.masterPin === null);

    PP.masterPin = '1234'; actions = null;
    PP.answerTimeRequest(request(0), () => {});
    dismiss(actions);
    ok('cancelling at the daily cap drops the PIN', PP.masterPin === null);

    env.setRespond(() => ({ status: 400, body: 'No.' }));
    PP.masterPin = '1234'; actions = null;
    PP.answerTimeRequest(request(240), () => {});
    actions.list[0].run();
    await settle();
    ok('a refused answer drops the PIN', PP.masterPin === null);
    env.setRespond(() => ({ status: 200, body: {} }));

    // Manage Profiles holds the PIN on purpose and keeps it.
    PP.isManageMode = true;
    PP.masterPin = '1234'; select = null;
    PP.chooseMoreTime(profile, () => {});
    dismiss(select);
    ok('Manage Profiles keeps the PIN it was opened with', PP.masterPin === '1234');

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})().catch(e => { console.log('  ERROR ' + (e && e.stack || e)); process.exit(1); });
