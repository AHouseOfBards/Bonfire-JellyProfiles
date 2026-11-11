/*
 * Mappings (and the other configuration lists) are never walked outside ConfigLock.
 *
 * `var config = Plugin.Instance?.Configuration;` hands back the live object. Another
 * request adds or removes a row under ConfigLock while this one is inside Where / Any /
 * FirstOrDefault, and List<T> throws "Collection was modified" (or, worse, answers from a
 * half-edited list). Read inside the lock, through FindMapping / CountProfiles, or from a
 * ReadConfigSnapshot() copy. This is the R-1139 class (R-1139, R-1152, R-1160).
 *
 * The check is a survey of the source, read line by line with a brace counter:
 *   - a local declared from Plugin.Instance?.Configuration is "live"; one declared from a
 *     snapshot or copy is not, and neither is a parameter (its caller decides);
 *   - a line that names Mappings / KnownDevices / BonfireGroups / AvatarLibrary on a live
 *     local, or straight off Plugin.Instance, must sit inside a `lock (ConfigLock)` block.
 *
 * Each violation is listed by file and line; nothing is counted into a total that could
 * hide which member failed. argv[2] is the Controllers directory, so the check can be
 * pointed at an older checkout to show it goes red.
 */
'use strict';

const fs = require('fs');
const path = require('path');
const { ROOT } = require('./_lib.js');

const LISTS = '(Mappings|KnownDevices|BonfireGroups|AvatarLibrary)';

function scan(dir) {
    const out = [];
    for (const f of fs.readdirSync(dir).filter(f => f.endsWith('.cs'))) {
        const lines = fs.readFileSync(path.join(dir, f), 'utf8').split(/\r?\n/);
        let depth = 0;
        const lockDepths = [];
        let pendingLock = false;
        const origin = {};
        for (let i = 0; i < lines.length; i++) {
            const l = lines[i].replace(/\/\/.*$/, '');
            const pm = /PluginConfiguration\??\s+(\w+)\s*[,)=]/.exec(l);
            const d = /\b(?:var|PluginConfiguration\??)\s+(\w+)\s*=\s*(.*)$/.exec(l);
            if (d) {
                origin[d[1]] = /Snapshot|CopyFor/.test(d[2]) ? 'snap'
                    : /Plugin\.Instance\??\.Configuration\s*;/.test(d[2]) ? 'live' : 'other';
            } else if (pm) {
                origin[pm[1]] = 'param';
            }
            if (/\block\s*\(\s*(ProfilesBaseController\.)?ConfigLock\s*\)/.test(l)) pendingLock = true;
            const oneLine = /lock\s*\(.*ConfigLock\s*\)\s*\{.*\}/.test(l);
            for (const ch of l) {
                if (ch === '{') { depth++; if (pendingLock) { lockDepths.push(depth); pendingLock = false; } }
                else if (ch === '}') { if (lockDepths.length && lockDepths[lockDepths.length - 1] === depth) lockDepths.pop(); depth--; }
            }
            const inLock = lockDepths.length > 0 || oneLine || pendingLock;
            if (inLock) continue;
            const re = new RegExp('\\b(\\w+)\\??\\.' + LISTS + '\\b', 'g');
            let m;
            while ((m = re.exec(l))) {
                if (origin[m[1]] === 'live') out.push(f + ':' + (i + 1) + '  ' + m[1] + '.' + m[2] + '  ' + lines[i].trim().slice(0, 80));
            }
            if (new RegExp('Plugin\\.Instance\\??\\.Configuration\\??\\.' + LISTS).test(l)) {
                out.push(f + ':' + (i + 1) + '  Plugin.Instance...  ' + lines[i].trim().slice(0, 80));
            }
        }
    }
    return out;
}

module.exports = { scan };

if (require.main === module) {
    const dir = process.argv[2] || path.join(ROOT, 'Controllers');
    const found = scan(dir);
    let failed = 0;
    for (const v of found) { console.log('FAIL unlocked list read: ' + v); failed++; }
    // The detector must be able to go red: a tiny source with the bad shape.
    const tmp = fs.mkdtempSync(path.join(require('os').tmpdir(), 'mapl-'));
    fs.writeFileSync(path.join(tmp, 'X.cs'), 'void A() {\n var config = Plugin.Instance?.Configuration;\n var r = config.Mappings.Count();\n}\n'
        + 'void B() {\n var config = Plugin.Instance?.Configuration;\n lock (ConfigLock)\n {\n var r = config.Mappings.Count();\n }\n}\n');
    const probe = scan(tmp);
    fs.rmSync(tmp, { recursive: true, force: true });
    if (probe.length !== 1 || !/X\.cs:3/.test(probe[0])) { console.log('FAIL detector probe: ' + JSON.stringify(probe)); failed++; }
    console.log(failed ? failed + ' failed' : 'mappingslock: ok (' + 1 + ' probe, no unlocked reads)');
    process.exit(failed ? 1 : 0);
}
