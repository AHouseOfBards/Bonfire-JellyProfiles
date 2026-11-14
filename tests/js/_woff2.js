/*
 * Just enough of WOFF2 to ask a font file what it can draw: the characters in its cmap,
 * its family name, and the weights a variable font spans.
 *
 * WOFF2 is a header, a table directory, and every table run through one Brotli stream.
 * Node has Brotli, and cmap, name and fvar are never transformed (only glyf, loca and
 * hmtx are), so after decompressing they sit in the stream as plain OpenType tables, one
 * after another in directory order. https://www.w3.org/TR/WOFF2/
 *
 * THROWS on anything it does not understand. A coverage check handed an empty character
 * set reports every character missing, which is loud; handed a parse that went wrong, it
 * could quietly report every character present.
 */
'use strict';

const zlib = require('zlib');

const KNOWN_TAGS = ['cmap', 'head', 'hhea', 'hmtx', 'maxp', 'name', 'OS/2', 'post', 'cvt ', 'fpgm', 'glyf', 'loca',
    'prep', 'CFF ', 'VORG', 'EBDT', 'EBLC', 'gasp', 'hdmx', 'kern', 'LTSH', 'PCLT', 'VDMX', 'vhea', 'vmtx', 'BASE',
    'GDEF', 'GPOS', 'GSUB', 'EBSC', 'JSTF', 'MATH', 'CBDT', 'CBLC', 'COLR', 'CPAL', 'SVG ', 'sbix', 'acnt', 'avar',
    'bdat', 'bloc', 'bsln', 'cvar', 'fdsc', 'feat', 'fmtx', 'fvar', 'gvar', 'hsty', 'just', 'lcar', 'mort', 'morx',
    'opbd', 'prop', 'trak', 'Zapf', 'Silf', 'Glat', 'Gloc', 'Feat', 'Sill'];

function base128(buf, at) {
    let value = 0;
    for (let i = 0; i < 5; i++) {
        const b = buf[at.i++];
        if (i === 0 && b === 0x80) throw new Error('woff2: leading zero in UIntBase128');
        value = value * 128 + (b & 0x7f);
        if ((b & 0x80) === 0) return value;
    }
    throw new Error('woff2: UIntBase128 longer than 5 bytes');
}

/** { tag: Buffer } for every table, glyf/loca/hmtx in their transformed form. */
function tables(file) {
    if (file.toString('ascii', 0, 4) !== 'wOF2') throw new Error('woff2: not a WOFF2 file');
    const numTables = file.readUInt16BE(12);
    const compressed = file.readUInt32BE(20);
    if (file.readUInt32BE(4) === 0x74746366) throw new Error('woff2: font collections are not read here');
    const at = { i: 48 };
    const dir = [];
    for (let n = 0; n < numTables; n++) {
        const flags = file[at.i++];
        const tag = (flags & 0x3f) === 63 ? file.toString('latin1', at.i, (at.i += 4)) : KNOWN_TAGS[flags & 0x3f];
        if (!tag) throw new Error('woff2: unknown tag index ' + (flags & 0x3f));
        const version = flags >> 6;
        const orig = base128(file, at);
        const transformed = tag === 'glyf' || tag === 'loca' ? version !== 3 : version !== 0;
        const length = transformed ? base128(file, at) : orig;
        dir.push({ tag, length });
    }
    const stream = zlib.brotliDecompressSync(file.subarray(at.i, at.i + compressed));
    const out = {};
    let offset = 0;
    dir.forEach(t => { out[t.tag] = stream.subarray(offset, offset + t.length); offset += t.length; });
    if (offset !== stream.length) throw new Error('woff2: tables do not fill the stream (' + offset + ' of ' + stream.length + ')');
    return out;
}

