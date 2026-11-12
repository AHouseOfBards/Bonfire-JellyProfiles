/*
 * Campground browser runtime. Loaded once by index.html, before every plugin script: since 0.10.0 as the
 * file /Campground/runtime.js, which every 0.10.0+ plugin's middleware answers; an older owner still inlines
 * it. The highest version any installed plugin carries wins. It can be missing (a security policy blocked it,
 * or the brake is on), so a plugin script checks window.Campground before it uses it. Edit it in
 * AHouseOfBards/Campground-Coordination/shared/web, never inside a plugin.
 *
 * window.Campground:
 *   version                      runtime version
 *   disabled / isDisabled(id)    emergency brake (?campground=off, ?campground=off:<id>, ?campground=on)
 *   register(id, info)           announce a plugin's browser side; returns false when it must not run
 *   plugins()                    registered plugins
 *   on(name, fn) / off / emit    event bus (route, theme, user, plus plugin events "<pluginId>.<name>")
 *   ready(fn)                    runs fn(apiClient) once Jellyfin's ApiClient exists
 *   api(path, options)           fetch against the server with auth headers and base URL; JSON in/out
 *   el(tag, attrs, children)     safe DOM builder (text only, never innerHTML)
 *   route()                      current route info
 *   safe(id, fn)                 runs fn, catching and logging errors with the plugin id
 *   patch(target, name, wrapper, id)    wrap target[name] (0.10.0); the only way a plugin wraps a global
 *   capture(target, name, fn, id)       fn(value) once, when target[name] appears (0.10.0)
 *   brake(id)                    pull the brake now for one plugin (or all, with no id); undoes its patches
 *   pagePolicy()                 Promise of this page's Content-Security-Policy, for the setup check
 */
