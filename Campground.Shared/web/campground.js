/*
 * Campground browser runtime. Inlined once into index.html by the Campground injection coordinator
 * (the highest version any installed plugin carries wins). Edit it in
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
 */
(function (root) {
  'use strict';

  var VERSION = '0.9.0';
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

  // ── Emergency brake ───────────────────────────────────────────────
  function readEmergency() {
    var query = '';
    try {
      var loc = root.location || {};
      query = (loc.search || '') + '&' + ((loc.hash || '').split('?')[1] || '');
    } catch (e) { /* ignore */ }
    var m = /(?:^|[?&])campground=([^&#]*)/.exec(query);
    if (m) {
      var v = decodeURIComponent(m[1]);
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
        } else if (k === 'innerHTML' || k === 'outerHTML') {
          throw new Error('Campground.el: ' + k + ' is not allowed; pass text or child nodes');
        } else if ((k === 'href' || k === 'src' || k === 'action') && /^\s*javascript:/i.test(String(v))) {
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
        params[decodeURIComponent(kv[0])] = decodeURIComponent(kv.slice(1).join('=') || '');
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

  root.Campground = {
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
    _compare: compare
  };

  if (!emergency.all) {
    watch();
    pollApi();
  } else if (root.console && root.console.warn) {
    root.console.warn('[Campground] disabled for this browser (?campground=on to re-enable).');
  }
})(typeof window !== 'undefined' ? window : this);
