// Parses the inline <script> block of the dashboard page. The file is an embedded
// resource, so a syntax error there is invisible until the settings page is opened.
const fs = require('fs');
const L = require('./_lib');
const html = fs.readFileSync(L.dashboardPath(), 'utf8');
const m = html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/);
if (!m) { console.error('no script block found'); process.exit(1); }
try {
    new Function(m[1]);
    console.log('dashboard script parses OK (' + m[1].split('\n').length + ' lines)');
} catch (e) {
    console.error('SYNTAX ERROR:', e.message);
    process.exit(1);
}
