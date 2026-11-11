// The emergency-disable prompt (R-1185, R-1188).
//
//  - R-1185: it showed the browser's own "Failed to fetch" and reported a 500 as
//    "incorrect code". A wrong code is 401/403 (or 400); a server fault or no connection
//    is not the person's mistake.
//  - R-1188: the code box was named only by its placeholder and the error was not
//    announced.
//
//   node tests/js/panicprompt.js [path/to/profiles.js]

'use strict';
const L = require('./_lib');
const S = require('./_stubdom');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

async function attempt(status) {
    const env = S.loadPlugin(L.readProfiles());
    const { PP, sandbox } = env;
    let dlg = null;
    sandbox.document.createElement = () => { dlg = S.makeContent(); dlg.remove = () => {}; return dlg; };
    if (status === 'network') {
        sandbox.fetch = () => Promise.reject(new TypeError('Failed to fetch'));
    } else {
        env.setRespond(() => ({ status: status, body: {} }));
    }
    PP.showPanicPrompt();
    const input = dlg.querySelector('#profiles-panic-input');
    input.value = 'abc';
    dlg.querySelector('#profiles-panic-submit').click();
    await env.settle();
    return { dlg, text: dlg.querySelector('#profiles-panic-error').textContent };
}

(async function () {
    const base = await attempt(401);
    const incorrect = base.text;
    ok('a refused code says the code is wrong', /\S/.test(incorrect), incorrect);
    ok('the code box has a name beyond its placeholder', /aria-label="[^"]+"/.test(/<input[^>]*profiles-panic-input[^>]*>/.exec(base.dlg.innerHTML)[0]));
    ok('the error is announced', /id="profiles-panic-error"[^>]*role="alert"|role="alert"[^>]*id="profiles-panic-error"/.test(base.dlg.innerHTML));

    const fault = await attempt(500);
    ok('a server fault is not reported as a wrong code', /\S/.test(fault.text) && fault.text !== incorrect, fault.text);
    const net = await attempt('network');
    ok('no connection is not "Failed to fetch"', !/Failed to fetch/.test(net.text) && /\S/.test(net.text) && net.text !== incorrect, net.text);
    const limited = await attempt(429);
    ok('too many attempts keeps its own message', /\S/.test(limited.text) && limited.text !== incorrect && limited.text !== fault.text, limited.text);

    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(fails.length ? 1 : 0);
})();
