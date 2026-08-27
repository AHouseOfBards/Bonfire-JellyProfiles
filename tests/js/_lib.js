/*
 * Shared plumbing for the JS harnesses.
 *
 * Everything here exists because the same detail was hardcoded in a dozen files and
 * broke a dozen times at once:
 *
 *  - Six harnesses had "d:/JellyfinProfiles/..." baked in, which is nobody's path but
 *    one machine's, and fails on CI immediately.
 *  - Five resolved "Web/profiles.js" against the current directory, so they only ran
 *    from the repository root.
 *  - Four located the injected stylesheet by searching for the literal
 *    `style.innerHTML = ` + backtick. 1.5.4 changed that one word to textContent and
 *    all four stopped finding any CSS — silently, since a miss reads as an empty sheet
 *    rather than an error.
 *
 * Resolve paths from __dirname, and locate things by shape rather than by a literal
 * that is free to change.
 */
'use strict';

const fs = require('fs');
const path = require('path');

/** Repository root, from this file's location — not from the current directory. */
const ROOT = path.resolve(__dirname, '..', '..');

/**
 * The client script under test.
 *
 * argv[2] overrides it so any harness can be pointed at another checkout — which is how
 * a new harness is shown to FAIL against the build carrying the bug before it is allowed
 * to pass. Keep that convention when adding one.
 */
function profilesPath() {
    return process.argv[2] || path.join(ROOT, 'Web', 'profiles.js');
}

function dashboardPath() {
    return process.argv[3] || path.join(ROOT, 'Web', 'profilesDashboard.html');
}

function i18nDir() {
    return path.join(ROOT, 'Web', 'i18n');
}

function readProfiles() {
    return fs.readFileSync(profilesPath(), 'utf8');
}

function readDashboard() {
    return fs.readFileSync(dashboardPath(), 'utf8');
}

/**
 * The injected stylesheet, as text.
 *
 * Found by matching the assignment inside injectStyles rather than a fixed property
 * name, and it THROWS on a miss. A harness that silently receives '' still runs and
 * still reports most of its assertions as passing, which is how a one-word change went
 * unnoticed across four files.
 */
function extractCss(src) {
    const at = src.indexOf('injectStyles: function');
    if (at === -1) throw new Error('extractCss: injectStyles not found in the source');

    const m = /style\.(innerHTML|textContent)\s*=\s*`/.exec(src.slice(at));
    if (!m) throw new Error('extractCss: no stylesheet assignment found inside injectStyles');

    const open = at + m.index + m[0].length;
    const close = src.indexOf('`;', open);
    if (close === -1) throw new Error('extractCss: unterminated stylesheet literal');

    const css = src.slice(open, close);
    if (css.length < 1000) {
        throw new Error('extractCss: got only ' + css.length + ' characters — the literal '
            + 'is probably being ended early by a stray backtick');
    }
    return css;
}

module.exports = {
    ROOT,
    profilesPath, dashboardPath, i18nDir,
    readProfiles, readDashboard,
    extractCss
};
