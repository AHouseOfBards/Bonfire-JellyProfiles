/*
 * The admin dashboard's and the emails' wording, phase 3.
 *
 * What it holds: the tab names, the setting names the owner decided, US spelling, "avatar" not
 * "picture", the Status note that used to say "so those profiles can switch into it", the email
 * settings living on the Notices tab, and the text of every email that was reviewed. Each
 * expectation names a key, so a failure says which string is wrong, not that "some text" is.
 *
 * Red against the page before the change:
 *
 *     node tests/js/adminphase3.js /path/to/old/profilesDashboard.html /path/to/old/EmailText.cs
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..', '..');
const DASH = process.argv[2] || path.join(ROOT, 'Web', 'profilesDashboard.html');
const MAIL = process.argv[3] || path.join(ROOT, 'Mail', 'EmailText.cs');
const LANGS = ['ca', 'es', 'fr', 'pl', 'tr'];

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? '\n          ' + detail : '')); }
}

const html = fs.readFileSync(DASH, 'utf8').replace(/\r\n/g, '\n');

// English as the page holds it: markup text by data-i18n key, then ADMIN_EN.
const en = {};
const markup = /<([a-z0-9]+)\b[^>]*\bdata-i18n="([^"]+)"[^>]*>([^<]*)</g;
let m;
while ((m = markup.exec(html))) en[m[2]] = m[3].replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim();
const start = html.indexOf('const ADMIN_EN = {');
const end = html.indexOf('\n            };', start);
const block = start >= 0 ? html.slice(start, end) : '';
const entry = /^\s*'([^']+)': (?:'((?:[^'\\]|\\.)*)'|"((?:[^"\\]|\\.)*)")/gm;
while ((m = entry.exec(block))) en[m[1]] = (m[2] !== undefined ? m[2] : m[3]).replace(/\\'/g, "'");

const WANT = {
    'admin.tab.devices': 'Sessions',
    'admin.tab.look': 'Theme',
    'admin.tab.messages': 'Notices',
    'admin.tab.activity': 'Switches',
    'admin.devices.title': 'Sessions',
    'admin.viewing.title': 'Viewing limits',
    'admin.viewing.lede': 'Households can set their own.',
    'admin.switcher.location': 'Switch button',
    'admin.switcher.locationButton': 'In the header',
    'admin.switcher.locationMenu': "In Jellyfin's user menu",
    'admin.avatars.lede': 'On a TV, this is the only way to set an avatar.',
    'admin.avatars.upload': 'Upload avatars',
    'admin.avatars.libraryOnlyHint': "Profiles can't upload their own.",
    'admin.avatars.starterHint': '30 built-in avatars. Fluent Emoji by Microsoft, MIT license.',
    'admin.avatars.quality': 'Avatar quality',
    'admin.avatars.qualityHint': 'More disk. Applies to new avatars.',
    'admin.status.avatarsOne': '1 profile avatar is missing from disk.',
    'admin.status.avatarsMany': '{count} profile avatars are missing from disk.',
    'admin.avatars.rejected': "The server refused it. It may be over {mb} MB after resizing, or Avatar quality was changed and not saved.",
    'admin.avatars.missingOne': "1 profile has an avatar that's no longer on disk.",
    'admin.avatars.missingMany': "{count} profiles have an avatar that's no longer on disk.",
    'admin.avatars.missingHint': 'They show an initial instead. Set a new avatar to clear this.',
    'admin.theme.crimson': 'Crimson (velvet theater, red and gold)',
    'admin.theme.glass': 'Glass (soft color blur, frosted buttons)',
    'admin.themes.followHint': 'Related colors follow the ones you pick.',
    'admin.themes.p.colour': 'Must be a color: #rrggbb or #rrggbbaa.',
    'admin.banner.failLive': "index.html isn't the cause, so there's nothing to change on disk. Restart Jellyfin if it still fails after a re-check.",
    'admin.saveBlocked': 'The settings didn\'t load, so nothing was saved. Press "Try again".',
    'admin.themes.none': 'No themes of your own yet. Choose "New theme" to make one.',
    'admin.help.librariesA': 'Change a profile\'s libraries in "Edit profile", not in Dashboard → Users. Adding one there sticks; removing one doesn\'t, because it looks the same as Jellyfin resetting the account.',
    'admin.email.needsEmail':'Needs working email. Set it up under Setup.',
    'admin.status.openPinsOne': '1 main account has no main PIN, so anyone using its profiles can switch to it. Require main PINs to make them set one.',
    'admin.status.openPinsMany': '{count} main accounts have no main PIN, so anyone using their profiles can switch to them. Require main PINs to make them set one.'
};
Object.keys(WANT).forEach(function (k) {
    ok(k, en[k] === WANT[k], 'is: ' + JSON.stringify(en[k]) + '\n          want: ' + JSON.stringify(WANT[k]));
});

// The old words, anywhere a person reads them.
const BANNED = [/home network/i, /\bunlock/i, /\b(is|are|did|does|do|was|were|has|have|had|will|would|should|can|could) not\b|\bcannot\b/i, /Manage Profiles/,/\bpictures?\b/i, /colour/i, /licence/i, /theatre/i, /Where to switch from/, /Viewing hours/];
Object.keys(en).filter(function (k) { return /^admin\./.test(k); }).forEach(function (k) {
    BANNED.forEach(function (re) { if (re.test(en[k])) ok(k + ' has none of ' + re, false, JSON.stringify(en[k])); });
});

// The hint under the switch button said again what the two choices say.
ok('admin.switcher.locationHint is gone from the page', !('admin.switcher.locationHint' in en));
LANGS.forEach(function (l) {
    const j = JSON.parse(fs.readFileSync(path.join(ROOT, 'Web', 'i18n', l + '.json'), 'utf8'));
    ok(l + ' no longer carries admin.switcher.locationHint', !('admin.switcher.locationHint' in j));
});

// The email server settings are on Setup, not Notices (owner decision, 2026-11-12). A move, so the ids stay.
function panel(id) {
    const a = html.indexOf('id="jpfPanel' + id + '"');
    const b = html.indexOf('<!-- /', a);
    return a < 0 ? '' : html.slice(a, b);
}
const setup = panel('Setup'), notices = panel('Messages');
['jpfEmail', 'emailHost', 'emailPort', 'emailSecurity', 'emailUsername', 'emailPassword', 'emailFrom', 'emailTestTo',
    'emailTest', 'emailSave', 'emailResult', 'emailAccounts', 'emailStatus'].forEach(function (id) {
    ok('#' + id + ' is on Setup', setup.indexOf('id="' + id + '"') >= 0);
    ok('#' + id + ' is not on Notices', notices.indexOf('id="' + id + '"') < 0);
});
ok('Setup keeps first-time setup, and the viewing limits are on Security',
   setup.indexOf('id="jpfOnboarding"') >= 0 && setup.indexOf('id="jpfViewing"') < 0 && panel('Security').indexOf('id="jpfViewing"') >= 0);
const links = html.match(/tab: '[a-z]+', anchor: 'jpfEmail'/g) || [];
ok('the status links to the email settings open Setup', links.length === 2 && links.every(function (s) { return s.indexOf("'setup'") >= 0; }), links.join(' | '));

// Emails.
const src = fs.readFileSync(MAIL, 'utf8').replace(/\r\n/g, '\n');
const mail = {};
const line = /^\s*\["([^"]+)"\] = "((?:[^"\\]|\\.)*)",$/gm;
while ((m = line.exec(src))) mail[m[1]] = m[2].replace(/\\n/g, '\n').replace(/\\"/g, '"').replace(/\\\\/g, '\\');
const TAIL = 'Turn these emails off in Bonfire: Settings, Your account.';
const MAILWANT = {
    'email.test.body': 'Bonfire email is working. Codes and notices will come from this address.',
    'email.confirm.subject': 'Confirm your Bonfire email',
    'email.confirm.body': "Your code is {code}. It expires in 15 minutes.\n\nEnter it in Bonfire to use this address for {account}.\n\nIf you didn't ask for this, ignore this email.",
    'email.reset.body': "Your code is {code}. It expires in 15 minutes.\n\nEnter it in Bonfire to set a new PIN for {account}.\n\nIf you didn't ask for this, ignore this email. Your PIN hasn't changed.",
    'email.lockout.body': "Someone entered {name}'s PIN wrong too many times. They can try again in 15 minutes.\n\n" + TAIL,
    'email.newDevice.body': "Your household signed in on a new device: {device}.\n\nIf you don't recognize it, ask your server's administrator to sign it out.\n\n" + TAIL,
    'email.timeAsked.body': '{name} asked for more time.\n\nTo answer, open Bonfire, choose Manage profiles, then {name}.\n\n' + TAIL,
    'email.limitReached.body': '{name} has reached today\'s limit of {minutes} min.\n\nTo give more time, open Bonfire, choose Manage profiles, then {name}.\n\n' + TAIL,
    'notice.asked': '{name} asked for more time. To answer, open Bonfire, choose Manage profiles, then {name}.',
    'notice.picker.tooMany': 'Too many tries. Try again later.'
};
Object.keys(MAILWANT).forEach(function (k) {
    ok(k, mail[k] === MAILWANT[k], 'is: ' + JSON.stringify(mail[k]) + '\n          want: ' + JSON.stringify(MAILWANT[k]));
});
// Placeholders: each mail keeps exactly the ones it had.
const HOLES = {
    'email.confirm.body': ['account', 'code'], 'email.reset.body': ['account', 'code'], 'email.resetDone.body': ['account'],
    'email.lockout.body': ['name'], 'email.newDevice.body': ['device'], 'email.timeAsked.body': ['name'],
    'email.timeAsked.subject': ['name'], 'email.limitReached.body': ['minutes', 'name'], 'email.limitReached.subject': ['name'],
    'notice.stopsIn': ['minutes'], 'notice.againAt': ['time'], 'notice.againOn': ['day', 'time'], 'notice.asked': ['name']
};
Object.keys(HOLES).forEach(function (k) {
    const got = Array.from(new Set((String(mail[k] || '').match(/\{[a-zA-Z]+\}/g) || []).map(function (s) { return s.slice(1, -1); }))).sort();
    ok(k + ' keeps its placeholders', JSON.stringify(got) === JSON.stringify(HOLES[k]), 'has: ' + got.join(','));
});
// US English and contractions, in every mail and notice.
Object.keys(mail).forEach(function (k) {
    ok(k + ' is US English', !/colour|licence|recognise|organis|cancelled/i.test(mail[k]));
    ok(k + ' uses contractions', !/\b(do not|does not|did not|cannot|could not|is not|was not|were not|has not|have not|had not|will not|would not|should not)\b/i.test(mail[k]));
});

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
