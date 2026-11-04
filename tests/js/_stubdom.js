/*
 * A stub DOM for screens that draw themselves as one innerHTML and then wire handlers
 * by id and class. Enough to run the real render-and-bind code of a Bonfire page and
 * read back what it drew and what it sent; not a browser.
 *
 *  - querySelector('#id') finds the tag by id in the markup and returns one node per id
 *    for the life of that markup, with .checked and .value read from what was drawn
 *    (a select's value is its selected option, and it carries .options).
 *  - querySelectorAll('.cls') returns one node per tag carrying that class.
 *  - node.fire(type) runs its listeners; node.click() is fire('click').
 *
 * Used by accountsettings.js and profilebasics.js.
 */
'use strict';

function attrsOf(tag) {
    const a = {};
    tag.replace(/([a-zA-Z-]+)="([^"]*)"/g, (m, k, v) => { a[k] = v; return m; });
    return a;
}

function makeNode(attrs, extra) {
    const listeners = {};
    const node = Object.assign({
        attrs: attrs, style: {}, textContent: '', value: '', checked: false, disabled: false,
        classList: { add() {}, remove() {}, contains: () => false, toggle: () => false },
        getAttribute: k => (k in attrs ? attrs[k] : null),
        setAttribute: (k, v) => { attrs[k] = String(v); },
        removeAttribute: k => { delete attrs[k]; },
        hasAttribute: k => k in attrs,
        addEventListener: (type, fn) => { (listeners[type] = listeners[type] || []).push(fn); },
        fire: (type, ev) => (listeners[type] || []).forEach(fn => fn(Object.assign({
            preventDefault() {}, stopPropagation() {}, key: '', target: node
        }, ev || {}))),
        click() { node.fire('click'); },
        focus() {},
        closest: () => null,
        appendChild() {},
        querySelector: () => null,
        querySelectorAll: () => []
    }, extra || {});
    return node;
}

function makeContent() {
    let html = '';
    let byId = {};
    let byClass = {};
    const content = makeNode({});
    Object.defineProperty(content, 'innerHTML', {
        get: () => html,
        set: v => { html = v; byId = {}; byClass = {}; }
    });
    content.querySelector = sel => {
        if (sel.charAt(0) !== '#') return (content.querySelectorAll(sel)[0] || null);
        const id = sel.slice(1);
        if (!/^[\w-]+$/.test(id)) return null;
        if (byId[id]) return byId[id];
        const m = new RegExp('<[a-z]+[^>]*\\bid="' + id + '"[^>]*>').exec(html);
        if (!m) return null;
        const node = makeNode(attrsOf(m[0]), { id: id });
        node.checked = /\schecked(\s|>|\/)/.test(m[0]);
        if (/^<input/.test(m[0]) && node.attrs.value !== undefined) node.value = node.attrs.value;
        if (/^<select/.test(m[0])) {
            const body = html.slice(m.index, html.indexOf('</select>', m.index));
            const opts = [];
            body.replace(/<option value="([^"]*)"([^>]*)>/g, (all, v, rest) => {
                opts.push({ value: v, selected: /\bselected\b/.test(rest) });
                return all;
            });
            node.options = opts;
            const chosen = opts.filter(o => o.selected)[0];
            node.value = chosen ? chosen.value : (opts[0] ? opts[0].value : '');
        }
        return (byId[id] = node);
    };
    content.querySelectorAll = sel => {
        if (!/^\.[\w-]+$/.test(sel)) return [];
        const cls = sel.slice(1);
        if (byClass[cls]) return byClass[cls];
        const list = [];
        const re = new RegExp('<[a-z]+[^>]*class="[^"]*\\b' + cls + '\\b[^"]*"[^>]*>', 'g');
        let m;
        while ((m = re.exec(html))) {
            const at = m.index;
            const node = makeNode(attrsOf(m[0]));
            // A device row's name, for the merge picker.
            const nameM = /class="device-row-name"[^>]*>([^<]*)</.exec(html.slice(at, at + 1200));
            node.querySelector = s => (s === '.device-row-name' && nameM ? { textContent: nameM[1] } : null);
            list.push(node);
        }
        return (byClass[cls] = list);
    };
    return content;
}

function makeStorage() {
    const map = new Map();
    return {
        getItem: k => (map.has(k) ? map.get(k) : null),
        setItem: (k, v) => map.set(k, String(v)),
        removeItem: k => map.delete(k),
        clear: () => map.clear()
    };
}

/**
 * Loads profiles.js into a sandbox whose document draws into one stub content node,
 * with a fetch that records every request and answers through `respond(url, body)`.
 * Returns { PP, content, posts, setRespond, settle, sandbox }.
 */
function loadPlugin(src) {
    const vm = require('vm');
    const code = src.replace('ProfilesPlugin.init();', 'globalThis.__PP = ProfilesPlugin;');
    if (!code.includes('globalThis.__PP')) throw new Error('bootstrap line not found — harness out of date');

    const content = makeContent();
    const posts = [];
    let respond = () => ({ status: 200, body: {} });
    const sandbox = {
        console,
        localStorage: makeStorage(),
        sessionStorage: makeStorage(),
        setTimeout: () => 0,
        clearTimeout: () => {},
        setInterval: () => 0,
        requestAnimationFrame: () => 0,
        Event: function (type) { this.type = type; },
        MutationObserver: function () { this.observe = () => {}; this.disconnect = () => {}; },
        fetch: (url, init) => {
            const body = init && init.body ? JSON.parse(init.body) : undefined;
            posts.push({ url: String(url), body: body, init: init || {} });
            const r = respond(String(url), body) || { status: 200, body: {} };
            return Promise.resolve({
                ok: r.status >= 200 && r.status < 300,
                status: r.status,
                json: () => Promise.resolve(r.body),
                text: () => Promise.resolve(typeof r.body === 'string' ? r.body : JSON.stringify(r.body))
            });
        },
        document: {
            // Everything drawn here is on the page; a screen that checks before drawing
            // late (the account's Email section) draws.
            head: makeNode({}), body: makeNode({}, { contains: () => true }), documentElement: makeNode({}),
            createElement: () => makeNode({}),
            querySelector: sel => (sel === '.profiles-modal-content' ? content : null),
            querySelectorAll: () => [],
            getElementById: id => content.querySelector('#' + id),
            addEventListener: () => {},
            removeEventListener: () => {}
        },
        window: { location: { hash: '', pathname: '/web/' }, addEventListener: () => {} },
        history: { pushState: () => {}, replaceState: () => {} },
        ApiClient: {
            getUrl: p => p, getCurrentUserId: () => 'main', accessToken: () => 'tok',
            deviceId: () => 'dev', deviceName: () => 'Test', appName: () => 'Jellyfin Web', appVersion: () => '10.11'
        }
    };
    sandbox.globalThis = sandbox;
    sandbox.window.localStorage = sandbox.localStorage;
    vm.createContext(sandbox);
    vm.runInContext(code, sandbox);
    const PP = sandbox.__PP;
    if (!PP) throw new Error('profiles.js did not export ProfilesPlugin');
    return {
        PP, content, posts, sandbox,
        setRespond: fn => { respond = fn; },
        settle: async () => { for (let i = 0; i < 30; i++) await Promise.resolve(); }
    };
}

module.exports = { attrsOf, makeNode, makeContent, makeStorage, loadPlugin };
