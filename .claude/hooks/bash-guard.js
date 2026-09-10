#!/usr/bin/env node
/**
 * Two rules this repository keeps breaking, moved out of CLAUDE.md and into the harness.
 *
 * Runs as a PreToolUse hook on Bash. Reads the hook payload on stdin and prints a
 * permissionDecision, or nothing at all to allow the command through.
 *
 *   1. NO HEREDOCS. CLAUDE.md's first rule, with eleven recorded cases of it silently
 *      producing broken or misplaced code. It kept happening anyway, because a rule in a
 *      document is advice competing with whatever is being concentrated on. On 2026-09-10
 *      a heredoc mangled `\\n` into a real newline inside a JSON string, shipped an
 *      unparseable manifest.json, and failed a release. A hook is not advice.
 *
 *   2. NO PUSHING A BROKEN MANIFEST. tests/js/manifest.js already caught that same
 *      failure and CI went red — but only after the push, because the version bump and the
 *      commit happened in one command with no test run between them. The gate existed;
 *      nothing ran it at the moment it mattered. This is the cheapest check at the last
 *      possible moment.
 *
 * Written in Node rather than as a shell script with jq, because jq is not installed here
 * — found by pipe-testing the first draft, which is the only reason this works at all.
 * Node is what the entire test suite runs on, so it is as safe a dependency as exists in
 * this repo.
 *
 * Anything unexpected exits silently and allows the command. A guard that blocks work
 * because it cannot parse its own input would be turned off within the hour, and then
 * neither rule would be enforced at all.
 */
'use strict';

const { execFileSync } = require('child_process');
const fs = require('fs');
const path = require('path');

function allow() { process.exit(0); }

function deny(reason) {
    process.stdout.write(JSON.stringify({
        hookSpecificOutput: {
            hookEventName: 'PreToolUse',
            permissionDecision: 'deny',
            permissionDecisionReason: reason,
        },
    }));
    process.exit(0);
}

let raw = '';
try {
    raw = fs.readFileSync(0, 'utf8');
} catch (e) {
    allow();
}

let command = '';
try {
    command = (JSON.parse(raw).tool_input || {}).command || '';
} catch (e) {
    allow();
}

// ── 1. heredocs ─────────────────────────────────────────────────────────────
//
// `<<` or `<<-` followed by a delimiter WORD, which is what makes it a heredoc.
//
// The first draft was `<<-?[^<]` and had two false positives, both caught by the test
// beside this file rather than by reading it:
//
//   `jq . <<< "{}"`    a here-string. It failed the leading `<` check at offset 0 and then
//                      matched at offset 1. Hence the lookbehind.
//   `$((1 << 3))`      an arithmetic left shift. `<<` followed by a space and a digit.
//                      Hence requiring a letter, underscore or quote after the operator.
//
// A here-string is a single line, cannot span content, and has never caused any of this.
//
// Matched on raw text: deciding whether an operator sits inside a quoted string needs a
// real shell parser, and the trade is one unnecessary rewrite against one corrupted file.
if (/(?<!<)<<-?\s*['"]?[A-Za-z_]/.test(command) || /(?<!<)<<-?\s*$/.test(command)) {
    deny(
        'Heredocs are blocked in this repository.\n\n'
        + 'CLAUDE.md rule 1, with eleven recorded cases of silent corruption — most '
        + 'recently an unparseable manifest.json that failed a release, because the shell '
        + 'turned \\n inside a JSON string into a real newline.\n\n'
        + 'Instead: write the script to a file in the scratchpad with the Write tool, then '
        + 'run that file. For a commit message use `git commit -F <file>`.\n\n'
        + 'A here-string (<<<) is fine and is not blocked.'
    );
}

// ── 2. pushing a manifest that does not parse ───────────────────────────────
if (!/\bgit\s+push\b/.test(command)) allow();

const repo = process.env.CLAUDE_PROJECT_DIR || process.cwd();
const harness = path.join(repo, 'tests', 'js', 'manifest.js');
if (!fs.existsSync(harness)) allow();

try {
    execFileSync(process.execPath, [harness], { cwd: repo, stdio: 'pipe' });
} catch (err) {
    const output = String((err.stdout || '') + (err.stderr || ''));
    const detail = output
        .split('\n')
        .filter(l => /FAIL|^\s+- |Error/.test(l))
        .slice(0, 8)
        .join('\n');

    deny(
        'tests/js/manifest.js fails, so this push would tag a manifest the release '
        + 'workflow will refuse — and it refuses at the step that stamps the checksum, '
        + 'after the tag already exists.\n\n'
        + (detail || output.slice(0, 600))
        + '\n\nFix the manifest, re-run `node tests/js/manifest.js`, then push.'
    );
}

allow();
