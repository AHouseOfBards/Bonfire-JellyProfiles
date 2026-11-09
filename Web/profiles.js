(function () {
    'use strict';

    // Told to the inline head script, which is the only other piece of the plugin that
    // runs on a page load. This tag is deferred, so it has finished evaluating before
    // DOMContentLoaded — meaning the absence of this flag at that point is proof the
    // script never loaded, and the head script can stop holding the page hidden for
    // something that is not coming.
    try { window.__jpLoaded = 1; } catch (e) { /* no window: reading the file, not running it */ }

    function escapeHtml(str) {
        if (!str) return '';
        return String(str)
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#039;");
    }

    // Avatar colours and profile images are stored server-side and — through Bonfire
    // groups — rendered on *other* accounts' switcher screens. Both are validated on the
    // server, but they are re-validated here so a value predating that validation (or
    // written directly to PluginConfiguration.xml) can never break out of its attribute.

    const HEX_COLOR_RE = /^#[0-9a-fA-F]{6}$/;
    const DEFAULT_AVATAR_COLOR = '#00A4DC';
    /// The colours the profile form offers, in order. ProfilesBaseController.AvatarPalette
    /// is the same list, and picks from it the same way for a profile made by another app.
    const AVATAR_PALETTE = [
        '#00A4DC', '#E50914', '#22C55E', '#EAB308', '#A855F7', '#EC4899',
        '#F97316', '#06B6D4', '#3B82F6', '#10B981', '#6366F1', '#8B5CF6',
        '#D946EF', '#F43F5E', '#14B8A6', '#F59E0B', '#84CC16', '#64748B'
    ];

    /// The looks Bonfire's own screens can take. Must match Themes.All on the server and
    /// the files in Web/themes/; tests/js/themes.js and tests/cs/themes hold the three
    /// together. A theme's name is the string theme.<id> (themeName), so "High contrast"
    /// can be translated; the proper names (Bonfire, Harvest...) are kept as they are in
    /// every language file, so the dashboard and every profile call a theme the same thing.
    const THEMES = ['bonfire', 'crimson', 'starlight', 'harbor', 'nightfall', 'glass', 'harvest', 'snowfall', 'bloom',
        'jellyfin-dark', 'jellyfin-light', 'high-contrast'];

    /// First-time setup steps this script can draw (OnboardingSteps on the server). A
    /// newer server's unknown step is left out rather than shown blank.
    const ONBOARDING_STEPS = ['welcome', 'mainPin', 'resetEmail', 'firstProfile', 'switcher', 'theme'];

    /// The administrator's own themes and fonts, and the built-ins they hid (1.7.2), from
    /// GET themes?part=custom. Empty until that answers, or until the copy cached on this
    /// device is read at startup (restoreCustomThemes). The built-ins are spliced into this
    /// file; these cannot be, because profiles.js is cached for the life of the server and
    /// an administrator changes them at any moment.
    let CUSTOM_THEMES = { themes: [], fonts: [], hidden: [], logo: null };

    /// Caps on what is read, the server's own (ThemeCatalog.MaxCustomThemes, MaxCustomFonts).
    const MAX_CUSTOM_THEMES = 40;
    const MAX_CUSTOM_FONTS = 20;

    function customTheme(id) {
        return CUSTOM_THEMES.themes.filter(c => c.id === id)[0] || null;
    }

    /// True for a built-in, hidden or not, or a custom theme this script has been told of.
    function isThemeId(value) {
        const v = String(value || '').toLowerCase();
        return THEMES.indexOf(v) !== -1 || !!customTheme(v);
    }

    /// What the pickers offer: the built-ins less the hidden ones (never Bonfire), then the
    /// custom themes in the order they were made. Themes.Offered on the server.
    function offeredThemes() {
        return THEMES.filter(id => id === 'bonfire' || CUSTOM_THEMES.hidden.indexOf(id) === -1)
            .concat(CUSTOM_THEMES.themes.map(c => c.id));
    }

    /// A stored choice the pickers can tick, lower case, or '' for one they do not offer.
    function offeredChoice(value) {
        const v = String(value || '').toLowerCase();
        return v && offeredThemes().indexOf(v) !== -1 ? v : '';
    }

    /// A theme's name as text. A built-in's is the string theme.<id>; a custom theme's is
    /// the name.text the administrator typed, which is not markup: see themeName.
    function themeNameText(id) {
        const v = normalizeTheme(id);
        const custom = customTheme(v);
        if (custom) return custom.name && typeof custom.name.text === 'string' && custom.name.text ? custom.name.text : v;
        return t('theme.' + v);
    }

    /// A theme's name for a template that becomes innerHTML. Escaped, because a custom
    /// theme's name is whatever an administrator typed.
    function themeName(id) {
        return escapeHtml(themeNameText(id));
    }

    /// The server's logo (1.7.2), or null for none: { image, text, alt }. The image only at
    /// the one address the server gives a logo, a hash and an extension, so nothing in the
    /// answer can point the switcher at another site. Text is one line; ThemeLogo's limits.
    function readLogo(logo) {
        if (!logo || typeof logo !== 'object') return null;
        const line = (v, max) => (typeof v === 'string' && v.length <= max && !/[\r\n]/.test(v) && v.trim() ? v.trim() : null);
        const image = typeof logo.image === 'string' && /^\/plugins\/profiles\/themes\/logo\/[0-9a-f]{32}\.(png|jpg|webp)$/.test(logo.image)
            ? logo.image : null;
        const text = line(logo.text, 40);
        if (!image && !text) return null;
        return { image: image, text: text, alt: line(logo.alt, 100) };
    }

    /// A ?part=custom answer, or the copy of one cached here, in the shape this script uses.
    /// Anything malformed is dropped rather than trusted: themeCss forces every value into
    /// its type as it writes CSS, and this keeps a custom theme from ever taking a
    /// built-in's id, whose block would then be drawn twice.
    function readCustomThemes(answer) {
        const out = { themes: [], fonts: [], hidden: [], logo: null };
        if (answer && typeof answer === 'object') out.logo = readLogo(answer.logo);
        if (!answer || typeof answer !== 'object') return out;
        (Array.isArray(answer.themes) ? answer.themes : []).forEach(c => {
            if (!c || typeof c !== 'object' || typeof c.id !== 'string' || !/^[a-z0-9-]{1,40}$/.test(c.id)) return;
            if (THEMES.indexOf(c.id) !== -1 || out.themes.some(x => x.id === c.id)) return;
            if (out.themes.length < MAX_CUSTOM_THEMES) out.themes.push(c);
        });
        (Array.isArray(answer.fonts) ? answer.fonts : []).forEach(f => {
            if (!f || f.custom !== true || typeof f.family !== 'string' || !f.family) return;
            const one = Array.isArray(f.files) ? f.files[0] : null;
            const url = one && typeof one.url === 'string' ? one.url : '';
            if (!/^\/plugins\/profiles\/themes\/fonts\/[0-9a-f]{32}\.woff2$/.test(url)) return;
            const w = one && Array.isArray(one.weight) ? one.weight : [];
            if (out.fonts.length >= MAX_CUSTOM_FONTS || out.fonts.some(x => x.family === f.family)) return;
            out.fonts.push({
                family: f.family,
                fallback: f.fallback === 'serif' ? 'serif' : 'sans-serif',
                weight: typeof w[0] === 'number' && typeof w[1] === 'number' ? [w[0], w[1]] : [400, 400],
                file: url
            });
        });
        (Array.isArray(answer.hidden) ? answer.hidden : []).forEach(id => {
            if (typeof id === 'string' && id !== 'bonfire' && THEMES.indexOf(id) !== -1 && out.hidden.indexOf(id) === -1) out.hidden.push(id);
        });
        return out;
    }

    /// Jellyfin Dark and Jellyfin Light have a match in jellyfin-web's own theme list;
    /// no other theme does. Choosing one OFFERS to switch the rest of Jellyfin too, and
    /// never does it unasked: jellyfin-web's theme is the person's, not Bonfire's.
    ///
    /// jellyfin-web keeps that theme on the device, not on the server: userSettings.theme()
    /// is set('appTheme', id, false), and appSettings stores it in localStorage as
    /// "<userId>-appTheme" (src/scripts/settings/userSettings.js and appSettings.js,
    /// release-10.11.z). Nothing stored means its default, "dark". It is read again on
    /// every page change (autoThemes.js, viewbeforeshow), so a value written here shows
    /// from the next page opened, with no reload.
    const JELLYFIN_WEB_THEMES = { 'jellyfin-dark': 'dark', 'jellyfin-light': 'light' };

    /// The localStorage key jellyfin-web reads a user's theme from, or null for anything
    /// that is not a user id. jellyfin-web's ids are the server's: 32 hex digits, no dashes.
    function jellyfinWebThemeKey(userId) {
        const id = String(userId || '').toLowerCase().replace(/-/g, '');
        return /^[0-9a-f]{32}$/.test(id) ? id + '-appTheme' : null;
    }

    /// The jellyfin-web theme to offer when a profile is given `theme`, or null when there
    /// is nothing to offer: a theme with no match, or a match the profile already has here.
    function jellyfinWebOffer(theme, stored) {
        const match = JELLYFIN_WEB_THEMES[theme];
        if (!match) return null;
        return (stored || 'dark') === match ? null : match;
    }

    /// GET plugins/profiles/ratings -> [[score, labels], ...], one entry per age.
    ///
    /// The server reads the rating table of its own Preferred metadata country — the one
    /// Jellyfin scored the library with — so these are that country's ratings and no
    /// others. Not /Localization/ParentalRatings: Jellyfin pads that list with generic
    /// ages the country does not have, and a padded "10" cannot be told from France's
    /// real one. See Configuration/RatingSystems.cs.
    ///
    /// A limit is stored as the score alone, so a profile keeps working whichever labels
    /// are shown. Returns [] for anything unusable, and the caller falls back to the US
    /// list this form always had.
    function ratingChoicesFrom(answer) {
        const list = answer && typeof answer === 'object'
            ? (Array.isArray(answer.ratings) ? answer.ratings : answer.Ratings) : null;
        if (!Array.isArray(list)) return [];
        const seen = {};
        return list.map(r => {
            if (!r || typeof r !== 'object') return null;
            const score = r.score !== undefined ? r.score : r.Score;
            const labels = (r.labels !== undefined ? r.labels : r.Labels) || [];
            if (typeof score !== 'number' || score < 0 || score >= 1000 || seen[score]) return null;
            const names = labels.filter(n => typeof n === 'string' && n.trim()).map(n => n.trim());
            if (!names.length) return null;
            seen[score] = true;
            // Four at most. Canada's 0 alone is E, G, TV-Y and TV-G.
            return [score, names.slice(0, 4).join(' / ')];
        }).filter(Boolean).sort((a, b) => a[0] - b[0]);
    }

    /// A theme this script can draw, or Bonfire. A hidden built-in still counts: the server
    /// resolves a profile on one to the default before it says what to show, and a preview
    /// of one must not turn into Bonfire. A custom theme counts once it is known.
    function normalizeTheme(value) {
        const v = String(value || '').toLowerCase();
        return isThemeId(v) ? v : 'bonfire';
    }

    /// A library or user id as the server writes one: 32 hex digits, dashed or not.
    function isGuidLike(value) {
        return /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i.test(String(value || ''));
    }

    function safeColor(color) {
        return HEX_COLOR_RE.test(color || '') ? color : DEFAULT_AVATAR_COLOR;
    }

    /// Our image endpoints are stored root-relative, which only resolves when the page was
    /// served by the Jellyfin server. Inside a packaged client — Samsung Tizen bundles the
    /// web client into the app — the origin is the app itself and those requests 404.
    /// ApiClient knows the real server address, so route through it whenever we can.
    function pluginUrl(value) {
        try {
            if (typeof ApiClient !== 'undefined' && ApiClient && typeof ApiClient.getUrl === 'function') {
                const resolved = ApiClient.getUrl(value.replace(/^\//, ''));
                if (resolved) return resolved;
            }
        } catch (e) { /* no ApiClient yet — the relative path is still right in a browser */ }
        return value;
    }

    /// Allows only the three shapes the plugin actually produces: its own image
    /// endpoint, a data:image payload, and an absolute http(s) URL. Anything else
    /// resolves to an empty src rather than being trusted.
    function safeImageSrc(src) {
        if (!src) return '';
        const value = String(src).trim();
        if (value.startsWith('/plugins/profiles/image/')) return escapeHtml(pluginUrl(value));
        if (value.startsWith('/plugins/profiles/avatars/')) return escapeHtml(pluginUrl(value));
        if (/^data:image\/(png|jpeg|jpg|gif|webp);base64,[A-Za-z0-9+/=]+$/i.test(value)) return escapeHtml(value);
        try {
            const parsed = new URL(value, window.location.origin);
            if (parsed.protocol === 'http:' || parsed.protocol === 'https:') return escapeHtml(parsed.href);
        } catch (e) { /* not a parseable URL — fall through */ }
        return '';
    }

    /// Appends ?size=thumb to one of our own image URLs. Grids and switcher cards ask for
    /// the small rendering; showing twenty full-size avatars would decode tens of megabytes
    /// of bitmap, which is enough to stall the TV browsers this plugin supports.
    /// Remote URLs and data payloads are returned untouched — there is no variant of those.
    function thumbSrc(src) {
        if (!src) return '';
        const value = String(src).trim();
        if (!value.startsWith('/plugins/profiles/image/') && !value.startsWith('/plugins/profiles/avatars/')) return value;
        return value + (value.includes('?') ? '&' : '?') + 'size=thumb';
    }

    /// Markup for the inside of an avatar circle.
    ///
    /// The initial is always rendered, with the picture layered over it. If the image fails
    /// — the file was deleted, the disk is unreadable, a remote host is down — onerror
    /// removes the img and the initial is simply revealed underneath. That is the whole
    /// fallback: no state, no second request, and nothing for a caller to remember to do.
    ///
    /// The containing element must be position:relative (all of ours already are, or are
    /// given it inline at the call site).
    function avatarInner(src, initial, useThumb) {
        const safeInitial = escapeHtml(initial || '?');
        const resolved = safeImageSrc(useThumb ? thumbSrc(src) : src);
        if (!resolved) return safeInitial;
        return safeInitial +
            `<img src="${resolved}" alt="" onerror="this.remove()" ` +
            `style="position:absolute;top:0;left:0;width:100%;height:100%;object-fit:cover;" />`;
    }

    // The profile gate overlay sits at z-index 99999. Anything that has to appear *over*
    // it — alerts, confirmations — must beat that number, or it renders behind a full-screen
    // opaque background and is completely invisible. That produced dead-looking buttons:
    // clicking Save with an invalid PIN fired a validation alert nobody could see.
    const OVERLAY_Z = 99999;
    const DIALOG_Z = OVERLAY_Z + 10;

    // ── Internationalisation ────────────────────────────────────────────────────
    // English lives inline, as the single dependency-free fallback every client can
    // render immediately — no fetch, no flash of untranslated text, works even if the
    // server is briefly unreachable. Every other language is a JSON file the server
    // hands out from Web/i18n/, fetched only for a user whose Jellyfin Display language
    // asks for it: nobody pays for a translation they will not read.
    //
    // To add a language: drop Web/i18n/<code>.json next to fr.json, with the same keys
    // and translated values. That is the whole job — the .csproj takes Web/i18n/*.json
    // as a wildcard, the server lists what it finds embedded, and the line below is
    // rewritten with that list as the script is served. Nothing here needs editing.
    //
    // `node Web/i18n/validate.js` checks a file against the catalogue below before you
    // open the pull request.
    //
    // The empty default is what a copy of this file read straight off disk will use, so
    // reading the raw file always yields English and never a broken fetch.
    let SUPPORTED_LOCALES = []; // __BONFIRE_LOCALES__

    /*
     * The stylesheet, spliced in by the server from Web/styles.css.
     *
     * This used to be a 1,400-line template literal right inside injectStyles, and
     * it cost two releases: a code comment containing two backticks ended the literal
     * early, the file still parsed, node --check passed, and the script threw at
     * runtime three calls into startup. 1.5.2 and 1.5.3-beta shipped dead.
     *
     * PublishStyles replaces the marker below with the CSS encoded as a JSON string
     * literal — double-quoted and fully escaped — so nothing in the stylesheet can
     * terminate anything here. The failure mode is gone rather than guarded.
     *
     * The default is an empty string, not the real CSS, so this file is never the
     * place the stylesheet lives. If the marker is ever edited away the replace does
     * nothing and the plugin renders unstyled, which is loud and immediate — unlike a
     * silently truncated literal, which is what got us here.
     */
    let BONFIRE_STYLES = ""; // __BONFIRE_STYLES__

    /*
     * The themes, spliced in by the server from Web/themes/*.json, the same way as the
     * stylesheet: { themes: [...], patterns: { name: "data:image/svg+xml,..." } }.
     * docs/themes.md describes a theme file. themeCss below turns each one into the
     * CSS Bonfire's screens read, and injectStyles puts that after the stylesheet.
     *
     * This is the only place a theme becomes CSS. Other apps read the same files and
     * draw their own way; nothing about a theme is in styles.css any more, so nothing
     * the web client shows is out of their reach.
     */
    let BONFIRE_THEMES = { themes: [], patterns: {} }; // __BONFIRE_THEMES__

    /// One theme file as CSS: its tokens on [data-jpf-theme="id"] (and on :root for
    /// Bonfire, the look before any theme is known), then the rules its layout and
    /// decorations need.
    ///
    /// Every value is forced into its type on the way out: a colour is #rrggbb[aa] or
    /// nothing, a number is a number, an id is letters, digits and dashes, a font name is
    /// letters, digits and spaces. An administrator's theme file reaches this, so a value
    /// that could close the declaration or the rule is dropped rather than written.
    ///
    /// Chromium 53: no object spread, no ?. — this runs on every webOS television.
    function themeCss(theme, patterns) {
        if (!theme || typeof theme !== 'object') return '';
        const id = String(theme.id || '');
        if (!/^[a-z0-9-]{1,40}$/.test(id)) return '';
        const g = (o, k) => (o && typeof o === 'object' ? o[k] : undefined);
        const num = (v, d) => (typeof v === 'number' && isFinite(v) ? v : d);
        const hex = v => (/^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$/.test(String(v || '')) ? String(v) : null);
        const rgbOf = v => [1, 3, 5].map(i => parseInt(v.slice(i, i + 2), 16));
        const alphaOf = v => (v.length === 9 ? Math.round(parseInt(v.slice(7), 16) / 255 * 1000) / 1000 : 1);
        const col = (v, d) => {
            const h = hex(v);
            if (!h) return d || 'transparent';
            if (h.length === 7) return h.toLowerCase();
            const c = rgbOf(h);
            return 'rgba(' + c[0] + ', ' + c[1] + ', ' + c[2] + ', ' + alphaOf(h) + ')';
        };
        const tint = (v, a) => {
            const h = hex(v);
            if (!h) return 'transparent';
            const c = rgbOf(h);
            return 'rgba(' + c[0] + ', ' + c[1] + ', ' + c[2] + ', ' + a + ')';
        };
        const shadowColour = hex(g(theme.colors, 'shadow')) || '#000000';
        const shadowCss = (s, text) => {
            if (!s || typeof s !== 'object') return 'none';
            const colour = hex(s.color) ? col(s.color) : tint(shadowColour, num(s.opacity, 1));
            return num(s.x, 0) + 'px ' + num(s.y, 0) + 'px ' + num(s.blur, 0) + 'px '
                + (!text && num(s.spread, 0) ? num(s.spread, 0) + 'px ' : '') + colour;
        };
        const style = v => (['solid', 'dashed', 'dotted'].indexOf(v) !== -1 ? v : 'solid');
        const borderCss = b => (b && typeof b === 'object'
            ? num(b.width, 0) + 'px ' + style(b.style) + ' ' + col(b.color) : '0 solid transparent');
        const fontName = v => String(v || '').replace(/[^A-Za-z0-9 -]/g, '');
        const quoteFont = v => (/^-|^[A-Za-z]+$/.test(v) ? v : "'" + v + "'");

        const c = theme.colors || {};
        const shape = theme.shape || {};
        const type = theme.type || {};
        const parts = theme.parts || {};
        const decorations = Array.isArray(theme.decorations) ? theme.decorations : [];
        const deco = name => decorations.filter(d => d && d.name === name)[0] || null;
        const light = theme.scheme === 'light';
        // A theme with no accent writes nothing, so whatever wears it shows Bonfire, whose
        // block is on :root. Not a stand-in colour: everything else in a theme is drawn
        // around its accent, and a guessed one would be a theme nobody made.
        const accent = hex(c.accent);
        if (!accent) return '';
        const sel = '[data-jpf-theme="' + id + '"]';

        // The background: each layer as a CSS gradient, first on top; or the plain fill.
        const bg = theme.background || {};
        const layers = (Array.isArray(bg.layers) ? bg.layers : []).map(L => {
            const stops = (Array.isArray(L && L.stops) ? L.stops : [])
                .map(s => col(s && s.color) + ' ' + num(s && s.at, 0) + '%').join(', ');
            if (!stops) return null;
            return L.type === 'radial'
                ? 'radial-gradient(' + (L.shape === 'circle' ? 'circle' : 'ellipse') + ' at ' + num(L.x, 50) + '% ' + num(L.y, 50) + '%, ' + stops + ')'
                : 'linear-gradient(' + num(L.angle, 180) + 'deg, ' + stops + ')';
        }).filter(Boolean);
        const gateBg = layers.length ? layers.join(', ') : col(bg.fill, '#101010');

        const av = shape.avatar || {};
        const avatarR = av.kind === 'circle' ? '50%'
            : av.kind === 'rounded' ? num(av.radius, 30) + '%'
            : av.kind === 'leaf' ? '50% ' + num(av.radius, 10) + 'px'
            : num(av.radius, 0) + 'px';
        const btn = shape.button || {};
        const btnR = btn.kind === 'pill' ? '999px' : btn.kind === 'square' ? '0' : num(btn.radius, 8) + 'px';
        const r = shape.radius || {};

        // The font: the copy Bonfire serves (fontFaceCss), then the names it goes by on
        // devices that have it installed, then the kind. The served copy is under a name of
        // its own, so declaring it never changes how jellyfin-web draws its own Noto Sans;
        // and where it is not declared (an engine without variable fonts) that first name
        // simply matches nothing and the next one is used.
        const fontEntry = (BONFIRE_THEMES.fonts || []).concat(CUSTOM_THEMES.fonts)
            .filter(f => f && f.family === g(type.font, 'family'))[0];
        const names = (fontEntry && (fontEntry.files || fontEntry.file) ? [servedFontName(fontEntry.family)] : [])
            .concat([fontName(g(type.font, 'family'))], fontEntry && Array.isArray(fontEntry.local)
            ? fontEntry.local.map(fontName) : []).filter((n, i, all) => n && all.indexOf(n) === i && n !== 'System UI');
        const fallback = g(type.font, 'fallback') === 'serif' ? 'serif' : 'sans-serif';
        const font = names.map(quoteFont).concat([fallback]).join(', ');

        const title = type.title || {}, name = type.name || {}, button = type.button || {};
        const b2 = parts.button2 || {}, b2f = b2.focus || {};
        const add = parts.addTile || {}, card = parts.card || {}, sheet = parts.sheet || {}, input = parts.input || {};
        const em = v => (num(v, 0) === 0 ? 'normal' : num(v, 0) + 'em');
        const rings = deco('rings');
        const focusScale = num(g(theme.focus, 'scale'), 1);
        const panels = g(theme.layout, 'households') === 'panels';
        const pattern = deco('pattern');
        const patternImage = pattern && typeof pattern.image === 'string' && patterns
            && typeof patterns[pattern.image] === 'string' && /^data:image\/svg\+xml,[^"\\]*$/.test(patterns[pattern.image])
            ? patterns[pattern.image] : null;
        const patternSize = pattern ? num(pattern.size, 240) : 240;
        const ac = rgbOf(accent);

        const vars = [
            ['--jpf-accent', accent.slice(0, 7)],
            ['--jpf-accent-rgb', ac.join(', ')],
            ['--jpf-accent-hover', col(c.accentFocus, accent)],
            ['--jpf-on-accent', col(c.onAccent, '#fff')],
            ['--jpf-accent-a08', tint(accent, 0.08)], ['--jpf-accent-a18', tint(accent, 0.18)],
            ['--jpf-accent-a30', tint(accent, 0.3)], ['--jpf-accent-a40', tint(accent, 0.4)],
            ['--jpf-accent-a45', tint(accent, 0.45)], ['--jpf-accent-a50', tint(accent, 0.5)],
            ['--jpf-accent-a60', tint(accent, 0.6)],
            ['--jpf-surface-1', col(c.surface1)], ['--jpf-surface-2', col(c.surface2)], ['--jpf-surface-3', col(c.surface3)],
            ['--jpf-border-1', col(c.border1)], ['--jpf-border-2', col(c.border2)], ['--jpf-border-3', col(c.border3)],
            ['--jpf-text-1', col(c.text1)], ['--jpf-text-2', col(c.text2)], ['--jpf-text-3', col(c.text3)],
            // Shadows keep their own scale, and go lighter on a light theme: the same
            // black at the same alpha reads as a smudge on white.
            ['--jpf-shadow-1', tint(shadowColour, light ? 0.1 : 0.2)],
            ['--jpf-shadow-2', tint(shadowColour, light ? 0.16 : 0.4)],
            ['--jpf-shadow-3', tint(shadowColour, light ? 0.22 : 0.55)],
            ['--jpf-danger', col(c.danger, light ? '#e03131' : '#ff6b6b')],
            ['--jpf-danger-quiet', light ? '#f03e3e' : '#ff8787'],
            ['--jpf-danger-deep', light ? '#c2255c' : '#e64980'],
            ['--jpf-warn', col(c.warning, light ? '#e8590c' : '#ff9900')],
            ['--jpf-gate-bg', gateBg],
            ['--jpf-panel', col(c.panel, '#1b1b28')],
            ['--jpf-font', font],
            ['--jpf-r-sm', num(r.small, 6) + 'px'], ['--jpf-r-md', num(r.medium, 12) + 'px'], ['--jpf-r-lg', num(r.large, 20) + 'px'],
            ['--jpf-avatar-r', avatarR],
            ['--jpf-avatar-shadow', shadowCss(g(theme.depth, 'avatar'))],
            ['--jpf-ring', col(c.ring, '#fff')],
            ['--jpf-ring-w', num(shape.ringWidth, 3) + 'px'],
            // With rings the frame grows rather than the picture inside it: see below.
            ['--jpf-hover-scale', rings ? 1 : focusScale],
            ['--jpf-hover-glow', shadowCss(g(theme.focus, 'glow'))],
            ['--jpf-title-weight', num(title.weight, 700)],
            ['--jpf-title-spacing', num(title.spacing, 0) === 0 ? '0' : num(title.spacing, 0) + 'em'],
            ['--jpf-title-shadow', shadowCss(g(theme.depth, 'title'), true)],
            ['--jpf-title-scale', num(title.scale, 1)],
            ['--jpf-name-color', col(c.name, '#fff')],
            ['--jpf-name-hover', col(c.nameFocus, '#fff')],
            ['--jpf-name-weight', num(name.weight, 500)],
            ['--jpf-name-opacity', num(name.opacity, 1)],
            ['--jpf-name-scale', num(name.scale, 1)],
            ['--jpf-btn-radius', btnR],
            ['--jpf-btn2-bg', col(b2.fill)],
            ['--jpf-btn2-color', col(b2.text, '#fff')],
            ['--jpf-btn2-border', borderCss(b2.border)],
            ['--jpf-btn2-case', button.case === 'upper' ? 'uppercase' : 'none'],
            ['--jpf-btn2-spacing', em(button.spacing)],
            ['--jpf-btn2-weight', num(button.weight, 600)],
            ['--jpf-btn2-hover-bg', col(b2f.fill)],
            ['--jpf-btn2-hover-color', col(b2f.text, '#fff')],
            ['--jpf-btn2-hover-border', col(g(b2f.border, 'color'))],
            ['--jpf-btn2-hover-glow', shadowCss(b2f.glow)],
            ['--jpf-add-bg', col(add.fill)],
            ['--jpf-add-border', borderCss(add.border)],
            ['--jpf-add-color', col(add.text, '#fff')],
            ['--jpf-add-hover-bg', col(add.focusFill)],
            ['--jpf-section-bg', panels ? 'var(--jpf-surface-1)' : 'transparent'],
            ['--jpf-section-border', panels ? '1px solid var(--jpf-border-1)' : '0 solid transparent'],
            ['--jpf-section-shadow', panels ? '0 10px 30px var(--jpf-shadow-1)' : 'none'],
            ['--jpf-section-pad', panels ? '1.75rem 2rem' : '0'],
            ['--jpf-section-rule', panels ? '1px solid var(--jpf-border-1)' : '0 solid transparent'],
            ['--jpf-sheet-bg', col(sheet.fill)],
            ['--jpf-sheet-border', borderCss(sheet.border)],
            ['--jpf-sheet-shadow', shadowCss(g(theme.depth, 'sheet'))],
            ['--jpf-card-bg', col(card.fill)],
            ['--jpf-card-border', col(card.line)],
            ['--jpf-input-bg', col(input.fill)],
            ['--jpf-input-border', col(input.line)],
            ['--jpf-input-r', num(g(shape.input, 'radius'), 8) + 'px'],
            ['--jpf-deco', patternImage ? 'url("' + patternImage + '")' : 'none'],
            ['--jpf-deco-size', patternSize + 'px']
        ];

        // On the attribute and on body, because the base tokens are declared on body too
        // and a declaration on body beats one inherited from html. Bonfire also on :root:
        // it is what every screen wears before a theme is known.
        const selectors = (id === 'bonfire' ? [':root', 'body'] : []).concat([sel, sel + ' body']);
        let css = selectors.join(',\n') + ' {\n' + vars.map(v => '    ' + v[0] + ': ' + v[1] + ';').join('\n') + '\n}\n';

        // Without panels the tiles sit straight on the background, so the layout changes
        // with it: one row where one fits, and a quiet heading, gone when there is only
        // one household. The doubled attribute keeps these at the weight they had as
        // [data-jpf-theme]:not([data-jpf-theme="bonfire"]), so the fitted gap
        // (#profiles-gate-overlay ... .jpf-fitted) still outranks the grid rule.
        if (!panels) {
            const open = sel + '[data-jpf-theme]';
            css += open + ':not(.jpf-tv) #profiles-gate-overlay .profiles-modal-content { max-width: 1240px; }\n'
                + open + ':not(.jpf-tv) .profiles-grid { max-width: none; gap: 2rem; }\n'
                + open + ' .profiles-home-header { justify-content: center; padding-bottom: 0; margin-bottom: 1.5rem; }\n'
                + open + ' .profiles-home-title { font-size: 0.85rem; font-weight: 600; letter-spacing: 0.1em; text-transform: uppercase; color: var(--jpf-text-3); }\n'
                + open + ' .profiles-home-icon { font-size: 1.1rem; }\n'
                + open + ' .jpf-one-home .profiles-home-header { display: none; }\n';
        }

        // rings: a gradient cannot be a border colour, so each tile's frame is a disc
        // behind it, painted in two layers: the gradient on the border box and the gap
        // colour inside it, which leaves a ring. Opaque inside so a cut-out picture sits
        // on the dark rather than on a gradient disc. Profile 1 takes the first gradient,
        // profile 2 the second, and round again. Not on the add tile, which is not a person.
        if (rings && Array.isArray(rings.gradients) && rings.gradients.length) {
            const gap = col(rings.gap, '#000000');
            const w = num(shape.ringWidth, 3);
            const pair = p => 'linear-gradient(' + gap + ', ' + gap + ') padding-box,\n        linear-gradient(135deg, '
                + col(p && p[0]) + ' 0%, ' + col(p && p[1]) + ' 100%) border-box';
            const n = rings.gradients.length;
            css += sel + ' .profile-avatar-container::before {\n    content: \'\';\n    position: absolute;\n'
                + '    top: -' + (w + 4) + 'px;\n    right: -' + (w + 4) + 'px;\n    bottom: -' + (w + 4) + 'px;\n    left: -' + (w + 4) + 'px;\n'
                + '    border-radius: 50%;\n    border: ' + w + 'px solid transparent;\n'
                + '    background:\n        ' + pair(rings.gradients[0]) + ';\n    z-index: 0;\n    pointer-events: none;\n}\n';
            for (let k = 2; k <= n; k++) {
                css += sel + ' .profile-card:nth-child(' + n + 'n' + (k === n ? '' : '+' + k) + ') .profile-avatar-container::before {\n'
                    + '    background:\n        ' + pair(rings.gradients[k - 1]) + ';\n}\n';
            }
            css += sel + ' .profile-card.action-add-profile .profile-avatar-container::before { display: none; }\n'
                + sel + ' .profile-avatar { z-index: 1; box-sizing: border-box; }\n'
                + sel + ' .profile-card:hover .profile-avatar-container,\n'
                + sel + ' .profile-card:focus .profile-avatar-container { transform: scale(' + focusScale + '); }\n'
                // Its own rule: :focus-within is Chrome 60, and a selector Chromium 53 cannot
                // parse drops every other selector listed with it.
                + sel + ' .profile-card:focus-within .profile-avatar-container { transform: scale(' + focusScale + '); }\n';
            // The small preview in the pickers: three tiles, each framed in the colour
            // halfway along its gradient (a box-shadow cannot be a gradient). Written here
            // from the file, not in styles.css, which used to carry Nightfall's three
            // colours by hand: a custom theme with rings had none in its preview, and an
            // edit to Nightfall's file left the preview showing the old ones.
            const mid = p => {
                const a = hex(p && p[0]), b = hex(p && p[1]);
                if (!a || !b) return col(a || b);
                const x = rgbOf(a), y = rgbOf(b);
                return '#' + [0, 1, 2].map(i => ('0' + Math.round((x[i] + y[i]) / 2).toString(16)).slice(-2)).join('');
            };
            for (let k = 1; k <= 3; k++) {
                css += '.jpf-theme-preview' + sel + ' .jpf-theme-preview-tile:nth-child(' + k + ') { box-shadow: 0 0 0 2px '
                    + gap + ', 0 0 0 4px ' + mid(rings.gradients[(k - 1) % n]) + '; }\n';
            }
        }

        // frost: backdrop-filter is Chromium 76, so webOS 4.5 and 5 keep the plain
        // see-through fills, which still read as the theme. Both spellings, because
        // Safari only knows the prefixed one. Not the Theme page, which draws no sheet:
        // its "backdrop-filter: none" ties with this on specificity and this comes later,
        // being written after the stylesheet, so the page has to be left out by name.
        const frost = deco('frost');
        if (frost) {
            const blur = num(frost.blur, 24);
            const small = Math.round(blur * 2 / 3);
            css += '@supports ((-webkit-backdrop-filter: blur(1px)) or (backdrop-filter: blur(1px))) {\n'
                + '    ' + sel + ' .create-profile-container:not(.jpf-theme-page) {\n        -webkit-backdrop-filter: blur(' + blur + 'px) saturate(160%);\n        backdrop-filter: blur(' + blur + 'px) saturate(160%);\n    }\n'
                + '    ' + sel + ' .btn-secondary {\n        -webkit-backdrop-filter: blur(' + small + 'px) saturate(160%);\n        backdrop-filter: blur(' + small + 'px) saturate(160%);\n    }\n}\n';
        }

        // A drifting pattern comes down, slowly: a transform on a layer one tile taller
        // than the screen, so the compositor moves it and nothing is repainted; one tile a
        // loop, so it joins up. Not on a television, whose browser is the slowest thing it
        // runs on, and not for anyone who has asked their system for less motion.
        if (patternImage && pattern.drift === 'down') {
            const seconds = num(pattern.seconds, 32);
            const anim = 'jpf-drift-' + patternSize;
            css += '@media (prefers-reduced-motion: no-preference) {\n'
                + '    ' + sel + ':not(.jpf-tv) #profiles-gate-overlay::before {\n'
                + '        top: -' + patternSize + 'px;\n        will-change: transform;\n'
                + '        -webkit-animation: ' + anim + ' ' + seconds + 's linear infinite;\n'
                + '        animation: ' + anim + ' ' + seconds + 's linear infinite;\n    }\n}\n'
                + '@-webkit-keyframes ' + anim + ' {\n    from { -webkit-transform: translateY(0); transform: translateY(0); }\n'
                + '    to { -webkit-transform: translateY(' + patternSize + 'px); transform: translateY(' + patternSize + 'px); }\n}\n'
                + '@keyframes ' + anim + ' {\n    from { transform: translateY(0); }\n    to { transform: translateY(' + patternSize + 'px); }\n}\n';
        }
        return css;
    }

    /// Every theme the server sent, as CSS, Bonfire first so each later block wins its
    /// ties on html.
    function allThemesCss() {
        const list = Array.isArray(BONFIRE_THEMES.themes) ? BONFIRE_THEMES.themes.slice() : [];
        list.sort((a, b) => (a && a.id === 'bonfire' ? -1 : b && b.id === 'bonfire' ? 1 : 0));
        return list.map(t => themeCss(t, BONFIRE_THEMES.patterns || {})).join('');
    }

    /// The name a served font is declared under: its family with Bonfire's prefix, letters,
    /// digits, spaces and hyphens only, as every font name written into CSS here is.
    function servedFontName(family) {
        return 'Bonfire ' + String(family || '').replace(/[^A-Za-z0-9 -]/g, '');
    }

    /// @font-face for every font Bonfire serves, one per subset, each fetched only when a
    /// character in its unicode-range is drawn in it. `urlFor` turns a file name into the
    /// address to fetch (pluginUrl, so a packaged client asks the server and not itself).
    ///
    /// Each file is a variable font spanning a range of weights. An engine without variable
    /// fonts (Chromium 62; webOS 4.5 is 53) would draw every weight from the default one and
    /// fake the bold, so injectFonts declares none there and the stack's installed names
    /// are used, which is how every theme looked before Bonfire served fonts at all.
    function fontFaceCss(urlFor) {
        const subsets = BONFIRE_THEMES.subsets || {};
        let css = '';
        (BONFIRE_THEMES.fonts || []).forEach(f => {
            if (!f || !f.files || typeof f.files !== 'object') return;
            const w = Array.isArray(f.weight) ? f.weight : [];
            const weight = typeof w[0] === 'number' && typeof w[1] === 'number' && isFinite(w[0]) && isFinite(w[1])
                ? Math.round(w[0]) + ' ' + Math.round(w[1]) : '400';
            Object.keys(subsets).forEach(sub => {
                const file = f.files[sub];
                const range = subsets[sub];
                if (typeof file !== 'string' || !/^[a-z0-9-]+\.woff2$/.test(file)) return;
                if (typeof range !== 'string' || !/^[Uu0-9A-Fa-f+, -]+$/.test(range)) return;
                const url = String(urlFor('/plugins/profiles/themes/fonts/' + file)).replace(/["\\\n\r]/g, '');
                css += "@font-face {\n    font-family: '" + servedFontName(f.family) + "';\n    font-style: normal;\n"
                    + '    font-weight: ' + weight + ';\n    font-display: swap;\n'
                    + '    src: url("' + url + '") format("woff2");\n    unicode-range: ' + range + ';\n}\n';
            });
        });
        return css;
    }

    /// @font-face for each uploaded font: one file holding every character it has, so no
    /// unicode-range. A variable one is left out on an engine without variable fonts, for the
    /// same reason as fontFaceCss; a static one is drawn everywhere.
    function customFontFaceCss(urlFor, variable) {
        let css = '';
        CUSTOM_THEMES.fonts.forEach(f => {
            const lo = Math.round(f.weight[0]), hi = Math.round(f.weight[1]);
            if (!isFinite(lo) || !isFinite(hi) || (lo !== hi && !variable)) return;
            const url = String(urlFor(f.file)).replace(/["\\\n\r]/g, '');
            css += "@font-face {\n    font-family: '" + servedFontName(f.family) + "';\n    font-style: normal;\n"
                + '    font-weight: ' + (lo === hi ? lo : lo + ' ' + hi) + ';\n    font-display: swap;\n'
                + '    src: url("' + url + '") format("woff2");\n}\n';
        });
        return css;
    }

    /// Every custom theme as CSS. Their ids are never a built-in's (readCustomThemes), so
    /// no block here ties with one in the stylesheet's style element.
    function customThemesCss() {
        return CUSTOM_THEMES.themes.map(c => themeCss(c, BONFIRE_THEMES.patterns || {})).join('');
    }

    /// The gate's background for one theme, as themeCss writes it, or '' for a theme this
    /// build does not have. Read out of themeCss's own output rather than worked out again,
    /// so the startup cover cannot drift from the gate it stands in for.
    function gateBackground(id) {
        const theme = (Array.isArray(BONFIRE_THEMES.themes) ? BONFIRE_THEMES.themes : [])
            .filter(t => t && t.id === id)[0] || customTheme(id);
        if (!theme) return '';
        const m = /--jpf-gate-bg: ([^;]*);/.exec(themeCss(theme, BONFIRE_THEMES.patterns || {}));
        return m ? m[1] : '';
    }

    const EN_STRINGS = {
        'common.cancel': 'Cancel',
        'common.confirm': 'Confirm',
        'common.back': 'Back',
        'common.done': 'Done',
        'common.save': 'Save',
        'common.ok': 'OK',
        'common.remove': 'Remove',
        'common.add': 'Add',
        'common.selectAll': 'Select all',
        'common.create': 'Create',
        'common.join': 'Join',
        'common.submit': 'Submit',
        'common.unlock': 'Unlock',
        'common.settings': 'Settings',

        'gate.whosWatching': "Who's Watching?",
        'gate.manageProfiles': 'Manage Profiles',
        'gate.signedIn': 'Signed in',
        'gate.pinProtected': 'PIN Protected',
        'gate.noPin': 'No PIN',
        'gate.manageHint': 'Tap a profile to edit it. The labels show its status.',
        'gate.addProfile': 'Add Profile',
        'gate.yourBonfire': 'Your Bonfire',
        'gate.guest': 'Guest',
        'gate.bonfireOf': "{name}'s Bonfire",
        'gate.limitReached': '{count}/{max} profiles — limit reached',
        'gate.cantGetPast': "Can't get past this screen?",
        'gate.bonfireProfile': 'Bonfire Profile',
        'gate.addedAccount': 'Added account',
        'gate.dismiss': 'Dismiss',

        'maintenance.title': 'Closed for maintenance',
        'maintenance.adminTitle': 'Closed for maintenance. Only administrators can get in.',
        'maintenance.backBy': 'Back by {time}',
        'maintenance.signOut': 'Sign out',
        'maintenance.checkAgain': 'Check again',
        'maintenance.stillClosed': 'Still closed.',

        'pin.enterProfilePin': 'Enter Profile PIN',
        'pin.numberKeys': 'Use the number keys',
        'pin.enterMasterPin': 'Enter main PIN',
        'pin.incorrectPin': 'Incorrect PIN. Please try again.',
        'pin.incorrectMasterPin': 'Incorrect main PIN. Please try again.',

        'avatar.positionYourPicture': 'Position your picture',
        'avatar.dragOrArrows': 'Drag or arrows to move, slider to zoom. Press OK when it looks right.',
        'avatar.usePicture': 'Use picture',
        'avatar.fromThisServer': 'From this server',
        'avatar.fromThisDevice': 'From this device',
        'avatar.uploadPicture': 'Upload a picture',
        'avatar.uploadHint': 'JPEG, PNG, WebP or GIF. You can position and zoom it after choosing.',
        'avatar.adminLimited': 'Your server administrator has limited profile pictures to the set above.',
        'avatar.profilePicture': 'Profile Picture',
        'avatar.changePicture': 'Change picture',
        'avatar.choosePicture': 'Choose a picture',
        'avatar.avatarLabel': 'Avatar',
        'avatar.zoom': 'Zoom',
        'avatar.chooseFirst': 'Choose a picture first.',
        'avatar.libraryArtworkTitle': 'Library artwork',

        'errors.error': 'Error',
        'errors.validationError': 'Validation Error',
        'errors.unauthorized': 'Unauthorized',
        'errors.failedDeleteDevice': 'Failed to delete device.',
        'errors.failedRenameDevice': 'Failed to rename device.',
        'errors.failedMergeDevice': 'Failed to merge devices.',
        'errors.withMessage': 'Error: {message}',
        'errors.savingProfile': 'Error saving profile: {message}',
        'errors.loadProfileDetails': 'Failed to load profile details: {message}',
        'errors.failedDeleteProfile': 'Failed to delete profile',
        'errors.deletingProfile': 'Error deleting profile: {message}',
        'errors.couldNotSaveArtwork': 'Could not save the artwork.',
        'errors.couldNotSaveThat': 'Could not save that.',
        'errors.heicUnsupported': "HEIC photos aren't supported. Export the photo as JPEG first.",
        'errors.svgUnsupported': "SVG images aren't supported. Use a JPEG, PNG, WebP or GIF.",
        'errors.imageTooLarge': 'That image is over 25 MB. Use a smaller one.',
        'errors.fileUnreadable': 'That file could not be read.',
        'errors.imageEmpty': 'That image appears to be empty.',
        'errors.imageFormatUnsupported': "That image format isn't supported. Save it as a JPEG or PNG first.",
        'errors.imageLoadFailed': 'That image could not be loaded.',

        'profileForm.profileName': 'Profile Name',
        'profileForm.namePlaceholder': 'e.g. Kids',
        'profileForm.masterNameHint': 'The main account takes its name from your Jellyfin account.',
        'profileForm.avatarColor': 'Avatar Color',
        'profileForm.colorHintNoPicture': 'Used as the avatar background when no picture is set.',
        'profileForm.colorHintHasPicture': 'Shows behind the picture, wherever it is transparent.',
        'profileForm.colorHintUnused': 'Not used while the background is transparent.',
        'profileForm.noBackground': 'No background',
        'profileForm.noBackgroundHint': 'For pictures that are cut out rather than rectangular. Leaves the corners clear instead of filling them with the avatar colour. Set automatically when a picture is chosen.',
        'profileForm.pin': 'PIN',
        'profileForm.pinPlaceholderEmpty': 'Leave empty for no PIN',
        'profileForm.pinPlaceholderNew': 'New PIN',
        'profileForm.pinPlaceholderNone': 'No PIN',
        'profileForm.pinPlaceholderUnprotected': 'Unprotected',
        'profileForm.clearPin': 'Clear PIN',
        'profileForm.pinIsSetHint': '🔒 <strong>A PIN is set.</strong> Leave blank to keep it, type a new one to replace it, or use Clear PIN.',
        'profileForm.pinAdminWarning': 'This account administers the server. Its PIN can sign in on TVs and other apps, so keep it long and private.',
        'profileForm.noPinSetHint': 'No PIN set. This profile can be opened by anyone who can reach the switcher.',
        'profileForm.bypassPinLan': 'Skip the PIN on my home network',
        'profileForm.bypassPinHint': 'No PIN prompt on your home network.',
        'profileForm.askPinToOpen': 'Ask for the PIN to open this account',
        'profileForm.askPinToOpenHint': 'Off, it opens without one. The PIN still protects settings.',
        'profileForm.autoLock': 'Auto-lock after inactivity',
        'profileForm.autoLockHintCreate': 'Only applies when this profile has a PIN set.',
        'profileForm.autoLockHintEdit': 'Only applies when a PIN is set on this profile.',
        'profileForm.lockoutNever': 'Never',
        'profileForm.lockout1Min': '1 minute',
        'profileForm.lockout5Min': '5 minutes',
        'profileForm.lockout5MinDefault': '5 minutes (default)',
        'profileForm.lockout10Min': '10 minutes',
        'profileForm.lockout20Min': '20 minutes',
        'profileForm.lockout30Min': '30 minutes',
        'profileForm.lockout1Hour': '1 hour',
        'profileForm.enabledLibraries': 'Enabled Libraries',
        'profileForm.libraries': 'Libraries',
        'profileForm.librariesInheritHint': 'Tick nothing and this profile sees every library your account does.',
        'profileForm.libraryHeader': 'Library',
        'profileForm.artworkHeader': 'Artwork',
        'profileForm.artworkForAria': 'Artwork for {name}',
        'profileForm.removeTagAria': 'Remove tag {tag}',
        'profileForm.artworkDefault': 'Default',
        'profileForm.artworkPicture': 'Picture',
        'profileForm.artworkHidden': 'Hidden',
        'profileForm.artworkChoose': 'Choose',
        'profileForm.artworkToggleLabel': 'Choose the artwork on each library tile',
        'profileForm.artworkExplainer': 'Tiles show what is inside a library. <strong>Picture</strong> or <strong>Hidden</strong> keeps that from this profile.',
        'profileForm.allowedDevices': 'Allowed Devices',
        'profileForm.allDevicesAllowed': 'All Devices Allowed',
        'profileForm.oneDeviceAllowed': '1 Device Allowed',
        'profileForm.devicesAllowed': '{count} Devices Allowed',
        'profileForm.noDevicesYet': 'No devices found for your account yet',
        'profileForm.devicesUnavailable': "Couldn't load your devices. Saving won't change this profile's devices.",
        'profileForm.devicesUnchanged': 'Devices unchanged',
        'profileForm.devicesHint': 'If no devices are selected, this profile can be accessed from any device.',
        'profileForm.unknownDevice': 'Unknown Device',
        'profileForm.noTags': 'No tags — this filter is off',
        'profileForm.unknownClient': 'Unknown Client',
        'profileForm.unknown': 'Unknown',
        'profileForm.deviceLastSeen': '{client} • Last seen {date}',
        'profileForm.forgetDevice': 'Forget this device',
        'profileForm.forgetDeviceAria': 'Forget {name}',
        'profileForm.renameDevice': 'Rename this device',
        'profileForm.renameDeviceAria': 'Rename {name}',
        'profileForm.renameDeviceTitle': 'Rename device',
        'profileForm.renameDeviceLabel': 'Name',
        'profileForm.mergeDevice': 'This is the same device as…',
        'profileForm.mergeDeviceAria': 'Mark {name} as the same device as another',
        'profileForm.mergeDeviceTitle': 'Same device?',
        'profileForm.mergeDeviceMessage': 'These two become one device. Any profile allowed on either one stays allowed.',
        'profileForm.tidyDevices': 'Tidy up duplicates',
        'profileForm.tidyNone': 'No duplicates found.',
        'profileForm.tidyFound': 'Found {count} device(s) that look like duplicates of one you already have. Merge them?',
        'profileForm.maximumRating': 'Maximum rating',
        'profileForm.noRestrictions': 'No Restrictions',
        'profileForm.ratingG': 'G / TV-G (6+)',
        'profileForm.ratingPG': 'PG / TV-PG (10+)',
        'profileForm.ratingPG13': 'PG-13 / TV-14 (14+)',
        'profileForm.ratingR': 'R / TV-MA (17+)',
        'profileForm.ratingCurrent': 'Current limit (level {score})',
        'profileForm.ratingAge': '{labels} ({score}+)',
        'profileForm.theme': 'Theme',
        'profileForm.themeDefault': 'Server default ({name})',
        'profileForm.themeHint': 'How these screens look for this profile.',
        'profileForm.blockedTags': 'Blocked tags',
        'profileForm.blockedTagsHint': 'Hides anything with these tags. A tag on a series or library covers everything inside it.',
        'profileForm.allowedTags': 'Allowed tags',
        'profileForm.allowedTagsHint': '⚠️ Allow-list: if you add any tag here, this profile sees <strong>only</strong> matching items. Untagged content is hidden too.',
        'profileForm.tagPlaceholderAdults': 'e.g. adults',
        'profileForm.tagPlaceholderKids': 'e.g. kids',
        'profileForm.createTitle': 'Create Profile',
        'profileForm.editTitle': 'Edit Profile',
        'profileForm.sectionProfileTitle': 'Profile',
        'profileForm.sectionProfileSubtitle': 'Name, colour, and picture',
        'profileForm.sectionSecurityTitle': 'Security',
        'profileForm.sectionSecuritySubtitle': 'PIN protection and automatic locking',
        'profileForm.sectionRestrictionsTitle': 'Tags',
        'profileForm.sectionRestrictionsSubtitle': 'Block or allow by tag',
        'profileForm.deleteProfile': 'Delete Profile',
        'profileForm.nameRequired': 'Profile name is required.',
        'profileForm.pinMustBeDigits': 'A PIN can only contain digits.',
        'profileForm.pinLengthCreate': 'PIN must be 4–8 digits.',
        'profileForm.pinLengthEdit': 'A PIN must be 4-8 digits — you entered {count}.',
        'profileForm.deleteConfirmMessage': 'Are you sure you want to delete profile "{name}" and its underlying user account? This action is irreversible.',
        'profileForm.deleteConfirmKept': 'Delete profile "{name}"? Its account is kept. An admin sets its password in Dashboard → Users.',
        'profileForm.deleteDeviceTitle': 'Delete Device History',
        'profileForm.deleteDeviceMessage': 'Remove this device? Any access restrictions for it go too.',

        'panic.emergencyDisable': 'Emergency disable',
        'panic.dialogBody': "Enter the code your server administrator set. This shuts the Bonfire switcher off until Jellyfin is restarted — the profile gate disappears and this account is used as-is. It does not unlock anyone else's profile.",
        'panic.emergencyCodePlaceholder': 'Emergency code',
        'panic.disable': 'Disable',
        'panic.checking': 'Checking…',
        'panic.tooManyAttempts': 'Too many attempts. Try again in an hour, or restart Jellyfin.',
        'panic.incorrectCode': 'Incorrect code.',
        'panic.disabledTitle': 'Bonfire disabled',
        'panic.disabledBody': 'The switcher is off until Jellyfin restarts. Reload the page if anything still looks wrong.',

        'settings.title': 'Settings',
        'settings.needsMain': 'Open {name} first, then choose Settings.',
        'mainPin.setTitle': 'Set a PIN for your account',
        'mainPin.setBody': 'Without one, anyone at this screen can change your profiles.',
        'mainPin.requiredBody': 'Your server needs one before you can change profiles or settings.',
        'mainPin.notNow': 'Not now',
        'mainPin.limitsBody': 'Viewing limits need a PIN on your account.',
        'mainPin.limitsOff': 'The profile was added, but its viewing limits are off. Set a PIN, then add them.',
        'mainPin.set': 'Set PIN',
        'onboarding.welcomeTitle': 'Welcome to Bonfire',
        'onboarding.welcomeBody': 'Everyone at home can have their own profile. This takes a minute.',
        'onboarding.start': 'Get started',
        'onboarding.next': 'Next',
        'onboarding.skip': 'Skip',
        'onboarding.step': 'Step {n} of {total}',
        'onboarding.profileTitle': 'Add a profile',
        'onboarding.profileBody': 'Make one for someone else at home. You can add more later.',
        'onboarding.switcherTitle': 'How do you want to switch?',
        'onboarding.switcherLater': 'You can change this later in Settings.',
        'onboarding.themeTitle': 'Pick a look',
        'onboarding.themeBody': 'You can change it later in Settings.',
        'onboarding.emailTitle': 'Add your email',
        'onboarding.emailBody': 'If you forget your PIN, a code can be sent here.',
        'accountEmail.title': 'Email',
        'accountEmail.subtitle': 'For a forgotten PIN and notices',
        'accountEmail.address': 'Email address',
        'accountEmail.send': 'Send code',
        'accountEmail.sentTo': 'Enter the code sent to {address}.',
        'accountEmail.code': 'Code',
        'accountEmail.confirm': 'Confirm',
        'accountEmail.otherAddress': 'Use a different address',
        'accountEmail.change': 'Change',
        'accountEmail.remove': 'Remove',
        'accountEmail.saved': 'Email saved.',
        'accountEmail.removed': 'Email removed.',
        'accountEmail.resetHint': 'If you forget your PIN, a code can be sent here.',
        'accountEmail.notifyLockout': 'Email me when a PIN is locked',
        'accountEmail.notifyNewDevice': 'Email me when a new device signs in',
        'accountEmail.notifyTimeAsked': 'Email me when a profile asks for more time',
        'accountEmail.notifyLimitReached': 'Email me when a profile reaches its daily limit',
        'accountEmail.invalid': 'Enter an email address.',
        'accountEmail.codeInvalid': 'Enter the 6-digit code.',
        'pinReset.forgot': 'Forgot PIN?',
        'pinReset.title': 'Reset your PIN',
        'pinReset.sentTo': 'Enter the code sent to {address}, then a new PIN.',
        'pinReset.newPin': 'New PIN',
        'pinReset.sendAgain': 'Send a new code',
        'settings.profilesTitle': 'Profiles',
        'settings.profilesBody': 'Add, edit and remove profiles.',
        'settings.accountTitle': 'Your account',
        'settings.accountBody': 'Your PIN, home network and devices.',
        'settings.switcherTitle': 'Switcher',
        'settings.switcherStyleBody': 'Where you reach this screen from, and whether it opens on startup.',
        'settings.sharingTitle': 'Sharing',
        'settings.yourBonfireBody': 'Share your profiles with another Bonfire, or join one.',
        'settings.lookTitle': 'Look',
        'settings.themeTitle': 'Theme',
        'settings.themeBody': "How Bonfire's screens look for each profile.",
        'account.signInTitle': 'Sign-in',
        'account.signInSubtitle': 'How your account opens',
        'account.savePin': 'Save PIN',
        'account.pinLabel': 'Main PIN',
        'account.pinSetHint': 'A PIN is set. Type a new one to change it.',
        'account.pinSaved': 'PIN saved.',
        'account.pinCleared': 'PIN removed.',
        'account.saved': 'Saved.',
        'account.devicesTitle': 'Devices',
        'account.devicesCount': 'Known devices: {count}',
        'account.devicesCountSubtitle': 'Which screens use this account',
        'account.manageDevices': 'Manage devices',
        'account.moreSummary': 'Ask for PIN, home network and auto-lock',
        'account.emailOff': 'Email is not set up. Ask your server admin to turn it on in the Bonfire dashboard.',
        'account.devicesSubtitle': 'Rename, merge or forget them',
        'account.zoneTitle': 'Time zone',
        'account.zoneSubtitle': 'For viewing hours',
        'account.zone': 'Time zone',
        'account.zoneDefault': 'Server default ({zone})',
        'viewing.title': 'Viewing hours',
        'viewing.subtitle': 'When and how long this profile can watch',
        'viewing.hoursEnabled': 'Only at these times',
        'viewing.fromAria': '{day} from',
        'viewing.toAria': '{day} until',
        'viewing.midnight': 'Midnight',
        'viewing.daily': 'Daily limit',
        'viewing.weekend': 'Weekend limit',
        'viewing.sameAsWeekdays': 'Same as weekdays',
        'viewing.noLimit': 'No limit',
        'viewing.hint': 'Counts video only. Music is never limited.',
        'viewing.min': '{minutes} min',
        'viewing.h': '{hours} h',
        'viewing.hMin': '{hours} h {minutes} min',
        'viewing.minutesLeft': '{minutes} min left',
        'viewing.until': 'Until {time}',
        'viewing.from': 'From {time}',
        'viewing.timeUp': "Time's up",
        'viewing.asked': 'Asked for more time',
        'viewing.canWatch': 'Can watch now',
        'viewing.usedToday': '{used} of {limit} min used today',
        'viewing.askedHint': '{name} asked for more time.',
        'viewing.moreTime': 'More time now',
        'viewing.decline': 'Say no',
        'viewing.given': 'Given {duration}.',
        'viewing.declined': 'Told no.',
        'viewing.timeUpTitle': "Time's up",
        'viewing.outsideBody': '{name} can watch again at {time}. You can still browse.',
        'viewing.limitBody': "{name} has used today's time. You can still browse.",
        'viewing.notNowBody': "{name} can't watch right now. You can still browse.",
        'viewing.ask': 'Ask for more time',
        'viewing.giveTime': 'Give more time',
        'viewing.browse': 'Browse',
        'viewing.askedTitle': 'Asked',
        'viewing.askedBody': 'A parent can answer from Bonfire.',
        'viewing.alreadyAsked': 'You already asked. Try again in a few minutes.',
        'viewing.giveTitle': 'More time for {name}',
        'viewing.giveBody': 'How long?',
        'account.devicesHint': 'Each profile chooses which of these it can be opened on.',
        'profileForm.sectionPinSubtitle': 'A PIN to open this profile',
        'profileForm.sectionWatchTitle': 'What they can watch',
        'profileForm.sectionWatchSubtitle': 'Rating limit, libraries and devices',
        'profileForm.sectionLockingTitle': 'Locking',
        'profileForm.sectionLockingSubtitle': 'When the PIN is asked again',
        'profileForm.themeSubtitle': "How Bonfire's screens look for this profile",
        'profileForm.moreTitle': 'More',
        'profileForm.moreSummary': 'Auto-lock, tags and theme',
        'profileForm.kidsPreset': 'Kids profile',
        'profileForm.kidsPresetHint': 'Sets the rating to age 10. You can change it.',
        'profileForm.mainSecurityMoved': 'Your PIN and sign-in settings are in Settings → Your account.',
        'themeSettings.default': 'Default',
        'themeSettings.matchJellyfin': 'Use {name} for the rest of Jellyfin on this device too?',
        'themeSettings.matchYes': 'Use it',
        'themeSettings.matchNo': 'No thanks',
        'themeSettings.matchDone': 'Done. It shows from the next page.',
        'theme.bonfire': 'Bonfire',
        'theme.crimson': 'Crimson',
        'theme.starlight': 'Starlight',
        'theme.harbor': 'Harbor',
        'theme.nightfall': 'Nightfall',
        'theme.glass': 'Glass',
        'theme.harvest': 'Harvest',
        'theme.snowfall': 'Snowfall',
        'theme.bloom': 'Bloom',
        'theme.jellyfin-dark': 'Jellyfin Dark',
        'theme.jellyfin-light': 'Jellyfin Light',
        'theme.high-contrast': 'High contrast',

        'switcher.title': 'Switcher Style',
        'switcher.intro': 'How you reach this screen, for everyone using your account.',
        'switcher.askOnStartup': 'Ask "Who\'s watching?" on startup',
        'switcher.askOnStartupHint': 'Shown once when the app opens — not every time you return to the home screen.',
        'switcher.whereToSwitchFrom': 'Where to switch from',
        'switcher.bonfireButtonTitle': 'Bonfire button',
        'switcher.bonfireButtonBody': "A separate switcher button in the header, next to Jellyfin's own profile icon.",
        'switcher.menuTitle': "Jellyfin's user menu",
        'switcher.menuBody': 'Adds "Switch Profile" above Sign out in Jellyfin\'s own menu, and to your profile page. Removes the second header icon.',
        'switcher.switchProfile': 'Switch Profile',
        'switcher.reloadFailedTitle': 'Almost there',
        'switcher.reloadFailedBody': 'Close and reopen the app to finish switching profiles.',
        'switcher.notSavedTitle': 'Switch not saved',
        'switcher.notSavedBody': 'This device would not store the change, so you are still signed in as before.',
        'switcher.manageTitle': 'Manage Profiles',
        'switcher.manageNeedsMaster': 'Open {name} first, then choose Manage Profiles.',

        'bonfire.yourBonfireTitle': 'Sharing',
        'links.title': 'Other accounts on this screen',
        'links.subtitle': 'Their tile opens their own account',
        'links.none': 'None yet.',
        'links.username': 'Their username',
        'links.password': 'Their password',
        'links.hint': "They type it once. It isn't saved.",
        'links.add': 'Add account',
        'links.needBoth': 'Enter their username and password.',
        'links.added': '{name} added.',
        'links.withPin': 'Opens with their PIN',
        'links.noPin': 'Opens without a PIN',
        'links.remove': 'Remove',
        'links.removeAria': 'Remove {name}',
        'links.removeTitle': 'Remove {name}?',
        'links.removeBody': 'Their tile leaves this screen. Adding it again needs their password.',
        'links.removed': 'Removed.',
        'links.shownTitle': 'Your account on other screens',
        'links.leave': 'Leave',
        'links.leaveAria': "Leave {name}'s screen",
        'links.leaveTitle': "Leave {name}'s screen?",
        'links.leaveBody': 'Your tile leaves their screen.',
        'links.manageBody': "{name}'s own account, added to this screen.",
        'bonfire.hostedTitle': 'Your Bonfire',
        'bonfire.shareCode': 'Share this code to invite someone to your Bonfire:',
        'bonfire.members': 'Members ({count})',
        'bonfire.kick': 'Remove',
        'bonfire.noMembersYet': 'No members joined yet.',
        'bonfire.deleteGroup': 'Delete Bonfire',
        'bonfire.hostTitle': 'Host a Bonfire',
        'bonfire.hostBody': 'Start a Bonfire and invite others to see your profiles.',
        'bonfire.generateJoinCode': 'Get an invite code',
        'bonfire.generating': 'Generating…',
        'bonfire.joinedTitle': 'Joined Bonfire',
        'bonfire.joinedOwnedBy': 'You have joined a Bonfire owned by:',
        'bonfire.accessEachOther': "You can open each other's profiles from the picker.",
        'bonfire.leaveGroup': 'Leave Bonfire',
        'bonfire.joinTitle': 'Join a Bonfire',
        'bonfire.joinBody': 'Enter an invite code to join a Bonfire:',
        'bonfire.joinCodePlaceholder': 'e.g. B7F8XA',
        'bonfire.joining': 'Joining…',
        'bonfire.pleaseEnterCode': 'Please enter a 6-character code.',
        'bonfire.tooManyJoinAttempts': 'Too many failed attempts. Try again in 15 minutes.',
        'bonfire.failedToJoin': 'Could not join the Bonfire.',
        'bonfire.leaveCurrentTitle': 'Leave your current Bonfire?',
        'bonfire.leaveCurrentBody': 'Joining this Bonfire removes you from your current one.',
        'bonfire.kickConfirmTitle': 'Remove member',
        'bonfire.kickConfirmBody': 'Remove this user from your Bonfire? Your code changes so they can’t rejoin with it.',
        'bonfire.newCode': 'New code',
        'bonfire.newCodeConfirmTitle': 'Replace Code',
        'bonfire.newCodeConfirmBody': 'The old code stops working. Members already in your Bonfire stay in.',
        'bonfire.failedNewCode': 'Could not replace the code.',
        'bonfire.failedKick': 'Could not remove the member.',
        'bonfire.deleteGroupConfirmTitle': 'Delete Bonfire',
        'bonfire.deleteGroupConfirmBody': 'Delete your Bonfire? Members drop out of your picker.',
        'bonfire.failedDeleteGroup': 'Could not delete the Bonfire.',
        'bonfire.leaveGroupConfirmTitle': 'Leave Bonfire',
        'bonfire.leaveGroupConfirmBody': 'Leave this Bonfire? You will stop sharing profiles.',
        'bonfire.failedLeaveGroup': 'Could not leave the Bonfire.',
        'bonfire.failedGenerateCode': 'Failed to generate code: {message}',
        'bonfire.failedLoadStatus': 'Failed to load Bonfire status: {message}',
        'bonfire.unknownUser': 'Unknown User',
        'bonfire.hideMine': 'Hide my profiles from others',
        'bonfire.hideMineHint': 'Others see only my main account.',
        'bonfire.hideOthers': "Hide other people's profiles from me",
        'bonfire.hideOthersHint': 'You see only their main accounts.',
        'bonfire.lanSwitchLabel': 'Let Bonfire members open my account on this network',
        'bonfire.lanSwitchHint': 'No PIN needed on your home network, for your account or your profiles. Away from home it still is{extra}.',
        'bonfire.lanSwitchHintExtra': ', and anything without a PIN cannot be opened remotely at all',
        'bonfire.settingsSaved': 'Saved.',
        'bonfire.settingsSaveFailed': 'Could not save. Nothing was changed.',
        'bonfire.adminAccountWarning': 'This is an admin account.',
        'bonfire.adminAccountWarningBody': 'Whoever switches into it gets your admin rights.',
        'bonfire.proxyHint': 'Behind a reverse proxy, check Networking → Known Proxies first, or everyone looks local.',
        'bonfire.allowHouseholdTitle': 'Allow household switching?',
        'bonfire.allowHouseholdBody': 'Anyone in your Bonfire can open your account on your home network without your PIN.',

        'profilePage.title': 'Bonfire Profiles',
        'profilePage.body': 'Currently watching as <strong>{name}</strong>. Switch to another profile in your household without signing out.',
        'profilePage.thisAccount': 'this account',
    };

    /// The active language's strings. Starts out equal to EN_STRINGS — every lookup
    /// already resolves correctly before loadLocale() has done anything — and is
    /// swapped wholesale, never merged, once a translation arrives: a partial file
    /// would otherwise leave a stale mix of two languages on screen.
    let activeStrings = EN_STRINGS;

    /// Looks up `key`, filling in `{token}` placeholders from `vars`. Falls back to the
    /// English string, then to the key itself, so a translation file missing a newer
    /// key degrades to English rather than showing nothing.
    ///
    /// `vars` values are interpolated as-is: pass already-escaped HTML for anything
    /// that ends up in a template destined for innerHTML, exactly as callers already do
    /// for user-supplied text like profile and device names.
    function t(key, vars) {
        let str = (activeStrings && activeStrings[key]) || EN_STRINGS[key] || key;
        if (vars) {
            Object.keys(vars).forEach(k => {
                str = str.split('{' + k + '}').join(vars[k]);
            });
        }
        return str;
    }

    /// The language Jellyfin is showing this user, as a tag ('fr', 'pt-br'), or '' if
    /// there is nothing to go on.
    ///
    /// Bonfire sits inside Jellyfin's own pages, so it speaks the language those pages
    /// do: Settings → Display → Display language. Up to 1.7.0 it used the browser's
    /// language list instead, so a French browser with Jellyfin set to English got
    /// French profile screens around an English page, and the setting did nothing.
    ///
    /// Read in the order jellyfin-web itself decides (src/lib/globalize,
    /// updateCurrentCulture, verified against release-10.11.z):
    ///
    ///   1. The setting. userSettings.language() is stored per device in localStorage
    ///      as '<userId>-language' (appSettings.#getKey). Read directly because <html
    ///      lang> lags it: globalize runs once at import, before anyone is signed in, and
    ///      only catches up on 'localusersignedin', which can land after the gate has
    ///      drawn.
    ///   2. <html lang>, which globalize writes from the same setting. Used when there is
    ///      no user id to read the setting under yet.
    ///   3. "Auto", which is an empty setting: jellyfin-web's getDefaultLanguage, which is
    ///      data-culture (set by the native app shells) and then navigator.language. That
    ///      is the device's language, but only because it is Jellyfin's too. Note it is
    ///      navigator.language, not the navigator.languages list, so a second preference
    ///      Jellyfin never shows does not get picked up here either.
    function jellyfinLanguage() {
        const root = document.documentElement;
        const attr = name => (root && root.getAttribute && root.getAttribute(name)) || '';

        let userId = '';
        try {
            if (typeof ApiClient !== 'undefined' && ApiClient && ApiClient.getCurrentUserId) {
                userId = ApiClient.getCurrentUserId() || '';
            }
        } catch (e) { /* not signed in yet */ }

        let lang = '';
        if (userId) {
            try { lang = localStorage.getItem(userId + '-language') || ''; } catch (e) { /* storage blocked */ }
        } else {
            lang = attr('lang');
        }
        if (!lang) lang = attr('data-culture') || navigator.language || '';

        // globalize.normalizeLocaleName: en_US and en-US are the same language to it.
        return String(lang).replace('_', '-').toLowerCase();
    }

    /// The translation this plugin ships for Jellyfin's language, or null to stay on
    /// English.
    ///
    /// A full tag wins over its base language, so pt-BR picks the Brazilian file when
    /// there is one and falls back to pt when there is not. Slicing to two characters —
    /// which is what this did first — made a regional translation unreachable and would
    /// have handed a pt-BR reader a pt-PT file without either of them choosing that.
    function detectLocale() {
        const tag = jellyfinLanguage();
        if (!tag) return null;

        // Locale codes reach a URL below. They come from the server rather than from
        // anything a visitor controls, but the shape is checked anyway — this is the one
        // place a bad value could turn into a request for something else.
        const have = SUPPORTED_LOCALES
            .filter(c => typeof c === 'string' && /^[a-z]{2,3}(-[a-z0-9]{2,8})*$/i.test(c));
        const lower = have.map(c => c.toLowerCase());

        // Longest match first, dropping one subtag at a time: zh-Hans-CN, then
        // zh-Hans, then zh. Trying only the full tag and the bare language would
        // skip straight past a zh-Hans file for a Jellyfin set to zh-Hans-CN.
        const parts = tag.split('-');
        for (let n = parts.length; n >= 1; n--) {
            const i = lower.indexOf(parts.slice(0, n).join('-'));
            if (i !== -1) return have[i];
        }
        return null;
    }

    /// The locale whose strings are in activeStrings, or null for English. Lets a change
    /// of language that lands on the same file skip the fetch.
    let activeLocale = null;
    /// Bumped on every load, so a slow response for a language the user has since moved
    /// off cannot land on top of the one they moved to.
    let localeLoadSeq = 0;

    /// Fetches the detected locale's strings and swaps them in. Always resolves —
    /// nothing here is worth failing startup over. No detected/supported locale, a
    /// missing file, or a network error all leave English active.
    ///
    /// Runs again whenever Jellyfin's language changes (watchJellyfinLanguage), so it
    /// has to be able to go back to English as well as away from it.
    function loadLocale() {
        const locale = detectLocale();
        const seq = ++localeLoadSeq;
        if (locale === activeLocale) return Promise.resolve();
        if (!locale) {
            activeStrings = EN_STRINGS;
            activeLocale = null;
            return Promise.resolve();
        }

        return fetch(pluginUrl('/plugins/profiles/i18n/' + locale + '.json'))
            .then(res => res.ok ? res.json() : null)
            .then(strings => {
                if (seq !== localeLoadSeq) return;
                if (strings && typeof strings === 'object') {
                    activeStrings = strings;
                    activeLocale = locale;
                }
            })
            .catch(() => { /* stay on what is showing */ });
    }

    /// Calls `onChange` whenever jellyfin-web rewrites <html lang>: on sign-in, sign-out
    /// and when the Display language setting is saved, which does not reload the page.
    /// An attribute observer on one element costs nothing between changes, and nothing
    /// is added to the route poll (see checkRoute). MutationObserver is Chromium 26, well
    /// inside webOS 4.5.
    function watchJellyfinLanguage(onChange) {
        if (typeof MutationObserver === 'undefined' || !document.documentElement) return;
        new MutationObserver(onChange)
            .observe(document.documentElement, { attributes: true, attributeFilter: ['lang'] });
    }

    // The startup cover: the page painted in the gate's background from before the first
    // frame until the gate is on top of it. Normally raised by the head script in
    // index.html (WebInjection.HeadScript), which carries this exact rule; this copy is
    // for a page without it — the Tizen package loads this script from the server long
    // after its own index.html has run. tests/js/gatecover.js fails if the two differ.
    //
    // The colour is the gate's, through --jpf-gate-bg so a theme can set it in one place.
    // The plain colour first is for an engine without custom properties, which would
    // otherwise drop the whole declaration and paint nothing.
    const GATE_COVER_CSS = 'html[data-jpf-cover]::before{position:fixed;top:0;left:0;right:0;bottom:0;z-index:99998;background:#121521;background:var(--jpf-gate-bg,radial-gradient(ellipse at 50% 100%,rgba(255,154,74,0.278) 0%,rgba(255,154,74,0) 55%),linear-gradient(180deg,#0f1220 0%,#1b1925 55%,#33201a 100%));content:""}';

    // How long the cover may stay up without the gate arriving. Each holder (the head
    // script, and profiles.js once it starts fetching the list) gets this long; a cover
    // that outlives it comes down and the page shows whatever it has. A flash is the
    // failure this exists to fix; a screen that stays blank is a much worse one.
    const GATE_COVER_MS = 3000;

    const ProfilesPlugin = {
        config: {
            masterStorageKey: 'jellyfin_profiles_master_state',
            activeSessionKey: 'jellyfin_profiles_active_token',
            // Key set before window.location.reload() so the early-hide
            // inline head script can suppress the page flash on the next load.
            switchingKey: 'jpf-sw',
            // The background colour and colour-scheme of the page being left, written
            // alongside switchingKey and read by the head script on the next load. See
            // _captureLeavingBackground.
            backgroundKey: 'jpf-bg',
            // Set when the emergency disable succeeds. profiles.js is served with a
            // five-minute cache, so a reload inside that window can re-run this very script
            // from cache — the marker is what stops it coming back to life.
            panicKey: 'jellyfin_profiles_disabled',
            // Per-account cache of the library tile artwork rules, applied before the
            // server answers so the real artwork never gets a frame on screen.
            libraryArtKey: 'jellyfin_profiles_library_art',
            // Local mirror of the account's gate/native preference, so checkRoute can decide
            // whether to raise the gate without waiting on a request.
            switcherModeKey: 'jellyfin_profiles_switcher_mode',
            // Retired in 1.7.2. It held "this device keeps its own answer to the startup
            // question", which was cut: ask-on-startup is one answer per account. Nothing
            // reads it; init removes it once so an old answer cannot linger on a TV.
            deviceGateKey: 'jellyfin_profiles_device_gate',
            // The last theme this browser was told for an account, so the first paint is
            // already in it rather than switching a moment after the gate appears.
            themeKey: 'jellyfin_profiles_theme',
            // The last ?part=custom answer, so a profile on a custom theme starts in it
            // instead of in Bonfire until the server answers.
            customThemesKey: 'jellyfin_profiles_custom_themes'
        },
        pluginId: 'b1462fca-774b-4b13-8d02-e2d4f2bc18b9',
        isManageMode: false,
        masterPin: null,
        cachedProfiles: [],
        currentProfiles: [],
        inactivityTimer: null,
        inactivityEventHandlers: null,
        _pageRevealed: false,
        _switchLock: false,
        _switcherPrefs: null,
        _switcherPrefsLoading: false,
        // How long a request the route poll makes on its own is left alone after it
        // fails. The poll runs every 500 ms, so without this a failing server was asked
        // twice a second by every open page — exactly when it could least afford it.
        POLL_RETRY_MS: 30000,
        // /list requests out now, when the oldest of them left, and when the poll may ask
        // again after a failure. See _requestList.
        _listInFlight: 0,
        _listSentAt: 0,
        _listRetryAt: 0,
        // { theme, defaultTheme, allow } from the server, or null until it answers. The
        // profile form offers a theme picker only once this says choosing is allowed.
        _themeSettings: null,
        _panicDisabled: false,
        // Null until the server answers. Only true reveals the emergency link.
        _panicLinkAvailable: null,
        _overlayTrap: null,
        // Set once the artwork rules have been fetched for this page load.
        _libraryArtLoaded: false,

        getAuthHeaders: function (token) {
            const apiClient = ApiClient;
            const client = typeof apiClient.appName === 'function' ? apiClient.appName() : (apiClient.appName || apiClient._appName || 'Jellyfin Web');
            const device = typeof apiClient.deviceName === 'function' ? apiClient.deviceName() : (apiClient.deviceName || apiClient._deviceName || 'Chrome');
            const deviceId = typeof apiClient.deviceId === 'function' ? apiClient.deviceId() : (apiClient.deviceId || apiClient._deviceId || '');
            const version = typeof apiClient.appVersion === 'function' ? apiClient.appVersion() : (apiClient.appVersion || apiClient._appVersion || '');

            return {
                'Authorization': `MediaBrowser Client="${client}", Device="${device}", DeviceId="${deviceId}", Version="${version}", Token="${token}"`
            };
        },

        /// Object.assign rather than object spread: spread is Chromium 60, and webOS 4.5
        /// (Chromium 53) rejects the whole file over it.
        jsonHeaders: function (token) {
            return Object.assign({ 'Content-Type': 'application/json' }, this.getAuthHeaders(token));
        },

        /// Headers for a call that changes the household: devices, sharing, switcher
        /// settings. The server checks the main PIN on every one of them, and most have no
        /// body to put it in, so it goes in a header once Manage Profiles or Settings has
        /// asked for it. Pass json false for a call with no body.
        manageHeaders: function (token, json) {
            const headers = json === false ? this.getAuthHeaders(token) : this.jsonHeaders(token);
            if (this.masterPin) headers['X-Bonfire-Master-Pin'] = this.masterPin;
            return headers;
        },

        /// A one-field dialog. Used where a value has to be typed rather than chosen, which
        /// so far is renaming a device.
        ///
        /// Not window.prompt(): it is blocked in several of the environments this runs in
        /// (and silently returns null in a WebView), it cannot be reached with a D-pad on a
        /// television, and it renders outside the focus trap the rest of the overlay lives
        /// inside. The input is focused and selected on open so a rename is type-and-enter.
        showPromptDialog: function (title, label, initialValue, onSubmit) {
            const dialog = document.createElement('div');
            dialog.id = 'profiles-prompt-dialog';
            dialog.classList.add('jpf-trap-surface');
            dialog.style.cssText = `
                position: fixed;
                top: 0;
                left: 0;
                right: 0;
                bottom: 0;
                background: rgba(0, 0, 0, 0.82);
                backdrop-filter: blur(8px);
                display: flex;
                align-items: center;
                justify-content: center;
                z-index: ${DIALOG_Z};
                opacity: 0;
                transition: opacity 0.15s ease-out;
            `;

            dialog.innerHTML = `
                <div class="prompt-dialog-content" style="
                    background: var(--jpf-panel);
                    border: 1px solid var(--jpf-border-1);
                    border-radius: var(--jpf-r-md);
                    padding: 24px;
                    max-width: 420px;
                    width: 90%;
                    box-shadow: 0 10px 30px rgba(0,0,0,0.5);
                ">
                    <h2 style="margin-top: 0; color: var(--jpf-text-1); font-size: 1.25rem; font-weight: 700; margin-bottom: 12px;">${escapeHtml(title)}</h2>
                    <label for="profiles-prompt-input" style="display: block; color: var(--jpf-text-2); font-size: 0.85rem; margin-bottom: 6px;">${escapeHtml(label)}</label>
                    <input id="profiles-prompt-input" type="text" maxlength="64" value="${escapeHtml(initialValue || '')}" style="width: 100%; box-sizing: border-box; margin-bottom: 20px;" />
                    <div class="jpf-actions">
                        <button id="prompt-cancel-btn" type="button" class="profiles-btn">${t('common.cancel')}</button>
                        <button id="prompt-save-btn" type="button" class="profiles-btn btn-primary">${t('common.save')}</button>
                    </div>
                </div>
            `;

            document.body.appendChild(dialog);
            requestAnimationFrame(() => requestAnimationFrame(() => {
                dialog.style.opacity = '1';
            }));

            const close = () => {
                dialog.style.opacity = '0';
                setTimeout(() => dialog.remove(), 160);
            };

            const input = dialog.querySelector('#profiles-prompt-input');
            const submit = () => {
                const value = (input.value || '').trim();
                if (!value) return;
                close();
                if (typeof onSubmit === 'function') onSubmit(value);
            };

            dialog.querySelector('#prompt-cancel-btn').addEventListener('click', close);
            dialog.querySelector('#prompt-save-btn').addEventListener('click', submit);

            dialog.addEventListener('keydown', (e) => {
                if (e.key === 'Escape') { e.preventDefault(); close(); }
                else if (e.key === 'Enter') { e.preventDefault(); submit(); }
            });

            input.focus();
            input.select();
        },

        /// A dialog that asks which one. `options` is [{ id, name }]; the chosen id is
        /// handed back. Built on a real <select> so a television's D-pad and a phone's
        /// native picker both work without any focus handling of our own.
        showSelectDialog: function (title, message, options, onSubmit) {
            const dialog = document.createElement('div');
            dialog.id = 'profiles-select-dialog';
            dialog.classList.add('jpf-trap-surface');
            dialog.style.cssText = `
                position: fixed;
                top: 0;
                left: 0;
                right: 0;
                bottom: 0;
                background: rgba(0, 0, 0, 0.82);
                backdrop-filter: blur(8px);
                display: flex;
                align-items: center;
                justify-content: center;
                z-index: ${DIALOG_Z};
                opacity: 0;
                transition: opacity 0.15s ease-out;
            `;

            const choices = options.map(o =>
                `<option value="${escapeHtml(o.id)}">${escapeHtml(o.name)}</option>`).join('');

            dialog.innerHTML = `
                <div class="select-dialog-content" style="
                    background: var(--jpf-panel);
                    border: 1px solid var(--jpf-border-1);
                    border-radius: var(--jpf-r-md);
                    padding: 24px;
                    max-width: 420px;
                    width: 90%;
                    box-shadow: 0 10px 30px rgba(0,0,0,0.5);
                ">
                    <h2 style="margin-top: 0; color: var(--jpf-text-1); font-size: 1.25rem; font-weight: 700; margin-bottom: 12px;">${escapeHtml(title)}</h2>
                    <p style="color: var(--jpf-text-2); font-size: 0.92rem; line-height: 1.5; margin-bottom: 16px;">${escapeHtml(message)}</p>
                    <select id="profiles-select-input" style="width: 100%; box-sizing: border-box; margin-bottom: 20px;">
                        ${choices}
                    </select>
                    <div class="jpf-actions">
                        <button id="select-cancel-btn" type="button" class="profiles-btn">${t('common.cancel')}</button>
                        <button id="select-confirm-btn" type="button" class="profiles-btn btn-primary">${t('common.ok')}</button>
                    </div>
                </div>
            `;

            document.body.appendChild(dialog);
            requestAnimationFrame(() => requestAnimationFrame(() => {
                dialog.style.opacity = '1';
            }));

            const close = () => {
                dialog.style.opacity = '0';
                setTimeout(() => dialog.remove(), 160);
            };

            const select = dialog.querySelector('#profiles-select-input');
            dialog.querySelector('#select-cancel-btn').addEventListener('click', close);
            dialog.querySelector('#select-confirm-btn').addEventListener('click', () => {
                const value = select.value;
                close();
                if (value && typeof onSubmit === 'function') onSubmit(value);
            });

            dialog.addEventListener('keydown', (e) => {
                if (e.key === 'Escape') { e.preventDefault(); close(); }
            });

            select.focus();
        },

        showConfirmDialog: function (title, message, onConfirm, onCancel) {
            const dialog = document.createElement('div');
            dialog.id = 'profiles-confirm-dialog';
            dialog.classList.add('jpf-trap-surface');
            dialog.style.cssText = `
                position: fixed;
                top: 0;
                left: 0;
                right: 0;
                bottom: 0;
                background: rgba(0, 0, 0, 0.82);
                backdrop-filter: blur(8px);
                display: flex;
                align-items: center;
                justify-content: center;
                z-index: ${DIALOG_Z};
                opacity: 0;
                transition: opacity 0.15s ease-out;
            `;

            dialog.innerHTML = `
                <div class="confirm-dialog-content" style="
                    background: var(--jpf-panel);
                    border: 1px solid var(--jpf-border-1);
                    border-radius: var(--jpf-r-md);
                    padding: 24px;
                    max-width: 420px;
                    width: 90%;
                    box-shadow: 0 10px 30px rgba(0,0,0,0.5);
                    text-align: center;
                ">
                    <h2 style="margin-top: 0; color: var(--jpf-text-1); font-size: 1.25rem; font-weight: 700; margin-bottom: 12px;">${title}</h2>
                    <p style="color: var(--jpf-text-2); font-size: 0.92rem; line-height: 1.5; margin-bottom: 24px;">${message}</p>
                    <div class="jpf-actions">
                        <button id="dialog-cancel-btn" class="profiles-btn btn-secondary" style="padding: 10px 20px; font-weight: 600; min-width: 100px;">${t('common.cancel')}</button>
                        <button id="dialog-confirm-btn" class="profiles-btn btn-danger" style="padding: 10px 20px; font-weight: 600; min-width: 100px;">${t('common.confirm')}</button>
                    </div>
                </div>
            `;

            document.body.appendChild(dialog);
            
            requestAnimationFrame(() => requestAnimationFrame(() => {
                dialog.style.opacity = '1';
            }));

            const closeDialog = () => {
                dialog.style.opacity = '0';
                setTimeout(() => dialog.remove(), 160);
            };

            const confirmBtn = dialog.querySelector('#dialog-confirm-btn');
            const cancelBtn = dialog.querySelector('#dialog-cancel-btn');

            confirmBtn.addEventListener('click', () => {
                closeDialog();
                if (typeof onConfirm === 'function') onConfirm();
            });

            cancelBtn.addEventListener('click', () => {
                closeDialog();
                if (typeof onCancel === 'function') onCancel();
            });

            cancelBtn.focus();
            
            dialog.addEventListener('keydown', (e) => {
                if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
                    e.preventDefault();
                    if (document.activeElement === confirmBtn) {
                        cancelBtn.focus();
                    } else {
                        confirmBtn.focus();
                    }
                }
                if (e.key === 'Escape') {
                    e.preventDefault();
                    cancelBtn.click();
                }
            });
        },

        showAlert: function (title, message, onClose) {
            const dialog = document.createElement('div');
            dialog.id = 'profiles-alert-dialog';
            dialog.classList.add('jpf-trap-surface');
            dialog.style.cssText = `
                position: fixed;
                top: 0;
                left: 0;
                right: 0;
                bottom: 0;
                background: rgba(0, 0, 0, 0.82);
                backdrop-filter: blur(8px);
                display: flex;
                align-items: center;
                justify-content: center;
                z-index: ${DIALOG_Z};
                opacity: 0;
                transition: opacity 0.15s ease-out;
            `;

            dialog.innerHTML = `
                <div class="alert-dialog-content" style="
                    background: var(--jpf-panel);
                    border: 1px solid var(--jpf-border-1);
                    border-radius: var(--jpf-r-md);
                    padding: 24px;
                    max-width: 420px;
                    width: 90%;
                    box-shadow: 0 10px 30px rgba(0,0,0,0.5);
                    text-align: center;
                ">
                    <h2 style="margin-top: 0; color: var(--jpf-text-1); font-size: 1.25rem; font-weight: 700; margin-bottom: 12px;">${title}</h2>
                    <p style="color: var(--jpf-text-2); font-size: 0.92rem; line-height: 1.5; margin-bottom: 24px;">${message}</p>
                    <div style="display: flex; justify-content: center;">
                        <button id="dialog-close-btn" class="profiles-btn btn-primary" style="padding: 10px 24px; font-weight: 600; min-width: 120px;">${t('common.ok')}</button>
                    </div>
                </div>
            `;

            document.body.appendChild(dialog);
            
            requestAnimationFrame(() => requestAnimationFrame(() => {
                dialog.style.opacity = '1';
            }));

            const closeDialog = () => {
                dialog.style.opacity = '0';
                setTimeout(() => dialog.remove(), 160);
            };

            const closeBtn = dialog.querySelector('#dialog-close-btn');

            closeBtn.addEventListener('click', () => {
                closeDialog();
                if (typeof onClose === 'function') onClose();
            });

            closeBtn.focus();

            dialog.addEventListener('keydown', (e) => {
                if (e.key === 'Escape' || e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    closeBtn.click();
                }
            });
        },

        /// A dialog with a row of choices. `actions` is [{ label, primary, run }]; the
        /// dialog closes before `run` is called. Escape closes it with no choice. The arrow
        /// keys move along the buttons, for a remote.
        showActionsDialog: function (title, message, actions) {
            // The TV Back key clicks the dialog's cancel button (_dismissSurface), so it has
            // to have an id. The cancel is the first action with nothing to run: choosing it
            // only closes the dialog, which is exactly what Back should do.
            let cancelAt = -1;
            actions.forEach((a, i) => { if (cancelAt < 0 && !(a && typeof a.run === 'function')) cancelAt = i; });
            const dialog = document.createElement('div');
            dialog.id = 'profiles-actions-dialog';
            dialog.classList.add('jpf-trap-surface');
            dialog.style.cssText = `
                position: fixed;
                top: 0;
                left: 0;
                right: 0;
                bottom: 0;
                background: rgba(0, 0, 0, 0.82);
                backdrop-filter: blur(8px);
                display: flex;
                align-items: center;
                justify-content: center;
                z-index: ${DIALOG_Z};
                opacity: 0;
                transition: opacity 0.15s ease-out;
            `;
            dialog.innerHTML = `
                <div class="confirm-dialog-content" style="
                    background: var(--jpf-panel);
                    border: 1px solid var(--jpf-border-1);
                    border-radius: var(--jpf-r-md);
                    padding: 24px;
                    max-width: 460px;
                    width: 90%;
                    box-shadow: 0 10px 30px rgba(0,0,0,0.5);
                    text-align: center;
                ">
                    <h2 style="margin-top: 0; color: var(--jpf-text-1); font-size: 1.25rem; font-weight: 700; margin-bottom: 12px;">${escapeHtml(title)}</h2>
                    <p style="color: var(--jpf-text-2); font-size: 0.92rem; line-height: 1.5; margin-bottom: 24px;">${escapeHtml(message)}</p>
                    <div class="jpf-actions jpf-actions-wrap">
                        ${actions.map((a, i) => `<button type="button"${i === cancelAt ? ' id="actions-cancel-btn"' : ''} data-action="${i}" class="profiles-btn ${a.primary ? 'btn-primary' : 'btn-secondary'}">${escapeHtml(a.label)}</button>`).join('')}
                    </div>
                </div>
            `;
            document.body.appendChild(dialog);
            requestAnimationFrame(() => requestAnimationFrame(() => {
                dialog.style.opacity = '1';
            }));

            const close = () => {
                dialog.style.opacity = '0';
                setTimeout(() => dialog.remove(), 160);
            };
            const buttons = Array.prototype.slice.call(dialog.querySelectorAll('button[data-action]'));
            buttons.forEach(btn => btn.addEventListener('click', () => {
                const action = actions[parseInt(btn.getAttribute('data-action'), 10)];
                close();
                if (action && typeof action.run === 'function') action.run();
            }));
            dialog.addEventListener('keydown', (e) => {
                const at = buttons.indexOf(document.activeElement);
                if (e.key === 'Escape') { e.preventDefault(); close(); }
                else if ((e.key === 'ArrowRight' || e.key === 'ArrowDown') && buttons.length) {
                    e.preventDefault();
                    buttons[(at + 1) % buttons.length].focus();
                } else if ((e.key === 'ArrowLeft' || e.key === 'ArrowUp') && buttons.length) {
                    e.preventDefault();
                    buttons[(at - 1 + buttons.length) % buttons.length].focus();
                }
            });
            const first = buttons.filter((b, i) => actions[i].primary)[0] || buttons[0];
            if (first) first.focus();
        },

        /// Writes a token and user into jellyfin-web's own stored credentials, and returns
        /// whether it actually managed to.
        ///
        /// The return value is the point of this function. Everything else about a switch
        /// can succeed — the server issues the token, ApiClient takes it, the page
        /// reloads — and the profile still not be the one that comes back, because this is
        /// the only part that survives the reload. It used to fail silently in two ways,
        /// both of which look from outside exactly like "it lets me pick a profile but
        /// loads my own account anyway", which is issue #16:
        ///
        ///   - The server was matched with `server.Id === currentServerId`, a raw string
        ///     comparison of two GUIDs. Every other id comparison in this file goes
        ///     through normalizeGuid because casing and dashes vary by where the value
        ///     came from. With more than one server stored — an address and a hostname for
        ///     the same box is the common way that happens — a mismatch updated nothing
        ///     and reported nothing.
        ///   - Writing to localStorage can be accepted and discarded (a full quota, a
        ///     television in a private-browsing-like mode). Nothing read it back.
        ///
        /// So: match by id, fall back to the address ApiClient is actually talking to,
        /// fall back to a single stored server, and then read back what was written. The
        /// caller decides what to do with a false; it must not be a reload.
        /// The household this device belongs to, and the session to act as.
        ///
        /// Only the master account's id is stored. Its TOKEN used to be kept here too, in
        /// localStorage, for as long as any profile was in use — so anybody at that browser
        /// with the developer tools open, or any other script running on the page, had the
        /// master account: every PIN, every parental control and every library restriction
        /// on the profiles was a picture over it. Closing the tab even put it back in use
        /// behind the gate.
        ///
        /// Nothing needs it. The server resolves the household from whatever session calls
        /// it, so listing and switching work from a profile's own token, and entering the
        /// master goes through /switch with the master's PIN like entering anything else.
        /// `token` here is simply the session in use now.
        readMasterState: function () {
            let stored = null;
            try {
                stored = JSON.parse(localStorage.getItem(this.config.masterStorageKey) || 'null');
            } catch (e) { /* unreadable: treat as unknown */ }
            if (!stored || !stored.masterUserId) return null;

            let token = null;
            try { token = (typeof ApiClient !== 'undefined' && ApiClient) ? ApiClient.accessToken() : null; } catch (e) { /* no client yet */ }

            // Left behind by an earlier version. Take it out of the browser, and if it is not
            // the session in use, end it on the server too: it is a live master session that
            // nothing on this device should be holding any more.
            if (stored.masterToken) {
                if (stored.masterToken !== token) this._endSession(stored.masterToken);
                this.writeMasterState(stored.masterUserId);
            }

            return { masterUserId: stored.masterUserId, token: token };
        },

        /// Makes the stored master agree with a list the server has just sent.
        ///
        /// The grid groups profiles by this id and the manage check compares against it,
        /// so when it is missing the household's own profiles are drawn as a linked
        /// "<master>'s Bonfire" and Manage Profiles tells the master to open the master.
        /// Seen on the iPad app. The list is the server's answer to "whose household is
        /// this" for the session in use — the same rule fetchAndRenderProfiles applies when
        /// it stores the id — so it wins. Only an id is stored, and every action is still
        /// authorised by the server.
        ///
        /// Safe only because no list from a previous account survives to be rendered: the
        /// sign-in screen drops the in-memory one (evaluateFloatingBubbleVisibility).
        _syncMasterFromProfiles: function (masterProfile) {
            if (!masterProfile || !masterProfile.profileUserId) return;
            const st = this.readMasterState();
            if (!st || this.normalizeGuid(st.masterUserId) !== this.normalizeGuid(masterProfile.profileUserId)) {
                this.writeMasterState(masterProfile.profileUserId);
            }
        },

        /// Whether the session in use is the household's master account itself.
        isSignedInAsMaster: function (state) {
            const st = state || this.readMasterState();
            if (!st || typeof ApiClient === 'undefined' || !ApiClient) return false;
            const current = typeof ApiClient.getCurrentUserId === 'function' ? ApiClient.getCurrentUserId() : '';
            return !!current && this.normalizeGuid(current) === this.normalizeGuid(st.masterUserId);
        },

        writeMasterState: function (masterUserId) {
            try {
                localStorage.setItem(this.config.masterStorageKey, JSON.stringify({ masterUserId: masterUserId }));
            } catch (e) { /* full or blocked: the next list fetch writes it again */ }
        },

        /// Signs one session out on the server. Best-effort and silent: the token has
        /// already been dropped from this browser, which is what matters here. keepalive,
        /// because the switch that calls this reloads the page straight afterwards, and a
        /// plain request in flight is cancelled by the navigation.
        _endSession: function (token) {
            try {
                if (!token || typeof ApiClient === 'undefined' || !ApiClient || typeof ApiClient.getUrl !== 'function') return;
                fetch(ApiClient.getUrl('Sessions/Logout'), { method: 'POST', headers: this.getAuthHeaders(token), keepalive: true })
                    .catch(() => { /* already gone, or offline */ });
            } catch (e) { /* never worth failing over */ }
        },

        updateStoredCredentials: function (newToken, newUserId) {
            try {
                const credsStr = localStorage.getItem('jellyfin_credentials');
                if (!credsStr) return false;

                const creds = JSON.parse(credsStr);
                if (!creds || !Array.isArray(creds.Servers) || !creds.Servers.length) return false;

                const serverId = this.normalizeGuid(
                    typeof ApiClient.serverId === 'function' ? ApiClient.serverId() : (ApiClient.serverId || ''));
                const address = (typeof ApiClient.serverAddress === 'function' ? ApiClient.serverAddress() : '') || '';
                const sameAddress = (server) => !!address && [server.ManualAddress, server.LocalAddress, server.RemoteAddress]
                    .some(a => a && String(a).replace(/\/+$/, '') === address.replace(/\/+$/, ''));

                // In order, and only one of them applies. Widening the match after a
                // narrower one has already hit would write another server's entry.
                //
                // The second rung is the one that makes a wrong refusal unlikely: the
                // entry to overwrite is the one holding the session being replaced, so the
                // user signed in right now names it even when the id and the address do
                // not. The old code wrote *every* stored server when it had no id, which
                // did make the switch work — by pointing other servers at a token they
                // will not accept.
                const signedIn = this.normalizeGuid(
                    typeof ApiClient.getCurrentUserId === 'function' ? ApiClient.getCurrentUserId() : '');
                let targets = creds.Servers.filter(s => serverId && this.normalizeGuid(s.Id) === serverId);
                if (!targets.length) targets = creds.Servers.filter(s => signedIn && this.normalizeGuid(s.UserId) === signedIn);
                if (!targets.length) targets = creds.Servers.filter(sameAddress);
                if (!targets.length && creds.Servers.length === 1) targets = creds.Servers.slice();
                if (!targets.length) {
                    console.error('ProfilesPlugin: none of the stored servers is the one being switched on.');
                    return false;
                }

                targets.forEach(server => {
                    server.AccessToken = newToken;
                    server.UserId = newUserId;
                });
                localStorage.setItem('jellyfin_credentials', JSON.stringify(creds));

                // Read back. A write that was accepted and dropped leaves the old token in
                // place, and the reload would sign back in as whoever was there before.
                const saved = JSON.parse(localStorage.getItem('jellyfin_credentials') || 'null');
                return !!(saved && Array.isArray(saved.Servers)
                    && saved.Servers.some(s => s.AccessToken === newToken && s.UserId === newUserId));
            } catch (e) {
                console.error("ProfilesPlugin: Stored credentials update failed:", e);
                return false;
            }
        },

        normalizeGuid: function (guid) {
            if (!guid) return '';
            return guid.toString().toLowerCase().replace(/[^a-z0-9]/g, '');
        },

        /// Registers a document-level listener that is torn down the next time a modal is
        /// rendered.
        ///
        /// The create/edit forms attach a document click handler to close the device dropdown.
        /// Those were added on every render and never removed, so each open/close cycle left
        /// another live closure bound to `document`, each capturing a now-detached modal. After
        /// a few passes every click on the page ran a growing pile of stale handlers — which
        /// showed up as clicks taking seconds to register, getting worse the longer the tab
        /// stayed open.
        addManagedDocumentListener: function (type, handler, options) {
            if (!this._managedDocListeners) this._managedDocListeners = [];
            document.addEventListener(type, handler, options);
            this._managedDocListeners.push({ type, handler, options });
        },

        /// Drops every listener registered via addManagedDocumentListener. Safe to call when
        /// none are outstanding.
        clearManagedDocumentListeners: function () {
            if (!this._managedDocListeners) return;
            this._managedDocListeners.forEach(({ type, handler, options }) => {
                document.removeEventListener(type, handler, options);
            });
            this._managedDocListeners = [];
        },

        /// Normalizes the /list response into the camelCase shape the UI uses.
        /// ASP.NET may serialize either casing depending on server configuration, so every
        /// field is read both ways. Single definition — adding a field here reaches every
        /// caller, which two hand-maintained copies of this map did not.
        normalizeProfiles: function (profiles) {
            const pick = (p, name, fallback) => {
                const upper = name.charAt(0).toUpperCase() + name.slice(1);
                if (p[name] !== undefined && p[name] !== null) return p[name];
                if (p[upper] !== undefined && p[upper] !== null) return p[upper];
                return fallback;
            };

            return (profiles || []).map(p => ({
                profileUserId: pick(p, 'profileUserId', null),
                profileName: pick(p, 'profileName', ''),
                avatarInitial: pick(p, 'avatarInitial', '?'),
                avatarColor: pick(p, 'avatarColor', DEFAULT_AVATAR_COLOR),
                // Cut-out picture: paint no colour behind it (issue #23).
                transparentAvatar: pick(p, 'transparentAvatar', false),
                // The profile's own choice; null follows the server default. Sent to the
                // account owner only.
                theme: pick(p, 'theme', null),
                requiresPin: pick(p, 'requiresPin', false),
                // "A PIN exists" — distinct from requiresPin, which is false on the LAN when
                // the bypass is enabled. Forms must use this one.
                hasPin: pick(p, 'hasPin', false),
                isMaster: pick(p, 'isMaster', false),
                // Sent for masters only, and used for one thing: warning that this PIN
                // opens an account which can reach server settings. A sub-profile is never
                // created with administrator rights, so it is always false for one.
                isAdministrator: pick(p, 'isAdministrator', false),
                lockoutMinutes: pick(p, 'lockoutMinutes', 5),
                maxSubProfiles: pick(p, 'maxSubProfiles', 5),
                bypassPinOnLocalNetwork: pick(p, 'bypassPinOnLocalNetwork', false),
                // Main account only, and only to its owner. True unless the owner turned it off.
                askPinToOpen: pick(p, 'askPinToOpen', true) !== false,
                allowedDeviceIds: pick(p, 'allowedDeviceIds', []),
                enabledFolders: pick(p, 'enabledFolders', []),
                allLibraries: pick(p, 'allLibraries', false),
                blockedTags: pick(p, 'blockedTags', []),
                allowedTags: pick(p, 'allowedTags', []),
                isBonfire: pick(p, 'isBonfire', false),
                // Another account added to this household's screen (issue #39). Also
                // isBonfire, so nothing that looks for "our own" counts it.
                isLinked: pick(p, 'isLinked', false) === true,
                profileImage: pick(p, 'profileImage', null),
                masterUserId: pick(p, 'masterUserId', null),
                // Viewing hours and limits (1.7.2): null for a profile without them.
                viewing: this.normalizeViewing(pick(p, 'viewing', null))
            }));
        },

        /// A /limits or /list viewing state in camelCase, or null when not limited.
        normalizeViewing: function (v) {
            if (!v) return null;
            const f = (k) => this._field(v, k);
            if (f('limited') !== true) return null;
            return {
                allowed: f('allowed') !== false,
                reason: f('reason') || '',
                binding: f('binding') || '',
                minutesLeft: typeof f('minutesLeft') === 'number' ? f('minutesLeft') : null,
                untilUtc: f('untilUtc') || null,
                nextStartUtc: f('nextStartUtc') || null,
                usedMinutes: f('usedMinutes') || 0,
                limitMinutes: typeof f('limitMinutes') === 'number' ? f('limitMinutes') : null,
                askedUtc: f('askedUtc') || null,
                timeZone: f('timeZone') || ''
            };
        },

        /// A time from the server, as the household's clock shows it: "20:00", or
        /// "Sat 08:00" when it isn't today. Falls back to the device's own zone if the
        /// browser doesn't know the household's.
        viewingTime: function (iso, zone) {
            const d = new Date(iso);
            if (isNaN(d.getTime())) return '';
            const locale = activeLocale || [];
            const opts = (extra) => Object.assign(zone ? { timeZone: zone } : {}, extra);
            try {
                const day = (x) => x.toLocaleDateString(locale, opts({ year: 'numeric', month: 'numeric', day: 'numeric' }));
                const time = d.toLocaleTimeString(locale, opts({ hour: '2-digit', minute: '2-digit' }));
                if (day(d) === day(new Date())) return time;
                return d.toLocaleDateString(locale, opts({ weekday: 'short' })) + ' ' + time;
            } catch (e) {
                return zone ? this.viewingTime(iso, null) : d.toLocaleTimeString();
            }
        },

        /// The line under a limited profile's name on the gate: "Until 20:00", "45 min
        /// left", "From 15:00", "Time's up". Empty for a profile with no limits.
        viewingLine: function (v) {
            if (!v) return '';
            if (v.askedUtc && !v.allowed) return t('viewing.asked');
            if (v.allowed) {
                if (v.binding === 'limit' && v.minutesLeft !== null) return t('viewing.minutesLeft', { minutes: v.minutesLeft });
                if (v.untilUtc) return t('viewing.until', { time: this.viewingTime(v.untilUtc, v.timeZone) });
                return '';
            }
            if (v.nextStartUtc && v.reason === 'hours') return t('viewing.from', { time: this.viewingTime(v.nextStartUtc, v.timeZone) });
            return t('viewing.timeUp');
        },

        /// One call to a /limits route as the household. Resolves the new state, or
        /// rejects with the server's words.
        _limitsCall: function (profileId, path, body) {
            const masterState = this.readMasterState();
            if (!masterState) return Promise.reject(new Error(t('errors.couldNotSaveThat')));
            return fetch(ApiClient.getUrl('plugins/profiles/limits/' + profileId + (path ? '/' + path : '')), {
                method: body ? 'POST' : 'GET',
                headers: body ? this.jsonHeaders(masterState.token) : this.getAuthHeaders(masterState.token),
                body: body ? JSON.stringify(body) : undefined
            }).then(res => {
                if (res.status === 401) { this.handleSessionExpired(); throw new Error(t('errors.couldNotSaveThat')); }
                if (res.status === 429 && path === 'ask') throw new Error(t('viewing.alreadyAsked'));
                if (!res.ok) return res.text().then(text => { throw new Error(text || t('errors.couldNotSaveThat')); });
                return res.json();
            });
        },

        /// A limited profile chosen on the gate while it is out of time. It can ask for
        /// more, a parent can give some with the main PIN, or it can go in and browse:
        /// only playing is refused.
        showTimeUp: function (profile, open) {
            const v = profile.viewing;
            const name = profile.profileName;
            const message = v.reason === 'hours' && v.nextStartUtc
                ? t('viewing.outsideBody', { name: name, time: this.viewingTime(v.nextStartUtc, v.timeZone) })
                : v.reason === 'limit' ? t('viewing.limitBody', { name: name }) : t('viewing.notNowBody', { name: name });
            const refresh = () => {
                const masterState = this.readMasterState();
                if (masterState) this.fetchAndRenderProfiles(ApiClient, masterState.masterUserId, masterState.token, true);
            };
            this.showActionsDialog(t('viewing.timeUpTitle'), message, [
                { label: t('viewing.ask'), primary: true, run: () => {
                    this._limitsCall(profile.profileUserId, 'ask', {})
                        .then(() => this.showAlert(t('viewing.askedTitle'), t('viewing.askedBody'), refresh))
                        .catch(err => this.showAlert(t('viewing.timeUpTitle'), escapeHtml(err.message)));
                } },
                { label: t('viewing.giveTime'), run: () => {
                    this.withMainPin(this.currentProfiles, () => this.chooseMoreTime(profile, refresh));
                } },
                { label: t('viewing.browse'), run: open },
                { label: t('common.cancel') }
            ]);
        },

        /// How much more time, then gives it with the main PIN just entered.
        chooseMoreTime: function (profile, done) {
            const options = [15, 30, 60, 120].map(m => ({ id: String(m), name: this.durationText(m) }));
            this.showSelectDialog(t('viewing.giveTitle', { name: profile.profileName }), t('viewing.giveBody'), options, (minutes) => {
                this._limitsCall(profile.profileUserId, 'grant', { minutes: parseInt(minutes, 10), masterPin: this.masterPin })
                    .then(() => { if (!this.isManageMode) this.masterPin = null; done(); })
                    .catch(err => this.showAlert(t('errors.error'), escapeHtml(err.message), done));
            });
        },

        /// "45 min", "1 h", "1 h 30 min".
        durationText: function (minutes) {
            if (minutes < 60) return t('viewing.min', { minutes: minutes });
            const h = Math.floor(minutes / 60);
            const m = minutes % 60;
            return m ? t('viewing.hMin', { hours: h, minutes: m }) : t('viewing.h', { hours: h });
        },

        initTVCheckboxes: function (container) {
            container.querySelectorAll('.library-check-label').forEach(label => {
                if (!label.hasAttribute('tabindex')) {
                    label.setAttribute('tabindex', '0');
                }
                if (label._tvInit) return;
                label._tvInit = true;

                label.addEventListener('keydown', (e) => {
                    if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault();
                        const checkbox = label.querySelector('input[type="checkbox"]');
                        if (checkbox) {
                            checkbox.checked = !checkbox.checked;
                            checkbox.dispatchEvent(new Event('change'));
                        }
                    }
                });
            });
        },

        /// Wraps a group of form fields in a titled, self-contained section.
        /// The create/edit forms grew field-by-field in the order features were added, which
        /// left unrelated controls adjacent and gave no landmarks to scroll by — worst on
        /// phones, where the form is several screens long. Sections give each group a heading
        /// and one-line purpose, and are plain blocks (no collapsing) so D-pad focus order on
        /// TV stays linear and predictable.
        renderSection: function (icon, title, subtitle, bodyHtml) {
            return `
                <section class="profile-section">
                    <div class="profile-section-header">
                        <span class="material-icons profile-section-icon" aria-hidden="true">${icon}</span>
                        <div class="profile-section-heading">
                            <h2 class="profile-section-title">${escapeHtml(title)}</h2>
                            <span class="profile-section-subtitle">${escapeHtml(subtitle)}</span>
                        </div>
                    </div>
                    <div class="profile-section-body">${bodyHtml}</div>
                </section>
            `;
        },

        /// "More settings" under a profile form's Basics: closed until asked for.
        ///
        /// A real <details>, so it opens with Enter on a remote and needs no script —
        /// Chromium 12, well inside both TV baselines. Its summary carries tabindex so the
        /// overlay's own focus walk finds it the way it finds every other control. Two
        /// columns of sections, like the Basics above it; empty strings are dropped, so a
        /// server that does not offer themes leaves no gap.
        renderMoreSettings: function (prefix, left, right, summary) {
            const col = (parts) => parts.filter(Boolean).join('');
            return this.renderFold(prefix + '-more', summary || t('profileForm.moreSummary'), `
                        <div class="form-col">${col(left)}</div>
                        <div class="form-col">${col(right)}</div>`);
        },

        /// The one fold every household screen uses, labelled "More": a closed <details>
        /// with a line saying what is inside. Account and both profile forms draw it, so
        /// rare options look the same wherever they are (P2-601's pattern, for these
        /// screens). Anything that reports an error about a field inside it must call
        /// revealFold, so the message is never behind a closed fold.
        renderFold: function (id, summary, bodyHtml, single) {
            return `
                <details class="jpf-more${single ? ' jpf-more-single' : ''}" id="${id}">
                    <summary class="jpf-more-summary" tabindex="0">
                        <span class="material-icons jpf-more-icon" aria-hidden="true">tune</span>
                        <span class="jpf-more-label">
                            <span class="jpf-more-title">${t('profileForm.moreTitle')}</span>
                            <span class="jpf-more-hint">${escapeHtml(summary)}</span>
                        </span>
                        <span class="material-icons jpf-more-caret" aria-hidden="true">expand_more</span>
                    </summary>
                    <div class="jpf-more-body">${bodyHtml}</div>
                </details>
            `;
        },

        /// Opens the More fold that holds `el`, if it is in one.
        revealFold: function (el) {
            for (let n = el; n && n.nodeType === 1; n = n.parentNode) {
                if (n.tagName === 'DETAILS') n.open = true;
            }
        },

        /// Viewing hours and daily limits on a profile's edit form (1.7.2), with "more time
        /// now". Drawn into `slot` once /limits answers; a server without limits leaves it
        /// empty. The rules are saved with the form's Save, after the profile itself, and
        /// only if they changed. More time is given at once: it is an answer, not a setting.
        ///
        /// The form shows one stretch per day. The server takes any number; a profile given
        /// more through the API shows the first of each day here, and saving replaces them.
        ///
        /// `creating` is the Add form: there is no profile yet, so nothing is fetched and no
        /// "more time now" is offered, and `save(id)` takes the id /create answered with. A
        /// child is set up in one pass, not made first and edited after (P2-103).
        initViewingEditor: function (slot, profile, creating) {
            const editor = { save: () => Promise.resolve(), pending: () => false };
            if (!slot || (!creating && (!profile || profile.isMaster))) return editor;
            const ORDER = [1, 2, 3, 4, 5, 6, 0];
            const LIMITS = [15, 30, 45, 60, 90, 120, 150, 180, 240, 300, 360];
            const locale = activeLocale || [];
            const pad = (n) => (n < 10 ? '0' : '') + n;
            const timeLabel = (hhmm) => {
                if (hhmm === '24:00') return t('viewing.midnight');
                const d = new Date(2000, 0, 1, parseInt(hhmm.slice(0, 2), 10), parseInt(hhmm.slice(3), 10));
                try { return d.toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' }); } catch (e) { return hhmm; }
            };
            const times = [];
            for (let m = 0; m < 1440; m += 15) times.push(pad(Math.floor(m / 60)) + ':' + pad(m % 60));
            const timeOptions = (chosen, end) => times.concat(end ? ['24:00'] : [])
                .concat(times.indexOf(chosen) === -1 && chosen !== '24:00' ? [chosen] : [])
                .map(v => `<option value="${v}"${v === chosen ? ' selected' : ''}>${escapeHtml(timeLabel(v))}</option>`).join('');
            const dayName = (d) => {
                // 2026-11-01 is a Sunday.
                try { return new Date(2026, 10, 1 + d).toLocaleDateString(locale, { weekday: 'short' }); } catch (e) { return String(d); }
            };
            const limitOptions = (chosen, weekend) => {
                const values = (weekend ? ['', '0'] : ['0']).concat(LIMITS.map(String));
                if (values.indexOf(String(chosen)) === -1) values.push(String(chosen));
                return values.map(v => `<option value="${v}"${v === String(chosen) ? ' selected' : ''}>${
                    v === '' ? t('viewing.sameAsWeekdays') : v === '0' ? t('viewing.noLimit') : escapeHtml(this.durationText(parseInt(v, 10)))
                }</option>`).join('');
            };

            let loaded = null;
            let baseline = null;
            const read = () => {
                const q = (s) => slot.querySelector(s);
                const groups = {};
                ORDER.forEach(d => {
                    if (!q('#viewing-day-' + d).checked) return;
                    const key = q('#viewing-from-' + d).value + '-' + q('#viewing-to-' + d).value;
                    groups[key] = (groups[key] || 0) | (1 << d);
                });
                const weekend = q('#viewing-weekend').value;
                return {
                    hoursEnabled: q('#viewing-hours').checked,
                    windows: Object.keys(groups).map(k => ({ days: groups[k], start: k.slice(0, 5), end: k.slice(6) })),
                    dailyMinutes: parseInt(q('#viewing-daily').value, 10) || 0,
                    weekendMinutes: weekend === '' ? null : parseInt(weekend, 10)
                };
            };

            const draw = (state) => {
                if (!document.body.contains(slot)) return;
                const f = (o, k) => this._field(o, k);
                const rules = f(state, 'rules') || {};
                const windows = (f(rules, 'windows') || []).map(w => ({ days: f(w, 'days') || 0, start: f(w, 'start'), end: f(w, 'end') }));
                const v = this.normalizeViewing(state);
                const weekend = f(rules, 'weekendMinutes');
                const rows = ORDER.map(d => {
                    const w = windows.filter(x => (x.days & (1 << d)) !== 0)[0];
                    const start = w ? w.start : '15:00';
                    const end = w ? w.end : '20:00';
                    return `
                        <div class="jpf-viewing-day">
                            <label class="library-check-label jpf-viewing-dayname">
                                <input type="checkbox" id="viewing-day-${d}" ${w ? 'checked' : ''} />
                                <span>${escapeHtml(dayName(d))}</span>
                            </label>
                            <select id="viewing-from-${d}" aria-label="${t('viewing.fromAria', { day: escapeHtml(dayName(d)) })}">${timeOptions(start, false)}</select>
                            <span class="jpf-viewing-dash" aria-hidden="true">–</span>
                            <select id="viewing-to-${d}" aria-label="${t('viewing.toAria', { day: escapeHtml(dayName(d)) })}">${timeOptions(end, true)}</select>
                        </div>`;
                }).join('');
                const now = !v ? '' : v.allowed
                    ? (v.binding === 'limit' || v.untilUtc ? this.viewingLine(v) : t('viewing.canWatch'))
                    : this.viewingLine(Object.assign({}, v, { askedUtc: null }));
                const used = v && v.limitMinutes !== null ? t('viewing.usedToday', { used: v.usedMinutes, limit: v.limitMinutes }) : '';
                slot.innerHTML = this.renderSection('schedule', t('viewing.title'), t('viewing.subtitle'), `
                    ${v ? `<div class="form-hint jpf-viewing-now">${escapeHtml([now, used].filter(Boolean).join(' · '))}</div>` : ''}
                    ${v && v.askedUtc ? `<div class="form-hint jpf-viewing-asked">${t('viewing.askedHint', { name: escapeHtml(profile.profileName) })}</div>` : ''}
                    ${v && !creating ? `
                    <div class="form-group">
                        <label>${t('viewing.moreTime')}</label>
                        <div class="jpf-inline-row">
                            ${[15, 30, 60].map(m => `<button type="button" class="profiles-btn btn-secondary viewing-grant" data-minutes="${m}">${escapeHtml(this.durationText(m))}</button>`).join('')}
                            ${v.askedUtc ? `<button type="button" class="profiles-btn btn-secondary viewing-grant" data-minutes="0">${t('viewing.decline')}</button>` : ''}
                        </div>
                        <div class="form-hint" id="viewing-grant-status" role="status" aria-live="polite"></div>
                    </div>` : ''}
                    <div class="form-group">
                        <label for="viewing-daily">${t('viewing.daily')}</label>
                        <select id="viewing-daily">${limitOptions(f(rules, 'dailyMinutes') || 0, false)}</select>
                    </div>
                    <div class="form-group" id="viewing-weekend-group">
                        <label for="viewing-weekend">${t('viewing.weekend')}</label>
                        <select id="viewing-weekend">${limitOptions(weekend === null || weekend === undefined ? '' : weekend, true)}</select>
                        <div class="form-hint">${t('viewing.hint')}</div>
                    </div>
                    <div class="form-group">
                        <label class="library-check-label" style="display: inline-flex; align-items: center; gap: 0.5rem; cursor: pointer;">
                            <input type="checkbox" id="viewing-hours" ${f(rules, 'hoursEnabled') ? 'checked' : ''} style="accent-color: var(--jpf-accent);" />
                            <span>${t('viewing.hoursEnabled')}</span>
                        </label>
                        <div class="jpf-viewing-days" id="viewing-days">${rows}</div>
                    </div>
                `);

                // The seven rows are only there once "hours" is ticked, and the weekend
                // limit only once there is a daily one for it to differ from (P2-602).
                const days = slot.querySelector('#viewing-days');
                const hours = slot.querySelector('#viewing-hours');
                const daily = slot.querySelector('#viewing-daily');
                const weekendSel = slot.querySelector('#viewing-weekend');
                const weekendGroup = slot.querySelector('#viewing-weekend-group');
                const syncDays = () => { days.style.display = hours.checked ? '' : 'none'; };
                const syncWeekend = () => {
                    const none = daily.value === '0' && weekendSel.value === '';
                    weekendGroup.style.display = none ? 'none' : '';
                };
                hours.addEventListener('change', syncDays);
                daily.addEventListener('change', () => {
                    if (daily.value === '0') weekendSel.value = '';
                    syncWeekend();
                });
                syncDays();
                syncWeekend();
                this.initTVCheckboxes(slot);

                const status = slot.querySelector('#viewing-grant-status');
                Array.prototype.forEach.call(slot.querySelectorAll('.viewing-grant'), btn => {
                    btn.addEventListener('click', () => {
                        const minutes = parseInt(btn.getAttribute('data-minutes'), 10);
                        const pending = read();
                        this._limitsCall(profile.profileUserId, 'grant', { minutes: minutes, masterPin: this.masterPin })
                            .then(reply => {
                                draw(Object.assign({}, reply, { rules: f(state, 'rules') }));
                                // Keep what was being edited: more time is not a save.
                                this._restoreViewingForm(slot, pending, ORDER);
                                const after = slot.querySelector('#viewing-grant-status');
                                if (after) after.textContent = minutes ? t('viewing.given', { duration: this.durationText(minutes) }) : t('viewing.declined');
                            })
                            .catch(err => { if (status) { status.textContent = err.message; status.style.color = 'var(--jpf-danger)'; } });
                    });
                });
            };

            if (creating) {
                loaded = {};
                draw({});
                baseline = JSON.stringify(read());
            } else {
                this._limitsCall(profile.profileUserId, '', null)
                    .then(state => { loaded = state; draw(state); baseline = JSON.stringify(read()); })
                    .catch(() => { slot.innerHTML = ''; });
            }

            // Whether saving would switch limits on: the server asks for a main PIN then.
            editor.pending = () => {
                if (!loaded || !document.body.contains(slot) || !slot.querySelector('#viewing-hours')) return false;
                const now = read();
                return JSON.stringify(now) !== baseline
                    && (now.hoursEnabled || now.dailyMinutes > 0 || (now.weekendMinutes || 0) > 0);
            };

            editor.save = (newId) => {
                if (!loaded || !document.body.contains(slot) || !slot.querySelector('#viewing-hours')) return Promise.resolve();
                const now = read();
                if (JSON.stringify(now) === baseline) return Promise.resolve();
                const id = creating ? newId : profile.profileUserId;
                if (!id) return Promise.resolve();
                return this._limitsCall(id, '', Object.assign({ masterPin: this.masterPin, language: activeLocale || '' }, now));
            };
            return editor;
        },

        /// Puts the form back as it was being edited after a redraw from the server.
        _restoreViewingForm: function (slot, state, order) {
            const q = (s) => slot.querySelector(s);
            if (!q('#viewing-hours')) return;
            q('#viewing-hours').checked = state.hoursEnabled;
            order.forEach(d => {
                const w = state.windows.filter(x => (x.days & (1 << d)) !== 0)[0];
                q('#viewing-day-' + d).checked = !!w;
                if (w) { q('#viewing-from-' + d).value = w.start; q('#viewing-to-' + d).value = w.end; }
            });
            q('#viewing-daily').value = String(state.dailyMinutes);
            q('#viewing-weekend').value = state.weekendMinutes === null ? '' : String(state.weekendMinutes);
            q('#viewing-days').style.display = state.hoursEnabled ? '' : 'none';
            q('#viewing-weekend-group').style.display = (state.dailyMinutes === 0 && state.weekendMinutes === null) ? 'none' : '';
        },

        /// The theme picker as a section of its own, or '' when profiles may not choose.
        renderThemeSection: function (prefix, stored) {
            const picker = this.renderThemePicker(prefix, stored);
            return picker
                ? this.renderSection('palette', t('settings.themeTitle'), t('profileForm.themeSubtitle'), picker)
                : '';
        },

        /// The rating the Kids preset fills in: the highest offered at or under 10 (PG,
        /// TV-PG, or the country's match), else the lowest offered. Null with none.
        kidsRatingFrom: function (values) {
            const scores = (values || []).map(Number).filter(n => isFinite(n) && n > 0);
            if (!scores.length) return null;
            const under = scores.filter(n => n <= 10);
            return String(under.length ? Math.max.apply(null, under) : Math.min.apply(null, scores));
        },

        /// Ticking Kids fills in the rating; unticking puts back what was there, unless
        /// the rating was changed by hand since. Changing the rating by hand unticks it,
        /// so the box never claims a preset that is no longer what will be saved.
        bindKidsPreset: function (box, select) {
            if (!box || !select) return;
            const values = Array.prototype.map.call(select.options || [], o => o.value);
            const kids = this.kidsRatingFrom(values);
            if (kids === null) { box.disabled = true; return; }
            let before = select.value;
            box.addEventListener('change', () => {
                if (box.checked) {
                    before = select.value;
                    select.value = kids;
                } else if (select.value === kids) {
                    select.value = before === kids ? '' : before;
                }
            });
            select.addEventListener('change', () => {
                if (select.value !== kids) box.checked = false;
            });
        },

        /// The avatar swatches are identical in both forms; keeping one copy means a palette
        /// change lands in both places at once.
        /// Dims the avatar colour when it has stopped doing anything.
        ///
        /// It is twenty-one swatches over three rows, the largest thing on the form, so
        /// leaving it at full strength when it cannot affect the avatar reads as a
        /// control that is simply being ignored. Dimmed rather than hidden: it starts
        /// mattering again the moment the picture is removed, and a control that
        /// vanishes and reappears is worse than one that fades.
        ///
        /// It used to dim whenever a picture was set, and say "Not used while a picture
        /// is set". That was wrong, and it is how issue #23 came to be filed: the colour
        /// is painted *behind* the picture and shows through wherever the picture is
        /// transparent, so somebody read the hint, chose a cut-out picture, and got a
        /// coloured square anyway. The colour is genuinely unused in one case only —
        /// a picture with the background switched off.
        setColorGroupInert: function (prefix, hasPicture, isTransparent) {
            const group = document.getElementById(prefix + '-color-group');
            if (!group) return;

            const unused = !!hasPicture && !!isTransparent;
            group.classList.toggle('is-inert', unused);

            const hint = group.querySelector('[data-role="color-hint"]');
            if (hint) {
                if (unused) {
                    hint.textContent = t('profileForm.colorHintUnused');
                } else if (hasPicture) {
                    hint.textContent = t('profileForm.colorHintHasPicture');
                } else {
                    hint.textContent = t('profileForm.colorHintNoPicture');
                }
            }
        },

        /// The first palette colour nobody in this household has, as the server picks it
        /// (NextAvatarColor), so a new profile doesn't start as a copy of the last tile.
        nextAvatarColor: function () {
            const own = (this.currentProfiles || []).filter(p => !p.isBonfire);
            const used = own.map(p => String(p.avatarColor || '').toLowerCase());
            const free = AVATAR_PALETTE.filter(c => used.indexOf(c.toLowerCase()) === -1);
            return free.length ? free[0] : AVATAR_PALETTE[own.length % AVATAR_PALETTE.length];
        },

        renderColorPicker: function (selectedColor) {
            const palette = AVATAR_PALETTE;
            const active = (selectedColor || '').toLowerCase();
            return `
                <div class="avatar-color-picker">
                    ${palette.map(c => `
                        <div class="color-dot${c.toLowerCase() === active ? ' active' : ''}"
                             style="background-color: ${c}" data-color="${c}"
                             role="radio" aria-label="${c}" tabindex="0"></div>
                    `).join('')}
                </div>
            `;
        },

        // ── Tag filtering ───────────────────────────────────────────────────────────
        // Jellyfin matches blocked/allowed tags against an item's inherited tags, so a tag
        // on a series or a whole library applies to everything inside it.

        /// Fetches the distinct tags present in the master's libraries, for the suggestion
        /// list. Degrades to an empty list — the inputs stay usable as free text.
        // ── Navigation ──────────────────────────────────────────────────────

        /// Ticket for the screen currently being asked for.
        ///
        /// Opening a form fires network requests and only draws when they return.
        /// Nothing used to stop a second navigation in the meantime, and nothing
        /// stopped the first response drawing itself over whatever had replaced it —
        /// so clicking Manage Profiles and then Switcher Style showed Switcher Style,
        /// then silently replaced it with Manage Profiles seconds later.
        ///
        /// Every screen change takes a ticket. An async render compares its ticket
        /// before touching the DOM and drops the response if the user has moved on.
        _navTicket: 0,

        /// Claims the screen. Call from anything that replaces the modal contents,
        /// including synchronous renders — those are exactly what a slow form loses
        /// the race to.
        beginNavigation: function () {
            return ++this._navTicket;
        },

        /// True while the ticket still owns the screen.
        navIsCurrent: function (ticket) {
            return ticket === this._navTicket;
        },

        // ── Shared form data ────────────────────────────────────────────────

        /// Cached answer to the four requests that are the same for every profile.
        ///
        /// Add and Edit Profile each fired five requests and waited for all of them.
        /// Only one — Users/{id} — is about the profile you clicked; the available
        /// libraries, the connected devices, the library tags and the avatar library
        /// belong to the account and were refetched in full every single time.
        _sharedForm: null,

        /// Fetches them once per gate session. Prefetched when the overlay opens, so
        /// by the time anyone picks a profile the shared half is already in hand and
        /// opening a form costs one request instead of five.
        fetchSharedFormData: function (apiClient, masterState) {
            if (this._sharedForm) return this._sharedForm;

            const headers = this.getAuthHeaders(masterState.token);
            this._sharedForm = Promise.all([
                fetch(apiClient.getUrl('plugins/profiles/libraries'), { headers })
                    .then(res => res.json()),
                // null, not [], when the list could not be read. The edit form rebuilds a
                // profile's allowed devices from the boxes it draws, so an empty list here
                // used to save as "any device" and switch the restriction off.
                fetch(apiClient.getUrl('plugins/profiles/devices'), { headers })
                    .then(res => res.ok ? res.json() : null)
                    .then(list => Array.isArray(list) ? list : null)
                    .catch(() => null),
                this.fetchLibraryTags(apiClient, masterState.token, masterState.masterUserId),
                this.fetchAvatarLibrary(apiClient, masterState.token),
                // The server country's own rating system. Never fails the form: null falls
                // back to the US list in renderRatingSelect.
                fetch(apiClient.getUrl('plugins/profiles/ratings'), { headers })
                    .then(res => res.ok ? res.json() : null)
                    .catch(() => null)
            ]).then(([libraries, devices, libraryTags, avatarLibrary, ratings]) => ({
                libraries, devices, libraryTags, avatarLibrary, ratings
            })).catch(err => {
                // Never cache a failure. A server that was briefly unreachable would
                // otherwise keep every form broken until the overlay was closed.
                this._sharedForm = null;
                throw err;
            });

            return this._sharedForm;
        },

        /// Dropped when the overlay closes, so reopening the gate always re-reads the
        /// server. Within one session the data does not change underneath us.
        clearSharedFormData: function () {
            this._sharedForm = null;
        },

        fetchLibraryTags: function (apiClient, token, userId) {
            let url;
            try {
                url = apiClient.getUrl('Items/Filters2', { userId: userId, recursive: true });
            } catch (e) {
                return Promise.resolve([]);
            }
            return fetch(url, { headers: this.getAuthHeaders(token) })
                .then(res => res.ok ? res.json() : null)
                .then(data => {
                    const tags = (data && (data.Tags || data.tags)) || [];
                    return Array.from(new Set(tags.filter(tag => tag)))
                        .sort((a, b) => a.localeCompare(b));
                })
                .catch(() => []);
        },

        renderTagSuggestions: function (id, tags) {
            return `<datalist id="${id}">${(tags || []).map(tag => `<option value="${escapeHtml(tag)}"></option>`).join('')}</datalist>`;
        },

        renderTagChip: function (tag) {
            const safe = escapeHtml(tag);
            return `<span class="tag-chip" data-tag="${safe}"><span>${safe}</span><button type="button" class="tag-chip-remove" tabindex="0" aria-label="${t('profileForm.removeTagAria', { tag: safe })}">×</button></span>`;
        },

        renderTagEditor: function (id, tags, placeholder, suggestionsId) {
            const chips = (tags || []).map(tag => this.renderTagChip(tag)).join('');
            return `
                <div class="tag-editor" id="${id}">
                    <div class="tag-chip-list" data-empty-label="${escapeHtml(t('profileForm.noTags'))}" ${(tags || []).length ? '' : 'data-empty="true"'}>${chips}</div>
                    <div class="tag-input-row">
                        <input type="text" class="tag-input" placeholder="${escapeHtml(placeholder)}" list="${suggestionsId}" autocomplete="off" />
                        <button type="button" class="profiles-btn btn-secondary tag-add-btn">${t('common.add')}</button>
                    </div>
                </div>
            `;
        },

        /// The allowed-devices dropdown, shared by the Add and Edit profile forms.
        ///
        /// These two forms were built by two functions that had been copied from each
        /// other and then edited apart: 213 identical lines, ten runs of six or more.
        /// The copies had already drifted — a fix to one was a fix to one — and the
        /// duplicated *comments* were the clearest sign of it, the same sentence about
        /// sending null rather than an empty array sitting verbatim in both.
        ///
        /// Three things actually differ between the modes, and they are all here:
        ///   - create prefixes its element ids, because both forms can be in the
        ///     document at once and duplicate ids would make getElementById a coin toss;
        ///   - the checkbox class follows the same prefix, since the handlers query it;
        ///   - only edit has a profile to read existing selections from.
        ///
        /// Neither offers rename, merge or forget any more (1.7.2). Those change the device
        /// for the whole household, so they live in Settings → Your account; here a profile
        /// only ticks the devices it may be opened on.
        renderDeviceDropdown: function (mode, devices, profile) {
            const isEdit = mode === 'edit';
            const p = isEdit ? '' : 'create-';
            const boxClass = isEdit ? 'device-checkbox' : 'create-device-checkbox';
            // The server matches device ids ignoring case, and so must this. A whitelisted id
            // in a different case from the device's record drew an unticked box, and the next
            // save dropped it from the list.
            const allowedIds = (isEdit && profile && profile.allowedDeviceIds || [])
                .map(id => String(id).toLowerCase());
            const unavailable = devices === null;

            const items = (devices && devices.length > 0)
                ? devices.map(dev => {
                    const deviceId = dev.deviceId || dev.DeviceId || '';
                    const deviceName = dev.deviceName || dev.DeviceName || t('profileForm.unknownDevice');
                    const client = dev.client || dev.Client || t('profileForm.unknownClient');
                    const lastSeen = dev.lastSeen || dev.LastSeen;
                    const lastSeenDate = lastSeen ? new Date(lastSeen) : null;
                    // Year 1 is what an unset DateTime serialises to; showing "01/01/0001"
                    // reads as a bug rather than as "we have never seen this device".
                    const lastSeenStr = (lastSeenDate && lastSeenDate.getFullYear() > 1)
                        ? lastSeenDate.toLocaleDateString() : t('profileForm.unknown');
                    // Both spellings, because the list can come from the plugin's own
                    // camelCase JSON or straight off a Jellyfin DTO.
                    const isChecked = allowedIds.indexOf(deviceId.toLowerCase()) !== -1
                        || (!!dev.DeviceId && allowedIds.indexOf(String(dev.DeviceId).toLowerCase()) !== -1);
                    return `
                        <div class="device-dropdown-item" data-device-id="${escapeHtml(deviceId)}">
                            <label style="display: flex; align-items: center; gap: 8px; cursor: pointer; flex: 1; margin: 0; font-size: 0.9rem; min-width: 0;">
                                <input type="checkbox" class="${boxClass}" value="${escapeHtml(deviceId)}" ${isChecked ? 'checked' : ''} style="cursor: pointer; accent-color: var(--jpf-accent); flex-shrink: 0;" />
                                <span style="display: flex; flex-direction: column; min-width: 0;">
                                    <span class="device-row-name" style="font-weight: 500; overflow: hidden; text-overflow: ellipsis;">${escapeHtml(deviceName)}</span>
                                    <span style="font-size: 0.75rem; opacity: 0.6;">${t('profileForm.deviceLastSeen', { client: escapeHtml(client), date: lastSeenStr })}</span>
                                </span>
                            </label>
                        </div>
                    `;
                }).join('')
                : `
                    <div style="padding: 12px; text-align: center; opacity: 0.6; font-size: 0.9rem;">${t(unavailable ? 'profileForm.devicesUnavailable' : 'profileForm.noDevicesYet')}</div>
                `;

            return `
                <div class="form-group">
                    <label>${t('profileForm.allowedDevices')}</label>
                    <div class="devices-dropdown-container" style="position: relative;">
                        <div id="${p}devices-dropdown-trigger" class="devices-dropdown-trigger" tabindex="0" role="button" aria-expanded="false">
                            <span id="${p}devices-dropdown-selected-text">${t(unavailable && isEdit ? 'profileForm.devicesUnchanged' : 'profileForm.allDevicesAllowed')}</span>
                        </div>
                        <div id="${p}devices-dropdown-list" class="devices-dropdown-list" style="display: none;">
                            ${items}
                        </div>
                    </div>
                    <div class="form-hint">${t('profileForm.devicesHint')}</div>
                </div>
            `;
        },

        /// The maximum-rating select, shared by both profile forms.
        ///
        /// The five options and their numeric values were written out twice; a rating
        /// added to one copy would silently not exist in the other. Only the id prefix
        /// and which option is preselected differ.
        ///
        /// `maxRating` is compared with ===, so it must already be a number or null.
        /// Passing the raw string from a form control would preselect nothing and
        /// quietly reset the profile's rating on the next save.
        /// `ratings` is the server's plugins/profiles/ratings answer. With it, the
        /// options are that country's ratings and nothing else; without it — an older
        /// server, a failed request — they are the four US ones this form always had.
        renderRatingSelect: function (mode, maxRating, ratings) {
            const id = mode + '-rating-select';
            const country = ratingChoicesFrom(ratings);
            // [value, text]. Country labels are official codes (12A, FSK 16, 14A) and are
            // shown as they are in every language; only the age around them is translated.
            const choices = [['', t('profileForm.noRestrictions')]].concat(country.length
                ? country.map(([score, labels]) => [String(score), t('profileForm.ratingAge', { labels: escapeHtml(labels), score: score })])
                : [
                    ['6', t('profileForm.ratingG')],
                    ['10', t('profileForm.ratingPG')],
                    ['14', t('profileForm.ratingPG13')],
                    ['17', t('profileForm.ratingR')]
                ]);
            // A limit set in Dashboard -> Users is any score, not necessarily one offered
            // here. With no option to preselect, the select fell back to "No Restrictions"
            // and the next save cleared the limit, whatever the save was for. It gets an
            // option of its own.
            const unlisted = typeof maxRating === 'number'
                && !choices.some(([value]) => value !== '' && Number(value) === maxRating);
            if (unlisted) choices.push([String(maxRating), t('profileForm.ratingCurrent', { score: maxRating })]);
            const opts = choices.map(([value, text]) => {
                const current = value === '' ? maxRating === null || maxRating === undefined
                    : maxRating === Number(value);
                return `<option value="${value}" ${current ? 'selected' : ''}>${text}</option>`;
            }).join('\n                            ');

            return `
                <div class="form-group">
                    <label for="${id}">${t('profileForm.maximumRating')}</label>
                    <select id="${id}">
                            ${opts}
                    </select>
                </div>
            `;
        },

        /// Blocked and allowed tag editors, shared by both profile forms.
        ///
        /// Only the id prefix and the starting tags differ: a new profile begins with
        /// none, an existing one with whatever it has. The allowed-tags hint carries
        /// form-hint-warn because an allow-list is subtractive — naming one tag hides
        /// everything without it — and that warning existing in only one of two copies
        /// is exactly the drift this merge is for.
        renderTagSection: function (mode, libraryTags, profile) {
            const p = mode + '-';
            const suggestions = p + 'tag-suggestions';
            const blocked = (profile && profile.blockedTags) || [];
            const allowed = (profile && profile.allowedTags) || [];

            return `
                ${this.renderTagSuggestions(suggestions, libraryTags)}
                <div class="form-group">
                    <label>${t('profileForm.blockedTags')}</label>
                    ${this.renderTagEditor(p + 'blocked-tags', blocked, t('profileForm.tagPlaceholderAdults'), suggestions)}
                    <div class="form-hint">${t('profileForm.blockedTagsHint')}</div>
                </div>
                <div class="form-group">
                    <label>${t('profileForm.allowedTags')}</label>
                    ${this.renderTagEditor(p + 'allowed-tags', allowed, t('profileForm.tagPlaceholderKids'), suggestions)}
                    <div class="form-hint form-hint-warn">${t('profileForm.allowedTagsHint')}</div>
                </div>
            `;
        },

        initTagEditors: function (container) {
            container.querySelectorAll('.tag-editor').forEach(editor => {
                if (editor._tagInit) return;
                editor._tagInit = true;

                const chipList = editor.querySelector('.tag-chip-list');
                const input = editor.querySelector('.tag-input');
                const addBtn = editor.querySelector('.tag-add-btn');
                if (!chipList || !input || !addBtn) return;

                const syncEmpty = () => {
                    if (chipList.querySelector('.tag-chip')) {
                        chipList.removeAttribute('data-empty');
                    } else {
                        // Set here too: a list that starts non-empty carries no label, and
                        // removing its last chip would otherwise show an empty grey box.
                        chipList.setAttribute('data-empty-label', t('profileForm.noTags'));
                        chipList.setAttribute('data-empty', 'true');
                    }
                };

                const addTag = () => {
                    const value = (input.value || '').trim();
                    input.value = '';
                    if (!value) return;
                    const exists = Array.from(chipList.querySelectorAll('.tag-chip')).some(
                        chip => (chip.getAttribute('data-tag') || '').toLowerCase() === value.toLowerCase());
                    if (!exists) {
                        chipList.insertAdjacentHTML('beforeend', this.renderTagChip(value));
                        syncEmpty();
                    }
                };

                addBtn.addEventListener('click', (e) => { e.preventDefault(); addTag(); input.focus(); });
                input.addEventListener('keydown', (e) => {
                    if (e.key === 'Enter') { e.preventDefault(); addTag(); }
                });
                // Committing on blur means a typed-but-not-added tag isn't silently lost on save.
                input.addEventListener('blur', () => addTag());

                chipList.addEventListener('click', (e) => {
                    const removeBtn = e.target.closest('.tag-chip-remove');
                    if (!removeBtn) return;
                    e.preventDefault();
                    removeBtn.closest('.tag-chip').remove();
                    syncEmpty();
                });
                chipList.addEventListener('keydown', (e) => {
                    if (e.key !== 'Enter' && e.key !== ' ') return;
                    const removeBtn = e.target.closest('.tag-chip-remove');
                    if (!removeBtn) return;
                    e.preventDefault();
                    removeBtn.closest('.tag-chip').remove();
                    syncEmpty();
                });
            });
        },

        getTagEditorValues: function (container, id) {
            const editor = container.querySelector('#' + id);
            if (!editor) return [];
            return Array.from(editor.querySelectorAll('.tag-chip'))
                .map(chip => chip.getAttribute('data-tag'))
                .filter(tag => tag);
        },

        /// Runs one startup step, and refuses to let it take the rest of init() with it.
        ///
        /// 1.5.2 shipped a stray backtick inside the stylesheet template literal in
        /// injectStyles. The file still parsed — it was valid JavaScript, just not what
        /// anyone meant — so the syntax check passed and it went out. At runtime
        /// injectStyles threw, and because init() called these bare, the nine steps after
        /// it never ran: no focus trap, so a remote could not reach the gate; no switcher
        /// preferences; no panic shortcut, which is the escape hatch for exactly this;
        /// no session validation. bindEvents had already run, so the route poll kept
        /// raising an unstyled gate and the plugin looked alive while almost nothing
        /// worked. A broken stylesheet should cost styling and nothing else.
        _step: function (name, fn) {
            try {
                fn();
            } catch (e) {
                console.error('ProfilesPlugin: startup step "' + name + '" failed.', e);
            }
        },

        /// Upstream's TV test, copied so the two cannot disagree.
        ///
        /// From jellyfin-web src/scripts/browser.js isTv(), which is the function behind
        /// browser.tv and therefore behind layoutManager's own auto-detection. Its own
        /// first comment is "This is going to be really difficult to get right", which is
        /// why this is a copy rather than something reasoned out afresh.
        ///
        /// `web0s` is spelt with a ZERO. That is upstream's spelling because it is what
        /// LG actually puts in the user agent, and writing the letter O here would miss
        /// every webOS set silently.
        _uaLooksLikeTv: function (ua) {
            const s = String(ua || '').toLowerCase();
            // The Oculus browser carries samsungbrowser in its UA and is a headset.
            if (s.indexOf('oculusbrowser') !== -1) return false;
            // No samsungbrowser: upstream dropped it in 12.x (browser.js, with a test),
            // because Samsung Internet on an Android phone carries it too and was laid out
            // as a television. A Samsung set says SMART-TV and TV, so 'tv' still finds it.
            return s.indexOf('tv') !== -1
                || s.indexOf('viera') !== -1
                || s.indexOf('titanos') !== -1
                || s.indexOf('netcast') !== -1
                || s.indexOf('web0s') !== -1;
        },

        /// Does this browser lay out flex containers with `gap`?
        ///
        /// Chrome 84. webOS 5 (the LG CX) is Chromium 68 and Tizen 6.0 is 76, so on both
        /// of the televisions this plugin has open issues from, every `gap` on a flex
        /// container is ignored and the spacing between children is simply gone.
        ///
        /// Measured, not asked. `@supports (gap: 1px)` answers TRUE on Chromium 68,
        /// because grid gap shipped in 66 and the query cannot tell the two layout modes
        /// apart — so the obvious feature query returns yes on exactly the devices where
        /// flex gap does nothing. Two empty children in a column box with a 1px row-gap
        /// make the box 1px tall where it works and 0 where it does not.
        _supportsFlexGap: function () {
            if (this._flexGapOk !== undefined) return this._flexGapOk;
            if (!document.body) return true;   // too early to tell; assume modern
            let probe;
            try {
                probe = document.createElement('div');
                probe.style.cssText = 'display:flex;flex-direction:column;row-gap:1px;'
                    + 'position:absolute;visibility:hidden;pointer-events:none';
                probe.appendChild(document.createElement('div'));
                probe.appendChild(document.createElement('div'));
                document.body.appendChild(probe);
                this._flexGapOk = probe.scrollHeight === 1;
            } catch (e) {
                this._flexGapOk = true;
            } finally {
                if (probe && probe.parentNode) probe.parentNode.removeChild(probe);
            }
            return this._flexGapOk;
        },

        /// Puts `jpf-tv` and `jpf-no-flex-gap` on <html>, so the stylesheet can size for
        /// a screen three metres away and can replace flex gaps the device ignores.
        ///
        /// The plan said to read `layoutManager.tv`. It cannot be read: layoutManager is
        /// an ES module default export in jellyfin-web and is never placed on window.
        /// What it does do is write `layout-tv` / `layout-mobile` / `layout-desktop` onto
        /// document.documentElement (src/components/layoutManager.js, setLayout), so that
        /// class is the supported signal — and it is strictly better than sniffing,
        /// because it also honours a layout the user has forced in Display settings. A
        /// desktop set to TV mode gets TV sizing, which sniffing would never give it.
        ///
        /// The UA is only the fallback, for the two moments the class is not there yet:
        /// a cold load where we run before layoutManager.init(), and the pre-sign-in gate.
        ///
        /// Re-run on class changes rather than on a timer. layoutManager can switch mode
        /// at runtime and fires 'modechange', but that event is on the module object we
        /// cannot reach; the class attribute it writes is observable, costs nothing when
        /// nothing changes, and is not the route poll's business.
        applyDeviceClasses: function () {
            const root = document.documentElement;
            if (!root) return;

            // Writes only on an actual change. The observer below watches the very
            // attribute this touches, and a write that changes nothing would still be a
            // mutation record and a second pass through here on every unrelated class
            // change Jellyfin makes.
            const apply = () => {
                const isTv = root.classList.contains('layout-tv')
                    || (!root.classList.contains('layout-mobile')
                        && !root.classList.contains('layout-desktop')
                        && this._uaLooksLikeTv(navigator.userAgent));
                if (isTv !== root.classList.contains('jpf-tv')) {
                    root.classList.toggle('jpf-tv', isTv);
                }
            };

            apply();
            root.classList.toggle('jpf-no-flex-gap', !this._supportsFlexGap());

            if (this._deviceClassObserver || typeof MutationObserver === 'undefined') return;
            this._deviceClassObserver = new MutationObserver(() => apply());
            this._deviceClassObserver.observe(root, {
                attributes: true,
                attributeFilter: ['class'],
            });
        },

        init: function () {
            if (typeof ApiClient === 'undefined') {
                // If ApiClient is not defined yet, wait for it
                setTimeout(() => this.init(), 100);
                return;
            }
            // viewshow fires when Jellyfin's React view system finishes rendering a view.
            // We gate _revealPage() on this event so we never fade in to a blank shell.
            this._viewShowFired = false;
            this._pendingReveal = false;
            // Kicked off now so it usually finishes before anyone actually needs a
            // translated string — fetchAndRenderProfiles awaits it just before drawing
            // the gate for the first time, which is the only render early enough to
            // matter.
            this._step('loadLocale', () => { this._i18nReady = loadLocale(); });
            // fetchAndRenderProfiles awaits this before the first gate render, so it has to
            // be a promise even if the step above failed.
            if (!this._i18nReady) this._i18nReady = Promise.resolve();
            // Follows Jellyfin's Display language when it changes without a reload. Screens
            // drawn after that are in the new language; one already open keeps its text.
            this._step('watchJellyfinLanguage', () => watchJellyfinLanguage(() => {
                this._i18nReady = loadLocale();
            }));

            this._step('bindEvents', () => this.bindEvents());
            // Once per page load, never on the route poll.
            try { localStorage.removeItem(this.config.deviceGateKey); } catch (e) { /* blocked */ }
            // Before injectStyles, so the sheet's .jpf-tv and .jpf-no-flex-gap rules match
            // on the first paint rather than after a reflow the viewer can see.
            this._step('applyDeviceClasses', () => this.applyDeviceClasses());
            // Same reason: the first frame of the gate should already be in this
            // account's theme.
            // Before applyCachedTheme, which would otherwise turn a custom theme it does not
            // know yet into Bonfire for the first frame.
            this._step('restoreCustomThemes', () => this.restoreCustomThemes());
            this._step('applyCachedTheme', () => this.applyCachedTheme());
            this._step('injectStyles', () => this.injectStyles());
            this._step('loadCustomThemes', () => this.loadCustomThemes());
            // Before anything that can decide about the gate, so every one of those
            // decisions knows whether a cover is waiting on it.
            this._step('_adoptCover', () => this._adoptCover());
            // Kicked off before the first route check so the gate decision is usually made
            // with the real answer in hand rather than the cached one.
            // Before any Jellyfin view renders: the cached rules have to be in the document
            // by the time the first library card paints, or the artwork this profile is not
            // meant to see gets a frame on screen.
            this._step('applyCachedLibraryArtwork', () => this.applyCachedLibraryArtwork());
            this._step('loadLibraryArtwork', () => this.loadLibraryArtwork());
            this._step('loadSwitcherPrefs', () => this.loadSwitcherPrefs());
            this._step('bindPanicShortcut', () => this.bindPanicShortcut());
            // Bound once for the life of the page. It resolves the active Bonfire screen on
            // every event and does nothing when there is none, so it covers the gate, the
            // PIN prompt, the profile forms and every dialog without per-screen wiring.
            this._step('_bindOverlayFocusTrap', () => this._bindOverlayFocusTrap());
            // Before validateSessionState, which can trigger a reload of its own.
            this._step('checkPersistedPanic', () => this.checkPersistedPanic());
            if (this._panicDisabled) return;
            // Once per page load, never from checkRoute.
            this._step('checkMaintenance', () => this.checkMaintenance());
            this._step('validateSessionState', () => this.validateSessionState());
            // If the user refreshes while a profile is active, restart the inactivity timer
            setTimeout(() => this._step('initLockoutTimer', () => this.initLockoutTimer()), 800);
        },

        bindEvents: function () {
            const doCheck = () => this.checkRoute();

            // Jellyfin view-system events (fires on every page/view change)
            // Use doCheck so all logic is centralised in checkRoute.
            document.addEventListener('viewshow', () => {
                // Mark the view as ready so _revealPage() knows content is rendered.
                this._viewShowFired = true;
                // If _revealPage() was deferred waiting for this event, run it now.
                if (this._pendingReveal) this._revealPage();
                doCheck();
            });

            this._bindSettingsRefresh();

            // SPA navigation events
            window.addEventListener('popstate', doCheck);
            window.addEventListener('hashchange', doCheck);

            // The corner-pill fallback is placed by geometry, so its position is stale
            // only when the viewport changes — a rotation on a phone, a window resize.
            // Marking it here is what lets the route tick stop recomputing it.
            ['resize', 'orientationchange'].forEach(evt => {
                window.addEventListener(evt, () => { this._fallbackPosDirty = true; });
            });

            // Intercept history.pushState / replaceState (React-router style navigation)
            ['pushState', 'replaceState'].forEach(method => {
                const orig = history[method];
                history[method] = function (...args) {
                    orig.apply(history, args);
                    // Small delay so the URL is committed before we read it
                    setTimeout(doCheck, 0);
                };
            });

            // Clear the cached master token on native Jellyfin sign-out, so it does not
            // persist in localStorage on a shared or public device. Signing out of
            // Jellyfin has to mean signing out of Bonfire too, or the next person at the
            // machine inherits the switcher.
            document.addEventListener('usersignedout', () => {
                try {
                    localStorage.removeItem(this.config.masterStorageKey);
                    localStorage.removeItem(this.config.activeSessionKey);
                    localStorage.removeItem(this.config.switcherModeKey);
                    this._switcherPrefs = null;
                    // Learned from the same response as the preferences, so it goes with them.
                    this._panicLinkAvailable = null;
                    localStorage.removeItem(this.config.libraryArtKey);
                    this.clearProfileSession();
                } catch (e) { /* ignore storage errors */ }
            });

            // 500 ms is sufficient because viewshow, popstate and hashchange already cover
            // every SPA navigation. The poll is only a safety net for the cases that change
            // the DOM without changing the route — the video OSD is the one that matters.
            //
            // It backs off to 2 s once playback starts. There is nothing for the plugin to
            // do during playback — the gate is not raised, the bubble is hidden, and the
            // menus are not on screen — and that is exactly when the device has least
            // headroom: a TV decoding 4K is the weakest hardware in the house doing the
            // hardest thing it ever does. Four times fewer wake-ups for work that has no
            // effect.
            //
            // Not stopped outright. The tick is also what notices playback *ending* on a
            // client whose URL does not change, and a poll that switched itself off would
            // then need something else to switch it back on. 2 s is a quarter of a second
            // of latency on the bubble reappearing, which nobody can see.
            this._tickMs = 500;
            const arm = () => {
                clearInterval(this._routeTimer);
                this._routeTimer = setInterval(() => {
                    doCheck();
                    // _lastRouteType is set by checkRoute, so this reads the classification
                    // that has just been made rather than repeating the work to get it.
                    const want = this._lastRouteType === 'videoosd' ? 2000 : 500;
                    if (want !== this._tickMs) {
                        this._tickMs = want;
                        arm();
                    }
                }, this._tickMs);
            };
            arm();

            // Initial check on load
            setTimeout(doCheck, 200);
        },

        /// True when the browser is sitting on the home screen.
        ///
        /// Extracted from checkRoute so reloadAtHome() can ask the same question. The two
        /// must agree: reloadAtHome only redirects when this is false, so a route this
        /// says is home is a route it will never try to build a URL for.
        isHomeRoute: function () {
            const hash = window.location.hash || '';
            const path = window.location.pathname || '';

            // Check if we are on the home screen
            // The home screen route can be: empty, '#/', '#/home', '#/home.html', or similar.
            // But we must NOT trigger it if we are on pages like configuration, plugins, selectserver, login, etc.
            const isIgnoredPage = hash.includes('configuration') ||
                                 hash.includes('plugin') ||
                                 hash.includes('login') ||
                                 hash.includes('selectserver') ||
                                 path.includes('configuration') ||
                                 path.includes('plugin') ||
                                 path.includes('login') ||
                                 path.includes('selectserver');

            if (isIgnoredPage) return false;

            // 10.11 moved the route out of the fragment and into the path, so on those
            // builds the hash is empty on every page — and the `hash === ''` clause below
            // was therefore answering "yes, this is home" for all of them. Whenever the
            // path carries a route, it is the authority and the hash has nothing to say.
            const webRoute = path.match(/^.*\/web\/(.*)$/);
            if (webRoute) {
                const route = webRoute[1].replace(/\/+$/, '');
                if (route !== '' && route !== 'index.html') {
                    return route === 'home' || route === 'home.html';
                }
            }

            return (
                hash === '' ||
                hash === '#/' ||
                hash.includes('home') ||
                path.endsWith('/home') ||
                path.endsWith('/home.html') ||
                // If there is no hash and we are at /web/index.html or root
                (!hash && (path.endsWith('index.html') || path === '/' || path === '/web/'))
            );
        },

        /// The home screen's URL in whichever routing style this build of jellyfin-web
        /// uses, or null when neither shape is recognisable.
        ///
        /// Two styles are in the wild: 10.10 and earlier keep the route in the fragment
        /// ('/web/index.html#/home.html'), 10.11 moved it into the path ('/web/home').
        /// Guessing wrong here means a 404 rather than a stale page, so an unrecognised
        /// shape returns null and the caller stays put instead.
        homeUrl: function () {
            const loc = window.location;
            const hash = loc.hash || '';

            // Hash-routed: the document does not change, only the fragment. Whichever
            // prefix this build uses is reused rather than guessed at.
            if (hash.startsWith('#!/')) return loc.pathname + loc.search + '#!/home.html';
            if (hash.startsWith('#/')) return loc.pathname + loc.search + '#/home.html';

            // Path-routed: '/web/mypreferencesmenu' -> '/web/home'. Matched from '/web/'
            // so a server hosted on a base path keeps it.
            const m = loc.pathname.match(/^(.*\/web\/)/);
            if (m) return m[1] + 'home';

            return null;
        },

        /// Reloads onto the home screen. For every caller here the signed-in identity has
        /// just changed, so the app must re-initialise under the new token — and the page
        /// that was on screen belonged to the previous profile.
        ///
        /// Issue #22: this used to be a bare reload, which preserves the route. Switching
        /// from the user menu therefore reloaded /web/mypreferencesmenu as the new
        /// profile, leaving the user to find their own way home. The gate never showed it
        /// because the gate only ever runs from home, where the two are the same thing.
        reloadAtHome: function () {
            // Already home: reload exactly as before. This is the path the gate uses and
            // the one with hardware testing behind it, so it is left untouched. It also
            // keeps homeUrl() away from the one case it could get wrong — a hash-routed
            // build sitting at '/web/' with no fragment, where the path branch would
            // wrongly produce '/web/home'.
            if (this.isHomeRoute()) {
                this._reloadWithFallback(null);
                return;
            }

            this._reloadWithFallback(this.homeUrl());
        },

        /// How long to wait before deciding a navigation attempt did not take.
        ///
        /// Long enough that a slow television is not interrupted mid-teardown, short
        /// enough that somebody who tapped a profile is not left staring at the wrong
        /// account. A page that is genuinely unloading never reaches the timer.
        RELOAD_ESCALATE_MS: 1200,

        /// Reloads the page, and checks that the reload happened.
        ///
        /// By the time this runs the switch is complete everywhere except the screen: the
        /// profile's token is in localStorage and in ApiClient, and all that is left is for
        /// the document to be rebuilt under the new identity. If that does not happen,
        /// jellyfin-web goes on rendering the page it built for the *previous* profile —
        /// its home sections, its Continue Watching, its Next Up — while the header shows
        /// the profile that was picked, because Bonfire draws that part itself.
        ///
        /// Reported on Samsung Tizen as "it lets you pick a profile, but nothing changes
        /// once you load in". The credentials, the token and the server were all ruled out
        /// first; what was left is that `location.reload()` is not reliable everywhere this
        /// runs. The bundled television clients serve jellyfin-web from a local file://
        /// origin, which is where it has been seen to do nothing at all.
        ///
        /// So each step is a different mechanism rather than a retry of the same one. The
        /// last rung says so out loud: showing a child the parent's Continue Watching while
        /// they believe it is theirs is worse than admitting the reload failed.
        _reloadWithFallback: function (target) {
            const step = (n) => {
                try {
                    if (n === 0) {
                        if (target) {
                            // replace() rather than assignment so Back does not return to
                            // the previous profile's page.
                            window.location.replace(target);
                            // A fragment-only change does not reload the document, and a
                            // full re-initialisation is the entire point of the call.
                            if (target.indexOf('#') !== -1) window.location.reload();
                        } else {
                            window.location.reload();
                        }
                    } else if (n === 1) {
                        // Assignment, not replace(): a different code path in the browser,
                        // which is the only reason to try it at all.
                        window.location.href = target || window.location.href;
                    } else if (n === 2) {
                        // Force a cache-busting navigation. Some webviews treat a reload
                        // of an identical URL as a no-op but will honour a changed query.
                        const url = target || window.location.href;
                        const sep = url.indexOf('?') === -1 ? '?' : '&';
                        const bust = url.indexOf('#') === -1
                            ? url + sep + 'jpf=' + Date.now()
                            : url.replace('#', sep + 'jpf=' + Date.now() + '#');
                        window.location.href = bust;
                    } else {
                        this._reloadFailed();
                        return;
                    }
                } catch (e) {
                    // A blocked navigation must not stop the ladder.
                }
                this._reloadTimer = setTimeout(() => step(n + 1), this.RELOAD_ESCALATE_MS);
            };

            // A navigation that has committed fires pagehide, and on a set slow enough to
            // still be running timers a step later the escalation would then navigate a
            // second time — onto a cache-busted URL nobody asked for. The ladder is for a
            // reload that does nothing, not for one that is merely slow.
            //
            // Taken off again when the ladder ends, which is not academic: the last rung
            // reveals the page and leaves the viewer able to try another profile, so a
            // ladder that never unbinds leaves a pair of listeners behind per attempt.
            this._releaseReloadGuards();
            const stop = () => { if (this._reloadTimer) clearTimeout(this._reloadTimer); };
            this._reloadGuards = stop;
            window.addEventListener('pagehide', stop);
            window.addEventListener('beforeunload', stop);

            step(0);
        },

        /// Nothing rebuilt the page, so the profile that was chosen is signed in but the
        /// screen still belongs to whoever was there before. Reveal the page and say so —
        /// the alternative is leaving somebody looking at another person's library and
        /// believing it is their own.
        /// Drops the pagehide/beforeunload pair the ladder arms. Safe with none outstanding.
        _releaseReloadGuards: function () {
            if (!this._reloadGuards) return;
            window.removeEventListener('pagehide', this._reloadGuards);
            window.removeEventListener('beforeunload', this._reloadGuards);
            this._reloadGuards = null;
        },

        _reloadFailed: function () {
            this._reloading = false;
            this._releaseReloadGuards();
            try { localStorage.removeItem(this.config.switchingKey); } catch (e) { /* ignore */ }

            // The switch hid the page behind an opaque overlay, or behind the head
            // script's opacity:0. Either would leave this message invisible.
            document.documentElement.style.removeProperty('opacity');
            document.documentElement.style.removeProperty('background');
            this._dropCover();
            const overlay = document.getElementById('profiles-gate-overlay');
            if (overlay) overlay.remove();

            console.error('ProfilesPlugin: the page could not be reloaded after switching.');
            this.showAlert(t('switcher.reloadFailedTitle'), t('switcher.reloadFailedBody'));
        },

        checkRoute: function () {
            // Emergency disable: do nothing at all. This runs on a 500 ms timer, so without
            // this guard it would rebuild the gate immediately after the teardown.
            if (this._panicDisabled) return;

            const hash = window.location.hash || '';
            const path = window.location.pathname || '';

            const isHome = this.isHomeRoute();

            // skipReveal: set to true when we know the gate overlay is about to be shown.
            // In that case showProfileOverlay() will call _revealPage() once the overlay
            // covers the page, preventing a blank-home flash during the async profile fetch.
            let skipReveal = false;

            if (isHome) {
                // Preferences are null until we have learned them for this account. Waiting
                // is the safe direction: loadSwitcherPrefs() re-runs this check the moment it
                // resolves, so at worst the gate arrives a beat late — whereas guessing would
                // flash a full-screen overlay at somebody who turned it off, on every load.
                //
                // Usually unknown because init() ran before the user had signed in, so there
                // was no token to ask with. Retrying here is what gets the gate working on the
                // first home screen after a fresh login. loadSwitcherPrefs guards itself and
                // settles on the historical default if the request fails, so this cannot spin
                // on the 500 ms route poll.
                // _panicLinkAvailable rides on the same response and is never cached, so a
                // browser holding a valid preferences cache would otherwise never learn it
                // and would hide the emergency link for the whole session.
                if (this.getSwitcherPrefs() === null || this._panicLinkAvailable === null) {
                    this.loadSwitcherPrefs();
                }

                // Same reason: init runs before sign-in on a fresh load, and there was no
                // token to ask with. Without a retry the artwork rules never arrive.
                if (!this._libraryArtLoaded) this.loadLibraryArtwork();

                if (this.shouldAskOnStartup()
                    && !this.isProfileSessionActive()
                    && !document.getElementById('profiles-gate-overlay')) {
                    // This used to call interceptHomeAndShowProfiles on every tick until the
                    // gate existed, and each call sent its own /list: a slow server got a
                    // pile of them, a down one got one every 500 ms. Now: a request already
                    // out means the gate is on its way, so keep the page hidden and wait for
                    // it; a recent failure means it is not, so let the page show (the catch
                    // dropped the cover) and ask again when POLL_RETRY_MS is up.
                    if (this._listPending()) {
                        skipReveal = true;
                    } else if (this._listPollMayRequest()) {
                        skipReveal = true;
                        this.interceptHomeAndShowProfiles();
                    }
                }
            } else {
                // If we navigate away from home, ensure the overlay is removed if it somehow got stuck
                //
                // Not while a switch is reloading the page. executeProfileSwitch marks the
                // new profile active *before* it reloads, which is exactly this test's
                // "stuck" signature — so on any page but home (the switcher opened from the
                // button or the user menu) the next tick took the opaque gate down and showed
                // the old profile's page until the navigation landed. On a television that is
                // most of a second.
                // Nor while first-time setup is on screen: a field read, no DOM work.
                if (!this._reloading && !this._onboarding
                    && document.getElementById('profiles-gate-overlay') && this.isProfileSessionActive()) {
                    this.removeProfileOverlay();
                }
            }

            // Monitor and hide shadow profiles from admin user management list
            const isUsersPage = hash.includes('users') || path.includes('users');
            if (isUsersPage) {
                this.monitorAndHideShadowProfiles();
            } else {
                if (this.usersObserver) {
                    this.usersObserver.disconnect();
                    this.usersObserver = null;
                }
                this.isMonitoringUsers = false;
                this.subProfileIdsToHide = null;
            }

            // ── Active player: URL-based detection ──────────────────────────────
            const isActivePlayer = hash.includes('videoosd') ||
                                   hash.includes('/nowplaying') ||
                                   (hash.includes('video') && !hash.includes('videos'));

            // ── Active player: DOM-based detection (catches delayed URL updates) ──
            // The OSD element appears in the DOM the moment playback starts.
            // Class selectors only. This list carried [class*="videoOsd"] and
            // [class*="osdBottom"] as well, and an unqualified attribute-substring
            // selector cannot use the class index — the engine walks every element in the
            // document and tests each one. That ran twice a second, for the life of the
            // page, on every device, including the weakest hardware in the house *during
            // playback*, which is the one moment it has least to spare.
            //
            // They also never matched anything the three explicit names beside them did
            // not already match: .videoOsdBottom is itself the element [class*="videoOsd"]
            // was reaching for. Verified against jellyfin-web 10.11 —
            // src/controllers/playback/video/index.html — and recorded in
            // tests/upstream-selectors.json.
            const hasOsdDom = !!document.querySelector(
                '.videoOsdBottom, .osdControls, .upNextContainer, .btnExitVideo'
            );

            // ── Admin / server-management pages ─────────────────────────────────
            // Exception: our own plugin settings page (configurationpage?name=Bonfire) is
            // the only admin-area page where the button should stay visible.
            //
            // Both spellings are accepted. The page was registered as "Profiles" until 1.5
            // and the name is what forms this URL, so a browser sitting on the old address —
            // a bookmark, or a tab open across the upgrade — would otherwise lose the button.
            const isProfilesSettingsPage = hash.includes('configurationpage')
                && (hash.toLowerCase().includes('name=bonfire')
                    || hash.toLowerCase().includes('name=profiles'));

            const isDashboard = !isProfilesSettingsPage && (
                hash.includes('dashboard')       || hash.includes('/admin')       ||
                hash.includes('useredit')        || hash.includes('usernew')      ||
                hash.includes('userparentalcontrol') || hash.includes('userlibraryaccess') ||
                hash.includes('userpassword')    || hash.includes('scheduledtasks') ||
                hash.includes('serveractivity')  || hash.includes('installedplugins') ||
                hash.includes('pluginscatalog')  || hash.includes('apikeys')      ||
                hash.includes('devices')         || hash.includes('dlnaprofiles') ||
                hash.includes('dlnasettings')    || hash.includes('networking')   ||
                hash.includes('notificationlist')|| hash.includes('streamingsettings') ||
                hash.includes('playbackconfiguration') ||                           
                hash.includes('library.html')    || hash.includes('librarydisplay') ||
                hash.includes('librarypathmapping') || hash.includes('log.html')  ||
                hash.includes('metadataeditor')  || hash.includes('metadatamanager') ||
                hash.includes('edititemmetadata')|| hash.includes('mediainfo')    ||
                hash.includes('configurationpage')  || // all other plugin config pages
                path.includes('dashboard')       || path.includes('/admin')
            );

            // The sign-in and server-select screens belong to nobody. validateSessionState
            // already forgets the previous account there, but only once, at init — and the
            // iOS app signs out without reloading, so init never runs again and the old
            // account's profile and master carried straight into the next sign-in. Classified
            // here, from the URL this tick already read, so the bubble can act on it without
            // a second look at location. Same four tests validateSessionState uses: the two
            // must agree on what a sign-in screen is.
            const isSignIn = hash.includes('login') || hash.includes('selectserver')
                          || path.includes('login') || path.includes('selectserver');

            const viewType = isSignIn                        ? 'signin'
                           : (isActivePlayer || hasOsdDom) ? 'videoosd'
                           : isDashboard                   ? 'dashboard'
                           : isHome                        ? 'home'
                                                          : 'other';
            this._lastRouteType = viewType;
            this.evaluateFloatingBubbleVisibility(viewType);

            // Menu-location entry points. Both are re-asserted on every route change
            // because React discards and rebuilds these views freely; the calls are cheap
            // and no-op when the element is already in place.
            this.syncUserMenuEntry();
            this.syncPreferencesMenuEntry();
            if (this.isMenuLocation()) {
                const isUserProfilePage = hash.includes('userprofile') || path.includes('userprofile');
                if (isUserProfilePage) this.injectProfilePageSection();
            }

            // Reveal the page now that the gate decision has been made.
            // Skip when skipReveal is set — the overlay isn't in the DOM yet and
            // revealing now would show a blank page during the profile fetch.
            if (!skipReveal) this._revealPage();

            // The startup cover is waiting on exactly the decision made above. If it was
            // not "raise the gate" — a profile is already active, the preferences say no
            // or are not this account's, this is not home, the sign-in page — there is
            // nothing coming to replace it. A cached boolean first: this runs twice a
            // second for the life of the page, and the cover is up for a second or two of
            // it at most.
            // Off home it comes down even while a gate request is out: that gate is never
            // going to be drawn here.
            if (this._coverUp && !skipReveal && !this._coverForSwitch
                && (!isHome || !this._coverHeldForGate)) this._dropCover();
        },

        // Smoothly fades the page back in after a profile switch.
        // Guards on _viewShowFired to ensure React has rendered the view before we
        // expose it — otherwise a blank white shell flashes for a moment.
        /**
         * Records the colour the page is currently painted in, so the next load can hold
         * that colour while it renders instead of a hardcoded one.
         *
         * The head script used to set background:#101010 and color-scheme:dark
         * unconditionally. Jellyfin ships light themes and lets a user pick any of them,
         * so on every one of those a profile switch flashed a full-screen black rectangle
         * — the exact flash the head script exists to prevent, just in the other
         * direction. It also forced dark form controls and scrollbars for that moment.
         *
         * Read here rather than guessed there, because here is the one place the answer is
         * knowable: the page is fully rendered, the theme stylesheet has applied, and
         * whatever the user is looking at is what we should keep showing.
         *
         * Returns the colour, so the caller can paint with it immediately as well.
         */
        _captureLeavingBackground: function () {
            let colour = '';
            try {
                // The body carries the theme colour in Jellyfin's stylesheets; when it is
                // transparent the colour is on <html> behind it, so ask that instead.
                const fromBody = getComputedStyle(document.body || document.documentElement).backgroundColor;
                colour = this._isSeeThrough(fromBody)
                    ? getComputedStyle(document.documentElement).backgroundColor
                    : fromBody;
            } catch (e) { /* fall through to the default */ }

            if (!colour || this._isSeeThrough(colour)) colour = '#101010';

            // The scheme has to match the colour or the browser paints dark scrollbars and
            // form controls over a light page for the length of the switch.
            const scheme = this._isLightColour(colour) ? 'light' : 'dark';

            try {
                localStorage.setItem(this.config.backgroundKey, colour + '|' + scheme);
            } catch (e) { /* private mode: the head script falls back on its own */ }

            return colour;
        },

        /** True for a colour that is missing or fully transparent. */
        _isSeeThrough: function (colour) {
            if (!colour) return true;
            if (colour === 'transparent') return true;
            const m = /^rgba\(\s*[\d.]+\s*,\s*[\d.]+\s*,\s*[\d.]+\s*,\s*0*\.?0*\s*\)$/.exec(colour);
            return !!m;
        },

        /**
         * Relative luminance, so "which colour-scheme is this" is decided by the colour
         * itself rather than by a list of theme names that would need updating whenever
         * Jellyfin adds one.
         */
        _isLightColour: function (colour) {
            const m = /^rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)/.exec(colour);
            if (!m) return false;
            const r = parseFloat(m[1]), g = parseFloat(m[2]), b = parseFloat(m[3]);
            return (0.2126 * r + 0.7152 * g + 0.0722 * b) > 128;
        },

        // ── The startup cover ──────────────────────────────────────────────────────
        //
        // With "Ask on startup" on, the home screen used to be on view before the gate:
        // jellyfin-web renders home as soon as it has a user, while the gate waits for the
        // first route check (200 ms after init, which itself waits for ApiClient), then for
        // the profile list from the server, then fades in from opacity 0 over 220 ms. On a
        // phone that was a third of a second of somebody else's Continue Watching; on a
        // television more. The head script now covers the page before the first frame
        // when the cached preferences say the gate is coming — see WebInjection.HeadScript
        // for the rules it decides by — and everything here is about taking it down again
        // at the right moment:
        //
        //   - the gate is drawn and opaque           -> showProfileOverlay
        //   - the gate is not coming after all       -> checkRoute, interceptHomeAndShowProfiles
        //   - the gate request failed                -> fetchAndRenderProfiles
        //   - the emergency disable, a failed reload -> applyPanicDisable, _reloadFailed
        //   - none of the above within GATE_COVER_MS -> the backstop timer
        //
        // It is a pseudo-element under the gate's z-index, not opacity on <html>, so the
        // gate can be drawn and fade in while the cover is still behind it, and dropping
        // the cover is then invisible because the two are the same colour.

        _coverUp: false,
        _coverHeldForGate: false,
        _coverExtended: false,

        /// The gate's fade-in (#profiles-gate-overlay's `transition: opacity 0.22s`), with
        /// a margin. Change the two together.
        GATE_FADE_MS: 260,

        /// Notes a cover the head script put up, or puts one up when a page that has no
        /// head script — the Tizen package — is heading for the gate. The second case is
        /// late by construction (the package's own page has been showing for a while) but
        /// still takes home off the screen sooner than the gate would.
        _adoptCover: function () {
            const root = document.documentElement;
            if (root.getAttribute('data-jpf-cover') !== null) {
                this._coverUp = true;
                return;
            }
            // Only the head script knows about the reload that is landing; that page has
            // its own cover and no gate.
            if (window.__jpSwitching) return;
            if (!this._gateIsComing()) return;

            if (!document.getElementById('jpf-cover-css')) {
                const style = document.createElement('style');
                style.id = 'jpf-cover-css';
                style.textContent = GATE_COVER_CSS;
                document.head.appendChild(style);
            }
            root.setAttribute('data-jpf-cover', '');
            this._coverUp = true;
            this._armCoverBackstop();
        },

        /// checkRoute's gate decision, asked ahead of time from what is already cached.
        /// Every input unknown or missing reads as no.
        _gateIsComing: function () {
            try {
                if (localStorage.getItem(this.config.panicKey)) return false;
            } catch (e) { return false; }
            if (!this._hasStoredSession()) return false;
            return this.isHomeRoute() && this.shouldAskOnStartup() && !this.isProfileSessionActive();
        },

        /// True when jellyfin-web holds a token for some server. ApiClient's own token is
        /// empty for a moment during startup, so it is not the thing to ask.
        _hasStoredSession: function () {
            try {
                const creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || 'null');
                return !!(creds && Array.isArray(creds.Servers) && creds.Servers.some(s => s && !!s.AccessToken));
            } catch (e) {
                return false;
            }
        },

        _armCoverBackstop: function () {
            try { clearTimeout(window.__jpCoverT); } catch (e) { /* none armed */ }
            window.__jpCoverT = setTimeout(() => this._dropCover(), GATE_COVER_MS);
        },

        /// The gate request is out. The head script's timer started before this script had
        /// even loaded, so it gets one fresh allowance — once, so a gate that never comes
        /// is still bounded.
        _holdCoverForGate: function () {
            if (!this._coverUp) return;
            this._coverHeldForGate = true;
            if (this._coverExtended) return;
            this._coverExtended = true;
            this._armCoverBackstop();
        },

        /// Puts the cover up for a switch that is about to reload the page. No timer: the
        /// reload replaces the document, and _reloadFailed drops it if that never happens.
        _holdCoverForSwitch: function () {
            try {
                if (!document.getElementById('jpf-cover-css')) {
                    const style = document.createElement('style');
                    style.id = 'jpf-cover-css';
                    style.textContent = GATE_COVER_CSS;
                    (document.head || document.documentElement).appendChild(style);
                }
                clearTimeout(window.__jpCoverT);
                window.__jpCoverT = null;
                document.documentElement.setAttribute('data-jpf-cover', '');
                this._coverUp = true;
                this._coverForSwitch = true;
            } catch (e) { /* no document: nothing to cover */ }
        },

        /// Takes the cover down. Safe to call at any time, any number of times, and with
        /// no cover up; it also clears one this object never noticed going up.
        _dropCover: function () {
            this._coverUp = false;
            this._coverForSwitch = false;
            this._coverHeldForGate = false;
            try {
                clearTimeout(window.__jpCoverT);
                window.__jpCoverT = null;
            } catch (e) { /* no timer */ }
            try { document.documentElement.removeAttribute('data-jpf-cover'); } catch (e) { /* no document */ }
        },

        _revealPage: function () {
            if (this._pageRevealed || !document.documentElement.style.opacity) return;

            // Defer until Jellyfin's view system has finished rendering the view.
            // _viewShowFired is set by the viewshow listener in bindEvents().
            if (!this._viewShowFired) {
                this._pendingReveal = true;
                return;
            }

            this._pageRevealed = true;
            this._pendingReveal = false;

            // The reload this flag was raised for has landed. Clearing it here rather than
            // only in removeProfileOverlay() matters twice over: the session mirror is
            // believed only while the flag is up, so leaving it set would keep a marker
            // alive across a genuine app close; and the head script hides the page whenever
            // it sees the flag, so a stale one cost a fade on every later hard refresh.
            //
            // Safe this late: validateSessionState() ran during init() and has already
            // pulled anything it needed into the in-memory tier.
            if (!this._reloading) {
                try { localStorage.removeItem(this.config.switchingKey); } catch (e) { /* ignore */ }
                // The head script has almost certainly already taken the key; this is the
                // copy that actually outlives it.
                try { window.__jpSwitching = 0; } catch (e) { /* ignore */ }
            }

            if (window.__jpReveal) {
                clearTimeout(window.__jpReveal);
                window.__jpReveal = null;
            }

            document.documentElement.style.transition = 'opacity 0.18s ease';
            document.documentElement.style.opacity = '1';
            setTimeout(() => {
                document.documentElement.style.removeProperty('opacity');
                document.documentElement.style.removeProperty('transition');
                document.documentElement.style.removeProperty('background');
                document.documentElement.style.removeProperty('color-scheme');
                this._pageRevealed = false;
            }, 220);
        },

        isMonitoringUsers: false,
        _usersMappingsRetryAt: 0,
        subProfileIdsToHide: null,
        usersObserver: null,

        monitorAndHideShadowProfiles: function () {
            const apiClient = ApiClient;
            if (!apiClient) return;

            // Fetch only once per page visit
            if (this.isMonitoringUsers) {
                this.applyUsersHide();
                return;
            }
            // A failed fetch settles for POLL_RETRY_MS before it is asked again. The catch
            // below used to just reset isMonitoringUsers, and checkRoute calls this on every
            // 500 ms tick, so a server that answered 403 (a non-admin on this page) or was
            // down got a request twice a second for as long as the page stayed open. Kept
            // across navigation on purpose: leaving and coming back is not news to a server
            // that just failed.
            if (this._usersMappingsRetryAt && Date.now() < this._usersMappingsRetryAt) return;
            this.isMonitoringUsers = true;

            const masterState = this.readMasterState();
            const token = masterState ? masterState.token : apiClient.accessToken();
            if (!token) return;

            const url = apiClient.getUrl('plugins/profiles/admin/mappings');
            fetch(url, {
                headers: this.getAuthHeaders(token)
            })
            .then(res => {
                if (!res.ok) throw new Error("Could not load mappings");
                return res.json();
            })
            .then(data => {
                const subProfiles = data.SubProfiles || [];
                this.subProfileIdsToHide = subProfiles.map(p => this.normalizeGuid(p.ProfileUserId));
                
                // Start a MutationObserver to hide cards as they are rendered dynamically
                if (this.usersObserver) {
                    this.usersObserver.disconnect();
                }

                this.usersObserver = new MutationObserver(() => this.applyUsersHide());
                this.usersObserver.observe(document.body, { childList: true, subtree: true });
                this.applyUsersHide();
            })
            .catch(err => {
                console.error("ProfilesPlugin: Error fetching admin mappings for hide logic:", err);
                this._usersMappingsRetryAt = Date.now() + this.POLL_RETRY_MS;
                this.isMonitoringUsers = false;
            });
        },

        applyUsersHide: function () {
            if (!this.subProfileIdsToHide || this.subProfileIdsToHide.length === 0) return;

            // Find all cards, list items, or elements with data-id or containing useredit links
            const cards = document.querySelectorAll('.card, .listItem, [data-id]');
            cards.forEach(card => {
                let id = card.getAttribute('data-id');
                if (id) {
                    id = this.normalizeGuid(id);
                    if (this.subProfileIdsToHide.includes(id)) {
                        card.style.display = 'none';
                        return;
                    }
                }

                // Check for links inside (e.g. useredit.html?userId=...)
                const links = card.querySelectorAll('a');
                for (let i = 0; i < links.length; i++) {
                    const href = links[i].getAttribute('href') || '';
                    if (href.includes('userId=')) {
                        const match = href.match(/userId=([^&]+)/);
                        if (match) {
                            const normalizedId = this.normalizeGuid(match[1]);
                            if (this.subProfileIdsToHide.includes(normalizedId)) {
                                card.style.display = 'none';
                                break;
                            }
                        }
                    }
                }
            });
        },

        // ── Active-profile session storage ────────────────────────────────────────
        // The active profile lives in sessionStorage deliberately: closing the app should
        // drop back to the picker rather than leave a child profile signed in.
        //
        // Some runtimes throw sessionStorage away on a full reload — and a full reload is
        // exactly how a profile switch finishes. The marker is then gone by the time the
        // page comes back, validateSessionState() concludes the app was closed, and it
        // reverts to the master token. On Samsung's Tizen that looked like the avatar
        // changing and nothing else (issues #15 and #16). On Android it is worse, because
        // the revert reloads: the reload loses the marker again, which reverts again — the
        // picker over and over, which is what an Android reporter saw.
        //
        // That was first fixed by sniffing for Tizen, which meant meeting each new runtime
        // through a bug report. There is no sniff any more. The rule is about WHY
        // sessionStorage is empty, which is the part that actually decides the answer:
        //
        //   empty because the app was closed     -> show the picker, drop the profile
        //   empty because our own reload lost it -> put it back, keep the profile
        //
        // and those two are told apart by the switching flag, which is set immediately
        // before every reload this plugin performs and at no other time.
        //
        // Values are also held in memory for the life of the page, so a runtime where
        // sessionStorage does not work at all still behaves while the page lives.
        SESSION_MIRROR_PREFIX: 'jpf-persist-',

        /// How long after our own reload the mirror is still believable. A reload takes
        /// seconds; anything older is a new session wearing a flag nobody cleared.
        SESSION_MIRROR_MAX_AGE_MS: 60000,

        _sessionMemory: {},

        _sessionSet: function (key, value) {
            this._sessionMemory[key] = value;
            try { sessionStorage.setItem(key, value); } catch (e) { /* storage blocked */ }
            try {
                localStorage.setItem(
                    this.SESSION_MIRROR_PREFIX + key,
                    JSON.stringify({ v: value, t: Date.now() }));
            } catch (e) { /* full or blocked — sessionStorage is still the primary */ }
        },

        _sessionGet: function (key) {
            if (Object.prototype.hasOwnProperty.call(this._sessionMemory, key)) {
                return this._sessionMemory[key];
            }

            let value = null;
            try { value = sessionStorage.getItem(key); } catch (e) { /* storage blocked */ }
            if (value !== null) {
                this._sessionMemory[key] = value;
                return value;
            }

            const mirrored = this._readSessionMirror(key);
            if (mirrored === null) return null;

            // Put it back where the rest of the code expects to find it, so this costs one
            // read per page load rather than one per call.
            this._sessionMemory[key] = mirrored;
            try { sessionStorage.setItem(key, mirrored); } catch (e) { /* storage blocked */ }
            return mirrored;
        },

        /// The mirrored value, but only when this page load is the continuation of a reload
        /// we caused, and that reload was recent. Null otherwise — which is what still makes
        /// closing the app drop back to the picker.
        _readSessionMirror: function (key) {
            // The injected head script consumes this key: it runs synchronously during head
            // parsing, while profiles.js is deferred, so by the time anything here looks the
            // key has been gone for the whole document parse. Reading localStorage alone made
            // this function return null every single time, which left the 1.4.6 switch-loop
            // fix dead on arrival — the three-reload backstop was doing all the work.
            //
            // The head script republishes it as a window global for this reason. localStorage
            // stays as the fallback for a page still carrying an older head script, and for
            // any caller reached before the head script has run.
            let switching = false;
            try { switching = !!window.__jpSwitching; } catch (e) { /* no window */ }
            if (!switching) {
                try {
                    switching = !!localStorage.getItem(this.config.switchingKey);
                } catch (e) { return null; }
            }
            if (!switching) return null;

            let raw = null;
            try { raw = localStorage.getItem(this.SESSION_MIRROR_PREFIX + key); } catch (e) { return null; }
            if (raw === null) return null;

            let parsed = null;
            try { parsed = JSON.parse(raw); } catch (e) { /* the older mirror: a bare string */ }

            if (!parsed || typeof parsed !== 'object' || typeof parsed.v !== 'string') {
                // Written by the Tizen-only mirror, which carried no timestamp. Upgrading
                // must not sign anyone out, so the switching flag above is all it gets.
                return raw;
            }

            if (typeof parsed.t !== 'number'
                || Date.now() - parsed.t > this.SESSION_MIRROR_MAX_AGE_MS) {
                return null;
            }
            return parsed.v;
        },

        _sessionRemove: function (key) {
            delete this._sessionMemory[key];
            try { sessionStorage.removeItem(key); } catch (e) { /* storage blocked */ }
            // Always clear the mirror: a stale copy would outlive every sign-out.
            try { localStorage.removeItem(this.SESSION_MIRROR_PREFIX + key); } catch (e) { /* ignore */ }
        },

        /// Drops every trace of an active profile session. Used by sign-out, the login
        /// route and the revert-to-master path, which must all clear both copies.
        clearProfileSession: function () {
            this._sessionRemove(this.config.activeSessionKey);
            this._sessionRemove('jellyfin_profiles_active_info');
        },

        isProfileSessionActive: function () {
            return !!this._sessionGet(this.config.activeSessionKey);
        },

        // ── Emergency disable ──────────────────────────────────────────────────────
        // An escape hatch for when the switcher itself is what is blocking the interface.
        // The administrator sets a long code in the plugin settings; entering it here shuts
        // the plugin down until the server restarts.
        //
        // Note the honest limit: this is script running inside the plugin, so it only helps
        // when the plugin is misbehaving, not when profiles.js fails to load or throws on
        // startup. In that case the answer is restarting Jellyfin or removing the plugin
        // folder, which is what the documentation says.
        //
        // Entry is a deliberate action rather than a keystroke sniffer. Watching everything
        // typed anywhere would burn the server's five-per-hour attempt budget on ordinary
        // typing — a search box would lock the real administrator out of their own escape
        // hatch.

        /// Tears the switcher down and stops every code path that could rebuild it.
        /// Setting the flag matters as much as the teardown: checkRoute runs on a 500 ms
        /// timer, so removing the overlay without it would have the gate back within half a
        /// second — failing in exactly the situation this feature exists for.
        applyPanicDisable: function (persist) {
            this._panicDisabled = true;
            this._releaseOverlayFocusTrap();
            // The cover is waiting for a gate this has just forbidden.
            this._dropCover();

            if (persist) {
                try {
                    localStorage.setItem(this.config.panicKey, String(Date.now()));
                } catch (e) { /* private mode — the in-memory flag still holds for this page */ }
            }

            this.removeProfileOverlay();
            const bubble = document.getElementById('profiles-floating-bubble');
            if (bubble) bubble.remove();
            // The drawer link injectSidebarLink used to add. Kept in the teardown, and
            // only here: a browser that ran an older build in this tab may still have one
            // in the page, and the emergency disable has to leave nothing of ours behind.
            const sidebarLink = document.getElementById('profiles-sidebar-link');
            if (sidebarLink) sidebarLink.remove();
            const menuItem = document.getElementById('profiles-user-menu-item');
            if (menuItem) menuItem.remove();
            const prefsItem = document.getElementById('profiles-preferences-menu-item');
            if (prefsItem) prefsItem.remove();

            this.stopInactivityTimer();
            document.body.classList.remove('profiles-no-scroll');
            document.documentElement.classList.remove('profiles-no-scroll');
            document.documentElement.style.removeProperty('opacity');
            try { localStorage.removeItem(this.config.switchingKey); } catch (e) { /* ignore */ }
        },

        /// On startup, honour a disable from a previous page load, then confirm it with the
        /// server. The flag is cleared once Jellyfin has restarted and the plugin is live
        /// again — the disable is meant to last until a restart, not forever.
        checkPersistedPanic: function () {
            let marked = false;
            try {
                marked = !!localStorage.getItem(this.config.panicKey);
            } catch (e) { /* storage unavailable — nothing to honour */ }
            if (!marked) return;

            // Apply first and ask afterwards: the request may be slow, and a gate that
            // appears for two seconds before vanishing is the behaviour being escaped.
            this.applyPanicDisable(/* persist */ false);

            fetch(ApiClient.getUrl('plugins/profiles/panic-state'), { cache: 'no-store' })
                .then(res => res.ok ? res.json() : Promise.reject(new Error('unavailable')))
                .then(state => {
                    const disabled = (state.disabled !== undefined ? state.disabled : state.Disabled) === true;
                    if (disabled) return;
                    try { localStorage.removeItem(this.config.panicKey); } catch (e) { /* ignore */ }
                    this._panicDisabled = false;
                    // The teardown above released the focus trap. Coming back to life has to
                    // put it back, or issue #16 returns for the rest of this page load.
                    this._bindOverlayFocusTrap();
                    this.checkRoute();
                })
                .catch(() => {
                    // Server unreachable: stay disabled. Erring towards "off" leaves the
                    // interface usable, which is the whole point.
                });
        },

        /// Shows the emergency link only once the server has said a code exists. Unknown
        /// counts as no — the feature is off by default, and advertising an escape hatch
        /// that cannot work is worse than not offering one.
        applyPanicLinkVisibility: function () {
            const link = document.getElementById('profiles-panic-link');
            if (!link) return;
            link.style.display = this._panicLinkAvailable === true ? '' : 'none';
        },

        bindPanicShortcut: function () {
            document.addEventListener('keydown', (e) => {
                // Ctrl+Shift+B. Chosen to be unreachable by accident and not to collide
                // with Jellyfin's own shortcuts. It is the way in when the switcher has
                // failed badly enough that no overlay renders at all.
                if (e.ctrlKey && e.shiftKey && (e.key === 'B' || e.key === 'b')) {
                    e.preventDefault();
                    this.showPanicPrompt();
                }
            });
        },

        showPanicPrompt: function () {
            if (document.getElementById('profiles-panic-dialog')) return;

            const dialog = document.createElement('div');
            dialog.id = 'profiles-panic-dialog';
            dialog.classList.add('jpf-trap-surface');
            dialog.style.cssText = `
                position: fixed; top: 0; left: 0; right: 0; bottom: 0;
                background: rgba(0,0,0,0.85); backdrop-filter: blur(8px);
                display: flex; align-items: center; justify-content: center;
                z-index: ${DIALOG_Z};
            `;

            dialog.innerHTML = `
                <div style="background:var(--jpf-panel); border:1px solid var(--jpf-border-1); border-radius: var(--jpf-r-md);
                            padding:24px; max-width:460px; width:90%; box-shadow:0 10px 30px rgba(0,0,0,0.5);">
                    <h2 style="margin:0 0 12px 0; color:var(--jpf-text-1); font-size:1.2rem; font-weight:700;">${t('panic.emergencyDisable')}</h2>
                    <p style="color:var(--jpf-text-2); font-size:0.9rem; line-height:1.5; margin:0 0 16px 0;">
                        ${t('panic.dialogBody')}
                    </p>
                    <input type="password" id="profiles-panic-input" autocomplete="off" placeholder="${t('panic.emergencyCodePlaceholder')}"
                           style="width:100%; box-sizing:border-box; padding:10px; font-size:1rem; margin-bottom:8px;" />
                    <div id="profiles-panic-error" style="display:none; color:var(--jpf-danger); font-size:0.85rem; font-weight:600; margin-bottom:8px;"></div>
                    <div style="display:flex; gap: var(--jpf-gap); justify-content:flex-end; margin-top:12px;">
                        <button id="profiles-panic-cancel" class="profiles-btn btn-secondary" style="padding:10px 20px; font-weight:600;">${t('common.cancel')}</button>
                        <button id="profiles-panic-submit" class="profiles-btn btn-danger" style="padding:10px 20px; font-weight:600;">${t('panic.disable')}</button>
                    </div>
                </div>
            `;

            document.body.appendChild(dialog);

            const input = dialog.querySelector('#profiles-panic-input');
            const errDiv = dialog.querySelector('#profiles-panic-error');
            const submitBtn = dialog.querySelector('#profiles-panic-submit');
            const close = () => dialog.remove();

            dialog.querySelector('#profiles-panic-cancel').addEventListener('click', close);

            const submit = () => {
                const code = (input.value || '').trim();
                if (!code) return;

                submitBtn.disabled = true;
                submitBtn.textContent = t('panic.checking');
                errDiv.style.display = 'none';

                fetch(ApiClient.getUrl('plugins/profiles/panic'), {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ code: code })
                })
                .then(res => {
                    if (res.ok) {
                        // Tear the switcher down here and now rather than waiting for a
                        // reload: the person doing this is stuck, and telling them to
                        // reload a page they cannot use would not help.
                        close();
                        this.applyPanicDisable(/* persist */ true);
                        this.showAlert(t('panic.disabledTitle'), t('panic.disabledBody'));
                        return;
                    }
                    if (res.status === 429) throw new Error(t('panic.tooManyAttempts'));
                    throw new Error(t('panic.incorrectCode'));
                })
                .catch(err => {
                    errDiv.textContent = err.message || t('panic.incorrectCode');
                    errDiv.style.display = 'block';
                    submitBtn.disabled = false;
                    submitBtn.textContent = t('panic.disable');
                });
            };

            submitBtn.addEventListener('click', submit);
            input.addEventListener('keydown', (e) => {
                if (e.key === 'Enter') { e.preventDefault(); submit(); }
                if (e.key === 'Escape') { e.preventDefault(); close(); }
            });

            setTimeout(() => input.focus(), 50);
        },

        // ── Switcher preferences ───────────────────────────────────────────────────
        // Two independent settings, because they answer different questions:
        //
        //   askOnStartup     — does the "Who's Watching?" screen appear when the client
        //                      loads? (Once per browser session, not per home visit.)
        //   switcherLocation — 'button' (Bonfire's floating header button) or 'menu'
        //                      (a row in Jellyfin's own user menu, no floating button).
        //
        // These were one setting in 1.3.1-beta, which could not express "ask me on startup
        // but put the switcher in Jellyfin's menu" — the combination issue #14 asked for.
        //
        // checkRoute has to decide about the gate long before a network round trip could
        // answer, so both are mirrored into localStorage and read synchronously, with the
        // server refreshing the copy in the background on every load.

        /// The cached preferences, or null when we have not learned them for this account.
        /// Null means "do not raise the gate yet" — a wrong guess that way costs a moment of
        /// home screen, whereas wrongly assuming a gate throws a full-screen overlay at
        /// somebody who deliberately turned it off, on every single load.
        getSwitcherPrefs: function () {
            if (this._switcherPrefs) return this._switcherPrefs;

            try {
                const cached = JSON.parse(localStorage.getItem(this.config.switcherModeKey) || 'null');
                if (!cached) return null;

                // The cache belongs to one account. On a shared browser the next person to
                // sign in must not inherit it, so it only counts when it matches either the
                // signed-in user or the master profile behind the active sub-profile.
                const cachedMaster = this.normalizeGuid(cached.masterUserId);
                const currentUserId = (typeof ApiClient !== 'undefined' && ApiClient)
                    ? this.normalizeGuid(ApiClient.getCurrentUserId()) : '';
                const masterState = this.readMasterState();
                const knownMaster = masterState ? this.normalizeGuid(masterState.masterUserId) : '';
                if (!cachedMaster || (cachedMaster !== currentUserId && cachedMaster !== knownMaster)) return null;

                let prefs;
                if (typeof cached.askOnStartup === 'boolean' && cached.location) {
                    prefs = { askOnStartup: cached.askOnStartup, location: cached.location };
                } else if (cached.mode) {
                    // A cache written by 1.3.1-beta. Expand it rather than discarding it,
                    // so upgrading does not flash a gate at someone who turned it off.
                    prefs = cached.mode === 'native'
                        ? { askOnStartup: false, location: 'menu' }
                        : { askOnStartup: true, location: 'button' };
                } else {
                    return null;
                }

                this._switcherPrefs = prefs;
                return prefs;
            } catch (e) { /* unreadable cache — fall through and refetch */ }

            return null;
        },

        /// True only when we know the startup prompt is wanted. Unknown reads as false so
        /// the gate is never raised on a guess.
        ///
        /// One answer per account, for every device. 1.6.x–1.7.1 let a device keep its own
        /// ("Use the same answer on all my devices"); it was cut in 1.7.2 as one setting
        /// too many for what it bought, and a TV that should always ask is now a household
        /// that always asks.
        shouldAskOnStartup: function () {
            const p = this.getSwitcherPrefs();
            return !!p && p.askOnStartup === true;
        },

        /// True when the switcher belongs in Jellyfin's user menu rather than the floating
        /// button. Unknown reads as false, which keeps the historical button behaviour.
        isMenuLocation: function () {
            const p = this.getSwitcherPrefs();
            return !!p && p.location === 'menu';
        },

        _cacheSwitcherPrefs: function (askOnStartup, location, masterUserId) {
            this._switcherPrefs = { askOnStartup: askOnStartup, location: location };
            try {
                localStorage.setItem(this.config.switcherModeKey, JSON.stringify({
                    askOnStartup: askOnStartup,
                    location: location,
                    masterUserId: masterUserId
                }));
            } catch (e) { /* storage full or blocked — the in-memory copy still works */ }
        },

        // ── Theme ──────────────────────────────────────────────────────────────────
        // One attribute on <html>; the theme's block (themeCss) does the rest. Per profile, so it is cached
        // per account: on a shared browser the next person must not inherit the last one's.

        /// Puts a theme on the page. Unknown ids become Bonfire, so a stale cache or an
        /// older server can never leave the attribute naming a theme this script has no block for
        /// (which would leave the tints resolving to nothing).
        applyTheme: function (theme) {
            const root = document.documentElement;
            if (!root) return;
            // Kept as asked, so a custom theme that arrives after this is put on then.
            this._wantedTheme = String(theme || '').toLowerCase();
            const id = normalizeTheme(theme);
            if (root.getAttribute('data-jpf-theme') !== id) root.setAttribute('data-jpf-theme', id);
        },

        /// The stored themes: { last, users: { normalisedUserId: theme }, bg: { theme: gate
        /// background } }. Injection/WebInjection.cs reads all three before this file loads.
        ///
        /// One entry per account, not one for the device. It was one, and a household
        /// overwrote it every time it moved between profiles: open as the main account,
        /// pick a child's profile, and the child's theme replaced the parent's — so the next
        /// start drew the "Who's watching?" screen, and the cover before it, in Bonfire's
        /// colours instead of the parent's. Reads the 1.6.6.1 development shape too.
        _readThemeCache: function () {
            let raw = null;
            try { raw = JSON.parse(localStorage.getItem(this.config.themeKey) || 'null'); } catch (e) { /* unreadable */ }
            const cache = { last: null, users: {}, bg: {} };
            if (!raw || typeof raw !== 'object') return cache;
            if (raw.bg && typeof raw.bg === 'object') {
                Object.keys(raw.bg).forEach(k => {
                    if (typeof raw.bg[k] === 'string') cache.bg[k] = raw.bg[k];
                });
            }
            if (raw.users && typeof raw.users === 'object') {
                Object.keys(raw.users).forEach(k => {
                    if (typeof raw.users[k] === 'string') cache.users[k] = raw.users[k];
                });
            } else if (raw.userId && typeof raw.theme === 'string') {
                cache.users[this.normalizeGuid(raw.userId)] = raw.theme;
            }
            cache.last = typeof raw.last === 'string' ? raw.last : (typeof raw.theme === 'string' ? raw.theme : null);
            return cache;
        },

        /// The theme stored for whoever is signed in, applied before the server answers.
        /// Before anyone is signed in it applies the last one stored on this device, which is
        /// the person most likely to be signing in.
        applyCachedTheme: function () {
            const cache = this._readThemeCache();

            let current = '';
            try {
                if (typeof ApiClient !== 'undefined' && ApiClient) {
                    current = this.normalizeGuid(ApiClient.getCurrentUserId());
                }
            } catch (e) { /* no client yet */ }

            const theme = current ? cache.users[current] : cache.last;
            if (theme) this.applyTheme(theme);
        },

        /// Records and applies what the server said, from loadSwitcherPrefs.
        _setThemeSettings: function (prefs) {
            const read = (name) => prefs[name] !== undefined
                ? prefs[name]
                : prefs[name.charAt(0).toUpperCase() + name.slice(1)];

            // A server older than this build sends none of these. Leave the page as it is
            // and offer no picker, rather than pretend the server knows about themes.
            if (read('theme') === undefined) return;
            // As sent, for useCustomThemes: a custom theme named here before its file has
            // arrived normalises to Bonfire below, and is read again once it has.
            this._lastThemePrefs = prefs;

            const settings = {
                theme: normalizeTheme(read('theme')),
                defaultTheme: normalizeTheme(read('defaultTheme')),
                allow: read('allowProfileThemes') !== false
            };
            this._themeSettings = settings;
            this.applyTheme(settings.theme);
            this.injectFonts();

            try {
                const userId = (typeof ApiClient !== 'undefined' && ApiClient) ? this.normalizeGuid(ApiClient.getCurrentUserId()) : '';
                if (userId) {
                    const cache = this._readThemeCache();
                    // Re-inserted so the most recent is last, and capped: a browser that
                    // has seen many accounts keeps only the most recent twenty.
                    delete cache.users[userId];
                    cache.users[userId] = settings.theme;
                    const ids = Object.keys(cache.users);
                    ids.slice(0, Math.max(0, ids.length - 20)).forEach(k => { delete cache.users[k]; });
                    cache.last = settings.theme;
                    // The gate's background for each theme still named here, for the head
                    // script's startup cover, which runs before this file has loaded and so
                    // cannot read a theme. It used to carry a copy of every built-in's
                    // background; a theme an administrator makes was never in that copy.
                    const bg = gateBackground(settings.theme);
                    if (bg) cache.bg[settings.theme] = bg;
                    Object.keys(cache.bg).forEach(k => {
                        if (k !== cache.last && !Object.keys(cache.users).some(u => cache.users[u] === k)) delete cache.bg[k];
                    });
                    localStorage.setItem(this.config.themeKey, JSON.stringify(cache));
                }
            } catch (e) { /* storage full or blocked — it still applies for this page */ }
        },

        /// The theme picker for the profile forms, or '' when the server does not let
        /// profiles choose (or has not said). `stored` is the profile's own choice, null for
        /// "follow the server default".
        renderThemePicker: function (prefix, stored) {
            const settings = this._themeSettings;
            if (!settings || !settings.allow) return '';

            const chosen = offeredChoice(stored);
            const shown = chosen || settings.defaultTheme;

            return `
                <div class="form-group" id="${prefix}-theme-group">
                    <label for="${prefix}-theme-select">${t('profileForm.theme')}</label>
                    <select id="${prefix}-theme-select">
                        <option value="" ${chosen ? '' : 'selected'}>${t('profileForm.themeDefault', { name: themeName(settings.defaultTheme) })}</option>
                        ${offeredThemes().map(id => `<option value="${id}" ${chosen === id ? 'selected' : ''}>${themeName(id)}</option>`).join('')}
                    </select>
                    ${this.renderThemePreview(shown, prefix + '-theme-preview')}
                    <div class="form-hint">${t('profileForm.themeHint')}</div>
                </div>
            `;
        },

        /// A small "Who's watching?" wearing one theme: its name as the title, three tiles
        /// with the first in the theme's focus frame, and a button. Drawn entirely from the
        /// theme's tokens through its own data-jpf-theme, so it is what choosing it does.
        renderThemePreview: function (theme, id) {
            const shown = normalizeTheme(theme);
            return `
                <div class="jpf-theme-preview" ${id ? `id="${id}"` : ''} data-jpf-theme="${shown}" aria-hidden="true">
                    <span class="jpf-theme-preview-name">${themeName(shown)}</span>
                    <span class="jpf-theme-preview-tiles">
                        <span class="jpf-theme-preview-tile"></span><span class="jpf-theme-preview-tile"></span><span class="jpf-theme-preview-tile"></span>
                    </span>
                    <span class="jpf-theme-preview-button">${t('gate.manageProfiles')}</span>
                </div>
            `;
        },

        /// Keeps the preview in step with the select. Safe to call when there is no picker.
        bindThemePicker: function (prefix) {
            const select = document.getElementById(prefix + '-theme-select');
            const preview = document.getElementById(prefix + '-theme-preview');
            if (!select || !preview) return;
            select.addEventListener('change', () => {
                const settings = this._themeSettings;
                const id = select.value ? normalizeTheme(select.value)
                    : (settings ? settings.defaultTheme : 'bonfire');
                preview.setAttribute('data-jpf-theme', id);
                const name = preview.querySelector('.jpf-theme-preview-name');
                if (name) name.textContent = themeNameText(id);
            });
        },

        /// What the form sends: the select's value ('' = server default), or undefined
        /// when there is no picker, which the server reads as "leave it alone".
        readThemePicker: function (prefix) {
            const select = document.getElementById(prefix + '-theme-select');
            return select ? (select.value ? normalizeTheme(select.value) : '') : undefined;
        },

        /// How often coming back to the app may re-read the preferences. A television
        /// woken from standby, a phone unlocked: once a minute is plenty, and it keeps a
        /// tab being flicked between from asking every time.
        SETTINGS_REFRESH_MS: 60000,

        /// Picks up a changed theme, or any other preference, without a reload.
        ///
        /// The theme used to arrive once per page load, so an administrator changing the
        /// server default, or anyone changing a profile's theme on another device, saw
        /// nothing until the app was restarted. Three signals now re-read it:
        ///   - 'jpf-settings-changed' on this window, which the admin page dispatches after
        ///     a save — it runs inside jellyfin-web, in the same window as this script;
        ///   - a write to SETTINGS_EPOCH_KEY from another tab, which the admin page also
        ///     makes, and which arrives here as a 'storage' event;
        ///   - the page becoming visible again, at most once a minute.
        /// None of these is on the route poll, which must not grow (see checkRoute).
        _bindSettingsRefresh: function () {
            if (this._settingsRefreshBound || typeof window === 'undefined' || !window.addEventListener) return;
            this._settingsRefreshBound = true;
            const refresh = () => {
                this._lastSettingsRefresh = Date.now();
                this.loadSwitcherPrefs();
            };
            window.addEventListener('jpf-settings-changed', refresh);
            window.addEventListener('storage', (e) => {
                if (e && e.key === 'jellyfin_profiles_settings_epoch') refresh();
            });
            document.addEventListener('visibilitychange', () => {
                if (document.visibilityState !== 'visible') return;
                if (Date.now() - (this._lastSettingsRefresh || 0) < this.SETTINGS_REFRESH_MS) return;
                refresh();
            });
        },

        /// Refreshes the cached preferences from the server. Called once per page load;
        /// re-runs the route check afterwards so a first-ever load on a new device settles
        /// into the right behaviour without the user having to navigate.
        loadSwitcherPrefs: function () {
            if (this._switcherPrefsLoading) return;
            if (typeof ApiClient === 'undefined' || !ApiClient || !ApiClient.accessToken()) return;

            this._switcherPrefsLoading = true;
            fetch(ApiClient.getUrl('plugins/profiles/preferences'), {
                cache: 'no-store',
                headers: this.getAuthHeaders(ApiClient.accessToken())
            })
            .then(res => res.ok ? res.json() : Promise.reject(new Error('preferences unavailable')))
            .then(prefs => {
                const askRaw = prefs.askOnStartup !== undefined ? prefs.askOnStartup : prefs.AskOnStartup;
                const locRaw = prefs.switcherLocation || prefs.SwitcherLocation;
                const master = prefs.masterUserId || prefs.MasterUserId;

                // A server older than this build answers with switcherMode only.
                const legacyNative = (prefs.switcherMode || prefs.SwitcherMode) === 'native';
                const ask = typeof askRaw === 'boolean' ? askRaw : !legacyNative;
                const location = locRaw === 'menu' || locRaw === 'button'
                    ? locRaw
                    : (legacyNative ? 'menu' : 'button');

                const emergency = (prefs.emergencyCodeConfigured !== undefined
                    ? prefs.emergencyCodeConfigured
                    : prefs.EmergencyCodeConfigured) === true;

                const before = this._switcherPrefs;
                const changed = !before || before.askOnStartup !== ask || before.location !== location;
                this._cacheSwitcherPrefs(ask, location, master);
                this._panicLinkAvailable = emergency;
                this._setThemeSettings(prefs);
                // First-time setup: asked for only when the server says it is due.
                if ((prefs.onboardingPending !== undefined ? prefs.onboardingPending : prefs.OnboardingPending) === true) {
                    this.maybeStartOnboarding();
                }
                // The gate may already be on screen — reveal or hide the link in place
                // rather than waiting for the next load.
                this.applyPanicLinkVisibility();
                this._switcherPrefsLoading = false;
                // Only re-check when the answer actually moved; checkRoute runs on a timer
                // anyway and this avoids a redundant pass on every load.
                if (changed) this.checkRoute();
            })
            .catch(() => {
                this._switcherPrefsLoading = false;
                // Server unreachable or an older plugin build: fall back to the historical
                // behaviour rather than leaving the gate permanently suppressed.
                if (!this._switcherPrefs) this._switcherPrefs = { askOnStartup: true, location: 'button' };
                // Settle this too. Leaving it unknown would have the route check re-request
                // on every poll, and an emergency link we cannot vouch for stays hidden.
                if (this._panicLinkAvailable === null) this._panicLinkAvailable = false;
            });
        },

        getCachedActiveProfile: function () {
            const activeInfoStr = this._sessionGet('jellyfin_profiles_active_info');
            if (activeInfoStr) {
                try {
                    const info = JSON.parse(activeInfoStr);
                    if (info && info.initial && info.color) return info;
                } catch (e) {}
            }

            // Fallback: search in localStorage cached profiles
            const currentUserId = ApiClient.getCurrentUserId();
            if (currentUserId) {
                try {
                    const cachedListStr = localStorage.getItem('jellyfin_profiles_cached_list');
                    if (cachedListStr) {
                        const profiles = JSON.parse(cachedListStr);
                        if (Array.isArray(profiles)) {
                            const profile = profiles.find(p => this.normalizeGuid(p.profileUserId) === this.normalizeGuid(currentUserId));
                            if (profile) {
                                const info = {
                                    name: profile.profileName,
                                    color: profile.avatarColor || DEFAULT_AVATAR_COLOR,
                                    initial: profile.avatarInitial || (profile.profileName ? profile.profileName.charAt(0).toUpperCase() : 'P'),
                                    profileImage: profile.profileImage || null,
                                    transparent: !!profile.transparentAvatar
                                };
                                // Store it in sessionStorage for future fast access
                                this._sessionSet('jellyfin_profiles_active_info', JSON.stringify(info));
                                return info;
                            }
                        }
                    }
                } catch (e) {
                    console.error("ProfilesPlugin: Failed to read from profiles cache:", e);
                }
            }

            // Ultimate fallback (e.g. before any profile list has been loaded)
            const apiClient = ApiClient;
            let fallbackName = 'Profiles';
            let fallbackInitial = 'P';
            if (apiClient) {
                const user = apiClient._currentUser || apiClient.currentUser;
                if (user && user.Name) {
                    fallbackName = user.Name;
                    fallbackInitial = user.Name.charAt(0).toUpperCase();
                }
            }
            return {
                name: fallbackName,
                color: DEFAULT_AVATAR_COLOR,
                initial: fallbackInitial,
                profileImage: null
            };
        },

        validateSessionState: function () {
            const apiClient = ApiClient;
            if (!apiClient) return;

            const hash = window.location.hash || '';
            const path = window.location.pathname || '';
            if (hash.includes('login') || hash.includes('selectserver') || path.includes('login') || path.includes('selectserver')) {
                localStorage.removeItem(this.config.masterStorageKey);
                this.clearProfileSession();
                return;
            }

            const currentToken = apiClient.accessToken();
            if (!currentToken) {
                // If ApiClient token is empty, check if we are truly logged out of Jellyfin.
                // During initial page load, ApiClient.accessToken() is temporarily empty
                // while the web app initializes, but 'jellyfin_credentials' in localStorage
                // still contains the active session token.
                let isLoggedOut = true;
                try {
                    const credsStr = localStorage.getItem('jellyfin_credentials');
                    if (credsStr) {
                        const creds = JSON.parse(credsStr);
                        if (creds && Array.isArray(creds.Servers) && creds.Servers.some(s => !!s.AccessToken)) {
                            isLoggedOut = false;
                        }
                    }
                } catch (e) {}

                if (isLoggedOut) {
                    localStorage.removeItem(this.config.masterStorageKey);
                    this.clearProfileSession();
                }
                return;
            }

            // Reading the state also retires a master token an earlier version left in this
            // browser, ending that session on the server; see readMasterState.
            //
            // This used to go on to a second step. If the app had been closed while a profile
            // was in use, it put the stored master token back and reloaded, so the gate came
            // up signed in as the master. That step was the reason the master token lived in
            // the browser at all, and it meant closing a tab left the least restricted account
            // in the house signed in behind a picture of a picker. It also needed a backstop
            // against reloading forever (_revertReloadAllowed), which went with it.
            //
            // Now the session in use stays in use. checkRoute still raises the gate over it
            // when the household asks on startup, because the active-profile marker is gone
            // with the tab — and every way out of the gate, the master included, is a /switch
            // the server checks.
            this.readMasterState();
        },

        handleSessionExpired: function () {
            console.warn("ProfilesPlugin: Master session expired or invalid. Redirecting to login.");
            localStorage.removeItem(this.config.masterStorageKey);
            this.clearProfileSession();
            
            const apiClient = ApiClient;
            if (apiClient) {
                if (typeof apiClient.clearUser === 'function') {
                    apiClient.clearUser();
                } else if (typeof apiClient.logout === 'function') {
                    apiClient.logout();
                }
            }
            
            window.location.hash = '#/login';
            window.location.reload();
        },

        interceptHomeAndShowProfiles: function () {
            const apiClient = ApiClient;
            if (!apiClient) return;

            const currentUserId = apiClient.getCurrentUserId();
            const token = apiClient.accessToken();

            // Not a reason to drop the cover. During startup jellyfin-web sits on /web/ —
            // which is home as far as isHomeRoute can tell — before it knows who is signed
            // in, and the route poll asks again next tick. Taking the cover down here put
            // the flash straight back. A sign-in page is not home, so checkRoute drops it
            // there; anything else is the backstop's.
            if (!currentUserId || !token) return;

            this._holdCoverForGate();

            // Listed as whoever is signed in. The household's master is learned from the list
            // (fetchAndRenderProfiles) rather than assumed to be the current user, which it is
            // not when a profile was signed into directly — a profile's PIN works on the web
            // sign-in page too once other apps' sign-in is on.
            const known = this.readMasterState();
            this.fetchAndRenderProfiles(apiClient, known ? known.masterUserId : currentUserId, token);
        },

        /// Every request for the profile list goes through here, so the route poll can see
        /// one already out and a recent failure before it sends another.
        ///
        /// Two poll paths ask for the list — the gate (interceptHomeAndShowProfiles) and
        /// the button's prefetch — and neither used to know about a request in flight or a
        /// failure. The gate path asked on every tick until the gate was drawn, so a slow
        /// server collected a pile of identical requests and a down one got a request every
        /// 500 ms; the prefetch had a pending flag but cleared it on failure with no pause.
        /// Only the poll consults this (_listPollMayRequest): a person who saves a profile
        /// or goes back to the grid still gets the request they asked for.
        _requestList: function (apiClient, token, init) {
            if (this._listInFlight === 0) this._listSentAt = Date.now();
            this._listInFlight++;
            const done = () => { this._listInFlight = Math.max(0, this._listInFlight - 1); };
            return fetch(apiClient.getUrl('plugins/profiles/list'),
                         Object.assign({ headers: this.getAuthHeaders(token) }, init || {}))
                .then(res => {
                    done();
                    this._listRetryAt = res.ok ? 0 : Date.now() + this.POLL_RETRY_MS;
                    return res;
                }, err => {
                    done();
                    this._listRetryAt = Date.now() + this.POLL_RETRY_MS;
                    throw err;
                });
        },

        /// True when one is already out. A request that has not answered within
        /// POLL_RETRY_MS is treated as lost: fetch has no timeout of its own, and a hung
        /// connection would otherwise keep the gate from ever being asked for again.
        _listPending: function () {
            return this._listInFlight > 0 && Date.now() - this._listSentAt < this.POLL_RETRY_MS;
        },

        /// Whether the route poll may send a /list request now.
        _listPollMayRequest: function () {
            return !this._listPending() && Date.now() >= this._listRetryAt;
        },

        /// Drops the prefetch cache so the next render is guaranteed to hit the server.
        /// Must be called after anything that changes profile state — otherwise the render
        /// that follows a save shows the pre-save snapshot.
        invalidateProfileCache: function () {
            this.cachedProfiles = [];
            this._profilePrefetchPending = false;
        },

        /// Renders the profile grid.
        ///
        /// `forceRefresh` bypasses the prefetch cache and is REQUIRED after any mutation
        /// (create / edit / delete). The cache exists only to make opening the switcher from
        /// the home screen flash-free; serving it after a save showed stale data — a freshly
        /// saved PIN still read as "No PIN" until the page was reloaded.
        fetchAndRenderProfiles: function (apiClient, masterUserId, masterToken, forceRefresh) {
            // Recorded, not claimed: several callers have already taken a ticket and are
            // finishing by coming back to the grid, and claiming a new one here would
            // invalidate theirs. This only needs to know whether the screen moved on while
            // the list was in flight — Settings, or a profile form, drawn over by a slow
            // grid was the same race the tickets were added for.
            const ticket = this._navTicket;

            if (forceRefresh) {
                this.invalidateProfileCache();
            } else if (this.cachedProfiles && this.cachedProfiles.length) {
                // Consume the prefetch exactly once, then drop it.
                const profiles = this.cachedProfiles;
                this.invalidateProfileCache();
                (this._i18nReady || Promise.resolve()).then(() => this.showProfileOverlay(profiles));
                return;
            }

            this._requestList(apiClient, masterToken, {
                // Never let a conditional/heuristic cache answer this — it drives the
                // PIN and library state shown in the editor.
                cache: 'no-store'
            })
            .then(res => {
                if (res.status === 401) {
                    this.handleSessionExpired();
                    throw new Error("Unauthorized");
                }
                // A 500 used to go on to res.json() and be drawn as whatever its body
                // parsed to. It is a failure like any other.
                if (!res.ok) throw new Error('Profile list unavailable (' + res.status + ')');
                return res.json();
            })
            .then(profiles => {
                // Not a reason to drop the startup cover: a stale ticket is usually a list
                // that another request has already drawn the gate from (a user action
                // while the poll's request was out — the poll itself no longer sends a
                // second, see _requestList), or one with the gate still on its way.
                // Dropping here showed home through the gate's fade. A cover nothing
                // replaces is the backstop's.
                if (!this.navIsCurrent(ticket)) return;
                const normalized = this.normalizeProfiles(profiles);
                // The household's own master: the master tile that is not a linked Bonfire.
                // Only its id is kept — see readMasterState.
                const ownMaster = normalized.find(p => p.isMaster && !p.isBonfire);
                if (ownMaster) this.writeMasterState(ownMaster.profileUserId);
                // Deliberately NOT stored in cachedProfiles: that field is the one-shot
                // prefetch buffer, and repopulating it here made the *next* call short-circuit
                // to data that was already stale.
                localStorage.setItem('jellyfin_profiles_cached_list', JSON.stringify(normalized));
                return (this._i18nReady || Promise.resolve()).then(() => this.showProfileOverlay(normalized));
            })
            .catch(err => {
                console.error("Failed to load sub-profiles:", err);
                // No gate is coming from this attempt. The poll will try again after
                // POLL_RETRY_MS and the home screen shows in between — which is the honest
                // state of things. _requestList has already set the pause for a failed
                // request; this covers a body that would not parse or a grid that would not
                // draw, which retrying twice a second would not fix either.
                this._listRetryAt = Date.now() + this.POLL_RETRY_MS;
                this._dropCover();
                // The saved master is NOT forgotten here any more. It was, on every failure
                // — a network blip, a 500, a restart — which cost the household its master
                // for no reason. Only a 401 says the session is gone, and that path is
                // handleSessionExpired's, which clears it itself.
            });
        },

        showProfileOverlay: function (profiles) {
            // Always stop the inactivity timer when showing the profile selector
            this.stopInactivityTimer();

            // Warm the account-wide form data now, while the user is still reading the
            // grid. Opening a profile then costs one request rather than five, which is
            // the difference between instant and the several-second wait that made
            // people click a second time.
            try {
                // Only for the master: the forms are the master's, and a profile's own
                // session would fetch the profile's libraries rather than the household's.
                const warmState = this.readMasterState();
                if (warmState && warmState.token && this.isSignedInAsMaster(warmState)) {
                    this.fetchSharedFormData(ApiClient, warmState).catch(() => {
                        // A cold prefetch failing is not an error anyone needs to see;
                        // the form will retry and report it properly if it still fails.
                    });
                }
            } catch (e) { /* no master state yet — the form will fetch on demand */ }
            this._overlayMountTime = Date.now();

            // Returning to the grid replaces the modal contents, so any document-level
            // listeners the previous form registered are now bound to detached nodes.
            this.clearManagedDocumentListeners();

            const skinHeader = document.querySelector('.skinHeader');
            if (skinHeader) skinHeader.style.display = 'none';

            // Do NOT apply filter:blur to #view-home — it triggers a GPU compositing
            // layer creation which causes a one-frame white flash on first paint.
            // The overlay's solid-dark background makes the blur redundant anyway.

            let overlay = document.getElementById('profiles-gate-overlay');
            if (!overlay) {
                overlay = document.createElement('div');
                overlay.id = 'profiles-gate-overlay';
                // Start at opacity 0 so the browser creates the compositing layer
                // silently.  We fade it in via rAF once it exists in the DOM.
                overlay.style.opacity = '0';
                document.body.appendChild(overlay);
            }

            // Disable scrolling
            document.body.classList.add('profiles-no-scroll');
            document.documentElement.classList.add('profiles-no-scroll');

            // Keep a stable reference to the profiles being shown so that
            // back-navigation (PIN cancel, master PIN cancel, promptMasterPinEntry)
            // can re-show the grid without depending on cachedProfiles, which is
            // intentionally cleared after first use.
            this.currentProfiles = profiles;
            this.renderOverlayContent(overlay, profiles);
            // First-time setup owns the overlay while it runs: the grid it was opened
            // with, or one drawn late by the gate, is replaced before it is painted.
            if (this._onboarding) {
                this._onboarding.waiting = false;
                this.showOnboardingStep();
            }

            // Reveal the page NOW — the overlay covers the home screen so there
            // is no blank-page flash.  checkRoute() skipped _revealPage() earlier
            // specifically so we could do it here at the right moment.
            this._viewShowFired = true; // overlay is rendered; treat this as view-ready
            this._revealPage();

            // Two rAF calls: first lets the browser paint with opacity:0 (compositing
            // layer created silently), second begins the CSS opacity transition.
            requestAnimationFrame(() => requestAnimationFrame(() => {
                overlay.style.opacity = ''; // CSS transition takes over
                // The startup cover comes down once the fade has finished and the gate is
                // opaque — dropping it at the start of the fade showed home through a
                // half-transparent gate, which is the flash again. The cover is the same
                // colour as the gate, so the fade itself is not lost.
                if (this._coverUp) {
                    this._coverHeldForGate = true;
                    setTimeout(() => this._dropCover(), this.GATE_FADE_MS);
                }
            }));

            // Auto-focus the first interactive element so TV/keyboard users
            // don't need to Tab before they can navigate the profile grid.
            setTimeout(() => {
                const first = overlay.querySelector('[tabindex="0"], button, input');
                if (first) first.focus();
            }, 100);
        },

        /// Builds the per-library artwork controls in the edit form and returns a handle
        /// whose save() posts whatever changed.
        ///
        /// Choices are held in memory until the form is saved, so backing out leaves
        /// nothing behind — the same contract as every other field on the form.
        /// Greys the artwork controls on a library this profile cannot see.
        ///
        /// Now that the tick and the artwork sit on one row, an enabled Picture
        /// dropdown next to an unticked library would be offering to style something
        /// that is not there.
        syncLibraryRowState: function (row) {
            if (!row) return;
            const box = row.querySelector('.library-checkbox');
            const on = !box || box.checked;

            row.querySelectorAll('.libart-mode, .libart-choose').forEach(el => {
                el.disabled = !on;
            });
            row.style.opacity = on ? '' : '0.5';
        },

        /// Applies the above to every row in a container.
        syncAllLibraryRows: function (root) {
            const scope = root || document;
            scope.querySelectorAll('.libart-row').forEach(r => this.syncLibraryRowState(r));
        },

        initLibraryArtworkEditor: function (container, profileId) {
            const rows = Array.prototype.slice.call(container.querySelectorAll(".libart-row"));
            const list = container.querySelector(".libart-list");
            const artToggle = container.querySelector(".libart-toggle input");
            const artHint = container.querySelector(".libart-explainer");

            // Off is the honest default: most profiles never touch artwork, and the
            // controls meant nothing to anyone who was only picking libraries.
            const showArtwork = (on) => {
                if (list) list.classList.toggle("show-artwork", on);
                if (artHint) artHint.style.display = on ? "" : "none";
            };
            if (artToggle) {
                artToggle.addEventListener("change", (e) => showArtwork(e.target.checked));
            }
            showArtwork(false);
            const state = {};
            const masterState = this.readMasterState();

            // A value safe to drop into a CSS url(): a data payload or our own endpoint.
            // Anything else previews as empty rather than as a broken rule.
            const previewUrl = (entry) => {
                if (entry.image && /^data:image\/(png|jpeg|jpg|gif|webp);base64,[A-Za-z0-9+/=]+$/i.test(entry.image)) {
                    return entry.image;
                }
                return entry.url ? this._libraryArtUrl(entry.url) : "";
            };

            const paint = (row) => {
                const id = row.getAttribute("data-lib");
                const entry = state[id] || { mode: "inherit" };
                const select = row.querySelector(".libart-mode");
                const choose = row.querySelector(".libart-choose");
                const thumb = row.querySelector(".libart-thumb");

                select.value = entry.mode;
                choose.style.visibility = entry.mode === "custom" ? "visible" : "hidden";
                // Class, not an inline style: the artwork switch hides these by rule, and an
                // inline display would outrank it and put the placeholder back.
                row.classList.toggle("libart-has-art", entry.mode !== "inherit");

                const preview = entry.mode === "custom" ? previewUrl(entry) : "";
                thumb.style.backgroundImage = preview ? ("url(\"" + preview + "\")") : "";
                thumb.textContent = (!preview && entry.mode === "none") ? "\u2014" : "";
            };

            rows.forEach(row => {
                const id = row.getAttribute("data-lib");
                state[id] = { mode: "inherit" };

                row.querySelector(".libart-mode").addEventListener("change", (e) => {
                    state[id].mode = e.target.value;
                    state[id].dirty = true;
                    paint(row);
                    // Choosing Picture with nothing picked yet opens the picker, rather
                    // than leaving a mode the server would refuse to save.
                    if (state[id].mode === "custom" && !state[id].image && !state[id].url) {
                        row.querySelector(".libart-choose").click();
                    }
                });

                row.querySelector(".libart-choose").addEventListener("click", () => {
                    this.pickLibraryArtwork((picked) => {
                        state[id].mode = "custom";
                        state[id].image = picked.image;
                        state[id].thumb = picked.thumb;
                        state[id].avatarLibraryId = picked.libraryId;
                        state[id].url = null;
                        state[id].dirty = true;
                        paint(row);
                    });
                });

                paint(row);
            });

            // Existing choices arrive after the form is drawn; the rows work meanwhile.
            if (masterState && masterState.token && rows.length) {
                fetch(ApiClient.getUrl("plugins/profiles/library-artwork/" + profileId), {
                    cache: "no-store",
                    headers: this.getAuthHeaders(masterState.token)
                })
                .then(res => res.ok ? res.json() : Promise.reject(new Error("unavailable")))
                .then(entries => {
                    (entries || []).forEach(entry => {
                        const wanted = this.normalizeGuid(entry.libraryId || entry.LibraryId);
                        const row = rows.find(r => this.normalizeGuid(r.getAttribute("data-lib")) === wanted);
                        if (!row) return;
                        const key = row.getAttribute("data-lib");
                        // Anything already changed by hand outranks what the server had.
                        if (state[key].dirty) return;
                        state[key] = {
                            mode: String(entry.mode || entry.Mode || "inherit").toLowerCase(),
                            url: entry.url || entry.Url || null
                        };
                        paint(row);
                    });

                    // A profile that already has artwork set should not have to find the
                    // switch to see it. Only ever turns the section on, so it cannot fold
                    // away controls somebody has just opened by hand.
                    const anyArt = rows.some(r => (state[r.getAttribute("data-lib")] || {}).mode !== "inherit");
                    if (anyArt && artToggle && !artToggle.checked) {
                        artToggle.checked = true;
                        showArtwork(true);
                    }
                })
                .catch(() => { /* nothing stored yet, or a server older than this build */ });
            }

            return {
                save: (targetProfileId) => {
                    const changed = rows.filter(r => state[r.getAttribute("data-lib")].dirty);
                    if (!changed.length) return Promise.resolve();

                    // One at a time: each request can carry an image and the server writes
                    // files, so firing them together buys nothing and risks a partial save.
                    return changed.reduce((chain, row) => chain.then(() => {
                        const id = row.getAttribute("data-lib");
                        const entry = state[id];
                        return fetch(ApiClient.getUrl("plugins/profiles/library-artwork"), {
                            method: "POST",
                            headers: this.jsonHeaders(masterState ? masterState.token : ApiClient.accessToken()),
                            body: JSON.stringify({
                                profileId: targetProfileId,
                                libraryId: id,
                                mode: entry.mode,
                                image: entry.image || null,
                                thumb: entry.thumb || null,
                                avatarLibraryId: entry.avatarLibraryId || null,
                                masterPin: this.masterPin
                            })
                        }).then(res => {
                            if (!res.ok) return res.text().then(body => { throw new Error(body || t('errors.couldNotSaveArtwork')); });
                            entry.dirty = false;
                        });
                    }), Promise.resolve());
                }
            };
        },

        /// Opens the picker and crop editor the profile pictures already use, so library
        /// artwork gets the avatar library, uploads and positioning without a second
        /// implementation of any of it.
        pickLibraryArtwork: function (onPicked) {
            const apiClient = ApiClient;
            const masterState = this.readMasterState();
            const token = masterState ? masterState.token : apiClient.accessToken();

            this.fetchAvatarLibrary(apiClient, token).then(library => {
                if (document.getElementById("profiles-libart-dialog")) return;

                const dialog = document.createElement("div");
                dialog.id = "profiles-libart-dialog";
                dialog.classList.add("jpf-trap-surface");
                dialog.style.cssText =
                    "position:fixed; top:0; left:0; right:0; bottom:0;" +
                    "background:rgba(0,0,0,0.85); backdrop-filter:blur(8px);" +
                    "display:flex; align-items:center; justify-content:center; z-index:" + DIALOG_Z + ";";
                dialog.innerHTML =
                    '<div style="background:var(--jpf-panel); border:1px solid var(--jpf-border-1); border-radius: var(--jpf-r-md);' +
                    ' padding:22px; max-width:420px; width:94%; max-height:86vh; overflow:auto;">' +
                    '<h2 style="margin:0 0 14px 0; color:var(--jpf-text-1); font-size:1.15rem; font-weight:700;">' + t('avatar.libraryArtworkTitle') + '</h2>' +
                    '<div id="profiles-libart-host"></div>' +
                    '<div id="profiles-libart-error" style="display:none; color:var(--jpf-danger); font-size:0.85rem;' +
                    ' font-weight:600; margin-top:8px;"></div>' +
                    '<div style="display:flex; gap: var(--jpf-gap); justify-content:flex-end; margin-top:16px;">' +
                    '<button id="profiles-libart-cancel" class="profiles-btn btn-secondary" style="padding:10px 20px; font-weight:600;">' + t('common.cancel') + '</button>' +
                    '<button id="profiles-libart-save" class="profiles-btn btn-primary" style="padding:10px 20px; font-weight:600;">' + t('avatar.usePicture') + '</button>' +
                    '</div></div>';
                document.body.appendChild(dialog);

                const host = dialog.querySelector("#profiles-libart-host");
                host.innerHTML = this.renderAvatarPicker("libart", library, null, DEFAULT_AVATAR_COLOR);
                const picker = this.initAvatarPicker(host, "libart", library, null, () => {});

                const close = () => dialog.remove();
                dialog.querySelector("#profiles-libart-cancel").addEventListener("click", close);
                dialog.querySelector("#profiles-libart-save").addEventListener("click", () => {
                    const picked = picker.get();
                    if (!picked.image && !picked.libraryId) {
                        const err = dialog.querySelector("#profiles-libart-error");
                        err.textContent = t('avatar.chooseFirst');
                        err.style.display = "block";
                        return;
                    }
                    close();
                    onPicked(picked);
                });

                setTimeout(() => {
                    const first = dialog.querySelector("button, input");
                    if (first) first.focus();
                }, 50);
            });
        },

        // ─── Library tile artwork (issue #19) ──────────────────────────────────────
        //
        // Jellyfin builds one image per library and caches it on the folder, from a query
        // with no user attached, so the tile for a mixed library can be drawn from something
        // the active profile is not allowed to open. There is no per-user image to ask the
        // server for, so the substitution happens here.
        //
        // It is done with a stylesheet rule per library rather than by touching the cards.
        // jellyfin-web renders `.card[data-id]` containing a `.cardImageContainer` and fills
        // its background in later from `data-src`, as a plain inline style. A stylesheet rule
        // marked !important outranks a non-important inline one, so ours wins without having
        // to race the lazy loader, survives every re-render with no observer, and applies
        // anywhere a card for that library appears.
        //
        // Honest limit: jellyfin-web preloads the original through `new Image()` before it
        // sets the style, so the file is still fetched even though it is never displayed.
        // Stopping that would mean rewriting `data-src` before the loader reads it, which is
        // the fragile DOM race this approach exists to avoid. Nobody sees the picture; it is
        // not a claim that the bytes never reach the device.

        LIBRARY_ART_STYLE_ID: 'profiles-library-art',

        /// Accepts only the shape our own endpoint produces. The value is interpolated into
        /// a stylesheet, where escapeHtml would be the wrong tool and a stray quote or
        /// parenthesis would end the rule early.
        _libraryArtUrl: function (value) {
            if (!value) return '';
            const raw = String(value).trim();
            if (!/^\/plugins\/profiles\/library-art\/[0-9a-f-]{36}\/[0-9a-f-]{36}(\?v=\d+)?$/i.test(raw)) return '';
            return pluginUrl(raw);
        },

        /// Writes the rules for `entries` into a single style element, replacing whatever was
        /// there. Entries are `{ libraryId, mode, url }`; anything unrecognised is skipped.
        applyLibraryArtwork: function (entries) {
            let css = '';

            (entries || []).forEach(entry => {
                // Jellyfin serialises GUIDs without dashes, and that is the form it puts
                // in data-id, so the selector has to use the same one. Both spellings are
                // accepted coming in; only hex goes out.
                const id = String(entry.libraryId || entry.LibraryId || '').replace(/-/g, '').toLowerCase();
                if (!/^[0-9a-f]{32}$/.test(id)) return;

                const mode = String(entry.mode || entry.Mode || '').toLowerCase();
                const selector = '.card[data-id="' + id + '"] .cardImageContainer';

                if (mode === 'custom') {
                    const url = this._libraryArtUrl(entry.url || entry.Url);
                    if (!url) return;
                    css += selector + '{background-image:url("' + url + '")!important;' +
                           'background-size:cover!important;background-position:center!important;}';
                } else if (mode === 'none') {
                    // Reveals the card padder underneath, which is the icon and name — the
                    // same thing a library with no artwork shows.
                    css += selector + '{background-image:none!important;}';
                }
            });

            let style = document.getElementById(this.LIBRARY_ART_STYLE_ID);
            if (!css) {
                if (style) style.remove();
                return;
            }
            if (!style) {
                style = document.createElement('style');
                style.id = this.LIBRARY_ART_STYLE_ID;
                document.head.appendChild(style);
            }
            style.textContent = css;
        },

        /// Remembers the rules against the account they belong to, so the next load can apply
        /// them before any request comes back. Without this every page load would show the
        /// real artwork until the fetch resolved, which is the whole thing being avoided.
        cacheLibraryArtwork: function (userId, entries) {
            try {
                localStorage.setItem(this.config.libraryArtKey, JSON.stringify({
                    userId: this.normalizeGuid(userId),
                    entries: entries || []
                }));
            } catch (e) { /* storage full or blocked — the fetch below still applies them */ }
        },

        /// Applies the cached rules if they belong to whoever is signed in now.
        applyCachedLibraryArtwork: function () {
            try {
                const cached = JSON.parse(localStorage.getItem(this.config.libraryArtKey) || 'null');
                if (!cached || !cached.entries) return;

                const current = (typeof ApiClient !== 'undefined' && ApiClient)
                    ? this.normalizeGuid(ApiClient.getCurrentUserId()) : '';
                // A cache belonging to somebody else is worse than none: it would paint one
                // profile's choices over another's libraries.
                if (!current || this.normalizeGuid(cached.userId) !== current) return;

                this.applyLibraryArtwork(cached.entries);
            } catch (e) { /* unreadable cache — the fetch will rebuild it */ }
        },

        /// Refreshes the rules from the server for whoever is signed in.
        loadLibraryArtwork: function () {
            if (typeof ApiClient === 'undefined' || !ApiClient || !ApiClient.accessToken()) return;
            const userId = ApiClient.getCurrentUserId();
            if (!userId) return;

            fetch(ApiClient.getUrl('plugins/profiles/library-artwork'), {
                cache: 'no-store',
                headers: this.getAuthHeaders(ApiClient.accessToken())
            })
            .then(res => res.ok ? res.json() : Promise.reject(new Error('unavailable')))
            .then(entries => {
                const list = Array.isArray(entries) ? entries : [];
                this._libraryArtLoaded = true;
                this.cacheLibraryArtwork(userId, list);
                this.applyLibraryArtwork(list);
            })
            .catch(() => {
                // Server unreachable, or a server older than this build. Leave whatever the
                // cache already applied rather than dropping back to Jellyfin's artwork.
            });
        },

        // ─── D-pad focus trap ─────────────────────────────────────────────────────
        // Issue #16: on a television our screens cover the page, but Jellyfin's own
        // navigation is still listening on document and still sees every element behind
        // them. Pressing a direction moved focus into the home screen underneath, leaving
        // the remote controlling a page nobody could see.
        //
        // `inert` would be the tidy answer and is not an option — the TV browsers this
        // has to work on predate it. So directions are taken at the capture phase, before
        // Jellyfin's handlers run, and resolved against our own elements.
        //
        // The listeners are bound once and stay bound. They resolve the active surface on
        // every event and return immediately when there is none, so the trap covers every
        // Bonfire screen without anyone having to remember to arm it, and stops applying
        // the instant the last one leaves the DOM. Lifecycle bookkeeping is what leaks.

        /// Selector for the surfaces we trap focus inside: the gate overlay and any of our
        /// own dialogs (confirm, alert, crop, panic), all of which sit above the page.
        /// Every dialog gets this class as it is built. It replaced
        /// `[id^="profiles-"][id$="-dialog"]`, which is two unqualified attribute
        /// selectors: the engine cannot use an index for either, so resolving them meant
        /// walking every element in the document — and this runs on every keydown and
        /// focusin *in the whole application*, so typing in Jellyfin's own search box
        /// walked the page once per keystroke.
        ///
        /// A class keeps the property that made the id pattern attractive: it travels with
        /// the element, so removing the dialog removes it from the set with no bookkeeping
        /// to get wrong. See the note above about lifecycle bookkeeping being what leaks.
        TRAP_SURFACE_CLASS: 'jpf-trap-surface',

        TRAP_SURFACE_SELECTOR: '#profiles-gate-overlay, .jpf-trap-surface',

        /// The surface the remote should currently be confined to, or null when Bonfire
        /// has nothing on screen. Dialogs render above the gate, and the last one in the
        /// DOM is the topmost, so document order picks the right one.
        _activeTrapSurface: function () {
            // Class first, id second. Dialogs render above the gate, so the last dialog in
            // document order is the topmost surface; with none open, the gate is. On the
            // far more common path — nothing of ours on screen — this is one class-index
            // lookup that comes back empty, where it used to be a full document walk on
            // every keystroke anywhere in the application.
            //
            // The answer is identical to the querySelectorAll version it replaces,
            // including the case that looks like an oversight: a topmost surface that has
            // already detached disarms the trap rather than falling back to the gate
            // underneath. That is existing behaviour with a test on it, and this change is
            // about how the surface is found, not about what counts as one.
            const dialogs = document.getElementsByClassName(this.TRAP_SURFACE_CLASS);
            const last = dialogs.length
                ? dialogs[dialogs.length - 1]
                : document.getElementById('profiles-gate-overlay');
            if (!last) return null;

            // A dialog mid-fade still counts; one already detached does not.
            return last.isConnected === false ? null : last;
        },

        /// Everything inside a surface a remote can land on. Hidden elements are left out:
        /// the overlay swaps between a grid and a form, and the old one lingers.
        ///
        /// The offsetParent read below was flagged as forcing layout once per element
        /// inside a focus handler. It does not: layout is invalidated by writes, and this
        /// filter only reads, so the whole loop costs one layout however many controls a
        /// surface has. Spatial navigation needs geometry anyway.
        ///
        /// `checkVisibility()` is the tidy replacement and is **not an option here**. It
        /// needs Chrome 105; the TVs this has to work on are Chrome 68 (webOS 5) and
        /// Chrome 76 (Tizen 6.0), so it would throw on exactly the devices the focus trap
        /// exists for — and a remote is the one input with no way to escape a broken trap.
        _overlayFocusables: function (root) {
            return Array.prototype.filter.call(
                root.querySelectorAll('button, input, select, textarea, a[href], [tabindex]:not([tabindex="-1"])'),
                el => !el.disabled && el.offsetParent !== null
            );
        },

        /// True for a control that binds the arrow keys to something other than moving
        /// between controls — the crop view pans the picture with them. Those keys are left
        /// alone there; the trap still keeps focus from escaping the surface.
        _ownsArrowKeys: function (node) {
            if (!node) return false;
            if (node.closest) return !!node.closest('[data-profiles-own-keys="1"]');
            return node.getAttribute && node.getAttribute('data-profiles-own-keys') === '1';
        },

        /// Steps one screen back from `surface`, or absorbs the press when there is nowhere
        /// to go. Every sub-screen carries a Back or Cancel control already, so this clicks
        /// the one it finds rather than duplicating each screen's teardown.
        ///
        /// Doing nothing is the right answer on the picker itself: it is a required choice,
        /// and on a TV the alternative is closing the whole app mid-selection.
        _dismissSurface: function (surface) {
            const back = surface.querySelector(
                '#profiles-crop-cancel, #profiles-panic-cancel, #dialog-cancel-btn, #dialog-close-btn, ' +
                '#pin-cancel-btn, #master-pin-cancel-btn, #create-cancel-btn, #edit-cancel-btn, ' +
                '#select-cancel-btn, #prompt-cancel-btn, #actions-cancel-btn, ' +
                '#bonfire-back-btn, #switcher-mode-back-btn, #settings-back-btn, #theme-settings-back-btn, #account-back-btn, #profiles-resume-btn, #profiles-libart-cancel, ' +
                '#onboarding-skip, #main-pin-skip, #pin-reset-back'
            );
            if (back) back.click();
        },

        /// Moves focus one step through the surface in document order, wrapping at both
        /// ends. This is what Tab means; geometry is for the direction keys.
        _stepOverlayFocus: function (root, delta) {
            const items = this._overlayFocusables(root);
            if (!items.length) return;
            const at = items.indexOf(document.activeElement);
            const next = at < 0
                ? (delta > 0 ? 0 : items.length - 1)
                : (at + delta + items.length) % items.length;
            this._focusVisibly(items[next]);
        },

        /// Focuses a control and brings it into view.
        ///
        /// The profile forms are taller than a television screen, so Save and Cancel sit
        /// below the fold. Focus alone moved to them without scrolling on the TV clients,
        /// which reads as the remote having stopped responding.
        _focusVisibly: function (el) {
            if (!el) return;
            el.focus();
            try {
                const r = el.getBoundingClientRect();
                const h = window.innerHeight || document.documentElement.clientHeight;
                // Only scroll when it is actually out of the viewport — an unconditional
                // scrollIntoView jerks the picker around on every arrow press.
                // The boolean form scrolls like { block: 'nearest' } for an element out of
                // view on one side; the options object is Chromium 61 and reads as `true`
                // before that.
                if (r.top < 0 || r.bottom > h) {
                    el.scrollIntoView(r.top < 0);
                }
            } catch (e) { /* no layout information — focus alone will do */ }
        },

        /// Nearest focusable in a direction. Movement across the axis is penalised so a
        /// grid walks along its row instead of cutting diagonally to a closer card.
        _moveOverlayFocus: function (root, dir) {
            const items = this._overlayFocusables(root);
            if (!items.length) return;

            const current = (document.activeElement && root.contains(document.activeElement))
                ? document.activeElement : null;
            if (!current) { this._focusVisibly(items[0]); return; }

            const from = current.getBoundingClientRect();
            const fx = from.left + from.width / 2;
            const fy = from.top + from.height / 2;
            const horizontal = (dir === 'left' || dir === 'right');

            let best = null, bestScore = Infinity;
            items.forEach(el => {
                if (el === current) return;
                const r = el.getBoundingClientRect();
                const dx = (r.left + r.width / 2) - fx;
                const dy = (r.top + r.height / 2) - fy;
                if (dir === 'left'  && dx > -1) return;
                if (dir === 'right' && dx <  1) return;
                if (dir === 'up'    && dy > -1) return;
                if (dir === 'down'  && dy <  1) return;

                const along  = horizontal ? Math.abs(dx) : Math.abs(dy);
                const across = horizontal ? Math.abs(dy) : Math.abs(dx);
                const score = along + across * 3;
                if (score < bestScore) { bestScore = score; best = el; }
            });

            if (best) this._focusVisibly(best);
        },

        _bindOverlayFocusTrap: function () {
            if (this._overlayTrap) return;

            // e.key is missing on some older TV browsers, so keyCode is the fallback.
            const dirOf = (e) => {
                switch (e.key) {
                    case 'ArrowLeft':  case 'Left':  return 'left';
                    case 'ArrowRight': case 'Right': return 'right';
                    case 'ArrowUp':    case 'Up':    return 'up';
                    case 'ArrowDown':  case 'Down':  return 'down';
                }
                switch (e.keyCode) {
                    case 37: return 'left';
                    case 38: return 'up';
                    case 39: return 'right';
                    case 40: return 'down';
                }
                return null;
            };

            const onKeyDown = (e) => {
                const surface = this._activeTrapSurface();
                if (!surface) return;
                // Never swallow a shortcut — Ctrl+Shift+B has to keep working from here.
                if (e.ctrlKey || e.altKey || e.metaKey) return;

                // The TV Back/Return key. Samsung sends 10009, LG sends 461, and neither is
                // a key the page gets a second chance at: unhandled, Tizen puts up its
                // "exit application?" prompt behind our overlay, where it cannot be read or
                // dismissed. While a Bonfire screen is up, Back belongs to that screen.
                if (e.keyCode === 10009 || e.keyCode === 461 || e.key === 'XF86Back') {
                    e.preventDefault();
                    e.stopPropagation();
                    this._dismissSurface(surface);
                    return;
                }

                // An event dispatched at `window` has no element target, and contains()
                // wants a node. Treat it as outside: the direction keys still move, and
                // _moveOverlayFocus enters at the first control when the active element is
                // not in the surface, which is the right first press.
                const inside = !!(e.target && e.target.nodeType && surface.contains(e.target));
                const dir = dirOf(e);

                if (dir) {
                    // The crop view pans with the arrows. Ownership is declared on that
                    // element rather than the whole dialog, so the buttons beside it stay
                    // reachable — a remote has no Tab key to escape with otherwise.
                    if (inside && this._ownsArrowKeys(e.target)) return;

                    const tag = (e.target.tagName || '').toLowerCase();
                    if (inside) {
                        // Left/right belong to the caret while typing a PIN or a name.
                        if ((tag === 'input' || tag === 'textarea') && (dir === 'left' || dir === 'right')) return;
                        // Up/down change the value of a dropdown, which is what a remote
                        // expects. Left/right stay ours, so there is still a way off it.
                        if (tag === 'select' && (dir === 'up' || dir === 'down')) return;
                    }
                    e.preventDefault();
                    e.stopPropagation();
                    this._moveOverlayFocus(surface, dir);
                    return;
                }

                if (e.key === 'Tab' || e.keyCode === 9) {
                    // Tab follows document order, not geometry. Routing it through the
                    // spatial search meant it did nothing at all in a stacked form, where
                    // every candidate is directly above or below rather than beside.
                    e.preventDefault();
                    e.stopPropagation();
                    this._stepOverlayFocus(surface, e.shiftKey ? -1 : 1);
                    return;
                }

                // A select landing on the page behind would act on something invisible.
                // Inside the surface it is left alone: that is how our own buttons fire.
                const isSelect = e.key === 'Enter' || e.key === ' ' || e.keyCode === 13;
                if (isSelect && !inside) {
                    e.preventDefault();
                    e.stopPropagation();
                    const items = this._overlayFocusables(surface);
                    if (items.length) items[0].focus();
                }
            };

            // Second line of defence: whatever moved focus out, take it back. Membership is
            // tested against any surface rather than the topmost one, so focus returning to
            // the gate under a dialog that is still fading out is left where it belongs.
            //
            // Two guards, and both are here because this handler fought another focus trap
            // to a standstill. MUI's Modal enforces focus back inside itself exactly the
            // way this does, so with Jellyfin's account menu still open the two handlers
            // bounced focus between them: 134 focus() calls and a RangeError, our onFocusIn
            // and MUI's contain() alternating the whole way down the stack. From the
            // household's side, every text box and dropdown in the profile form was dead
            // and the page jumped to the top — items[0] being the first field in the form.
            const onFocusIn = (e) => {
                // 1. Never re-enter. focus() dispatches focusin synchronously, so without
                //    this the handler calls itself through its own reclaim.
                if (this._reclaimingFocus) return;

                const surface = this._activeTrapSurface();
                if (!surface) return;
                if (e.target && e.target.closest && e.target.closest(this.TRAP_SURFACE_SELECTOR)) return;
                if (e.target === document.body || e.target === document.documentElement) return;

                // 2. Stop fighting. A reclaim that is immediately undone means something
                //    else is also enforcing focus, and whoever gives up last wins nothing —
                //    the loop only ends in a stack overflow. Standing down leaves focus
                //    where the other trap wants it, which is at least a usable state, and
                //    is the correct outcome anyway: the other trap belongs to something
                //    rendered above us.
                const now = Date.now();
                if (this._focusStandDownUntil && now < this._focusStandDownUntil) return;

                this._focusReclaims = (now - (this._focusReclaimAt || 0) < 1000)
                    ? (this._focusReclaims || 0) + 1
                    : 1;
                this._focusReclaimAt = now;
                if (this._focusReclaims > 4) {
                    this._focusStandDownUntil = now + 5000;
                    console.warn('ProfilesPlugin: another focus trap is active; standing down '
                        + 'rather than fighting it for focus.');
                    return;
                }

                const items = this._overlayFocusables(surface);
                if (!items.length) return;
                this._reclaimingFocus = true;
                try {
                    items[0].focus();
                } finally {
                    this._reclaimingFocus = false;
                }
            };

            // On `window`, and in the capture phase. Both halves of that matter, and the
            // second time #16 was reported it was because of the first.
            //
            // jellyfin-web binds its own remote navigation with
            // `window.addEventListener('keydown', ...)` — no capture — in
            // scripts/keyboardNavigation.js, and it bails on `e.defaultPrevented`. So for
            // any key event that travels through the document we are ahead of it either
            // way. What document-level capture does NOT see is an event dispatched at
            // `window` itself: its propagation path is `window` alone, so a document
            // listener is never on it while upstream's window listener is. Television
            // webviews do dispatch remote keys that way.
            //
            // When that happens the arrows belong to upstream's focusManager, and its
            // focusable set is INPUT/TEXTAREA/SELECT/BUTTON/A plus `.focusable` — it does
            // not look at [tabindex] at all. A profile card is a div with tabindex="0", so
            // the grid is invisible to it and our two footer buttons are not: every arrow
            // press lands on Manage Profiles and no profile can be reached. That is
            // exactly how it was reported. The cards carry `focusable` now as well, so
            // both ends of this are covered.
            //
            // Window capture is first on the path for everything else too, so this is a
            // superset of what it replaces, not a trade.
            window.addEventListener('keydown', onKeyDown, true);
            window.addEventListener('focusin', onFocusIn, true);
            this._overlayTrap = { onKeyDown: onKeyDown, onFocusIn: onFocusIn };
        },

        _releaseOverlayFocusTrap: function () {
            if (!this._overlayTrap) return;
            window.removeEventListener('keydown', this._overlayTrap.onKeyDown, true);
            window.removeEventListener('focusin', this._overlayTrap.onFocusIn, true);
            this._overlayTrap = null;
        },

        removeProfileOverlay: function () {
            const overlay = document.getElementById('profiles-gate-overlay');
            if (overlay) overlay.remove();

            // Reopening the gate should re-read the server; caching only spans one
            // session so a library added elsewhere shows up next time.
            this.clearSharedFormData();

            // Tearing down the overlay must also drop the form listeners it owned.
            this.clearManagedDocumentListeners();

            // Re-enable scrolling
            document.body.classList.remove('profiles-no-scroll');
            document.documentElement.classList.remove('profiles-no-scroll');

            const skinHeader = document.querySelector('.skinHeader');
            if (skinHeader) skinHeader.style.display = '';

            // Note: view-home blur no longer applied (removed in v1.0.14)
        },

        // ─── Inactivity Lockout Timer ─────────────────────────────────────────────

        // Called on page load when an active profile session already exists.
        // Fetches /list (using the master token) to find the active profile's
        // lockout setting, then arms the inactivity timer.
        initLockoutTimer: function () {
            if (!this.isProfileSessionActive()) return;

            const masterState = this.readMasterState();
            if (!masterState || !masterState.token) return;

            const apiClient = ApiClient;
            if (!apiClient) return;

            const currentUserId = typeof apiClient.getCurrentUserId === 'function'
                ? apiClient.getCurrentUserId() : null;
            if (!currentUserId) return;

            const url = apiClient.getUrl('plugins/profiles/list');
            fetch(url, { headers: this.getAuthHeaders(masterState.token) })
            .then(res => { if (!res.ok) throw new Error('fail'); return res.json(); })
            .then(profiles => {
                const active = (profiles || []).find(p => {
                    const id = p.profileUserId || p.ProfileUserId;
                    return this.normalizeGuid(id) === this.normalizeGuid(currentUserId);
                });
                if (!active) return;
                const requiresPin = active.requiresPin !== undefined ? active.requiresPin : active.RequiresPin;
                if (!requiresPin) return; // No PIN = no lockout
                const minutes = active.lockoutMinutes !== undefined ? active.lockoutMinutes
                    : (active.LockoutMinutes !== undefined ? active.LockoutMinutes : 5);
                if (minutes > 0) this.startInactivityTimer(minutes);
            })
            .catch(() => { /* silent — lockout timer is best-effort */ });
        },

        isMediaPlaying: function () {
            const mediaElements = document.querySelectorAll('video, audio');
            for (let i = 0; i < mediaElements.length; i++) {
                const media = mediaElements[i];
                if (media && !media.paused && !media.ended && media.currentTime > 0) {
                    return true;
                }
            }
            return false;
        },

        // Arms the inactivity timer. Resets on any user interaction.
        // Any device event (mouse, keyboard, touch, pointer, scroll) counts as activity,
        // making this safe for TV remotes, magic remotes, game pads, and touchscreens.
        startInactivityTimer: function (minutes) {
            this.stopInactivityTimer();
            const ms = minutes * 60 * 1000;
            const events = [
                'mousemove', 'mousedown', 'keydown',
                'touchstart', 'scroll', 'wheel', 'click',
                'pointermove', 'pointerdown'  // covers LG Magic Remote and pointer-based TV inputs
            ];

            const checkAndLock = () => {
                if (this.isMediaPlaying()) {
                    // Defer lockout by 1 minute if media is actively playing
                    this.inactivityTimer = setTimeout(checkAndLock, 60 * 1000);
                } else {
                    this.lockActiveProfile();
                }
            };

            const resetTimer = () => {
                clearTimeout(this.inactivityTimer);
                this.inactivityTimer = setTimeout(checkAndLock, ms);
            };

            events.forEach(ev => document.addEventListener(ev, resetTimer, { passive: true }));
            this.inactivityEventHandlers = { resetTimer, events };
            resetTimer(); // Arm immediately
        },

        stopInactivityTimer: function () {
            clearTimeout(this.inactivityTimer);
            this.inactivityTimer = null;
            if (this.inactivityEventHandlers) {
                const { resetTimer, events } = this.inactivityEventHandlers;
                events.forEach(ev => document.removeEventListener(ev, resetTimer));
                this.inactivityEventHandlers = null;
            }
        },

        // Called when the inactivity timer fires. Clears the active session,
        // restores master credentials, then shows the profile selector.
        //
        // It used to restore the master's credentials here, which made the lock the least
        // secure moment in the product: the device went from a restricted profile to the
        // master account, with only the picker drawn over it. The profile's own session is
        // kept instead; getting back into it — or into anything else — goes through the
        // gate, and the server asks for the PIN.
        lockActiveProfile: function () {
            this.stopInactivityTimer();
            this.clearProfileSession();
            this.interceptHomeAndShowProfiles();
        },

        renderOverlayContent: function (overlay, profiles) {
            // Claims the screen so a form still loading in the background does not
            // draw itself over the grid when it finally returns.
            this.beginNavigation();

            const title = this.isManageMode ? t('gate.manageProfiles') : t('gate.whosWatching');
            const manageBtnText = this.isManageMode ? t('common.done') : t('gate.manageProfiles');

            const masterProfile = profiles.find(p => p.isMaster && !p.isBonfire);
            // Before readMasterState below, which decides which group is "yours".
            this._syncMasterFromProfiles(masterProfile);
            const maxSubProfiles = masterProfile ? masterProfile.maxSubProfiles : 5;
            const subProfileCount = profiles.filter(p => !p.isMaster && !p.isBonfire).length;
            const atLimit = subProfileCount >= maxSubProfiles;

            const masterState = this.readMasterState();
            const localMasterId = masterState ? this.normalizeGuid(masterState.masterUserId) : '';

            // Same condition the two settings tiles used, so Settings shows up in exactly
            // the cases they used to.
            const hasLocalMaster = profiles.some(p => p.isMaster && !p.isBonfire);

            // Group profiles by masterUserId
            const grouped = {};
            for (const p of profiles) {
                // An added account sits with the household it was added to, not in a
                // section of its own: it is one more person on this screen.
                const key = p.isLinked
                    ? localMasterId
                    : this.normalizeGuid(p.masterUserId || (p.isBonfire ? '' : localMasterId));
                if (!grouped[key]) {
                    grouped[key] = [];
                }
                grouped[key].push(p);
            }

            // Order groups so local home is first, then rest alphabetical by master user name
            const groupKeys = Object.keys(grouped);
            groupKeys.sort((a, b) => {
                if (a === localMasterId) return -1;
                if (b === localMasterId) return 1;
                const masterA = grouped[a].find(p => p.isMaster);
                const masterB = grouped[b].find(p => p.isMaster);
                const nameA = masterA ? masterA.profileName.toLowerCase() : '';
                const nameB = masterB ? masterB.profileName.toLowerCase() : '';
                return nameA.localeCompare(nameB);
            });

            // The signed-in Jellyfin user IS the active profile — switching signs in as
            // that profile's own account. The gate never said which one that was, and the
            // avatar-colour rings made it look as though it did.
            //
            // But ApiClient is not the authority when the picker was opened from the header
            // bubble. handleBubbleClick puts the *master's* credentials back before listing,
            // because a sub-profile's token cannot see its siblings — so by the time this
            // renders, getCurrentUserId() is the master on every platform, and the badge
            // sat on the master's card however long you had been using a profile. Reported
            // as "the switcher says I am signed in as my main profile"; it is this, not a
            // switch that failed. _resumeState is the profile that was active a moment ago
            // and is what the badge means, so it wins when it is set.
            const resumed = this._resumeState && !this._resumeState.closeOnly
                ? this._resumeState.userId
                : null;
            const signedInId = resumed
                ? this.normalizeGuid(resumed)
                : ((typeof ApiClient.getCurrentUserId === 'function')
                    ? this.normalizeGuid(ApiClient.getCurrentUserId())
                    : '');

            // `focusable` is jellyfin-web's own hook, not ours — see focusManager.js,
            // whose focusable set is INPUT/TEXTAREA/SELECT/BUTTON/A plus that class, and
            // which never looks at [tabindex]. Without it a profile card does not exist as
            // far as upstream's remote navigation is concerned, so any moment our own trap
            // does not get the key event leaves the whole grid unreachable and only the
            // footer buttons focusable. That is issue #16's second report, word for word:
            // "if I move off the top left one, it goes to manage profiles".
            const renderCard = (p) => `
                <div class="profile-card focusable ${this.isManageMode ? 'manage-mode' : ''}${
                    signedInId && this.normalizeGuid(p.profileUserId) === signedInId ? ' is-current' : ''
                }" data-id="${p.profileUserId}" data-pin="${p.requiresPin}" tabindex="0"${
                    signedInId && this.normalizeGuid(p.profileUserId) === signedInId ? ' aria-current="true"' : ''
                }>
                    <div class="profile-avatar-container">
                        ${p.isMaster && !p.isLinked ? `
                        <div class="profile-crown">
                            <svg viewBox="0 0 24 24" fill="currentColor" style="width: 28px; height: 28px; color: var(--jpf-warn); filter: drop-shadow(0 2px 5px rgba(0,0,0,0.55));">
                                <path d="M5 16h14a1 1 0 0 0 1-.76l2.89-10.12a.5.5 0 0 0-.74-.53l-5.6 3.73-4.11-6.17a.5.5 0 0 0-.88 0L7.45 8.32 1.85 4.59a.5.5 0 0 0-.74.53L4 15.24a1 1 0 0 0 1 .76z"/>
                                <rect x="4" y="18" width="16" height="2" rx="1"/>
                            </svg>
                        </div>
                        ` : ''}
                        <div class="profile-avatar${
                            p.profileImage && p.transparentAvatar ? ' is-transparent' : ''
                        }" style="background-color: ${
                            p.profileImage && p.transparentAvatar ? 'transparent' : safeColor(p.avatarColor)
                        }; overflow: ${
                            p.profileImage && p.transparentAvatar ? 'visible' : 'hidden'
                        }; display: flex; align-items: center; justify-content: center; position: relative;">
                            ${avatarInner(p.profileImage, p.avatarInitial, /* useThumb */ true)}
                            ${this.isManageMode ? `
                            <div class="profile-avatar-overlay-wrap">
                                <svg class="profile-avatar-overlay-svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" style="width: 32px; height: 32px; color: #fff;">
                                    <path d="M12 20h9"></path>
                                    <path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4L16.5 3.5z"></path>
                                </svg>
                            </div>
                            ` : ''}
                        </div>
                        ${p.requiresPin ? `
                        <div class="profile-lock-indicator">
                            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round" style="width: 14px; height: 14px; color: #fff;">
                                <rect x="3" y="11" width="18" height="11" rx="2" ry="2"></rect>
                                <path d="M7 11V7a5 5 0 0 1 10 0v4"></path>
                            </svg>
                        </div>
                        ` : ''}
                        ${p.isBonfire ? `
                        <div class="profile-bonfire-indicator" title="${p.isLinked ? t('gate.addedAccount') : t('gate.bonfireProfile')}">
                            <span class="material-icons" style="font-size: 1.15rem; color: #fff;">${p.isLinked ? 'link' : 'local_fire_department'}</span>
                        </div>
                        ` : ''}
                    </div>
                    <div class="profile-name">
                        <span>${escapeHtml(p.profileName)}</span>
                        ${signedInId && this.normalizeGuid(p.profileUserId) === signedInId
                            ? `<span class="profile-current-badge">${t('gate.signedIn')}</span>` : ''}
                        ${this.viewingLine(p.viewing)
                            ? `<span class="profile-viewing${p.viewing.allowed ? '' : ' is-out'}">${escapeHtml(this.viewingLine(p.viewing))}</span>` : ''}
                        ${this.isManageMode ? `
                            <span class="profile-pin-badge ${p.requiresPin ? 'locked' : 'unlocked'}">
                                ${p.requiresPin ? t('gate.pinProtected') : t('gate.noPin')}
                            </span>
                        ` : ''}
                    </div>
                </div>
            `;

            let sectionsHtml = '';
            for (const k of groupKeys) {
                const groupProfiles = grouped[k];
                const isLocalGroup = (k === localMasterId);

                let headerTitle = '';
                let headerIcon = '';
                let isBonfireIcon = false;

                // Both headers use the campfire glyph — the section is called a Bonfire, so a
                // house icon read as a different concept entirely. Your own Bonfire keeps the
                // warm flame colour; linked ones are tinted by .bonfire-icon-color so the two
                // remain distinguishable at a glance.
                if (isLocalGroup) {
                    headerTitle = t('gate.yourBonfire');
                    headerIcon = "local_fire_department";
                } else {
                    const masterProfileForGroup = groupProfiles.find(p => p.isMaster);
                    const groupName = masterProfileForGroup ? escapeHtml(masterProfileForGroup.profileName) : t('gate.guest');
                    headerTitle = t('gate.bonfireOf', { name: groupName });
                    headerIcon = "local_fire_department";
                    isBonfireIcon = true;
                }

                let cardsHtml = groupProfiles.map(p => renderCard(p)).join('');

                if (isLocalGroup) {
                    // Settings used to sit here, as two cards in the same row as people.
                    // They are not profiles: Switcher Style is per-account and Your Bonfire
                    // is between accounts, and neither belongs to the grid of who can watch.
                    // They live behind Settings now — which also means the LAN-bypass toggle,
                    // the one that can hand somebody your admin rights, is somewhere you went
                    // on purpose rather than somewhere you can land while renaming a profile.

                    // Manage Profiles only. The gate answers one question — who is
                    // watching — and a card in that grid reads as a person. Adding one is
                    // administration, and it belongs on the screen named for it, which is
                    // one button away and already where you go to rename or delete.
                    if (this.isManageMode) {
                        if (!atLimit) {
                            cardsHtml += `
                                <div class="profile-card action-add-profile focusable" tabindex="0">
                                    <div class="profile-avatar-container">
                                        <div class="profile-avatar add-avatar">+</div>
                                    </div>
                                    <div class="profile-name">${t('gate.addProfile')}</div>
                                </div>
                            `;
                        } else {
                            cardsHtml += `
                                <div class="profiles-limit-notice">${t('gate.limitReached', { count: subProfileCount, max: maxSubProfiles })}</div>
                            `;
                        }
                    }
                }

                sectionsHtml += `
                    <div class="profiles-home-section">
                        <div class="profiles-home-header">
                            <span class="material-icons profiles-home-icon ${isBonfireIcon ? 'bonfire-icon-color' : ''}">${headerIcon}</span>
                            <span class="profiles-home-title">${headerTitle}</span>
                        </div>
                        <div class="profiles-grid">
                            ${cardsHtml}
                        </div>
                    </div>
                `;
            }

            // jpf-one-home: one household on screen, so its heading names nothing the title
            // has not. Themes without a panel round each household hide it then.
            overlay.innerHTML = `
                <div class="profiles-modal-content anim-fade-in${groupKeys.length === 1 ? ' jpf-one-home' : ''}">
                    <h1 class="profiles-title">${title}</h1>
                    ${this.isManageMode ? `<p class="jpf-manage-hint">${t('gate.manageHint')}</p>` : ''}
                    ${sectionsHtml}
                    <div class="profiles-footer">
                        <button id="profiles-toggle-manage-btn" class="profiles-btn btn-secondary">${manageBtnText}</button>
                        ${this.isManageMode && hasLocalMaster
                            ? `<button id="profiles-settings-btn" class="profiles-btn btn-secondary">${t('common.settings')}</button>`
                            : ''}
                        ${this._resumeState && !this.isManageMode
                            ? `<button id="profiles-resume-btn" class="profiles-btn btn-secondary">${t('common.cancel')}</button>`
                            : ''}
                    </div>
                    <!-- Deliberately plain and dim. It has to be reachable by D-pad, because
                         a TV has no keyboard shortcut, and this screen is exactly where
                         someone locked out by a broken switcher would be standing.
                         Hidden until the server confirms a code is configured. -->
                    <button id="profiles-panic-link" tabindex="0" style="
                        display: none; background: none; border: none; color: var(--jpf-text-3);
                        font-size: 0.72rem; margin-top: 1.5rem; cursor: pointer;
                        text-decoration: underline; padding: 6px 10px;">${t('gate.cantGetPast')}</button>
                    <!-- Last in the markup and drawn first by the stylesheet (order: -1).
                         The focus trap reclaims to the first control in the DOM, and on a
                         TV that must be a profile, not an announcement's Dismiss button. -->
                    <div id="profiles-announcements" class="profiles-announcements" style="display: none;"></div>
                </div>
            `;

            this.drawLogo(overlay);
            this.attachOverlayInteractions(overlay, profiles);
            this.fitGateToScreen(overlay);
            this.showAnnouncements(overlay);
        },

        /// The administrator's announcements, above the title.
        ///
        /// Asked for once a minute at most: the gate re-renders on every switch into and
        /// out of manage mode, and a message that changed thirty seconds ago can wait.
        /// Never from checkRoute — only when the gate is drawn.
        ///
        /// Plain text only. The message is written into textContent, never innerHTML,
        /// because it reaches every household on the server.
        ///
        /// An info announcement can be dismissed on this device. The key is the id and the
        /// text together, so one the administrator rewrites shows again. Warning and
        /// critical stay until they end.
        showAnnouncements: function (overlay) {
            const draw = (items) => {
                const box = overlay.querySelector('#profiles-announcements');
                if (!box) return;
                const dismissed = this._readDismissedAnnouncements();
                const shown = (items || []).filter(a => {
                    const sev = this._announcementSeverity(a);
                    return sev !== 'info' || dismissed.indexOf(this._announcementKey(a)) < 0;
                });
                box.innerHTML = '';
                shown.forEach(a => {
                    const sev = this._announcementSeverity(a);
                    const row = document.createElement('div');
                    row.className = 'profiles-announcement';
                    row.setAttribute('data-severity', sev);
                    row.setAttribute('role', sev === 'critical' ? 'alert' : 'status');

                    const icon = document.createElement('span');
                    icon.className = 'material-icons profiles-announcement-icon';
                    icon.setAttribute('aria-hidden', 'true');
                    icon.textContent = sev === 'critical' ? 'error' : (sev === 'warning' ? 'warning' : 'info');
                    row.appendChild(icon);

                    const text = document.createElement('span');
                    text.className = 'profiles-announcement-text';
                    text.textContent = String(a.message || a.Message || '');
                    row.appendChild(text);

                    if (sev === 'info') {
                        const close = document.createElement('button');
                        close.type = 'button';
                        close.className = 'profiles-announcement-dismiss focusable';
                        close.setAttribute('aria-label', t('gate.dismiss'));
                        close.title = t('gate.dismiss');
                        close.textContent = '×';
                        close.addEventListener('click', () => {
                            this._dismissAnnouncement(a);
                            draw(items);
                        });
                        row.appendChild(close);
                    }
                    box.appendChild(row);
                });
                box.style.display = shown.length ? '' : 'none';
                this.fitGateToScreen(overlay);
            };

            const cached = this._announcementCache;
            if (cached && Date.now() - cached.at < 60000) { draw(cached.items); return; }

            const masterState = this.readMasterState();
            const token = (masterState && masterState.token)
                || (typeof ApiClient.accessToken === 'function' ? ApiClient.accessToken() : '');
            if (!token) return;
            fetch(ApiClient.getUrl('plugins/profiles/announcements'), {
                headers: this.getAuthHeaders(token),
                cache: 'no-store'
            })
                .then(res => res.ok ? res.json() : [])
                .then(items => {
                    const list = Array.isArray(items) ? items : [];
                    this._announcementCache = { at: Date.now(), items: list };
                    draw(list);
                })
                .catch(() => { /* no announcements is the safe reading */ });
        },

        /// Closed for maintenance: say so, once per page load.
        ///
        /// Asked of /info, which needs no token, because the people it is for are the ones
        /// who cannot sign in. On the sign-in screen and for an administrator it is a strip
        /// across the top; for anyone else signed in it covers the page, since every request
        /// they make is refused and the page behind would only show errors.
        ///
        /// Written with textContent: the message is the administrator's own words.
        checkMaintenance: function () {
            this.serverInfo()
                .then(info => {
                    const m = info && (info.maintenance || info.Maintenance);
                    if (!m) return;
                    const userId = typeof ApiClient.getCurrentUserId === 'function' ? ApiClient.getCurrentUserId() : null;
                    if (!userId || typeof ApiClient.getCurrentUser !== 'function') {
                        this.showMaintenance(m, false);
                        return;
                    }
                    return Promise.resolve(ApiClient.getCurrentUser()).then(user => {
                        const policy = user && (user.Policy || user.policy);
                        const admin = !!(policy && (policy.IsAdministrator || policy.isAdministrator));
                        this.showMaintenance(m, !admin, admin);
                    }, () => this.showMaintenance(m, true));
                })
                .catch(() => { /* unknown: say nothing */ });
        },

        showMaintenance: function (m, blocking, admin) {
            if (document.getElementById('profiles-maintenance')) return;
            const message = String(m.message || m.Message || '');
            const until = m.untilUtc || m.UntilUtc;
            let back = '';
            if (until) {
                const d = new Date(until);
                // Only a time still to come: "Back by" an hour ago says the server is late.
                if (!isNaN(d.getTime()) && d.getTime() > Date.now()) back = t('maintenance.backBy', { time: d.toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' }) });
            }

            const box = document.createElement('div');
            box.id = 'profiles-maintenance';
            // The cover is a trap surface. Without it, the focus trap hands focus back to the
            // gate underneath, and a remote moves around profiles nobody can see.
            box.className = blocking ? 'profiles-maintenance is-blocking ' + this.TRAP_SURFACE_CLASS : 'profiles-maintenance';
            box.setAttribute('role', blocking ? 'alertdialog' : 'status');

            const inner = document.createElement('div');
            inner.className = 'profiles-maintenance-inner';

            const title = document.createElement('div');
            title.className = 'profiles-maintenance-title';
            title.textContent = admin ? t('maintenance.adminTitle') : t('maintenance.title');
            inner.appendChild(title);

            const text = document.createElement('div');
            text.className = 'profiles-maintenance-text';
            text.textContent = message;
            inner.appendChild(text);

            if (back) {
                const when = document.createElement('div');
                when.className = 'profiles-maintenance-when';
                when.textContent = back;
                inner.appendChild(when);
            }

            // Asked again while the cover is up, on a timer the cover owns and stops when it
            // closes. Never on checkRoute: that runs twice a second on every page, and this
            // is needed only while a notice is showing.
            let status = null;
            if (blocking) {
                const again = document.createElement('button');
                again.type = 'button';
                again.className = 'profiles-btn btn-primary focusable';
                again.textContent = t('maintenance.checkAgain');
                again.addEventListener('click', () => this._recheckMaintenance(box, blocking, status));
                inner.appendChild(again);
                status = document.createElement('div');
                status.className = 'profiles-maintenance-when';
                status.setAttribute('role', 'status');

                const out = document.createElement('button');
                out.type = 'button';
                out.className = 'profiles-btn btn-secondary focusable';
                out.textContent = t('maintenance.signOut');
                out.addEventListener('click', () => {
                    Promise.resolve(ApiClient.logout()).then(() => window.location.reload(), () => window.location.reload());
                });
                inner.appendChild(out);
                inner.appendChild(status);
            }

            box.appendChild(inner);
            document.body.appendChild(box);
            if (blocking) {
                const btn = box.querySelector('button');
                if (btn) btn.focus();
            }
            this._stopMaintenanceTimer();
            this._maintenanceTimer = setInterval(() => {
                if (document.getElementById('profiles-maintenance') !== box) { this._stopMaintenanceTimer(); return; }
                this._recheckMaintenance(box, blocking, null);
            }, 30000);
        },

        _stopMaintenanceTimer: function () {
            if (this._maintenanceTimer) clearInterval(this._maintenanceTimer);
            this._maintenanceTimer = null;
        },

        /// Asks /info again, past the one-minute cache. Maintenance over takes the notice
        /// down; an answer that cannot be read leaves it up, since "unknown" is not "open".
        /// A cover is followed by a reload: everything behind it was refused while it was up.
        _recheckMaintenance: function (box, blocking, status) {
            this._serverInfo = null;
            this.serverInfo().then(info => {
                if (document.getElementById('profiles-maintenance') !== box) { this._stopMaintenanceTimer(); return; }
                if (!info) return;
                if (info.maintenance || info.Maintenance) {
                    if (status) status.textContent = t('maintenance.stillClosed');
                    return;
                }
                this._stopMaintenanceTimer();
                box.remove();
                if (blocking) window.location.reload();
            });
        },

        _announcementSeverity: function (a) {
            const s = String(a.severity || a.Severity || '').toLowerCase();
            return (s === 'warning' || s === 'critical') ? s : 'info';
        },

        _announcementKey: function (a) {
            const text = String(a.message || a.Message || '');
            let h = 5381;
            for (let i = 0; i < text.length; i++) h = ((h * 33) ^ text.charCodeAt(i)) >>> 0;
            return this.normalizeGuid(a.id || a.Id || '') + ':' + h.toString(36);
        },

        _readDismissedAnnouncements: function () {
            try {
                const v = JSON.parse(localStorage.getItem('jpf-dismissed-announcements') || '[]');
                return Array.isArray(v) ? v : [];
            } catch (e) { return []; }
        },

        _dismissAnnouncement: function (a) {
            const list = this._readDismissedAnnouncements();
            list.push(this._announcementKey(a));
            // Fifty is the server's own limit, so nothing still showing is ever pushed out.
            try { localStorage.setItem('jpf-dismissed-announcements', JSON.stringify(list.slice(-50))); } catch (e) { /* private mode */ }
        },

        /// Shrinks the profile tiles, only as far as needed, until the whole gate fits on
        /// the screen without scrolling (issue #31).
        ///
        /// The tiles were a fixed size per device class — 280px on a television — so four
        /// profiles on a 1080p set ran 247px past the bottom, and a remote had to scroll to
        /// reach half the household. Two profiles still get the full size; more get smaller.
        ///
        /// Measured rather than calculated. How many tiles share a row depends on the names
        /// under them, the sections a linked Bonfire adds, manage mode's extra controls and
        /// the theme's fonts, and a formula would have to know all of that. Instead the
        /// tile size is a custom property, and this halves the gap between "fits" and
        /// "too big" a few times, reading the overlay's scroll height after each try. Only
        /// ever runs when the gate is drawn or the window is resized — never on the route
        /// poll, which is why it is not called from checkRoute.
        ///
        /// Below the floor it stops shrinking and lets the gate scroll: on a phone with
        /// nine profiles, scrolling is better than tiles too small to read.
        fitGateToScreen: function (overlay) {
            if (!overlay || !overlay.querySelector) return;

            // A television rotating its UI, a desktop window being resized: fit again. Bound
            // once, and finds whatever gate is on screen when it fires.
            if (!this._gateResizeBound && typeof window !== 'undefined' && window.addEventListener) {
                this._gateResizeBound = true;
                let pending = null;
                window.addEventListener('resize', () => {
                    if (pending) clearTimeout(pending);
                    pending = setTimeout(() => {
                        pending = null;
                        const open = document.getElementById('profiles-gate-overlay');
                        if (open && open.querySelector('.profiles-grid')) this.fitGateToScreen(open);
                    }, 150);
                });
            }

            // The class and the size go on the gate's own content, not on the overlay. The
            // PIN, create and settings screens are drawn into the same overlay and share
            // .profiles-title; a class left on the overlay would carry over to them. On the
            // content it is replaced along with the markup it was measured for.
            const grid = overlay.querySelector('.profiles-grid');
            const content = grid && grid.closest('.profiles-modal-content');
            if (!content) return;
            const fits = () => overlay.scrollHeight <= overlay.clientHeight + 1;

            content.classList.remove('jpf-fitted');
            content.style.removeProperty('--jpf-tile');
            if (fits()) return;

            const sample = grid.querySelector('.profile-avatar-container');
            if (!sample) return;
            const natural = Math.round(sample.getBoundingClientRect().width);
            if (!natural) return;

            const isTv = document.documentElement.classList.contains('jpf-tv');
            const floor = Math.min(natural, isTv ? 120 : 88);
            const tryTile = (px) => { content.style.setProperty('--jpf-tile', px + 'px'); return fits(); };

            content.classList.add('jpf-fitted');
            let lo = floor;
            let hi = natural;
            if (!tryTile(lo)) return;             // already at the floor; leave it scrolling
            for (let i = 0; i < 8 && hi - lo > 2; i++) {
                const mid = Math.floor((lo + hi) / 2);
                if (tryTile(mid)) lo = mid; else hi = mid;
            }
            tryTile(lo);
        },

        attachOverlayInteractions: function (overlay, profiles) {
            // Support D-pad Enter/Space selection on focused profile cards
            overlay.addEventListener('keydown', (e) => {
                if ((e.key === 'Enter' || e.key === ' ') && e.target.classList.contains('profile-card')) {
                    // Prevent accidental selection from propagated keydown events during login
                    if (Date.now() - (this._overlayMountTime || 0) < 350) {
                        e.preventDefault();
                        return;
                    }
                    e.preventDefault();
                    e.target.click();
                }
            });

            // Card selection logic
            overlay.querySelectorAll('.profile-card:not(.action-add-profile)').forEach(card => {
                card.addEventListener('click', (e) => {
                    e.stopPropagation();
                    if (this._switchLock) return;
                    const profileId = card.getAttribute('data-id');
                    const profile = profiles.find(p => this.normalizeGuid(p.profileUserId) === this.normalizeGuid(profileId));
                    if (!profile) return;

                    const open = () => {
                        if (profile.requiresPin) {
                            this.promptPinEntry(profileId);
                        } else {
                            this.executeProfileSwitch(profileId, null);
                        }
                    };
                    if (this.isManageMode && profile.isLinked) {
                        this.showAddedAccount(profile);
                    } else if (this.isManageMode) {
                        this.showEditProfileModal(profile);
                    } else if (profile.viewing && !profile.viewing.allowed) {
                        // Out of time: say so first. Browsing is still allowed, so the
                        // profile opens anyway if they carry on.
                        this.showTimeUp(profile, open);
                    } else {
                        open();
                    }
                });
            });

            // "Add Profile" action. The card only exists in Manage Profiles, which has
            // already asked for the main PIN. It used to ask again, after reading the
            // plugin's configuration, which only an administrator may read: for everyone
            // else that failed and the form opened with no PIN at all.
            const addCard = overlay.querySelector('.action-add-profile');
            if (addCard) {
                addCard.addEventListener('click', () => {
                    if (this._switchLock) return;
                    this.withMainPin(profiles, () => this.showAddProfileModal());
                });
            }

            // Settings: everything that is not a profile. They are the main account's, so
            // the main account opens them, with its PIN.
            const settingsBtn = overlay.querySelector('#profiles-settings-btn');
            if (settingsBtn) {
                settingsBtn.addEventListener('click', () => {
                    if (this._switchLock) return;
                    const masterProfile = profiles.find(p => p.isMaster && !p.isBonfire);
                    this._syncMasterFromProfiles(masterProfile);
                    if (!this.isSignedInAsMaster()) {
                        this.showAlert(t('settings.title'),
                            t('settings.needsMain', { name: escapeHtml(masterProfile ? masterProfile.profileName : '') }));
                        return;
                    }
                    this.withMainPin(profiles, () => this.showSettingsMenu());
                });
            }

            // Emergency disable entry point for clients with no keyboard shortcut.
            const panicLink = overlay.querySelector('#profiles-panic-link');
            if (panicLink) {
                panicLink.addEventListener('click', (e) => {
                    e.preventDefault();
                    this.showPanicPrompt();
                });
                panicLink.addEventListener('keydown', (e) => {
                    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); panicLink.click(); }
                });
                this.applyPanicLinkVisibility();
            }

            // Back out of a switcher the user opened on purpose, returning to the profile
            // they were already using. Only present when there is something to return to —
            // the startup gate is a required choice and has no Cancel.
            const resumeBtn = overlay.querySelector('#profiles-resume-btn');
            if (resumeBtn) {
                resumeBtn.addEventListener('click', () => {
                    if (this._switchLock) return;
                    this.resumePreviousProfile();
                });
            }

            // "Manage Profiles" / "Done" toggle
            const manageBtn = document.getElementById('profiles-toggle-manage-btn');
            if (manageBtn) {
                manageBtn.addEventListener('click', () => {
                    if (this._switchLock) return;
                    if (this.isManageMode) {
                        this.isManageMode = false;
                        this.masterPin = null;
                        this.renderOverlayContent(overlay, profiles);
                    } else {
                        const masterProfile = profiles.find(p => p.isMaster && !p.isBonfire);
                        // isSignedInAsMaster compares against the stored id; without it the
                        // master was told to open the master.
                        this._syncMasterFromProfiles(masterProfile);

                        // Managing profiles is the master account's to do, and the server
                        // refuses it from any other. It used to work from a profile only
                        // because the master's token was kept in the browser; now a profile
                        // opens the master first, like any other switch.
                        if (!this.isSignedInAsMaster()) {
                            this.showAlert(t('switcher.manageTitle'),
                                t('switcher.manageNeedsMaster', { name: escapeHtml(masterProfile ? masterProfile.profileName : '') }));
                            return;
                        }

                        this.withMainPin(profiles, () => {
                            this.isManageMode = true;
                            this.renderOverlayContent(overlay, profiles);
                        });
                    }
                });
            }
        },

        /// How long the PIN box waits after the last digit before checking what was typed.
        PIN_PAUSE_MS: 1500,

        /// Checks a PIN box by itself once the typing stops, so a right PIN opens without
        /// Enter. Shared by the profile PIN and the main PIN.
        ///
        /// Both boxes used to POST verify-pin on every keystroke from the fourth digit on.
        /// The server counts every one of those as a guess — it has to, or a "silent" check
        /// would be a free way to try PINs — so an eight-digit PIN typed correctly spent four
        /// wrong prefixes first, and one typo on top of that reached the lockout: a "PIN is
        /// locked" activity line and a PinLocked email for somebody who knew their PIN, and
        /// a fifth of the account's twenty guesses a day gone. Now it is one check per pause
        /// (or at once when the box is full), never the same value twice, and none at all
        /// after a 429.
        ///
        /// opts: send(pin) → fetch promise; busy() → true while a switch is underway;
        /// onRight(pin); onLocked(serverText).
        /// Returns { input, cancel, hold, takeRefusal(pin) }. takeRefusal hands back the server's
        /// answer if this exact value was just refused, so Enter can show it rather than
        /// spend another guess on the same digits; it is used once, so retyping asks again.
        _pinAutoCheck: function (pinInput, opts) {
            const refused = new Map();
            let timer = null;
            let stopped = false;
            let pending = null;

            const check = () => {
                timer = null;
                const pin = pinInput.value;
                if (stopped || pin.length < 4 || opts.busy() || pin === pending || refused.has(pin)) return;
                pending = pin;
                opts.send(pin)
                    .then(res => {
                        if (pending === pin) pending = null;
                        if (res.status === 401) { this.handleSessionExpired(); return null; }
                        if (res.ok) {
                            // Typed on past it while the answer was coming: the box no longer
                            // holds a right PIN, and Enter will send what it does hold.
                            if (!stopped && pinInput.value === pin) opts.onRight(pin);
                            return null;
                        }
                        return res.text().then(text => {
                            const body = (text || '').trim();
                            if (res.status === 429) {
                                // Locked. Every further check would be refused and counted
                                // nowhere useful; say so and stop.
                                stopped = true;
                                if (timer) { clearTimeout(timer); timer = null; }
                                opts.onLocked(body);
                                return;
                            }
                            refused.set(pin, body);
                        });
                    })
                    .catch(() => {
                        // Network trouble: nothing learned. Enter still works.
                        if (pending === pin) pending = null;
                    });
            };

            const full = pinInput.maxLength > 0 ? pinInput.maxLength : 8;
            return {
                input: () => {
                    if (timer) { clearTimeout(timer); timer = null; }
                    if (stopped) return;
                    const n = pinInput.value.length;
                    if (n < 4) return;
                    // Nothing more can be typed, so there is nothing to wait for.
                    if (n >= full) check();
                    else timer = setTimeout(check, this.PIN_PAUSE_MS);
                },
                cancel: () => {
                    if (timer) { clearTimeout(timer); timer = null; }
                    stopped = true;
                },
                takeRefusal: (pin) => {
                    if (!refused.has(pin)) return undefined;
                    const body = refused.get(pin);
                    refused.delete(pin);
                    return body;
                },
                // Enter is the person saying they are done: the pending pause has no more
                // to tell us.
                hold: () => { if (timer) { clearTimeout(timer); timer = null; } }
            };
        },

        promptPinEntry: function (profileId) {
            this.beginNavigation();
            const content = document.querySelector('.profiles-modal-content');
            content.innerHTML = `
                <h1 class="profiles-title">${t('pin.enterProfilePin')}</h1>
                <div class="pin-entry-container">
                    <input type="text" id="profile-pin-input" maxlength="8" pattern="[0-9]*" inputmode="numeric" placeholder="${t('pin.numberKeys')}" aria-label="${t('pin.numberKeys')}" autocomplete="one-time-code" data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true" autofocus />
                    <div id="pin-error-msg" style="display:none; color:var(--jpf-danger); font-size:0.9rem; font-weight:600; text-align:center; margin-top:-0.5rem;"></div>
                    <div class="pin-actions jpf-actions">
                        <button id="pin-cancel-btn" class="profiles-btn btn-secondary">${t('common.back')}</button>
                        <button id="pin-submit-btn" class="profiles-btn btn-primary">${t('common.unlock')}</button>
                    </div>
                    <button type="button" id="pin-forgot" class="jpf-text-link" hidden>${t('pinReset.forgot')}</button>
                </div>
            `;

            const pinInput = document.getElementById('profile-pin-input');
            const errorMsg = document.getElementById('pin-error-msg');
            pinInput.focus();
            // The main account's own PIN only: a profile's is reset by the main account.
            const own = (this.currentProfiles || []).find(p => p.isMaster && !p.isBonfire);
            if (own && this.normalizeGuid(own.profileUserId) === this.normalizeGuid(profileId)) {
                this._offerPinReset(content, () => this.promptPinEntry(profileId),
                    (pin) => this.executeProfileSwitch(profileId, pin, () => this.promptPinEntry(profileId)));
            }

            // Prevents a second switch from firing if one is already in progress.
            let switchInProgress = false;

            const showPinError = (msg) => {
                switchInProgress = false;
                pinInput.style.borderColor = 'var(--jpf-danger)';
                pinInput.style.boxShadow = '0 0 15px rgba(255,107,107,0.5)';
                errorMsg.textContent = msg || t('pin.incorrectPin');
                errorMsg.style.display = 'block';
                pinInput.value = '';
                // setTimeout avoids re-triggering the 'input' clearError listener on refocus
                setTimeout(() => pinInput.focus(), 0);
            };

            const clearError = () => {
                pinInput.style.borderColor = '';
                pinInput.style.boxShadow = '';
                errorMsg.style.display = 'none';
                errorMsg.textContent = '';
            };

            // Checked once the typing pauses — see _pinAutoCheck for why not per keystroke.
            // A wrong answer shows nothing yet: the person may still be typing.
            const auto = this._pinAutoCheck(pinInput, {
                send: (pin) => {
                    const masterState = this.readMasterState();
                    if (!masterState) return Promise.reject(new Error('no master'));
                    return fetch(ApiClient.getUrl('plugins/profiles/verify-pin'), {
                        method: 'POST',
                        headers: this.jsonHeaders(masterState.token),
                        body: JSON.stringify({ profileId: profileId, pin: pin })
                    });
                },
                busy: () => switchInProgress || this._switchLock,
                onRight: (pin) => {
                    if (switchInProgress || this._switchLock) return;
                    switchInProgress = true;
                    this.executeProfileSwitch(profileId, pin, () => {
                        // Verify said OK but switch failed (edge case) — reset silently
                        switchInProgress = false;
                    });
                },
                onLocked: (msg) => showPinError(msg || t('pin.incorrectPin'))
            });

            pinInput.addEventListener('input', () => {
                clearError();
                auto.input();
            });

            // Manual submit — only place where we show an error on wrong PIN
            const submitPin = () => {
                if (switchInProgress || this._switchLock) return;
                auto.hold();
                const pin = pinInput.value;
                if (!pin) return;
                // Just checked and refused: the same digits again would only spend another
                // of the account's guesses to hear the same answer.
                const refusal = auto.takeRefusal(pin);
                if (refusal !== undefined) { showPinError(refusal || t('pin.incorrectPin')); return; }
                switchInProgress = true;
                this.executeProfileSwitch(profileId, pin, (msg) => {
                    switchInProgress = false;
                    showPinError(msg);
                });
            };

            document.getElementById('pin-submit-btn').addEventListener('click', submitPin);
            pinInput.addEventListener('keydown', (e) => {
                if (e.key === 'Enter') submitPin();
            });

            document.getElementById('pin-cancel-btn').addEventListener('click', () => {
                auto.cancel();
                this.isManageMode = false;
                this.showProfileOverlay(this.currentProfiles);
            });
        },

        /// The server's /info, read at most once a minute. Null when it can't be read.
        serverInfo: function () {
            const now = Date.now();
            if (this._serverInfo && now - this._serverInfo.at < 60000) return this._serverInfo.promise;
            const promise = fetch(ApiClient.getUrl('plugins/profiles/info'), { cache: 'no-store' })
                .then(res => res.ok ? res.json() : null)
                .catch(() => null);
            this._serverInfo = { at: now, promise: promise };
            return promise;
        },

        /// Runs then() once the main PIN is known: asks for it if the account has one and
        /// it hasn't been typed yet. With none set and the administrator requiring one,
        /// sets one first.
        ///
        /// Decided by hasPin, never requiresPin. requiresPin answers "does opening the
        /// account ask", which is false on the home network with the bypass on, or with
        /// asking turned off; the server still wants the PIN to change anything. Going by
        /// requiresPin, Manage Profiles opened without asking and every save then failed
        /// as a wrong PIN.
        withMainPin: function (profiles, then) {
            const main = (profiles || this.currentProfiles || []).find(p => p.isMaster && !p.isBonfire);
            if (main && main.hasPin) {
                if (this.masterPin) { then(); return; }
                this.promptMasterPinEntry('manage', then);
                return;
            }
            this.serverInfo().then(info => {
                const features = info && (info.features || info.Features);
                const required = !!(features && (features.requireMainPin || features.RequireMainPin));
                if (required) this.promptSetMainPin(profiles, true, then);
                else then();
            });
        },

        /// Asks the main account to set a PIN. Required: there is no "Not now", and Cancel
        /// goes back to the profiles. Offered: "Not now" carries on without one.
        ///
        /// `setup` is first-time setup's: `{ onSkip }`, where a null onSkip is a step the
        /// administrator made required, so there is no button at all.
        promptSetMainPin: function (profiles, required, done, setup, noteKey) {
            this.beginNavigation();
            const content = document.querySelector('.profiles-modal-content');
            const masterState = this.readMasterState();
            if (!content || !masterState) { done(); return; }

            content.innerHTML = `
                <h1 class="profiles-title">${t('mainPin.setTitle')}</h1>
                <div class="pin-entry-container">
                    ${setup ? this._onboardingStepLine() : ''}
                    <p class="profiles-pin-note">${noteKey ? t(noteKey) : required && !setup ? t('mainPin.requiredBody') : t('mainPin.setBody')}</p>
                    <input type="text" id="main-pin-new" maxlength="8" pattern="[0-9]*" inputmode="numeric" placeholder="${t('pin.numberKeys')}" aria-label="${t('pin.numberKeys')}" autocomplete="one-time-code" data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true" />
                    <div id="main-pin-new-error" class="profiles-pin-error" role="alert" hidden></div>
                    <div class="pin-actions jpf-actions">
                        ${setup && !setup.onSkip ? '' : `<button id="main-pin-skip" class="profiles-btn btn-secondary">${required && !setup ? t('common.cancel') : t('mainPin.notNow')}</button>`}
                        <button id="main-pin-save" class="profiles-btn btn-primary">${t('mainPin.set')}</button>
                    </div>
                </div>
            `;

            const input = content.querySelector('#main-pin-new');
            const error = content.querySelector('#main-pin-new-error');
            const fail = (message) => {
                error.textContent = message;
                error.hidden = false;
                input.value = '';
                input.focus();
            };
            input.focus();

            const save = () => {
                const pin = input.value;
                if (!/^\d{4,8}$/.test(pin)) { fail(t('profileForm.pinLengthCreate')); return; }
                fetch(ApiClient.getUrl('plugins/profiles/account'), {
                    method: 'POST',
                    headers: this.jsonHeaders(masterState.token),
                    body: JSON.stringify({ pin: pin })
                })
                .then(res => {
                    if (res.status === 401) { this.handleSessionExpired(); return null; }
                    if (!res.ok) return res.text().then(text => { fail(text || t('errors.error')); return null; });
                    (profiles || []).concat(this.currentProfiles || []).forEach(p => {
                        if (p.isMaster && !p.isBonfire) p.hasPin = true;
                    });
                    this.masterPin = pin;
                    done();
                    return null;
                })
                .catch(err => fail(err.message));
            };

            content.querySelector('#main-pin-save').addEventListener('click', save);
            input.addEventListener('keydown', (e) => { if (e.key === 'Enter') save(); });
            const skipBtn = content.querySelector('#main-pin-skip');
            if (skipBtn) skipBtn.addEventListener('click', () => {
                if (setup) setup.onSkip();
                else if (required) this.showProfileOverlay(this.currentProfiles);
                else done();
            });
        },

        promptMasterPinEntry: function (actionType, callback) {
            this.beginNavigation();
            const masterProfile = this.currentProfiles.find(p => p.isMaster && !p.isBonfire);
            if (!masterProfile) return;

            const content = document.querySelector('.profiles-modal-content');
            content.innerHTML = `
                <h1 class="profiles-title">${t('pin.enterMasterPin')}</h1>
                <div class="pin-entry-container">
                    <input type="text" id="master-pin-input" maxlength="8" pattern="[0-9]*" inputmode="numeric" placeholder="${t('pin.numberKeys')}" aria-label="${t('pin.numberKeys')}" autocomplete="one-time-code" data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true" autofocus />
                    <div id="master-pin-error-msg" style="display:none; color:var(--jpf-danger); font-size:0.9rem; font-weight:600; text-align:center; margin-top:-0.5rem;"></div>
                    <div class="pin-actions jpf-actions">
                        <button id="master-pin-cancel-btn" class="profiles-btn btn-secondary">${t('common.cancel')}</button>
                        <button id="master-pin-submit-btn" class="profiles-btn btn-primary">${t('common.submit')}</button>
                    </div>
                    <button type="button" id="pin-forgot" class="jpf-text-link" hidden>${t('pinReset.forgot')}</button>
                </div>
            `;

            const pinInput = document.getElementById('master-pin-input');
            const errorMsg = document.getElementById('master-pin-error-msg');
            pinInput.focus();
            this._offerPinReset(content, () => this.promptMasterPinEntry(actionType, callback), () => callback());

            let verified = false; // prevent callback firing more than once

            const showPinError = (msg) => {
                verified = false;
                pinInput.style.borderColor = 'var(--jpf-danger)';
                pinInput.style.boxShadow = '0 0 15px rgba(255,107,107,0.5)';
                errorMsg.textContent = msg || t('pin.incorrectPin');
                errorMsg.style.display = 'block';
                pinInput.value = '';
                setTimeout(() => pinInput.focus(), 0);
            };

            const clearError = () => {
                pinInput.style.borderColor = '';
                pinInput.style.boxShadow = '';
                errorMsg.style.display = 'none';
                errorMsg.textContent = '';
            };

            // Manual submit — only place where we show an error on wrong master PIN
            let verifyInProgress = false;

            // Checked once the typing pauses, not per keystroke — see _pinAutoCheck. Every
            // prefix of the main PIN was a counted guess at the account's own PIN.
            const auto = this._pinAutoCheck(pinInput, {
                send: (pin) => {
                    const masterState = this.readMasterState();
                    if (!masterState) return Promise.reject(new Error('no master'));
                    return fetch(ApiClient.getUrl('plugins/profiles/account/verify-pin'), {
                        method: 'POST',
                        headers: this.jsonHeaders(masterState.token),
                        body: JSON.stringify({ pin: pin })
                    });
                },
                busy: () => verified || verifyInProgress,
                onRight: (pin) => {
                    if (verified || verifyInProgress) return;
                    verified = true;
                    this.masterPin = pin;
                    callback();
                },
                onLocked: (msg) => showPinError(msg || t('pin.incorrectMasterPin'))
            });

            pinInput.addEventListener('input', () => {
                clearError();
                auto.input();
            });

            const submitPin = () => {
                if (verifyInProgress || verified) return;
                auto.hold();
                const pin = pinInput.value;
                if (!pin) return;
                // Just checked and refused: asking again spends another guess for the
                // same answer.
                const refusal = auto.takeRefusal(pin);
                if (refusal !== undefined) { showPinError(refusal || t('pin.incorrectMasterPin')); return; }
                const masterState = this.readMasterState();
                if (!masterState) return;

                verifyInProgress = true;
                fetch(ApiClient.getUrl('plugins/profiles/account/verify-pin'), {
                    method: 'POST',
                    headers: this.jsonHeaders(masterState.token),
                    body: JSON.stringify({ pin: pin })
                })
                .then(res => {
                    verifyInProgress = false;
                    if (res.status === 401) {
                        this.handleSessionExpired();
                        throw new Error('Session expired');
                    }
                    if (!res.ok) {
                        return res.text().then(text => {
                            throw new Error(text || t('pin.incorrectMasterPin'));
                        });
                    }
                    this.masterPin = pin;
                    verified = true;
                    callback();
                })
                .catch(err => {
                    verifyInProgress = false;
                    if (err.message !== 'Session expired') {
                        showPinError(err.message || t('pin.incorrectMasterPin'));
                    }
                });
            };

            document.getElementById('master-pin-submit-btn').addEventListener('click', submitPin);
            pinInput.addEventListener('keydown', (e) => {
                if (e.key === 'Enter') submitPin();
            });

            document.getElementById('master-pin-cancel-btn').addEventListener('click', () => {
                auto.cancel();
                this.showProfileOverlay(this.currentProfiles);
            });
        },

        /// A field from a server reply in either casing: o.sentTo or o.SentTo.
        _field: function (o, k) {
            if (!o) return undefined;
            return o[k] !== undefined ? o[k] : o[k.charAt(0).toUpperCase() + k.slice(1)];
        },

        /// One call to an /account/email or /account/pin-reset route as the main account.
        /// No body is a GET. Resolves the reply's JSON ({} when there is none); rejects
        /// with the server's words.
        ///
        /// The language goes with every POST: it is what the mail is written in.
        _emailCall: function (path, body) {
            const masterState = this.readMasterState();
            const token = masterState ? masterState.token : ApiClient.accessToken();
            const init = body === undefined
                ? { cache: 'no-store', headers: this.manageHeaders(token, false) }
                : {
                    method: 'POST',
                    headers: this.jsonHeaders(token),
                    body: JSON.stringify(Object.assign({ masterPin: this.masterPin || null, language: activeLocale || '' }, body))
                };
            return fetch(ApiClient.getUrl('plugins/profiles/' + path), init).then(res => {
                if (res.status === 401) { this.handleSessionExpired(); throw new Error(t('errors.error')); }
                if (!res.ok) return res.text().then(text => { throw new Error(text || t('errors.error')); });
                return res.text().then(text => {
                    try { return text ? JSON.parse(text) : {}; } catch (e) { return {}; }
                });
            });
        },

        /// Shows "Forgot PIN?" on a main-PIN prompt when the server can mail this account a
        /// code. Asked of /me once, when the prompt opens.
        ///
        /// back() redraws the prompt; done(pin) carries on with the new PIN, so a reset
        /// lands where the person was going instead of asking for the PIN just set.
        _offerPinReset: function (content, back, done) {
            const link = content.querySelector('#pin-forgot');
            const masterState = this.readMasterState();
            if (!link || !masterState) return;
            fetch(ApiClient.getUrl('plugins/profiles/me'), {
                cache: 'no-store',
                headers: this.getAuthHeaders(masterState.token)
            })
            .then(res => res.ok ? res.json() : null)
            .then(me => {
                if (this._field(me, 'pinResetByEmail') !== true || !document.body.contains(link)) return;
                link.hidden = false;
                link.addEventListener('click', () => this.showPinReset(back, done));
            })
            .catch(() => { /* no link; the prompt works as before */ });
        },

        /// Forgot PIN: a code to the main account's confirmed address, then a new PIN.
        /// The code is sent as the screen opens, since sending it is all the screen is for.
        showPinReset: function (back, done) {
            this.beginNavigation();
            const content = document.querySelector('.profiles-modal-content');
            if (!content) return;
            content.innerHTML = `
                <h1 class="profiles-title">${t('pinReset.title')}</h1>
                <div class="pin-entry-container">
                    <p class="profiles-pin-note" id="pin-reset-note"></p>
                    <input type="text" id="pin-reset-code" maxlength="7" inputmode="numeric" placeholder="${t('accountEmail.code')}" aria-label="${t('accountEmail.code')}" autocomplete="one-time-code" data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true" />
                    <input type="text" id="pin-reset-new" maxlength="8" pattern="[0-9]*" inputmode="numeric" placeholder="${t('pinReset.newPin')}" aria-label="${t('pinReset.newPin')}" autocomplete="off" data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true" />
                    <div id="pin-reset-error" class="profiles-pin-error" role="alert" hidden></div>
                    <div class="pin-actions jpf-actions">
                        <button id="pin-reset-back" class="profiles-btn btn-secondary">${t('common.back')}</button>
                        <button id="pin-reset-save" class="profiles-btn btn-primary">${t('mainPin.set')}</button>
                    </div>
                    <button type="button" id="pin-reset-again" class="jpf-text-link">${t('pinReset.sendAgain')}</button>
                </div>
            `;
            const note = content.querySelector('#pin-reset-note');
            const error = content.querySelector('#pin-reset-error');
            const code = content.querySelector('#pin-reset-code');
            const pin = content.querySelector('#pin-reset-new');
            const fail = (message) => { error.textContent = message; error.hidden = false; };

            const send = () => {
                error.hidden = true;
                this._emailCall('account/pin-reset/send', {})
                    .then(reply => {
                        note.textContent = t('pinReset.sentTo', { address: this._field(reply, 'sentTo') || '' });
                        code.focus();
                    })
                    .catch(err => fail(err.message));
            };

            let busy = false;
            const save = () => {
                const typed = code.value.replace(/\s/g, '');
                if (!/^\d{6}$/.test(typed)) { fail(t('accountEmail.codeInvalid')); code.focus(); return; }
                if (!/^\d{4,8}$/.test(pin.value)) { fail(t('profileForm.pinLengthCreate')); pin.focus(); return; }
                if (busy) return;
                busy = true;
                const newPin = pin.value;
                this._emailCall('account/pin-reset', { code: typed, newPin: newPin })
                    .then(() => {
                        this.masterPin = newPin;
                        (this.currentProfiles || []).forEach(p => { if (p.isMaster && !p.isBonfire) p.hasPin = true; });
                        done(newPin);
                    })
                    .catch(err => { busy = false; fail(err.message); });
            };

            content.querySelector('#pin-reset-save').addEventListener('click', save);
            pin.addEventListener('keydown', (e) => { if (e.key === 'Enter') save(); });
            code.addEventListener('keydown', (e) => { if (e.key === 'Enter') pin.focus(); });
            content.querySelector('#pin-reset-back').addEventListener('click', back);
            content.querySelector('#pin-reset-again').addEventListener('click', send);
            send();
        },

        /// The Email section of Your account. Draws nothing until the server says email
        /// works, so a server without it never shows an empty section.
        ///
        /// Every route answers with the whole state, so each action just redraws from the
        /// reply: the address, a code on its way, or nothing yet.
        _bindAccountEmail: function (slot) {
            if (!slot) return;
            let data = null;
            let editing = false;
            const field = (k) => this._field(data, k);
            const say = (text, bad) => {
                const status = slot.querySelector('#account-email-status');
                if (!status) return;
                status.textContent = text || '';
                status.style.color = bad ? 'var(--jpf-danger)' : '';
            };
            const run = (path, body, done) => this._emailCall(path, body)
                .then(reply => { data = reply; editing = false; draw(); if (done) say(done); })
                .catch(err => say(err.message, true));

            const draw = () => {
                if (!document.body.contains(slot)) return;
                // Email off on this server: say so and where to turn it on, instead of an
                // account page with no hint that a forgotten PIN could ever be reset (P2-607).
                if (field('available') !== true) {
                    slot.innerHTML = this.renderSection('mail', t('accountEmail.title'), t('accountEmail.subtitle'),
                        `<div class="form-hint" id="account-email-off">${t('account.emailOff')}</div>`);
                    return;
                }
                const address = field('address') || '';
                const pending = field('pendingAddress') || '';
                const box = (id, on, label) => `
                    <div class="form-group">
                        <label class="library-check-label jpf-email-check">
                            <input type="checkbox" id="${id}" ${on ? 'checked' : ''} />
                            <span>${label}</span>
                        </label>
                    </div>`;
                const noFill = 'data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true"';
                let body;
                if (pending && !editing) {
                    body = `
                        <div class="form-group">
                            <label for="account-email-code">${t('accountEmail.code')}</label>
                            <div class="jpf-inline-row">
                                <input type="text" id="account-email-code" maxlength="7" inputmode="numeric" autocomplete="one-time-code" ${noFill} />
                                <button type="button" id="account-email-confirm" class="profiles-btn btn-primary">${t('accountEmail.confirm')}</button>
                            </div>
                            <div class="form-hint" id="account-email-sent"></div>
                            <button type="button" id="account-email-other" class="jpf-text-link">${t('accountEmail.otherAddress')}</button>
                        </div>`;
                } else if (address && !editing) {
                    body = `
                        <div class="form-group">
                            <label>${t('accountEmail.address')}</label>
                            <div class="jpf-inline-row">
                                <span class="jpf-email-address">${escapeHtml(address)}</span>
                                <button type="button" id="account-email-change" class="profiles-btn btn-secondary">${t('accountEmail.change')}</button>
                                <button type="button" id="account-email-remove" class="profiles-btn btn-secondary">${t('accountEmail.remove')}</button>
                            </div>
                            ${field('pinResetEnabled') === true ? `<div class="form-hint">${t('accountEmail.resetHint')}</div>` : ''}
                        </div>
                        ${box('account-email-lockout', field('notifyLockout') !== false, t('accountEmail.notifyLockout'))}
                        ${box('account-email-device', field('notifyNewDevice') !== false, t('accountEmail.notifyNewDevice'))}
                        ${box('account-email-asked', field('notifyTimeAsked') !== false, t('accountEmail.notifyTimeAsked'))}
                        ${box('account-email-limit', field('notifyLimitReached') === true, t('accountEmail.notifyLimitReached'))}`;
                } else {
                    body = `
                        <div class="form-group">
                            <label for="account-email-input">${t('accountEmail.address')}</label>
                            <div class="jpf-inline-row">
                                <input type="email" id="account-email-input" maxlength="254" autocomplete="email" value="${escapeHtml(address)}" />
                                <button type="button" id="account-email-send" class="profiles-btn btn-primary">${t('accountEmail.send')}</button>
                                ${address ? `<button type="button" id="account-email-cancel" class="profiles-btn btn-secondary">${t('common.cancel')}</button>` : ''}
                            </div>
                        </div>`;
                }
                slot.innerHTML = this.renderSection('mail', t('accountEmail.title'), t('accountEmail.subtitle'),
                    body + '<div id="account-email-status" class="form-hint" role="status" aria-live="polite"></div>');

                const q = (id) => slot.querySelector('#' + id);
                const sent = q('account-email-sent');
                if (sent) sent.textContent = t('accountEmail.sentTo', { address: pending });

                const input = q('account-email-input');
                if (input) {
                    const send = () => {
                        const typed = input.value.trim();
                        if (!/^[^\s@,;]+@[^\s@,;]+\.[^\s@,;]+$/.test(typed)) { say(t('accountEmail.invalid'), true); input.focus(); return; }
                        run('account/email', { address: typed });
                    };
                    q('account-email-send').addEventListener('click', send);
                    input.addEventListener('keydown', (e) => { if (e.key === 'Enter') send(); });
                    const cancel = q('account-email-cancel');
                    if (cancel) cancel.addEventListener('click', () => { editing = false; draw(); });
                }

                const codeInput = q('account-email-code');
                if (codeInput) {
                    const confirm = () => {
                        const typed = codeInput.value.replace(/\s/g, '');
                        if (!/^\d{6}$/.test(typed)) { say(t('accountEmail.codeInvalid'), true); codeInput.focus(); return; }
                        run('account/email/confirm', { code: typed }, t('accountEmail.saved'));
                    };
                    q('account-email-confirm').addEventListener('click', confirm);
                    codeInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') confirm(); });
                    q('account-email-other').addEventListener('click', () => { editing = true; draw(); });
                }

                const change = q('account-email-change');
                if (change) change.addEventListener('click', () => { editing = true; draw(); });
                const remove = q('account-email-remove');
                if (remove) remove.addEventListener('click', () => run('account/email/remove', {}, t('accountEmail.removed')));

                [['account-email-lockout', 'notifyLockout'], ['account-email-device', 'notifyNewDevice'],
                    ['account-email-asked', 'notifyTimeAsked'], ['account-email-limit', 'notifyLimitReached']].forEach(pair => {
                    const el = q(pair[0]);
                    if (!el) return;
                    el.addEventListener('change', () => {
                        const body = {};
                        body[pair[1]] = el.checked;
                        this._emailCall('account/email/notifications', body)
                            .then(reply => { data = reply; say(t('account.saved')); })
                            .catch(err => { el.checked = !el.checked; say(err.message, true); });
                    });
                });
                this.initTVCheckboxes(slot);
            };

            this._emailCall('account/email')
                .then(reply => { data = reply; draw(); })
                .catch(() => { /* no section; the rest of the page works without it */ });
        },

        // onError: optional callback(message) invoked on a failed switch.
        // Callers capture their own DOM references via closure so we never re-query
        // the DOM inside an async callback (which can race against overlay teardown).
        /// Puts the picker into "working on it" while a switch is in flight.
        ///
        /// The switch is a round trip to the server and then a full page reload. On a phone
        /// that is a couple of seconds during which nothing on screen changed at all, so the
        /// only reasonable reading was that the tap had missed — and the reporter kept
        /// tapping. _switchLock already made the extra taps harmless; it just never said so.
        setSwitchBusy: function (profileId, busy) {
            const overlay = document.getElementById('profiles-gate-overlay');
            if (overlay) {
                overlay.classList.toggle('is-switching', busy);
                if (busy) overlay.setAttribute('aria-busy', 'true');
                else overlay.removeAttribute('aria-busy');

                // Matched by normalised id rather than an attribute selector, so a profile
                // id can never be read as one.
                const wanted = busy ? this.normalizeGuid(profileId) : null;
                overlay.querySelectorAll('.profile-card').forEach(card => {
                    const isTarget = !!wanted
                        && this.normalizeGuid(card.getAttribute('data-id')) === wanted;
                    card.classList.toggle('is-switching', isTarget);
                });
            }

            // Started from a PIN prompt, the button is what the user is looking at — the
            // card behind it may not even be on screen.
            ['pin-submit-btn', 'master-pin-submit-btn'].forEach(id => {
                const btn = document.getElementById(id);
                if (btn) btn.classList.toggle('is-busy', busy);
            });
        },

        executeProfileSwitch: function (profileId, pin, onError) {
            if (this._switchLock) return;

            const apiClient = ApiClient;
            const masterState = this.readMasterState();
            if (!masterState) return;

            this._switchLock = true;
            this.setSwitchBusy(profileId, true);
            const url = apiClient.getUrl('plugins/profiles/switch');

            fetch(url, {
                method: 'POST',
                headers: this.jsonHeaders(masterState.token),
                body: JSON.stringify({ profileId: profileId, pin: pin })
            })
            .then(res => {
                if (!res.ok) {
                    return res.text().then(text => {
                        const body = (text || '').trim();
                        // Our own 401 (caller's token rejected) has an empty body. A 401 with a
                        // message came from something failing further in — the target profile,
                        // not the caller — so signing the master out would be wrong (issue #15).
                        if (res.status === 401 && !body) {
                            this._switchLock = false;
                            this.handleSessionExpired();
                            throw new Error('Session expired');
                        }
                        throw new Error(body || t('pin.incorrectPin'));
                    });
                }
                return res.json();
            })
            .then(data => {
                const activeProfileToken = data.activeProfileToken || data.ActiveProfileToken;
                const jellyfinUserId = data.jellyfinUserId || data.JellyfinUserId;

                // The session being left, ended once the new one is safely stored below.
                const leavingToken = masterState.token;

                // Before anything else is written, because this is the one write that has
                // to survive the reload. Without it the token lives only on this page's
                // ApiClient, and the reload signs back in as whoever was there before —
                // right name in the header, wrong account underneath. Bail here rather
                // than leave session keys describing a switch that will not happen.
                if (!this.updateStoredCredentials(activeProfileToken, jellyfinUserId)) {
                    this._switchLock = false;
                    this.setSwitchBusy(profileId, false);
                    this.showAlert(t('switcher.notSavedTitle'), t('switcher.notSavedBody'));
                    return;
                }

                this._sessionSet(this.config.activeSessionKey, activeProfileToken);

                // Nothing on this device should go on holding the account it just left —
                // above all when that was the master. Its token was the thing the switcher
                // is meant to stand in front of, and a copy kept "for later" is a copy anyone
                // at this browser can use. Coming back is a /switch like any other.
                if (leavingToken && leavingToken !== activeProfileToken) this._endSession(leavingToken);

                // Cached against the profile being switched into, before the reload, so the
                // next load starts with the right rules already in place.
                this.cacheLibraryArtwork(jellyfinUserId, data.libraryArtwork || data.LibraryArtwork || []);

                // A switch has happened; there is no longer an earlier profile to go back to.
                this._resumeState = null;

                const profile = this.currentProfiles.find(p => this.normalizeGuid(p.profileUserId) === this.normalizeGuid(profileId));
                if (profile) {
                    this._sessionSet('jellyfin_profiles_active_info', JSON.stringify({
                        name: profile.profileName,
                        color: profile.avatarColor,
                        initial: profile.avatarInitial,
                        profileImage: profile.profileImage || null,
                        transparent: !!profile.transparentAvatar
                    }));
                }

                apiClient.setAuthenticationInfo(activeProfileToken, jellyfinUserId);

                // The overlay is opaque and covers the viewport, so leaving it up is all it
                // takes to keep the old page out of sight — and it keeps the spinner on
                // screen for however long the reload costs. Blanking the document instead
                // meant a phone showed several seconds of black with nothing to say the tap
                // had registered. Only the background is calmed towards the next page.
                const leavingBg = this._captureLeavingBackground();
                // A second layer under the gate, in its colour, for as long as this page
                // lives. The gate is the cover while it stays up; this is for the moment
                // it does not — whatever takes it down before the navigation lands, the
                // old profile's home is not what shows. Only the reload failing takes it
                // down (_reloadFailed), so there is no backstop timer to race the reload.
                this._holdCoverForSwitch();
                const overlay = document.getElementById('profiles-gate-overlay');
                if (overlay) {
                    overlay.style.transition = 'background 0.2s ease';
                    overlay.style.background = leavingBg;
                } else {
                    // Menu-mode switch with no gate on screen: nothing is covering the app,
                    // so the old page does still have to be hidden before it repaints.
                    document.documentElement.style.cssText =
                        'opacity:0;background:' + leavingBg + ';color-scheme:'
                        + (this._isLightColour(leavingBg) ? 'light' : 'dark');
                }
                this._reloading = true;
                localStorage.setItem(this.config.switchingKey, '1');
                this.reloadAtHome();
            })
            .catch(err => {
                this._switchLock = false;
                this.setSwitchBusy(profileId, false);
                if (err.message === 'Session expired') return;
                if (typeof onError === 'function') {
                    // Caller has closed-over references to the DOM — no re-query needed
                    onError(err.message || t('pin.incorrectPin'));
                } else {
                    // Fallback: no PIN screen is currently shown (e.g. direct card tap without PIN prompt)
                    this.isManageMode = false;
                    this.interceptHomeAndShowProfiles();
                }
            });
        },

        // ── Avatar images ──────────────────────────────────────────────────────────
        // Two renderings are produced for every picture: a master used where the avatar is
        // shown large, and a thumbnail used by grids and switcher cards. Twenty full-size
        // avatars in a picker would otherwise decode about twenty megabytes of bitmap,
        // which is enough to stall the TV browsers this plugin supports.
        //
        // Both are produced here, in the browser, so the plugin needs no server-side image
        // library. It also means the accepted input set is "whatever this browser can
        // decode" while the stored output stays deliberately narrow.

        IMAGE_MASTER_SIZE: 512,
        IMAGE_THUMB_SIZE: 128,
        IMAGE_JPEG_QUALITY: 0.85,

        /// The sizes to render a picture at, from the server's picture quality setting.
        /// Anything missing or out of range falls back to the standard sizes above, which
        /// are what every picture was before the setting existed — and what a server older
        /// than it expects, since it will refuse anything over 2 MB.
        parseImageSpec: function (raw) {
            const pick = (k) => raw ? (raw[k] !== undefined ? raw[k] : raw[k.charAt(0).toUpperCase() + k.slice(1)]) : undefined;
            const size = Number(pick('masterSize'));
            const thumb = Number(pick('thumbSize'));
            const quality = Number(pick('jpegQuality'));
            return {
                masterSize: size >= 128 && size <= 4096 ? size : this.IMAGE_MASTER_SIZE,
                thumbSize: thumb >= 64 && thumb <= 1024 ? thumb : this.IMAGE_THUMB_SIZE,
                jpegQuality: quality > 0 && quality <= 1 ? quality : this.IMAGE_JPEG_QUALITY
            };
        },

        imageSpec: function () {
            return this._imageSpec || this.parseImageSpec(null);
        },

        /// Formats refused before we even try to decode, each with a reason worth showing.
        /// Everything else is handed to the browser: if it decodes, we can store it.
        _rejectImageFile: function (file) {
            const name = (file.name || '').toLowerCase();
            const type = (file.type || '').toLowerCase();

            // The common case by a distance: iPhones shoot HEIC by default, and only Safari
            // can decode it. Elsewhere the canvas load simply fails, so without this the
            // user gets silence and no idea why.
            if (type.includes('heic') || type.includes('heif') || /\.hei[cf]$/.test(name)) {
                return t('errors.heicUnsupported');
            }

            // SVG can carry script, and these files are served back from the server's own
            // origin. Not worth it for an avatar.
            if (type.includes('svg') || /\.svgz?$/.test(name)) {
                return t('errors.svgUnsupported');
            }

            if (file.size > 25 * 1024 * 1024) {
                return t('errors.imageTooLarge');
            }

            return null;
        },

        /// Reads a File into a decoded <img>. Rejects with a message fit to show the user.
        loadImageFromFile: function (file) {
            return new Promise((resolve, reject) => {
                const reason = this._rejectImageFile(file);
                if (reason) { reject(new Error(reason)); return; }

                const reader = new FileReader();
                reader.onerror = () => reject(new Error(t('errors.fileUnreadable')));
                reader.onload = (event) => {
                    const img = new Image();
                    img.onload = () => {
                        if (!img.width || !img.height) {
                            reject(new Error(t('errors.imageEmpty')));
                            return;
                        }
                        resolve(img);
                    };
                    // Reached for any format this browser cannot decode — including a HEIC
                    // that slipped past the check above with an empty MIME type.
                    img.onerror = () => reject(new Error(t('errors.imageFormatUnsupported')));
                    img.src = event.target.result;
                };
                reader.readAsDataURL(file);
            });
        },

        /// Loads a URL into an <img> for cropping. Used when someone picks an avatar from the
        /// administrator's library. The canvas has to stay untainted or the crop cannot be
        /// re-encoded, so anything off-origin is requested with CORS — that is the packaged
        /// clients, where the page origin is the app rather than the server.
        loadImageFromUrl: function (url) {
            const resolved = pluginUrl(url);
            return new Promise((resolve, reject) => {
                const img = new Image();
                img.onload = () => resolve(img);
                img.onerror = () => reject(new Error(t('errors.imageLoadFailed')));
                try {
                    if (new URL(resolved, window.location.href).origin !== window.location.origin) {
                        img.crossOrigin = 'anonymous';
                    }
                } catch (e) { /* unparseable — treat as same-origin */ }
                img.src = resolved;
            });
        },

        /// Smallest zoom at which the image still covers a square of `size`.
        _coverZoom: function (img, size) {
            return Math.max(size / img.width, size / img.height);
        },

        /// Keeps the image covering the viewport, so a crop can never include blank edges.
        _clampCrop: function (img, viewport, crop) {
            const w = img.width * crop.zoom;
            const h = img.height * crop.zoom;
            return {
                zoom: crop.zoom,
                x: Math.min(0, Math.max(viewport - w, crop.x)),
                y: Math.min(0, Math.max(viewport - h, crop.y))
            };
        },

        /// Renders the cropped square at `outSize` onto a canvas.
        _cropCanvas: function (img, viewport, crop, outSize) {
            const canvas = document.createElement('canvas');
            canvas.width = outSize;
            canvas.height = outSize;
            const ctx = canvas.getContext('2d');
            const k = outSize / viewport;
            ctx.drawImage(img, crop.x * k, crop.y * k, img.width * crop.zoom * k, img.height * crop.zoom * k);
            return canvas;
        },

        /// Renders the cropped square at `outSize` and returns it as a data URL.
        /// PNG is used when the source has transparency — re-encoding a cut-out avatar as
        /// JPEG would fill the transparent area with black.
        renderCrop: function (img, viewport, crop, outSize, preferPng) {
            const canvas = this._cropCanvas(img, viewport, crop, outSize);
            return preferPng ? canvas.toDataURL('image/png') : canvas.toDataURL('image/jpeg', this.imageSpec().jpegQuality);
        },

        /// Reads an image's alpha channel at low resolution and answers the two separate
        /// questions the picker has about it. One decode, two answers.
        ///
        ///   hasAlpha — is any pixel less than fully opaque? Decides PNG over JPEG, where
        ///              a single soft pixel is enough to matter, because re-encoding a
        ///              cut-out as JPEG fills the transparent area with black.
        ///
        ///   cutout   — is this shaped art rather than a rectangular photo? Decides
        ///              whether to paint the avatar colour behind it (issue #23). This is
        ///              a much stricter question and deliberately not the same test: an
        ///              antialiased edge or a soft border makes hasAlpha true, and must
        ///              not be allowed to switch somebody's background off.
        ///
        /// cutout reads the corners, because `object-fit: cover` means the corners are the
        /// only place the colour is ever visible. A circular avatar has four transparent
        /// corners; a photograph has none.
        _alphaProfile: function (source) {
            const out = { hasAlpha: false, cutout: false };
            try {
                const s = 32;
                const canvas = document.createElement('canvas');
                canvas.width = s; canvas.height = s;
                const ctx = canvas.getContext('2d');
                ctx.drawImage(source, 0, 0, s, s);
                const data = ctx.getImageData(0, 0, s, s).data;

                for (let i = 3; i < data.length; i += 4) {
                    if (data[i] < 250) { out.hasAlpha = true; break; }
                }

                // Nothing is transparent at all, so there is no shape to work around and
                // the corner walk cannot say otherwise.
                if (!out.hasAlpha) return out;

                // A corner counts as cut away only when a whole 4x4 block of it is clear.
                // One outermost pixel would let a single stray sample decide this.
                const BLOCK = 4;
                const corners = [[0, 0], [s - BLOCK, 0], [0, s - BLOCK], [s - BLOCK, s - BLOCK]];
                let clearCorners = 0;

                for (let c = 0; c < corners.length; c++) {
                    const cx = corners[c][0];
                    const cy = corners[c][1];
                    let clear = true;
                    for (let y = cy; y < cy + BLOCK && clear; y++) {
                        for (let x = cx; x < cx + BLOCK && clear; x++) {
                            if (data[(y * s + x) * 4 + 3] >= 16) clear = false;
                        }
                    }
                    if (clear) clearCorners++;
                }

                // Three of four rather than all four: a circle with one stray opaque
                // sample still counts, a rectangle with one rounded corner still does not.
                out.cutout = clearCorners >= 3;
            } catch (e) { /* tainted canvas or no context — assume an opaque photo */ }
            return out;
        },

        /// True when any pixel is not fully opaque. Sampled at low resolution — this only
        /// decides an output format, so an exact answer is not worth the work.
        _hasTransparency: function (img) {
            return this._alphaProfile(img).hasAlpha;
        },

        /// Opens the crop editor. Calls back with { image, thumb } data URLs, or does
        /// nothing if the user cancels.
        ///
        /// Supports all three input modes the supported clients need: mouse drag, touch
        /// drag, and D-pad (arrows pan, +/- zoom, and the zoom slider is focusable).
        showCropDialog: function (img, onDone) {
            const VIEW = 260;
            const preferPng = this._hasTransparency(img);

            const minZoom = this._coverZoom(img, VIEW);
            let crop = this._clampCrop(img, VIEW, {
                zoom: minZoom,
                // Start centred — the subject of a photo is far more often in the middle
                // than in a corner.
                x: (VIEW - img.width * minZoom) / 2,
                y: (VIEW - img.height * minZoom) / 2
            });

            const dialog = document.createElement('div');
            dialog.id = 'profiles-crop-dialog';
            dialog.classList.add('jpf-trap-surface');
            dialog.style.cssText = `
                position: fixed; top: 0; left: 0; right: 0; bottom: 0;
                background: rgba(0,0,0,0.85); backdrop-filter: blur(8px);
                display: flex; align-items: center; justify-content: center;
                z-index: ${DIALOG_Z};
            `;
            dialog.innerHTML = `
                <div style="background:var(--jpf-panel); border:1px solid var(--jpf-border-1); border-radius: var(--jpf-r-md);
                            padding:22px; max-width:340px; width:92%; box-shadow:0 10px 30px rgba(0,0,0,0.5); text-align:center;
                            user-select:none; -webkit-user-select:none;">
                    <h2 style="margin:0 0 4px 0; color:var(--jpf-text-1); font-size:1.15rem; font-weight:700;">${t('avatar.positionYourPicture')}</h2>
                    <p style="color:var(--jpf-text-2); font-size:0.78rem; margin:0 0 14px 0;">
                        ${t('avatar.dragOrArrows')}
                    </p>
                    <!-- data-profiles-own-keys marks the arrows as this element's own, so
                         the focus trap leaves them for panning. It is on the view and not
                         the dialog so the buttons below stay reachable by remote. -->
                    <div id="profiles-crop-view" tabindex="0" data-profiles-own-keys="1" style="
                        width:${VIEW}px; height:${VIEW}px; margin:0 auto; border-radius:50%;
                        overflow:hidden; position:relative; cursor:grab; touch-action:none;
                        background:#0d0d12; outline-offset:3px;">
                        <canvas id="profiles-crop-canvas" width="${VIEW}" height="${VIEW}"
                                style="display:block; width:${VIEW}px; height:${VIEW}px;"></canvas>
                    </div>
                    <input type="range" id="profiles-crop-zoom" min="1" max="4" step="0.01" value="1"
                           style="width:100%; margin:16px 0 4px 0;" aria-label="${t('avatar.zoom')}" />
                    <div style="display:flex; gap: var(--jpf-gap); justify-content:center; margin-top:12px;">
                        <button id="profiles-crop-cancel" class="profiles-btn btn-secondary" style="padding:10px 20px; font-weight:600;">${t('common.cancel')}</button>
                        <button id="profiles-crop-save" class="profiles-btn btn-primary" style="padding:10px 20px; font-weight:600;">${t('avatar.usePicture')}</button>
                    </div>
                </div>
            `;
            document.body.appendChild(dialog);

            const view = dialog.querySelector('#profiles-crop-view');
            const canvas = dialog.querySelector('#profiles-crop-canvas');
            const ctx = canvas.getContext('2d');
            const zoomInput = dialog.querySelector('#profiles-crop-zoom');

            const draw = () => {
                ctx.clearRect(0, 0, VIEW, VIEW);
                ctx.drawImage(img, crop.x, crop.y, img.width * crop.zoom, img.height * crop.zoom);
            };
            draw();

            const setZoom = (multiplier, anchorX, anchorY) => {
                const next = Math.max(1, Math.min(4, multiplier));
                const newZoom = minZoom * next;
                // Zoom about a point so the image does not lurch sideways: keep whatever
                // was under the anchor in the same place.
                const ax = anchorX === undefined ? VIEW / 2 : anchorX;
                const ay = anchorY === undefined ? VIEW / 2 : anchorY;
                const ratio = newZoom / crop.zoom;
                crop = this._clampCrop(img, VIEW, {
                    zoom: newZoom,
                    x: ax - (ax - crop.x) * ratio,
                    y: ay - (ay - crop.y) * ratio
                });
                zoomInput.value = String(next);
                draw();
            };

            zoomInput.addEventListener('input', () => setZoom(parseFloat(zoomInput.value)));

            // ── Dragging ──────────────────────────────────────────────────────────
            // Movement and release are tracked on window, not on the circle. Pointer
            // capture used to be what kept a gesture alive, and it is not dependable
            // across the clients this runs on — a throw from setPointerCapture aborted
            // the handler before dragging was ever set, which is why panning did nothing
            // while the zoom slider still worked. Capture is now best-effort only.
            let dragging = false, lastX = 0, lastY = 0, pinchStart = 0, pinchZoomStart = 1;
            const activePointers = new Map();

            const beginPointer = (id, x, y) => {
                activePointers.set(id, { x: x, y: y });
                if (activePointers.size === 1) {
                    dragging = true; lastX = x; lastY = y;
                    view.style.cursor = 'grabbing';
                } else if (activePointers.size === 2) {
                    const pts = Array.from(activePointers.values());
                    pinchStart = Math.hypot(pts[0].x - pts[1].x, pts[0].y - pts[1].y);
                    pinchZoomStart = parseFloat(zoomInput.value);
                }
            };

            const movePointer = (id, x, y) => {
                if (!activePointers.has(id)) return;
                activePointers.set(id, { x: x, y: y });

                if (activePointers.size === 2 && pinchStart > 0) {
                    const pts = Array.from(activePointers.values());
                    const dist = Math.hypot(pts[0].x - pts[1].x, pts[0].y - pts[1].y);
                    setZoom(pinchZoomStart * (dist / pinchStart));
                    return;
                }
                if (!dragging) return;
                crop = this._clampCrop(img, VIEW, {
                    zoom: crop.zoom,
                    x: crop.x + (x - lastX),
                    y: crop.y + (y - lastY)
                });
                lastX = x; lastY = y;
                draw();
            };

            const endPointer = (id) => {
                activePointers.delete(id);
                if (activePointers.size < 2) pinchStart = 0;
                if (activePointers.size === 0) { dragging = false; view.style.cursor = 'grab'; }
            };

            // Tracked so the window-level listeners come off again when the dialog closes.
            const bound = [];
            const on = (target, type, fn, opts) => {
                target.addEventListener(type, fn, opts);
                bound.push([target, type, fn, opts]);
            };

            if (window.PointerEvent) {
                on(view, 'pointerdown', (e) => {
                    // Stops the browser starting a text selection or image drag instead,
                    // which on desktop swallows the rest of the gesture.
                    e.preventDefault();
                    // preventDefault also suppresses the focus that a click would give,
                    // and focus is what the arrow-key panning below needs.
                    view.focus();
                    try { view.setPointerCapture(e.pointerId); } catch (err) { /* best effort */ }
                    beginPointer(e.pointerId, e.clientX, e.clientY);
                });
                on(window, 'pointermove', (e) => movePointer(e.pointerId, e.clientX, e.clientY));
                on(window, 'pointerup', (e) => endPointer(e.pointerId));
                on(window, 'pointercancel', (e) => endPointer(e.pointerId));
            } else {
                // Older TV browsers have no Pointer Events at all.
                on(view, 'mousedown', (e) => { e.preventDefault(); beginPointer('mouse', e.clientX, e.clientY); });
                on(window, 'mousemove', (e) => movePointer('mouse', e.clientX, e.clientY));
                on(window, 'mouseup', () => endPointer('mouse'));

                on(view, 'touchstart', (e) => {
                    e.preventDefault();
                    Array.prototype.forEach.call(e.changedTouches, t => beginPointer(t.identifier, t.clientX, t.clientY));
                }, { passive: false });
                on(window, 'touchmove', (e) => {
                    if (!activePointers.size) return;
                    // Without this the page scrolls under the finger instead of panning.
                    e.preventDefault();
                    Array.prototype.forEach.call(e.changedTouches, t => movePointer(t.identifier, t.clientX, t.clientY));
                }, { passive: false });
                const endTouch = (e) => Array.prototype.forEach.call(e.changedTouches, t => endPointer(t.identifier));
                on(window, 'touchend', endTouch);
                on(window, 'touchcancel', endTouch);
            }

            // D-pad / keyboard. A television has no pointer at all, so this is the only way
            // in on the clients that need the avatar library most.
            view.addEventListener('keydown', (e) => {
                const step = 12;
                let handled = true;
                switch (e.key) {
                    case 'ArrowLeft':  crop = this._clampCrop(img, VIEW, { zoom: crop.zoom, x: crop.x + step, y: crop.y }); break;
                    case 'ArrowRight': crop = this._clampCrop(img, VIEW, { zoom: crop.zoom, x: crop.x - step, y: crop.y }); break;
                    case 'ArrowUp':    crop = this._clampCrop(img, VIEW, { zoom: crop.zoom, x: crop.x, y: crop.y + step }); break;
                    case 'ArrowDown':  crop = this._clampCrop(img, VIEW, { zoom: crop.zoom, x: crop.x, y: crop.y - step }); break;
                    case '+': case '=': setZoom(parseFloat(zoomInput.value) + 0.2); break;
                    case '-': case '_': setZoom(parseFloat(zoomInput.value) - 0.2); break;
                    // The arrows are spent on panning here, so OK is the way onward. A
                    // remote has nothing else to leave the picture with.
                    case 'Enter': case ' ':
                        e.preventDefault();
                        dialog.querySelector('#profiles-crop-save').focus();
                        return;
                    default: handled = false;
                }
                if (handled) { e.preventDefault(); draw(); }
            });

            const close = () => {
                bound.forEach(([target, type, fn, opts]) => target.removeEventListener(type, fn, opts));
                bound.length = 0;
                dialog.remove();
            };
            dialog.querySelector('#profiles-crop-cancel').addEventListener('click', close);
            dialog.querySelector('#profiles-crop-save').addEventListener('click', () => {
                const spec = this.imageSpec();
                const thumbCanvas = this._cropCanvas(img, VIEW, crop, spec.thumbSize);

                // Read from the cropped result, not the source. Cropping into the opaque
                // middle of a cut-out leaves a picture with nothing transparent about it,
                // and the background decision has to describe what will actually be shown.
                // preferPng stays on the source, where being over-eager costs only bytes.
                const cutout = this._alphaProfile(thumbCanvas).cutout;

                const image = this.renderCrop(img, VIEW, crop, spec.masterSize, preferPng);
                const thumb = preferPng
                    ? thumbCanvas.toDataURL('image/png')
                    : thumbCanvas.toDataURL('image/jpeg', spec.jpegQuality);
                close();
                onDone({ image: image, thumb: thumb, cutout: cutout });
            });

            setTimeout(() => view.focus(), 50);
        },

        /// Fetches the administrator's avatar library. Resolves to a safe empty shape on
        /// failure so a picker can always render.
        fetchAvatarLibrary: function (apiClient, token) {
            return fetch(apiClient.getUrl('plugins/profiles/avatars'), {
                cache: 'no-store',
                headers: this.getAuthHeaders(token)
            })
            .then(res => res.ok ? res.json() : Promise.reject(new Error('unavailable')))
            .then(data => {
                // The administrator's picture quality rides along with the avatar list,
                // because every picker that could save a picture has already asked for it.
                this._imageSpec = this.parseImageSpec(data.image || data.Image);
                return data;
            })
            .then(data => ({
                allowCustomUploads: (data.allowCustomUploads !== undefined ? data.allowCustomUploads : data.AllowCustomUploads) !== false,
                avatars: (data.avatars || data.Avatars || []).map(a => ({
                    id: a.id || a.Id,
                    displayName: a.displayName || a.DisplayName || '',
                    url: a.url || a.Url,
                    thumbUrl: a.thumbUrl || a.ThumbUrl
                }))
            }))
            .catch(() => ({ allowCustomUploads: true, avatars: [] }));
        },

        /// Renders the profile-picture block. Shared by the create and edit forms so the two
        /// cannot drift — they already carried near-identical copies of the old upload UI.
        ///
        /// `prefix` namespaces the element ids ('create' or 'edit').
        /// The cut-out background choice (issue #23).
        ///
        /// Part of the picture picker since 1.7.2, right under the preview and its buttons.
        /// It sat under the name field for a while, beside the colour swatches it governs;
        /// the forms now put the swatches straight after the picker, so it is beside them
        /// either way and it reads as what it is, a property of the picture.
        /// initAvatarPicker owns the wiring and finds it by id.
        ///
        /// Hidden until a picture is set — with no picture the colour is the entire
        /// background and there is nothing to switch off. The library-artwork dialog passes
        /// no `transparency`, so its picker never draws this.
        renderTransparentToggle: function (prefix, currentImage, currentTransparent) {
            return `
                <div id="${prefix}-transparent-row" class="form-group picture-transparent-row"
                     style="display: ${currentImage ? 'block' : 'none'};">
                    <label class="library-check-label picture-transparent-toggle">
                        <input type="checkbox" id="${prefix}-transparent-toggle"${currentTransparent ? ' checked' : ''} />
                        <span>${t('profileForm.noBackground')}</span>
                    </label>
                    <div class="form-hint picture-transparent-hint">${t('profileForm.noBackgroundHint')}</div>
                </div>
            `;
        },

        /// `transparency` opts this picker into the cut-out background choice (issue #23):
        /// null or omitted means the form is not offering it, which is what the
        /// library-artwork dialog wants — it reuses this picker to choose a tile picture,
        /// where a transparent avatar background means nothing. Otherwise { enabled: bool }.
        renderAvatarPicker: function (prefix, library, currentImage, currentColor, transparency) {
            const supportsTransparent = !!transparency;
            const currentTransparent = supportsTransparent && !!transparency.enabled;
            const hasLibrary = library.avatars.length > 0;
            const preview = currentImage ? avatarInner(currentImage, '+', /* useThumb */ true) : '+';

            const libraryHtml = hasLibrary ? `
                <div class="picture-source-block">
                    <div class="picture-source-title">${t('avatar.fromThisServer')}</div>
                    <div id="${prefix}-avatar-library" class="avatar-library-grid">
                        ${library.avatars.map(a => `
                            <button type="button" class="avatar-library-item" tabindex="0"
                                    data-id="${escapeHtml(a.id)}" data-url="${escapeHtml(a.url)}"
                                    title="${escapeHtml(a.displayName)}"
                                    aria-label="${escapeHtml(a.displayName) || t('avatar.avatarLabel')}">
                                <img src="${safeImageSrc(a.thumbUrl)}" alt="" loading="lazy" />
                            </button>
                        `).join('')}
                    </div>
                </div>
            ` : '';

            // The upload control disappears entirely when the administrator has locked
            // avatars to the library — a disabled button people cannot use is just noise.
            const uploadHtml = library.allowCustomUploads ? `
                <div class="picture-source-block">
                    <div class="picture-source-title">${t('avatar.fromThisDevice')}</div>
                    <label for="${prefix}-profile-image-file" id="${prefix}-profile-image-label" class="profiles-btn btn-secondary image-upload-btn" tabindex="0">
                        <span class="material-icons" style="font-size: 1.25rem;">photo_camera</span>
                        <span>${t('avatar.uploadPicture')}</span>
                    </label>
                    <input type="file" id="${prefix}-profile-image-file" accept="image/jpeg,image/png,image/webp,image/gif,image/avif,image/bmp" style="display: none;" />
                    <div class="form-hint" style="margin: 0;">${t('avatar.uploadHint')}</div>
                </div>
            ` : `
                <div class="form-hint" style="margin: 0; min-width: 0;">
                    ${t('avatar.adminLimited')}
                </div>
            `;

            // A pasted URL was the odd one out among the ways of setting a picture: it
            // could not be cropped, nothing was copied onto the server, and the avatar
            // broke the day the far end went away — with no sign here that it had. Every
            // other path ends in a file this server owns.

            // Closed, always (P2-104). It was open on a new profile because choosing a
            // picture is the task there, but on a phone that put the whole library grid
            // between the name and the rating, the Create button a screen further down. The
            // colour swatches are the default; the button is one tap.
            const sourcesOpen = false;

            // Remove is rendered here rather than by the edit form, so the two actions are
            // the same size and sit together. It was a third-height button hanging under
            // the row, which read as belonging to whatever came next.
            return `
                <div class="form-group">
                    <label>${t('avatar.profilePicture')}</label>
                    <div class="profile-image-upload-container" style="display: flex; flex-direction: column; gap: var(--jpf-gap);">
                        <div class="image-upload-row">
                            <div id="${prefix}-image-upload-preview" class="image-upload-preview${
                                currentImage && currentTransparent ? ' is-transparent' : ''
                            }" style="background-color: ${
                                currentImage && currentTransparent ? 'transparent' : safeColor(currentColor)
                            };">${preview}</div>
                            <div class="image-upload-actions">
                                <button type="button" id="${prefix}-change-picture" class="profiles-btn btn-secondary image-upload-btn picture-change-btn"
                                        aria-expanded="${sourcesOpen}" aria-controls="${prefix}-picture-sources">
                                    <span class="material-icons" style="font-size: 1.25rem;">photo_camera</span>
                                    <span>${currentImage ? t('avatar.changePicture') : t('avatar.choosePicture')}</span>
                                    <span class="material-icons picture-caret" aria-hidden="true">expand_more</span>
                                </button>
                                <button type="button" id="${prefix}-clear-profile-image-btn" class="profiles-btn btn-secondary image-upload-btn picture-remove-btn"
                                        style="display: ${currentImage ? 'inline-flex' : 'none'};">
                                    <span class="material-icons" style="font-size: 1.25rem;">delete_outline</span>
                                    <span>${t('common.remove')}</span>
                                </button>
                            </div>
                        </div>
                        ${supportsTransparent ? this.renderTransparentToggle(prefix, currentImage, currentTransparent) : ''}
                        <div id="${prefix}-picture-sources" class="picture-sources${sourcesOpen ? ' is-open' : ''}">
                            ${libraryHtml}
                            ${uploadHtml}
                        </div>
                        <div id="${prefix}-image-error" style="display:none; color:var(--jpf-danger); font-size:0.82rem; font-weight:600; line-height:1.45;"></div>
                    </div>
                </div>
            `;
        },

        /// Wires the picker up. Returns an accessor for the chosen image so the caller can
        /// read it at save time without tracking the state itself.
        /// `transparency` must match what was passed to renderAvatarPicker — see there.
        initAvatarPicker: function (container, prefix, library, initialImage, onPreviewChange, transparency) {
            const initialTransparent = !!transparency && !!transparency.enabled;
            // libraryId is set instead of image/thumb when the picture comes from the
            // library on a locked-down server: the server copies the file itself, which is
            // the only form of the choice it can actually verify.
            const state = {
                image: initialImage || null,
                thumb: null,
                libraryId: null,
                // Only meaningful while a picture is set. With no picture the colour is
                // the entire background, so there is nothing to make transparent.
                transparent: !!(initialImage && initialTransparent)
            };

            const previewEl = container.querySelector(`#${prefix}-image-upload-preview`);
            const errEl = container.querySelector(`#${prefix}-image-error`);
            const fileInput = container.querySelector(`#${prefix}-profile-image-file`);
            const fileLabel = container.querySelector(`#${prefix}-profile-image-label`);
            const removeBtn = container.querySelector(`#${prefix}-clear-profile-image-btn`);
            const transparentRow = container.querySelector(`#${prefix}-transparent-row`);
            const transparentInput = container.querySelector(`#${prefix}-transparent-toggle`);

            const showError = (message) => {
                if (!errEl) return;
                errEl.textContent = message;
                errEl.style.display = message ? 'block' : 'none';
            };

            /// The colour currently chosen on the form. Read from the DOM rather than
            /// passed in: the two forms already keep `.active` on exactly one dot, so
            /// this cannot drift out of step with what will be saved.
            const currentColor = () => {
                const active = container.querySelector('.color-dot.active');
                return active ? safeColor(active.getAttribute('data-color')) : DEFAULT_AVATAR_COLOR;
            };

            /// Whether the colour swatches can still affect the avatar. Owned here rather
            /// than by the two forms, because it depends on both facts this function
            /// holds — a picture being set, and its background being switched off — and
            /// two writers would race each other.
            const syncColorGroup = () => {
                this.setColorGroupInert(prefix, !!state.image, state.transparent);
            };

            /// Paints the preview to match state.transparent and keeps the checkbox in
            /// step. Called both when detection decides and when the user overrides, so
            /// the two can never disagree about what is stored.
            const applyTransparency = () => {
                if (transparentInput) transparentInput.checked = state.transparent;
                syncColorGroup();
                if (!previewEl) return;
                previewEl.classList.toggle('is-transparent', state.transparent);
                previewEl.style.backgroundColor = state.transparent ? 'transparent' : currentColor();
            };

            const setPreview = (src) => {
                if (!previewEl) return;
                previewEl.style.position = 'relative';
                previewEl.innerHTML = src ? avatarInner(src, '+', /* useThumb */ true) : '+';
                // Removing is only an option while there is something to remove. Driven
                // from here so it is right on the first paint too — setPreview runs once
                // at init.
                if (removeBtn) removeBtn.style.display = src ? 'inline-flex' : 'none';
                // Same for the transparency choice: it describes a picture, so it has
                // nothing to say when there is not one.
                if (transparentRow) transparentRow.style.display = src ? 'block' : 'none';
                syncColorGroup();
                if (typeof onPreviewChange === 'function') onPreviewChange(src);
            };
            setPreview(state.image);
            applyTransparency();

            if (transparentInput) {
                // The user's answer always beats detection's. Detection only ever writes
                // this value when a new picture arrives.
                transparentInput.addEventListener('change', () => {
                    state.transparent = transparentInput.checked;
                    applyTransparency();
                });
            }

            // The ways of choosing a picture are collapsed behind one button. Every id
            // the handlers below bind to still exists — they have only moved inside the
            // panel — so nothing else in this function changes.
            const sourcesEl = container.querySelector(`#${prefix}-picture-sources`);
            const changeBtn = container.querySelector(`#${prefix}-change-picture`);
            if (sourcesEl && changeBtn) {
                changeBtn.addEventListener('click', () => {
                    const open = sourcesEl.classList.toggle('is-open');
                    changeBtn.setAttribute('aria-expanded', open ? 'true' : 'false');
                });
            }

            const applyCropped = (result) => {
                state.image = result.image;
                state.thumb = result.thumb;
                state.libraryId = null;
                setPreview(result.image);
                // A new picture re-answers the question, so detection wins here even if
                // the user had overridden it for the previous one — the override was
                // about a picture that is no longer set.
                state.transparent = !!result.cutout;
                applyTransparency();
                showError('');
                if (fileInput) fileInput.value = '';
            };

            // Label doubles as the file trigger so it can be focused by D-pad.
            if (fileLabel && fileInput) {
                fileLabel.addEventListener('keydown', (e) => {
                    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); fileInput.click(); }
                });
            }

            if (fileInput) {
                fileInput.addEventListener('change', (e) => {
                    const file = e.target.files && e.target.files[0];
                    if (!file) return;
                    showError('');
                    this.loadImageFromFile(file)
                        .then(img => this.showCropDialog(img, applyCropped))
                        .catch(err => {
                            showError(err.message);
                            fileInput.value = '';
                        });
                });
            }

            container.querySelectorAll(`#${prefix}-avatar-library .avatar-library-item`).forEach(btn => {
                const pick = () => {
                    showError('');

                    // On a locked-down server the crop step is skipped and the id is sent
                    // instead. Cropping would produce a data payload indistinguishable from
                    // an upload, which is exactly what that server has chosen to refuse.
                    if (!library.allowCustomUploads) {
                        state.image = btn.getAttribute('data-url');
                        state.thumb = null;
                        state.libraryId = btn.getAttribute('data-id');
                        setPreview(state.image);

                        // No crop step on this path, so there is no cropped canvas to read
                        // and the source has to be fetched again to be sampled. Same
                        // origin — it is our own avatar endpoint — so the canvas is not
                        // tainted. Detection is best-effort: a failure just leaves the
                        // colour painted, which is the old behaviour.
                        const pickedUrl = state.image;
                        this.loadImageFromUrl(pickedUrl)
                            .then(img => {
                                // The user may have picked something else while this was
                                // in flight; a late answer must not overwrite it.
                                if (state.image !== pickedUrl) return;
                                state.transparent = this._alphaProfile(img).cutout;
                                applyTransparency();
                            })
                            .catch(() => { /* leave the colour on */ });
                        return;
                    }

                    // Full size, not the thumbnail — this is about to be re-cropped, and
                    // cropping a 128px source would produce a soft avatar.
                    this.loadImageFromUrl(btn.getAttribute('data-url'))
                        .then(img => this.showCropDialog(img, applyCropped))
                        .catch(err => showError(err.message));
                };
                btn.addEventListener('click', (e) => { e.preventDefault(); pick(); });
                btn.addEventListener('keydown', (e) => {
                    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); pick(); }
                });
            });

            // Empty string, not null: the server reads that as "delete the picture",
            // whereas null means "leave it alone".
            const clearPicture = () => {
                state.image = '';
                state.thumb = null;
                state.libraryId = null;
                // With no picture the colour is the whole background. Leaving this set
                // would save a profile whose avatar is a transparent square.
                state.transparent = false;
                setPreview(null);
                applyTransparency();
                if (fileInput) fileInput.value = '';
            };

            if (removeBtn) removeBtn.addEventListener('click', clearPicture);

            return {
                get: () => ({
                    image: state.image,
                    thumb: state.thumb,
                    libraryId: state.libraryId,
                    transparent: state.transparent
                }),
                clear: clearPicture,
                setError: showError
            };
        },

        showAddProfileModal: function () {
            const apiClient = ApiClient;
            const masterState = this.readMasterState();
            if (!masterState) return;

            const ticket = this.beginNavigation();

            // Everything this form needs is account-wide, so it is usually already
            // cached from the prefetch that ran when the overlay opened.
            this.fetchSharedFormData(apiClient, masterState)
            .then(shared => [shared.libraries, shared.devices, shared.libraryTags, shared.avatarLibrary, shared.ratings])
            .then(([libraries, devices, libraryTags, avatarLibrary, ratings]) => {
                if (!this.navIsCurrent(ticket)) return;
                const normalizedLibs = (libraries || []).map(lib => ({
                    id: lib.id || lib.Id,
                    name: lib.name || lib.Name,
                    collectionType: lib.collectionType || lib.CollectionType
                }));
                // Re-rendering the modal orphans any listeners the previous form owned.
                this.clearManagedDocumentListeners();
                const content = document.querySelector('.profiles-modal-content');
                // Basics, then a closed "More settings" (1.7.2). Basics is what nearly every
                // profile is made of: who it is, its PIN, what it may watch. The rest is
                // there when wanted and out of the way when not.

                // ── Basics: who this profile is ─────────────────────────────────
                const startColor = this.nextAvatarColor();
                const createAppearance = `
                    <div class="form-group">
                        <label for="create-name-input">${t('profileForm.profileName')}</label>
                        <input type="text" id="create-name-input" maxlength="32" placeholder="${t('profileForm.namePlaceholder')}" required />
                    </div>
                    ${this.renderAvatarPicker('create', avatarLibrary, null, startColor, { enabled: false })}
                    <div class="form-group avatar-color-group" id="create-color-group">
                        <label>${t('profileForm.avatarColor')}</label>
                        ${this.renderColorPicker(startColor)}
                        <div class="form-hint" data-role="color-hint">${t('profileForm.colorHintNoPicture')}</div>
                    </div>
                `;

                // ── Basics: its PIN ─────────────────────────────────────────────
                const createSecurity = `
                    <div class="form-group">
                        <label for="create-pin-input">${t('profileForm.pin')}</label>
                        <input type="text" id="create-pin-input" maxlength="8" pattern="[0-9]*" inputmode="numeric" placeholder="${t('profileForm.pinPlaceholderEmpty')}" autocomplete="one-time-code" data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true" />
                    </div>
                `;

                // ── More: when the PIN is asked again ───────────────────────────
                const createLocking = `
                    <div class="form-group">
                        <label for="create-lockout-select">${t('profileForm.autoLock')}</label>
                        <select id="create-lockout-select">${this.renderLockoutOptions(5, true)}</select>
                        <div class="form-hint">${t('profileForm.autoLockHintCreate')}</div>
                    </div>
                    <div class="form-group">
                        <label class="library-check-label" style="display: inline-flex; align-items: center; gap: 0.5rem; cursor: pointer; user-select: none;">
                            <input type="checkbox" id="create-local-bypass-checkbox" style="cursor: pointer; accent-color: var(--jpf-accent);" />
                            <span>${t('profileForm.bypassPinLan')}</span>
                        </label>
                        <div class="form-hint">${t('profileForm.bypassPinHint')}</div>
                    </div>
                `;

                // ── Basics: what this profile can watch ─────────────────────────
                // The Kids preset only fills in the rating; it is not a kind of profile,
                // and nothing remembers that it was ticked.
                const createLibraries = `
                    <div class="form-group">
                        <label class="library-check-label" style="display: inline-flex; align-items: center; gap: 0.5rem; cursor: pointer; user-select: none;">
                            <input type="checkbox" id="create-kids-preset" style="cursor: pointer; accent-color: var(--jpf-accent);" />
                            <span>${t('profileForm.kidsPreset')}</span>
                        </label>
                        <div class="form-hint">${t('profileForm.kidsPresetHint')}</div>
                    </div>
                    ${this.renderRatingSelect('create', null, ratings)}
                    <div class="form-group">
                        <div class="section-inline-header">
                            <label style="margin: 0;">${t('profileForm.enabledLibraries')}</label>
                            <label class="library-check-label" style="font-size: 0.85rem; color: var(--jpf-text-2); margin: 0; display: inline-flex; align-items: center; gap: 0.4rem;">
                                <input type="checkbox" id="create-select-all-libraries" style="margin: 0; cursor: pointer; accent-color: var(--jpf-accent);" />
                                <span>${t('common.selectAll')}</span>
                            </label>
                        </div>
                        <div class="library-checklist">
                            ${normalizedLibs.map(lib => `
                                <label class="library-check-label">
                                    <input type="checkbox" class="library-checkbox" value="${lib.id}" />
                                    <span>${escapeHtml(lib.name)}</span>
                                </label>
                            `).join('')}
                        </div>
                        <div class="form-hint">${t('profileForm.librariesInheritHint')}</div>
                    </div>
                    ${this.renderDeviceDropdown('create', devices, null)}
                `;

                // ── More: tags on top of the libraries ──────────────────────────
                // Devices are above, with what the profile can watch: keeping a child to
                // one television is the common parental task, not a rare one (P2-605).
                const createRestrictions = `
                    ${this.renderTagSection('create', libraryTags, null)}
                `;

                content.innerHTML = `
                    <h1 class="profiles-title">${t('profileForm.createTitle')}</h1>
                    <div class="create-profile-container is-two-col">
                        <div class="form-col">
                            ${this.renderSection('person', t('profileForm.sectionProfileTitle'), t('profileForm.sectionProfileSubtitle'), createAppearance)}
                            ${this.renderSection('lock', t('profileForm.sectionSecurityTitle'), t('profileForm.sectionPinSubtitle'), createSecurity)}
                        </div>
                        <div class="form-col">
                            ${this.renderSection('video_library', t('profileForm.sectionWatchTitle'), t('profileForm.sectionWatchSubtitle'), createLibraries)}
                            <div id="create-viewing-slot"></div>
                        </div>
                        ${this.renderMoreSettings('create', [
                            this.renderSection('timer', t('profileForm.sectionLockingTitle'), t('profileForm.sectionLockingSubtitle'), createLocking),
                            this.renderThemeSection('create', null)
                        ], [
                            this.renderSection('shield', t('profileForm.sectionRestrictionsTitle'), t('profileForm.sectionRestrictionsSubtitle'), createRestrictions)
                        ])}

                        <div id="create-error-msg" class="form-error" style="display:none;"></div>
                        <div class="profile-dialog-actions jpf-actions">
                            <button id="create-cancel-btn" class="profiles-btn btn-secondary">${t('common.cancel')}</button>
                            <button id="create-submit-btn" class="profiles-btn btn-primary">${t('common.create')}</button>
                        </div>
                    </div>
                `;

                // Color selector interaction
                const dots = content.querySelectorAll('.color-dot');
                let selectedColor = startColor;
                dots.forEach(dot => {
                    dot.addEventListener('click', () => {
                        dots.forEach(d => d.classList.remove('active'));
                        dot.classList.add('active');
                        selectedColor = dot.getAttribute('data-color');
                        const createPreview = document.getElementById('create-image-upload-preview');
                        if (createPreview && !createPreview.querySelector('img')) {
                            createPreview.style.backgroundColor = selectedColor;
                        }
                    });
                });

                // The colour group is dimmed by the picker itself now — it depends on the
                // transparency choice as well as on whether a picture is set.
                const avatarPicker = this.initAvatarPicker(
                    content, 'create', avatarLibrary, null, null, { enabled: false });

                // With no picture chosen, the preview shows the profile's initial — so it
                // has to follow what is being typed into the name field.
                const previewDiv = document.getElementById('create-image-upload-preview');
                const nameInput = document.getElementById('create-name-input');
                if (nameInput && previewDiv) {
                    nameInput.addEventListener('input', () => {
                        if (avatarPicker.get().image) return;
                        const nameVal = nameInput.value.trim();
                        previewDiv.innerHTML = nameVal ? escapeHtml(nameVal.charAt(0).toUpperCase()) : '+';
                    });
                }

                // Support D-pad Enter/Space select on color dots
                content.addEventListener('keydown', (e) => {
                    if ((e.key === 'Enter' || e.key === ' ') && e.target.classList.contains('color-dot')) {
                        e.preventDefault();
                        e.target.click();
                    }
                });

                // Select all libraries logic for creation
                const selectAllCheckbox = document.getElementById('create-select-all-libraries');
                const libCheckboxes = content.querySelectorAll('.library-checkbox');
                if (selectAllCheckbox) {
                    selectAllCheckbox.addEventListener('change', (e) => {
                        const isChecked = e.target.checked;
                        libCheckboxes.forEach(cb => {
                            cb.checked = isChecked;
                        });
                    });

                    libCheckboxes.forEach(cb => {
                        cb.addEventListener('change', () => {
                            const allChecked = Array.from(libCheckboxes).every(c => c.checked);
                            selectAllCheckbox.checked = allChecked;
                        });
                    });
                }

                // Devices dropdown logic for create
                const createTrigger = document.getElementById('create-devices-dropdown-trigger');
                const createList = document.getElementById('create-devices-dropdown-list');
                if (createTrigger && createList) {
                    // Keep aria-expanded in step with the visual state so screen readers and
                    // TV remotes report the dropdown correctly.
                    const setCreateOpen = (open) => {
                        createList.style.display = open ? 'block' : 'none';
                        createTrigger.setAttribute('aria-expanded', open ? 'true' : 'false');
                    };
                    createTrigger.addEventListener('click', (e) => {
                        e.stopPropagation();
                        setCreateOpen(createList.style.display === 'none');
                    });
                    createTrigger.addEventListener('keydown', (e) => {
                        if (e.key === 'Enter' || e.key === ' ') {
                            e.preventDefault();
                            createTrigger.click();
                        } else if (e.key === 'Escape') {
                            setCreateOpen(false);
                        }
                    });
                    this.addManagedDocumentListener('click', () => setCreateOpen(false));
                    createList.addEventListener('click', (e) => {
                        e.stopPropagation();
                    });
                    createList.addEventListener('keydown', (e) => {
                        if (e.key === 'Enter' && e.target.type === 'checkbox') {
                            e.preventDefault();
                            e.target.checked = !e.target.checked;
                            e.target.dispatchEvent(new Event('change'));
                        }
                    });
                }

                const updateCreateSelectedText = () => {
                    const checked = Array.from(content.querySelectorAll('.create-device-checkbox:checked'));
                    const txt = document.getElementById('create-devices-dropdown-selected-text');
                    if (txt) {
                        if (checked.length === 0) {
                            txt.textContent = t('profileForm.allDevicesAllowed');
                        } else if (checked.length === 1) {
                            txt.textContent = t('profileForm.oneDeviceAllowed');
                        } else {
                            txt.textContent = t('profileForm.devicesAllowed', { count: checked.length });
                        }
                    }
                };
                content.querySelectorAll('.create-device-checkbox').forEach(cb => {
                    cb.addEventListener('change', updateCreateSelectedText);
                });
                updateCreateSelectedText();

                this.bindKidsPreset(content.querySelector('#create-kids-preset'),
                                    content.querySelector('#create-rating-select'));

                document.getElementById('create-submit-btn').addEventListener('click', () => {
                    const name = document.getElementById('create-name-input').value.trim();
                    const pin = document.getElementById('create-pin-input').value;
                    const rating = document.getElementById('create-rating-select').value;
                    const lockoutMinutes = parseInt(document.getElementById('create-lockout-select').value, 10);
                    const bypassPin = document.getElementById('create-local-bypass-checkbox').checked;
                    
                    const checkedLibs = [];
                    content.querySelectorAll('.library-checkbox:checked').forEach(cb => {
                        // Only an id. A library listed without one gave its checkbox the
                        // value "undefined", and that one entry failed the whole create (#33).
                        if (isGuidLike(cb.value)) checkedLibs.push(cb.value);
                    });

                    const checkedDevices = [];
                    content.querySelectorAll('.create-device-checkbox:checked').forEach(cb => {
                        checkedDevices.push(cb.value);
                    });

                    const showCreateError = (msg) => {
                        const el = document.getElementById('create-error-msg');
                        if (el) { el.textContent = msg; el.style.display = 'block'; }
                    };

                    if (!name) {
                        showCreateError(t('profileForm.nameRequired'));
                        return;
                    }

                    if (pin && (pin.length < 4 || pin.length > 8 || !/^\d+$/.test(pin))) {
                        showCreateError(t('profileForm.pinLengthCreate'));
                        return;
                    }

                    const createUrl = apiClient.getUrl('plugins/profiles/create');
                    fetch(createUrl, {
                        method: 'POST',
                        headers: this.jsonHeaders(masterState.token),
                        body: JSON.stringify({
                            profileName: name,
                            pin: pin,
                            avatarColor: selectedColor,
                            // "" is no limit of its own — the same word /update uses (R-1144).
                            maxParentalRating: rating,
                            // No box ticked means every library. Said explicitly, as /update
                            // needs it to be: there, a left-out list now keeps what it was.
                            enabledFolders: checkedLibs.length > 0 ? checkedLibs : null,
                            allLibraries: checkedLibs.length > 0 ? null : true,
                            blockedTags: this.getTagEditorValues(content, 'create-blocked-tags'),
                            allowedTags: this.getTagEditorValues(content, 'create-allowed-tags'),
                            masterPin: this.masterPin,
                            lockoutMinutes: lockoutMinutes,
                            bypassPinOnLocalNetwork: bypassPin,
                            allowedDeviceIds: checkedDevices,
                            profileImage: avatarPicker.get().image,
                            profileImageThumb: avatarPicker.get().thumb,
                            avatarLibraryId: avatarPicker.get().libraryId,
                            transparentAvatar: avatarPicker.get().transparent,
                            theme: this.readThemePicker('create')
                        })
                    })
                    .then(res => {
                        if (!res.ok) return res.text().then(text => { throw new Error(text); });
                        return res.json();
                    })
                    .then(created => {
                        const refresh = () => this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.token, /* forceRefresh */ true);
                        // The profile exists now, so its viewing rules can be saved against
                        // its id. A failure here must not look like a failed create.
                        const id = created && (created.profileUserId || created.ProfileUserId);
                        const saveLimits = () => viewingEditor.save(id).catch(err => {
                            this.showAlert(t('errors.error'), t('errors.savingProfile', { message: escapeHtml(err.message) }));
                        });
                        // The first profile is when a main PIN starts to matter: from now on
                        // the main account is the way past that profile's restrictions.
                        const people = this.currentProfiles || [];
                        const main = people.find(x => x.isMaster && !x.isBonfire);
                        const first = !people.some(x => !x.isMaster && !x.isBonfire);
                        const needsPin = !!main && !main.hasPin;
                        if (needsPin && viewingEditor.pending()) {
                            // The server will not switch limits on with no main PIN to switch
                            // them off again, so the PIN comes first and the limits after it.
                            // Saving them first lost them, with an error (P2-712).
                            this.promptSetMainPin(people, false, () => {
                                if (main.hasPin) saveLimits().then(refresh);
                                else this.showAlert(t('errors.error'), t('mainPin.limitsOff'), refresh);
                            }, null, 'mainPin.limitsBody');
                        } else if (needsPin && first) {
                            this.promptSetMainPin(people, false, refresh);
                        } else {
                            saveLimits().then(refresh);
                        }
                    })
                    .catch(err => {
                        const el = document.getElementById('create-error-msg');
                        if (el) { el.textContent = err.message; el.style.display = 'block'; }
                    });
                });

                document.getElementById('create-cancel-btn').addEventListener('click', () => {
                    this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.token);
                });
                const viewingEditor = this.initViewingEditor(content.querySelector('#create-viewing-slot'), null, true);
                this.bindThemePicker('create');
                this.initTVCheckboxes(content);
                this.initTagEditors(content);
            });
        },

        showEditProfileModal: function (profile) {
            const apiClient = ApiClient;
            const masterState = this.readMasterState();
            if (!masterState) return;

            const ticket = this.beginNavigation();

            // Users/{id} is the only one of the five that is about the profile you
            // clicked. The rest come from the account-wide cache.
            const userUrl = apiClient.getUrl(`Users/${profile.profileUserId}`);

            Promise.all([
                this.fetchSharedFormData(apiClient, masterState),
                fetch(userUrl, { headers: this.getAuthHeaders(masterState.token) }).then(res => res.json())
            ])
            .then(([shared, userDetails]) => [shared.libraries, userDetails, shared.devices, shared.libraryTags, shared.avatarLibrary, shared.ratings])
            .then(([libraries, userDetails, devices, libraryTags, avatarLibrary, ratings]) => {
                if (!this.navIsCurrent(ticket)) return;
                const normalizedLibs = (libraries || []).map(lib => ({
                    id: lib.id || lib.Id,
                    name: lib.name || lib.Name,
                    collectionType: lib.collectionType || lib.CollectionType
                }));
                const policy = userDetails.Policy || userDetails.policy || {};
                const blockedFolders = policy.BlockedMediaFolders || policy.blockedMediaFolders || [];
                const enableAll = policy.EnableAllFolders !== undefined ? policy.EnableAllFolders : (policy.enableAllFolders || false);
                const maxRating = policy.MaxParentalRating !== undefined ? policy.MaxParentalRating : (policy.maxParentalRating !== undefined ? policy.maxParentalRating : null);
                const currentLockout = profile.lockoutMinutes !== undefined ? profile.lockoutMinutes : 5;

                // Re-rendering the modal orphans any listeners the previous form owned.
                this.clearManagedDocumentListeners();
                const content = document.querySelector('.profiles-modal-content');

                const isSub = !profile.isMaster;

                // Basics, then a closed "More settings" — see the create form.

                // ── Basics: who this profile is ─────────────────────────────────
                const appearanceBody = `
                    <div class="form-group">
                        <label for="edit-name-input">${t('profileForm.profileName')}</label>
                        <input type="text" id="edit-name-input" maxlength="32" value="${escapeHtml(profile.profileName)}" ${profile.isMaster ? 'disabled style="opacity: 0.6"' : ''} required />
                        ${profile.isMaster ? `<div class="form-hint">${t('profileForm.masterNameHint')}</div>` : ''}
                    </div>
                    ${this.renderAvatarPicker('edit', avatarLibrary, profile.profileImage, profile.avatarColor, { enabled: !!profile.transparentAvatar })}
                    <div class="form-group avatar-color-group" id="edit-color-group">
                        <label>${t('profileForm.avatarColor')}</label>
                        ${this.renderColorPicker(profile.avatarColor)}
                        <div class="form-hint" data-role="color-hint">${t('profileForm.colorHintNoPicture')}</div>
                    </div>
                `;

                // ── Basics: its PIN ─────────────────────────────────────────────
                // The main account's PIN, home-network skip and auto-lock are in Settings →
                // Your account (1.7.2), which saves each one alone through /account. They are
                // not repeated here: two places to set one PIN is how they drift.
                const securityBody = !isSub ? `
                    <div class="form-hint">${t('profileForm.mainSecurityMoved')}</div>
                ` : `
                    <div class="form-group">
                        <label for="edit-pin-input">${t('profileForm.pin')}</label>
                        <div class="pin-edit-group" style="display:flex; gap: var(--jpf-gap); flex-wrap: wrap;">
                            <input type="text" id="edit-pin-input" maxlength="8" pattern="[0-9]*" inputmode="numeric" placeholder="${profile.hasPin ? t('profileForm.pinPlaceholderNew') : t('profileForm.pinPlaceholderNone')}" autocomplete="one-time-code" data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true" style="flex:1; min-width: 160px;" />
                            ${profile.hasPin ? `<button id="edit-clear-pin-btn" class="profiles-btn btn-secondary" style="padding:10px 15px;">${t('profileForm.clearPin')}</button>` : ''}
                        </div>
                        <div id="edit-pin-error" class="form-error" style="display:none; margin-top:8px;"></div>
                        <div class="form-hint">
                            ${profile.hasPin
                                ? t('profileForm.pinIsSetHint')
                                : t('profileForm.noPinSetHint')}
                        </div>
                    </div>
                `;

                // ── More: when the PIN is asked again ───────────────────────────
                const lockingBody = `
                    <div class="form-group">
                        <label for="edit-lockout-select">${t('profileForm.autoLock')}</label>
                        <select id="edit-lockout-select">${this.renderLockoutOptions(currentLockout, false)}</select>
                        <div class="form-hint">${t('profileForm.autoLockHintEdit')}</div>
                    </div>
                    <div class="form-group">
                        <label class="library-check-label" style="display: inline-flex; align-items: center; gap: 0.5rem; cursor: pointer; user-select: none;">
                            <input type="checkbox" id="edit-local-bypass-checkbox" ${profile.bypassPinOnLocalNetwork ? 'checked' : ''} style="cursor: pointer; accent-color: var(--jpf-accent);" />
                            <span>${t('profileForm.bypassPinLan')}</span>
                        </label>
                        <div class="form-hint">${t('profileForm.bypassPinHint')}</div>
                    </div>
                `;

                // ── Basics: what this profile can watch ─────────────────────────
                const librariesBody = `
                    ${this.renderRatingSelect('edit', maxRating, ratings)}
                    <div class="form-group">
                        <div class="section-inline-header">
                            <label style="margin: 0;">${t('profileForm.libraries')}</label>
                            <label class="library-check-label" style="font-size: 0.85rem; color: var(--jpf-text-2); margin: 0; display: inline-flex; align-items: center; gap: 0.4rem;">
                                <input type="checkbox" id="edit-select-all-libraries" style="margin: 0; cursor: pointer; accent-color: var(--jpf-accent);" />
                                <span>${t('common.selectAll')}</span>
                            </label>
                        </div>
                        <div class="form-hint" style="margin: 0 0 8px 0;">
                            ${t('profileForm.librariesInheritHint')}
                        </div>
                        <div class="libart-list" id="edit-library-artwork">
                            <div class="libart-head" aria-hidden="true">
                                <span class="libart-head-lib">${t('profileForm.libraryHeader')}</span>
                                <span class="libart-head-art">${t('profileForm.artworkHeader')}</span>
                            </div>
                            ${normalizedLibs.map(lib => {
                                const storedFolders = profile.enabledFolders;
                                let isChecked;
                                // "Every library" is saved as nothing ticked, so it is shown
                                // that way. Drawn with every box ticked, the next save would
                                // send them all and turn it into a fixed list.
                                if (profile.allLibraries) {
                                    isChecked = false;
                                } else if (storedFolders !== null && storedFolders !== undefined) {
                                    isChecked = storedFolders.some(id => this.normalizeGuid(id) === this.normalizeGuid(lib.id));
                                } else {
                                    isChecked = enableAll || !blockedFolders.some(bf => this.normalizeGuid(bf) === this.normalizeGuid(lib.id));
                                }
                                return `
                                    <div class="libart-row" data-lib="${lib.id}">
                                        <label class="library-check-label libart-check">
                                            <input type="checkbox" class="library-checkbox" value="${lib.id}" ${isChecked ? 'checked' : ''} />
                                            <span class="libart-thumb" aria-hidden="true"></span>
                                            <span class="libart-name" title="${escapeHtml(lib.name)}">${escapeHtml(lib.name)}</span>
                                        </label>
                                        <select class="libart-mode" aria-label="${t('profileForm.artworkForAria', { name: escapeHtml(lib.name) })}">
                                            <option value="inherit">${t('profileForm.artworkDefault')}</option>
                                            <option value="custom">${t('profileForm.artworkPicture')}</option>
                                            <option value="none">${t('profileForm.artworkHidden')}</option>
                                        </select>
                                        <button type="button" class="profiles-btn btn-secondary libart-choose" style="padding:6px 12px; font-size:0.8rem;">${t('profileForm.artworkChoose')}</button>
                                    </div>
                                `;
                            }).join('')}
                        </div>
                        <label class="library-check-label libart-toggle">
                            <input type="checkbox" id="edit-libart-toggle" />
                            <span>${t('profileForm.artworkToggleLabel')}</span>
                        </label>
                        <div class="form-hint libart-explainer" id="edit-libart-hint" style="display: none;">
                            ${t('profileForm.artworkExplainer')}
                        </div>
                    </div>
                    ${this.renderDeviceDropdown('edit', devices, profile)}
                `;

                // ── More: tags on top of the libraries ──────────────────────────
                // Devices sit above with what the profile can watch (P2-605).
                const restrictionsBody = `
                    ${this.renderTagSection('edit', libraryTags, profile)}
                `;

                // The main account has no More: its sign-in settings are in Your account and
                // it has no limits to set, so all that is left of it is the theme.
                content.innerHTML = `
                    <h1 class="profiles-title">${t('profileForm.editTitle')}</h1>
                    <div class="create-profile-container${isSub ? ' is-two-col' : ''}">
                        <div class="form-col">
                            ${this.renderSection('person', t('profileForm.sectionProfileTitle'), t('profileForm.sectionProfileSubtitle'), appearanceBody)}
                            ${this.renderSection('lock', t('profileForm.sectionSecurityTitle'), isSub ? t('profileForm.sectionPinSubtitle') : t('profileForm.sectionSecuritySubtitle'), securityBody)}
                            ${isSub ? '' : this.renderThemeSection('edit', profile.theme)}
                        </div>
                        ${isSub ? `
                        <div class="form-col">
                            ${this.renderSection('video_library', t('profileForm.sectionWatchTitle'), t('profileForm.sectionWatchSubtitle'), librariesBody)}
                            <div id="edit-viewing-slot"></div>
                        </div>
                        ${this.renderMoreSettings('edit', [
                            this.renderSection('timer', t('profileForm.sectionLockingTitle'), t('profileForm.sectionLockingSubtitle'), lockingBody),
                            this.renderThemeSection('edit', profile.theme)
                        ], [
                            this.renderSection('shield', t('profileForm.sectionRestrictionsTitle'), t('profileForm.sectionRestrictionsSubtitle'), restrictionsBody)
                        ])}` : ''}

                        <div class="profile-dialog-actions jpf-actions">
                            <div class="dialog-action-buttons">
                                <button id="edit-cancel-btn" class="profiles-btn btn-secondary">${t('common.cancel')}</button>
                                ${isSub ? `
                                    <button id="edit-delete-btn" class="profiles-btn btn-danger-quiet">${t('profileForm.deleteProfile')}</button>
                                ` : ''}
                            </div>
                            <button id="edit-submit-btn" class="profiles-btn btn-primary">${t('common.save')}</button>
                        </div>
                    </div>
                `;

                // Setup active color dot selection
                const dots = content.querySelectorAll('.color-dot');
                let selectedColor = profile.avatarColor || DEFAULT_AVATAR_COLOR;
                dots.forEach(dot => {
                    const color = dot.getAttribute('data-color');
                    if (color.toLowerCase() === selectedColor.toLowerCase()) {
                        dot.classList.add('active');
                    }
                    dot.addEventListener('click', () => {
                        dots.forEach(d => d.classList.remove('active'));
                        dot.classList.add('active');
                        selectedColor = color;
                        const editPreview = document.getElementById('edit-image-upload-preview');
                        if (editPreview && !editPreview.querySelector('img')) {
                            editPreview.style.backgroundColor = selectedColor;
                        }
                    });
                });

                // Remove now lives inside the picker, next to Change picture, and shows
                // itself from setPreview — so there is nothing left to wire here.
                const avatarPicker = this.initAvatarPicker(
                    content, 'edit', avatarLibrary, profile.profileImage,
                    (src) => {
                        // Colour dimming is the picker's own business now; this callback
                        // is only here to put the initial back when the picture goes.
                        const preview = document.getElementById('edit-image-upload-preview');
                        if (preview && !src) preview.innerHTML = escapeHtml(profile.avatarInitial);
                    },
                    { enabled: !!profile.transparentAvatar });

                // Support D-pad Enter/Space select on color dots
                content.addEventListener('keydown', (e) => {
                    if ((e.key === 'Enter' || e.key === ' ') && e.target.classList.contains('color-dot')) {
                        e.preventDefault();
                        e.target.click();
                    }
                });

                // Select all libraries logic for edit
                const selectAllCheckbox = document.getElementById('edit-select-all-libraries');
                const libCheckboxes = content.querySelectorAll('.library-checkbox');
                if (selectAllCheckbox) {
                    const allChecked = libCheckboxes.length > 0 && Array.from(libCheckboxes).every(c => c.checked);
                    selectAllCheckbox.checked = allChecked;

                    selectAllCheckbox.addEventListener('change', (e) => {
                        const isChecked = e.target.checked;
                        libCheckboxes.forEach(cb => {
                            cb.checked = isChecked;
                        });
                        this.syncAllLibraryRows(content);
                    });

                    libCheckboxes.forEach(cb => {
                        cb.addEventListener('change', () => {
                            const allChecked = Array.from(libCheckboxes).every(c => c.checked);
                            selectAllCheckbox.checked = allChecked;
                            this.syncLibraryRowState(cb.closest('.libart-row'));
                        });
                    });
                }

                // The tick and the artwork controls share a row now, so the artwork half
                // has to start out matching the tick rather than waiting for a change.
                this.syncAllLibraryRows(content);

                // Devices dropdown logic for edit
                const editTrigger = document.getElementById('devices-dropdown-trigger');
                const editList = document.getElementById('devices-dropdown-list');
                if (editTrigger && editList) {
                    // Keep aria-expanded in step with the visual state — see the create form.
                    const setEditOpen = (open) => {
                        editList.style.display = open ? 'block' : 'none';
                        editTrigger.setAttribute('aria-expanded', open ? 'true' : 'false');
                    };
                    editTrigger.addEventListener('click', (e) => {
                        e.stopPropagation();
                        setEditOpen(editList.style.display === 'none');
                    });
                    editTrigger.addEventListener('keydown', (e) => {
                        if (e.key === 'Enter' || e.key === ' ') {
                            e.preventDefault();
                            editTrigger.click();
                        } else if (e.key === 'Escape') {
                            setEditOpen(false);
                        }
                    });
                    this.addManagedDocumentListener('click', () => setEditOpen(false));
                    editList.addEventListener('click', (e) => {
                        e.stopPropagation();
                    });
                    editList.addEventListener('keydown', (e) => {
                        if (e.key === 'Enter' && e.target.type === 'checkbox') {
                            e.preventDefault();
                            e.target.checked = !e.target.checked;
                            e.target.dispatchEvent(new Event('change'));
                        }
                    });
                }

                const updateSelectedText = () => {
                    const checked = Array.from(content.querySelectorAll('.device-checkbox:checked'));
                    const txt = document.getElementById('devices-dropdown-selected-text');
                    if (txt && devices === null) {
                        txt.textContent = t('profileForm.devicesUnchanged');
                    } else if (txt) {
                        if (checked.length === 0) {
                            txt.textContent = t('profileForm.allDevicesAllowed');
                        } else if (checked.length === 1) {
                            txt.textContent = t('profileForm.oneDeviceAllowed');
                        } else {
                            txt.textContent = t('profileForm.devicesAllowed', { count: checked.length });
                        }
                    }
                };
                content.querySelectorAll('.device-checkbox').forEach(cb => {
                    cb.addEventListener('change', updateSelectedText);
                });
                updateSelectedText();

                // Clear PIN logic
                let isPinCleared = false;
                const clearPinBtn = document.getElementById('edit-clear-pin-btn');
                if (clearPinBtn) {
                    clearPinBtn.addEventListener('click', (e) => {
                        e.preventDefault();
                        isPinCleared = true;
                        document.getElementById('edit-pin-input').value = '';
                        document.getElementById('edit-pin-input').placeholder = t('profileForm.pinPlaceholderUnprotected');
                        clearPinBtn.style.display = 'none';
                    });
                }


                // Save handler
                document.getElementById('edit-submit-btn').addEventListener('click', () => {
                    const name = document.getElementById('edit-name-input').value.trim();
                    // The main account's form has none of these (see Section 2): null
                    // leaves each as it is on the server.
                    const pinField = document.getElementById('edit-pin-input');
                    const pinVal = pinField ? pinField.value : '';
                    const bypassBox = document.getElementById('edit-local-bypass-checkbox');
                    const bypassPin = bypassBox ? bypassBox.checked : null;

                    let rating = null;
                    let checkedLibs = null;
                    let checkedDevices = null;
                    // Null for the master profile, which has no tag editor — the server reads
                    // null as "leave unchanged".
                    let blockedTags = null;
                    let allowedTags = null;
                    if (!profile.isMaster) {
                        blockedTags = this.getTagEditorValues(content, 'edit-blocked-tags');
                        allowedTags = this.getTagEditorValues(content, 'edit-allowed-tags');
                        rating = document.getElementById('edit-rating-select').value;
                        const rawLibs = [];
                        content.querySelectorAll('.library-checkbox:checked').forEach(cb => {
                            // Same guard as the create form (#33).
                            if (isGuidLike(cb.value)) rawLibs.push(cb.value);
                        });
                        // No box ticked means every library, and that is sent as
                        // allLibraries: true. It used to be enabledFolders: null, which the
                        // server also read as "every library" — so any client that simply
                        // left the field out lifted a child's library limits (R-1144). Null
                        // now keeps the list, and "all" has to be said.
                        checkedLibs = rawLibs.length > 0 ? rawLibs : null;
                        // Null leaves the list as it is. With no device list to draw there
                        // are no boxes, and [] would mean "any device".
                        if (devices !== null) {
                            checkedDevices = [];
                            content.querySelectorAll('.device-checkbox:checked').forEach(cb => {
                                checkedDevices.push(cb.value);
                            });
                        }
                    }
                    const lockoutSel = document.getElementById('edit-lockout-select');
                    const lockoutMinutes = lockoutSel ? parseInt(lockoutSel.value, 10) : undefined;

                    // Report validation failures inline next to the offending field as well as
                    // in a dialog. A dialog alone is fragile — when it was rendering behind the
                    // overlay this button looked completely dead on an invalid PIN.
                    const pinError = document.getElementById('edit-pin-error');
                    const showPinError = (msg) => {
                        if (pinError) {
                            pinError.textContent = msg;
                            pinError.style.display = 'block';
                        }
                        const input = document.getElementById('edit-pin-input');
                        if (input) {
                            input.style.borderColor = 'var(--jpf-danger)';
                            input.focus();
                        }
                    };
                    if (pinError) pinError.style.display = 'none';
                    const pinInputEl = document.getElementById('edit-pin-input');
                    if (pinInputEl) pinInputEl.style.borderColor = '';

                    if (!name) {
                        this.showAlert(t('errors.validationError'), t('profileForm.nameRequired'));
                        return;
                    }

                    // What is typed in the box comes first. "Clear PIN" empties the field
                    // but leaves it on screen, and the clear used to be checked first: a
                    // parent who cleared and then typed a new PIN saved an unprotected
                    // profile and was told it had worked (R-1214). Clear means "no PIN"
                    // only while the box is still empty.
                    let pin = null;
                    if (pinVal) {
                        if (!/^\d+$/.test(pinVal)) {
                            showPinError(t('profileForm.pinMustBeDigits'));
                            return;
                        }
                        if (pinVal.length < 4 || pinVal.length > 8) {
                            showPinError(t('profileForm.pinLengthEdit', { count: pinVal.length }));
                            return;
                        }
                        pin = pinVal;
                    } else if (isPinCleared) {
                        pin = ''; // Tells backend to clear the PIN
                    }

                    const updateUrl = apiClient.getUrl('plugins/profiles/update');
                    fetch(updateUrl, {
                        method: 'POST',
                        headers: this.jsonHeaders(masterState.token),
                        body: JSON.stringify({
                            profileId: profile.profileUserId,
                            profileName: name,
                            pin: pin,
                            avatarColor: selectedColor,
                            // "" is "no limit of its own"; null (the main account) keeps it.
                            // Null used to be both, so this sent null for "no limit" (R-1144).
                            maxParentalRating: rating,
                            enabledFolders: checkedLibs,
                            allLibraries: !profile.isMaster && checkedLibs === null ? true : null,
                            blockedTags: blockedTags,
                            allowedTags: allowedTags,
                            masterPin: this.masterPin,
                            lockoutMinutes: lockoutMinutes,
                            bypassPinOnLocalNetwork: bypassPin,
                            allowedDeviceIds: checkedDevices,
                            profileImage: avatarPicker.get().image,
                            profileImageThumb: avatarPicker.get().thumb,
                            avatarLibraryId: avatarPicker.get().libraryId,
                            transparentAvatar: avatarPicker.get().transparent,
                            theme: this.readThemePicker('edit')
                        })
                    })
                    .then(res => {
                        if (!res.ok) return res.text().then(text => { throw new Error(text); });
                        // Artwork is stored per library, so it is saved after the profile
                        // itself rather than folded into that one request.
                        // The account on screen changed its own theme: put it on now rather
                        // than on the next load. Any other profile's theme shows when it is
                        // opened.
                        const savedTheme = this.readThemePicker('edit');
                        if (savedTheme !== undefined && this._themeSettings
                            && this.normalizeGuid(profile.profileUserId) === this.normalizeGuid(ApiClient.getCurrentUserId())) {
                            this._setThemeSettings({
                                theme: savedTheme || this._themeSettings.defaultTheme,
                                defaultTheme: this._themeSettings.defaultTheme,
                                allowProfileThemes: this._themeSettings.allow
                            });
                        }
                        return artworkEditor.save(profile.profileUserId)
                            .then(() => viewingEditor.save())
                            .then(() => {
                                this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.token, /* forceRefresh */ true);
                            });
                    })
                    .catch(err => this.showAlert(t('errors.error'), t('errors.savingProfile', { message: escapeHtml(err.message) })));
                });

                // Delete handler
                const delBtn = document.getElementById('edit-delete-btn');
                if (delBtn) {
                    delBtn.addEventListener('click', () => {
                        this.showConfirmDialog(t('profileForm.deleteProfile'), t(profile.keepAccountOnDelete ? 'profileForm.deleteConfirmKept' : 'profileForm.deleteConfirmMessage', { name: escapeHtml(profile.profileName) }), () => {
                            this.executeProfileDeletion(profile.profileUserId);
                        });
                    });
                }
// Cancel handler
                document.getElementById('edit-cancel-btn').addEventListener('click', () => {
                    this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.token);
                });
                const artworkEditor = this.initLibraryArtworkEditor(content, profile.profileUserId);
                const viewingEditor = this.initViewingEditor(content.querySelector('#edit-viewing-slot'), profile);
                this.bindThemePicker('edit');
                this.initTVCheckboxes(content);
                this.initTagEditors(content);
            })
            .catch(err => {
                this.showAlert(t('errors.error'), t('errors.loadProfileDetails', { message: escapeHtml(err.message) }));
                this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.token);
            });
        },

        showBonfireModal: function () {
            // Kept, so a slow render cannot draw over a screen the user moved on to.
            const navTicket = this.beginNavigation();
            const apiClient = ApiClient;
            const masterState = this.readMasterState();
            if (!masterState) return;

            const content = document.querySelector('.profiles-modal-content');
            if (!content) return;

            content.innerHTML = `
                <h1 class="profiles-title">${t('bonfire.yourBonfireTitle')}</h1>
                <div class="create-profile-container" style="max-width: 500px; width: 100%;">
                    <div id="account-links-slot"></div>
                    <div id="bonfire-container" style="width: 100%; min-height: 100px; display: flex; flex-direction: column; gap: 1.5rem;">
                        <div style="display: flex; justify-content: center; padding: 20px;">
                            <div class="profiles-loading-spinner" style="border: 3px solid var(--jpf-border-1); border-radius: 50%; border-top: 3px solid var(--jpf-accent); width: 24px; height: 24px; animation: jpfSpin 1s linear infinite;"></div>
                        </div>
                        <div class="bonfire-dialog-actions jpf-actions" style="margin-top: 2rem !important; display: flex !important; justify-content: flex-start !important; width: 100% !important; box-sizing: border-box !important; position: relative !important; bottom: auto !important; left: auto !important; right: auto !important; top: auto !important;">
                            <button id="bonfire-back-btn" class="profiles-btn btn-secondary" style="padding: 10px 24px !important; font-size: 1rem !important; box-sizing: border-box !important; margin: 0 !important; display: inline-block !important; width: auto !important; flex: 0 0 auto !important; position: relative !important; bottom: auto !important; left: auto !important; right: auto !important; top: auto !important;">${t('common.back')}</button>
                        </div>
                    </div>
                </div>
            `;

            const attachBackBtnListener = () => {
                const btn = content.querySelector('#bonfire-back-btn');
                if (btn) {
                    // Back to Settings, where Sharing is opened from — the same place the
                    // loaded page's Back goes. It went to the profile grid while loading.
                    btn.addEventListener('click', () => this.showSettingsMenu());
                }
            };

            attachBackBtnListener();

            this._bindAccountLinks(content.querySelector('#account-links-slot'));
            this.loadBonfireStatus(content, apiClient, masterState.token);

            // Auto-focus first focusable element for TV D-pad navigation
            setTimeout(() => {
                // Never a Remove button: focus fills a button, and the first thing on the
                // screen read as a filled primary action (P2-102).
                const first = content.querySelector('input, button:not(.links-remove):not(.links-leave)');
                if (first) first.focus();
            }, 250);
        },

        /// Settings → Sharing: other accounts shown on this household's screen (issue #39),
        /// and the screens this account is shown on. Each tile opens that person's own
        /// account, so there is one watch history and nothing to keep in step.
        ///
        /// Adding one takes its password, typed here once; the server checks it and keeps
        /// nothing. Every route answers with the whole list, so each action redraws from it.
        _bindAccountLinks: function (slot) {
            if (!slot) return;
            let data = null;
            const status = () => slot.querySelector('#links-status');
            const say = (text, bad) => {
                const el = status();
                if (!el) return;
                el.textContent = text || '';
                el.style.color = bad ? 'var(--jpf-danger)' : '';
            };
            const list = (key) => (this._field(data, key) || []).map(x => ({
                id: this._field(x, 'userId'),
                name: this._field(x, 'name') || '',
                hasPin: this._field(x, 'hasPin') === true
            }));

            const draw = () => {
                if (!document.body.contains(slot)) return;
                const added = list('added');
                const shown = list('shownIn');
                const row = (x, cls, label, aria) => `
                    <div class="jpf-link-row">
                        <span class="jpf-link-name">
                            <span>${escapeHtml(x.name)}</span>
                            ${cls === 'links-remove' ? `<span class="form-hint">${x.hasPin ? t('links.withPin') : t('links.noPin')}</span>` : ''}
                        </span>
                        <button type="button" class="profiles-btn btn-danger-quiet ${cls}" data-id="${escapeHtml(x.id)}" data-name="${escapeHtml(x.name)}" aria-label="${escapeHtml(aria)}">${label}</button>
                    </div>`;
                slot.innerHTML = this.renderSection('group_add', t('links.title'), t('links.subtitle'), `
                    ${added.length
                        ? `<div class="jpf-link-list">${added.map(x => row(x, 'links-remove', t('links.remove'), t('links.removeAria', { name: x.name }))).join('')}</div>`
                        : `<div class="form-hint">${t('links.none')}</div>`}
                    <div class="form-group">
                        <label for="links-username">${t('links.username')}</label>
                        <input type="text" id="links-username" autocomplete="off" autocapitalize="off" spellcheck="false" data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true" />
                    </div>
                    <div class="form-group">
                        <label for="links-password">${t('links.password')}</label>
                        <input type="password" id="links-password" autocomplete="off" data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true" />
                        <div class="form-hint">${t('links.hint')}</div>
                    </div>
                    <button type="button" id="links-add" class="profiles-btn btn-primary">${t('links.add')}</button>
                    ${shown.length ? `
                    <div class="form-group" style="margin-top: 1.25rem;">
                        <label>${t('links.shownTitle')}</label>
                        <div class="jpf-link-list">${shown.map(x => row(x, 'links-leave', t('links.leave'), t('links.leaveAria', { name: x.name }))).join('')}</div>
                    </div>` : ''}
                    <div id="links-status" class="form-hint" role="status" aria-live="polite"></div>
                `);

                const user = slot.querySelector('#links-username');
                const pass = slot.querySelector('#links-password');
                let busy = false;
                const add = () => {
                    const name = (user.value || '').trim();
                    if (!name || !pass.value) { say(t('links.needBoth'), true); return; }
                    if (busy) return;
                    busy = true;
                    this._emailCall('accounts/links/add', { username: name, password: pass.value })
                        .then(reply => { data = reply; draw(); say(t('links.added', { name: name })); })
                        .catch(err => { busy = false; pass.value = ''; say(err.message, true); });
                };
                slot.querySelector('#links-add').addEventListener('click', add);
                pass.addEventListener('keydown', (e) => { if (e.key === 'Enter') add(); });
                user.addEventListener('keydown', (e) => { if (e.key === 'Enter') pass.focus(); });

                const bind = (cls, body, title, message) => {
                    Array.prototype.forEach.call(slot.querySelectorAll('.' + cls), btn => {
                        btn.addEventListener('click', () => {
                            const id = btn.getAttribute('data-id');
                            const name = btn.getAttribute('data-name') || '';
                            this.showActionsDialog(title(name), message, [
                                { label: cls === 'links-remove' ? t('links.remove') : t('links.leave'), primary: true, run: () => {
                                    this._emailCall('accounts/links/remove', body(id))
                                        .then(reply => { data = reply; draw(); say(t('links.removed')); })
                                        .catch(err => say(err.message, true));
                                } },
                                { label: t('common.cancel') }
                            ]);
                        });
                    });
                };
                bind('links-remove', id => ({ userId: id }), name => t('links.removeTitle', { name: name }), t('links.removeBody'));
                bind('links-leave', id => ({ householdId: id }), name => t('links.leaveTitle', { name: name }), t('links.leaveBody'));
            };

            this._emailCall('accounts/links')
                .then(reply => { data = reply; draw(); })
                .catch(() => { slot.innerHTML = ''; });
        },

        /// An added account chosen in Manage Profiles: it isn't this household's to edit,
        /// so the only thing to do with it is take it off the screen.
        showAddedAccount: function (profile) {
            const name = profile.profileName || '';
            this.showActionsDialog(name, t('links.manageBody', { name: name }), [
                { label: t('links.remove'), primary: true, run: () => {
                    this._emailCall('accounts/links/remove', { userId: profile.profileUserId })
                        .then(() => {
                            const st = this.readMasterState();
                            if (st) this.fetchAndRenderProfiles(ApiClient, st.masterUserId, st.token, true);
                        })
                        .catch(err => this.showAlert(t('errors.error'), escapeHtml(err.message)));
                } },
                { label: t('common.cancel') }
            ]);
        },

        /// Settings → Your account: the main account's PIN and sign-in settings, and the
        /// household's devices.
        ///
        /// The device tools (rename, merge, forget, tidy) used to sit inside each profile's
        /// device list. A device belongs to the account, not to a profile: renaming the TV
        /// from Kid's form renamed it for everyone. A profile now only ticks devices.
        ///
        /// Every control saves on its own, through /account or /devices/*, so there is no
        /// Save button to miss and nothing else on the account is sent back.
        showAccountSettings: function () {
            const ticket = this.beginNavigation();
            const masterState = this.readMasterState();
            const content = document.querySelector('.profiles-modal-content');
            if (!masterState || !content) return;
            const main = (this.currentProfiles || []).find(p => p.isMaster && !p.isBonfire);
            if (!main) { this.showSettingsMenu(); return; }

            this.fetchSharedFormData(ApiClient, masterState)
                .then(shared => shared.devices, () => null)
                .then(devices => {
                    if (!this.navIsCurrent(ticket)) return;
                    this._renderAccountSettings(content, main, devices, masterState);
                });
        },

        _renderAccountSettings: function (content, main, devices, masterState) {
            const apiClient = ApiClient;
            const check = (id, on, label, hint) => `
                <div class="form-group">
                    <label class="library-check-label" style="display: inline-flex; align-items: center; gap: 0.5rem; cursor: pointer; user-select: none;">
                        <input type="checkbox" id="${id}" ${on ? 'checked' : ''} style="cursor: pointer; accent-color: var(--jpf-accent);" />
                        <span>${label}</span>
                    </label>
                    <div class="form-hint">${hint}</div>
                </div>`;

            const signIn = `
                <div class="form-group">
                    <label for="account-pin-input">${t('account.pinLabel')}</label>
                    <div class="pin-edit-group" style="display:flex; gap: var(--jpf-gap); flex-wrap: wrap;">
                        <input type="text" id="account-pin-input" maxlength="8" pattern="[0-9]*" inputmode="numeric" placeholder="${main.hasPin ? t('profileForm.pinPlaceholderNew') : t('profileForm.pinPlaceholderNone')}" autocomplete="one-time-code" data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true" style="flex:1; min-width: 140px;" />
                        <button type="button" id="account-pin-save" class="profiles-btn btn-primary" style="padding:10px 15px;">${t('account.savePin')}</button>
                        ${main.hasPin ? `<button type="button" id="account-pin-clear" class="profiles-btn btn-secondary" style="padding:10px 15px;">${t('profileForm.clearPin')}</button>` : ''}
                    </div>
                    <div class="form-hint">${main.hasPin ? t('account.pinSetHint') : t('mainPin.setBody')}</div>
                    ${main.isAdministrator ? `<div class="form-hint" style="color: var(--jpf-danger); font-weight: 600;">${t('profileForm.pinAdminWarning')}</div>` : ''}
                </div>
            `;

            // Rare, and each says what it changes in a line: closed under the one fold
            // every household screen uses (P2-603).
            const rare = `
                ${check('account-ask-pin', main.askPinToOpen, t('profileForm.askPinToOpen'), t('profileForm.askPinToOpenHint'))}
                ${check('account-lan-bypass', main.bypassPinOnLocalNetwork, t('profileForm.bypassPinLan'), t('profileForm.bypassPinHint'))}
                <div class="form-group">
                    <label for="account-lockout-select">${t('profileForm.autoLock')}</label>
                    <select id="account-lockout-select">${this.renderLockoutOptions(main.lockoutMinutes, false)}</select>
                    <div class="form-hint">${t('profileForm.autoLockHintEdit')}</div>
                </div>
            `;

            // A count and a button: the list, with rename, merge and forget on each row,
            // is a screen of its own (P2-603).
            const deviceBody = devices === null
                ? `<div class="form-hint">${t('profileForm.devicesUnavailable')}</div>`
                : `<div class="form-hint" id="account-device-count">${t('account.devicesCount', { count: devices.length })}</div>
                   <button type="button" id="account-manage-devices" class="profiles-btn btn-secondary">${t('account.manageDevices')}</button>`;

            content.innerHTML = `
                <h1 class="profiles-title">${t('settings.accountTitle')}</h1>
                <div class="create-profile-container" style="max-width: var(--jpf-w-form); width: 100%;">
                    ${this.renderSection('lock', t('account.signInTitle'), t('account.signInSubtitle'), signIn)}
                    <div id="account-email-slot"></div>
                    <div id="account-zone-slot"></div>
                    ${this.renderSection('devices', t('account.devicesTitle'), t('account.devicesCountSubtitle'), deviceBody)}
                    ${this.renderFold('account-more', t('account.moreSummary'), `<div class="form-col">${rare}</div>`, true)}
                    <div id="account-status" class="form-hint" role="status" aria-live="polite"></div>
                    <div class="bonfire-dialog-actions jpf-actions" style="margin-top: 1.5rem !important; display: flex !important; justify-content: flex-start !important; width: 100% !important;">
                        <button id="account-back-btn" class="profiles-btn btn-secondary" style="padding: 10px 24px !important; font-size: 1rem !important; margin: 0 !important; width: auto !important;">${t('common.done')}</button>
                    </div>
                </div>
            `;
            this._bindAccountScreen(content, main, devices, masterState);
        },

        /// Settings → Your account → Manage devices: every device the household has used,
        /// with rename, merge and forget on each. A screen of its own so the account page
        /// can show a count (P2-603).
        showAccountDevices: function () {
            const ticket = this.beginNavigation();
            const masterState = this.readMasterState();
            const content = document.querySelector('.profiles-modal-content');
            if (!masterState || !content) return;
            this.fetchSharedFormData(ApiClient, masterState)
                .then(shared => shared.devices, () => null)
                .then(devices => {
                    if (!this.navIsCurrent(ticket)) return;
                    this._renderAccountDevices(content, devices, masterState);
                });
        },

        _renderAccountDevices: function (content, devices, masterState) {
            const deviceRow = (dev) => {
                const id = dev.deviceId || dev.DeviceId || '';
                const name = dev.deviceName || dev.DeviceName || t('profileForm.unknownDevice');
                const client = dev.client || dev.Client || t('profileForm.unknownClient');
                const seen = dev.lastSeen || dev.LastSeen;
                const seenDate = seen ? new Date(seen) : null;
                // Year 1 is an unset DateTime; see renderDeviceDropdown.
                const seenStr = (seenDate && seenDate.getFullYear() > 1)
                    ? seenDate.toLocaleDateString() : t('profileForm.unknown');
                return `
                    <div class="device-dropdown-item" data-device-id="${escapeHtml(id)}">
                        <span style="display: flex; flex-direction: column; min-width: 0; flex: 1;">
                            <span class="device-row-name" style="font-weight: 500; overflow: hidden; text-overflow: ellipsis;">${escapeHtml(name)}</span>
                            <span style="font-size: 0.75rem; opacity: 0.6;">${t('profileForm.deviceLastSeen', { client: escapeHtml(client), date: seenStr })}</span>
                        </span>
                        <button type="button" class="device-rename-btn" data-id="${escapeHtml(id)}" data-name="${escapeHtml(name)}" title="${t('profileForm.renameDevice')}" aria-label="${t('profileForm.renameDeviceAria', { name: escapeHtml(name) })}"><span class="material-icons" aria-hidden="true">edit</span></button>
                        <button type="button" class="device-merge-btn" data-id="${escapeHtml(id)}" title="${t('profileForm.mergeDevice')}" aria-label="${t('profileForm.mergeDeviceAria', { name: escapeHtml(name) })}"><span class="material-icons" aria-hidden="true">swap_horiz</span></button>
                        <button type="button" class="device-delete-btn" data-id="${escapeHtml(id)}" title="${t('profileForm.forgetDevice')}" aria-label="${t('profileForm.forgetDeviceAria', { name: escapeHtml(name) })}"><span class="material-icons" aria-hidden="true">delete</span></button>
                    </div>`;
            };
            const deviceBody = devices === null
                ? `<div class="form-hint">${t('profileForm.devicesUnavailable')}</div>`
                : !devices.length
                    ? `<div class="form-hint">${t('profileForm.noDevicesYet')}</div>`
                    : `<div class="jpf-device-list">${devices.map(deviceRow).join('')}</div>
                       <div class="form-hint">${t('account.devicesHint')}</div>
                       <button type="button" id="devices-tidy-btn" class="profiles-btn btn-secondary device-tidy-btn">${t('profileForm.tidyDevices')}</button>`;

            content.innerHTML = `
                <h1 class="profiles-title">${t('account.devicesTitle')}</h1>
                <div class="create-profile-container" style="max-width: var(--jpf-w-form); width: 100%;">
                    ${this.renderSection('devices', t('account.devicesTitle'), t('account.devicesSubtitle'), deviceBody)}
                    <div class="bonfire-dialog-actions jpf-actions" style="margin-top: 1.5rem !important; display: flex !important; justify-content: flex-start !important; width: 100% !important;">
                        <button id="account-back-btn" class="profiles-btn btn-secondary" style="padding: 10px 24px !important; font-size: 1rem !important; margin: 0 !important; width: auto !important;">${t('common.back')}</button>
                    </div>
                </div>
            `;
            this.bindDeviceTools(content, masterState, () => this.showAccountDevices());
            content.querySelector('#account-back-btn').addEventListener('click', () => this.showAccountSettings());
            this.initTVCheckboxes(content);
        },

        /// Everything on the account page that does something: each control saves alone
        /// through /account, so there is no Save button to miss.
        _bindAccountScreen: function (content, main, devices, masterState) {
            const apiClient = ApiClient;
            const status = content.querySelector('#account-status');
            const say = (text, bad) => {
                status.textContent = text || '';
                status.style.color = bad ? 'var(--jpf-danger)' : '';
            };
            const rerender = () => this._renderAccountSettings(content, main, devices, masterState);

            /// One POST to /account. Resolves true when saved; on refusal says why and
            /// resolves false, so the caller can put its control back.
            const saveAccount = (body) => {
                say('');
                return fetch(apiClient.getUrl('plugins/profiles/account'), {
                    method: 'POST',
                    headers: this.jsonHeaders(masterState.token),
                    body: JSON.stringify(Object.assign({ masterPin: this.masterPin }, body))
                })
                .then(res => {
                    if (res.status === 401) { this.handleSessionExpired(); return false; }
                    if (!res.ok) return res.text().then(text => { say(text || t('errors.couldNotSaveThat'), true); return false; });
                    return res.json().then(saved => {
                        const pick = (k) => (saved[k] !== undefined ? saved[k] : saved[k.charAt(0).toUpperCase() + k.slice(1)]);
                        if (pick('hasPin') !== undefined) main.hasPin = pick('hasPin') === true;
                        if (pick('askPinToOpen') !== undefined) main.askPinToOpen = pick('askPinToOpen') !== false;
                        if (pick('bypassPinOnLocalNetwork') !== undefined) main.bypassPinOnLocalNetwork = pick('bypassPinOnLocalNetwork') === true;
                        if (typeof pick('lockoutMinutes') === 'number') main.lockoutMinutes = pick('lockoutMinutes');
                        return true;
                    }, () => true);
                })
                .catch(err => { say(err.message || t('errors.couldNotSaveThat'), true); return false; });
            };

            const pinInput = content.querySelector('#account-pin-input');
            const savePin = () => {
                const pin = pinInput.value;
                if (!/^\d{4,8}$/.test(pin)) { say(t('profileForm.pinLengthCreate'), true); pinInput.focus(); return; }
                saveAccount({ pin: pin }).then(ok => {
                    if (!ok) return;
                    this.masterPin = pin;
                    main.hasPin = true;
                    rerender();
                    say(t('account.pinSaved'));
                });
            };
            content.querySelector('#account-pin-save').addEventListener('click', savePin);
            pinInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') savePin(); });

            const clearBtn = content.querySelector('#account-pin-clear');
            if (clearBtn) {
                clearBtn.addEventListener('click', () => {
                    saveAccount({ pin: '' }).then(ok => {
                        if (!ok) return;
                        this.masterPin = null;
                        main.hasPin = false;
                        rerender();
                        say(t('account.pinCleared'));
                    });
                });
            }

            const toggle = (id, field) => {
                const box = content.querySelector('#' + id);
                if (!box) return;
                box.addEventListener('change', () => {
                    const body = {};
                    body[field] = box.checked;
                    saveAccount(body).then(ok => {
                        if (ok) say(t('account.saved'));
                        else { box.checked = !box.checked; this.revealFold(box); }
                    });
                });
            };
            toggle('account-ask-pin', 'askPinToOpen');
            toggle('account-lan-bypass', 'bypassPinOnLocalNetwork');

            const lockSel = content.querySelector('#account-lockout-select');
            lockSel.addEventListener('change', () => {
                const before = main.lockoutMinutes;
                saveAccount({ lockoutMinutes: parseInt(lockSel.value, 10) }).then(ok => {
                    if (ok) say(t('account.saved'));
                    else { lockSel.value = String(before); this.revealFold(lockSel); }
                });
            });

            const manage = content.querySelector('#account-manage-devices');
            if (manage) manage.addEventListener('click', () => this.showAccountDevices());
            this._bindAccountEmail(content.querySelector('#account-email-slot'));
            this._bindAccountZone(content.querySelector('#account-zone-slot'), saveAccount, say);

            content.querySelector('#account-back-btn').addEventListener('click', () => this.showSettingsMenu());
            this.initTVCheckboxes(content);
        },

        /// The household's time zone, which viewing hours are read in. Drawn once the
        /// server has listed its zones; a server too old to list them shows nothing.
        _bindAccountZone: function (slot, saveAccount, say) {
            if (!slot) return;
            const masterState = this.readMasterState();
            if (!masterState) return;
            fetch(ApiClient.getUrl('plugins/profiles/timezones'), { headers: this.getAuthHeaders(masterState.token) })
                .then(res => (res.ok ? res.json() : null))
                .then(data => {
                    if (!data || !document.body.contains(slot)) return;
                    const zones = this._field(data, 'zones') || [];
                    const chosen = this._field(data, 'household') || '';
                    const fallback = this._field(data, 'default') || this._field(data, 'server') || '';
                    slot.innerHTML = this.renderSection('schedule', t('account.zoneTitle'), t('account.zoneSubtitle'), `
                        <div class="form-group">
                            <label for="account-zone-select">${t('account.zone')}</label>
                            <select id="account-zone-select">
                                <option value="">${t('account.zoneDefault', { zone: escapeHtml(fallback) })}</option>
                                ${zones.map(z => `<option value="${escapeHtml(z)}"${z === chosen ? ' selected' : ''}>${escapeHtml(z.replace(/_/g, ' '))}</option>`).join('')}
                            </select>
                        </div>`);
                    const select = slot.querySelector('#account-zone-select');
                    let before = select.value;
                    select.addEventListener('change', () => {
                        saveAccount({ timeZone: select.value }).then(ok => {
                            if (ok) { before = select.value; say(t('account.saved')); }
                            else select.value = before;
                        });
                    });
                })
                .catch(() => {});
        },

        /// Rename, merge, forget and tidy, for the rows _renderAccountSettings draws.
        ///
        /// Each posts and then redraws the page through refresh(), rather than patching the
        /// row: a merge changes which devices exist, and a redraw from the server's list is
        /// the only way the page can't describe a list that no longer exists.
        bindDeviceTools: function (content, masterState, refresh) {
            const apiClient = ApiClient;
            const deviceAction = (path, body, failKey) => {
                // Some of these take their argument in the query string and no body at
                // all. Sending the four bytes "null" for those is not harmless: ASP.NET
                // binds it as an explicit null, which is a different thing from absent.
                const init = {
                    method: 'POST',
                    headers: this.manageHeaders(masterState.token)
                };
                if (body !== null && body !== undefined) init.body = JSON.stringify(body);

                fetch(apiClient.getUrl('plugins/profiles/devices/' + path), init)
                .then(res => {
                    if (res.ok) {
                        // The device list is part of the account-wide cache the profile
                        // forms share; left, the next form would list what was just changed.
                        this.clearSharedFormData();
                        refresh();
                        return null;
                    }
                    // Forget is refused when the device is the only one a profile may be
                    // opened on, and the server says which profiles: forgetting it would
                    // empty their list, and an empty list means "any device". That reason
                    // is shown, not a generic failure.
                    return res.text().then(text => {
                        this.showAlert(t('errors.error'), escapeHtml(text && text.trim()) || t(failKey));
                    });
                })
                .catch(err => this.showAlert(t('errors.error'), t('errors.withMessage', { message: escapeHtml(err.message) })));
            };

            content.querySelectorAll('.device-delete-btn').forEach(btn => {
                btn.addEventListener('click', (e) => {
                    e.preventDefault();
                    this.showConfirmDialog(t('profileForm.deleteDeviceTitle'), t('profileForm.deleteDeviceMessage'),
                        () => deviceAction('delete', { deviceId: btn.getAttribute('data-id') }, 'errors.failedDeleteDevice'));
                });
            });

            // Renaming is what makes two ids for one machine tellable apart: jellyfin-web
            // keeps its id per origin, so the LAN address and the domain name give one
            // computer two devices, as do a new browser profile or a reinstall. The name
            // sticks — see KnownDevice.NameIsCustom.
            content.querySelectorAll('.device-rename-btn').forEach(btn => {
                btn.addEventListener('click', (e) => {
                    e.preventDefault();
                    this.showPromptDialog(
                        t('profileForm.renameDeviceTitle'),
                        t('profileForm.renameDeviceLabel'),
                        btn.getAttribute('data-name'),
                        (name) => deviceAction('rename',
                            { deviceId: btn.getAttribute('data-id'), deviceName: name },
                            'errors.failedRenameDevice'));
                });
            });

            // Nothing in a request tells one machine's two ids from two real machines, so
            // the plugin never merges on a guess: merging two that are different would
            // widen a profile's list. A person says so here.
            content.querySelectorAll('.device-merge-btn').forEach(btn => {
                btn.addEventListener('click', (e) => {
                    e.preventDefault();
                    const fromId = btn.getAttribute('data-id');
                    const others = Array.from(content.querySelectorAll('.device-dropdown-item'))
                        .map(row => ({
                            id: row.getAttribute('data-device-id'),
                            name: (row.querySelector('.device-row-name') || {}).textContent || ''
                        }))
                        .filter(d => d.id && d.id !== fromId);
                    if (others.length === 0) return;
                    this.showSelectDialog(
                        t('profileForm.mergeDeviceTitle'),
                        t('profileForm.mergeDeviceMessage'),
                        others,
                        (intoId) => deviceAction('merge',
                            { fromDeviceId: fromId, intoDeviceId: intoId },
                            'errors.failedMergeDevice'));
                });
            });

            // One browser that has minted a new id every time its site data was cleared
            // leaves four or five identical-looking rows, and merging them a pair at a
            // time is the kind of chore nobody finishes. Two calls: the first only
            // reports, the second acts.
            const tidyBtn = content.querySelector('#devices-tidy-btn');
            if (tidyBtn) {
                tidyBtn.addEventListener('click', (e) => {
                    e.preventDefault();
                    fetch(apiClient.getUrl('plugins/profiles/devices/merge-duplicates'), {
                        method: 'POST',
                        headers: this.manageHeaders(masterState.token)
                    })
                    .then(res => res.json())
                    .then(data => {
                        const count = (data.groups || [])
                            .reduce((n, g) => n + (g.merge ? g.merge.length : 0), 0);
                        if (!count) {
                            this.showAlert(t('profileForm.tidyDevices'), t('profileForm.tidyNone'));
                            return;
                        }
                        this.showConfirmDialog(
                            t('profileForm.tidyDevices'),
                            t('profileForm.tidyFound', { count: count }),
                            () => deviceAction('merge-duplicates?apply=true', null, 'errors.failedMergeDevice'));
                    })
                    .catch(err => this.showAlert(t('errors.error'),
                        t('errors.withMessage', { message: escapeHtml(err.message) })));
                });
            }
        },

        /// The auto-lock choices. `isNew` marks five minutes as the default in its label.
        renderLockoutOptions: function (current, isNew) {
            return [
                [0, 'profileForm.lockoutNever'],
                [1, 'profileForm.lockout1Min'],
                [5, isNew ? 'profileForm.lockout5MinDefault' : 'profileForm.lockout5Min'],
                [10, 'profileForm.lockout10Min'],
                [20, 'profileForm.lockout20Min'],
                [30, 'profileForm.lockout30Min'],
                [60, 'profileForm.lockout1Hour']
            ].map(([v, key]) => `<option value="${v}" ${current === v ? 'selected' : ''}>${t(key)}</option>`).join('');
        },

        // ─── First-time setup (1.7.2) ────────────────────────────────────────────

        /// Asks the server whether this account should be walked through first-time setup,
        /// and if so starts it. At most once per page load, and only after /preferences has
        /// said it is pending, so nobody else pays a request for it. Never from checkRoute.
        ///
        /// The server decides who sees it: main accounts only, once per account rather than
        /// per device, and only while the administrator has it on.
        maybeStartOnboarding: function () {
            if (this._onboardingChecked) return;
            this._onboardingChecked = true;
            if (typeof ApiClient === 'undefined' || !ApiClient || !ApiClient.accessToken()) return;

            fetch(ApiClient.getUrl('plugins/profiles/onboarding'), {
                cache: 'no-store',
                headers: this.getAuthHeaders(ApiClient.accessToken())
            })
            .then(res => res.ok ? res.json() : null)
            .then(data => {
                if (!data || (data.pending !== undefined ? data.pending : data.Pending) !== true) return;
                const steps = (data.steps || data.Steps || []).map(s => ({
                    id: s.id || s.Id,
                    required: (s.required !== undefined ? s.required : s.Required) === true,
                    done: (s.done !== undefined ? s.done : s.Done) === true
                })).filter(s => ONBOARDING_STEPS.indexOf(s.id) !== -1);
                // Already true when setup began: passed over, and not counted in "Step n of m".
                steps.forEach(s => { s.given = s.done; });
                if (!steps.length) return;

                this._onboarding = {
                    steps: steps,
                    welcome: String(data.welcomeText || data.WelcomeText || ''),
                    index: 0,
                    waiting: true
                };
                // On the gate already: take over its content. Otherwise open the overlay,
                // and showProfileOverlay hands it to the setup before the grid is painted.
                if (document.getElementById('profiles-gate-overlay') && document.querySelector('.profiles-modal-content')) {
                    this._onboarding.waiting = false;
                    this.showOnboardingStep();
                } else {
                    this.fetchAndRenderProfiles(ApiClient, ApiClient.getCurrentUserId(), ApiClient.accessToken(), /* forceRefresh */ true);
                }
            })
            .catch(() => { /* not shown this time; it stays pending on the server */ });
        },

        /// Draws the current step. Steps already true of the account (a PIN set, a profile
        /// made) are passed over. Every screen redrawn by the overlay while setup runs comes
        /// back here, so a slow grid arriving late can't paint over a step.
        showOnboardingStep: function () {
            const ob = this._onboarding;
            if (!ob) return;
            while (ob.index < ob.steps.length && ob.steps[ob.index].done) ob.index++;
            if (ob.index >= ob.steps.length) { this.finishOnboarding(); return; }

            const step = ob.steps[ob.index];
            const next = () => {
                if (this._onboarding !== ob) return;
                step.done = true;
                ob.index++;
                this.showOnboardingStep();
            };
            const skip = step.required ? null : () => {
                if (this._onboarding !== ob) return;
                ob.index++;
                this.showOnboardingStep();
            };

            if (step.id === 'mainPin') {
                this.promptSetMainPin(this.currentProfiles, false, next, { onSkip: skip });
                return;
            }
            if (step.id === 'welcome') this._onboardingWelcome(next);
            else if (step.id === 'resetEmail') this._onboardingEmail(next, skip);
            else if (step.id === 'firstProfile') this._onboardingProfile(next, skip);
            else if (step.id === 'switcher') this._onboardingSwitcher(next, skip);
            else if (step.id === 'theme') this._onboardingTheme(next, skip);
            else skip ? skip() : next();
        },

        /// The frame every step but the PIN shares: title, body, an error line, and the
        /// buttons. A required step has no Skip.
        _onboardingFrame: function (title, body, nextLabel, skip) {
            this.beginNavigation();
            const content = document.querySelector('.profiles-modal-content');
            if (!content) return null;
            content.innerHTML = `
                <h1 class="profiles-title">${title}</h1>
                <div class="create-profile-container jpf-onboarding" style="max-width: var(--jpf-w-form); width: 100%;">
                    ${this._onboardingStepLine()}
                    ${body}
                    <div id="onboarding-error" class="profiles-pin-error" role="alert" hidden></div>
                    <div class="bonfire-dialog-actions jpf-actions jpf-onboarding-actions">
                        ${skip ? `<button id="onboarding-skip" class="profiles-btn btn-secondary">${t('onboarding.skip')}</button>` : ''}
                        <button id="onboarding-next" class="profiles-btn btn-primary">${nextLabel}</button>
                    </div>
                </div>
            `;
            const skipBtn = content.querySelector('#onboarding-skip');
            if (skipBtn) skipBtn.addEventListener('click', skip);
            return content;
        },

        /// "Step 2 of 5" for the step on screen, or '' with nothing to count. The PIN step
        /// draws its own screen and calls this too, so it is numbered like the rest (P2-110).
        _onboardingStepLine: function () {
            const ob = this._onboarding;
            if (!ob) return '';
            const shown = ob.steps.filter(s => s.id !== 'welcome' && !s.given);
            const position = shown.indexOf(ob.steps[ob.index]);
            return position !== -1 && shown.length > 1
                ? `<p class="jpf-onboarding-step">${t('onboarding.step', { n: position + 1, total: shown.length })}</p>` : '';
        },

        _onboardingError: function (content, message) {
            const el = content.querySelector('#onboarding-error');
            if (!el) return;
            el.textContent = message || t('errors.couldNotSaveThat');
            el.hidden = false;
        },

        /// The administrator's welcome, as plain text: escaped, line breaks kept.
        _onboardingWelcome: function (next) {
            const own = this._onboarding.welcome.trim();
            const body = own
                ? escapeHtml(own).replace(/\r?\n/g, '<br>')
                : t('onboarding.welcomeBody');
            const content = this._onboardingFrame(t('onboarding.welcomeTitle'),
                `<p class="jpf-onboarding-text">${body}</p>`, t('onboarding.start'), null);
            if (!content) return;
            const go = content.querySelector('#onboarding-next');
            go.addEventListener('click', next);
            setTimeout(() => go.focus(), 50);
        },

        /// An address for a forgotten PIN and notices: the address, then the code mailed to
        /// it. The server offers this step only while email works. `sentTo` is the second
        /// screen; `refill` puts back what was typed when a refusal redraws the first.
        _onboardingEmail: function (next, skip, sentTo, refill) {
            const noFill = 'data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true"';
            const content = this._onboardingFrame(t('onboarding.emailTitle'), sentTo ? `
                <p class="jpf-onboarding-text" id="onboarding-email-sent"></p>
                <div class="bonfire-form-group">
                    <label for="onboarding-email-code">${t('accountEmail.code')}</label>
                    <input type="text" id="onboarding-email-code" maxlength="7" inputmode="numeric" autocomplete="one-time-code" ${noFill} />
                </div>
                <button type="button" id="onboarding-email-other" class="jpf-text-link">${t('accountEmail.otherAddress')}</button>
            ` : `
                <p class="jpf-onboarding-text">${t('onboarding.emailBody')}</p>
                <div class="bonfire-form-group">
                    <label for="onboarding-email">${t('accountEmail.address')}</label>
                    <input type="email" id="onboarding-email" maxlength="254" autocomplete="email" />
                </div>
            `, sentTo ? t('accountEmail.confirm') : t('accountEmail.send'), skip);
            if (!content) return;

            // withMainPin may have drawn its own screen; come back to this one to say why.
            const fail = (message, typed) => {
                this._onboardingEmail(next, skip, sentTo, typed);
                const again = document.querySelector('.profiles-modal-content');
                if (again) this._onboardingError(again, message);
            };
            let busy = false;

            if (sentTo) {
                content.querySelector('#onboarding-email-sent').textContent = t('accountEmail.sentTo', { address: sentTo });
                const input = content.querySelector('#onboarding-email-code');
                const confirm = () => {
                    const code = input.value.replace(/\s/g, '');
                    if (!/^\d{6}$/.test(code)) { this._onboardingError(content, t('accountEmail.codeInvalid')); input.focus(); return; }
                    if (busy) return;
                    busy = true;
                    this.withMainPin(this.currentProfiles, () => {
                        this._emailCall('account/email/confirm', { code: code })
                            .then(() => next(), err => fail(err.message));
                    });
                };
                content.querySelector('#onboarding-next').addEventListener('click', confirm);
                input.addEventListener('keydown', (e) => { if (e.key === 'Enter') confirm(); });
                content.querySelector('#onboarding-email-other').addEventListener('click', () => this._onboardingEmail(next, skip));
                setTimeout(() => input.focus(), 50);
                return;
            }

            const input = content.querySelector('#onboarding-email');
            if (refill) input.value = refill;
            const send = () => {
                const address = input.value.trim();
                if (!/^[^\s@,;]+@[^\s@,;]+\.[^\s@,;]+$/.test(address)) { this._onboardingError(content, t('accountEmail.invalid')); input.focus(); return; }
                if (busy) return;
                busy = true;
                this.withMainPin(this.currentProfiles, () => {
                    this._emailCall('account/email', { address: address })
                        .then(reply => this._onboardingEmail(next, skip, this._field(reply, 'pendingAddress') || address),
                              err => fail(err.message, address));
                });
            };
            content.querySelector('#onboarding-next').addEventListener('click', send);
            input.addEventListener('keydown', (e) => { if (e.key === 'Enter') send(); });
            setTimeout(() => input.focus(), 50);
        },

        /// A first profile, by name only. Everything else has a default and lives in the
        /// profile's own form.
        _onboardingProfile: function (next, skip) {
            const content = this._onboardingFrame(t('onboarding.profileTitle'), `
                <p class="jpf-onboarding-text">${t('onboarding.profileBody')}</p>
                <div class="bonfire-form-group">
                    <label for="onboarding-profile-name">${t('profileForm.profileName')}</label>
                    <input type="text" id="onboarding-profile-name" maxlength="32" placeholder="${t('profileForm.namePlaceholder')}" />
                </div>
            `, t('common.create'), skip);
            if (!content) return;
            const input = content.querySelector('#onboarding-profile-name');
            let busy = false;
            const create = () => {
                const name = input.value.trim();
                if (!name) { this._onboardingError(content, t('profileForm.nameRequired')); input.focus(); return; }
                if (busy) return;
                busy = true;
                this.withMainPin(this.currentProfiles, () => {
                    const masterState = this.readMasterState();
                    fetch(ApiClient.getUrl('plugins/profiles/create'), {
                        method: 'POST',
                        headers: this.jsonHeaders(masterState ? masterState.token : ApiClient.accessToken()),
                        body: JSON.stringify({ profileName: name, avatarColor: this.nextAvatarColor(), masterPin: this.masterPin })
                    })
                    .then(res => res.ok ? null : res.text().then(text => { throw new Error(text); }))
                    .then(() => { this.invalidateProfileCache(); next(); })
                    .catch(err => {
                        busy = false;
                        // withMainPin may have drawn its own screen; come back to this one.
                        this.showOnboardingStep();
                        const again = document.querySelector('.profiles-modal-content');
                        const field = again && again.querySelector('#onboarding-profile-name');
                        if (field) field.value = name;
                        if (again) this._onboardingError(again, err.message);
                    });
                });
            };
            content.querySelector('#onboarding-next').addEventListener('click', create);
            input.addEventListener('keydown', (e) => { if (e.key === 'Enter') create(); });
            setTimeout(() => input.focus(), 50);
        },

        /// Ask on startup, and where to switch from. The same two questions as Settings →
        /// Switcher, saved together when Next is pressed.
        _onboardingSwitcher: function (next, skip) {
            const prefs = this.getSwitcherPrefs() || { askOnStartup: true, location: 'button' };
            let location = prefs.location === 'menu' ? 'menu' : 'button';
            const option = (value, icon, title, body) => `
                <div class="switcher-location-option jpf-choice focusable" data-location="${value}" tabindex="0" role="radio"
                     aria-checked="${location === value}">
                    <span class="material-icons jpf-choice-icon">${icon}</span>
                    <div style="flex: 1 1 auto; min-width: 0;">
                        <div style="font-weight: 700; font-size: 1rem; margin-bottom: 4px;">${title}</div>
                        <div style="font-size: 0.85rem; opacity: 0.7; line-height: 1.5;">${body}</div>
                    </div>
                </div>
            `;
            const content = this._onboardingFrame(t('onboarding.switcherTitle'), `
                <label class="library-check-label jpf-onboarding-check">
                    <input type="checkbox" id="onboarding-ask" ${prefs.askOnStartup ? 'checked' : ''} />
                    <span>${t('switcher.askOnStartup')}</span>
                </label>
                <div class="form-hint">${t('switcher.askOnStartupHint')}</div>
                <div class="jpf-onboarding-label">${t('switcher.whereToSwitchFrom')}</div>
                <div role="radiogroup" class="jpf-onboarding-choices">
                    ${option('button', 'account_circle', t('switcher.bonfireButtonTitle'), t('switcher.bonfireButtonBody'))}
                    ${option('menu', 'switch_account', t('switcher.menuTitle'), t('switcher.menuBody'))}
                </div>
                <div class="form-hint" id="onboarding-switcher-later">${t('onboarding.switcherLater')}</div>
            `, t('onboarding.next'), skip);
            if (!content) return;

            const cards = content.querySelectorAll('.switcher-location-option');
            cards.forEach(card => {
                const choose = () => {
                    location = card.getAttribute('data-location');
                    cards.forEach(c => c.setAttribute('aria-checked', String(c === card)));
                };
                card.addEventListener('click', choose);
                card.addEventListener('keydown', (e) => {
                    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); choose(); }
                });
            });
            this.initTVCheckboxes(content);

            content.querySelector('#onboarding-next').addEventListener('click', () => {
                const ask = content.querySelector('#onboarding-ask').checked;
                this.withMainPin(this.currentProfiles, () => {
                    const masterState = this.readMasterState();
                    fetch(ApiClient.getUrl('plugins/profiles/preferences'), {
                        method: 'POST',
                        headers: this.manageHeaders(masterState ? masterState.token : ApiClient.accessToken()),
                        body: JSON.stringify({ askOnStartup: ask, switcherLocation: location })
                    })
                    .then(res => res.ok ? res.json() : res.text().then(text => { throw new Error(text); }))
                    .then(saved => {
                        const savedAsk = (saved.askOnStartup !== undefined ? saved.askOnStartup : saved.AskOnStartup) === true;
                        const savedLoc = (saved.switcherLocation || saved.SwitcherLocation) === 'menu' ? 'menu' : 'button';
                        this._cacheSwitcherPrefs(savedAsk, savedLoc, masterState ? masterState.masterUserId : ApiClient.getCurrentUserId());
                        next();
                    })
                    .catch(err => {
                        this.showOnboardingStep();
                        const again = document.querySelector('.profiles-modal-content');
                        if (again) this._onboardingError(again, err.message);
                    });
                });
            });
            setTimeout(() => {
                const first = content.querySelector('.switcher-location-option[aria-checked="true"]');
                if (first) first.focus();
            }, 50);
        },

        /// The main account's own theme. Profiles choose theirs in Settings → Look.
        _onboardingTheme: function (next, skip) {
            const settings = this._themeSettings;
            const main = (this.currentProfiles || []).find(p => p.isMaster && !p.isBonfire);
            if (!settings || !settings.allow || !main) { (skip || next)(); return; }

            const stored = offeredChoice(main.theme);
            let chosen = stored || settings.defaultTheme;
            const option = (id) => `
                <button type="button" class="jpf-theme-option focusable" role="radio" data-id="${id}"
                        aria-checked="${id === chosen}">
                    ${this.renderThemePreview(id)}
                    <span class="jpf-theme-option-label">
                        <span>${themeName(id)}</span>
                        <span class="material-icons jpf-theme-option-check" aria-hidden="true">check_circle</span>
                    </span>
                </button>
            `;
            const content = this._onboardingFrame(t('onboarding.themeTitle'), `
                <p class="jpf-onboarding-text">${t('onboarding.themeBody')}</p>
                <div class="jpf-theme-grid" role="radiogroup">${offeredThemes().map(option).join('')}</div>
            `, t('onboarding.next'), skip);
            if (!content) return;

            const options = content.querySelectorAll('.jpf-theme-option');
            options.forEach(btn => btn.addEventListener('click', () => {
                chosen = btn.getAttribute('data-id');
                options.forEach(o => o.setAttribute('aria-checked', String(o === btn)));
            }));

            content.querySelector('#onboarding-next').addEventListener('click', () => {
                if (chosen === (stored || settings.defaultTheme)) { next(); return; }
                this.withMainPin(this.currentProfiles, () => {
                    const masterState = this.readMasterState();
                    fetch(ApiClient.getUrl('plugins/profiles/theme'), {
                        method: 'POST',
                        headers: this.jsonHeaders(masterState ? masterState.token : ApiClient.accessToken()),
                        // The default theme goes on the wire as '', so the account follows
                        // the server's default if it changes — as Settings → Look does.
                        body: JSON.stringify({
                            profileId: main.profileUserId,
                            theme: chosen === settings.defaultTheme ? '' : chosen,
                            masterPin: this.masterPin
                        })
                    })
                    .then(res => res.ok ? res.json() : res.text().then(text => { throw new Error(text); }))
                    .then(saved => {
                        main.theme = (saved.stored !== undefined ? saved.stored : saved.Stored) || null;
                        if (this.normalizeGuid(main.profileUserId) === this.normalizeGuid(ApiClient.getCurrentUserId())) {
                            this._setThemeSettings({
                                theme: normalizeTheme(saved.theme || saved.Theme),
                                defaultTheme: settings.defaultTheme,
                                allowProfileThemes: settings.allow
                            });
                        }
                        next();
                    })
                    .catch(err => {
                        this.showOnboardingStep();
                        const again = document.querySelector('.profiles-modal-content');
                        if (again) this._onboardingError(again, err.message);
                    });
                });
            });
            setTimeout(() => {
                const first = content.querySelector('.jpf-theme-option[aria-checked="true"]');
                if (first) first.focus();
            }, 50);
        },

        /// Records the setup as seen and puts the person where they would have been: the
        /// grid if this account asks on startup, otherwise straight into Jellyfin. The
        /// main PIN typed during setup is forgotten, as it is when Settings closes.
        finishOnboarding: function () {
            this._onboarding = null;
            this.masterPin = null;
            if (typeof ApiClient !== 'undefined' && ApiClient && ApiClient.accessToken()) {
                fetch(ApiClient.getUrl('plugins/profiles/onboarding/done'), {
                    method: 'POST',
                    headers: this.getAuthHeaders(ApiClient.accessToken())
                }).catch(() => { /* shown again next time, which is the honest outcome */ });
            }
            if (this.shouldAskOnStartup() && !this.isProfileSessionActive()) {
                const st = this.readMasterState();
                if (st) {
                    this.fetchAndRenderProfiles(ApiClient, st.masterUserId, st.token, /* forceRefresh */ true);
                    return;
                }
            }
            this.removeProfileOverlay();
        },

        /// The main account's Settings, in five sections (1.7.2): Profiles, Your account,
        /// Switcher, Sharing, Look.
        ///
        /// Opened through withMainPin, so the main PIN is asked once on the way in and every
        /// page below sends it; the server checks it again on each save. Profiles opens the
        /// manage grid rather than a second list of the same people. Server-wide settings
        /// stay on the dashboard.
        showSettingsMenu: function () {
            this.beginNavigation();

            const content = document.querySelector('.profiles-modal-content');
            if (!content) return;

            const entry = (id, icon, title, body) => `
                <div class="settings-menu-entry jpf-choice focusable" id="${id}" tabindex="0" role="button">
                    <span class="material-icons jpf-choice-icon">${icon}</span>
                    <div style="flex: 1 1 auto; min-width: 0;">
                        <div style="font-weight: 700; font-size: 1rem; margin-bottom: 4px;">${title}</div>
                        <div style="font-size: 0.85rem; opacity: 0.7; line-height: 1.5;">${body}</div>
                    </div>
                </div>
            `;

            content.innerHTML = `
                <h1 class="profiles-title">${t('settings.title')}</h1>
                <div class="create-profile-container" style="max-width: var(--jpf-w-form); width: 100%;">
                    <div style="display: flex; flex-direction: column; gap: var(--jpf-gap); width: 100%;">
                        ${entry('settings-profiles', 'people', t('settings.profilesTitle'),
                            t('settings.profilesBody'))}
                        ${entry('settings-account', 'manage_accounts', t('settings.accountTitle'),
                            t('settings.accountBody'))}
                        ${entry('settings-switcher-style', 'switch_account', t('settings.switcherTitle'),
                            t('settings.switcherStyleBody'))}
                        ${entry('settings-your-bonfire', 'local_fire_department', t('settings.sharingTitle'),
                            t('settings.yourBonfireBody'))}
                        ${this._themeSettings && this._themeSettings.allow
                            ? entry('settings-theme', 'palette', t('settings.lookTitle'), t('settings.themeBody'))
                            : ''}
                    </div>
                    <div class="bonfire-dialog-actions jpf-actions" style="margin-top: 2rem !important; display: flex !important; justify-content: flex-start !important; width: 100% !important;">
                        <button id="settings-back-btn" class="profiles-btn btn-secondary" style="padding: 10px 24px !important; font-size: 1rem !important; margin: 0 !important; width: auto !important;">${t('common.back')}</button>
                    </div>
                </div>
            `;

            const open = (id, fn) => {
                const el = content.querySelector('#' + id);
                if (!el) return;
                el.addEventListener('click', () => fn.call(this));
                el.addEventListener('keydown', (e) => {
                    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); el.click(); }
                });
            };
            open('settings-profiles', () => {
                const st = this.readMasterState();
                if (!st) return;
                // The PIN typed to open Settings is the one Manage Profiles would ask for.
                this.isManageMode = true;
                this.fetchAndRenderProfiles(ApiClient, st.masterUserId, st.token, /* forceRefresh */ true);
            });
            open('settings-account', this.showAccountSettings);
            open('settings-switcher-style', this.showSwitcherModeModal);
            open('settings-your-bonfire', this.showBonfireModal);
            open('settings-theme', () => this.showThemeSettings(null));

            const back = content.querySelector('#settings-back-btn');
            if (back) {
                back.addEventListener('click', () => {
                    // Manage Profiles may still be open underneath, and still needs it.
                    if (!this.isManageMode) this.masterPin = null;
                    const st = this.readMasterState();
                    if (st) {
                        this.fetchAndRenderProfiles(ApiClient, st.masterUserId, st.token, /* forceRefresh */ true);
                    }
                });
            }

        },

        /// Settings → Theme: every theme as a small preview, for one profile at a time.
        ///
        /// The profile form already had a picker, but nobody looks for a theme inside a
        /// profile's restrictions; Settings is where it was looked for and not found. Each
        /// choice saves at once through /theme, which changes nothing else on the profile,
        /// and when the profile is the one signed in the screen changes with it.
        showThemeSettings: function (selectedId) {
            this.beginNavigation();
            const content = document.querySelector('.profiles-modal-content');
            const settings = this._themeSettings;
            const masterState = this.readMasterState();
            if (!content || !settings || !settings.allow || !masterState) {
                this.showSettingsMenu();
                return;
            }

            // This household's own profiles; a linked Bonfire's are theirs to style.
            const people = (this.currentProfiles || []).filter(p => !p.isBonfire);
            if (!people.length) { this.showSettingsMenu(); return; }

            const signedIn = this.normalizeGuid(ApiClient.getCurrentUserId());
            const pick = (id) => people.find(p => this.normalizeGuid(p.profileUserId) === this.normalizeGuid(id));
            const person = pick(selectedId) || pick(signedIn) || people.find(p => p.isMaster) || people[0];
            const stored = offeredChoice(person.theme);
            // What the profile is wearing: its own choice, or the server's default.
            const current = stored || settings.defaultTheme;

            // One card per theme. There used to be a separate "Server default (Crimson)"
            // card in front of a Crimson card that looked identical, and which of the two
            // was ticked depended on how the choice had been stored. Now the default theme's
            // own card carries the tag, and choosing it follows the default ('' on the wire),
            // so a profile set that way changes when the server's default does.
            const option = (id) => `
                <button type="button" class="jpf-theme-option focusable" role="radio"
                        data-theme="${id === settings.defaultTheme ? '' : id}"
                        aria-checked="${id === current}">
                    ${this.renderThemePreview(id)}
                    <span class="jpf-theme-option-label">
                        <span>${themeName(id)}${id === settings.defaultTheme
                            ? ` <span class="jpf-theme-option-tag">${t('themeSettings.default')}</span>` : ''}</span>
                        <span class="material-icons jpf-theme-option-check" aria-hidden="true">check_circle</span>
                    </span>
                </button>
            `;

            // jpf-theme-page: no sheet behind the cards in any theme. Bonfire's sheet and
            // its padding left room for three previews a row where every other theme
            // showed four, so the page looked different only in Bonfire.
            content.innerHTML = `
                <h1 class="profiles-title">${t('settings.themeTitle')}</h1>
                <div class="create-profile-container jpf-theme-page">
                    ${people.length > 1 ? `
                    <div class="jpf-theme-people" role="radiogroup" aria-label="${t('settings.themeTitle')}">
                        ${people.map(p => `
                            <button type="button" class="jpf-theme-person focusable" role="radio"
                                    data-id="${escapeHtml(p.profileUserId)}"
                                    aria-checked="${p === person}">${escapeHtml(p.profileName)}</button>
                        `).join('')}
                    </div>` : ''}
                    <div class="jpf-theme-grid" role="radiogroup" aria-label="${escapeHtml(person.profileName)}">
                        ${offeredThemes().map(option).join('')}
                    </div>
                    <div class="jpf-theme-status" id="theme-settings-status" role="status"></div>
                    <div class="jpf-theme-match" id="theme-settings-match" hidden></div>
                    <div class="bonfire-dialog-actions jpf-actions" style="margin-top: 1rem !important; display: flex !important; justify-content: flex-start !important; width: 100% !important;">
                        <button id="theme-settings-back-btn" class="profiles-btn btn-secondary" style="padding: 10px 24px !important; font-size: 1rem !important; margin: 0 !important; width: auto !important;">${t('common.done')}</button>
                    </div>
                </div>
            `;

            const status = content.querySelector('#theme-settings-status');
            const back = content.querySelector('#theme-settings-back-btn');
            if (back) back.addEventListener('click', () => this.showSettingsMenu());

            content.querySelectorAll('.jpf-theme-person').forEach(btn => {
                btn.addEventListener('click', () => this.showThemeSettings(btn.getAttribute('data-id')));
            });

            let busy = false;
            content.querySelectorAll('.jpf-theme-option').forEach(btn => {
                btn.addEventListener('click', () => {
                    const value = btn.getAttribute('data-theme') || '';
                    if (busy || btn.getAttribute('aria-checked') === 'true') return;
                    busy = true;
                    status.classList.remove('is-error');
                    status.textContent = '';
                    fetch(ApiClient.getUrl('plugins/profiles/theme'), {
                        method: 'POST',
                        headers: this.jsonHeaders(masterState.token),
                        body: JSON.stringify({ profileId: person.profileUserId, theme: value, masterPin: this.masterPin })
                    })
                    .then(res => res.ok ? res.json() : res.text().then(text => { throw new Error(text || t('errors.couldNotSaveThat')); }))
                    .then(saved => {
                        const shownNow = normalizeTheme(saved.theme || saved.Theme);
                        const keep = saved.stored !== undefined ? saved.stored : saved.Stored;
                        person.theme = keep || null;
                        if (this.normalizeGuid(person.profileUserId) === signedIn) {
                            this._setThemeSettings({
                                theme: shownNow,
                                defaultTheme: settings.defaultTheme,
                                allowProfileThemes: settings.allow
                            });
                        }
                        // No "Saved." line: the tick moving to the card is the confirmation.
                        // The status line is for a failure only.
                        this.showThemeSettings(person.profileUserId);
                        this._offerJellyfinTheme(person, shownNow);
                        const chosen = document.querySelector('.jpf-theme-option[aria-checked="true"]');
                        if (chosen) chosen.focus();
                    })
                    .catch(err => {
                        busy = false;
                        status.classList.add('is-error');
                        status.textContent = escapeHtml(err.message || t('errors.couldNotSaveThat'));
                    });
                });
            });

            this.initTVCheckboxes(content);
            setTimeout(() => {
                const first = content.querySelector('.jpf-theme-option[aria-checked="true"]')
                    || content.querySelector('.jpf-theme-option');
                if (first) first.focus();
            }, 150);
        },

        /// After Settings → Theme gives a profile Jellyfin Dark or Light: offers to set that
        /// profile's own jellyfin-web theme on this device to match, and does nothing unless
        /// the offer is taken. Any profile of the household, not only the one signed in: the
        /// setting is keyed by user id, so it waits for that profile to open here. See
        /// JELLYFIN_WEB_THEMES for where jellyfin-web keeps it.
        _offerJellyfinTheme: function (person, theme) {
            const key = jellyfinWebThemeKey(person && person.profileUserId);
            const box = document.querySelector('#theme-settings-match');
            if (!key || !box) return;
            let stored = null;
            try { stored = localStorage.getItem(key); } catch (e) { return; }
            const offer = jellyfinWebOffer(theme, stored);
            if (!offer) return;
            box.innerHTML = `
                <p class="jpf-theme-match-text">${escapeHtml(t('themeSettings.matchJellyfin', { name: themeNameText(theme) }))}</p>
                <div class="jpf-theme-match-actions">
                    <button type="button" id="theme-match-yes" class="profiles-btn btn-secondary focusable">${t('themeSettings.matchYes')}</button>
                    <button type="button" id="theme-match-no" class="profiles-btn btn-secondary focusable">${t('themeSettings.matchNo')}</button>
                </div>`;
            box.hidden = false;
            box.querySelector('#theme-match-yes').addEventListener('click', () => {
                let done = true;
                try { localStorage.setItem(key, offer); } catch (e) { done = false; }
                box.innerHTML = `<p class="jpf-theme-match-text${done ? '' : ' is-error'}">${escapeHtml(t(done ? 'themeSettings.matchDone' : 'errors.couldNotSaveThat'))}</p>`;
            });
            box.querySelector('#theme-match-no').addEventListener('click', () => {
                box.innerHTML = '';
                box.hidden = true;
            });
        },

        showSwitcherModeModal: function () {
            this.beginNavigation();
            const apiClient = ApiClient;
            const masterState = this.readMasterState();
            if (!masterState) return;

            const content = document.querySelector('.profiles-modal-content');
            if (!content) return;

            const prefs = this.getSwitcherPrefs() || { askOnStartup: true, location: 'button' };
            const askOnStartup = prefs.askOnStartup;

            // Two separate questions, deliberately not folded into one list of modes: the
            // combination people asked for in issue #14 — ask on startup, but switch from
            // Jellyfin's menu — is unreachable when it is a single choice.
            const locationOption = (value, icon, title, body) => `
                <div class="switcher-location-option jpf-choice focusable" data-location="${value}" tabindex="0" role="radio"
                     aria-checked="${prefs.location === value}">
                    <span class="material-icons jpf-choice-icon">${icon}</span>
                    <div style="flex: 1 1 auto; min-width: 0;">
                        <div style="font-weight: 700; font-size: 1rem; margin-bottom: 4px;">${title}</div>
                        <div style="font-size: 0.85rem; opacity: 0.7; line-height: 1.5;">${body}</div>
                    </div>
                </div>
            `;

            content.innerHTML = `
                <h1 class="profiles-title">${t('switcher.title')}</h1>
                <div class="create-profile-container" style="max-width: 560px; width: 100%;">
                    <p style="opacity: 0.75; font-size: 0.9rem; line-height: 1.5; margin: 0 0 1.5rem 0; text-align: left;">
                        ${t('switcher.intro')}
                    </p>

                    <div class="bonfire-form-group" style="gap: 4px; text-align: left; margin-bottom: 1.5rem;">
                        <label class="library-check-label" style="display: inline-flex !important; align-items: center !important; gap: 0.5rem !important; cursor: pointer !important; user-select: none !important; font-size: 0.95rem !important; font-weight: 600 !important; position: relative !important;">
                            <input type="checkbox" id="switcher-ask-startup" ${askOnStartup ? 'checked' : ''} style="cursor: pointer !important; accent-color: var(--jpf-accent) !important; position: relative !important; opacity: 1 !important; width: 18px !important; height: 18px !important; margin: 0 !important; padding: 0 !important; flex-shrink: 0 !important;" />
                            <span>${t('switcher.askOnStartup')}</span>
                        </label>
                        <div class="form-hint" style="margin-left: 1.6rem !important; opacity: 0.75 !important; font-size: 0.8rem !important; position: relative !important; display: block !important;">
                            ${t('switcher.askOnStartupHint')}
                        </div>
                    </div>

                    <div style="text-align: left; font-size: 0.95rem; font-weight: 600; margin-bottom: 10px;">${t('switcher.whereToSwitchFrom')}</div>
                    <div role="radiogroup" style="display: flex; flex-direction: column; gap: var(--jpf-gap); width: 100%;">
                        ${locationOption('button', 'account_circle', t('switcher.bonfireButtonTitle'),
                            t('switcher.bonfireButtonBody'))}
                        ${locationOption('menu', 'switch_account', t('switcher.menuTitle'),
                            t('switcher.menuBody'))}
                    </div>

                    <div id="switcher-mode-error" style="display: none; color: var(--jpf-danger); font-size: 0.85rem; font-weight: 600; margin-top: 12px;"></div>
                    <div class="bonfire-dialog-actions jpf-actions" style="margin-top: 2rem !important; display: flex !important; justify-content: flex-start !important; width: 100% !important;">
                        <button id="switcher-mode-back-btn" class="profiles-btn btn-secondary" style="padding: 10px 24px !important; font-size: 1rem !important; margin: 0 !important; width: auto !important;">${t('common.done')}</button>
                    </div>
                </div>
            `;

            // Back to Settings, which is where this was opened from.
            const goBack = () => this.showSettingsMenu();

            const backBtn = content.querySelector('#switcher-mode-back-btn');
            if (backBtn) backBtn.addEventListener('click', goBack);

            const errDiv = content.querySelector('#switcher-mode-error');
            const askCb = content.querySelector('#switcher-ask-startup');

            const save = (askOnStartup, location) => {
                errDiv.style.display = 'none';
                return fetch(apiClient.getUrl('plugins/profiles/preferences'), {
                    method: 'POST',
                    headers: this.manageHeaders(masterState.token),
                    body: JSON.stringify({ askOnStartup: askOnStartup, switcherLocation: location })
                })
                .then(res => res.ok ? res.json() : Promise.reject(new Error(t('errors.couldNotSaveThat'))))
                .then(saved => {
                    const ask = (saved.askOnStartup !== undefined ? saved.askOnStartup : saved.AskOnStartup) === true;
                    const loc = (saved.switcherLocation || saved.SwitcherLocation) === 'menu' ? 'menu' : 'button';
                    this._cacheSwitcherPrefs(ask, loc, masterState.masterUserId);
                    return { askOnStartup: ask, location: loc };
                })
                .catch(err => {
                    errDiv.textContent = err.message || t('errors.couldNotSaveThat');
                    errDiv.style.display = 'block';
                    return null;
                });
            };

            // getSwitcherPrefs() returns null until the account's preferences have loaded,
            // which is reachable here — init() runs before sign-in on a fresh browser, and
            // only the home route retries. Fall back to what the form was rendered with
            // rather than throwing and leaving the controls looking dead.
            const currentPrefs = () => this.getSwitcherPrefs() || prefs;

            if (askCb) {
                askCb.addEventListener('change', () => {
                    save(askCb.checked, currentPrefs().location).then(applied => {
                        // Revert the box if the server refused, so it cannot sit there
                        // showing a state that was never stored.
                        if (!applied) askCb.checked = !askCb.checked;
                    });
                });
            }

            content.querySelectorAll('.switcher-location-option').forEach(el => {
                const choose = () => {
                    const location = el.getAttribute('data-location');
                    if (location === currentPrefs().location) return;

                    const ask = askCb ? askCb.checked : currentPrefs().askOnStartup;

                    save(ask, location).then(applied => {
                        if (!applied) return;
                        // Re-render so the selected card updates, and so the floating button
                        // appears or disappears immediately rather than on the next poll.
                        this.showSwitcherModeModal();
                        this.checkRoute();
                    });
                };

                el.addEventListener('click', choose);
                el.addEventListener('keydown', (e) => {
                    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); choose(); }
                });
            });

            this.initTVCheckboxes(content);

            setTimeout(() => {
                const first = content.querySelector('#switcher-ask-startup');
                if (first) first.focus();
            }, 250);
        },

        executeProfileDeletion: function (profileId) {
            const apiClient = ApiClient;
            const masterState = this.readMasterState();
            if (!masterState) return;

            const url = apiClient.getUrl('plugins/profiles/delete');

            fetch(url, {
                method: 'POST',
                headers: this.jsonHeaders(masterState.token),
                body: JSON.stringify({ 
                    profileId: profileId,
                    masterPin: this.masterPin
                })
            })
            .then(res => {
                if (!res.ok) throw new Error(t('errors.failedDeleteProfile'));
                this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.token, /* forceRefresh */ true);
            })
            .catch(err => this.showAlert(t('errors.error'), t('errors.deletingProfile', { message: escapeHtml(err.message) })));
        },

        loadBonfireStatus: function (content, apiClient, masterToken) {
            const container = content.querySelector('#bonfire-container');
            if (!container) return;

            // Whatever owns the screen right now. If anything claims it before this
            // returns, the response is dropped rather than drawn over the newer screen —
            // the same guard the profile forms use.
            const ticket = this._navTicket;

            const statusUrl = apiClient.getUrl('plugins/profiles/bonfire/status');
            fetch(statusUrl, { headers: this.getAuthHeaders(masterToken) })
            .then(res => {
                if (res.status === 401) {
                    this.handleSessionExpired();
                    throw new Error(t('errors.unauthorized'));
                }
                return res.json();
            })
            .then(status => {
                if (!this.navIsCurrent(ticket)) return;
                this.renderBonfireStatus(container, content, status, apiClient, masterToken);
            })
            .catch(err => {
                if (!this.navIsCurrent(ticket)) return;
                container.innerHTML = `<div style="color: var(--jpf-danger); font-size: 0.9rem;">${t('bonfire.failedLoadStatus', { message: escapeHtml(err.message) })}</div>`;
            });
        },

        renderBonfireStatus: function (container, content, status, apiClient, masterToken) {
            const isOwner = status.isOwner !== undefined ? status.isOwner : status.IsOwner;
            const isMember = status.isMember !== undefined ? status.isMember : status.IsMember;
            const ownedCode = status.ownedCode || status.OwnedCode || '';
            const ownedMembers = status.ownedMembers || status.OwnedMembers || [];
            const joinedOwnerName = escapeHtml(status.joinedOwnerName || status.JoinedOwnerName || '');

            let hostSectionHtml = '';
            if (isOwner) {
                hostSectionHtml = `
                    <div style="display: flex; flex-direction: column; gap: var(--jpf-gap-lg); border-bottom: 1px solid var(--jpf-card-border); padding-bottom: 1.5rem;">
                        <div class="bonfire-form-group">
                            <label style="font-size: 1.1rem; font-weight: 700; display: block; margin-bottom: 4px;">${t('bonfire.hostedTitle')}</label>
                            <span style="font-size: 0.88rem; opacity: 0.75; display: block;">${t('bonfire.shareCode')}</span>
                            <div style="font-size: 2rem; font-weight: 700; letter-spacing: 4px; margin: 12px 0; font-family: monospace; text-align: center; background: rgba(0,0,0,0.3); padding: 12px; border-radius: var(--jpf-r-md); border: 1px solid var(--jpf-accent-a30);">${escapeHtml(ownedCode)}</div>
                            <button type="button" id="bonfire-newcode-btn" class="profiles-btn btn-danger-quiet" style="padding: 8px 16px !important; font-size: 0.9rem !important; margin: 0 auto !important; display: block !important;">${t('bonfire.newCode')}</button>
                        </div>

                        <div class="bonfire-form-group">
                            <label style="font-size: 1rem; font-weight: 600; color: var(--jpf-text-1); display: block; margin-bottom: 8px;">${t('bonfire.members', { count: ownedMembers.length })}</label>
                            <div style="display: flex; flex-direction: column; gap: 8px; max-height: 180px; overflow-y: auto; background: var(--jpf-input-bg); border: 1px solid var(--jpf-input-border); border-radius: var(--jpf-input-r); padding: 8px;">
                                ${ownedMembers.length > 0 ? ownedMembers.map(m => {
                                    const mUserId = m.userId || m.UserId;
                                    const mUsername = escapeHtml(m.username || m.Username) || t('bonfire.unknownUser');
                                    return `
                                    <div style="display: flex; align-items: center; justify-content: space-between; padding: 8px 12px; background: var(--jpf-card-bg); border-radius: var(--jpf-r-sm);">
                                        <span style="font-size: 0.95rem; font-weight: 500;">${mUsername}</span>
                                        <button type="button" class="bonfire-kick-btn" data-id="${escapeHtml(mUserId)}" style="background: var(--jpf-danger) !important; border: none !important; color: #fff !important; padding: 6px 12px !important; border-radius: var(--jpf-r-sm) !important; font-size: 0.85rem !important; cursor: pointer !important; font-weight: 600 !important; transition: background-color 0.2s !important; margin: 0 !important; box-sizing: border-box !important;">${t('bonfire.kick')}</button>
                                    </div>
                                    `;
                                }).join('') : '<div style="font-size: 0.9rem; opacity: 0.5; font-style: italic; text-align: center; padding: 12px;">' + t('bonfire.noMembersYet') + '</div>'}
                            </div>
                        </div>
                        <div style="display: flex; justify-content: flex-end;">
                            <button type="button" id="bonfire-delete-btn" class="profiles-btn btn-danger-quiet" style="padding: 10px 20px !important; font-size: 0.95rem !important; box-sizing: border-box !important; margin: 0 !important; display: inline-block !important;">${t('bonfire.deleteGroup')}</button>
                        </div>
                    </div>
                `;
            } else {
                hostSectionHtml = `
                    <div class="bonfire-form-group" style="border-bottom: 1px solid var(--jpf-card-border); padding-bottom: 1.5rem;">
                        <label style="font-size: 1.1rem; font-weight: 700; display: block; margin-bottom: 4px;">${t('bonfire.hostTitle')}</label>
                        <span style="font-size: 0.88rem; opacity: 0.75; display: block; margin-bottom: 12px;">${t('bonfire.hostBody')}</span>
                        <button type="button" id="bonfire-generate-btn" class="profiles-btn btn-primary" style="width: 100% !important; padding: 12px !important; font-weight: 600 !important; box-sizing: border-box !important; display: block !important; margin: 8px 0 !important;">${t('bonfire.generateJoinCode')}</button>
                    </div>
                `;
            }

            let guestSectionHtml = '';
            if (isMember) {
                guestSectionHtml = `
                    <div style="display: flex; flex-direction: column; gap: var(--jpf-gap-lg); border-bottom: 1px solid var(--jpf-card-border); padding-bottom: 1.5rem;">
                        <div class="bonfire-form-group">
                            <label style="font-size: 1.1rem; font-weight: 700; display: block; margin-bottom: 4px;">${t('bonfire.joinedTitle')}</label>
                            <span style="font-size: 0.88rem; opacity: 0.75;">${t('bonfire.joinedOwnedBy')}</span>
                            <div style="font-size: 1.25rem; font-weight: 700; color: var(--jpf-accent); margin: 12px 0; background: var(--jpf-input-bg); padding: 12px; border-radius: var(--jpf-input-r); border: 1px solid var(--jpf-input-border); text-align: center;">${joinedOwnerName}</div>
                            <span style="font-size: 0.85rem; opacity: 0.6; display: block; margin-top: -4px;">${t('bonfire.accessEachOther')}</span>
                        </div>
                        <div style="display: flex; justify-content: flex-end;">
                            <button type="button" id="bonfire-leave-btn" class="profiles-btn btn-danger-quiet" style="padding: 10px 20px !important; font-size: 0.95rem !important; box-sizing: border-box !important; margin: 0 !important; display: inline-block !important;">${t('bonfire.leaveGroup')}</button>
                        </div>
                    </div>
                `;
            } else {
                guestSectionHtml = `
                    <div class="bonfire-form-group" style="border-bottom: 1px solid var(--jpf-card-border); padding-bottom: 1.5rem;">
                        <label style="font-size: 1.1rem; font-weight: 700; display: block; margin-bottom: 4px;">${t('bonfire.joinTitle')}</label>
                        <span style="font-size: 0.88rem; opacity: 0.75; display: block; margin-bottom: 12px;">${t('bonfire.joinBody')}</span>
                        <div style="display: flex !important; gap: var(--jpf-gap) !important; align-items: center !important; width: 100% !important; box-sizing: border-box !important; margin: 12px 0 !important;">
                            <input type="text" id="bonfire-join-input" placeholder="${t('bonfire.joinCodePlaceholder')}" maxlength="6" style="flex: 1 1 0% !important; min-width: 0 !important; text-align: center !important; text-transform: uppercase !important; font-family: monospace !important; letter-spacing: 2px !important; height: 44px !important; box-sizing: border-box !important; margin: 0 !important; padding: 10px !important;" />
                            <button type="button" id="bonfire-join-btn" class="profiles-btn btn-primary" style="flex: 0 0 auto !important; padding: 0 24px !important; height: 44px !important; display: inline-flex !important; align-items: center !important; justify-content: center !important; font-weight: 600 !important; margin: 0 !important; box-sizing: border-box !important;">${t('common.join')}</button>
                        </div>
                        <div id="bonfire-join-error" style="display: none; color: var(--jpf-danger); font-size: 0.85rem; font-weight: 600; margin-top: 8px; text-align: center;"></div>
                    </div>
                `;
            }

            const hideMine = status.hideMySubProfilesFromOthers || status.HideMySubProfilesFromOthers || false;
            const hideOthers = status.hideOthersSubProfilesFromMe || status.HideOthersSubProfilesFromMe || false;
            const lanBypass = status.allowHouseholdLanBypass || status.AllowHouseholdLanBypass || false;
            const isAdmin = status.isAdministrator || status.IsAdministrator || false;
            const hasPinSet = status.hasPin || status.HasPin || false;

            // Only worth showing once there is somebody to share with — on a standalone
            // account the setting has nothing to act on and just reads as a scary toggle.
            const lanBypassSectionHtml = (isOwner || isMember) ? `
                <div class="bonfire-form-group" style="gap: 4px; border-top: 1px solid var(--jpf-card-border); padding-top: 16px;">
                    <label class="library-check-label" style="display: inline-flex !important; align-items: center !important; gap: 0.5rem !important; cursor: pointer !important; user-select: none !important; font-size: 0.9rem !important; font-weight: 600 !important; position: relative !important;">
                        <input type="checkbox" id="bonfire-lan-bypass-checkbox" ${lanBypass ? 'checked' : ''} style="cursor: pointer !important; accent-color: var(--jpf-warn) !important; position: relative !important; opacity: 1 !important; width: 18px !important; height: 18px !important; margin: 0 !important; padding: 0 !important; flex-shrink: 0 !important;" />
                        <span>${t('bonfire.lanSwitchLabel')}</span>
                    </label>
                    <div class="form-hint" style="margin-left: 1.6rem !important; opacity: 0.75 !important; font-size: 0.8rem !important; position: relative !important; display: block !important;">
                        ${t('bonfire.lanSwitchHint', { extra: hasPinSet ? '' : t('bonfire.lanSwitchHintExtra') })}
                    </div>
                    ${isAdmin ? `
                    <div style="margin-left: 1.6rem; margin-top: 8px; padding: 10px 12px; background: rgba(255,153,0,0.08); border-left: 3px solid var(--jpf-warn); border-radius: var(--jpf-r-sm); font-size: 0.75rem; line-height: 1.5; color: var(--jpf-text-1);">
                        <strong style="color: var(--jpf-warn);">${t('bonfire.adminAccountWarning')}</strong> ${t('bonfire.adminAccountWarningBody')}
                    </div>` : ''}
                    <div style="margin-left: 1.6rem; margin-top: 8px; font-size: 0.78rem; line-height: 1.5; opacity: 0.7;">
                        ${t('bonfire.proxyHint')}
                    </div>
                </div>
            ` : '';

            const settingsSectionHtml = `
                <div class="bonfire-form-group" style="margin-top: 5px; display: flex; flex-direction: column; gap: var(--jpf-gap);">
                    ${(isOwner || isMember) ? `<div class="bonfire-form-group" style="gap: 4px;">
                        <label class="library-check-label" style="display: inline-flex !important; align-items: center !important; gap: 0.5rem !important; cursor: pointer !important; user-select: none !important; font-size: 0.9rem !important; font-weight: 600 !important; position: relative !important;">
                            <input type="checkbox" id="bonfire-hide-mine-checkbox" ${hideMine ? 'checked' : ''} style="cursor: pointer !important; accent-color: var(--jpf-accent) !important; position: relative !important; opacity: 1 !important; width: 18px !important; height: 18px !important; margin: 0 !important; padding: 0 !important; flex-shrink: 0 !important;" />
                            <span>${t('bonfire.hideMine')}</span>
                        </label>
                        <div class="form-hint" style="margin-left: 1.6rem !important; opacity: 0.75 !important; font-size: 0.8rem !important; position: relative !important; display: block !important;">${t('bonfire.hideMineHint')}</div>
                    </div>

                    <div class="bonfire-form-group" style="gap: 4px;">
                        <label class="library-check-label" style="display: inline-flex !important; align-items: center !important; gap: 0.5rem !important; cursor: pointer !important; user-select: none !important; font-size: 0.9rem !important; font-weight: 600 !important; position: relative !important;">
                            <input type="checkbox" id="bonfire-hide-others-checkbox" ${hideOthers ? 'checked' : ''} style="cursor: pointer !important; accent-color: var(--jpf-accent) !important; position: relative !important; opacity: 1 !important; width: 18px !important; height: 18px !important; margin: 0 !important; padding: 0 !important; flex-shrink: 0 !important;" />
                            <span>${t('bonfire.hideOthers')}</span>
                        </label>
                        <div class="form-hint" style="margin-left: 1.6rem !important; opacity: 0.75 !important; font-size: 0.8rem !important; position: relative !important; display: block !important;">${t('bonfire.hideOthersHint')}</div>
                    </div>` : ''}

                    ${lanBypassSectionHtml}

                    <!-- Saving these is a fetch that can fail. Without somewhere to say so,
                         a failure left the box sitting in its new position with the server
                         still holding the old value — the one case where that matters is the
                         LAN bypass, where the box can read "on" while nobody can actually
                         switch in, or the reverse. aria-live so a screen reader hears it
                         without the focus having to move. -->
                    <div id="bonfire-settings-status" role="status" aria-live="polite"
                         style="display: none; margin-left: 1.6rem; font-size: 0.78rem; line-height: 1.4;"></div>
                </div>
            `;

            container.innerHTML = `
                <div style="display: flex; flex-direction: column; gap: 1.5rem; width: 100%;">
                    ${hostSectionHtml}
                    ${guestSectionHtml}
                    ${settingsSectionHtml}
                    <div class="bonfire-dialog-actions jpf-actions" style="margin-top: 2rem !important; display: flex !important; justify-content: flex-start !important; width: 100% !important; box-sizing: border-box !important; position: relative !important; bottom: auto !important; left: auto !important; right: auto !important; top: auto !important;">
                        <button id="bonfire-back-btn" class="profiles-btn btn-secondary" style="padding: 10px 24px !important; font-size: 1rem !important; box-sizing: border-box !important; margin: 0 !important; display: inline-block !important; width: auto !important; flex: 0 0 auto !important; position: relative !important; bottom: auto !important; left: auto !important; right: auto !important; top: auto !important;">${t('common.back')}</button>
                    </div>
                </div>
            `;

            // Event Listener: Back Button
            const backBtn = container.querySelector('#bonfire-back-btn');
            if (backBtn) {
                backBtn.addEventListener('click', () => {
                    // Back to Settings, which is where this was opened from.
                    this.showSettingsMenu();
                });
            }

            // Event Listeners: Kick Members
            container.querySelectorAll('.bonfire-kick-btn').forEach(btn => {
                btn.addEventListener('click', () => {
                    const mId = btn.getAttribute('data-id');
                    this.showConfirmDialog(t('bonfire.kickConfirmTitle'), t('bonfire.kickConfirmBody'), () => {
                        fetch(apiClient.getUrl('plugins/profiles/bonfire/kick'), {
                            method: 'POST',
                            headers: this.manageHeaders(masterToken),
                            body: JSON.stringify({ memberId: mId })
                        })
                        .then(res => {
                            if (res.ok) this.loadBonfireStatus(content, apiClient, masterToken);
                            else this.showAlert(t('errors.error'), t('bonfire.failedKick'));
                        })
                        .catch(err => this.showAlert(t('errors.error'), t('errors.withMessage', { message: escapeHtml(err.message) })));
                    });
                });
            });

            // Replaces a code that has gone further than intended. Members stay in; the old
            // code stops working. A kick does the same on its own, since the kicked member
            // otherwise just joined again with the code they already had.
            const newCodeBtn = container.querySelector('#bonfire-newcode-btn');
            if (newCodeBtn) {
                newCodeBtn.addEventListener('click', () => {
                    this.showConfirmDialog(t('bonfire.newCodeConfirmTitle'), t('bonfire.newCodeConfirmBody'), () => {
                        fetch(apiClient.getUrl('plugins/profiles/bonfire/regenerate-code'), {
                            method: 'POST',
                            headers: this.manageHeaders(masterToken)
                        })
                        .then(res => {
                            if (res.ok) this.loadBonfireStatus(content, apiClient, masterToken);
                            else this.showAlert(t('errors.error'), t('bonfire.failedNewCode'));
                        })
                        .catch(err => this.showAlert(t('errors.error'), t('errors.withMessage', { message: escapeHtml(err.message) })));
                    });
                });
            }

            // Event Listeners: Delete Group
            const deleteBtn = container.querySelector('#bonfire-delete-btn');
            if (deleteBtn) {
                deleteBtn.addEventListener('click', () => {
                    this.showConfirmDialog(t('bonfire.deleteGroupConfirmTitle'), t('bonfire.deleteGroupConfirmBody'), () => {
                        fetch(apiClient.getUrl('plugins/profiles/bonfire/delete-group'), {
                            method: 'POST',
                            headers: this.manageHeaders(masterToken, false)
                        })
                        .then(res => {
                            if (res.ok) this.loadBonfireStatus(content, apiClient, masterToken);
                            else this.showAlert(t('errors.error'), t('bonfire.failedDeleteGroup'));
                        })
                        .catch(err => this.showAlert(t('errors.error'), t('errors.withMessage', { message: escapeHtml(err.message) })));
                    });
                });
            }

            // Event Listeners: Leave Group
            const leaveBtn = container.querySelector('#bonfire-leave-btn');
            if (leaveBtn) {
                leaveBtn.addEventListener('click', () => {
                    this.showConfirmDialog(t('bonfire.leaveGroupConfirmTitle'), t('bonfire.leaveGroupConfirmBody'), () => {
                        fetch(apiClient.getUrl('plugins/profiles/bonfire/leave'), {
                            method: 'POST',
                            headers: this.manageHeaders(masterToken, false)
                        })
                        .then(res => {
                            if (res.ok) this.loadBonfireStatus(content, apiClient, masterToken);
                            else this.showAlert(t('errors.error'), t('bonfire.failedLeaveGroup'));
                        })
                        .catch(err => this.showAlert(t('errors.error'), t('errors.withMessage', { message: escapeHtml(err.message) })));
                    });
                });
            }

            // Event Listeners: Generate Code
            const generateBtn = container.querySelector('#bonfire-generate-btn');
            if (generateBtn) {
                generateBtn.addEventListener('click', () => {
                    // Disable immediately to prevent double-fire on slow connections
                    generateBtn.disabled = true;
                    generateBtn.textContent = t('bonfire.generating');
                    fetch(apiClient.getUrl('plugins/profiles/bonfire/generate'), {
                        method: 'POST',
                        headers: masterToken ? this.manageHeaders(masterToken, false) : {}
                    })
                    .then(res => {
                        if (res.ok) this.loadBonfireStatus(content, apiClient, masterToken);
                        else return res.text().then(text => { throw new Error(text); });
                    })
                    .catch(err => {
                        generateBtn.disabled = false;
                        generateBtn.textContent = t('bonfire.generateJoinCode');
                        this.showAlert(t('errors.error'), t('bonfire.failedGenerateCode', { message: escapeHtml(err.message) }));
                    });
                });
            }

            // Event Listeners: Join Group
            const joinInput = container.querySelector('#bonfire-join-input');
            const joinBtn = container.querySelector('#bonfire-join-btn');
            const errDiv = container.querySelector('#bonfire-join-error');
            if (joinBtn && joinInput && errDiv) {
                const performJoin = () => {
                    const code = joinInput.value.trim();
                    errDiv.style.display = 'none';
                    if (!code || code.length !== 6) {
                        errDiv.textContent = t('bonfire.pleaseEnterCode');
                        errDiv.style.display = 'block';
                        return;
                    }
                    // The server allows membership in only one group, so joining silently
                    // drops the current one. Confirm first rather than surprising the user.
                    if (isMember) {
                        this.showConfirmDialog(
                            t('bonfire.leaveCurrentTitle'),
                            t('bonfire.leaveCurrentBody'),
                            () => submitJoin(code));
                        return;
                    }
                    submitJoin(code);
                };

                const submitJoin = (code) => {
                    errDiv.style.display = 'none';
                    // Disable to prevent double-submit; re-enable on all error paths
                    joinBtn.disabled = true;
                    joinBtn.textContent = t('bonfire.joining');
                    fetch(apiClient.getUrl('plugins/profiles/bonfire/join'), {
                        method: 'POST',
                        headers: this.manageHeaders(masterToken),
                        body: JSON.stringify({ code: code })
                    })
                    .then(res => {
                        if (res.status === 429) {
                            errDiv.textContent = t('bonfire.tooManyJoinAttempts');
                            errDiv.style.display = 'block';
                            joinBtn.disabled = false;
                            joinBtn.textContent = t('common.join');
                            return;
                        }
                        if (!res.ok) return res.text().then(text => { throw new Error(text); });
                        this.loadBonfireStatus(content, apiClient, masterToken);
                    })
                    .catch(err => {
                        errDiv.textContent = err.message || t('bonfire.failedToJoin');
                        errDiv.style.display = 'block';
                        joinBtn.disabled = false;
                        joinBtn.textContent = t('common.join');
                    });
                };

                joinBtn.addEventListener('click', performJoin);
                joinInput.addEventListener('keydown', (e) => {
                    if (e.key === 'Enter') performJoin();
                });
            }

            // Settings checkbox listeners — debounced 300ms to prevent race conditions
            // when the user toggles both checkboxes in quick succession.
            const hideMineCb = container.querySelector('#bonfire-hide-mine-checkbox');
            const hideOthersCb = container.querySelector('#bonfire-hide-others-checkbox');
            const lanBypassCb = container.querySelector('#bonfire-lan-bypass-checkbox');
            let _settingsDebounceTimer = null;
            const statusDiv = container.querySelector('#bonfire-settings-status');

            // The last state the server acknowledged. A failed save is reverted to this, so
            // the boxes always show what is actually stored rather than what was attempted.
            // Seeded from the values this panel was rendered with, which came from the server.
            const lastSaved = {
                hideMine: hideMineCb ? hideMineCb.checked : false,
                hideOthers: hideOthersCb ? hideOthersCb.checked : false,
                lanBypass: lanBypassCb ? lanBypassCb.checked : false
            };

            const settingsBoxes = [hideMineCb, hideOthersCb, lanBypassCb].filter(Boolean);

            const setStatus = (message, ok) => {
                if (!statusDiv) return;
                statusDiv.textContent = message;
                statusDiv.style.color = ok ? 'var(--jpf-accent)' : 'var(--jpf-danger)';
                statusDiv.style.display = 'block';
                statusDiv.style.opacity = ok ? '0.75' : '1';
            };

            let _statusClearTimer = null;
            const flashSaved = () => {
                setStatus(t('bonfire.settingsSaved'), true);
                clearTimeout(_statusClearTimer);
                _statusClearTimer = setTimeout(() => {
                    if (statusDiv) statusDiv.style.display = 'none';
                }, 2500);
            };

            const saveSettings = () => {
                clearTimeout(_settingsDebounceTimer);
                _settingsDebounceTimer = setTimeout(() => {
                    const hideMineVal = hideMineCb ? hideMineCb.checked : false;
                    const hideOthersVal = hideOthersCb ? hideOthersCb.checked : false;
                    const lanBypassVal = lanBypassCb ? lanBypassCb.checked : false;

                    const body = {
                        hideMySubProfilesFromOthers: hideMineVal,
                        hideOthersSubProfilesFromMe: hideOthersVal
                    };
                    // Omitted rather than sent as false when the toggle is not on screen, so a
                    // standalone account saving the hide flags cannot clear a setting it never
                    // rendered. The server treats a missing value as "leave alone".
                    if (lanBypassCb) body.allowHouseholdLanBypass = lanBypassVal;

                    // Locked while the request is in flight. Toggling again mid-save would
                    // queue a second write whose result arrives in whatever order the network
                    // decides, and a revert would then restore a state neither click asked for.
                    settingsBoxes.forEach(cb => { cb.disabled = true; });
                    clearTimeout(_statusClearTimer);

                    const finish = () => { settingsBoxes.forEach(cb => { cb.disabled = false; }); };

                    fetch(apiClient.getUrl('plugins/profiles/bonfire/settings'), {
                        method: 'POST',
                        headers: this.manageHeaders(masterToken),
                        body: JSON.stringify(body)
                    })
                    .then(res => {
                        if (!res.ok) throw new Error('HTTP ' + res.status);
                        lastSaved.hideMine = hideMineVal;
                        lastSaved.hideOthers = hideOthersVal;
                        lastSaved.lanBypass = lanBypassVal;
                        finish();
                        flashSaved();
                    })
                    .catch(err => {
                        // This used to be a console.error and nothing else. The box stayed
                        // where the user put it while the server still held the old value —
                        // and for the LAN bypass that means the panel can read "household
                        // switching allowed" when it is not, or the other way round. Whichever
                        // way it lands, the person believes a security setting that is not true.
                        console.error('Error saving Bonfire settings:', err);
                        if (hideMineCb) hideMineCb.checked = lastSaved.hideMine;
                        if (hideOthersCb) hideOthersCb.checked = lastSaved.hideOthers;
                        if (lanBypassCb) lanBypassCb.checked = lastSaved.lanBypass;
                        finish();
                        setStatus(t('bonfire.settingsSaveFailed'), false);
                    });
                }, 300);
            };

            if (hideMineCb) hideMineCb.addEventListener('change', saveSettings);
            if (hideOthersCb) hideOthersCb.addEventListener('change', saveSettings);

            if (lanBypassCb) {
                lanBypassCb.addEventListener('change', () => {
                    // Turning it off is always safe and saves straight away. Turning it on
                    // widens who can reach the account, so it is confirmed first — the checkbox
                    // reverts if the user backs out, otherwise it would look enabled while the
                    // server still had it off.
                    if (!lanBypassCb.checked) {
                        saveSettings();
                        return;
                    }

                    const adminLine = isAdmin
                        ? `<br><br><strong style="color:var(--jpf-warn);">${t('bonfire.adminAccountWarning')}</strong> ${t('bonfire.adminAccountWarningBody')}`
                        : '';

                    this.showConfirmDialog(
                        t('bonfire.allowHouseholdTitle'),
                        t('bonfire.allowHouseholdBody') + adminLine,
                        () => saveSettings(),
                        () => { lanBypassCb.checked = lastSaved.lanBypass; }
                    );
                });
            }

            this.initTVCheckboxes(container);

            // TV D-pad Auto-focus helper
            setTimeout(() => {
                const target = container.querySelector('input, button');
                if (target) target.focus();
            }, 100);
        },


        // ── Native-mode entry points ───────────────────────────────────────────────
        // Native mode drops the forced gate, so the switcher has to be reachable from
        // Jellyfin's own interface instead. Two places, deliberately:
        //
        //   1. The user menu, which is what issue #8 actually asks for.
        //   2. The profile page, as the fallback — the menu is built dynamically by React
        //      and every selector below is a guess about markup we do not control. When a
        //      theme defeats the menu injection, the profile page still works.
        //
        // Both are additive: nothing is removed from Jellyfin's own UI, so a failed match
        // costs a missing entry rather than a broken menu.

        // Jellyfin 10.11's user menu is AppUserMenu.tsx — a MUI <Menu id="app-user-menu">
        // of <MenuItem> rows, NOT the .actionSheet component older builds used. It also
        // sets keepMounted, so the menu lives in the DOM from first render even while
        // closed, which is why this can be a plain periodic check rather than an observer
        // racing the menu open.
        //
        // Everything below degrades to "no entry appears" if the markup moves. The
        // #/userprofile section is the deliberate fallback for that case.
        USER_MENU_SELECTOR: '#app-user-menu',

        /// Re-asserts the "Switch Profile" row in Jellyfin's user menu. Cheap and
        /// idempotent — React re-renders the menu freely, so this runs from checkRoute
        /// rather than trying to catch every rebuild.
        syncUserMenuEntry: function () {
            // Cheap guard first, for the same reason as syncPreferencesMenuEntry: this runs
            // from the route poll, and in button mode the only reason to touch the menu is
            // to remove a row a previous menu-mode session left behind.
            const menuMode = this.isMenuLocation();
            if (!menuMode && !this._userEntryPlaced) return;

            const menu = document.querySelector(this.USER_MENU_SELECTOR);
            if (!menu) return;

            const existing = menu.querySelector('#profiles-user-menu-item');

            if (!menuMode) {
                // Location switched back to the floating button — take the row out again.
                if (existing) existing.remove();
                this._userEntryPlaced = false;
                return;
            }
            if (existing && menu.contains(existing)) return;

            const items = Array.from(menu.querySelectorAll('li.MuiMenuItem-root, [role="menuitem"]'));
            if (!items.length) return;

            // Anchor on the Sign out row. It is the only item in this menu with no
            // navigation target, and matching it by position instead would break the
            // moment an optional row (Quick Connect, Select Server, Exit) appears.
            // Translated UIs are the normal case — issue #14 came with a Polish
            // screenshot — so text matching alone is not enough; the MUI Logout icon
            // is language-independent and checked first.
            const signOut = items.find(el => el.querySelector('[data-testid="LogoutIcon"]'))
                || items.find(el => {
                    if (el.getAttribute('href') || el.querySelector('a[href]')) return false;
                    const text = (el.textContent || '').trim().toLowerCase();
                    return text === 'sign out' || text === 'log out' || text === 'logout';
                });
            if (!signOut) return;

            // Clone a real row so the entry inherits the active theme's MUI classes
            // instead of carrying styling of our own that would not match. Cloning does
            // not copy listeners, so React's logout handler does not come with it.
            const entry = signOut.cloneNode(true);
            entry.id = 'profiles-user-menu-item';
            entry.removeAttribute('aria-controls');
            entry.removeAttribute('data-testid');

            const iconSlot = entry.querySelector('.MuiListItemIcon-root');
            if (iconSlot) {
                // Replace the MUI SVG with a Material Icons glyph — Jellyfin already
                // loads that font, and we cannot construct a MUI icon component here.
                // The glyph comes from the class, not the text: jellyfin-web bundles
                // material-design-icons-iconfont, whose CSS sets :before content per name.
                iconSlot.innerHTML = '';
                const glyph = document.createElement('span');
                glyph.className = 'material-icons switch_account';
                glyph.setAttribute('aria-hidden', 'true');
                glyph.style.fontSize = '1.5rem';
                iconSlot.appendChild(glyph);
            }

            const label = entry.querySelector('.MuiListItemText-primary') || entry.querySelector('.MuiListItemText-root');
            if (label) label.textContent = t('switcher.switchProfile');
            else entry.textContent = t('switcher.switchProfile');

            entry.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                this.closeUserMenu();
                // Let MUI finish its close transition before the overlay mounts, so the
                // menu is not still fading out on top of the switcher.
                setTimeout(() => this.handleBubbleClick(), 80);
            });
            entry.addEventListener('keydown', (e) => {
                if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); entry.click(); }
            });

            signOut.parentNode.insertBefore(entry, signOut);
            this._userEntryPlaced = true;
        },

        /// Re-asserts the "Switch Profile" row on the Settings page (#/mypreferencesmenu),
        /// which is the list users actually mean by "the menu" — the header dropdown above
        /// is a different component and only some layouts show it.
        ///
        /// Source of truth: src/apps/legacy/routes/user/settings/index.tsx, page id
        /// myPreferencesMenuPage. The row goes in the "User" section above Sign out.
        syncPreferencesMenuEntry: function () {
            // Cheap guard first. This runs from the route poll, so in button mode — the
            // default — it used to query the document and read offsetParent (which forces
            // layout) twice a second purely to discover it had nothing to do. Reading a
            // cached preference costs nothing, and the only reason to look at the page in
            // button mode is to take away a row a previous menu-mode session left behind,
            // which _prefsEntryPlaced already knows about.
            const menuMode = this.isMenuLocation();
            if (!menuMode && !this._prefsEntryPlaced) return;

            // Jellyfin keeps previous views in the DOM, so match the visible one or the
            // row lands on a page nobody is looking at.
            const page = Array.from(document.querySelectorAll('#myPreferencesMenuPage'))
                .find(el => el.offsetParent !== null || el.classList.contains('is-active'));
            if (!page) return;

            const existing = page.querySelector('#profiles-preferences-menu-item');

            if (!menuMode) {
                if (existing) existing.remove();
                this._prefsEntryPlaced = false;
                return;
            }
            if (existing) return;

            const signOut = page.querySelector('.userSection .btnLogout') || page.querySelector('.btnLogout');
            if (!signOut) return;

            // Clone a real row so it inherits the active theme's classes. React dispatches
            // through fiber props stored on the node, which cloneNode does not copy, so the
            // logout handler does not come along.
            const entry = signOut.cloneNode(true);
            entry.id = 'profiles-preferences-menu-item';
            entry.classList.remove('btnLogout');
            entry.removeAttribute('href');
            // An <a> with no href is not focusable, and this row has none — Jellyfin's own
            // Sign out is a click handler rather than a link. Without this the keyboard and
            // D-pad can never reach the entry and its Enter handler is dead code.
            entry.setAttribute('tabindex', '0');
            entry.setAttribute('role', 'button');

            const icon = entry.querySelector('.listItemIcon');
            if (icon) {
                icon.classList.remove('exit_to_app');
                icon.classList.add('switch_account');
                icon.textContent = '';
            }

            const label = entry.querySelector('.listItemBodyText');
            if (label) label.textContent = t('switcher.switchProfile');
            else entry.textContent = t('switcher.switchProfile');

            entry.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                this.handleBubbleClick();
            });
            entry.addEventListener('keydown', (e) => {
                if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); entry.click(); }
            });

            signOut.parentNode.insertBefore(entry, signOut);
            this._prefsEntryPlaced = true;
        },

        /// Closes the MUI menu. Our cloned row has no React handler, so nothing would
        /// dismiss it otherwise. Escape is what MUI itself listens for; clicking the
        /// backdrop is the fallback for builds where that listener is not attached.
        closeUserMenu: function () {
            const menu = document.getElementById('app-user-menu');

            // Dispatched INSIDE the menu, not on the document.
            //
            // MUI's Modal handles Escape with an onKeyDown on its own root element, so the
            // event has to travel through that element to be seen. Dispatched on
            // `document` — which is what this did — the propagation path is [window,
            // document] and the modal is never on it, so the call has never once closed
            // anything. Only the backdrop click below has, and when that missed too the
            // menu stayed open with its focus trap live, fighting ours for focus until the
            // stack overflowed. See onFocusIn.
            //
            // Dispatched on a node inside the menu and allowed to bubble, it passes through
            // the modal root whether the id sits on that root or on the paper within it.
            const from = menu
                ? (menu.contains(document.activeElement) ? document.activeElement
                    : (menu.firstElementChild || menu))
                : null;
            if (from) {
                from.dispatchEvent(new KeyboardEvent('keydown', {
                    key: 'Escape', code: 'Escape', keyCode: 27, which: 27,
                    bubbles: true, cancelable: true
                }));
            }

            setTimeout(() => {
                const backdrop = document.querySelector('#app-user-menu .MuiBackdrop-root, .MuiModal-root .MuiBackdrop-root');
                if (backdrop) backdrop.click();
            }, 60);
        },

        /// Adds a Profiles section to Jellyfin's own profile page (#/userprofile).
        /// Re-checked on every route change because React replaces the view wholesale.
        injectProfilePageSection: function () {
            if (document.getElementById('profiles-userprofile-section')) return;

            // The visible page, not a cached off-screen view: Jellyfin keeps previous views
            // in the DOM, and appending to a hidden one puts the section nowhere.
            const page = Array.from(document.querySelectorAll('#userProfilePage, .userProfilePage, .page'))
                .find(el => el.offsetParent !== null || el.classList.contains('is-active'));
            if (!page) return;

            // The padded page container, deliberately — our section is a sibling of the
            // avatar block and of the password form, not a part of either.
            //
            // This used to be one querySelector with all three in the list, which reads
            // like a preference order and is not one: querySelector returns the first match
            // in DOCUMENT order, and .padded-left is the parent of .readOnlyContent, so the
            // answer was the parent whatever order they were written in. Right answer,
            // wrong reason — asked one at a time now so the intent is the behaviour.
            const host = page.querySelector('.padded-left')
                || page.querySelector('.readOnlyContent')
                || page.querySelector('form')
                || page;

            const activeInfo = this.getCachedActiveProfile();
            const section = document.createElement('div');
            section.id = 'profiles-userprofile-section';
            // Geometry lives in the stylesheet, so it can be checked and so it matches
            // what the page around it does — see .jpf-userprofile-section.
            section.className = 'verticalSection jpf-userprofile-section';
            section.innerHTML = `
                <h2 class="sectionTitle" style="display:flex; align-items:center; gap:0.4em;">
                    <span class="material-icons local_fire_department" aria-hidden="true" style="color:var(--jpf-warn);"></span>
                    ${t('profilePage.title')}
                </h2>
                <p style="opacity:0.7; margin:0 0 1em 0; line-height:1.5;">
                    ${t('profilePage.body', { name: escapeHtml(activeInfo.name) || t('profilePage.thisAccount') })}
                </p>
                <button is="emby-button" type="button" id="profiles-userprofile-switch-btn" class="raised button-submit block">
                    <span>${t('switcher.switchProfile')}</span>
                </button>
            `;

            host.appendChild(section);

            const btn = section.querySelector('#profiles-userprofile-switch-btn');
            btn.addEventListener('click', (e) => {
                e.preventDefault();
                this.handleBubbleClick();
            });
            btn.addEventListener('keydown', (e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    btn.click();
                }
            });
        },

        // ── Bubble visibility helpers ──────────────────────────────────────────
        _bubbleHide: function (bubble) {
            if (!bubble || bubble.dataset.profilesHiding === '1') return;
            bubble.dataset.profilesHiding = '1';
            bubble.classList.add('profiles-bubble-hiding');
            setTimeout(() => {
                // Only commit display:none if still in hiding state
                if (bubble.dataset.profilesHiding === '1') {
                    bubble.style.display = 'none';
                    bubble.classList.remove('profiles-bubble-hiding');
                    delete bubble.dataset.profilesHiding;
                }
            }, 160);
        },
        _bubbleShow: function (bubble) {
            if (!bubble) return;
            delete bubble.dataset.profilesHiding;
            bubble.style.display = '';
            // Tick so the browser has a chance to paint display:'' before removing
            // the opacity class, triggering the CSS transition.
            requestAnimationFrame(() => bubble.classList.remove('profiles-bubble-hiding'));
        },

        evaluateFloatingBubbleVisibility: function (viewType) {
            let bubble = document.getElementById('profiles-floating-bubble');

            // Nobody is signed in here, whatever storage says — see isSignIn in checkRoute.
            // Forget the previous account the first time the screen is seen, exactly as
            // validateSessionState would have on a reload: its profile session, its master,
            // and the in-memory list, which would otherwise be the next account's first grid
            // (and, through _syncMasterFromProfiles, its master). Ahead of the menu-location
            // check because menu mode carries the same state.
            if (viewType === 'signin') {
                if (!this._signInScreenCleared) {
                    this._signInScreenCleared = true;
                    try { localStorage.removeItem(this.config.masterStorageKey); } catch (e) { /* blocked */ }
                    this.clearProfileSession();
                    this.invalidateProfileCache();
                }
                if (bubble) bubble.remove();
                return;
            }
            this._signInScreenCleared = false;

            // With the switcher in Jellyfin's menu, keeping the floating button as well
            // would be two controls doing one job — and the second portrait beside the
            // native user icon is precisely what issue #14 reported as confusing.
            if (this.isMenuLocation()) {
                if (bubble) bubble.remove();
                return;
            }

            // Hide during active playback/OSD or on any server-management page.
            if (viewType === 'videoosd' || viewType === 'dashboard') {
                this._bubbleHide(bubble);
                return;
            }

            // The active-profile marker is set by the gate and lives in sessionStorage, so it
            // is gone on every fresh load. With "Ask who's watching on startup" off nobody
            // passes through the gate, the marker never exists, and in button mode that left
            // no switcher at all — no button, and no menu row either. So with the gate off the
            // button stands whenever someone is signed in; handleBubbleClick already copes
            // with no marker, because menu mode never has one.
            //
            // The access token is required as well as the preferences: the preference cache
            // outlives sign-out (it matches the stored master), and on its own would keep the
            // button on screen with nobody behind it. getSwitcherPrefs costs nothing extra
            // here — isMenuLocation has just asked it the same question.
            if (!this.isProfileSessionActive()) {
                let signedIn = false;
                try { signedIn = !!(typeof ApiClient !== 'undefined' && ApiClient && ApiClient.accessToken()); } catch (e) { /* no client yet */ }
                const gateOff = signedIn && this.getSwitcherPrefs() !== null && !this.shouldAskOnStartup();
                if (!gateOff) {
                    // No session and the gate will provide one — remove entirely (it is
                    // re-created when needed).
                    if (bubble) bubble.remove();
                    return;
                }
            }

            // ── Strategy 1: find the header button container by class name ─────────
            const headerContainer = this._findHeaderContainer();

            if (headerContainer) {
                if (bubble && bubble.classList.contains('profiles-floating-fallback')) {
                    bubble.remove();
                    bubble = null;
                }
                if (bubble) {
                    if (!document.contains(bubble)) {
                        bubble.remove();
                        bubble = null;
                    } else if (!headerContainer.contains(bubble)) {
                        // Button is in the DOM but drifted outside the header — re-insert.
                        this._insertBeforeUserBtn(headerContainer, bubble);
                    }
                }
                if (!bubble) {
                    bubble = this._buildHeaderBubble();
                    this._insertBeforeUserBtn(headerContainer, bubble);
                    this.attachBubbleClickHandler(bubble);
                }

            } else {
                // Nothing named matched. If a named anchor may still be on its way, wait
                // rather than guess — see HEADER_ANCHOR_GRACE_MS. Only when there is
                // nothing on screen yet: a button already placed is left exactly where it
                // is, because removing it here would trade the jump for a flicker.
                if (!bubble && this._withinAnchorGrace()) return;

                // ── Strategy 2: geometry-based anchor ────────────────────────────────
                // If no named container matched (e.g. a custom Skin Manager theme),
                // find the rightmost visible button in the top 80px of the viewport
                // and insert next to it.  Works for ANY theme regardless of class names.
                // Cached like the header container, and for a sharper reason: this walks
                // every button and link in the document and reads getBoundingClientRect on
                // each one, which forces layout. Running it twice a second was the most
                // expensive thing the plugin did, and it only ever runs on themes where no
                // named container matched — so the worst case fell on the people already
                // worst served. Re-found when it leaves the page or the viewport changes.
                if (this._geoAnchor && !document.contains(this._geoAnchor)) {
                    this._geoAnchor = null;
                }
                //
                // A failed search backs off for the same three seconds the named search
                // does. Without it, a page with no buttons at all in the top 80px — which
                // is where this ends up — would re-run the whole walk on every tick
                // forever, having already established there is nothing to find.
                const geoNow = Date.now();
                const geoStale = !this._geoSearchedAt || geoNow - this._geoSearchedAt >= 3000;
                if (!this._geoAnchor && (geoStale || this._fallbackPosDirty)) {
                    this._geoSearchedAt = geoNow;
                    this._geoAnchor = this._findGeometricHeaderAnchor();
                } else if (this._fallbackPosDirty) {
                    this._geoSearchedAt = geoNow;
                    this._geoAnchor = this._findGeometricHeaderAnchor();
                }
                // The resize has been answered, so the flag is spent. It used to be
                // cleared only on the corner-pill path below, so on a theme where this
                // search found an anchor a single resize left it set for good, and the
                // branch above re-ran the whole walk on every tick for the rest of the
                // session. With no anchor the pill path still needs it to re-place the
                // pill, and clears it there.
                if (this._geoAnchor) this._fallbackPosDirty = false;
                const anchor = this._geoAnchor;
                if (anchor) {
                    if (bubble && bubble.classList.contains('profiles-floating-fallback')) {
                        bubble.remove();
                        bubble = null;
                    }
                    if (bubble) {
                        if (!document.contains(bubble)) {
                            bubble.remove(); bubble = null;
                        } else if (!anchor.parentElement.contains(bubble)) {
                            bubble.remove(); bubble = null;
                        }
                    }
                    if (!bubble) {
                        bubble = this._buildHeaderBubble();
                        anchor.parentElement.insertBefore(bubble, anchor);
                        this.attachBubbleClickHandler(bubble);
                    }

                } else {
                    // ── Strategy 3: true corner-pill fallback ────────────────────────
                    // Appended to <html> (outside the body transform chain) so
                    // position:fixed works correctly regardless of CSS transforms.
                    if (bubble && !bubble.classList.contains('profiles-floating-fallback')) {
                        bubble.remove();
                        bubble = null;
                    }
                    if (!bubble) {
                        bubble = this._buildFallbackBubble();
                        document.documentElement.appendChild(bubble);
                        this.attachBubbleClickHandler(bubble);
                        // _buildFallbackBubble positions it as it builds it, so it starts
                        // clean rather than asking for the same computation twice.
                        this._fallbackPosDirty = false;
                    }
                }
            }

            if (bubble && bubble.classList.contains('profiles-floating-fallback')
                && this._fallbackPosDirty !== false) {
                // Where the corner pill goes depends on the viewport, not on the route.
                // This ran on every tick — a document-wide button query plus several
                // elementFromPoint calls, each one forcing layout — twice a second, on
                // precisely the themes where the header search had already failed and was
                // therefore costing the most. Recomputed when the pill is built and when
                // the viewport changes, which is when the answer can actually differ.
                const pos = this._findBestFallbackPosition();
                bubble.style.top = pos.top;
                bubble.style.bottom = pos.bottom;
                bubble.style.left = pos.left;
                bubble.style.right = pos.right;
                this._fallbackPosDirty = false;
            }

            this._bubbleShow(bubble);

            // Pre-fetch the profile list while the button is visible so the overlay
            // appears instantly (no network wait) when the user clicks it.
            if (viewType === 'home' && !this._profilePrefetchPending) {
                this._prefetchProfiles();
            }
        },

        // Fetches /list using the master token and caches the result in this.cachedProfiles.
        // Called proactively by evaluateFloatingBubbleVisibility; the cached result is
        // consumed by fetchAndRenderProfiles for instant, flash-free overlay display.
        _prefetchProfiles: function () {
            if (this._profilePrefetchPending || (this.cachedProfiles && this.cachedProfiles.length)) return;
            // The pending flag below was cleared on any failure with nothing to slow the
            // next attempt, so a failing server got a /list from every open home screen
            // every 500 ms. The shared guard also stops this racing the gate's request.
            if (!this._listPollMayRequest()) return;
            // With the gate off the button stands without a profile session, so this is
            // reached on every home tick before any master state exists — a storage read and
            // a JSON.parse twice a second, for nothing. Back off three seconds after finding
            // none, the same interval the header searches use.
            if (this._prefetchNoMasterAt && Date.now() - this._prefetchNoMasterAt < 3000) return;
            const masterState = this.readMasterState();
            if (!masterState || !masterState.token) {
                this._prefetchNoMasterAt = Date.now();
                return;
            }
            this._prefetchNoMasterAt = 0;

            this._profilePrefetchPending = true;
            this._requestList(ApiClient, masterState.token)
                .then(res => {
                    // A 401 used to be swallowed with every other failure, leaving a dead
                    // session on screen with a button that could not open anything. It is
                    // the same expired session the gate's request handles.
                    if (res.status === 401) {
                        this.handleSessionExpired();
                        throw new Error('Unauthorized');
                    }
                    if (!res.ok) throw new Error();
                    return res.json();
                })
                .then(profiles => {
                    const normalized = this.normalizeProfiles(profiles);
                    this.cachedProfiles = normalized;
                    localStorage.setItem('jellyfin_profiles_cached_list', JSON.stringify(normalized));
                    this._profilePrefetchPending = false;

                    // Sync sessionStorage if matches current active user
                    const currentUserId = ApiClient.getCurrentUserId();
                    if (currentUserId) {
                        const currentProfile = normalized.find(p => this.normalizeGuid(p.profileUserId) === this.normalizeGuid(currentUserId));
                        if (currentProfile) {
                            const info = {
                                name: currentProfile.profileName,
                                color: currentProfile.avatarColor || DEFAULT_AVATAR_COLOR,
                                initial: currentProfile.avatarInitial || (currentProfile.profileName ? currentProfile.profileName.charAt(0).toUpperCase() : 'P'),
                                profileImage: currentProfile.profileImage || null,
                                transparent: !!currentProfile.transparentAvatar
                            };
                            this._sessionSet('jellyfin_profiles_active_info', JSON.stringify(info));
                        }
                    }

                    // Re-render bubble with the fetched info
                    const currentRouteType = this._lastRouteType || 'other';
                    this.evaluateFloatingBubbleVisibility(currentRouteType);
                })
                .catch(() => {
                    this._profilePrefetchPending = false;
                    // Also set by _requestList for a failed request; this covers a body
                    // that would not parse.
                    this._listRetryAt = Date.now() + this.POLL_RETRY_MS;
                });
        },

        // ── Header-container detection ───────────────────────────────────────────

        /**
         * The container the floating switcher button is placed in.
         *
         * Cached, because this is reached from evaluateFloatingBubbleVisibility on every
         * route tick while a profile is active — which is essentially always — and the
         * header is built once per page. It was re-running the entire search twice a
         * second to arrive at the same answer: six document queries on a stock install and
         * ten on a theme where nothing matched, two of them full-document walks.
         * tests/js/routetick.js measures it.
         *
         * The cache is invalidated by the element leaving the document, which is what
         * React rebuilding the header looks like from out here.
         */
        /// True when an element is actually laid out — not merely present in the document.
        ///
        /// getClientRects() is empty for anything inside a display:none subtree, and unlike
        /// offsetParent it stays correct for a position:fixed element. Both matter here.
        ///
        /// jellyfin-web keeps its whole legacy header in the DOM and hides it whenever the
        /// modern layout is active — the default in 12.0, and an option in 10.11. Its own
        /// comment in components/AppHeader.tsx says why: "these components are not used
        /// with the new layouts, but legacy views interact with the elements directly so
        /// they need to be present in the DOM. We use display: none to hide them and
        /// prevent errors." RootAppRouter renders it as
        /// <AppHeader isHidden={layoutManager.modern || isNewLayoutPath} />, and
        /// libraryMenu.js still fills that hidden .skinHeader with .headerRight,
        /// .headerButton and .headerUserButton.
        ///
        /// So on the modern layout every named strategy below matched, and the button went
        /// into a container nobody could see. From outside that is indistinguishable from
        /// the injection having failed — and the geometric fallback, which would have found
        /// the real toolbar, never ran because the search had already "succeeded".
        _isLaidOut: function (el) {
            return !!el && typeof el.getClientRects === 'function' && el.getClientRects().length > 0;
        },

        /// How long a named anchor gets to appear before geometry is allowed to guess.
        ///
        /// A named anchor is strictly better than a guessed one — it is the account button
        /// itself rather than whatever happened to be furthest right — so the guess waits
        /// for it. The modern toolbar renders progressively, and on the first tick the
        /// account button is not there yet while the Cast button is.
        ///
        /// The cost is borne by custom themes where nothing named will ever match: their
        /// button now appears this much later, and then stays put. That is the better
        /// trade. One late arrival beats a visible jump on every single page load, and
        /// three seconds is well inside how long the rest of the page takes to settle.
        HEADER_ANCHOR_GRACE_MS: 3000,

        /// True while a named anchor may still be about to appear and nothing has been
        /// placed yet. Reset whenever the container is lost, so a React remount gets the
        /// same head start the first load did.
        _withinAnchorGrace: function () {
            return this._headerAnchorWaitFrom !== undefined
                && Date.now() - this._headerAnchorWaitFrom < this.HEADER_ANCHOR_GRACE_MS;
        },

        _findHeaderContainer: function () {
            const cachedNow = Date.now();
            if (this._headerContainer && document.contains(this._headerContainer)) {
                // Re-checked on the same three-second cadence as a fresh search, not on
                // every tick. This is a layout read and checkRoute runs twice a second;
                // what it is watching for — someone switching layout in Display settings —
                // does not happen between ticks.
                if (cachedNow - (this._headerCheckedAt || 0) < 3000) {
                    return this._headerContainer;
                }
                this._headerCheckedAt = cachedNow;
                if (this._isLaidOut(this._headerContainer)) {
                    return this._headerContainer;
                }
                // The layout changed under us and this container is now hidden. Drop it and
                // search again rather than keeping a button nobody can see — and restart
                // the anchor grace, so a React remount gets the same head start the first
                // load did instead of falling straight to geometry.
                this._headerContainer = null;
                this._headerAnchorWaitFrom = undefined;
            }

            // A failed search is remembered too, briefly. Caching only successes would
            // leave the full cost in place on exactly the themes where no strategy
            // matches — the case that was already the most expensive. Three seconds is
            // far below the time anyone takes to notice a missing button, and far above
            // a React re-render.
            //
            // Except while we are still waiting for a named anchor to appear. The modern
            // toolbar is React and its account button is not in the DOM on the first tick,
            // so the first search fails, the throttle then held that answer for three
            // seconds, and the switcher spent those three seconds wherever geometry had
            // guessed. Reported as "it loads squished next to the cast button first, then
            // updates" — the update being this throttle expiring.
            //
            // During the grace the named search runs on every tick instead. That is four
            // indexed queries, which is what the geometric walk it replaces costs many
            // times over, and it only happens in the first few seconds of a page.
            const now = Date.now();
            if (this._headerAnchorWaitFrom === undefined) this._headerAnchorWaitFrom = now;
            const waitingForAnchor = now - this._headerAnchorWaitFrom < this.HEADER_ANCHOR_GRACE_MS;

            if (!waitingForAnchor
                && this._headerContainer === null
                && this._headerSearchedAt
                && now - this._headerSearchedAt < 3000) {
                return null;
            }
            this._headerSearchedAt = now;
            this._headerCheckedAt = now;

            // Indexed strategies only while waiting. Strategy C ends in
            // [class*="skinHeader"], [class*="topBar"] — two unqualified attribute-substring
            // selectors, which can never use an index and always walk the whole document.
            // Running that on every tick is precisely what the route-tick budget forbids,
            // and it would buy nothing: it is the heuristic for custom themes, and a custom
            // theme's header does not appear three seconds late. It runs once the grace is
            // over, on the same tick geometry would otherwise have taken.
            this._headerContainer = this._searchForHeaderContainer(waitingForAnchor);
            // Found one, so the wait is over. Cleared rather than left running, so that if
            // this container is ever lost the grace starts again from that moment.
            if (this._headerContainer) this._headerAnchorWaitFrom = undefined;
            return this._headerContainer;
        },

        _searchForHeaderContainer: function (indexedOnly) {
            // Strategy A: Jellyfin's own right-hand button container.
            //
            // Five other class names were tried before this one — .headerRightButtons,
            // .headerSelfView, .skinHeader-rightButtons, .headerButtons-right and
            // .viewHeaderRight. Every one is absent from jellyfin-web 10.11, so on a
            // stock install the browser resolved five selectors that could not match
            // before reaching the one that does. They are recorded in
            // tests/upstream-selectors.json.
            // Laid out, not merely present — see _isLaidOut. On the modern layout this
            // element exists and is inside a display:none wrapper.
            const byClass = document.querySelector('.headerRight');
            if (this._isLaidOut(byClass)) return byClass;

            // Strategy B: the parent of a header button. A theme that renames the
            // container usually keeps Jellyfin's own buttons inside it.
            //
            // .headerButton rather than [class*="headerButton"]: every header button
            // upstream carries the bare class alongside its specific one
            // (headerCastButton castButton headerButton headerButtonRight), so the plain
            // class selector matches exactly the same elements while letting the engine
            // use the class index instead of walking the document. .btnCurrentUser,
            // .headerButtonUser, .headerButton-user, .btnCast and .headerButton-cast were
            // also tried here and none of them exists upstream either — the cast button
            // is .headerCastButton.
            // Strategy A2: the modern toolbar's user-menu button.
            //
            // Named, not geometric, and that is the point. The first attempt at the modern
            // layout let the geometric fallback place the button, and it landed in a
            // different slot on every other load — between SyncPlay and Cast one time,
            // beside the avatar the next. React fills that toolbar progressively, so
            // "rightmost button in the top 80px" answers whatever happened to be rendered
            // at the instant the search ran, and the answer was then cached.
            //
            // components/toolbar/UserMenuButton.tsx renders
            //   <IconButton aria-controls={ID} aria-haspopup='true'>  where ID is
            // 'app-user-menu' — the same contract as the #app-user-menu we already inject
            // the menu entry into, and byte-identical in 10.11 and 12.0. So the account
            // button can be named outright, and the switcher lands beside the avatar every
            // time. An exact-match attribute selector indexes; the [class*=] kind is what
            // cannot.
            const modernUserBtn = document.querySelector('[aria-controls="app-user-menu"]');
            if (this._isLaidOut(modernUserBtn) && this._isLaidOut(modernUserBtn.parentElement)) {
                return modernUserBtn.parentElement;
            }

            //
            // Every match is considered, not just the first: on the modern layout the
            // hidden legacy header supplies the first .headerButton in document order, and
            // taking it would rule out a visible one a theme had put further down.
            const knownBtns = document.querySelectorAll(
                '.headerButton:not(#profiles-floating-bubble)'
            );
            for (const btn of knownBtns) {
                if (this._isLaidOut(btn) && this._isLaidOut(btn.parentElement)) {
                    return btn.parentElement;
                }
            }

            // Strategy C: find the button cluster inside a custom skin/theme header.
            // ElegantFin and Skin Manager themes wrap everything in .skinHeader or
            // a similarly named element; we pick the child that contains the most
            // icon buttons (likely the right-side group).
            // Strategy C walks the document and is skipped while a named anchor may still
            // be coming — see the call site.
            if (indexedOnly) return null;

            // querySelectorAll, and the first one that is laid out. Stock jellyfin-web's
            // own .skinHeader comes first in document order and is display:none on the
            // modern layout, so querySelector would hand back the one candidate that
            // cannot work and stop before reaching a theme's visible header.
            let skinHeader = null;
            const headers = document.querySelectorAll(
                '.skinHeader, .jellyfinHeader, [class*="skinHeader"], [class*="topBar"]'
            );
            for (const h of headers) {
                if (this._isLaidOut(h)) { skinHeader = h; break; }
            }
            if (!skinHeader) return null;

            // Two queries, not one per candidate. This used to ask for every div, nav, ul
            // and span under the header and then run a second query *inside each one*, so
            // the same buttons were counted once for every ancestor they had. Counting
            // upwards from each button gives the identical answer in one pass.
            const counts = new Map();
            for (const btn of skinHeader.querySelectorAll('button, a[role="button"]')) {
                for (let el = btn.parentElement; el && el !== skinHeader; el = el.parentElement) {
                    counts.set(el, (counts.get(el) || 0) + 1);
                }
            }

            // Walked in document order, and ties go to the first — the same result the
            // nested version produced, since it iterated the same node list.
            let best = null, bestCount = 0;
            for (const el of skinHeader.querySelectorAll('div, nav, ul, span')) {
                const n = counts.get(el) || 0;
                if (n > bestCount && n >= 2) {
                    bestCount = n;
                    best = el;
                }
            }
            // A visible header can still hold a hidden cluster — a theme's collapsed
            // overflow menu counts buttons just as well as its visible row does.
            return this._isLaidOut(best) ? best : null;
        },

        // Finds the rightmost visible button within the top 80px of the viewport.
        // Theme-agnostic: works even when class names are completely non-standard.
        _findGeometricHeaderAnchor: function () {
            const candidates = document.querySelectorAll(
                'button:not(#profiles-floating-bubble), a[role="button"]:not(#profiles-floating-bubble)'
            );
            let rightmost = null, rightmostX = -Infinity;
            for (const el of candidates) {
                const r = el.getBoundingClientRect();
                if (r.top >= 0 && r.bottom <= 80 && r.width > 0 && r.right > rightmostX) {
                    rightmostX = r.right;
                    rightmost = el;
                }
            }
            return rightmost;
        },

        // Builds the icon-style button for header insertion.
        _buildHeaderBubble: function () {
            const b = document.createElement('button');
            b.id = 'profiles-floating-bubble';
            // `focusable` is what Jellyfin's own focusManager looks for on TV layouts
            // (focusableQuery in src/components/focusManager.js is the standard tags plus
            // `.focusable`). A bare <button> qualifies on paper; carrying the class as well
            // costs nothing and is what every other header control does.
            b.className = 'paper-icon-button-light headerButton focusable';
            b.title = t('switcher.switchProfile');
            b.setAttribute('aria-label', t('switcher.switchProfile'));

            const activeInfo = this.getCachedActiveProfile();
            b.innerHTML = `
                <div class="profiles-header-avatar" style="background-color: ${
                    activeInfo.profileImage && activeInfo.transparent ? 'transparent' : safeColor(activeInfo.color)
                }; width: 28px; height: 28px; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 0.85rem; font-weight: 700; color: #fff; text-shadow: 0 1px 2px rgba(0,0,0,0.5); border: 1.5px solid rgba(255,255,255,0.25); box-sizing: border-box; overflow: hidden; position: relative;">
                    ${avatarInner(activeInfo.profileImage, activeInfo.initial, /* useThumb */ true)}
                </div>
            `;
            return b;
        },

        // Builds the corner pill button (last-resort fallback).
        _buildFallbackBubble: function () {
            const b = document.createElement('button');
            b.id = 'profiles-floating-bubble';
            b.className = 'profiles-floating-fallback focusable';
            b.title = t('switcher.switchProfile');
            b.setAttribute('aria-label', t('switcher.switchProfile'));

            // Set initial position dynamically
            const pos = this._findBestFallbackPosition();
            b.style.top = pos.top;
            b.style.bottom = pos.bottom;
            b.style.left = pos.left;
            b.style.right = pos.right;

            const activeInfo = this.getCachedActiveProfile();
            b.innerHTML = `
                <div class="profiles-header-avatar" style="background-color: ${
                    activeInfo.profileImage && activeInfo.transparent ? 'transparent' : safeColor(activeInfo.color)
                }; width: 28px; height: 28px; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 0.85rem; font-weight: 700; color: #fff; text-shadow: 0 1px 2px rgba(0,0,0,0.5); border: 1.5px solid rgba(255,255,255,0.25); box-sizing: border-box; overflow: hidden; position: relative;">
                    ${avatarInner(activeInfo.profileImage, activeInfo.initial, /* useThumb */ true)}
                </div>
            `;
            return b;
        },

        _findBestFallbackPosition: function () {
            const corners = [
                // Top-Right
                { top: '80px', bottom: 'auto', left: 'auto', right: '24px', x: () => window.innerWidth - 60, y: () => 95 },
                // Top-Left
                { top: '80px', bottom: 'auto', left: '24px', right: 'auto', x: () => 60, y: () => 95 },
                // Bottom-Left
                { top: 'auto', bottom: '24px', left: '24px', right: 'auto', x: () => 60, y: () => window.innerHeight - 40 },
                // Bottom-Right
                { top: 'auto', bottom: '24px', left: 'auto', right: '24px', x: () => window.innerWidth - 60, y: () => window.innerHeight - 40 }
            ];

            for (const corner of corners) {
                const cx = corner.x();
                const cy = corner.y();
                if (cx < 0 || cy < 0 || cx > window.innerWidth || cy > window.innerHeight) continue;

                const el = document.elementFromPoint(cx, cy);
                if (!el) return corner;

                if (!el.closest('button, a, [role="button"], input, select, textarea, .headerButton, .paper-icon-button-light')) {
                    return corner;
                }
            }

            return corners[0];
        },

        // Inserts bubble before the user-account button, or appends if not found.
        _insertBeforeUserBtn: function (container, bubble) {
            // .headerUserButton is the real class. This asked for .headerButton-user,
            // .btnCurrentUser and .headerButtonUser, none of which exists in jellyfin-web,
            // so the query never matched and the function has always fallen through to
            // lastElementChild — placing the switcher at the end of the header rather than
            // beside the account icon it is named for. Verified in
            // src/scripts/libraryMenu.js: "headerButton headerButtonRight headerUserButton"
            // and the headerUserButtonRound variant.
            // The third is the modern toolbar's account button — see Strategy A2 in
            // _searchForHeaderContainer for why aria-controls is the right handle on it.
            const userBtn =
                container.querySelector(
                    '.headerUserButton, .headerUserButtonRound, [aria-controls="app-user-menu"]'
                ) ||
                container.lastElementChild;

            // Sized against whatever it ends up standing next to. The modern toolbar puts a
            // 40px MUI Avatar in an IconButton with padding:0 (UserAvatar.tsx renders
            // <Avatar> at its default size), and a 28px circle beside that read as an
            // afterthought rather than a control. The legacy header's buttons are smaller,
            // so this is a class rather than a new default.
            const modern = !!(userBtn && userBtn.getAttribute
                && userBtn.getAttribute('aria-controls') === 'app-user-menu');
            bubble.classList.toggle('jpf-modern-toolbar-btn', modern);

            if (userBtn) {
                userBtn.parentNode.insertBefore(bubble, userBtn);
            } else {
                container.appendChild(bubble);
            }
        },

        /// Opens the profile selector over whatever page the user is on.
        ///
        /// Every entry point routes through here — the header bubble, the sidebar link, the
        /// user-menu item and the profile page — so they cannot drift apart. There is no
        /// page reload: the overlay is drawn on top of the current view, which is what
        /// removed the white flash the old reload-based button caused.
        handleBubbleClick: function () {
            // Every entry point into the switcher funnels through here, so this is where the
            // emergency disable has to hold. An entry that survived the teardown — a menu row
            // a theme rebuilt, say — must not be able to raise the gate again.
            if (this._panicDisabled) return;

            // Opening the switcher deliberately is a reversible act, so remember enough to
            // undo it. Nothing is signed out and no credentials change — the list is fetched
            // with the session in use, which the server resolves to its household — so
            // backing out costs no PIN and no reload. Without this there is no way off the
            // picker at all once it is open, which on a TV means the Back button lands on a
            // dead end.
            //
            // This used to put the master's credentials back in memory first, on the belief
            // that a profile's token could not list its siblings. /list resolves the caller's
            // master and always could; the swap only meant the master account was live
            // behind the picker for as long as it was open.
            const priorUserId = ApiClient.getCurrentUserId();
            const priorToken = this._sessionGet(this.config.activeSessionKey);
            this._resumeState = (priorToken && priorUserId)
                ? {
                    token: priorToken,
                    userId: priorUserId,
                    info: this._sessionGet('jellyfin_profiles_active_info')
                }
                // No profile session marked active, which is the normal case in menu mode:
                // nobody passed through the gate. Closing is still a valid answer, and
                // without it there is no way off the picker at all.
                : { closeOnly: true };

            this.clearProfileSession();
            this.interceptHomeAndShowProfiles();
        },

        /// Puts back the profile that was active before the switcher was opened.
        ///
        /// Nothing was signed out to get here — handleBubbleClick only swapped the master's
        /// credentials into memory so the profile list could be fetched — so the profile's
        /// own token is still good and this needs neither a PIN nor a reload.
        resumePreviousProfile: function () {
            const prior = this._resumeState;
            if (!prior) return;
            this._resumeState = null;

            // closeOnly means there was no profile session to put back — the credentials
            // in play are already the right ones.
            if (!prior.closeOnly) {
                this._sessionSet(this.config.activeSessionKey, prior.token);
                if (prior.info) this._sessionSet('jellyfin_profiles_active_info', prior.info);

                this.updateStoredCredentials(prior.token, prior.userId);
                ApiClient.setAuthenticationInfo(prior.token, prior.userId);
            }

            this.isManageMode = false;
            this.masterPin = null;
            this.removeProfileOverlay();
            this.checkRoute();
        },

        attachBubbleClickHandler: function (bubble) {
            const activate = (e) => {
                e.preventDefault();
                e.stopPropagation();

                bubble.disabled = true;
                bubble.style.opacity = '0.45';
                bubble.style.cursor = 'wait';

                this.handleBubbleClick();

                // Re-enable after the overlay has appeared so the button is ready again if
                // the user dismisses it.
                setTimeout(() => {
                    bubble.disabled = false;
                    bubble.style.opacity = '';
                    bubble.style.cursor = '';
                }, 400);
            };

            bubble.addEventListener('click', activate);

            // Explicit D-pad/keyboard Enter+Space handler.
            // Native <button> fires click on Enter in most browsers, but some TV browsers
            // (notably older Tizen and webOS) skip this for non-focused or injected elements.
            bubble.addEventListener('keydown', (e) => {
                if (e.key === 'Enter' || e.key === ' ') activate(e);
            });
        },

        injectStyles: function () {
            // Idempotent: a second evaluation of this script (a re-injected tag, a client
            // that loads it twice) would otherwise append a second full copy of the sheet.
            if (document.getElementById('jpf-styles')) return;

            const style = document.createElement('style');
            style.id = 'jpf-styles';
            // textContent, not innerHTML. A <style> element's contents are CSS, not markup,
            // so HTML-parsing them buys nothing and can only misread the text.
            // The themes go after the stylesheet. Nothing should be decided by that order,
            // though: a theme rule and a sheet rule tying on the same element is a rule
            // that changed winner when the themes left the sheet, and tests/js/themes.js
            // fails on every one.
            style.textContent = BONFIRE_STYLES + '\n' + allThemesCss();
            document.head.appendChild(style);
            // After the stylesheet, as the copy restored at startup could not be.
            this.applyCustomThemesCss();
            this.injectFonts();
        },

        /// The served fonts, once, in a <style> of their own. Called again from
        /// _setThemeSettings, because a packaged client (Tizen) has no ApiClient when this
        /// script starts and a root-relative address there asks the app, not the server. A
        /// browser that does not fetch a font until text needs it makes declaring all of
        /// them free.
        injectFonts: function () {
            if (document.getElementById('jpf-fonts')) return;
            const inBrowser = /^https?:$/.test(window.location.protocol);
            const hasClient = typeof ApiClient !== 'undefined' && ApiClient && typeof ApiClient.getUrl === 'function';
            if (!inBrowser && !hasClient) return;
            let variable = false;
            try { variable = !!(window.CSS && CSS.supports && CSS.supports('font-variation-settings', 'normal')); } catch (e) { /* old engine */ }
            if (!variable) return;
            const css = fontFaceCss(pluginUrl);
            if (!css) return;
            const style = document.createElement('style');
            style.id = 'jpf-fonts';
            style.textContent = css;
            document.head.appendChild(style);
        },

        // ── Custom themes (1.7.2) ────────────────────────────────────────────────────

        /// The copy of the last answer kept on this device, put to use before anything is
        /// drawn. Read once, at startup.
        restoreCustomThemes: function () {
            let raw = null;
            try { raw = JSON.parse(localStorage.getItem(this.config.customThemesKey) || 'null'); } catch (e) { /* unreadable */ }
            if (raw) this.useCustomThemes(raw, false);
        },

        /// GET themes?part=custom, once per page load and never from checkRoute. Anonymous,
        /// so it works on the gate before anyone has signed in. Sent no-cache with an ETag,
        /// so the browser's own cache turns an unchanged answer into a 304. A failure keeps
        /// whatever the device already had.
        loadCustomThemes: function () {
            if (this._customThemesLoad) return this._customThemesLoad;
            this._customThemesLoad = fetch(pluginUrl('/plugins/profiles/themes?part=custom'))
                .then(res => (res.ok ? res.json() : null))
                .then(answer => { if (answer) this.useCustomThemes(answer, true); })
                .catch(() => { /* offline or an older server: keep the cached copy */ });
            return this._customThemesLoad;
        },

        /// Puts an answer in place: the list, the CSS in a style element of its own, the
        /// device's copy, and the theme the page was asked for, which may be one of these.
        useCustomThemes: function (answer, store) {
            const next = readCustomThemes(answer);
            const text = JSON.stringify(next);
            const changed = text !== this._customThemesText;
            this._customThemesText = text;
            CUSTOM_THEMES = next;

            if (store) {
                try {
                    // The answer as it came, not `next`: restoreCustomThemes reads the copy
                    // with the same reader, and the reader's output is a different shape. Kept
                    // as `next`, every uploaded font came back from the copy as nothing, so a
                    // theme's text drew in its fallback until the server answered.
                    //
                    // Up to forty themes of up to 64 KB each could crowd out everything else
                    // this origin keeps. Past that, the copy is dropped; the theme still
                    // arrives with the answer, a moment after the first frame.
                    const copy = JSON.stringify(answer);
                    if (copy.length <= 512 * 1024) localStorage.setItem(this.config.customThemesKey, copy);
                    else localStorage.removeItem(this.config.customThemesKey);
                } catch (e) { /* storage full or blocked */ }
            }
            if (!changed) return;

            this.drawLogo(null);
            // Not before the stylesheet is in: injectStyles puts this block after it.
            if (document.getElementById('jpf-styles')) this.applyCustomThemesCss();
            if (this._lastThemePrefs) this._setThemeSettings(this._lastThemePrefs);
            else if (this._wantedTheme) this.applyTheme(this._wantedTheme);
        },

        /// The server's logo, top right of the gate (1.7.2). On the gate itself, not inside
        /// .profiles-modal-content, so every screen that swaps the content (PIN, settings,
        /// the theme page) keeps it, and it scrolls away with the page rather than sitting
        /// over a form. Built with the DOM, never markup: the text and the alt are whatever
        /// an administrator typed. Called when the gate is drawn and when the answer
        /// changes; never from checkRoute. `overlay` may be null to mean the one on screen.
        drawLogo: function (overlay) {
            const gate = overlay || document.getElementById('profiles-gate-overlay');
            const logo = CUSTOM_THEMES.logo;
            let box = document.getElementById('profiles-logo');
            const key = logo ? (logo.image || '') + '|' + (logo.text || '') + '|' + (logo.alt || '') : '';
            if (box && (!gate || box.parentNode !== gate || box.getAttribute('data-logo') !== key)) {
                if (box.parentNode) box.parentNode.removeChild(box);
                box = null;
            }
            if (!gate || !logo || box) return;

            box = document.createElement('div');
            box.id = 'profiles-logo';
            box.setAttribute('data-logo', key);
            const asText = () => {
                while (box.firstChild) box.removeChild(box.firstChild);
                if (!logo.text) { if (box.parentNode) box.parentNode.removeChild(box); return; }
                box.className = 'is-text';
                box.textContent = logo.text;
            };
            if (logo.image) {
                const img = document.createElement('img');
                img.alt = logo.alt || '';
                // A logo replaced since this device's copy was kept 404s: the text, if any.
                img.onerror = asText;
                img.src = pluginUrl(logo.image);
                box.appendChild(img);
            } else {
                asText();
            }
            gate.appendChild(box);
        },

        /// The custom themes and their fonts in <style id="jpf-themes-custom">, after the
        /// stylesheet. Rewritten only when what it holds changes.
        applyCustomThemesCss: function () {
            if (!document.head) return;
            let variable = false;
            try { variable = !!(window.CSS && CSS.supports && CSS.supports('font-variation-settings', 'normal')); } catch (e) { /* old engine */ }
            const css = customThemesCss() + customFontFaceCss(pluginUrl, variable);
            let style = document.getElementById('jpf-themes-custom');
            if (!css) {
                if (style) style.parentNode.removeChild(style);
                return;
            }
            if (!style) {
                style = document.createElement('style');
                style.id = 'jpf-themes-custom';
                document.head.appendChild(style);
            }
            if (style.textContent !== css) style.textContent = css;
        }
    };

    /// For the settings page's theme editor (1.7.2), which loads inside jellyfin-web after
    /// this script. Its previews are drawn by themeCss, the one generator, so a preview is
    /// what the theme will look like rather than a second copy's guess. css() returns text
    /// and writes nothing; builtIns() is a copy, so the editor can start from a hidden
    /// built-in, which GET /themes leaves out; refresh() is the startup fetch again, so a
    /// theme just saved reaches this window's pickers without a reload. The page works
    /// without this, minus the preview, when the script was not injected.
    if (typeof window !== 'undefined') {
        window.BonfireThemePreview = {
            css: theme => themeCss(theme, BONFIRE_THEMES.patterns || {}),
            builtIns: () => JSON.parse(JSON.stringify(Array.isArray(BONFIRE_THEMES.themes) ? BONFIRE_THEMES.themes : [])),
            refresh: () => {
                ProfilesPlugin._customThemesLoad = null;
                return ProfilesPlugin.loadCustomThemes();
            }
        };
    }

    ProfilesPlugin.init();
})();
