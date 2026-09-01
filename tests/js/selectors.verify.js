/*
 * Re-check the selector ledger against jellyfin-web. Uses the network, so it is NOT part
 * of the pull-request run — selectors.test.js is the offline gate; this is the tool that
 * refreshes what that gate is checking against.
 *
 * Run it when bumping the supported Jellyfin version, or before adding a selector:
 *
 *   node tests/js/selectors.verify.js
 *   node tests/js/selectors.verify.js release-10.12.z
 *
 * For each "upstream" entry it fetches the exact file the ledger claims and confirms the
 * token is still in it. That is a precise check and costs one request per entry.
 *
 * It cannot prove a "dead" entry is still absent — that needs the whole tree — so those
 * are listed for a manual look rather than guessed at.
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');

const BRANCH = process.argv[2] || 'release-10.11.z';
const RAW = 'https://raw.githubusercontent.com/jellyfin/jellyfin-web/' + BRANCH + '/';
const LEDGER = path.join(L.ROOT, 'tests', 'upstream-selectors.json');

function get(url) {
    return fetch(url).then(r => (r.ok ? r.text() : Promise.resolve(null)))
        .catch(() => null);
}

/**
 * Class and id tokens worth grepping for, without their leading . or #.
 *
 * `:not(...)` arguments are stripped first. What sits inside one is what the selector
 * excludes, and here that is almost always an element of ours — `.headerButton:not(
 * #profiles-floating-bubble)` was reported as broken upstream because jellyfin-web quite
 * correctly contains no id of ours.
 */
function tokens(selector) {
    const matchable = selector.replace(/:not\([^)]*\)/g, '');
    return [...new Set((matchable.match(/[.#]([A-Za-z][\w-]*)/g) || [])
        .map(t => t.slice(1)))];
}

/**
 * True when `token` appears in `body` as a complete class or id, not merely as a
 * substring of a longer one.
 *
 * This was `body.indexOf(token) === -1`, and it reported `.navMenu` as present in
 * jellyfin-web because `navMenuOption` contains those seven characters. There is no bare
 * `navMenu` class in 10.11 — only `navMenuOption`, `navMenuOptionText`,
 * `navMenuOptionIcon` and `navMenuOption-selected` — so `injectSidebarLink` had a
 * selector recorded as verified that has never matched anything.
 *
 * A class name ends at any character that cannot appear in one, so requiring
 * non-`[\w-]` on both sides is the whole fix. It is the same failure the rest of this
 * repository keeps finding: the check answered a coarser question ("do these characters
 * occur") than the one anybody cared about ("does this selector match").
 */
function hasToken(body, token) {
    const escaped = token.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    return new RegExp('(?<![\\w-])' + escaped + '(?![\\w-])').test(body);
}

(async () => {
    const ledger = JSON.parse(fs.readFileSync(LEDGER, 'utf8'));
    const upstream = ledger.selectors.filter(e => e.status === 'upstream');
    const dead = ledger.selectors.filter(e => e.status === 'dead');

    console.log('Verifying ' + upstream.length + ' upstream selectors against '
        + 'jellyfin/jellyfin-web@' + BRANCH);
    console.log('');

    const broken = [];
    const unreachable = [];

    for (const e of upstream) {
        const body = await get(RAW + e.source);
        if (body === null) {
            unreachable.push(e);
            console.log('  ?  ' + e.selector.padEnd(30) + ' could not fetch ' + e.source);
            continue;
        }
        const missing = tokens(e.selector).filter(t => !hasToken(body, t));
        if (missing.length) {
            broken.push({ e, missing });
            console.log('  X  ' + e.selector.padEnd(30) + ' no longer in ' + e.source
                + '   (missing: ' + missing.join(', ') + ')');
        } else {
            console.log('  ok ' + e.selector.padEnd(30) + e.source);
        }
    }

    if (dead.length) {
        console.log('');
        console.log('Still recorded as dead — absence cannot be proved from single files,');
        console.log('so confirm these by searching the tree if you are changing them:');
        dead.forEach(e => console.log('    ' + e.selector.padEnd(30)
            + (e.trackedBy ? '(' + e.trackedBy + ')' : '')));
    }

    console.log('');
    if (broken.length) {
        console.log(broken.length + ' selector(s) have moved or been removed upstream.');
        console.log('Read the component, fix the code, then update the ledger — do not just');
        console.log('repoint the ledger at whatever file still happens to contain the string.');
        process.exit(1);
    }
    if (unreachable.length) {
        console.log(unreachable.length + ' file(s) could not be fetched. Network, or the file '
            + 'moved in ' + BRANCH + '.');
        process.exit(1);
    }
    console.log('All ' + upstream.length + ' upstream selectors still present in ' + BRANCH + '.');
    console.log('Update verifiedAgainst/verifiedOn in the ledger if you are recording this run.');
})();
