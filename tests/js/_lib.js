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

/** Web/styles.css, the stylesheet as a real file. */
function stylesPath() {
    return path.join(ROOT, 'Web', 'styles.css');
}

/**
 * The stylesheet.
 *
 * It is a real file now (Web/styles.css), spliced into the served script by
 * ProfilesController.PublishStyles. Before that it was a 1,400-line template literal
 * inside injectStyles, and this function used to have to find it by shape and guard
 * against it being truncated by a stray backtick — the defect that shipped 1.5.2 and
 * 1.5.3-beta dead. None of that is reachable any more; a .css file has no host string
 * to terminate.
 *
 * `src` is still accepted and still honoured, for one reason: every harness takes the
 * source path as argv[2] so it can be pointed at an older checkout to prove it fails
 * there. In those builds the CSS really is inside the script, so when the argument
 * looks like the old shape it is parsed the old way. Drop this only when nothing needs
 * to bisect against a pre-1.5.9 build.
 */
function extractCss(src) {
    if (typeof src === 'string' && src.indexOf('injectStyles: function') !== -1) {
        const at = src.indexOf('injectStyles: function');
        const m = /style\.(innerHTML|textContent)\s*=\s*`/.exec(src.slice(at));
        if (m) {
            const open = at + m.index + m[0].length;
            const close = src.indexOf('`;', open);
            if (close === -1) throw new Error('extractCss: unterminated stylesheet literal');
            const css = src.slice(open, close);
            if (css.length < 1000) {
                throw new Error('extractCss: got only ' + css.length + ' characters — the '
                    + 'literal is probably being ended early by a stray backtick');
            }
            return css;
        }
        // Falls through: a current build has injectStyles but no literal to find.
    }

    const css = fs.readFileSync(stylesPath(), 'utf8');
    // THROWS on a miss for the same reason the old version did: a harness handed ''
    // still runs and still reports most of its assertions as passing.
    if (css.length < 1000) {
        throw new Error('extractCss: Web/styles.css is only ' + css.length + ' characters');
    }
    return css;
}

/**
 * The source of one named function declaration, brace-matched from its opening `{`.
 *
 * For running a single function out of a file that is otherwise one big IIFE, where
 * evaluating the whole thing would need the entire page and every global it touches.
 * THROWS on a miss, for the same reason extractCss does: a harness handed '' still runs
 * and still reports most of its assertions as passing.
 *
 * Brace counting, not a parser. It is fooled by a brace inside a string or a comment,
 * so the result is checked for balance by the caller evaluating it — a truncated
 * function is a syntax error, not a silent wrong answer.
 */
function extractFunction(src, name) {
    const re = new RegExp('function\\s+' + name + '\\s*\\(');
    const m = re.exec(src);
    if (!m) throw new Error('extractFunction: no function named ' + name);

    const open = src.indexOf('{', m.index);
    if (open === -1) throw new Error('extractFunction: no body for ' + name);

    let depth = 0;
    for (let i = open; i < src.length; i++) {
        if (src[i] === '{') depth++;
        else if (src[i] === '}') {
            depth--;
            if (depth === 0) return src.slice(m.index, i + 1);
        }
    }
    throw new Error('extractFunction: unterminated body for ' + name);
}

/**
 * The script as the browser actually receives it: profiles.js with the stylesheet
 * spliced in, exactly as ProfilesController.PublishStyles does it.
 *
 * Use this anywhere a harness asserts about "the shipped client" — evaluating the
 * startup path, or searching for a CSS selector. Reading Web/profiles.js alone stopped
 * being that when the stylesheet moved to its own file; four harnesses were searching
 * the script text for CSS rules and silently found none.
 *
 * The encoding mirrors the server's: JSON, so a quote or a backslash in the CSS is
 * data. If the two ever disagree the harnesses are testing a file nobody is served,
 * so tests/js/bundle.js checks this function against the C# one.
 */
function readClientBundle(src) {
    const js = typeof src === 'string' ? src : readProfiles();
    const marker = 'let BONFIRE_STYLES = ""; // __BONFIRE_STYLES__';
    if (js.indexOf(marker) === -1) return js;   // an older build, CSS still inline
    return js.replace(
        marker,
        'let BONFIRE_STYLES = ' + JSON.stringify(extractCss()) + '; // __BONFIRE_STYLES__');
}

/**
 * The script and the stylesheet as plain text, concatenated.
 *
 * For harnesses that SEARCH the source rather than run it. Do not use readClientBundle
 * for that: there the CSS is a JSON-encoded string literal, so a double quote inside a
 * selector is the two characters backslash-quote and a newline is backslash-n. A
 * search for `[aria-expanded="true"]` finds nothing, and reads as the rule being
 * missing rather than the search being wrong — which is exactly how three uitest
 * assertions failed on the day the stylesheet moved.
 *
 * Run it, use readClientBundle. Read it, use this.
 */
function readSourceAndStyles(src) {
    const js = typeof src === 'string' ? src : readProfiles();
    // An older checkout still has the CSS inline; appending it again would double
    // every rule and quietly break any "declared exactly once" assertion.
    if (js.indexOf('let BONFIRE_STYLES = ""; // __BONFIRE_STYLES__') === -1) return js;
    return js + '\n\n' + extractCss();
}

/** The inline <script> block of the dashboard page. */
function dashboardScript(html) {
    const m = html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/);
    if (!m) throw new Error('dashboardScript: no script block found');
    return m[1];
}

/**
 * Resolves a CSS value through the --jpf-* custom properties, so a check can assert
 * what a rule *paints* rather than how it is spelled.
 *
 * Written because P6 moved thirty white-alpha literals onto nine tokens and four
 * assertions went red without a single rendered pixel changing — they were matching
 * `rgba(0, 0, 0, 0.62)` against `var(--jpf-scrim)`. Spelling is the weaker question:
 * it passes for a token redefined to the wrong colour, and fails for a literal that
 * is exactly right. Resolving asks what the assertion meant to ask.
 *
 * Only the floor definitions count — the first `:root, body` block. A token
 * redefined under @supports or @media is deliberately ignored, because the floor is
 * what the oldest television renders and is the value worth pinning.
 */
function resolveCssValue(css, value, depth) {
    depth = depth || 0;
    if (depth > 8 || value.indexOf('var(') === -1) return value;

    const start = css.indexOf(':root, body {');
    const floor = start === -1 ? '' : css.slice(start, css.indexOf('}', start));

    const resolved = value.replace(
        /var\(\s*(--jpf-[a-z0-9-]+)\s*(?:,[^()]*)?\)/g,
        (whole, name) => {
            const m = floor.match(new RegExp(name + '\\s*:\\s*([^;]+);'));
            return m ? m[1].trim() : whole;
        });

    // No progress means the token is undefined; returning stops an infinite descent.
    return resolved === value ? resolved : resolveCssValue(css, resolved, depth + 1);
}

/** The alpha of an rgba() value, or 1 when it carries none. */
function alphaOf(value) {
    const m = /rgba?\([^)]*?,\s*([0-9.]+)\s*\)/.exec(value);
    return m ? parseFloat(m[1]) : 1;
}

module.exports = {
    ROOT,
    profilesPath, dashboardPath, i18nDir,
    readProfiles, readDashboard,
    extractCss, extractFunction, dashboardScript, stylesPath, readClientBundle, readSourceAndStyles,
    resolveCssValue, alphaOf
};