(function (root) {
  'use strict';

  var VERSION = '0.13.0';
  if (root.Campground && root.Campground.version && compare(root.Campground.version, VERSION) >= 0) {
    return;
  }

  var STORAGE_KEY = 'campground.emergency';
  var listeners = {};
  var plugins = {};
  var readyQueue = [];
  var apiReady = false;

  function compare(a, b) {
    var pa = String(a).split('.');
    var pb = String(b).split('.');
    for (var i = 0; i < Math.max(pa.length, pb.length); i++) {
      var x = parseInt(pa[i] || '0', 10);
      var y = parseInt(pb[i] || '0', 10);
      if (x !== y) {
        return x > y ? 1 : -1;
      }
    }
    return 0;
  }

  function storageGet(k) {
    try { return root.localStorage.getItem(k); } catch (e) { return null; }
  }

  function storageSet(k, v) {
    try {
      if (v === null) { root.localStorage.removeItem(k); } else { root.localStorage.setItem(k, v); }
    } catch (e) { /* private mode */ }
  }

  // A link can carry a bad escape (%, %E0%A4%A). decodeURIComponent throws on those, so use the raw text.
  function decode(s) {
    try { return decodeURIComponent(s); } catch (e) { return s; }
  }

  // ── Emergency brake ───────────────────────────────────────────────
  function readEmergency() {
    var query = '';
    try {
      var loc = root.location || {};
      query = (loc.search || '') + '&' + ((loc.hash || '').split('?')[1] || '');
    } catch (e) { /* ignore */ }
    var m = /(?:^|[?&])campground=([^&#]*)/.exec(query);
    if (m) {
      var v = decode(m[1]);
      storageSet(STORAGE_KEY, v === 'on' ? null : v);
    }
    var stored = storageGet(STORAGE_KEY) || '';
    var state = { all: false, plugins: {} };
    if (stored === 'off') {
      state.all = true;
    } else if (stored.indexOf('off:') === 0) {
      stored.substring(4).split(',').forEach(function (id) { if (id) { state.plugins[id] = true; } });
    }
    return state;
  }

  var emergency = readEmergency();

  function isDisabled(id) {
    return emergency.all || !!(id && emergency.plugins[id]);
  }

  // ── Events ────────────────────────────────────────────────────────
  function on(name, fn) {
    (listeners[name] = listeners[name] || []).push(fn);
    return function () { off(name, fn); };
  }

  function off(name, fn) {
    var list = listeners[name];
    if (!list) { return; }
    var i = list.indexOf(fn);
    if (i !== -1) { list.splice(i, 1); }
  }

  function emit(name, detail) {
    (listeners[name] || []).slice().forEach(function (fn) {
      try { fn(detail); } catch (e) { log('event ' + name, e); }
    });
  }

  function log(where, e) {
    if (root.console && root.console.error) {
      root.console.error('[Campground] ' + where + ':', e);
    }
  }

  function safe(id, fn) {
    try { return fn(); } catch (e) { log(id, e); return undefined; }
  }

  // ── Plugins ───────────────────────────────────────────────────────
  function register(id, info) {
    if (isDisabled(id)) {
      return false;
    }
    plugins[id] = { id: id, version: (info && info.version) || '0', name: (info && info.name) || id };
    emit('plugin', plugins[id]);
    return true;
  }

  function listPlugins() {
    return Object.keys(plugins).map(function (k) { return plugins[k]; });
  }

  // ── ApiClient ─────────────────────────────────────────────────────
  function apiClient() {
    return root.ApiClient || (root.ServerConnections && root.ServerConnections.currentApiClient && root.ServerConnections.currentApiClient()) || null;
  }

  function ready(fn) {
    if (apiReady && apiClient()) {
      safe('ready', function () { fn(apiClient()); });
    } else {
      readyQueue.push(fn);
    }
  }

  function pollApi() {
    var tries = 0;
    var timer = root.setInterval(function () {
      tries++;
      var c = apiClient();
      if (c || tries > 600) {
        root.clearInterval(timer);
        if (!c) { return; }
        apiReady = true;
        var q = readyQueue.splice(0, readyQueue.length);
        q.forEach(function (fn) { safe('ready', function () { fn(c); }); });
        emit('user', { userId: safe('user', function () { return c.getCurrentUserId(); }) });
      }
    }, 100);
  }

  function api(path, options) {
    options = options || {};
    var c = apiClient();
    if (!c) {
      return Promise.reject(new Error('Jellyfin ApiClient is not ready'));
    }
    var url = c.getUrl(String(path).replace(/^\//, ''), options.query);
    var headers = { 'Accept': 'application/json' };
    var token = c.accessToken && c.accessToken();
    if (token) {
      headers.Authorization = 'MediaBrowser Token="' + token + '"';
    }
    var body = options.body;
    if (body !== undefined && typeof body !== 'string' && !(body instanceof root.FormData)) {
      body = JSON.stringify(body);
      headers['Content-Type'] = options.contentType || 'application/json';
    }
    if (options.headers) {
      Object.keys(options.headers).forEach(function (k) { headers[k] = options.headers[k]; });
    }
    return root.fetch(url, { method: options.method || (body === undefined ? 'GET' : 'POST'), headers: headers, body: body, credentials: 'same-origin' })
      .then(function (r) {
        if (!r.ok) {
          var err = new Error('HTTP ' + r.status);
          err.status = r.status;
          err.retryAfter = r.headers.get('Retry-After');
          throw err;
        }
        var type = r.headers.get('Content-Type') || '';
        return r.status === 204 ? null : (type.indexOf('json') !== -1 ? r.json() : r.text());
      });
  }

  // ── Safe DOM ──────────────────────────────────────────────────────
  var URL_ATTRS = ['href', 'src', 'action', 'formaction', 'xlink:href'];

  function el(tag, attrs, children) {
    var node = root.document.createElement(tag);
    if (attrs) {
      Object.keys(attrs).forEach(function (k) {
        var v = attrs[k];
        if (v === null || v === undefined || v === false) { return; }
        if (k === 'text') {
          node.textContent = String(v);
        } else if (k === 'class' || k === 'className') {
          node.className = String(v);
        } else if (k === 'style' && typeof v === 'object') {
          Object.keys(v).forEach(function (s) { node.style[s] = v[s]; });
        } else if (k.indexOf('on') === 0 && typeof v === 'function') {
          node.addEventListener(k.substring(2).toLowerCase(), v);
        } else if (k.toLowerCase().indexOf('on') === 0) {
          throw new Error('Campground.el: ' + k + ' must be a function');
        } else if (k === 'innerHTML' || k === 'outerHTML' || k === 'srcdoc') {
          throw new Error('Campground.el: ' + k + ' is not allowed; pass text or child nodes');
        } else if (URL_ATTRS.indexOf(k.toLowerCase()) !== -1 && /^javascript:/i.test(String(v).replace(/[\u0000- ]/g, ''))) {
          // Browsers drop tabs, newlines and leading controls from a URL before reading its scheme.
          throw new Error('Campground.el: javascript: URLs are not allowed');
        } else {
          node.setAttribute(k, v === true ? '' : String(v));
        }
      });
    }
    [].concat(children || []).forEach(function (c) {
      if (c === null || c === undefined || c === false) { return; }
      node.appendChild(typeof c === 'object' && c.nodeType ? c : root.document.createTextNode(String(c)));
    });
    return node;
  }

  // ── Route and theme ───────────────────────────────────────────────
  var lastRoute = null;

  function route() {
    var loc = root.location || {};
    var hash = loc.hash || '';
    var path = hash.indexOf('#') === 0 ? hash.substring(1) : (loc.pathname || '');
    var q = path.indexOf('?');
    var params = {};
    if (q !== -1) {
      path.substring(q + 1).split('&').forEach(function (p) {
        if (!p) { return; }
        var kv = p.split('=');
        params[decode(kv[0])] = decode(kv.slice(1).join('=') || '');
      });
      path = path.substring(0, q);
    }
    return { path: path.replace(/^!/, ''), params: params };
  }

  function checkRoute() {
    var r = route();
    var key = r.path + '?' + JSON.stringify(r.params);
    if (key !== lastRoute) {
      lastRoute = key;
      emit('route', r);
    }
  }

  function theme() {
    var d = root.document && root.document.documentElement;
    var bg = '';
    try { bg = root.getComputedStyle(root.document.body).backgroundColor; } catch (e) { /* no body yet */ }
    var m = /rgba?\((\d+),\s*(\d+),\s*(\d+)/.exec(bg || '');
    var dark = m ? (0.299 * m[1] + 0.587 * m[2] + 0.114 * m[3]) < 128 : true;
    return { dark: dark, className: d ? d.className : '' };
  }

  function watch() {
    root.addEventListener('hashchange', checkRoute);
    root.addEventListener('popstate', checkRoute);
    if (root.document) {
      root.document.addEventListener('viewshow', checkRoute);
      if (root.MutationObserver) {
        var lastDark = null;
        var mo = new root.MutationObserver(function () {
          var t = theme();
          if (t.dark !== lastDark) {
            lastDark = t.dark;
            emit('theme', t);
          }
        });
        var start = function () {
          mo.observe(root.document.documentElement, { attributes: true, attributeFilter: ['class', 'data-theme'] });
          var css = root.document.querySelector('link#cssTheme, link[data-theme]');
          if (css) { mo.observe(css, { attributes: true }); }
        };
        if (root.document.readyState === 'loading') {
          root.document.addEventListener('DOMContentLoaded', start);
        } else {
          start();
        }
      }
    }
    checkRoute();
  }

  // ── Wrapping globals (0.10.0, issue #20 M2) ──────────────────────
  // Campground plugins wrap history, fetch, XHR and the player only through patch(), because so do JavaScript
  // Injector, Jellyfin Enhanced and themes with scripts, and two careless wrappers lose each other's work.
  // patch() chains to whatever is there when it is called, wraps once per plugin per target, and can be undone
  // by the brake without breaking a wrapper someone else added on top.
  //
  // The records live on window.Campground, so a newer runtime taking over keeps them (an older one never does).
  var previous = root.Campground || {};
  var patches = Array.isArray(previous._patches) ? previous._patches : [];
  var captures = Array.isArray(previous._captures) ? previous._captures : [];

  function findPatch(target, name, id) {
    for (var i = 0; i < patches.length; i++) {
      var p = patches[i];
      if (p.active && p.target === target && p.name === name && p.id === id) { return p; }
    }
    return null;
  }

  function linkRecord(fn) {
    for (var i = 0; i < patches.length; i++) {
      if (patches[i].link === fn) { return patches[i]; }
    }
    return null;
  }

  // wrapper(original) returns the replacement, which calls original.apply(this, arguments) when it wants the
  // real thing. Returns a handle with remove(), or null when nothing was wrapped (braked, not a function, or
  // a property that can't be written).
  function patch(target, name, wrapper, id) {
    id = String(id || '');
    if (!target || (typeof target !== 'object' && typeof target !== 'function') || typeof wrapper !== 'function' || isDisabled(id)) {
      return null;
    }
    var existing = findPatch(target, name, id);
    if (existing) { return existing.handle; }

    var original;
    try { original = target[name]; } catch (e) { return null; }
    if (typeof original !== 'function') { return null; }

    var rec = { target: target, name: name, id: id, original: original, active: true, calls: 0, impl: null, link: null, handle: null };
    var next = function () {
      rec.calls++;
      return rec.original.apply(this, arguments);
    };
    var impl;
    try { impl = wrapper(next); } catch (e) { log('patch ' + id + ' ' + name, e); return null; }
    if (typeof impl !== 'function') { return null; }
    rec.impl = impl;

    // The link stays the same function for good. Once undone it only passes calls on, so a wrapper that a
    // third party put on top of it keeps working.
    rec.link = function () {
      if (!rec.active) { return rec.original.apply(this, arguments); }
      var before = rec.calls;
      try {
        return rec.impl.apply(this, arguments);
      } catch (e) {
        // Our wrapper broke before it reached the original: the page still gets the original, once.
        if (rec.calls !== before) { throw e; }
        log('patch ' + id + ' ' + name, e);
        return rec.original.apply(this, arguments);
      }
    };
    try { target[name] = rec.link; } catch (e) { return null; }
    if (target[name] !== rec.link) { return null; }

    rec.handle = { id: id, name: name, remove: function () { unpatch(rec); } };
    patches.push(rec);
    return rec.handle;
  }

  function unpatch(rec) {
    if (!rec.active) { return; }
    rec.active = false;
    // Put the original back only while ours is still the outermost. Otherwise someone wrapped on top of us and
    // holds our link, which now just passes calls through. Then unwind any of ours that are undone too.
    var current = rec.target[rec.name];
    var r = linkRecord(current);
    while (r && !r.active) {
      try { r.target[r.name] = r.original; } catch (e) { return; }
      r = linkRecord(r.original);
    }
  }

  // fn(value) runs once, when target[name] is first set to something (at once if it's already there). For
  // things that don't exist when scripts load: hls.js, a player. Once per plugin per target.
  function capture(target, name, fn, id) {
    id = String(id || '');
    if (!target || typeof fn !== 'function' || isDisabled(id)) { return false; }
    var cap = null;
    for (var i = 0; i < captures.length; i++) {
      if (captures[i].target === target && captures[i].name === name) { cap = captures[i]; break; }
    }
    if (!cap) {
      cap = { target: target, name: name, fired: {}, waiting: [], armed: false, timer: null, value: undefined, had: false };
      captures.push(cap);
    }
    if (cap.fired[id] || cap.waiting.some(function (w) { return w.id === id; })) { return true; }

    var value;
    try { value = target[name]; } catch (e) { value = undefined; }
    if (value !== undefined && value !== null) {
      cap.fired[id] = true;
      safe(id, function () { fn(value); });
      return true;
    }
    cap.waiting.push({ id: id, fn: fn });
    if (!cap.armed && !cap.timer) { arm(cap); }
    return true;
  }

  function arm(cap) {
    var desc = Object.getOwnPropertyDescriptor(cap.target, cap.name);
    // Someone else's getter or a fixed property: we can't stand in the way, so look every 250 ms instead.
    if (!desc || (desc.configurable && !desc.get && !desc.set)) {
      try {
        cap.had = !!desc;
        cap.value = desc ? desc.value : undefined;
        Object.defineProperty(cap.target, cap.name, {
          configurable: true,
          enumerable: desc ? desc.enumerable : true,
          get: function () { return cap.value; },
          set: function (v) {
            cap.value = v;
            if (v !== undefined && v !== null) { settle(cap, v); }
          }
        });
        cap.armed = true;
        return;
      } catch (e) { /* fall through to polling */ }
    }
    var tries = 0;
    cap.timer = root.setInterval(function () {
      var v;
      try { v = cap.target[cap.name]; } catch (e) { v = undefined; }
      if ((v !== undefined && v !== null) || ++tries > 1200) {
        root.clearInterval(cap.timer);
        cap.timer = null;
        if (v !== undefined && v !== null) { settle(cap, v); }
      }
    }, 250);
  }

  // Back to a plain property holding whatever is there now.
  function disarm(cap) {
    if (cap.timer) {
      root.clearInterval(cap.timer);
      cap.timer = null;
    }
    if (!cap.armed) { return; }
    cap.armed = false;
    if (cap.value === undefined && !cap.had) {
      delete cap.target[cap.name];
    } else {
      Object.defineProperty(cap.target, cap.name, { value: cap.value, writable: true, configurable: true, enumerable: true });
    }
  }

  function settle(cap, value) {
    disarm(cap);
    var waiting = cap.waiting.splice(0, cap.waiting.length);
    waiting.forEach(function (w) {
      if (cap.fired[w.id] || isDisabled(w.id)) { return; }
      cap.fired[w.id] = true;
      safe(w.id, function () { w.fn(value); });
    });
  }

  // The brake, pulled from code (a plugin that sees itself breaking the page, or an admin in the console).
  // Kept for this browser like ?campground=off until ?campground=on.
  function brake(id) {
    if (id) {
      emergency.plugins[id] = true;
    } else {
      emergency.all = true;
      if (runtime) { runtime.disabled = true; }
    }
    storageSet(STORAGE_KEY, emergency.all ? 'off' : 'off:' + Object.keys(emergency.plugins).join(','));
    // Newest first, so our own stacked links unwind back to the original.
    for (var i = patches.length - 1; i >= 0; i--) {
      if (patches[i].active && isDisabled(patches[i].id)) { unpatch(patches[i]); }
    }
    captures.forEach(function (cap) {
      cap.waiting = cap.waiting.filter(function (w) { return !isDisabled(w.id); });
      if (cap.waiting.length === 0) { disarm(cap); }
    });
    emit('brake', { id: id || null });
  }

  // This page's Content-Security-Policy: the header from loading the page again, plus any <meta> policy,
  // joined with commas as browsers join several headers. A dashboard sends it to the server's setup checks in
  // the X-Campground-Page-CSP header.
  function pagePolicy() {
    var policies = [];
    try {
      var metas = root.document.getElementsByTagName('meta');
      for (var i = 0; i < metas.length; i++) {
        if ((metas[i].getAttribute('http-equiv') || '').toLowerCase() === 'content-security-policy') {
          policies.push(metas[i].getAttribute('content') || '');
        }
      }
    } catch (e) { /* no document */ }
    var url = String((root.location && root.location.href) || '').split('#')[0];
    if (!root.fetch || !url) { return Promise.resolve(policies.join(', ')); }
    return root.fetch(url, { credentials: 'same-origin', cache: 'no-store' }).then(function (r) {
      var header = r.headers.get('Content-Security-Policy');
      return (header ? [header] : []).concat(policies).join(', ');
    }, function () { return policies.join(', '); });
  }

  var runtime = root.Campground = {
    version: VERSION,
    disabled: emergency.all,
    isDisabled: isDisabled,
    register: register,
    plugins: listPlugins,
    on: on,
    off: off,
    emit: emit,
    ready: ready,
    api: api,
    el: el,
    route: route,
    theme: theme,
    safe: safe,
    patch: patch,
    capture: capture,
    brake: brake,
    pagePolicy: pagePolicy,
    _patches: patches,
    _captures: captures,
    _compare: compare
  };

  if (!emergency.all) {
    watch();
    pollApi();
  } else if (root.console && root.console.warn) {
    root.console.warn('[Campground] disabled for this browser (?campground=on to re-enable).');
  }
})(typeof window !== 'undefined' ? window : this);
