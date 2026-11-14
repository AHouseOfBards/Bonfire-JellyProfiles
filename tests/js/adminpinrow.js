/*
 * One task, one row; and the headline agrees with the rows under it.
 *
 * A main account with profiles and no main PIN showed three things at once: the headline
 * "Nothing needs your attention.", a NOTE row saying the account has no main PIN, and a
 * TO DO row under Getting started asking to require one. The same task twice, under a
 * headline that said there was none.
 *
 * The real renderStatus draws the real pinStatusRows, and the real gettingStartedItems is
 * asked for its list in the same state. Every combination of (accounts without a PIN, PIN
 * required) is checked on its own, not summed.
 *
 *     node tests/js/adminpinrow.js "" /path/to/old/profilesDashboard.html
 */
'use strict';

const fs = require('fs');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '  - ' + detail : '')); }
}

const html = fs.readFileSync(process.argv[3] || process.argv[2] || L.dashboardPath(), 'utf8');
const script = L.dashboardScript(html);
const has = name => new RegExp('function ' + name + '\\s*\\(').test(script);

if (!has('pinStatusRows')) {
    ok('the Status row for accounts without a main PIN can be run on its own', false);
    console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
    process.exit(1);
}

const consts = ['STATUS_ORDER', 'LEVEL_RANK'].map(name => new RegExp('const ' + name + ' = [^;]+;').exec(script)[0]);
const pieces = L.dashboardI18n(script) + consts.join('\n') + '\n'
    + ['field', 'levelLabel', 'setStatus', 'renderStatus', 'goTo', 'pinStatusRows', 'isUtcZone', 'gettingStartedItems']
        .map(n => L.extractFunction(script, n)).join('\n');

function el(tag) {
    return {
        tag, children: [], className: '', attrs: {}, listeners: {}, _text: '',
        set textContent(v) { this._text = v; this.children = []; },
        get textContent() { return this._text + this.children.map(c => c.textContent).join(' '); },
        appendChild(c) { return this.children.push(c), c; },
        setAttribute(k, v) { this.attrs[k] = v; },
        addEventListener(t, f) { this.listeners[t] = f; }
    };
}
const api = new Function('document', pieces + '\nreturn { pinStatusRows, renderStatus, gettingStartedItems };')({ createElement: el });

function scenario(open, required) {
    const list = el('div'), summary = el('div');
    const page = {
        dataset: {}, _status: {}, _config: { RequireMainPin: required }, _mappings: { subProfiles: [{}] },
        _openPins: open,
        querySelector: sel => sel === '#statusChecks' ? list : sel === '#statusSummary' ? summary : null
    };
    page._status.pins = api.pinStatusRows(open, required);
    api.renderStatus(page);
    return {
        headline: summary.textContent,
        statusRows: list.children,
        gs: api.gettingStartedItems(page).filter(i => i.key === 'pins')
    };
}

[[0, false], [0, true], [1, false], [2, false], [1, true], [3, true]].forEach(c => {
    const open = c[0], required = c[1];
    const label = open + ' without a PIN, ' + (required ? 'required' : 'not required');
    const s = scenario(open, required);
    const needsAttention = s.statusRows.some(r => /^(warn|bad)$/.test(r.attrs['data-level']));
    ok(label + ': a row that needs attention is not under "Nothing needs your attention"',
       !(s.statusRows.length && s.headline === 'Nothing needs your attention.' && open > 0),
       s.headline);
    if (open > 0) ok(label + ': the headline counts that row', needsAttention && /need/.test(s.headline), s.headline);
    ok(label + ': the main PIN is asked for in one place',
       (open > 0 && !required) ? s.gs.length === 0 : true,
       s.gs.length + ' Getting started item(s) next to the Status row');
});

const quiet = scenario(0, false);
ok('with no account at risk, Getting started still offers the main PIN', quiet.gs.length === 1);
ok('and Status is quiet', quiet.statusRows.length === 0 && quiet.headline === 'Nothing needs your attention.', quiet.headline);

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
