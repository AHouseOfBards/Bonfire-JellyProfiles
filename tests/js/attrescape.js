// Text a translation supplies, written into an attribute, is escaped; and the error lines
// and the upload label say what they are to a screen reader (R-1220, R-1221).
//
// A translation may hold a double quote, and one inside title="…" ends the attribute and
// lets the rest of the string become markup. t() cannot escape for itself, because some of
// its output is HTML on purpose. So every ${t(...)} inside an attribute value must be
// wrapped in escapeHtml(...). This reads every attribute value in the script and names each
// one that is not, by line.
//
//   node tests/js/attrescape.js [path/to/profiles.js]

'use strict';
const L = require('./_lib');

const SRC = L.readProfiles();

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; console.log('  PASS  ' + name); }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

// Every attribute value that holds a bare ${t(...)}: [line, attribute].
function bare(src) {
    const found = [];
    src.split(/\r?\n/).forEach((line, n) => {
        const re = /([\w-]+)="/g;
        let m;
        while ((m = re.exec(line))) {
            let p = m.index + m[0].length;
            while (p < line.length && line[p] !== '"') {
                if (line.startsWith('${', p)) {
                    if (line.startsWith('${t(', p)) found.push([n + 1, m[1]]);
                    let d = 0, k = p + 1;
                    for (; k < line.length; k++) {
                        if (line[k] === '{') d++;
                        else if (line[k] === '}') { d--; if (d === 0) break; }
                    }
                    p = k + 1;
                } else p++;
            }
            re.lastIndex = p;
        }
    });
    return found;
}

// The detector has to be able to go red before its silence means anything.
ok('the detector sees a bare t() in an attribute', bare('<i title="${t(\'a\')}">').length === 1);
ok('and passes a wrapped one', bare('<i title="${escapeHtml(t(\'a\'))}">').length === 0);
ok('and one with other text around it', bare('<i title="x ${t(\'a\', { n: 1 })} y">').length === 1);

const loose = bare(SRC);
loose.forEach(f => ok('line ' + f[0] + ': ' + f[1] + '= is escaped', false, 'a translation with " breaks out of it'));
ok('no attribute carries an unescaped translation', loose.length === 0, loose.length + ' found');

// Error lines are announced when they appear.
['profiles-libart-error', 'create-error-msg', 'edit-pin-error', 'switcher-mode-error', 'bonfire-join-error']
    .forEach(id => {
        const m = new RegExp('<div id="' + id + '"[^>]*>').exec(SRC);
        ok(id + ' is an alert', !!m && /role="alert"/.test(m[0]), m ? m[0] : 'not found');
    });

// A label that works as a button says so.
{
    const m = /<label for="\$\{prefix\}-profile-image-file"[^>]*>/.exec(SRC);
    ok('the upload label is a button to a screen reader', !!m && /role="button"/.test(m[0]), m ? m[0] : 'not found');
}

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
if (fails.length) { console.log('\n   FAILED: ' + fails.length); process.exit(1); }