/** Every code point the cmap maps to a glyph other than .notdef. */
function codepoints(cmap) {
    const count = cmap.readUInt16BE(2);
    const subtables = [];
    for (let i = 0; i < count; i++) {
        const p = cmap.readUInt16BE(4 + i * 8), e = cmap.readUInt16BE(6 + i * 8), off = cmap.readUInt32BE(8 + i * 8);
        subtables.push({ p, e, off, format: cmap.readUInt16BE(off) });
    }
    const set = new Set();
    // Unicode full repertoire first (format 12), then the BMP (format 4).
    const full = subtables.find(s => s.format === 12 && ((s.p === 3 && s.e === 10) || s.p === 0));
    if (full) {
        const groups = cmap.readUInt32BE(full.off + 12);
        for (let g = 0; g < groups; g++) {
            const at = full.off + 16 + g * 12;
            const start = cmap.readUInt32BE(at), end = cmap.readUInt32BE(at + 4), glyph = cmap.readUInt32BE(at + 8);
            for (let c = start; c <= end; c++) if (glyph + (c - start) !== 0) set.add(c);
        }
        return set;
    }
    const bmp = subtables.find(s => s.format === 4 && ((s.p === 3 && s.e === 1) || s.p === 0));
    if (!bmp) throw new Error('woff2: no Unicode cmap subtable');
    const o = bmp.off;
    const segs = cmap.readUInt16BE(o + 6) / 2;
    const ends = o + 14, starts = ends + segs * 2 + 2, deltas = starts + segs * 2, ranges = deltas + segs * 2;
    for (let s = 0; s < segs; s++) {
        const end = cmap.readUInt16BE(ends + s * 2), start = cmap.readUInt16BE(starts + s * 2);
        const delta = cmap.readInt16BE(deltas + s * 2), rangeOffset = cmap.readUInt16BE(ranges + s * 2);
        for (let c = start; c <= end && c !== 0xffff; c++) {
            let glyph;
            if (rangeOffset === 0) glyph = (c + delta) & 0xffff;
            else {
                const g = cmap.readUInt16BE(ranges + s * 2 + rangeOffset + (c - start) * 2);
                glyph = g === 0 ? 0 : (g + delta) & 0xffff;
            }
            if (glyph !== 0) set.add(c);
        }
    }
    return set;
}

/** The typographic family (name 16), else the family (name 1), from the Windows records. */
function family(name) {
    const count = name.readUInt16BE(2), strings = name.readUInt16BE(4);
    const found = {};
    for (let i = 0; i < count; i++) {
        const r = 6 + i * 12;
        const platform = name.readUInt16BE(r), id = name.readUInt16BE(r + 6);
        if (platform !== 3 || (id !== 1 && id !== 16)) continue;
        const len = name.readUInt16BE(r + 8), off = name.readUInt16BE(r + 10);
        const raw = name.subarray(strings + off, strings + off + len);
        let s = '';
        for (let k = 0; k + 1 < raw.length; k += 2) s += String.fromCharCode(raw.readUInt16BE(k));
        found[id] = s;
    }
    return found[16] || found[1] || null;
}

/** { min, max } of the wght axis, or null for a font that is not variable in weight. */
function weightAxis(fvar) {
    if (!fvar) return null;
    const axesOffset = fvar.readUInt16BE(4), axisCount = fvar.readUInt16BE(8), axisSize = fvar.readUInt16BE(10);
    for (let i = 0; i < axisCount; i++) {
        const a = axesOffset + i * axisSize;
        if (fvar.toString('latin1', a, a + 4) === 'wght') {
            return { min: fvar.readInt32BE(a + 4) / 65536, max: fvar.readInt32BE(a + 12) / 65536 };
        }
    }
    return null;
}

/** Everything a harness needs from one .woff2 file. */
function read(buffer) {
    const t = tables(buffer);
    if (!t.cmap || !t.name) throw new Error('woff2: no cmap or name table');
    return { codepoints: codepoints(t.cmap), family: family(t.name), weight: weightAxis(t.fvar) };
}

/** A CSS unicode-range ("U+0000-00FF,U+0131") as a test of one code point. */
function inRange(range) {
    const parts = String(range).split(',').map(p => p.trim()).filter(Boolean).map(p => {
        const m = /^U\+([0-9A-Fa-f]+)(?:-([0-9A-Fa-f]+))?$/.exec(p);
        if (!m) throw new Error('woff2: bad unicode-range part ' + p);
        return [parseInt(m[1], 16), parseInt(m[2] || m[1], 16)];
    });
    return c => parts.some(([a, b]) => c >= a && c <= b);
}

module.exports = { read, inRange };
