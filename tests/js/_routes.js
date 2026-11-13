/*
 * The routes the controller exposes, and the authorisation each one enforces, read from the
 * source. Shared by apidocs.js (the Markdown reference) and openapi.js (the OpenAPI file), so
 * the two cannot end up judging a route by different rules.
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');

// The controller is split into partial files (ProfilesController.Email.cs, 1.7.2), and a
// route in a file this didn't read would be undocumented and unchecked without a word. So
// every ProfilesController*.cs is read, and below, any other file declaring routes fails.
// argv[2] points the harness at another checkout, to prove it goes red against one.
const ROOT = process.argv[2] ? path.resolve(process.argv[2]) : L.ROOT;
const CONTROLLERS = path.join(ROOT, 'Controllers');
const controllerFiles = fs.readdirSync(CONTROLLERS).filter(f => /^ProfilesController(\.\w+)?\.cs$/.test(f)).sort();
const cs = controllerFiles.map(f => fs.readFileSync(path.join(CONTROLLERS, f), 'utf8')).join('\n');

const PREFIX = '/plugins/profiles';

/* ── the routes the controller actually exposes ──────────────────────────────── */

/*
 * Read the attribute, then the method that follows it, then classify the body.
 *
 * Auth is hand-rolled per endpoint here, so it has to be read from the body rather
 * than from an attribute:
 *   admin      - checks Policy.IsAdministrator, or carries [Authorize] with the
 *                administrator policy
 *   user       - calls GetCurrentUserId() and returns 401 when it is null
 *   anonymous  - neither, and therefore public whether or not that was intended
 */
function routes() {
    const out = [];
    const re = /\[Http(Get|Post|Put|Delete|Patch)(?:\("([^"]*)"\))?\]/g;
    let m;
    while ((m = re.exec(cs)) !== null) {
        const method = m[1].toUpperCase();
        const tail = m[2] || '';

        /* Scan the WHOLE file from the attribute, not a fixed window. The first
         * version of this took a 6,000-character slice, which is shorter than
         * CreateProfile — so its body was truncated before the GetCurrentUserId call
         * and it was classified anonymous. Four routes were reported as public that
         * are not, which in a security table is the worst possible direction to be
         * wrong in. */
        const sigRe = /\n\s*public\s+(?:async\s+)?[^\n]*?\s(\w+)\s*\(/g;
        sigRe.lastIndex = m.index;
        const sig = sigRe.exec(cs);
        const name = sig ? sig[1] : '(unknown)';

        let level = 'anonymous';
        if (sig) {
            const open = cs.indexOf('{', sig.index + sig[0].length - 1);
            let depth = 0, end = cs.length;
            for (let i = open; i < cs.length; i++) {
                if (cs[i] === '{') depth++;
                else if (cs[i] === '}') { depth--; if (depth === 0) { end = i; break; } }
            }
            const body = cs.slice(open, end + 1);
            let attrs = cs.slice(Math.max(0, m.index - 400), m.index);
            // Only this route's own attributes: stop at the previous method's closing brace, so
            // a RequireAdministrator on the route above cannot count for this one (R-1282).
            attrs = attrs.slice(attrs.lastIndexOf('\n        }') + 1);
            // `.IsAdministrator`, a member access — not the bare word.
            //
            // This was /IsAdministrator/, which is the same substring trap that made
            // `.navMenu` "verify" against `navMenuOption` in the selector ledger. A route
            // that read an account's admin flag as DATA — to warn that its PIN opens an
            // account which can reach server settings — was reported as admin-only, because
            // the helper it called was named IsAdministratorAccount.
            //
            // Every real check in this codebase reaches the flag through a policy
            // (`policy.IsAdministrator`, `Policy?.IsAdministrator`), so requiring the dot
            // keeps all of them and drops identifiers that merely contain the word. The
            // documented admin routes are what prove it still detects them: loosen this and
            // they flip to "user" and this very assertion fails.
            // A CHECK, not the token. This matched any ".IsAdministrator", and two route
            // bodies wrote "targetPolicy.IsAdministrator = false" — data, the opposite of a
            // check — so /create and /switch were reported, and documented, as
            // administrator-only when any signed-in user can call them. A detector that cannot
            // tell data from a check will one day call a public route gated.
            if (/!\s*\w+\.Policy\.IsAdministrator\b/.test(body) || /RequireAdministrator\(/.test(attrs + body)) {
                level = 'admin';
            } else if (/GetCurrentUserId\(\)/.test(body) && /\bUnauthorized(Object)?\(|StatusCode\(401|Status401|\bRequireMainAccount\(/.test(body)) {
                // Reading the id is not a check: the route must also be able to answer 401.
                // RequireMainAccount is the one helper that does it for the caller (it returns
                // Unauthorized for a missing id and for a sub-profile).
                level = 'user';
            }
        }
        out.push({ method, path: PREFIX + (tail ? '/' + tail : ''), name, level });
    }
    return out;
}


module.exports = { ROOT, PREFIX, controllerFiles, routes: () => routes(), cs };
