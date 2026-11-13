/*
 * No lede or hint says again what its label says.
 *
 * The owner read the dashboard and the profile screen and found a sentence under nearly every
 * heading and control: "Optional.", "Off, it ...", "Otherwise ...", reassurance, and two clauses
 * where one fact would do. They were cut, and this is what keeps them cut:
 *
 *   - the removed ledes and hints are gone from the page and from every language file;
 *   - the shortened ones say what they now say, in English;
 *   - no hint under a control, on either screen, opens with one of the phrases he named;
 *   - the hints that protect somebody stay: a limit, a format, an irreversible effect.
 *
 * Every key is enumerated, because a count of "hints under ten words" passes with one key
 * wrong and another missed.
 *
 *     node tests/js/adminledes.js /path/to/old/profiles.js /path/to/old/profilesDashboard.html
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' — ' + detail : '')); }
}

const html = fs.readFileSync(L.dashboardPath(), 'utf8').replace(/\r\n/g, '\n');
const prof = fs.readFileSync(L.profilesPath(), 'utf8').replace(/\r\n/g, '\n');
const LANGS = ['ca', 'es', 'fr', 'pl', 'tr'];
const langs = {};
LANGS.forEach(function (l) { langs[l] = JSON.parse(fs.readFileSync(path.join(L.i18nDir(), l + '.json'), 'utf8')); });

// What the page says in English, markup and ADMIN_EN together.
const en = {};
(/const ADMIN_EN = \{([\s\S]*?)\n {12}\};/.exec(html) || [0, ''])[1].split('\n').forEach(function (line) {
    const m = /^\s*'([^']+)':\s*(?:'((?:[^'\\]|\\.)*)'|"((?:[^"\\]|\\.)*)")/.exec(line);
    if (m) { en[m[1]] = (m[2] !== undefined ? m[2] : m[3]).replace(/\\'/g, "'"); }
});
(html.match(/data-i18n="[^"]+"[^>]*>[^<]*</g) || []).forEach(function (x) {
    const m = /data-i18n="([^"]+)"[^>]*>([^<]*)</.exec(x);
    if (m) { en[m[1]] = m[2].replace(/&amp;/g, '&'); }
});
// The profile screen.
const screen = {};
prof.split('\n').forEach(function (line) {
    const m = /^\s*'([A-Za-z0-9.]+)':\s*(?:'((?:[^'\\]|\\.)*)'|"((?:[^"\\]|\\.)*)"),?\s*$/.exec(line);
    if (m) { screen[m[1]] = (m[2] !== undefined ? m[2] : m[3]).replace(/\\'/g, "'"); }
});

// ── Removed ──────────────────────────────────────────────────────────────────
const REMOVED = ['admin.households.lede', 'admin.devices.lede', 'admin.security.pinResetByEmailHint', 'admin.theme.allowProfilesHint',
    'admin.themes.builtInHint', 'admin.onboarding.lede', 'admin.switcher.askOnStartupHint', 'admin.apps.fromAnywhereHint',
    'admin.mnt.messageHint', 'admin.mnt.untilHint', 'admin.mnt.reopenHint', 'admin.mnt.stopHint', 'admin.script.lede'];
REMOVED.forEach(function (k) {
    ok(k + ' is gone from the page', !(k in en) && html.indexOf('"' + k + '"') < 0);
    LANGS.forEach(function (l) { ok(l + ' no longer carries ' + k, !(k in langs[l])); });
});

// ── Shortened ────────────────────────────────────────────────────────────────
const SHORT = {
    'admin.banner.restartBody': 'Until then the old version keeps running.',
    'admin.onboarding.askBody': 'New main accounts get a few screens to set a main PIN and make a profile.',
    'admin.households.maxProfilesHint': "1 to 20. A main account's own limit wins.",
    'admin.orphans.lede': "Left when a profile is deleted in Dashboard → Users. They still count against the main account's limit.",
    'admin.viewing.lede': 'Households can set their own.',
    'admin.panic.warnBody': 'Until Jellyfin restarts, signed-in devices skip the profile screen. A TV left on a main account opens its whole library.',
    'admin.panic.hint': "Blank turns it off. It can't be shown again, so write it down.",
    'admin.households.pinPerDayHint': 'Per account, all apps together. 1 to 20.',
    'admin.theme.lede': "Bonfire's screens only.",
    'admin.logo.lede': "The image, or the text if there's none.",
    'admin.logo.altHint': 'For screen readers. Up to 100 characters.',
    'admin.themes.lede': 'Hiding and fonts save at once.',
    'admin.themes.advancedHint': 'Lists are JSON.',
    'admin.onboarding.enableHint': "Existing main accounts won't see it.",
    'admin.switcher.lede': 'Defaults until a main account chooses.',
    'admin.avatars.lede': 'On a TV, this is the only way to set an avatar.',
    'admin.avatars.uploadHint': 'JPEG, PNG, WebP or GIF.',
    'admin.avatars.libraryOnlyHint': "Profiles can't upload their own.",
    'admin.avatars.qualityHint': 'More disk. Applies to new avatars.',
    'admin.email.lede': "For PIN reset codes and notices. Use your mail provider's SMTP settings.",
    'admin.email.usernameHint': 'Blank if the server needs none.',
    'admin.apps.lede': 'For apps like Android TV, Roku and Swiftfin. Set each to ask for sign-in every time, or a saved profile opens without its PIN.',
    'admin.apps.pinLoginHint': 'Type the PIN as the password. A profile with no PIN takes an empty box, on devices its household has used.',
    'admin.apps.profileListHint': 'Only on devices the household has signed in on. Changes the user list every client reads.',
    'admin.apps.skipQuickConnectHint': "Android TV only, after a household's first sign-in.",
    'admin.apps.pickerBeta': 'Beta. May change once clients support profiles themselves.',
    'admin.ann.lede': 'Shown on the profile screen. Apps that support Bonfire show them too.',
    'admin.ann.endsHint': 'Blank until you delete it.',
    'admin.mnt.lede': "Only administrators can use the server while it's closed.",
    'admin.audit.lede': 'The last 1,000 switches, kept for 90 days. Other events are in Dashboard → Activity.',
    'admin.status.whereBody': 'Households manage their own profiles from the Jellyfin home screen.',
    'admin.backup.lede': 'Export saves everything except the mail password and emergency code. Import replaces everything except those two. The file holds scrambled PINs, so keep it private.',
    'admin.avatars.missingHint': 'They show an initial instead. Set a new avatar to clear this.',
    'admin.links.note': 'Its tile goes on this household\'s profile screen. It stays its own account.'
};
Object.keys(SHORT).forEach(function (k) {
    ok(k, en[k] === SHORT[k], 'is: ' + JSON.stringify(en[k]));
    LANGS.forEach(function (l) { ok(l + ' ' + k + ' is translated', typeof langs[l][k] === 'string' && langs[l][k].length > 0 && langs[l][k] !== SHORT[k] || SHORT[k] === 'JPEG, PNG, WebP or GIF.'); });
});

const SCREEN = {
    'gate.manageHint': 'Choose a profile to edit.',
    'profileForm.noBackgroundHint': 'For cut-out avatars.',
    'profileForm.askPinToOpenHint': 'Settings always need the PIN.',
    'profileForm.blockedTagsHint': 'A tag on a series or library covers everything inside it.',
    'profileForm.kidsPresetHint': 'Sets the rating limit to 10+.',
    'onboarding.profileBody': 'Make one for someone else at home.',
    'switcher.intro': 'For everyone using your account.'
};
Object.keys(SCREEN).forEach(function (k) {
    ok(k, screen[k] === SCREEN[k], 'is: ' + JSON.stringify(screen[k]));
    LANGS.forEach(function (l) { ok(l + ' ' + k + ' is translated', typeof langs[l][k] === 'string' && langs[l][k] !== SCREEN[k]); });
});

// ── The shapes he named, under any control, on either screen ─────────────────
const SHAPES = [/^Optional\b/, /^Off[,:]/, /^When off,/, /^Otherwise\b/, /^Needs a time\b/, /^Nothing about\b/, /^Don't worry/i, /^Note:/i];
(html.match(/<div class="fieldDescription[^"]*"[^>]*data-i18n="[^"]+"[^>]*>[^<]*</g) || []).forEach(function (x) {
    const m = /data-i18n="([^"]+)"[^>]*>([^<]*)</.exec(x);
    SHAPES.forEach(function (re) { ok(m[1] + ' does not open with ' + re, !re.test(m[2]), m[2]); });
});
Object.keys(screen).filter(function (k) { return /(Hint|Body|intro)$/.test(k); }).forEach(function (k) {
    SHAPES.forEach(function (re) { ok(k + ' does not open with ' + re, !re.test(screen[k]), screen[k]); });
});

// ── What stays: a limit, a format, an irreversible effect, a requirement ─────
const KEPT = {
    'admin.security.requireMainPinHint': /can't change profiles or settings/,
    'admin.logo.imageHint': /PNG, JPEG or WebP, up to 1 MB/,
    'admin.email.securityHint': /465/,
    'admin.email.testHint': /once a test goes through/,
    'admin.avatars.starterHint': /MIT license/,
    'admin.avatars.serverFolderHint': /not on this computer/,
    'admin.ann.severityHint': /Warning and Critical stay/,
    'admin.audit.exportNote': /IP addresses/,
    'admin.report.lede': /No names, ids, addresses or PINs/,
    'admin.toProfile.note': /Its password stops working/,
    'admin.standalone.note': /can't be undone/,
    'admin.backup.confirm': /can't be undone/,
    'admin.audit.deleteConfirm': /can't be undone/,
    'admin.panic.clearConfirm': /Nobody will be able to switch Bonfire off/,
    'admin.apps.moonfinHint': /can't sign in directly on Moonfin/,
    'admin.status.openPinsOne': /anyone using its profiles can switch to it/
};
Object.keys(KEPT).forEach(function (k) { ok(k + ' is still there', KEPT[k].test(en[k] || ''), en[k]); });
ok('the Docker restart note is still there (markup inside, so read from the page)', /data-i18n="admin\.banner\.restartDocker"><strong>On Docker, restart the container\.<\/strong>/.test(html));
ok('profileForm.allowedTagsHint is still there', /only<\/strong> items with these tags/.test(screen['profileForm.allowedTagsHint'] || ''));
ok('profileForm.noPinSetHint is still there', /Anyone can open this profile/.test(screen['profileForm.noPinSetHint'] || ''));

console.log('\n' + pass + ' passed, ' + fails.length + ' failed');
process.exit(fails.length ? 1 : 0);
