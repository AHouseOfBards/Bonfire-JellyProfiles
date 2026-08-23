(function () {
    'use strict';

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

    const ProfilesPlugin = {
        config: {
            masterStorageKey: 'jellyfin_profiles_master_state',
            activeSessionKey: 'jellyfin_profiles_active_token',
            // Key set before window.location.reload() so the early-hide
            // inline head script can suppress the page flash on the next load.
            switchingKey: 'jpf-sw',
            // Set when the emergency disable succeeds. profiles.js is served with a
            // five-minute cache, so a reload inside that window can re-run this very script
            // from cache — the marker is what stops it coming back to life.
            panicKey: 'jellyfin_profiles_disabled',
            // Local mirror of the account's gate/native preference, so checkRoute can decide
            // whether to raise the gate without waiting on a request.
            // Per-account cache of the library tile artwork rules, applied before the
            // server answers so the real artwork never gets a frame on screen.
            libraryArtKey: 'jellyfin_profiles_library_art',
            switcherModeKey: 'jellyfin_profiles_switcher_mode'
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

        showConfirmDialog: function (title, message, onConfirm, onCancel) {
            const dialog = document.createElement('div');
            dialog.id = 'profiles-confirm-dialog';
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
                    background: #181818;
                    border: 1px solid rgba(255, 255, 255, 0.1);
                    border-radius: var(--jpf-r-md);
                    padding: 24px;
                    max-width: 420px;
                    width: 90%;
                    box-shadow: 0 10px 30px rgba(0,0,0,0.5);
                    text-align: center;
                ">
                    <h2 style="margin-top: 0; color: #fff; font-size: 1.25rem; font-weight: 700; margin-bottom: 12px;">${title}</h2>
                    <p style="color: rgba(255,255,255,0.7); font-size: 0.92rem; line-height: 1.5; margin-bottom: 24px;">${message}</p>
                    <div style="display: flex; gap: var(--jpf-gap); justify-content: center;">
                        <button id="dialog-confirm-btn" class="profiles-btn btn-danger" style="padding: 10px 20px; font-weight: 600; min-width: 100px;">Confirm</button>
                        <button id="dialog-cancel-btn" class="profiles-btn btn-secondary" style="padding: 10px 20px; font-weight: 600; min-width: 100px;">Cancel</button>
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
                    background: #181818;
                    border: 1px solid rgba(255, 255, 255, 0.1);
                    border-radius: var(--jpf-r-md);
                    padding: 24px;
                    max-width: 420px;
                    width: 90%;
                    box-shadow: 0 10px 30px rgba(0,0,0,0.5);
                    text-align: center;
                ">
                    <h2 style="margin-top: 0; color: #fff; font-size: 1.25rem; font-weight: 700; margin-bottom: 12px;">${title}</h2>
                    <p style="color: rgba(255,255,255,0.7); font-size: 0.92rem; line-height: 1.5; margin-bottom: 24px;">${message}</p>
                    <div style="display: flex; justify-content: center;">
                        <button id="dialog-close-btn" class="profiles-btn btn-primary" style="padding: 10px 24px; font-weight: 600; min-width: 120px;">OK</button>
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

        updateStoredCredentials: function (newToken, newUserId) {
            try {
                const credsStr = localStorage.getItem('jellyfin_credentials');
                if (credsStr) {
                    const creds = JSON.parse(credsStr);
                    if (creds && Array.isArray(creds.Servers)) {
                        const currentServerId = typeof ApiClient.serverId === 'function' ? ApiClient.serverId() : (ApiClient.serverId || '');
                        creds.Servers.forEach(server => {
                            if (!currentServerId || server.Id === currentServerId || creds.Servers.length === 1) {
                                server.AccessToken = newToken;
                                server.UserId = newUserId;
                            }
                        });
                        localStorage.setItem('jellyfin_credentials', JSON.stringify(creds));
                    }
                }
            } catch (e) {
                console.error("ProfilesPlugin: Stored credentials update failed:", e);
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
                avatarColor: pick(p, 'avatarColor', '#00A4DC'),
                requiresPin: pick(p, 'requiresPin', false),
                // "A PIN exists" — distinct from requiresPin, which is false on the LAN when
                // the bypass is enabled. Forms must use this one.
                hasPin: pick(p, 'hasPin', false),
                isMaster: pick(p, 'isMaster', false),
                lockoutMinutes: pick(p, 'lockoutMinutes', 5),
                maxSubProfiles: pick(p, 'maxSubProfiles', 5),
                bypassPinOnLocalNetwork: pick(p, 'bypassPinOnLocalNetwork', false),
                allowedDeviceIds: pick(p, 'allowedDeviceIds', []),
                enabledFolders: pick(p, 'enabledFolders', []),
                blockedTags: pick(p, 'blockedTags', []),
                allowedTags: pick(p, 'allowedTags', []),
                isBonfire: pick(p, 'isBonfire', false),
                profileImage: pick(p, 'profileImage', null),
                masterUserId: pick(p, 'masterUserId', null)
            }));
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

        /// The avatar swatches are identical in both forms; keeping one copy means a palette
        /// change lands in both places at once.
        /// Dims the avatar colour once a picture is set.
        ///
        /// The colour is the background behind the initial, so with a picture it does
        /// nothing at all — and it was twenty-one swatches over three rows, the largest
        /// thing on the form, sitting there inert. Dimmed rather than hidden: it starts
        /// mattering again the moment the picture is removed, and a control that
        /// vanishes and reappears is worse than one that fades.
        setColorGroupInert: function (prefix, hasPicture) {
            const group = document.getElementById(prefix + '-color-group');
            if (!group) return;

            group.classList.toggle('is-inert', !!hasPicture);

            const hint = group.querySelector('[data-role="color-hint"]');
            if (hint) {
                hint.textContent = hasPicture
                    ? 'Not used while a picture is set.'
                    : 'Used as the avatar background when no picture is set.';
            }
        },

        renderColorPicker: function (selectedColor) {
            const palette = [
                '#00A4DC', '#E50914', '#22C55E', '#EAB308', '#A855F7', '#EC4899',
                '#F97316', '#06B6D4', '#3B82F6', '#10B981', '#6366F1', '#8B5CF6',
                '#D946EF', '#F43F5E', '#14B8A6', '#F59E0B', '#84CC16', '#64748B'
            ];
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

            const headers = this.getAuthHeaders(masterState.masterToken);
            this._sharedForm = Promise.all([
                fetch(apiClient.getUrl('plugins/profiles/libraries'), { headers })
                    .then(res => res.json()),
                fetch(apiClient.getUrl('plugins/profiles/devices'), { headers })
                    .then(res => res.json()).catch(() => []),
                this.fetchLibraryTags(apiClient, masterState.masterToken, masterState.masterUserId),
                this.fetchAvatarLibrary(apiClient, masterState.masterToken)
            ]).then(([libraries, devices, libraryTags, avatarLibrary]) => ({
                libraries, devices, libraryTags, avatarLibrary
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
                    return Array.from(new Set(tags.filter(t => t)))
                        .sort((a, b) => a.localeCompare(b));
                })
                .catch(() => []);
        },

        renderTagSuggestions: function (id, tags) {
            return `<datalist id="${id}">${(tags || []).map(t => `<option value="${escapeHtml(t)}"></option>`).join('')}</datalist>`;
        },

        renderTagChip: function (tag) {
            const safe = escapeHtml(tag);
            return `<span class="tag-chip" data-tag="${safe}"><span>${safe}</span><button type="button" class="tag-chip-remove" tabindex="0" aria-label="Remove tag ${safe}">×</button></span>`;
        },

        renderTagEditor: function (id, tags, placeholder, suggestionsId) {
            const chips = (tags || []).map(t => this.renderTagChip(t)).join('');
            return `
                <div class="tag-editor" id="${id}">
                    <div class="tag-chip-list" ${(tags || []).length ? '' : 'data-empty="true"'}>${chips}</div>
                    <div class="tag-input-row">
                        <input type="text" class="tag-input" placeholder="${escapeHtml(placeholder)}" list="${suggestionsId}" autocomplete="off" />
                        <button type="button" class="profiles-btn btn-secondary tag-add-btn">Add</button>
                    </div>
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
                .filter(t => t);
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
            this.bindEvents();
            this.injectStyles();
            // Kicked off before the first route check so the gate decision is usually made
            // with the real answer in hand rather than the cached one.
            // Before any Jellyfin view renders: the cached rules have to be in the document
            // by the time the first library card paints, or the artwork this profile is not
            // meant to see gets a frame on screen.
            this.applyCachedLibraryArtwork();
            this.loadLibraryArtwork();
            this.loadSwitcherPrefs();
            this.bindPanicShortcut();
            // Bound once for the life of the page. It resolves the active Bonfire screen on
            // every event and does nothing when there is none, so it covers the gate, the
            // PIN prompt, the profile forms and every dialog without per-screen wiring.
            this._bindOverlayFocusTrap();
            // Before validateSessionState, which can trigger a reload of its own.
            this.checkPersistedPanic();
            if (this._panicDisabled) return;
            this.validateSessionState();
            // If the user refreshes while a profile is active, restart the inactivity timer
            setTimeout(() => this.initLockoutTimer(), 800);
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

            // SPA navigation events
            window.addEventListener('popstate', doCheck);
            window.addEventListener('hashchange', doCheck);

            // Intercept history.pushState / replaceState (React-router style navigation)
            ['pushState', 'replaceState'].forEach(method => {
                const orig = history[method];
                history[method] = function (...args) {
                    orig.apply(history, args);
                    // Small delay so the URL is committed before we read it
                    setTimeout(doCheck, 0);
                };
            });

            // Fix #3: Clear the cached master token on native Jellyfin sign-out so it
            // does not persist in localStorage on shared / public devices.
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

            // Fix #8: 500ms interval is sufficient since viewshow/popstate/hashchange
            // already cover all SPA navigation. The poll is only a safety net for rare
            // DOM-mutation scenarios (e.g., video OSD).
            setInterval(doCheck, 500);

            // Initial check on load
            setTimeout(doCheck, 200);
        },

        checkRoute: function () {
            // Emergency disable: do nothing at all. This runs on a 500 ms timer, so without
            // this guard it would rebuild the gate immediately after the teardown.
            if (this._panicDisabled) return;

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

            const isHome = !isIgnoredPage && (
                hash === '' || 
                hash === '#/' || 
                hash.includes('home') || 
                path.endsWith('/home') || 
                path.endsWith('/home.html') ||
                // If there is no hash and we are at /web/index.html or root
                (!hash && (path.endsWith('index.html') || path === '/' || path === '/web/'))
            );

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
                    skipReveal = true;
                    this.interceptHomeAndShowProfiles();
                }
            } else {
                // If we navigate away from home, ensure the overlay is removed if it somehow got stuck
                if (document.getElementById('profiles-gate-overlay') && this.isProfileSessionActive()) {
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
            const hasOsdDom = !!document.querySelector(
                '.videoOsdBottom, .osdControls, .upNextContainer, ' +
                '[class*="videoOsd"], [class*="osdBottom"], .btnExitVideo'
            );

            // ── Admin / server-management pages ─────────────────────────────────
            // Exception: our own plugin settings page (configurationpage?name=Profiles)
            // is the only admin-area page where the button should remain visible.
            const isProfilesSettingsPage = hash.includes('configurationpage') &&
                                           hash.toLowerCase().includes('name=profiles');

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

            const viewType = (isActivePlayer || hasOsdDom) ? 'videoosd'
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

            // Inject sidebar link fallbacks for TV D-pad targeting
            this.injectSidebarLink();
        },

        // Smoothly fades the page back in after a profile switch.
        // Guards on _viewShowFired to ensure React has rendered the view before we
        // expose it — otherwise a blank white shell flashes for a moment.
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
            this.isMonitoringUsers = true;

            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            const token = masterState ? masterState.masterToken : apiClient.accessToken();
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
        // Samsung's Tizen runtime clears sessionStorage on a full reload, and a full reload
        // is exactly how a profile switch finishes. The marker was therefore gone by the
        // time the page came back, validateSessionState() concluded the app had been closed
        // and reverted to the master token — the avatar changed and nothing else did
        // (issues #15 and #16, reported independently by two people).
        //
        // On Tizen only, the marker is mirrored into localStorage. The cost is that closing
        // the app there leaves the profile active instead of returning to the picker. That
        // is the safer of the two failures: the account it would otherwise revert to is the
        // master, which is the less restricted one.
        TIZEN_MIRROR_PREFIX: 'jpf-persist-',

        /// True on Samsung's TV runtime, where sessionStorage does not survive a reload.
        _isTizenRuntime: function () {
            if (this._tizenRuntime === undefined) {
                let detected = false;
                try {
                    const ua = (navigator && navigator.userAgent) || '';
                    detected = typeof window.tizen !== 'undefined' || /tizen/i.test(ua);
                } catch (e) { /* no navigator — assume not */ }
                this._tizenRuntime = detected;
            }
            return this._tizenRuntime;
        },

        _sessionSet: function (key, value) {
            try { sessionStorage.setItem(key, value); } catch (e) { /* storage blocked */ }
            if (!this._isTizenRuntime()) return;
            try { localStorage.setItem(this.TIZEN_MIRROR_PREFIX + key, value); } catch (e) { /* full */ }
        },

        _sessionGet: function (key) {
            let value = null;
            try { value = sessionStorage.getItem(key); } catch (e) { /* storage blocked */ }
            if (value !== null || !this._isTizenRuntime()) return value;

            try {
                value = localStorage.getItem(this.TIZEN_MIRROR_PREFIX + key);
                // Put it back where the rest of the code expects to find it, so this
                // fallback costs one read per page load rather than one per call.
                if (value !== null) sessionStorage.setItem(key, value);
            } catch (e) { /* unreadable — treat as absent */ }
            return value;
        },

        _sessionRemove: function (key) {
            try { sessionStorage.removeItem(key); } catch (e) { /* storage blocked */ }
            // Always clear the mirror, whatever the runtime says now: a stale copy left by
            // an earlier detection would outlive every sign-out.
            try { localStorage.removeItem(this.TIZEN_MIRROR_PREFIX + key); } catch (e) { /* ignore */ }
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

            if (persist) {
                try {
                    localStorage.setItem(this.config.panicKey, String(Date.now()));
                } catch (e) { /* private mode — the in-memory flag still holds for this page */ }
            }

            this.removeProfileOverlay();
            const bubble = document.getElementById('profiles-floating-bubble');
            if (bubble) bubble.remove();
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
            dialog.style.cssText = `
                position: fixed; top: 0; left: 0; right: 0; bottom: 0;
                background: rgba(0,0,0,0.85); backdrop-filter: blur(8px);
                display: flex; align-items: center; justify-content: center;
                z-index: ${DIALOG_Z};
            `;

            dialog.innerHTML = `
                <div style="background:#181818; border:1px solid rgba(255,255,255,0.1); border-radius: var(--jpf-r-md);
                            padding:24px; max-width:460px; width:90%; box-shadow:0 10px 30px rgba(0,0,0,0.5);">
                    <h2 style="margin:0 0 12px 0; color:#fff; font-size:1.2rem; font-weight:700;">Emergency disable</h2>
                    <p style="color:rgba(255,255,255,0.7); font-size:0.9rem; line-height:1.5; margin:0 0 16px 0;">
                        Enter the code your server administrator set. This shuts the Bonfire switcher off
                        until Jellyfin is restarted — the profile gate disappears and this account is used
                        as-is. It does not unlock anyone else's profile.
                    </p>
                    <input type="password" id="profiles-panic-input" autocomplete="off" placeholder="Emergency code"
                           style="width:100%; box-sizing:border-box; padding:10px; font-size:1rem; margin-bottom:8px;" />
                    <div id="profiles-panic-error" style="display:none; color:#ff6b6b; font-size:0.85rem; font-weight:600; margin-bottom:8px;"></div>
                    <div style="display:flex; gap: var(--jpf-gap); justify-content:flex-end; margin-top:12px;">
                        <button id="profiles-panic-cancel" class="profiles-btn btn-secondary" style="padding:10px 20px; font-weight:600;">Cancel</button>
                        <button id="profiles-panic-submit" class="profiles-btn btn-danger" style="padding:10px 20px; font-weight:600;">Disable</button>
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
                submitBtn.textContent = 'Checking…';
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
                        this.showAlert('Bonfire disabled',
                            'The switcher is off until Jellyfin restarts. Reload the page if anything still looks wrong.');
                        return;
                    }
                    if (res.status === 429) throw new Error('Too many attempts. Try again in an hour, or restart Jellyfin.');
                    throw new Error('Incorrect code.');
                })
                .catch(err => {
                    errDiv.textContent = err.message || 'Incorrect code.';
                    errDiv.style.display = 'block';
                    submitBtn.disabled = false;
                    submitBtn.textContent = 'Disable';
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
                const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey) || 'null');
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

        /// True only when we know the account wants the startup prompt. Unknown reads as
        /// false so the gate is never raised on a guess.
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
                                    color: profile.avatarColor || '#00A4DC',
                                    initial: profile.avatarInitial || (profile.profileName ? profile.profileName.charAt(0).toUpperCase() : 'P'),
                                    profileImage: profile.profileImage || null
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
                color: '#00A4DC',
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

            // Dual-token check: if tab/app was closed, sessionStorage is wiped out.
            // If the current token in Jellyfin is NOT the master token, but sessionStorage is empty,
            // we must revert the browser to the master token and force the selection gate to display.
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            if (masterState && masterState.masterToken) {
                const currentUserId = apiClient.getCurrentUserId();
                if (this.normalizeGuid(currentUserId) === this.normalizeGuid(masterState.masterUserId)) {
                    if (currentToken !== masterState.masterToken) {
                        masterState.masterToken = currentToken;
                        localStorage.setItem(this.config.masterStorageKey, JSON.stringify(masterState));
                        console.log("ProfilesPlugin: Master session token updated to match new valid token.");
                    }
                } else if (currentToken !== masterState.masterToken && !this.isProfileSessionActive()) {
                    this.updateStoredCredentials(masterState.masterToken, masterState.masterUserId);
                    apiClient.setAuthenticationInfo(masterState.masterToken, masterState.masterUserId);
                    // Hide current page instantly so there is no visible frame
                    // between old page unloading and new page's head script running.
                    document.documentElement.style.cssText = 'opacity:0;background:#101010;color-scheme:dark';
                    localStorage.setItem(this.config.switchingKey, '1');
                    window.location.reload();
                }
            }
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

            const masterUserId = apiClient.getCurrentUserId();
            const masterToken = apiClient.accessToken();

            if (!masterUserId || !masterToken) return;

            let masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey)) || {};
            if (!masterState.masterToken) {
                masterState.masterToken = masterToken;
                masterState.masterUserId = masterUserId;
                localStorage.setItem(this.config.masterStorageKey, JSON.stringify(masterState));
            }

            this.fetchAndRenderProfiles(apiClient, masterUserId, masterToken);
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
            if (forceRefresh) {
                this.invalidateProfileCache();
            } else if (this.cachedProfiles && this.cachedProfiles.length) {
                // Consume the prefetch exactly once, then drop it.
                const profiles = this.cachedProfiles;
                this.invalidateProfileCache();
                this.showProfileOverlay(profiles);
                return;
            }

            const url = apiClient.getUrl(`plugins/profiles/list`);

            fetch(url, {
                // Never let a conditional/heuristic cache answer this — it drives the
                // PIN and library state shown in the editor.
                cache: 'no-store',
                headers: this.getAuthHeaders(masterToken)
            })
            .then(res => {
                if (res.status === 401) {
                    this.handleSessionExpired();
                    throw new Error("Unauthorized");
                }
                return res.json();
            })
            .then(profiles => {
                const normalized = this.normalizeProfiles(profiles);
                // Deliberately NOT stored in cachedProfiles: that field is the one-shot
                // prefetch buffer, and repopulating it here made the *next* call short-circuit
                // to data that was already stale.
                localStorage.setItem('jellyfin_profiles_cached_list', JSON.stringify(normalized));
                this.showProfileOverlay(normalized);
            })
            .catch(err => {
                console.error("Failed to load sub-profiles:", err);
                localStorage.removeItem(this.config.masterStorageKey);
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
                const warmState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
                if (warmState && warmState.masterToken) {
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

            // Reveal the page NOW — the overlay covers the home screen so there
            // is no blank-page flash.  checkRoute() skipped _revealPage() earlier
            // specifically so we could do it here at the right moment.
            this._viewShowFired = true; // overlay is rendered; treat this as view-ready
            this._revealPage();

            // Two rAF calls: first lets the browser paint with opacity:0 (compositing
            // layer created silently), second begins the CSS opacity transition.
            requestAnimationFrame(() => requestAnimationFrame(() => {
                overlay.style.opacity = ''; // CSS transition takes over
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
            const state = {};
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey) || "null");

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
            if (masterState && masterState.masterToken && rows.length) {
                fetch(ApiClient.getUrl("plugins/profiles/library-artwork/" + profileId), {
                    cache: "no-store",
                    headers: this.getAuthHeaders(masterState.masterToken)
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
                            headers: {
                                "Content-Type": "application/json",
                                ...this.getAuthHeaders(masterState ? masterState.masterToken : ApiClient.accessToken())
                            },
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
                            if (!res.ok) return res.text().then(t => { throw new Error(t || "Could not save the artwork."); });
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
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey) || "null");
            const token = masterState ? masterState.masterToken : apiClient.accessToken();

            this.fetchAvatarLibrary(apiClient, token).then(library => {
                if (document.getElementById("profiles-libart-dialog")) return;

                const dialog = document.createElement("div");
                dialog.id = "profiles-libart-dialog";
                dialog.style.cssText =
                    "position:fixed; top:0; left:0; right:0; bottom:0;" +
                    "background:rgba(0,0,0,0.85); backdrop-filter:blur(8px);" +
                    "display:flex; align-items:center; justify-content:center; z-index:" + DIALOG_Z + ";";
                dialog.innerHTML =
                    '<div style="background:#181818; border:1px solid rgba(255,255,255,0.1); border-radius: var(--jpf-r-md);' +
                    ' padding:22px; max-width:420px; width:94%; max-height:86vh; overflow:auto;">' +
                    '<h2 style="margin:0 0 14px 0; color:#fff; font-size:1.15rem; font-weight:700;">Library artwork</h2>' +
                    '<div id="profiles-libart-host"></div>' +
                    '<div id="profiles-libart-error" style="display:none; color:#ff6b6b; font-size:0.85rem;' +
                    ' font-weight:600; margin-top:8px;"></div>' +
                    '<div style="display:flex; gap: var(--jpf-gap); justify-content:flex-end; margin-top:16px;">' +
                    '<button id="profiles-libart-cancel" class="profiles-btn btn-secondary" style="padding:10px 20px; font-weight:600;">Cancel</button>' +
                    '<button id="profiles-libart-save" class="profiles-btn btn-primary" style="padding:10px 20px; font-weight:600;">Use picture</button>' +
                    '</div></div>';
                document.body.appendChild(dialog);

                const host = dialog.querySelector("#profiles-libart-host");
                host.innerHTML = this.renderAvatarPicker("libart", library, null, "#00A4DC");
                const picker = this.initAvatarPicker(host, "libart", library, null, () => {});

                const close = () => dialog.remove();
                dialog.querySelector("#profiles-libart-cancel").addEventListener("click", close);
                dialog.querySelector("#profiles-libart-save").addEventListener("click", () => {
                    const picked = picker.get();
                    if (!picked.image && !picked.libraryId) {
                        const err = dialog.querySelector("#profiles-libart-error");
                        err.textContent = "Choose a picture first.";
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
        TRAP_SURFACE_SELECTOR: '#profiles-gate-overlay, [id^="profiles-"][id$="-dialog"]',

        /// The surface the remote should currently be confined to, or null when Bonfire
        /// has nothing on screen. Dialogs render above the gate, and the last one in the
        /// DOM is the topmost, so document order picks the right one.
        _activeTrapSurface: function () {
            const surfaces = document.querySelectorAll(this.TRAP_SURFACE_SELECTOR);
            if (!surfaces.length) return null;

            const last = surfaces[surfaces.length - 1];
            // A dialog mid-fade still counts; one already detached does not.
            return last.isConnected === false ? null : last;
        },

        /// Everything inside a surface a remote can land on. Hidden elements are left out:
        /// the overlay swaps between a grid and a form, and the old one lingers.
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
                '#bonfire-back-btn, #switcher-mode-back-btn, #settings-back-btn, #profiles-resume-btn, #profiles-libart-cancel'
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
                if (r.top < 0 || r.bottom > h) {
                    el.scrollIntoView({ block: 'nearest', inline: 'nearest' });
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

                const inside = surface.contains(e.target);
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
            const onFocusIn = (e) => {
                const surface = this._activeTrapSurface();
                if (!surface) return;
                if (e.target && e.target.closest && e.target.closest(this.TRAP_SURFACE_SELECTOR)) return;
                if (e.target === document.body || e.target === document.documentElement) return;
                const items = this._overlayFocusables(surface);
                if (items.length) items[0].focus();
            };

            document.addEventListener('keydown', onKeyDown, true);
            document.addEventListener('focusin', onFocusIn, true);
            this._overlayTrap = { onKeyDown: onKeyDown, onFocusIn: onFocusIn };
        },

        _releaseOverlayFocusTrap: function () {
            if (!this._overlayTrap) return;
            document.removeEventListener('keydown', this._overlayTrap.onKeyDown, true);
            document.removeEventListener('focusin', this._overlayTrap.onFocusIn, true);
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

            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            if (!masterState || !masterState.masterToken) return;

            const apiClient = ApiClient;
            if (!apiClient) return;

            const currentUserId = typeof apiClient.getCurrentUserId === 'function'
                ? apiClient.getCurrentUserId() : null;
            if (!currentUserId) return;

            const url = apiClient.getUrl('plugins/profiles/list');
            fetch(url, { headers: this.getAuthHeaders(masterState.masterToken) })
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
        lockActiveProfile: function () {
            this.stopInactivityTimer();
            this.clearProfileSession();
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            if (masterState) {
                this.updateStoredCredentials(masterState.masterToken, masterState.masterUserId);
                ApiClient.setAuthenticationInfo(masterState.masterToken, masterState.masterUserId);
            }
            this.interceptHomeAndShowProfiles();
        },


        renderOverlayContent: function (overlay, profiles) {
            // Claims the screen so a form still loading in the background does not
            // draw itself over the grid when it finally returns.
            this.beginNavigation();

            const title = this.isManageMode ? "Manage Profiles" : "Who's Watching?";
            const manageBtnText = this.isManageMode ? "Done" : "Manage Profiles";

            const masterProfile = profiles.find(p => p.isMaster && !p.isBonfire);
            const maxSubProfiles = masterProfile ? masterProfile.maxSubProfiles : 5;
            const subProfileCount = profiles.filter(p => !p.isMaster && !p.isBonfire).length;
            const atLimit = subProfileCount >= maxSubProfiles;

            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            const localMasterId = masterState ? this.normalizeGuid(masterState.masterUserId) : '';

            // Same condition the two settings tiles used, so Settings shows up in exactly
            // the cases they used to.
            const hasLocalMaster = profiles.some(p => p.isMaster && !p.isBonfire);

            // Group profiles by masterUserId
            const grouped = {};
            for (const p of profiles) {
                const key = this.normalizeGuid(p.masterUserId || (p.isBonfire ? '' : localMasterId));
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
            const signedInId = (typeof ApiClient.getCurrentUserId === 'function')
                ? this.normalizeGuid(ApiClient.getCurrentUserId())
                : '';

            const renderCard = (p) => `
                <div class="profile-card ${this.isManageMode ? 'manage-mode' : ''}${
                    signedInId && this.normalizeGuid(p.profileUserId) === signedInId ? ' is-current' : ''
                }" data-id="${p.profileUserId}" data-pin="${p.requiresPin}" tabindex="0"${
                    signedInId && this.normalizeGuid(p.profileUserId) === signedInId ? ' aria-current="true"' : ''
                }>
                    <div class="profile-avatar-container">
                        ${p.isMaster ? `
                        <div class="profile-crown">
                            <svg viewBox="0 0 24 24" fill="currentColor" style="width: 28px; height: 28px; color: #ffb800; filter: drop-shadow(0 2px 5px rgba(0,0,0,0.55));">
                                <path d="M5 16h14a1 1 0 0 0 1-.76l2.89-10.12a.5.5 0 0 0-.74-.53l-5.6 3.73-4.11-6.17a.5.5 0 0 0-.88 0L7.45 8.32 1.85 4.59a.5.5 0 0 0-.74.53L4 15.24a1 1 0 0 0 1 .76z"/>
                                <rect x="4" y="18" width="16" height="2" rx="1"/>
                            </svg>
                        </div>
                        ` : ''}
                        <div class="profile-avatar" style="background-color: ${safeColor(p.avatarColor)}; overflow: hidden; display: flex; align-items: center; justify-content: center; position: relative;">
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
                        <div class="profile-bonfire-indicator" title="Bonfire Profile">
                            <span class="material-icons" style="font-size: 1.15rem; color: #fff;">local_fire_department</span>
                        </div>
                        ` : ''}
                    </div>
                    <div class="profile-name">
                        <span>${escapeHtml(p.profileName)}</span>
                        ${signedInId && this.normalizeGuid(p.profileUserId) === signedInId
                            ? '<span class="profile-current-badge">Watching now</span>' : ''}
                        ${this.isManageMode ? `
                            <span class="profile-pin-badge ${p.requiresPin ? 'locked' : 'unlocked'}">
                                ${p.requiresPin ? 'PIN Protected' : 'No PIN'}
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
                    headerTitle = "Your Bonfire";
                    headerIcon = "local_fire_department";
                } else {
                    const masterProfileForGroup = groupProfiles.find(p => p.isMaster);
                    const groupName = masterProfileForGroup ? escapeHtml(masterProfileForGroup.profileName) : "Guest";
                    headerTitle = `${groupName}'s Bonfire`;
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

                    // Deliberately in both modes. This used to render only when NOT managing,
            // so the one screen actually called "Manage Profiles" was the one screen
            // with no way to add a profile.
            if (!atLimit) {
                        cardsHtml += `
                            <div class="profile-card action-add-profile" tabindex="0">
                                <div class="profile-avatar-container">
                                    <div class="profile-avatar add-avatar">+</div>
                                </div>
                                <div class="profile-name">Add Profile</div>
                            </div>
                        `;
                    } else if (!this.isManageMode) {
                        cardsHtml += `
                            <div class="profiles-limit-notice">${subProfileCount}/${maxSubProfiles} profiles — limit reached</div>
                        `;
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

            overlay.innerHTML = `
                <div class="profiles-modal-content anim-fade-in">
                    <h1 class="profiles-title">${title}</h1>
                    ${sectionsHtml}
                    <div class="profiles-footer">
                        <button id="profiles-toggle-manage-btn" class="profiles-btn btn-secondary">${manageBtnText}</button>
                        ${this.isManageMode && hasLocalMaster
                            ? '<button id="profiles-settings-btn" class="profiles-btn btn-secondary">Settings</button>'
                            : ''}
                        ${this._resumeState && !this.isManageMode
                            ? '<button id="profiles-resume-btn" class="profiles-btn btn-secondary">Cancel</button>'
                            : ''}
                    </div>
                    <!-- Deliberately plain and dim. It has to be reachable by D-pad, because
                         a TV has no keyboard shortcut, and this screen is exactly where
                         someone locked out by a broken switcher would be standing.
                         Hidden until the server confirms a code is configured. -->
                    <button id="profiles-panic-link" tabindex="0" style="
                        display: none; background: none; border: none; color: rgba(255,255,255,0.28);
                        font-size: 0.72rem; margin-top: 1.5rem; cursor: pointer;
                        text-decoration: underline; padding: 6px 10px;">Can't get past this screen?</button>
                </div>
            `;

            this.attachOverlayInteractions(overlay, profiles);
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

                    if (this.isManageMode) {
                        this.showEditProfileModal(profile);
                    } else {
                        if (profile.requiresPin) {
                            this.promptPinEntry(profileId);
                        } else {
                            this.executeProfileSwitch(profileId, null);
                        }
                    }
                });
            });

            // "Add Profile" action
            const addCard = overlay.querySelector('.action-add-profile');
            if (addCard) {
                addCard.addEventListener('click', () => {
                    if (this._switchLock) return;
                    const masterProfile = profiles.find(p => p.isMaster && !p.isBonfire);
                    const masterRequiresPin = masterProfile && masterProfile.requiresPin;
                    
                    if (masterRequiresPin) {
                        ApiClient.getPluginConfiguration(this.pluginId).then(config => {
                            if (config.RequireMasterPinForCreation) {
                                this.promptMasterPinEntry('create', () => {
                                    this.showAddProfileModal();
                                });
                            } else {
                                this.showAddProfileModal();
                            }
                        }).catch(() => {
                            this.showAddProfileModal();
                        });
                    } else {
                        this.showAddProfileModal();
                    }
                });
            }

            // Settings: everything that is not a profile.
            const settingsBtn = overlay.querySelector('#profiles-settings-btn');
            if (settingsBtn) {
                settingsBtn.addEventListener('click', () => {
                    if (this._switchLock) return;
                    this.showSettingsMenu();
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
                        if (masterProfile && masterProfile.requiresPin) {
                            this.promptMasterPinEntry('manage', () => {
                                this.isManageMode = true;
                                this.renderOverlayContent(overlay, profiles);
                            });
                        } else {
                            this.isManageMode = true;
                            this.renderOverlayContent(overlay, profiles);
                        }
                    }
                });
            }
        },

        promptPinEntry: function (profileId) {
            this.beginNavigation();
            const content = document.querySelector('.profiles-modal-content');
            content.innerHTML = `
                <h1 class="profiles-title">Enter Profile PIN</h1>
                <div class="pin-entry-container">
                    <input type="text" id="profile-pin-input" maxlength="8" pattern="[0-9]*" inputmode="numeric" placeholder="••••" autocomplete="one-time-code" data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true" autofocus />
                    <div id="pin-error-msg" style="display:none; color:#ff6b6b; font-size:0.9rem; font-weight:600; text-align:center; margin-top:-0.5rem;"></div>
                    <div class="pin-actions">
                        <button id="pin-submit-btn" class="profiles-btn btn-primary">Unlock</button>
                        <button id="pin-cancel-btn" class="profiles-btn btn-secondary">Back</button>
                    </div>
                </div>
            `;

            const pinInput = document.getElementById('profile-pin-input');
            const errorMsg = document.getElementById('pin-error-msg');
            pinInput.focus();

            // Track in-flight silent verify so we can cancel it if the user keeps typing,
            // and prevent a second switch from firing if one is already in progress.
            let verifyController = null;
            let switchInProgress = false;

            const showPinError = (msg) => {
                switchInProgress = false;
                pinInput.style.borderColor = '#ff6b6b';
                pinInput.style.boxShadow = '0 0 15px rgba(255,107,107,0.5)';
                errorMsg.textContent = msg || 'Incorrect PIN. Please try again.';
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

            pinInput.addEventListener('input', () => {
                clearError();
                const currentValue = pinInput.value;

                // Need at least 4 digits, and don't fire another switch if one is underway
                if (currentValue.length < 4 || switchInProgress || this._switchLock) return;

                // Cancel any previous in-flight verify — only the latest keystroke matters
                if (verifyController) verifyController.abort();
                verifyController = typeof AbortController !== 'undefined' ? new AbortController() : null;

                const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
                if (!masterState) return;

                // Silent verify — no error shown on failure, user just keeps typing
                fetch(ApiClient.getUrl('plugins/profiles/verify-pin'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...this.getAuthHeaders(masterState.masterToken)
                    },
                    body: JSON.stringify({ profileId: profileId, pin: currentValue }),
                    ...(verifyController ? { signal: verifyController.signal } : {})
                })
                .then(res => {
                    if (res.status === 401) {
                        this.handleSessionExpired();
                        return;
                    }
                    // Only proceed if PIN matched and nothing else already triggered a switch
                    if (res.ok && !switchInProgress && !this._switchLock) {
                        switchInProgress = true;
                        this.executeProfileSwitch(profileId, currentValue, () => {
                            // Verify said OK but switch failed (edge case) — reset silently
                            switchInProgress = false;
                        });
                    }
                })
                .catch(err => {
                    // AbortError = user typed another digit, a new verify is already in flight
                    // Other errors (network) = ignore silently, user can still hit Enter
                });
            });

            // Manual submit — only place where we show an error on wrong PIN
            const submitPin = () => {
                if (switchInProgress || this._switchLock) return;
                if (verifyController) verifyController.abort();
                verifyController = null;
                const pin = pinInput.value;
                if (!pin) return;
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
                if (verifyController) verifyController.abort();
                this.isManageMode = false;
                this.showProfileOverlay(this.currentProfiles);
            });
        },

        promptMasterPinEntry: function (actionType, callback) {
            this.beginNavigation();
            const masterProfile = this.currentProfiles.find(p => p.isMaster && !p.isBonfire);
            if (!masterProfile) return;

            const content = document.querySelector('.profiles-modal-content');
            content.innerHTML = `
                <h1 class="profiles-title">Enter Master PIN</h1>
                <div class="pin-entry-container">
                    <input type="text" id="master-pin-input" maxlength="8" pattern="[0-9]*" inputmode="numeric" placeholder="••••" autocomplete="one-time-code" data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true" autofocus />
                    <div id="master-pin-error-msg" style="display:none; color:#ff6b6b; font-size:0.9rem; font-weight:600; text-align:center; margin-top:-0.5rem;"></div>
                    <div class="pin-actions">
                        <button id="master-pin-submit-btn" class="profiles-btn btn-primary">Submit</button>
                        <button id="master-pin-cancel-btn" class="profiles-btn btn-secondary">Cancel</button>
                    </div>
                </div>
            `;

            const pinInput = document.getElementById('master-pin-input');
            const errorMsg = document.getElementById('master-pin-error-msg');
            pinInput.focus();

            let verifyController = null;
            let verified = false; // prevent callback firing more than once

            const showPinError = (msg) => {
                verified = false;
                pinInput.style.borderColor = '#ff6b6b';
                pinInput.style.boxShadow = '0 0 15px rgba(255,107,107,0.5)';
                errorMsg.textContent = msg || 'Incorrect PIN. Please try again.';
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

            pinInput.addEventListener('input', () => {
                clearError();
                const currentValue = pinInput.value;
                if (currentValue.length < 4 || verified || verifyInProgress) return;

                // Cancel previous in-flight verify — only the latest matters
                if (verifyController) verifyController.abort();
                verifyController = typeof AbortController !== 'undefined' ? new AbortController() : null;

                const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
                if (!masterState) return;

                // Silent verify — no error on failure, user keeps typing
                fetch(ApiClient.getUrl('plugins/profiles/verify-pin'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...this.getAuthHeaders(masterState.masterToken)
                    },
                    body: JSON.stringify({ profileId: masterProfile.profileUserId, pin: currentValue }),
                    ...(verifyController ? { signal: verifyController.signal } : {})
                })
                .then(res => {
                    if (res.status === 401) {
                        this.handleSessionExpired();
                        return;
                    }
                    if (res.ok && !verified && !verifyInProgress) {
                        verified = true;
                        this.masterPin = currentValue;
                        callback();
                    }
                })
                .catch(() => {
                    // AbortError or network error — ignore silently
                });
            });

            const submitPin = () => {
                if (verifyInProgress || verified) return;
                if (verifyController) verifyController.abort();
                verifyController = null;
                const pin = pinInput.value;
                if (!pin) return;
                const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
                if (!masterState) return;

                verifyInProgress = true;
                fetch(ApiClient.getUrl('plugins/profiles/verify-pin'), {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        ...this.getAuthHeaders(masterState.masterToken)
                    },
                    body: JSON.stringify({ profileId: masterProfile.profileUserId, pin: pin })
                })
                .then(res => {
                    verifyInProgress = false;
                    if (res.status === 401) {
                        this.handleSessionExpired();
                        throw new Error('Session expired');
                    }
                    if (!res.ok) {
                        return res.text().then(text => {
                            throw new Error(text || 'Incorrect Master PIN. Please try again.');
                        });
                    }
                    this.masterPin = pin;
                    verified = true;
                    callback();
                })
                .catch(err => {
                    verifyInProgress = false;
                    if (err.message !== 'Session expired') {
                        showPinError(err.message || 'Incorrect Master PIN. Please try again.');
                    }
                });
            };

            document.getElementById('master-pin-submit-btn').addEventListener('click', submitPin);
            pinInput.addEventListener('keydown', (e) => {
                if (e.key === 'Enter') submitPin();
            });

            document.getElementById('master-pin-cancel-btn').addEventListener('click', () => {
                if (verifyController) verifyController.abort();
                this.showProfileOverlay(this.currentProfiles);
            });
        },

        // onError: optional callback(message) invoked on a failed switch.
        // Callers capture their own DOM references via closure so we never re-query
        // the DOM inside an async callback (which can race against overlay teardown).
        executeProfileSwitch: function (profileId, pin, onError) {
            if (this._switchLock) return;

            const apiClient = ApiClient;
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            if (!masterState) return;

            this._switchLock = true;
            const url = apiClient.getUrl('plugins/profiles/switch');

            fetch(url, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    ...this.getAuthHeaders(masterState.masterToken)
                },
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
                        throw new Error(body || 'Incorrect PIN. Please try again.');
                    });
                }
                return res.json();
            })
            .then(data => {
                const activeProfileToken = data.activeProfileToken || data.ActiveProfileToken;
                const jellyfinUserId = data.jellyfinUserId || data.JellyfinUserId;

                if (this.normalizeGuid(jellyfinUserId) === this.normalizeGuid(masterState.masterUserId)) {
                    masterState.masterToken = activeProfileToken;
                    localStorage.setItem(this.config.masterStorageKey, JSON.stringify(masterState));
                }

                this._sessionSet(this.config.activeSessionKey, activeProfileToken);
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
                        profileImage: profile.profileImage || null
                    }));
                }

                this.updateStoredCredentials(activeProfileToken, jellyfinUserId);
                apiClient.setAuthenticationInfo(activeProfileToken, jellyfinUserId);

                // Keep the overlay visible through the reload — removing it first
                // would expose the home screen for a frame before opacity:0 kicks in.
                // The reload will naturally destroy the overlay on the new page.
                // Transitioning it to solid black blends with the new page's dark state.
                const overlay = document.getElementById('profiles-gate-overlay');
                if (overlay) {
                    overlay.style.transition = 'background 0.12s ease';
                    overlay.style.background = '#101010';
                }
                // Hide everything else instantly.
                document.documentElement.style.cssText = 'opacity:0;background:#101010;color-scheme:dark';
                localStorage.setItem(this.config.switchingKey, '1');
                window.location.reload();
            })
            .catch(err => {
                this._switchLock = false;
                if (err.message === 'Session expired') return;
                if (typeof onError === 'function') {
                    // Caller has closed-over references to the DOM — no re-query needed
                    onError(err.message || 'Incorrect PIN. Please try again.');
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

        /// Formats refused before we even try to decode, each with a reason worth showing.
        /// Everything else is handed to the browser: if it decodes, we can store it.
        _rejectImageFile: function (file) {
            const name = (file.name || '').toLowerCase();
            const type = (file.type || '').toLowerCase();

            // The common case by a distance: iPhones shoot HEIC by default, and only Safari
            // can decode it. Elsewhere the canvas load simply fails, so without this the
            // user gets silence and no idea why.
            if (type.includes('heic') || type.includes('heif') || /\.hei[cf]$/.test(name)) {
                return 'HEIC photos aren\'t supported. Export the photo as JPEG first.';
            }

            // SVG can carry script, and these files are served back from the server's own
            // origin. Not worth it for an avatar.
            if (type.includes('svg') || /\.svgz?$/.test(name)) {
                return 'SVG images aren\'t supported. Use a JPEG, PNG, WebP or GIF.';
            }

            if (file.size > 25 * 1024 * 1024) {
                return 'That image is over 25 MB. Use a smaller one.';
            }

            return null;
        },

        /// Reads a File into a decoded <img>. Rejects with a message fit to show the user.
        loadImageFromFile: function (file) {
            return new Promise((resolve, reject) => {
                const reason = this._rejectImageFile(file);
                if (reason) { reject(new Error(reason)); return; }

                const reader = new FileReader();
                reader.onerror = () => reject(new Error('That file could not be read.'));
                reader.onload = (event) => {
                    const img = new Image();
                    img.onload = () => {
                        if (!img.width || !img.height) {
                            reject(new Error('That image appears to be empty.'));
                            return;
                        }
                        resolve(img);
                    };
                    // Reached for any format this browser cannot decode — including a HEIC
                    // that slipped past the check above with an empty MIME type.
                    img.onerror = () => reject(new Error(
                        'That image format isn\'t supported. Save it as a JPEG or PNG first.'));
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
                img.onerror = () => reject(new Error('That image could not be loaded.'));
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

        /// Renders the cropped square at `outSize` and returns it as a data URL.
        /// PNG is used when the source has transparency — re-encoding a cut-out avatar as
        /// JPEG would fill the transparent area with black.
        renderCrop: function (img, viewport, crop, outSize, preferPng) {
            const canvas = document.createElement('canvas');
            canvas.width = outSize;
            canvas.height = outSize;
            const ctx = canvas.getContext('2d');
            const k = outSize / viewport;
            ctx.drawImage(img, crop.x * k, crop.y * k, img.width * crop.zoom * k, img.height * crop.zoom * k);
            return preferPng ? canvas.toDataURL('image/png') : canvas.toDataURL('image/jpeg', 0.85);
        },

        /// True when any pixel is not fully opaque. Sampled at low resolution — this only
        /// decides an output format, so an exact answer is not worth the work.
        _hasTransparency: function (img) {
            try {
                const s = 32;
                const canvas = document.createElement('canvas');
                canvas.width = s; canvas.height = s;
                const ctx = canvas.getContext('2d');
                ctx.drawImage(img, 0, 0, s, s);
                const data = ctx.getImageData(0, 0, s, s).data;
                for (let i = 3; i < data.length; i += 4) {
                    if (data[i] < 250) return true;
                }
            } catch (e) { /* tainted canvas or no context — assume opaque */ }
            return false;
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
            dialog.style.cssText = `
                position: fixed; top: 0; left: 0; right: 0; bottom: 0;
                background: rgba(0,0,0,0.85); backdrop-filter: blur(8px);
                display: flex; align-items: center; justify-content: center;
                z-index: ${DIALOG_Z};
            `;
            dialog.innerHTML = `
                <div style="background:#181818; border:1px solid rgba(255,255,255,0.1); border-radius: var(--jpf-r-md);
                            padding:22px; max-width:340px; width:92%; box-shadow:0 10px 30px rgba(0,0,0,0.5); text-align:center;
                            user-select:none; -webkit-user-select:none;">
                    <h2 style="margin:0 0 4px 0; color:#fff; font-size:1.15rem; font-weight:700;">Position your picture</h2>
                    <p style="color:rgba(255,255,255,0.55); font-size:0.78rem; margin:0 0 14px 0;">
                        Drag or arrows to move, slider to zoom. Press OK when it looks right.
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
                           style="width:100%; margin:16px 0 4px 0;" aria-label="Zoom" />
                    <div style="display:flex; gap: var(--jpf-gap); justify-content:center; margin-top:12px;">
                        <button id="profiles-crop-cancel" class="profiles-btn btn-secondary" style="padding:10px 20px; font-weight:600;">Cancel</button>
                        <button id="profiles-crop-save" class="profiles-btn btn-primary" style="padding:10px 20px; font-weight:600;">Use picture</button>
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
                const image = this.renderCrop(img, VIEW, crop, this.IMAGE_MASTER_SIZE, preferPng);
                const thumb = this.renderCrop(img, VIEW, crop, this.IMAGE_THUMB_SIZE, preferPng);
                close();
                onDone({ image: image, thumb: thumb });
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
        renderAvatarPicker: function (prefix, library, currentImage, currentColor) {
            const hasLibrary = library.avatars.length > 0;
            const preview = currentImage ? avatarInner(currentImage, '+', /* useThumb */ true) : '+';

            const libraryHtml = hasLibrary ? `
                <div class="form-group" style="margin: 0;">
                    <div class="form-hint" style="margin: 0 0 6px 0;">Choose one of your server's avatars</div>
                    <div id="${prefix}-avatar-library" class="avatar-library-grid">
                        ${library.avatars.map(a => `
                            <button type="button" class="avatar-library-item" tabindex="0"
                                    data-id="${escapeHtml(a.id)}" data-url="${escapeHtml(a.url)}"
                                    title="${escapeHtml(a.displayName)}"
                                    aria-label="${escapeHtml(a.displayName || 'Avatar')}">
                                <img src="${safeImageSrc(a.thumbUrl)}" alt="" loading="lazy" />
                            </button>
                        `).join('')}
                    </div>
                </div>
            ` : '';

            // The upload control disappears entirely when the administrator has locked
            // avatars to the library — a disabled button people cannot use is just noise.
            const uploadHtml = library.allowCustomUploads ? `
                <div style="display: flex; flex-direction: column; gap: 8px; min-width: 0; width: 100%;">
                    <label for="${prefix}-profile-image-file" id="${prefix}-profile-image-label" class="profiles-btn btn-secondary image-upload-btn" tabindex="0">
                        <span class="material-icons" style="font-size: 1.25rem;">photo_camera</span>
                        <span>Upload a picture</span>
                    </label>
                    <input type="file" id="${prefix}-profile-image-file" accept="image/jpeg,image/png,image/webp,image/gif,image/avif,image/bmp" style="display: none;" />
                    <div class="form-hint" style="margin: 0;">JPEG, PNG, WebP or GIF. You can position and zoom it after choosing.</div>
                </div>
            ` : `
                <div class="form-hint" style="margin: 0; min-width: 0;">
                    Your server administrator has limited profile pictures to the set above.
                </div>
            `;

            const urlHtml = library.allowCustomUploads ? `
                <div class="form-divider"><span>OR</span></div>
                <div class="form-group" style="margin: 0;">
                    <input type="text" id="${prefix}-profile-image-url" placeholder="Paste image URL" />
                    <div class="form-hint" style="margin: 4px 0 0 0;">Linked directly, not stored on your server — and not croppable.</div>
                </div>
            ` : '';

            // Setting one picture used to mean six controls on screen at once: the
            // library grid, the preview, an Upload button, an OR divider, a URL field and
            // Remove. The preview and one action stay; the ways of choosing collapse
            // behind them. Open by default when there is no picture yet, because on a new
            // profile choosing one IS the task — it is on an existing profile that the
            // whole apparatus was sitting there for nothing.
            const sourcesOpen = !currentImage;

            return `
                <div class="form-group">
                    <label>Profile Picture</label>
                    <div class="profile-image-upload-container" style="display: flex; flex-direction: column; gap: var(--jpf-gap);">
                        <div class="image-upload-row">
                            <div id="${prefix}-image-upload-preview" class="image-upload-preview" style="background-color: ${safeColor(currentColor)};">${preview}</div>
                            <button type="button" id="${prefix}-change-picture" class="profiles-btn btn-secondary image-upload-btn"
                                    aria-expanded="${sourcesOpen}" aria-controls="${prefix}-picture-sources">
                                <span class="material-icons" style="font-size: 1.25rem;">photo_camera</span>
                                <span>${currentImage ? 'Change picture' : 'Choose a picture'}</span>
                            </button>
                        </div>
                        <div id="${prefix}-picture-sources" class="picture-sources${sourcesOpen ? ' is-open' : ''}">
                            ${libraryHtml}
                            ${uploadHtml}
                            ${urlHtml}
                        </div>
                        <div id="${prefix}-image-error" style="display:none; color:#ff6b6b; font-size:0.82rem; font-weight:600; line-height:1.45;"></div>
                    </div>
                </div>
            `;
        },

        /// Wires the picker up. Returns an accessor for the chosen image so the caller can
        /// read it at save time without tracking the state itself.
        initAvatarPicker: function (container, prefix, library, initialImage, onPreviewChange) {
            // libraryId is set instead of image/thumb when the picture comes from the
            // library on a locked-down server: the server copies the file itself, which is
            // the only form of the choice it can actually verify.
            const state = { image: initialImage || null, thumb: null, libraryId: null };

            const previewEl = container.querySelector(`#${prefix}-image-upload-preview`);
            const errEl = container.querySelector(`#${prefix}-image-error`);
            const fileInput = container.querySelector(`#${prefix}-profile-image-file`);
            const fileLabel = container.querySelector(`#${prefix}-profile-image-label`);
            const urlInput = container.querySelector(`#${prefix}-profile-image-url`);

            const showError = (message) => {
                if (!errEl) return;
                errEl.textContent = message;
                errEl.style.display = message ? 'block' : 'none';
            };

            const setPreview = (src) => {
                if (!previewEl) return;
                previewEl.style.position = 'relative';
                previewEl.innerHTML = src ? avatarInner(src, '+', /* useThumb */ true) : '+';
                if (typeof onPreviewChange === 'function') onPreviewChange(src);
            };
            setPreview(state.image);

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
                showError('');
                if (urlInput) urlInput.value = '';
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

            if (urlInput) {
                urlInput.addEventListener('input', () => {
                    const url = urlInput.value.trim();
                    // A remote URL is stored as a link, so there is nothing to crop and no
                    // thumbnail to generate — the browser scales it on render instead.
                    state.image = url || null;
                    state.thumb = null;
                    state.libraryId = null;
                    setPreview(url || null);
                    if (fileInput) fileInput.value = '';
                });
            }

            return {
                get: () => ({ image: state.image, thumb: state.thumb, libraryId: state.libraryId }),
                clear: () => {
                    // Empty string, not null: the server reads that as "delete the picture",
                    // whereas null means "leave it alone".
                    state.image = '';
                    state.thumb = null;
                    state.libraryId = null;
                    setPreview(null);
                    if (urlInput) urlInput.value = '';
                    if (fileInput) fileInput.value = '';
                },
                setError: showError
            };
        },

        showAddProfileModal: function () {
            const apiClient = ApiClient;
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            if (!masterState) return;

            const ticket = this.beginNavigation();

            // Everything this form needs is account-wide, so it is usually already
            // cached from the prefetch that ran when the overlay opened.
            this.fetchSharedFormData(apiClient, masterState)
            .then(shared => [shared.libraries, shared.devices, shared.libraryTags, shared.avatarLibrary])
            .then(([libraries, devices, libraryTags, avatarLibrary]) => {
                if (!this.navIsCurrent(ticket)) return;
                const normalizedLibs = (libraries || []).map(lib => ({
                    id: lib.id || lib.Id,
                    name: lib.name || lib.Name,
                    collectionType: lib.collectionType || lib.CollectionType
                }));
                // Re-rendering the modal orphans any listeners the previous form owned.
                this.clearManagedDocumentListeners();
                const content = document.querySelector('.profiles-modal-content');
                // ── Section 1: who this profile is ──────────────────────────────
                const createAppearance = `
                    <div class="form-group">
                        <label for="create-name-input">Profile Name</label>
                        <input type="text" id="create-name-input" placeholder="e.g. Kids" required />
                    </div>
                    <div class="form-group avatar-color-group" id="create-color-group">
                        <label>Avatar Color</label>
                        ${this.renderColorPicker('#00A4DC')}
                        <div class="form-hint" data-role="color-hint">Used as the avatar background when no picture is set.</div>
                    </div>
                    ${this.renderAvatarPicker('create', avatarLibrary, null, '#00A4DC')}
                `;

                // ── Section 2: getting into this profile ────────────────────────
                const createSecurity = `
                    <div class="form-group">
                        <label for="create-pin-input">PIN</label>
                        <input type="text" id="create-pin-input" maxlength="8" pattern="[0-9]*" inputmode="numeric" placeholder="Leave empty for no PIN" autocomplete="one-time-code" data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true" />
                    </div>
                    <div class="form-group">
                        <label class="library-check-label" style="display: inline-flex; align-items: center; gap: 0.5rem; cursor: pointer; user-select: none;">
                            <input type="checkbox" id="create-local-bypass-checkbox" style="cursor: pointer; accent-color: var(--jpf-accent);" />
                            <span>Bypass PIN on local network (LAN)</span>
                        </label>
                        <div class="form-hint">No PIN prompt on your home network.</div>
                    </div>
                    <div class="form-group">
                        <label for="create-lockout-select">Auto-lock after inactivity</label>
                        <select id="create-lockout-select">
                            <option value="0">Never</option>
                            <option value="1">1 minute</option>
                            <option value="5" selected>5 minutes (default)</option>
                            <option value="10">10 minutes</option>
                            <option value="20">20 minutes</option>
                            <option value="30">30 minutes</option>
                            <option value="60">1 hour</option>
                        </select>
                        <div class="form-hint">Only applies when this profile has a PIN set.</div>
                    </div>
                `;

                // ── Section 3: what this profile can browse ─────────────────────
                const createLibraries = `
                    <div class="form-group">
                        <div class="section-inline-header">
                            <label style="margin: 0;">Enabled Libraries</label>
                            <label class="library-check-label" style="font-size: 0.85rem; color: rgba(255,255,255,0.6); margin: 0; display: inline-flex; align-items: center; gap: 0.4rem;">
                                <input type="checkbox" id="create-select-all-libraries" style="margin: 0; cursor: pointer; accent-color: var(--jpf-accent);" />
                                <span>Select all</span>
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
                        <div class="form-hint">If nothing is selected, this profile inherits every library your account can see.</div>
                    </div>
                `;

                // ── Section 4: limits applied on top of the libraries above ─────
                const createRestrictions = `
                    <div class="form-group">
                        <label>Allowed Devices</label>
                        <div class="devices-dropdown-container" style="position: relative;">
                            <div id="create-devices-dropdown-trigger" class="devices-dropdown-trigger" tabindex="0" role="button" aria-expanded="false">
                                <span id="create-devices-dropdown-selected-text">All Devices Allowed</span>
                            </div>
                            <div id="create-devices-dropdown-list" class="devices-dropdown-list" style="display: none;">
                                ${devices && devices.length > 0 ? devices.map(dev => {
                                    const deviceId = dev.deviceId || dev.DeviceId || '';
                                    const deviceName = dev.deviceName || dev.DeviceName || 'Unknown Device';
                                    const client = dev.client || dev.Client || 'Unknown Client';
                                    const lastSeen = dev.lastSeen || dev.LastSeen;
                                    const lastSeenDate = lastSeen ? new Date(lastSeen) : null;
                                    const lastSeenStr = (lastSeenDate && lastSeenDate.getFullYear() > 1)
                                        ? lastSeenDate.toLocaleDateString() : 'Unknown';
                                    return `
                                        <div class="device-dropdown-item">
                                            <label style="display: flex; align-items: center; gap: 8px; cursor: pointer; flex: 1; margin: 0; font-size: 0.9rem; min-width: 0;">
                                                <input type="checkbox" class="create-device-checkbox" value="${escapeHtml(deviceId)}" style="cursor: pointer; accent-color: var(--jpf-accent); flex-shrink: 0;" />
                                                <span style="display: flex; flex-direction: column; min-width: 0;">
                                                    <span style="font-weight: 500; overflow: hidden; text-overflow: ellipsis;">${escapeHtml(deviceName)}</span>
                                                    <span style="font-size: 0.75rem; opacity: 0.6;">${escapeHtml(client)} • Last seen ${lastSeenStr}</span>
                                                </span>
                                            </label>
                                        </div>
                                    `;
                                }).join('') : `
                                    <div style="padding: 12px; text-align: center; opacity: 0.6; font-size: 0.9rem;">No devices found for your account yet</div>
                                `}
                            </div>
                        </div>
                        <div class="form-hint">If no devices are selected, this profile can be accessed from any device.</div>
                    </div>

                    <div class="form-group">
                        <label for="create-rating-select">Maximum rating</label>
                        <select id="create-rating-select">
                            <option value="">No Restrictions</option>
                            <option value="6">G / TV-G (6+)</option>
                            <option value="10">PG / TV-PG (10+)</option>
                            <option value="14">PG-13 / TV-14 (14+)</option>
                            <option value="17">R / TV-MA (17+)</option>
                        </select>
                    </div>

                    ${this.renderTagSuggestions('create-tag-suggestions', libraryTags)}
                    <div class="form-group">
                        <label>Blocked tags</label>
                        ${this.renderTagEditor('create-blocked-tags', [], 'e.g. adults', 'create-tag-suggestions')}
                        <div class="form-hint">Hides anything with these tags. A tag on a series or library covers everything inside it.</div>
                    </div>
                    <div class="form-group">
                        <label>Allowed tags</label>
                        ${this.renderTagEditor('create-allowed-tags', [], 'e.g. kids', 'create-tag-suggestions')}
                        <div class="form-hint form-hint-warn">⚠️ Allow-list: if you add any tag here, this profile sees <strong>only</strong> matching items. Untagged content is hidden too.</div>
                    </div>
                `;

                content.innerHTML = `
                    <h1 class="profiles-title">Create Profile</h1>
                    <div class="create-profile-container is-two-col">
                        <div class="form-col">
                            ${this.renderSection('person', 'Profile', 'Name, colour, and picture', createAppearance)}
                            ${this.renderSection('lock', 'Security', 'PIN protection and automatic locking', createSecurity)}
                        </div>
                        <div class="form-col">
                            ${this.renderSection('video_library', 'Libraries', 'Which libraries this profile can browse', createLibraries)}
                            ${this.renderSection('shield', 'Content & Device Restrictions', 'Limits applied on top of the libraries above', createRestrictions)}
                        </div>

                        <div id="create-error-msg" class="form-error" style="display:none;"></div>
                        <div class="pin-actions">
                            <button id="create-submit-btn" class="profiles-btn btn-primary">Create</button>
                            <button id="create-cancel-btn" class="profiles-btn btn-secondary">Cancel</button>
                        </div>
                    </div>
                `;

                // Color selector interaction
                const dots = content.querySelectorAll('.color-dot');
                let selectedColor = '#00A4DC';
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

                const avatarPicker = this.initAvatarPicker(
                    content, 'create', avatarLibrary, null,
                    (src) => this.setColorGroupInert('create', !!src));

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
                            txt.textContent = 'All Devices Allowed';
                        } else if (checked.length === 1) {
                            txt.textContent = '1 Device Allowed';
                        } else {
                            txt.textContent = `${checked.length} Devices Allowed`;
                        }
                    }
                };
                content.querySelectorAll('.create-device-checkbox').forEach(cb => {
                    cb.addEventListener('change', updateCreateSelectedText);
                });
                updateCreateSelectedText();

                document.getElementById('create-submit-btn').addEventListener('click', () => {
                    const name = document.getElementById('create-name-input').value.trim();
                    const pin = document.getElementById('create-pin-input').value;
                    const rating = document.getElementById('create-rating-select').value;
                    const lockoutMinutes = parseInt(document.getElementById('create-lockout-select').value, 10);
                    const bypassPin = document.getElementById('create-local-bypass-checkbox').checked;
                    
                    const checkedLibs = [];
                    content.querySelectorAll('.library-checkbox:checked').forEach(cb => {
                        checkedLibs.push(cb.value);
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
                        showCreateError('Profile name is required.');
                        return;
                    }

                    if (pin && (pin.length < 4 || pin.length > 8 || !/^\d+$/.test(pin))) {
                        showCreateError('PIN must be 4–8 digits.');
                        return;
                    }

                    const createUrl = apiClient.getUrl('plugins/profiles/create');
                    fetch(createUrl, {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json',
                            ...this.getAuthHeaders(masterState.masterToken)
                        },
                        body: JSON.stringify({
                            profileName: name,
                            pin: pin,
                            avatarColor: selectedColor,
                            maxParentalRating: rating || null,
                            // Send null (not empty array) when no libraries are checked.
                            // An empty array tells the server "allow no libraries",
                            // while null means "inherit all accessible libraries from master".
                            enabledFolders: checkedLibs.length > 0 ? checkedLibs : null,
                            blockedTags: this.getTagEditorValues(content, 'create-blocked-tags'),
                            allowedTags: this.getTagEditorValues(content, 'create-allowed-tags'),
                            masterPin: this.masterPin,
                            lockoutMinutes: lockoutMinutes,
                            bypassPinOnLocalNetwork: bypassPin,
                            allowedDeviceIds: checkedDevices,
                            profileImage: avatarPicker.get().image,
                            profileImageThumb: avatarPicker.get().thumb,
                            avatarLibraryId: avatarPicker.get().libraryId
                        })
                    })
                    .then(res => {
                        if (!res.ok) return res.text().then(text => { throw new Error(text); });
                        return res.json();
                    })
                    .then(() => {
                        this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.masterToken, /* forceRefresh */ true);
                    })
                    .catch(err => {
                        const el = document.getElementById('create-error-msg');
                        if (el) { el.textContent = err.message; el.style.display = 'block'; }
                    });
                });

                document.getElementById('create-cancel-btn').addEventListener('click', () => {
                    this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.masterToken);
                });
                this.initTVCheckboxes(content);
                this.initTagEditors(content);
            });
        },

        showEditProfileModal: function (profile) {
            const apiClient = ApiClient;
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            if (!masterState) return;

            const ticket = this.beginNavigation();

            // Users/{id} is the only one of the five that is about the profile you
            // clicked. The rest come from the account-wide cache.
            const userUrl = apiClient.getUrl(`Users/${profile.profileUserId}`);

            Promise.all([
                this.fetchSharedFormData(apiClient, masterState),
                fetch(userUrl, { headers: this.getAuthHeaders(masterState.masterToken) }).then(res => res.json())
            ])
            .then(([shared, userDetails]) => [shared.libraries, userDetails, shared.devices, shared.libraryTags, shared.avatarLibrary])
            .then(([libraries, userDetails, devices, libraryTags, avatarLibrary]) => {
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

                // ── Section 1: who this profile is ──────────────────────────────
                const appearanceBody = `
                    <div class="form-group">
                        <label for="edit-name-input">Profile Name</label>
                        <input type="text" id="edit-name-input" value="${escapeHtml(profile.profileName)}" ${profile.isMaster ? 'disabled style="opacity: 0.6"' : ''} required />
                        ${profile.isMaster ? `<div class="form-hint">The master profile takes its name from your Jellyfin account.</div>` : ''}
                    </div>
                    <div class="form-group avatar-color-group" id="edit-color-group">
                        <label>Avatar Color</label>
                        ${this.renderColorPicker(profile.avatarColor)}
                        <div class="form-hint" data-role="color-hint">Used as the avatar background when no picture is set.</div>
                    </div>
                    ${this.renderAvatarPicker('edit', avatarLibrary, profile.profileImage, profile.avatarColor)}
                    ${profile.profileImage ? `
                        <button type="button" id="edit-clear-profile-image-btn" class="profiles-btn btn-secondary" style="padding: 6px 12px; font-size: 0.8rem; align-self: flex-start; margin-top: -6px;">Remove Picture</button>
                    ` : ''}
                `;

                // ── Section 2: getting into this profile ────────────────────────
                const securityBody = `
                    <div class="form-group">
                        <label for="edit-pin-input">PIN</label>
                        <div class="pin-edit-group" style="display:flex; gap: var(--jpf-gap); flex-wrap: wrap;">
                            <input type="text" id="edit-pin-input" maxlength="8" pattern="[0-9]*" inputmode="numeric" placeholder="${profile.hasPin ? 'New PIN' : 'No PIN'}" autocomplete="one-time-code" data-1p-ignore data-lpignore="true" data-bwignore data-protonpass-ignore="true" style="flex:1; min-width: 160px;" />
                            ${profile.hasPin ? `<button id="edit-clear-pin-btn" class="profiles-btn btn-secondary" style="padding:10px 15px;">Clear PIN</button>` : ''}
                        </div>
                        <div id="edit-pin-error" class="form-error" style="display:none; margin-top:8px;"></div>
                        <div class="form-hint">
                            ${profile.hasPin
                                ? '🔒 <strong>A PIN is set.</strong> Leave blank to keep it, type a new one to replace it, or use Clear PIN.'
                                : 'No PIN set. This profile can be opened by anyone who can reach the switcher.'}
                        </div>
                    </div>
                    <div class="form-group">
                        <label class="library-check-label" style="display: inline-flex; align-items: center; gap: 0.5rem; cursor: pointer; user-select: none;">
                            <input type="checkbox" id="edit-local-bypass-checkbox" ${profile.bypassPinOnLocalNetwork ? 'checked' : ''} style="cursor: pointer; accent-color: var(--jpf-accent);" />
                            <span>Bypass PIN on local network (LAN)</span>
                        </label>
                        <div class="form-hint">No PIN prompt on your home network.</div>
                    </div>
                    <div class="form-group">
                        <label for="edit-lockout-select">Auto-lock after inactivity</label>
                        <select id="edit-lockout-select">
                            <option value="0" ${currentLockout === 0 ? 'selected' : ''}>Never</option>
                            <option value="1" ${currentLockout === 1 ? 'selected' : ''}>1 minute</option>
                            <option value="5" ${currentLockout === 5 ? 'selected' : ''}>5 minutes</option>
                            <option value="10" ${currentLockout === 10 ? 'selected' : ''}>10 minutes</option>
                            <option value="20" ${currentLockout === 20 ? 'selected' : ''}>20 minutes</option>
                            <option value="30" ${currentLockout === 30 ? 'selected' : ''}>30 minutes</option>
                            <option value="60" ${currentLockout === 60 ? 'selected' : ''}>1 hour</option>
                        </select>
                        <div class="form-hint">Only applies when a PIN is set on this profile.</div>
                    </div>
                `;

                // ── Section 3: what this profile can browse ─────────────────────
                const librariesBody = `
                    <div class="form-group">
                        <div class="section-inline-header">
                            <label style="margin: 0;">Libraries</label>
                            <label class="library-check-label" style="font-size: 0.85rem; color: rgba(255,255,255,0.6); margin: 0; display: inline-flex; align-items: center; gap: 0.4rem;">
                                <input type="checkbox" id="edit-select-all-libraries" style="margin: 0; cursor: pointer; accent-color: var(--jpf-accent);" />
                                <span>Select all</span>
                            </label>
                        </div>
                        <div class="libart-list" id="edit-library-artwork">
                            ${normalizedLibs.map(lib => {
                                const storedFolders = profile.enabledFolders;
                                let isChecked;
                                if (storedFolders !== null && storedFolders !== undefined) {
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
                                        <select class="libart-mode" aria-label="Artwork for ${escapeHtml(lib.name)}">
                                            <option value="inherit">Default</option>
                                            <option value="custom">Picture</option>
                                            <option value="none">Hidden</option>
                                        </select>
                                        <button type="button" class="profiles-btn btn-secondary libart-choose" style="padding:6px 12px; font-size:0.8rem;">Choose</button>
                                    </div>
                                `;
                            }).join('')}
                        </div>
                        <div class="form-hint">If nothing is ticked, this profile inherits every library your account can see. Artwork applies to the tile Jellyfin builds from the items inside a library, which can show something this profile cannot open — Hidden shows just the icon and name.</div>
                    </div>
                `;

                // ── Section 4: limits applied on top of the libraries above ─────
                const restrictionsBody = `
                    <div class="form-group">
                        <label>Allowed Devices</label>
                        <div class="devices-dropdown-container" style="position: relative;">
                            <div id="devices-dropdown-trigger" class="devices-dropdown-trigger" tabindex="0" role="button" aria-expanded="false">
                                <span id="devices-dropdown-selected-text">All Devices Allowed</span>
                            </div>
                            <div id="devices-dropdown-list" class="devices-dropdown-list" style="display: none;">
                                ${devices && devices.length > 0 ? devices.map(dev => {
                                    const deviceId = dev.deviceId || dev.DeviceId || '';
                                    const deviceName = dev.deviceName || dev.DeviceName || 'Unknown Device';
                                    const client = dev.client || dev.Client || 'Unknown Client';
                                    const lastSeen = dev.lastSeen || dev.LastSeen;
                                    const lastSeenDate = lastSeen ? new Date(lastSeen) : null;
                                    const lastSeenStr = (lastSeenDate && lastSeenDate.getFullYear() > 1)
                                        ? lastSeenDate.toLocaleDateString() : 'Unknown';
                                    const isChecked = profile.allowedDeviceIds && (profile.allowedDeviceIds.includes(deviceId) || (dev.DeviceId && profile.allowedDeviceIds.includes(dev.DeviceId)));
                                    return `
                                        <div class="device-dropdown-item">
                                            <label style="display: flex; align-items: center; gap: 8px; cursor: pointer; flex: 1; margin: 0; font-size: 0.9rem; min-width: 0;">
                                                <input type="checkbox" class="device-checkbox" value="${escapeHtml(deviceId)}" ${isChecked ? 'checked' : ''} style="cursor: pointer; accent-color: var(--jpf-accent); flex-shrink: 0;" />
                                                <span style="display: flex; flex-direction: column; min-width: 0;">
                                                    <span style="font-weight: 500; overflow: hidden; text-overflow: ellipsis;">${escapeHtml(deviceName)}</span>
                                                    <span style="font-size: 0.75rem; opacity: 0.6;">${escapeHtml(client)} • Last seen ${lastSeenStr}</span>
                                                </span>
                                            </label>
                                            <button type="button" class="device-delete-btn" data-id="${escapeHtml(deviceId)}" title="Forget this device" aria-label="Forget ${escapeHtml(deviceName)}">🗑️</button>
                                        </div>
                                    `;
                                }).join('') : `
                                    <div style="padding: 12px; text-align: center; opacity: 0.6; font-size: 0.9rem;">No devices found for your account yet</div>
                                `}
                            </div>
                        </div>
                        <div class="form-hint">If no devices are selected, this profile can be accessed from any device.</div>
                    </div>

                    <div class="form-group">
                        <label for="edit-rating-select">Maximum rating</label>
                        <select id="edit-rating-select">
                            <option value="" ${maxRating === null ? 'selected' : ''}>No Restrictions</option>
                            <option value="6" ${maxRating === 6 ? 'selected' : ''}>G / TV-G (6+)</option>
                            <option value="10" ${maxRating === 10 ? 'selected' : ''}>PG / TV-PG (10+)</option>
                            <option value="14" ${maxRating === 14 ? 'selected' : ''}>PG-13 / TV-14 (14+)</option>
                            <option value="17" ${maxRating === 17 ? 'selected' : ''}>R / TV-MA (17+)</option>
                        </select>
                    </div>

                    ${this.renderTagSuggestions('edit-tag-suggestions', libraryTags)}
                    <div class="form-group">
                        <label>Blocked tags</label>
                        ${this.renderTagEditor('edit-blocked-tags', profile.blockedTags || [], 'e.g. adults', 'edit-tag-suggestions')}
                        <div class="form-hint">Hides anything with these tags. A tag on a series or library covers everything inside it.</div>
                    </div>
                    <div class="form-group">
                        <label>Allowed tags</label>
                        ${this.renderTagEditor('edit-allowed-tags', profile.allowedTags || [], 'e.g. kids', 'edit-tag-suggestions')}
                        <div class="form-hint form-hint-warn">⚠️ Allow-list: if you add any tag here, this profile sees <strong>only</strong> matching items. Untagged content is hidden too.</div>
                    </div>
                `;

                content.innerHTML = `
                    <h1 class="profiles-title">Edit Profile</h1>
                    <div class="create-profile-container${isSub ? ' is-two-col' : ''}">
                        <div class="form-col">
                            ${this.renderSection('person', 'Profile', 'Name, colour, and picture', appearanceBody)}
                            ${this.renderSection('lock', 'Security', 'PIN protection and automatic locking', securityBody)}
                        </div>
                        <div class="form-col">
                            ${isSub ? this.renderSection('video_library', 'Libraries', 'Which libraries this profile can browse', librariesBody) : ''}
                            ${isSub ? this.renderSection('shield', 'Content & Device Restrictions', 'Limits applied on top of the libraries above', restrictionsBody) : ''}
                        </div>

                        <div class="profile-dialog-actions">
                            <div class="dialog-action-buttons">
                                <button id="edit-submit-btn" class="profiles-btn btn-primary">Save</button>
                                <button id="edit-cancel-btn" class="profiles-btn btn-secondary">Cancel</button>
                            </div>
                            ${isSub ? `
                                <button id="edit-delete-btn" class="profiles-btn btn-danger-quiet">Delete Profile</button>
                            ` : ''}
                        </div>
                    </div>
                `;

                // Setup active color dot selection
                const dots = content.querySelectorAll('.color-dot');
                let selectedColor = profile.avatarColor || '#00A4DC';
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

                const clearImgBtn = document.getElementById('edit-clear-profile-image-btn');
                const avatarPicker = this.initAvatarPicker(
                    content, 'edit', avatarLibrary, profile.profileImage,
                    (src) => {
                        // The Remove button only makes sense while there is a picture.
                        if (clearImgBtn) clearImgBtn.style.display = src ? 'block' : 'none';
                        this.setColorGroupInert('edit', !!src);
                        const preview = document.getElementById('edit-image-upload-preview');
                        if (preview && !src) preview.innerHTML = escapeHtml(profile.avatarInitial);
                    });

                if (clearImgBtn) {
                    clearImgBtn.addEventListener('click', () => {
                        avatarPicker.clear();
                        clearImgBtn.style.display = 'none';
                    });
                }

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
                    if (txt) {
                        if (checked.length === 0) {
                            txt.textContent = 'All Devices Allowed';
                        } else if (checked.length === 1) {
                            txt.textContent = '1 Device Allowed';
                        } else {
                            txt.textContent = `${checked.length} Devices Allowed`;
                        }
                    }
                };
                content.querySelectorAll('.device-checkbox').forEach(cb => {
                    cb.addEventListener('change', updateSelectedText);
                });
                updateSelectedText();

                // Device deletion handler
                content.querySelectorAll('.device-delete-btn').forEach(btn => {
                    btn.addEventListener('click', (e) => {
                        e.preventDefault();
                        e.stopPropagation();
                        const devId = btn.getAttribute('data-id');
                        this.showConfirmDialog('Delete Device History', 'Remove this device? Any access restrictions for it go too.', () => {
                            const delDevUrl = apiClient.getUrl('plugins/profiles/devices/delete');
                            fetch(delDevUrl, {
                                method: 'POST',
                                headers: {
                                    'Content-Type': 'application/json',
                                    ...this.getAuthHeaders(masterState.masterToken)
                                },
                                body: JSON.stringify({ deviceId: devId })
                            })
                            .then(res => {
                                if (res.ok) {
                                    // The device list is part of the account-wide cache the
                                    // forms share. Removing only the row would let the next
                                    // form re-list the device it just deleted, and allow it
                                    // to be ticked as permitted.
                                    this.clearSharedFormData();

                                    const row = btn.closest('.device-dropdown-item');
                                    if (row) row.remove();
                                    const remaining = editList.querySelectorAll('.device-dropdown-item');
                                    if (remaining.length === 0) {
                                        editList.innerHTML = '<div style="padding: 12px; text-align: center; opacity: 0.6; font-size: 0.9rem;">No connected devices found</div>';
                                    }
                                    updateSelectedText();
                                } else {
                                    this.showAlert('Error', 'Failed to delete device.');
                                }
                            })
                            .catch(err => this.showAlert('Error', 'Error: ' + err.message));
                        });
                    });
                });

                // Clear PIN logic
                let isPinCleared = false;
                const clearPinBtn = document.getElementById('edit-clear-pin-btn');
                if (clearPinBtn) {
                    clearPinBtn.addEventListener('click', (e) => {
                        e.preventDefault();
                        isPinCleared = true;
                        document.getElementById('edit-pin-input').value = '';
                        document.getElementById('edit-pin-input').placeholder = 'Unprotected';
                        clearPinBtn.style.display = 'none';
                    });
                }



                // Save handler
                document.getElementById('edit-submit-btn').addEventListener('click', () => {
                    const name = document.getElementById('edit-name-input').value.trim();
                    const pinVal = document.getElementById('edit-pin-input').value;
                    const bypassPin = document.getElementById('edit-local-bypass-checkbox').checked;
                    
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
                            rawLibs.push(cb.value);
                        });
                        // Send null (not empty array) when no libraries are checked.
                        // An empty array tells the server "allow no libraries",
                        // while null means "inherit all accessible libraries from master".
                        checkedLibs = rawLibs.length > 0 ? rawLibs : null;
                        checkedDevices = [];
                        content.querySelectorAll('.device-checkbox:checked').forEach(cb => {
                            checkedDevices.push(cb.value);
                        });
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
                            input.style.borderColor = '#ff6b6b';
                            input.focus();
                        }
                    };
                    if (pinError) pinError.style.display = 'none';
                    const pinInputEl = document.getElementById('edit-pin-input');
                    if (pinInputEl) pinInputEl.style.borderColor = '';

                    if (!name) {
                        this.showAlert("Validation Error", "Profile name is required.");
                        return;
                    }

                    let pin = null;
                    if (isPinCleared) {
                        pin = ''; // Tells backend to clear the PIN
                    } else if (pinVal) {
                        if (!/^\d+$/.test(pinVal)) {
                            showPinError('A PIN can only contain digits.');
                            return;
                        }
                        if (pinVal.length < 4 || pinVal.length > 8) {
                            showPinError(`A PIN must be 4-8 digits — you entered ${pinVal.length}.`);
                            return;
                        }
                        pin = pinVal;
                    }

                    const updateUrl = apiClient.getUrl('plugins/profiles/update');
                    fetch(updateUrl, {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json',
                            ...this.getAuthHeaders(masterState.masterToken)
                        },
                        body: JSON.stringify({
                            profileId: profile.profileUserId,
                            profileName: name,
                            pin: pin,
                            avatarColor: selectedColor,
                            maxParentalRating: rating || null,
                            enabledFolders: checkedLibs,
                            blockedTags: blockedTags,
                            allowedTags: allowedTags,
                            masterPin: this.masterPin,
                            lockoutMinutes: lockoutMinutes,
                            bypassPinOnLocalNetwork: bypassPin,
                            allowedDeviceIds: checkedDevices,
                            profileImage: avatarPicker.get().image,
                            profileImageThumb: avatarPicker.get().thumb,
                            avatarLibraryId: avatarPicker.get().libraryId
                        })
                    })
                    .then(res => {
                        if (!res.ok) return res.text().then(text => { throw new Error(text); });
                        // Artwork is stored per library, so it is saved after the profile
                        // itself rather than folded into that one request.
                        return artworkEditor.save(profile.profileUserId).then(() => {
                            this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.masterToken, /* forceRefresh */ true);
                        });
                    })
                    .catch(err => this.showAlert("Error", "Error saving profile: " + err.message));
                });

                // Delete handler
                const delBtn = document.getElementById('edit-delete-btn');
                if (delBtn) {
                    delBtn.addEventListener('click', () => {
                        this.showConfirmDialog('Delete Profile', `Are you sure you want to delete profile "${escapeHtml(profile.profileName)}" and its underlying user account? This action is irreversible.`, () => {
                            this.executeProfileDeletion(profile.profileUserId);
                        });
                    });
                }
// Cancel handler
                document.getElementById('edit-cancel-btn').addEventListener('click', () => {
                    this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.masterToken);
                });
                const artworkEditor = this.initLibraryArtworkEditor(content, profile.profileUserId);
                this.initTVCheckboxes(content);
                this.initTagEditors(content);
            })
            .catch(err => {
                this.showAlert("Error", "Failed to load profile details: " + err.message);
                this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.masterToken);
            });
        },

        showBonfireModal: function () {
            // Kept, so a slow render cannot draw over a screen the user moved on to.
            const navTicket = this.beginNavigation();
            const apiClient = ApiClient;
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            if (!masterState) return;

            const content = document.querySelector('.profiles-modal-content');
            if (!content) return;

            content.innerHTML = `
                <h1 class="profiles-title">Your Bonfire</h1>
                <div class="create-profile-container" style="max-width: 500px; width: 100%;">
                    <div id="bonfire-container" style="width: 100%; min-height: 100px; display: flex; flex-direction: column; gap: 1.5rem;">
                        <div style="display: flex; justify-content: center; padding: 20px;">
                            <div class="profiles-loading-spinner" style="border: 3px solid rgba(255,255,255,0.1); border-radius: 50%; border-top: 3px solid var(--jpf-accent); width: 24px; height: 24px; animation: spin 1s linear infinite;"></div>
                        </div>
                        <div class="bonfire-dialog-actions" style="margin-top: 2rem !important; display: flex !important; justify-content: center !important; width: 100% !important; box-sizing: border-box !important; position: relative !important; bottom: auto !important; left: auto !important; right: auto !important; top: auto !important;">
                            <button id="bonfire-back-btn" class="profiles-btn btn-secondary" style="padding: 10px 24px !important; font-size: 1rem !important; box-sizing: border-box !important; margin: 0 !important; display: inline-block !important; width: auto !important; flex: 0 0 auto !important; position: relative !important; bottom: auto !important; left: auto !important; right: auto !important; top: auto !important;">Back</button>
                        </div>
                    </div>
                </div>
            `;

            const attachBackBtnListener = () => {
                const btn = content.querySelector('#bonfire-back-btn');
                if (btn) {
                    btn.addEventListener('click', () => {
                        this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.masterToken, /* forceRefresh */ true);
                    });
                }
            };

            attachBackBtnListener();

            this.loadBonfireStatus(content, apiClient, masterState.masterToken);

            // Auto-focus first focusable element for TV D-pad navigation
            setTimeout(() => {
                const first = content.querySelector('input, button');
                if (first) first.focus();
            }, 250);
        },

        /// Lets the account holder choose between the forced gate and native-menu access.
        /// A per-account preference rather than a server setting — some households want the
        /// Netflix-style "Who's Watching?" screen, others find it intrusive, and neither
        /// answer should be imposed on the other by whoever runs the server.
        /// Everything that is not a profile.
        ///
        /// Bonfire has four scopes of setting: per profile (Edit Profile), per account
        /// (Switcher Style), between accounts (Your Bonfire) and server-wide (the
        /// dashboard). The middle two used to be cards in the profile grid, which is
        /// why they needed their own gradient tiles to avoid reading as people.
        showSettingsMenu: function () {
            this.beginNavigation();

            const content = document.querySelector('.profiles-modal-content');
            if (!content) return;

            const entry = (id, icon, title, body) => `
                <div class="settings-menu-entry" id="${id}" tabindex="0" role="button" style="
                    display: flex; gap: var(--jpf-gap); text-align: left; padding: 16px;
                    border-radius: var(--jpf-r-md); cursor: pointer; box-sizing: border-box;
                    border: 2px solid rgba(255,255,255,0.08);
                    background: rgba(255,255,255,0.02);
                ">
                    <span class="material-icons" style="font-size: 2rem; color: rgba(255,255,255,0.5); flex-shrink: 0;">${icon}</span>
                    <div style="flex: 1 1 auto; min-width: 0;">
                        <div style="font-weight: 700; font-size: 1rem; margin-bottom: 4px;">${title}</div>
                        <div style="font-size: 0.85rem; opacity: 0.7; line-height: 1.5;">${body}</div>
                    </div>
                </div>
            `;

            content.innerHTML = `
                <h1 class="profiles-title">Settings</h1>
                <div class="create-profile-container" style="max-width: var(--jpf-w-form); width: 100%;">
                    <div style="display: flex; flex-direction: column; gap: var(--jpf-gap); width: 100%;">
                        ${entry('settings-switcher-style', 'switch_account', 'Switcher Style',
                            'Where you reach this screen from, and whether it opens on startup.')}
                        ${entry('settings-your-bonfire', 'local_fire_department', 'Your Bonfire',
                            'Share your profiles with another home, or join theirs.')}
                    </div>
                    <div class="bonfire-dialog-actions" style="margin-top: 2rem !important; display: flex !important; justify-content: center !important; width: 100% !important;">
                        <button id="settings-back-btn" class="profiles-btn btn-secondary" style="padding: 10px 24px !important; font-size: 1rem !important; margin: 0 !important; width: auto !important;">Back</button>
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
            open('settings-switcher-style', this.showSwitcherModeModal);
            open('settings-your-bonfire', this.showBonfireModal);

            const back = content.querySelector('#settings-back-btn');
            if (back) {
                back.addEventListener('click', () => {
                    const st = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
                    if (st) {
                        this.fetchAndRenderProfiles(ApiClient, st.masterUserId, st.masterToken, /* forceRefresh */ true);
                    }
                });
            }

        },

        showSwitcherModeModal: function () {
            this.beginNavigation();
            const apiClient = ApiClient;
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey) || 'null');
            if (!masterState) return;

            const content = document.querySelector('.profiles-modal-content');
            if (!content) return;

            const prefs = this.getSwitcherPrefs() || { askOnStartup: true, location: 'button' };

            // Two separate questions, deliberately not folded into one list of modes: the
            // combination people asked for in issue #14 — ask on startup, but switch from
            // Jellyfin's menu — is unreachable when it is a single choice.
            const locationOption = (value, icon, title, body) => `
                <div class="switcher-location-option" data-location="${value}" tabindex="0" role="radio"
                     aria-checked="${prefs.location === value}" style="
                    display: flex; gap: var(--jpf-gap); text-align: left; padding: 16px;
                    border-radius: var(--jpf-r-md); cursor: pointer; box-sizing: border-box;
                    border: 2px solid ${prefs.location === value ? 'var(--jpf-accent)' : 'rgba(255,255,255,0.08)'};
                    background: ${prefs.location === value ? 'var(--jpf-accent-a08)' : 'rgba(255,255,255,0.02)'};
                ">
                    <span class="material-icons" style="font-size: 2rem; color: ${prefs.location === value ? 'var(--jpf-accent)' : 'rgba(255,255,255,0.5)'}; flex-shrink: 0;">${icon}</span>
                    <div style="flex: 1 1 auto; min-width: 0;">
                        <div style="font-weight: 700; font-size: 1rem; margin-bottom: 4px;">${title}</div>
                        <div style="font-size: 0.85rem; opacity: 0.7; line-height: 1.5;">${body}</div>
                    </div>
                </div>
            `;

            content.innerHTML = `
                <h1 class="profiles-title">Switcher Style</h1>
                <div class="create-profile-container" style="max-width: 560px; width: 100%;">
                    <p style="opacity: 0.75; font-size: 0.9rem; line-height: 1.5; margin: 0 0 1.5rem 0; text-align: left;">
                        How you reach this screen. Applies to your account on every device.
                    </p>

                    <div class="bonfire-form-group" style="gap: 4px; text-align: left; margin-bottom: 1.5rem;">
                        <label class="library-check-label" style="display: inline-flex !important; align-items: center !important; gap: 0.5rem !important; cursor: pointer !important; user-select: none !important; font-size: 0.95rem !important; font-weight: 600 !important; position: relative !important;">
                            <input type="checkbox" id="switcher-ask-startup" ${prefs.askOnStartup ? 'checked' : ''} style="cursor: pointer !important; accent-color: var(--jpf-accent) !important; position: relative !important; opacity: 1 !important; width: 18px !important; height: 18px !important; margin: 0 !important; padding: 0 !important; flex-shrink: 0 !important;" />
                            <span>Ask "Who's watching?" on startup</span>
                        </label>
                        <div class="form-hint" style="margin-left: 1.6rem !important; opacity: 0.5 !important; font-size: 0.78rem !important; position: relative !important; display: block !important;">
                            Shown once when the app opens — not every time you return to the home screen.
                        </div>
                    </div>

                    <div style="text-align: left; font-size: 0.95rem; font-weight: 600; margin-bottom: 10px;">Where to switch from</div>
                    <div role="radiogroup" style="display: flex; flex-direction: column; gap: var(--jpf-gap); width: 100%;">
                        ${locationOption('button', 'account_circle', 'Bonfire button',
                            'A separate switcher button in the header, next to Jellyfin\'s own profile icon.')}
                        ${locationOption('menu', 'switch_account', 'Jellyfin\'s user menu',
                            'Adds "Switch Profile" above Sign out in Jellyfin\'s own menu, and to your profile page. Removes the second header icon.')}
                    </div>

                    <div id="switcher-mode-error" style="display: none; color: #ff6b6b; font-size: 0.85rem; font-weight: 600; margin-top: 12px;"></div>
                    <div class="bonfire-dialog-actions" style="margin-top: 2rem !important; display: flex !important; justify-content: center !important; width: 100% !important;">
                        <button id="switcher-mode-back-btn" class="profiles-btn btn-secondary" style="padding: 10px 24px !important; font-size: 1rem !important; margin: 0 !important; width: auto !important;">Done</button>
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
                    headers: {
                        'Content-Type': 'application/json',
                        ...this.getAuthHeaders(masterState.masterToken)
                    },
                    body: JSON.stringify({ askOnStartup: askOnStartup, switcherLocation: location })
                })
                .then(res => res.ok ? res.json() : Promise.reject(new Error('Could not save that.')))
                .then(saved => {
                    const ask = (saved.askOnStartup !== undefined ? saved.askOnStartup : saved.AskOnStartup) === true;
                    const loc = (saved.switcherLocation || saved.SwitcherLocation) === 'menu' ? 'menu' : 'button';
                    this._cacheSwitcherPrefs(ask, loc, masterState.masterUserId);
                    return { askOnStartup: ask, location: loc };
                })
                .catch(err => {
                    errDiv.textContent = err.message || 'Could not save that.';
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

                    save(askCb ? askCb.checked : prefs.askOnStartup, location).then(applied => {
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
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            if (!masterState) return;

            const url = apiClient.getUrl('plugins/profiles/delete');

            fetch(url, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    ...this.getAuthHeaders(masterState.masterToken)
                },
                body: JSON.stringify({ 
                    profileId: profileId,
                    masterPin: this.masterPin
                })
            })
            .then(res => {
                if (!res.ok) throw new Error("Failed to delete profile");
                this.fetchAndRenderProfiles(apiClient, masterState.masterUserId, masterState.masterToken, /* forceRefresh */ true);
            })
            .catch(err => this.showAlert("Error", "Error deleting profile: " + err.message));
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
                    throw new Error('Unauthorized');
                }
                return res.json();
            })
            .then(status => {
                if (!this.navIsCurrent(ticket)) return;
                this.renderBonfireStatus(container, content, status, apiClient, masterToken);
            })
            .catch(err => {
                if (!this.navIsCurrent(ticket)) return;
                container.innerHTML = `<div style="color: #ff6b6b; font-size: 0.9rem;">Failed to load Bonfire status: ${err.message}</div>`;
            });
        },

        renderBonfireStatus: function (container, content, status, apiClient, masterToken) {
            const isOwner = status.isOwner !== undefined ? status.isOwner : status.IsOwner;
            const isMember = status.isMember !== undefined ? status.isMember : status.IsMember;
            const ownedCode = status.ownedCode || status.OwnedCode || '';
            const ownedMembers = status.ownedMembers || status.OwnedMembers || [];
            const joinedOwnerName = status.joinedOwnerName || status.JoinedOwnerName || '';

            let hostSectionHtml = '';
            if (isOwner) {
                hostSectionHtml = `
                    <div style="display: flex; flex-direction: column; gap: var(--jpf-gap-lg); border-bottom: 1px solid rgba(255,255,255,0.08); padding-bottom: 1.5rem;">
                        <div class="bonfire-form-group">
                            <label style="font-size: 1.1rem; font-weight: 700; display: block; margin-bottom: 4px;">Your Hosted Bonfire</label>
                            <span style="font-size: 0.88rem; opacity: 0.75; display: block;">Share this code to invite someone to your Bonfire:</span>
                            <div style="font-size: 2rem; font-weight: 700; letter-spacing: 4px; margin: 12px 0; font-family: monospace; text-align: center; background: rgba(0,0,0,0.3); padding: 12px; border-radius: var(--jpf-r-md); border: 1px solid var(--jpf-accent-a30);">${ownedCode}</div>
                        </div>
                        
                        <div class="bonfire-form-group">
                            <label style="font-size: 1rem; font-weight: 600; color: #fff; display: block; margin-bottom: 8px;">Members (${ownedMembers.length})</label>
                            <div style="display: flex; flex-direction: column; gap: 8px; max-height: 180px; overflow-y: auto; background: rgba(0,0,0,0.2); border: 1px solid rgba(255,255,255,0.06); border-radius: var(--jpf-r-md); padding: 8px;">
                                ${ownedMembers.length > 0 ? ownedMembers.map(m => {
                                    const mUserId = m.userId || m.UserId;
                                    const mUsername = m.username || m.Username || 'Unknown User';
                                    return `
                                    <div style="display: flex; align-items: center; justify-content: space-between; padding: 8px 12px; background: rgba(255,255,255,0.03); border-radius: var(--jpf-r-sm);">
                                        <span style="font-size: 0.95rem; font-weight: 500;">${mUsername}</span>
                                        <button type="button" class="bonfire-kick-btn" data-id="${mUserId}" style="background: #ff6b6b !important; border: none !important; color: #fff !important; padding: 6px 12px !important; border-radius: var(--jpf-r-sm) !important; font-size: 0.85rem !important; cursor: pointer !important; font-weight: 600 !important; transition: background-color 0.2s !important; margin: 0 !important; box-sizing: border-box !important;">Kick</button>
                                    </div>
                                    `;
                                }).join('') : '<div style="font-size: 0.9rem; opacity: 0.5; font-style: italic; text-align: center; padding: 12px;">No members joined yet.</div>'}
                            </div>
                        </div>
                        <div style="display: flex; justify-content: flex-end;">
                            <button type="button" id="bonfire-delete-btn" class="profiles-btn btn-danger-quiet" style="padding: 10px 20px !important; font-size: 0.95rem !important; box-sizing: border-box !important; margin: 0 !important; display: inline-block !important;">Delete Group</button>
                        </div>
                    </div>
                `;
            } else {
                hostSectionHtml = `
                    <div class="bonfire-form-group" style="border-bottom: 1px solid rgba(255,255,255,0.08); padding-bottom: 1.5rem;">
                        <label style="font-size: 1.1rem; font-weight: 700; display: block; margin-bottom: 4px;">Host a Bonfire</label>
                        <span style="font-size: 0.88rem; opacity: 0.75; display: block; margin-bottom: 12px;">Host your own group to share your sub-profiles with friends.</span>
                        <button type="button" id="bonfire-generate-btn" class="profiles-btn btn-primary" style="width: 100% !important; padding: 12px !important; font-weight: 600 !important; box-sizing: border-box !important; display: block !important; margin: 8px 0 !important;">Generate Join Code</button>
                    </div>
                `;
            }

            let guestSectionHtml = '';
            if (isMember) {
                guestSectionHtml = `
                    <div style="display: flex; flex-direction: column; gap: var(--jpf-gap-lg); border-bottom: 1px solid rgba(255,255,255,0.08); padding-bottom: 1.5rem;">
                        <div class="bonfire-form-group">
                            <label style="font-size: 1.1rem; font-weight: 700; display: block; margin-bottom: 4px;">Joined Bonfire</label>
                            <span style="font-size: 0.88rem; opacity: 0.75;">You have joined a bonfire group owned by:</span>
                            <div style="font-size: 1.25rem; font-weight: 700; color: var(--jpf-accent); margin: 12px 0; background: rgba(0,0,0,0.2); padding: 12px; border-radius: var(--jpf-r-md); border: 1px solid rgba(255,255,255,0.05); text-align: center;">${joinedOwnerName}</div>
                            <span style="font-size: 0.85rem; opacity: 0.6; display: block; margin-top: -4px;">You can access each other's profiles from the switcher grid.</span>
                        </div>
                        <div style="display: flex; justify-content: flex-end;">
                            <button type="button" id="bonfire-leave-btn" class="profiles-btn btn-danger-quiet" style="padding: 10px 20px !important; font-size: 0.95rem !important; box-sizing: border-box !important; margin: 0 !important; display: inline-block !important;">Leave Group</button>
                        </div>
                    </div>
                `;
            } else {
                guestSectionHtml = `
                    <div class="bonfire-form-group" style="border-bottom: 1px solid rgba(255,255,255,0.08); padding-bottom: 1.5rem;">
                        <label style="font-size: 1.1rem; font-weight: 700; display: block; margin-bottom: 4px;">Join a Bonfire</label>
                        <span style="font-size: 0.88rem; opacity: 0.75; display: block; margin-bottom: 12px;">Enter a friend's Bonfire Code to join their group:</span>
                        <div style="display: flex !important; gap: var(--jpf-gap) !important; align-items: center !important; width: 100% !important; box-sizing: border-box !important; margin: 12px 0 !important;">
                            <input type="text" id="bonfire-join-input" placeholder="e.g. B7F8XA" maxlength="6" style="flex: 1 1 0% !important; min-width: 0 !important; text-align: center !important; text-transform: uppercase !important; font-family: monospace !important; letter-spacing: 2px !important; height: 44px !important; box-sizing: border-box !important; margin: 0 !important; padding: 10px !important;" />
                            <button type="button" id="bonfire-join-btn" class="profiles-btn btn-primary" style="flex: 0 0 auto !important; padding: 0 24px !important; height: 44px !important; display: inline-flex !important; align-items: center !important; justify-content: center !important; font-weight: 600 !important; margin: 0 !important; box-sizing: border-box !important;">Join</button>
                        </div>
                        <div id="bonfire-join-error" style="display: none; color: #ff6b6b; font-size: 0.85rem; font-weight: 600; margin-top: 8px; text-align: center;"></div>
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
                <div class="bonfire-form-group" style="gap: 4px; border-top: 1px solid rgba(255,255,255,0.08); padding-top: 16px;">
                    <label class="library-check-label" style="display: inline-flex !important; align-items: center !important; gap: 0.5rem !important; cursor: pointer !important; user-select: none !important; font-size: 0.9rem !important; font-weight: 600 !important; position: relative !important;">
                        <input type="checkbox" id="bonfire-lan-bypass-checkbox" ${lanBypass ? 'checked' : ''} style="cursor: pointer !important; accent-color: #ff9900 !important; position: relative !important; opacity: 1 !important; width: 18px !important; height: 18px !important; margin: 0 !important; padding: 0 !important; flex-shrink: 0 !important;" />
                        <span>Let my Bonfire switch into my account on this network</span>
                    </label>
                    <div class="form-hint" style="margin-left: 1.6rem !important; opacity: 0.5 !important; font-size: 0.75rem !important; position: relative !important; display: block !important;">
                        No PIN needed on your home network. Away from home it still is${hasPinSet ? '' : ', and until you set one your account cannot be opened remotely at all'}.
                    </div>
                    ${isAdmin ? `
                    <div style="margin-left: 1.6rem; margin-top: 8px; padding: 10px 12px; background: rgba(255,153,0,0.08); border-left: 3px solid #ff9900; border-radius: var(--jpf-r-sm); font-size: 0.75rem; line-height: 1.5; color: rgba(255,255,255,0.8);">
                        <strong style="color: #ff9900;">This is an admin account.</strong> Whoever switches into it gets your admin rights.
                    </div>` : ''}
                    <div style="margin-left: 1.6rem; margin-top: 8px; font-size: 0.72rem; line-height: 1.5; opacity: 0.45;">
                        Behind a reverse proxy, check Networking → Known Proxies first, or everyone looks local.
                    </div>
                </div>
            ` : '';

            const settingsSectionHtml = `
                <div class="bonfire-form-group" style="margin-top: 5px; display: flex; flex-direction: column; gap: var(--jpf-gap);">
                    <div class="bonfire-form-group" style="gap: 4px;">
                        <label class="library-check-label" style="display: inline-flex !important; align-items: center !important; gap: 0.5rem !important; cursor: pointer !important; user-select: none !important; font-size: 0.9rem !important; font-weight: 600 !important; position: relative !important;">
                            <input type="checkbox" id="bonfire-hide-mine-checkbox" ${hideMine ? 'checked' : ''} style="cursor: pointer !important; accent-color: var(--jpf-accent) !important; position: relative !important; opacity: 1 !important; width: 18px !important; height: 18px !important; margin: 0 !important; padding: 0 !important; flex-shrink: 0 !important;" />
                            <span>Hide my sub-profiles from others</span>
                        </label>
                        <div class="form-hint" style="margin-left: 1.6rem !important; opacity: 0.5 !important; font-size: 0.75rem !important; position: relative !important; display: block !important;">Connected homes see only your master profile.</div>
                    </div>

                    <div class="bonfire-form-group" style="gap: 4px;">
                        <label class="library-check-label" style="display: inline-flex !important; align-items: center !important; gap: 0.5rem !important; cursor: pointer !important; user-select: none !important; font-size: 0.9rem !important; font-weight: 600 !important; position: relative !important;">
                            <input type="checkbox" id="bonfire-hide-others-checkbox" ${hideOthers ? 'checked' : ''} style="cursor: pointer !important; accent-color: var(--jpf-accent) !important; position: relative !important; opacity: 1 !important; width: 18px !important; height: 18px !important; margin: 0 !important; padding: 0 !important; flex-shrink: 0 !important;" />
                            <span>Hide other people's sub-profiles from me</span>
                        </label>
                        <div class="form-hint" style="margin-left: 1.6rem !important; opacity: 0.5 !important; font-size: 0.75rem !important; position: relative !important; display: block !important;">You see only the master profiles of connected homes.</div>
                    </div>

                    ${lanBypassSectionHtml}
                </div>
            `;

            container.innerHTML = `
                <div style="display: flex; flex-direction: column; gap: 1.5rem; width: 100%;">
                    ${hostSectionHtml}
                    ${guestSectionHtml}
                    ${settingsSectionHtml}
                    <div class="bonfire-dialog-actions" style="margin-top: 2rem !important; display: flex !important; justify-content: center !important; width: 100% !important; box-sizing: border-box !important; position: relative !important; bottom: auto !important; left: auto !important; right: auto !important; top: auto !important;">
                        <button id="bonfire-back-btn" class="profiles-btn btn-secondary" style="padding: 10px 24px !important; font-size: 1rem !important; box-sizing: border-box !important; margin: 0 !important; display: inline-block !important; width: auto !important; flex: 0 0 auto !important; position: relative !important; bottom: auto !important; left: auto !important; right: auto !important; top: auto !important;">Back</button>
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
                    this.showConfirmDialog('Kick Member', 'Are you sure you want to kick this user from your Bonfire group?', () => {
                        fetch(apiClient.getUrl('plugins/profiles/bonfire/kick'), {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json', ...this.getAuthHeaders(masterToken) },
                            body: JSON.stringify({ memberId: mId })
                        })
                        .then(res => {
                            if (res.ok) this.loadBonfireStatus(content, apiClient, masterToken);
                            else this.showAlert('Error', 'Failed to kick member.');
                        })
                        .catch(err => this.showAlert('Error', 'Error: ' + err.message));
                    });
                });
            });

            // Event Listeners: Delete Group
            const deleteBtn = container.querySelector('#bonfire-delete-btn');
            if (deleteBtn) {
                deleteBtn.addEventListener('click', () => {
                    this.showConfirmDialog('Delete Group', 'Delete your Bonfire? Members are disconnected and drop out of your switcher.', () => {
                        fetch(apiClient.getUrl('plugins/profiles/bonfire/delete-group'), {
                            method: 'POST',
                            headers: this.getAuthHeaders(masterToken)
                        })
                        .then(res => {
                            if (res.ok) this.loadBonfireStatus(content, apiClient, masterToken);
                            else this.showAlert('Error', 'Failed to delete group.');
                        })
                        .catch(err => this.showAlert('Error', 'Error: ' + err.message));
                    });
                });
            }

            // Event Listeners: Leave Group
            const leaveBtn = container.querySelector('#bonfire-leave-btn');
            if (leaveBtn) {
                leaveBtn.addEventListener('click', () => {
                    this.showConfirmDialog('Leave Group', 'Leave this Bonfire? You will stop sharing switchers.', () => {
                        fetch(apiClient.getUrl('plugins/profiles/bonfire/leave'), {
                            method: 'POST',
                            headers: this.getAuthHeaders(masterToken)
                        })
                        .then(res => {
                            if (res.ok) this.loadBonfireStatus(content, apiClient, masterToken);
                            else this.showAlert('Error', 'Failed to leave group.');
                        })
                        .catch(err => this.showAlert('Error', 'Error: ' + err.message));
                    });
                });
            }

            // Event Listeners: Generate Code
            const generateBtn = container.querySelector('#bonfire-generate-btn');
            if (generateBtn) {
                generateBtn.addEventListener('click', () => {
                    // Disable immediately to prevent double-fire on slow connections
                    generateBtn.disabled = true;
                    generateBtn.textContent = 'Generating…';
                    fetch(apiClient.getUrl('plugins/profiles/bonfire/generate'), {
                        method: 'POST',
                        headers: masterToken ? this.getAuthHeaders(masterToken) : {}
                    })
                    .then(res => {
                        if (res.ok) this.loadBonfireStatus(content, apiClient, masterToken);
                        else return res.text().then(text => { throw new Error(text); });
                    })
                    .catch(err => {
                        generateBtn.disabled = false;
                        generateBtn.textContent = 'Generate Join Code';
                        this.showAlert('Error', 'Failed to generate code: ' + err.message);
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
                        errDiv.textContent = 'Please enter a 6-character code.';
                        errDiv.style.display = 'block';
                        return;
                    }
                    // The server allows membership in only one group, so joining silently
                    // drops the current one. Confirm first rather than surprising the user.
                    if (isMember) {
                        this.showConfirmDialog(
                            'Leave your current Bonfire?',
                            'Joining this Bonfire removes you from your current one.',
                            () => submitJoin(code));
                        return;
                    }
                    submitJoin(code);
                };

                const submitJoin = (code) => {
                    errDiv.style.display = 'none';
                    // Disable to prevent double-submit; re-enable on all error paths
                    joinBtn.disabled = true;
                    joinBtn.textContent = 'Joining…';
                    fetch(apiClient.getUrl('plugins/profiles/bonfire/join'), {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json', ...this.getAuthHeaders(masterToken) },
                        body: JSON.stringify({ code: code })
                    })
                    .then(res => {
                        if (res.status === 429) {
                            errDiv.textContent = 'Too many failed attempts. Try again in 15 minutes.';
                            errDiv.style.display = 'block';
                            joinBtn.disabled = false;
                            joinBtn.textContent = 'Join';
                            return;
                        }
                        if (!res.ok) return res.text().then(text => { throw new Error(text); });
                        this.loadBonfireStatus(content, apiClient, masterToken);
                    })
                    .catch(err => {
                        errDiv.textContent = err.message || 'Failed to join group.';
                        errDiv.style.display = 'block';
                        joinBtn.disabled = false;
                        joinBtn.textContent = 'Join';
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
            const saveSettings = () => {
                clearTimeout(_settingsDebounceTimer);
                _settingsDebounceTimer = setTimeout(() => {
                    const hideMineVal = hideMineCb ? hideMineCb.checked : false;
                    const hideOthersVal = hideOthersCb ? hideOthersCb.checked : false;

                    const body = {
                        hideMySubProfilesFromOthers: hideMineVal,
                        hideOthersSubProfilesFromMe: hideOthersVal
                    };
                    // Omitted rather than sent as false when the toggle is not on screen, so a
                    // standalone account saving the hide flags cannot clear a setting it never
                    // rendered. The server treats a missing value as "leave alone".
                    if (lanBypassCb) body.allowHouseholdLanBypass = lanBypassCb.checked;

                    fetch(apiClient.getUrl('plugins/profiles/bonfire/settings'), {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json',
                            ...this.getAuthHeaders(masterToken)
                        },
                        body: JSON.stringify(body)
                    })
                    .then(res => {
                        if (!res.ok) console.error('Failed to save Bonfire settings.');
                    })
                    .catch(err => console.error('Error saving Bonfire settings:', err));
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
                        ? '<br><br><strong style="color:#ff9900;">This is an admin account.</strong> Whoever switches into it gets your admin rights.'
                        : '';

                    this.showConfirmDialog(
                        'Allow household switching?',
                        'Anyone in your Bonfire can open your account on your home network without your PIN.' + adminLine,
                        () => saveSettings(),
                        () => { lanBypassCb.checked = false; }
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

        injectSidebarLink: function () {
            const existingLink = document.getElementById('profiles-sidebar-link');
            if (existingLink) {
                // Already injected and configured. No need to touch it!
                return;
            }

            const activeInfo = this.getCachedActiveProfile();
            const initial = activeInfo.initial;
            const color = activeInfo.color;
            const name = activeInfo.name;

            const container = document.querySelector('.sidebar-nav') || 
                              document.querySelector('.navMenu') || 
                              document.getElementById('menuItems');
            if (!container) return;

            const link = document.createElement('a');
            link.id = 'profiles-sidebar-link';
            link.href = '#';
            link.className = 'sidebarLink navMenu-link';
            link.setAttribute('tabindex', '0');
            link.style.display = 'flex';
            link.style.alignItems = 'center';
            link.style.gap = '10px';
            link.style.cursor = 'pointer';

            link.innerHTML = `
                <div class="sidebar-profile-avatar" style="width: 24px; height: 24px; border-radius: 50%; background-color: ${color}; color: #fff; display: flex; align-items: center; justify-content: center; font-size: 0.85rem; font-weight: bold; text-transform: uppercase; flex-shrink: 0; overflow: hidden; position: relative;">
                    ${avatarInner(activeInfo.profileImage, initial, /* useThumb */ true)}
                </div>
                <span class="sidebarLinkText">${name} (Switch)</span>
            `;

            link.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                const drawer = document.querySelector('.drawer-open');
                if (drawer) {
                    const mask = document.querySelector('.appdrawer-mask');
                    if (mask) mask.click();
                }
                this.handleBubbleClick();
            });

            link.addEventListener('keydown', (e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    link.click();
                }
            });

            container.appendChild(link);
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
            const menu = document.querySelector(this.USER_MENU_SELECTOR);
            if (!menu) return;

            const existing = menu.querySelector('#profiles-user-menu-item');

            if (!this.isMenuLocation()) {
                // Location switched back to the floating button — take the row out again.
                if (existing) existing.remove();
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
            if (label) label.textContent = 'Switch Profile';
            else entry.textContent = 'Switch Profile';

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
        },

        /// Re-asserts the "Switch Profile" row on the Settings page (#/mypreferencesmenu),
        /// which is the list users actually mean by "the menu" — the header dropdown above
        /// is a different component and only some layouts show it.
        ///
        /// Source of truth: src/apps/legacy/routes/user/settings/index.tsx, page id
        /// myPreferencesMenuPage. The row goes in the "User" section above Sign out.
        syncPreferencesMenuEntry: function () {
            // Jellyfin keeps previous views in the DOM, so match the visible one or the
            // row lands on a page nobody is looking at.
            const page = Array.from(document.querySelectorAll('#myPreferencesMenuPage'))
                .find(el => el.offsetParent !== null || el.classList.contains('is-active'));
            if (!page) return;

            const existing = page.querySelector('#profiles-preferences-menu-item');

            if (!this.isMenuLocation()) {
                if (existing) existing.remove();
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
            if (label) label.textContent = 'Switch Profile';
            else entry.textContent = 'Switch Profile';

            entry.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                this.handleBubbleClick();
            });
            entry.addEventListener('keydown', (e) => {
                if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); entry.click(); }
            });

            signOut.parentNode.insertBefore(entry, signOut);
        },

        /// Closes the MUI menu. Our cloned row has no React handler, so nothing would
        /// dismiss it otherwise. Escape is what MUI itself listens for; clicking the
        /// backdrop is the fallback for builds where that listener is not attached.
        closeUserMenu: function () {
            document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }));

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

            const host = page.querySelector('.readOnlyContent, .padded-left, form') || page;

            const activeInfo = this.getCachedActiveProfile();
            const section = document.createElement('div');
            section.id = 'profiles-userprofile-section';
            section.className = 'verticalSection';
            section.style.cssText = 'margin: 2em 0; max-width: 44em;';
            section.innerHTML = `
                <h2 class="sectionTitle" style="display:flex; align-items:center; gap:0.4em;">
                    <span class="material-icons local_fire_department" aria-hidden="true" style="color:#ff9900;"></span>
                    Bonfire Profiles
                </h2>
                <p style="opacity:0.7; margin:0 0 1em 0; line-height:1.5;">
                    Currently watching as <strong>${escapeHtml(activeInfo.name || 'this account')}</strong>.
                    Switch to another profile in your household without signing out.
                </p>
                <button is="emby-button" type="button" id="profiles-userprofile-switch-btn" class="raised button-submit block">
                    <span>Switch Profile</span>
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

            if (!this.isProfileSessionActive()) {
                // No active session — remove entirely (will be re-created when needed)
                if (bubble) bubble.remove();
                return;
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
                // ── Strategy 2: geometry-based anchor ────────────────────────────────
                // If no named container matched (e.g. a custom Skin Manager theme),
                // find the rightmost visible button in the top 80px of the viewport
                // and insert next to it.  Works for ANY theme regardless of class names.
                const anchor = this._findGeometricHeaderAnchor();
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
                    }
                }
            }

            if (bubble && bubble.classList.contains('profiles-floating-fallback')) {
                const pos = this._findBestFallbackPosition();
                bubble.style.top = pos.top;
                bubble.style.bottom = pos.bottom;
                bubble.style.left = pos.left;
                bubble.style.right = pos.right;
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
            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey));
            if (!masterState || !masterState.masterToken) return;

            this._profilePrefetchPending = true;
            const url = ApiClient.getUrl('plugins/profiles/list');
            fetch(url, { headers: this.getAuthHeaders(masterState.masterToken) })
                .then(res => {
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
                                color: currentProfile.avatarColor || '#00A4DC',
                                initial: currentProfile.avatarInitial || (currentProfile.profileName ? currentProfile.profileName.charAt(0).toUpperCase() : 'P'),
                                profileImage: currentProfile.profileImage || null
                            };
                            this._sessionSet('jellyfin_profiles_active_info', JSON.stringify(info));
                        }
                    }

                    // Re-render bubble with the fetched info
                    const currentRouteType = this._lastRouteType || 'other';
                    this.evaluateFloatingBubbleVisibility(currentRouteType);
                })
                .catch(() => { this._profilePrefetchPending = false; });
        },

        // ── Header-container detection ───────────────────────────────────────────

        _findHeaderContainer: function () {
            // Strategy A: explicit Jellyfin class names (fast path)
            const byClass =
                document.querySelector('.headerRightButtons') ||
                document.querySelector('.headerSelfView') ||
                document.querySelector('.skinHeader-rightButtons') ||
                document.querySelector('.headerButtons-right') ||
                document.querySelector('.headerRight') ||
                document.querySelector('.viewHeaderRight');
            if (byClass) return byClass;

            // Strategy B: parent of any known Jellyfin icon button
            const knownBtn = document.querySelector(
                '.btnCurrentUser, .headerButtonUser, .headerButton-user, ' +
                '.btnCast, .headerButton-cast, ' +
                '[class*="headerButton"]:not(#profiles-floating-bubble)'
            );
            if (knownBtn) return knownBtn.parentElement;

            // Strategy C: find the button cluster inside a custom skin/theme header.
            // ElegantFin and Skin Manager themes wrap everything in .skinHeader or
            // a similarly named element; we pick the child that contains the most
            // icon buttons (likely the right-side group).
            const skinHeader = document.querySelector(
                '.skinHeader, .jellyfinHeader, [class*="skinHeader"], [class*="topBar"]'
            );
            if (skinHeader) {
                const children = skinHeader.querySelectorAll('div, nav, ul, span');
                let best = null, bestCount = 0;
                for (const el of children) {
                    const btns = el.querySelectorAll('button, a[role="button"]');
                    if (btns.length > bestCount && btns.length >= 2) {
                        bestCount = btns.length;
                        best = el;
                    }
                }
                return best;
            }

            return null;
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
            b.title = 'Switch Profile';
            b.setAttribute('aria-label', 'Switch Profile');

            const activeInfo = this.getCachedActiveProfile();
            b.innerHTML = `
                <div class="profiles-header-avatar" style="background-color: ${safeColor(activeInfo.color)}; width: 28px; height: 28px; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 0.85rem; font-weight: 700; color: #fff; text-shadow: 0 1px 2px rgba(0,0,0,0.5); border: 1.5px solid rgba(255,255,255,0.25); box-sizing: border-box; overflow: hidden; position: relative;">
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
            b.title = 'Switch Profile';
            b.setAttribute('aria-label', 'Switch Profile');

            // Set initial position dynamically
            const pos = this._findBestFallbackPosition();
            b.style.top = pos.top;
            b.style.bottom = pos.bottom;
            b.style.left = pos.left;
            b.style.right = pos.right;

            const activeInfo = this.getCachedActiveProfile();
            b.innerHTML = `
                <div class="profiles-header-avatar" style="background-color: ${safeColor(activeInfo.color)}; width: 28px; height: 28px; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 0.85rem; font-weight: 700; color: #fff; text-shadow: 0 1px 2px rgba(0,0,0,0.5); border: 1.5px solid rgba(255,255,255,0.25); box-sizing: border-box; overflow: hidden; position: relative;">
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
            const userBtn =
                container.querySelector('.headerButton-user, .btnCurrentUser, .headerButtonUser') ||
                container.lastElementChild;
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

            const masterState = JSON.parse(localStorage.getItem(this.config.masterStorageKey) || 'null');

            if (masterState && masterState.masterToken) {
                // Opening the switcher deliberately is a reversible act, so remember enough to
                // undo it. The profile's token is still valid — nothing has been signed out —
                // so backing out costs no PIN and no reload. Without this there is no way off
                // the picker at all once it is open, which on a TV means the Back button lands
                // on a dead end.
                const priorUserId = ApiClient.getCurrentUserId();
                const priorToken = this._sessionGet(this.config.activeSessionKey);
                this._resumeState = (priorToken && priorUserId)
                    ? {
                        token: priorToken,
                        userId: priorUserId,
                        info: this._sessionGet('jellyfin_profiles_active_info')
                    }
                    // No profile session to restore, which is the normal case in menu mode:
                    // the master never passed through the gate. Closing is still a valid
                    // answer, and without it there is no way off the picker at all.
                    : { closeOnly: true };

                // Put the master's credentials back in memory before listing profiles — the
                // active sub-profile's token cannot see its siblings.
                this.clearProfileSession();
                this.updateStoredCredentials(masterState.masterToken, masterState.masterUserId);
                ApiClient.setAuthenticationInfo(masterState.masterToken, masterState.masterUserId);
            } else {
                // Signed in as the master with nothing stored: closing is all that is
                // needed to get back to where they were.
                this._resumeState = { closeOnly: true };
            }

            // With no stored master state we are still signed in as the master — that is the
            // normal case in native mode, where the user never passes through the gate.
            // interceptHomeAndShowProfiles() records the state and takes it from there.
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
            const style = document.createElement('style');
            style.innerHTML = `
                /* ── Theme integration ───────────────────────────────────────────
                   jellyfin-web has no colour variables of its own. The request to add
                   them (jellyfin-web discussion #6520) is answered "planned" and nothing
                   more, so there is no supported token to read. What every custom theme
                   and Skin Manager *does* converge on is --accent, so read that where it
                   exists and fall back to Jellyfin's stock blue everywhere else. A stock
                   install renders exactly as it did before; a themed one stops having a
                   foreign blue button sitting in its header.

                   Declared on body as well as :root because a theme may set --accent on
                   either, and a custom property resolves against the element it is
                   declared on — reading it only at :root would silently miss every theme
                   that sets it on body. */
                :root, body {
                    --jpf-accent: var(--accent, #00a4dc);

                    /* Tints of the accent. These literals are what Bonfire shipped
                       through 1.4.1 and stay as the floor, because deriving them needs
                       colour arithmetic the older TV browsers do not have. */
                    --jpf-accent-a08: rgba(0, 164, 220, 0.08);
                    --jpf-accent-a18: rgba(0, 164, 220, 0.18);
                    --jpf-accent-a30: rgba(0, 164, 220, 0.3);
                    --jpf-accent-a40: rgba(0, 164, 220, 0.4);
                    --jpf-accent-a45: rgba(0, 164, 220, 0.45);
                    --jpf-accent-a50: rgba(0, 164, 220, 0.5);
                    --jpf-accent-a60: rgba(0, 164, 220, 0.6);

                    /* ── Shape and spacing ───────────────────────────────────
                       Every screen was styled on its own, which left eight corner
                       radii, ten container widths and four gap values across the
                       product — so the surfaces never read as one thing. Three of
                       each, chosen near the most common existing values.

                       50% and 999px are deliberately NOT tokens: those mean "a
                       circle" and "a pill", which are shapes rather than sizes. */
                    --jpf-r-sm: 6px;    /* inputs, chips, small controls */
                    --jpf-r-md: 12px;   /* buttons, cards, dialogs       */
                    --jpf-r-lg: 20px;   /* full-screen surfaces, avatars */

                    --jpf-gap: 12px;      /* controls sitting in a row */
                    --jpf-gap-lg: 1.25rem; /* sections and button rows */

                    /* Applied by the layout work rather than swapped in blind: a
                       container width is not interchangeable the way a radius is. */
                    --jpf-w-narrow: 420px; /* PIN and confirm dialogs   */
                    --jpf-w-form: 560px;   /* single-column forms       */
                    --jpf-w-wide: 960px;   /* two-column forms, the gate */
                }

                /* Where the browser can mix colours, tint the *theme's* accent rather
                   than the hardcoded blue. color-mix is Chrome 111+, so Tizen 6
                   (Chromium 76) and older webOS keep the literals above instead of
                   losing the glow altogether. */
                @supports (color: color-mix(in srgb, red 50%, transparent)) {
                    :root, body {
                        --jpf-accent-a08: color-mix(in srgb, var(--jpf-accent) 8%, transparent);
                        --jpf-accent-a18: color-mix(in srgb, var(--jpf-accent) 18%, transparent);
                        --jpf-accent-a30: color-mix(in srgb, var(--jpf-accent) 30%, transparent);
                        --jpf-accent-a40: color-mix(in srgb, var(--jpf-accent) 40%, transparent);
                        --jpf-accent-a45: color-mix(in srgb, var(--jpf-accent) 45%, transparent);
                        --jpf-accent-a50: color-mix(in srgb, var(--jpf-accent) 50%, transparent);
                        --jpf-accent-a60: color-mix(in srgb, var(--jpf-accent) 60%, transparent);
                    }
                }
                /* Administrator-supplied avatar picker.
                   auto-fill rather than a fixed count so it reflows from a phone to a TV
                   without a media query, and scrolls internally instead of pushing the
                   form's buttons off screen when the library is large. */
                .avatar-color-group {
                    transition: opacity 0.2s ease;
                }
                .avatar-color-group.is-inert {
                    opacity: 0.45;
                }
                /* Collapsed by default on a profile that already has a picture. */
                .picture-sources {
                    display: none;
                    flex-direction: column;
                    gap: var(--jpf-gap);
                    width: 100%;
                    min-width: 0;
                }
                .picture-sources.is-open {
                    display: flex;
                }
                .avatar-library-grid {
                    display: grid;
                    grid-template-columns: repeat(auto-fill, minmax(56px, 1fr));
                    gap: var(--jpf-gap);
                    max-height: 200px;
                    overflow-y: auto;
                    padding: 4px;
                    background: rgba(0,0,0,0.15);
                    border-radius: var(--jpf-r-md);
                    border: 1px solid rgba(255,255,255,0.05);
                }
                .avatar-library-item {
                    aspect-ratio: 1 / 1;
                    padding: 0;
                    border: 2px solid transparent;
                    border-radius: 50%;
                    overflow: hidden;
                    background: rgba(255,255,255,0.04);
                    cursor: pointer;
                    transition: border-color 0.15s ease, transform 0.15s ease;
                }
                .avatar-library-item img {
                    width: 100%; height: 100%; object-fit: cover; display: block;
                }
                .avatar-library-item:hover,
                .avatar-library-item:focus {
                    border-color: var(--jpf-accent);
                    transform: scale(1.06);
                    outline: none;
                }

                /* Scroll Block */
                body.profiles-no-scroll, html.profiles-no-scroll {
                    overflow: hidden !important;
                    height: 100% !important;
                }

                /* Hide loaders behind overlay */
                body.profiles-no-scroll .docloader,
                body.profiles-no-scroll .mainLoader,
                body.profiles-no-scroll .loadingSpinner,
                body.profiles-no-scroll .spinner,
                body.profiles-no-scroll .view-loader,
                body.profiles-no-scroll paper-spinner,
                body.profiles-no-scroll paper-spinner-lite,
                body.profiles-no-scroll [class*="loader"],
                body.profiles-no-scroll [class*="spinner"],
                body.profiles-no-scroll [id*="loader"],
                body.profiles-no-scroll [id*="spinner"] {
                    display: none !important;
                    opacity: 0 !important;
                    visibility: hidden !important;
                }

                /* Overlay Glassmorphic Layout */
                #profiles-gate-overlay {
                    position: fixed; top: 0; left: 0; width: 100vw; height: 100vh;
                    background: radial-gradient(circle at 50% 40%, #1e1e2e 0%, #0d0d12 100%);
                    z-index: 99999; display: flex; align-items: center; justify-content: center;
                    color: #fff; font-family: 'Outfit', 'Inter', sans-serif;
                    overflow-y: auto; padding: 2rem 0; box-sizing: border-box;
                    opacity: 1; transition: opacity 0.22s ease;
                }
                .profiles-modal-content {
                    text-align: center; max-width: 900px; width: 90%;
                    display: flex; flex-direction: column; align-items: center;
                    margin: auto;
                }
                .profiles-title {
                    font-size: 3rem; font-weight: 700; margin-bottom: 3rem;
                    text-shadow: 0 4px 20px rgba(0,0,0,0.6); letter-spacing: -0.05rem;
                }
                .profiles-home-section {
                    width: 100%;
                    margin-bottom: 2.5rem;
                    background: rgba(255, 255, 255, 0.015);
                    border: 1px solid rgba(255, 255, 255, 0.04);
                    border-radius: var(--jpf-r-lg);
                    padding: 1.75rem 2rem;
                    box-sizing: border-box;
                    box-shadow: 0 10px 30px rgba(0, 0, 0, 0.2);
                    text-align: left;
                }
                .profiles-home-header {
                    display: flex;
                    align-items: center;
                    gap: 0.75rem;
                    margin-bottom: 1.75rem;
                    padding-bottom: 0.75rem;
                    border-bottom: 1px solid rgba(255, 255, 255, 0.08);
                }
                /* Your own Bonfire: a warm amber flame. */
                .profiles-home-icon {
                    font-size: 1.8rem;
                    color: #ff9900;
                    text-shadow: 0 2px 10px rgba(255, 153, 0, 0.35);
                }
                /* A linked household's Bonfire: deeper ember, so the two read as different
                   groups without needing a second glyph. */
                .profiles-home-icon.bonfire-icon-color {
                    color: #ff5500;
                    text-shadow: 0 2px 10px rgba(255, 85, 0, 0.3);
                }
                .profiles-home-title {
                    font-size: 1.5rem;
                    font-weight: 700;
                    letter-spacing: -0.02em;
                    color: #fff;
                }
                .profiles-grid {
                    display: flex; flex-wrap: wrap; gap: 3rem; justify-content: center; width: 100%;
                    max-width: var(--jpf-w-wide); margin: 0 auto;
                }
                /* Cards used to be a fixed 140px whatever the display, so on a desktop
                   they sat in a thin band with large voids above and below, and on a TV
                   they were small at viewing distance. clamp keeps the phone size as
                   the floor and lets them grow with the viewport. */
                .profile-card {
                    display: flex; flex-direction: column; align-items: center;
                    width: clamp(140px, 12vw, 210px); cursor: pointer; position: relative;
                }
                .profile-avatar-container {
                    position: relative;
                    width: clamp(130px, 11vw, 195px);
                    height: clamp(130px, 11vw, 195px);
                    margin-top: 15px;
                }
                .profile-crown {
                    position: absolute; top: -20px; left: 50%;
                    transform: translateX(-50%); z-index: 15;
                    pointer-events: none;
                    animation: crownFloat 3s ease-in-out infinite;
                }
                @keyframes crownFloat {
                    0% { transform: translateX(-50%) translateY(0); }
                    50% { transform: translateX(-50%) translateY(-4px) rotate(2deg); }
                    100% { transform: translateX(-50%) translateY(0); }
                }
                .profile-avatar {
                    position: relative;
                    width: 100%; height: 100%; border-radius: var(--jpf-r-lg);
                    /* The 3px border below is transparent, so without this the avatar
                       colour showed through it as a ring — a blue outline around Bard's
                       orange picture. Clipping to the padding box keeps the colour behind
                       the initial, which is the only thing it is for, and off the border. */
                    background-clip: padding-box;
                    display: flex; align-items: center; justify-content: center;
                    font-size: 3.5rem; font-weight: bold; text-transform: uppercase;
                    box-shadow: 0 10px 30px rgba(0,0,0,0.5);
                    transition: transform 0.3s cubic-bezier(0.165, 0.84, 0.44, 1), box-shadow 0.3s ease, border-color 0.3s ease;
                    border: 3px solid transparent;
                }
                /* The one you are already in. Now that the avatar colour no longer
                   paints the border, an accent ring means exactly one thing. */
                .profile-card.is-current .profile-avatar {
                    border-color: var(--jpf-accent);
                }
                .profile-current-badge {
                    display: block;
                    font-size: 0.7rem;
                    font-weight: 600;
                    letter-spacing: 0.04em;
                    text-transform: uppercase;
                    color: var(--jpf-accent);
                    margin-top: 2px;
                }
                .profile-card:hover .profile-avatar,
                .profile-card:focus .profile-avatar,
                .profile-card:focus-within .profile-avatar {
                    transform: scale(1.08);
                    box-shadow: 0 15px 35px var(--jpf-accent-a40);
                    border-color: rgba(255,255,255,0.8);
                }
                .profile-card:focus {
                    outline: none;
                }
                .add-avatar {
                    border: 3px dashed rgba(255,255,255,0.25);
                    background: rgba(255,255,255,0.02) !important; color: rgba(255,255,255,0.4);
                }
                .profile-card:hover .add-avatar,
                .profile-card:focus .add-avatar,
                .profile-card:focus-within .add-avatar {
                    border-color: rgba(255,255,255,0.8); color: #fff;
                    background: rgba(255,255,255,0.05) !important;
                }
                .profile-name {
                    margin-top: 1rem; font-size: 1.25rem; font-weight: 500;
                    opacity: 0.75; transition: opacity 0.3s ease;
                    display: flex; flex-direction: column; align-items: center; gap: 4px;
                    text-align: center;
                }
                .profiles-limit-notice {
                    font-size: 0.85rem; color: rgba(255,255,255,0.35);
                    font-style: italic; align-self: center; padding: 1rem 0;
                    width: 140px; text-align: center;
                }
                .master-badge {
                    font-size: 0.8rem; opacity: 0.6; font-weight: 400;
                }
                .profile-card:hover .profile-name {
                    opacity: 1;
                }

                /* Manage Mode Overlay Icon styling */
                .profile-avatar-overlay-wrap {
                    position: absolute; top: 0; left: 0; width: 100%; height: 100%;
                    background: rgba(0, 0, 0, 0.65); border-radius: var(--jpf-r-lg);
                    display: flex; align-items: center; justify-content: center;
                    opacity: 0; transition: opacity 0.25s ease;
                    pointer-events: none;
                    z-index: 5;
                }
                .profile-card.manage-mode:hover .profile-avatar-overlay-wrap {
                    opacity: 1;
                }
                .profile-avatar-overlay-svg {
                    filter: drop-shadow(0 2px 6px rgba(0,0,0,0.6));
                }
                .profile-lock-indicator {
                    position: absolute; bottom: 8px; right: 8px;
                    background: rgba(15, 15, 15, 0.85); border-radius: 50%;
                    width: 32px; height: 32px; display: flex;
                    align-items: center; justify-content: center;
                    pointer-events: none;
                    box-shadow: 0 4px 10px rgba(0,0,0,0.55);
                    border: 1.5px solid rgba(255,255,255,0.2);
                    z-index: 10;
                }
                .profile-bonfire-indicator {
                    position: absolute; top: 8px; left: 8px;
                    background: linear-gradient(135deg, #ff9900 0%, #ff5500 100%);
                    border-radius: 50%;
                    width: 32px; height: 32px; display: flex;
                    align-items: center; justify-content: center;
                    pointer-events: none;
                    box-shadow: 0 4px 10px rgba(0,0,0,0.55);
                    border: 1.5px solid rgba(255,255,255,0.2);
                    z-index: 10;
                }
                #bonfire-join-input {
                    background: rgba(255,255,255,0.06); border: 1px solid rgba(255,255,255,0.15);
                    border-radius: var(--jpf-r-md); padding: 10px; color: #fff; font-size: 1rem;
                    transition: border-color 0.25s, box-shadow 0.25s;
                    position: relative !important;
                }
                #bonfire-join-input:focus {
                    border-color: var(--jpf-accent); outline: none;
                    box-shadow: 0 0 10px var(--jpf-accent-a40);
                }
                .bonfire-kick-btn:focus, .bonfire-kick-btn:hover {
                    background-color: #e64980 !important;
                    outline: none;
                    box-shadow: 0 0 10px rgba(255, 107, 107, 0.4);
                }
                .profile-card.manage-mode:hover .profile-avatar {
                    transform: scale(1.08);
                    border-color: var(--jpf-accent);
                }

                /* PIN Status Badges */
                .profile-pin-badge {
                    font-size: 0.75rem; margin-top: 4px; padding: 2px 8px; border-radius: var(--jpf-r-md);
                    font-weight: 600; display: inline-flex; align-items: center; gap: 4px;
                }
                /* These were inverted: "PIN Protected" in red and "No PIN" in green, so
                   the screen called the protected state a problem and the unprotected one
                   a success. Neither is an error, so neither gets an error colour —
                   protected reads present, unprotected reads muted. */
                .profile-pin-badge.locked {
                    background: rgba(255, 255, 255, 0.10); color: rgba(255, 255, 255, 0.88);
                    border: 1px solid rgba(255, 255, 255, 0.20);
                }
                .profile-pin-badge.unlocked {
                    background: transparent; color: rgba(255, 255, 255, 0.45);
                    border: 1px solid rgba(255, 255, 255, 0.12);
                }

                /* Floating Profiles Selector Bubble — fallback corner pill */
                /* This only appears when header injection fails entirely.    */
                #profiles-floating-bubble.profiles-floating-fallback {
                    position: fixed;
                    bottom: 24px; left: 24px; right: auto; top: auto;
                    z-index: 9999;
                    background: transparent;
                    color: #fff; padding: 0; border-radius: 50%;
                    cursor: pointer;
                    display: inline-flex; align-items: center; justify-content: center;
                    box-shadow: 0 4px 16px rgba(0,0,0,0.35);
                    transition: transform 0.2s ease, box-shadow 0.2s ease;
                    border: none;
                    width: 40px; height: 40px;
                }
                #profiles-floating-bubble.profiles-floating-fallback:hover,
                #profiles-floating-bubble.profiles-floating-fallback:focus {
                    transform: scale(1.08) translateY(-2px);
                    box-shadow: 0 8px 20px rgba(0,0,0,0.5);
                    outline: none;
                }

                /* Header button integration style */
                #profiles-floating-bubble.headerButton {
                    display: inline-flex;
                    align-items: center;
                    justify-content: center;
                    color: inherit;
                    background: transparent;
                    border: none;
                    cursor: pointer;
                    vertical-align: middle;
                    margin: 0 4px;
                    padding: 0;
                }

                /* Footer and bottom buttons */
                .profiles-footer {
                    margin-top: 4rem; width: 100%; display: flex; justify-content: center;
                    /* Matches .pin-actions, the button row on the other gate screens. */
                    gap: var(--jpf-gap-lg); flex-wrap: wrap;
                }

                /* PIN Entry Form styles */
                .pin-entry-container {
                    display: flex; flex-direction: column; align-items: center; gap: 2rem;
                }
                #profile-pin-input, #master-pin-input {
                    background: rgba(255,255,255,0.06); border: 2px solid rgba(255,255,255,0.15);
                    border-radius: var(--jpf-r-md); color: #fff; font-size: 2.5rem; text-align: center;
                    padding: 12px; width: 180px; letter-spacing: 0.6rem;
                    transition: border-color 0.3s ease, box-shadow 0.3s ease;
                    -webkit-text-security: disc;
                }
                #create-pin-input, #edit-pin-input {
                    -webkit-text-security: disc;
                }
                #profile-pin-input:focus, #master-pin-input:focus {
                    border-color: var(--jpf-accent); outline: none;
                    box-shadow: 0 0 15px var(--jpf-accent-a30);
                }
                .pin-error-text {
                    color: #ff6b6b;
                    font-size: 0.95rem;
                    font-weight: 500;
                    margin-top: -10px;
                    text-align: center;
                }
                .pin-input-error {
                    border-color: #ff6b6b !important;
                    box-shadow: 0 0 15px rgba(255, 107, 107, 0.4) !important;
                }

                /* Button Styling */
                .profiles-btn {
                    padding: 10px 24px; border: 1.5px solid transparent; border-radius: var(--jpf-r-md);
                    font-weight: 600; font-size: 1rem; cursor: pointer;
                    transition: background-color 0.25s ease, border-color 0.25s ease, box-shadow 0.25s ease, transform 0.2s ease;
                }
                .profiles-btn:disabled {
                    opacity: 0.55;
                    cursor: not-allowed;
                    transform: none !important;
                    box-shadow: none !important;
                }
                .btn-primary {
                    background-color: var(--jpf-accent); color: #fff;
                }
                .btn-primary:hover,
                .btn-primary:focus {
                    background-color: #0082ad; border-color: rgba(255, 255, 255, 0.4);
                    box-shadow: 0 0 12px var(--jpf-accent-a50); transform: translateY(-1px);
                    outline: none;
                }
                .btn-secondary {
                    background-color: rgba(255,255,255,0.08); color: rgba(255,255,255,0.7);
                    border: 1px solid rgba(255,255,255,0.15);
                }
                .btn-secondary:hover,
                .btn-secondary:focus {
                    background-color: rgba(255,255,255,0.15); color: #fff;
                    border-color: var(--jpf-accent);
                    box-shadow: 0 0 10px var(--jpf-accent-a40);
                    outline: none;
                }
                .pin-actions {
                    display: flex; gap: var(--jpf-gap-lg); margin-top: 1rem;
                }

                /* Profile Creation Form styles */
                /* A column of sections. Below the breakpoint the two of these simply
                   stack, so the phone order is unchanged: Profile, Security,
                   Libraries, Restrictions. */
                .form-col {
                    display: flex; flex-direction: column; gap: 1.5rem;
                    min-width: 0;
                }

                /* Two columns once there is room for them. Everything Bonfire drew was
                   a phone column whatever the screen — on a desktop this form was a
                   ~440px ribbon with the rest of the window empty, and the Save button
                   several screens below the name field. */
                @media (min-width: 900px) {
                    .create-profile-container.is-two-col {
                        max-width: var(--jpf-w-wide);
                        display: grid;
                        grid-template-columns: 1fr 1fr;
                        align-items: start;
                        max-height: 85vh;
                    }
                    /* Everything that is not one of the two columns spans both of them.
                       Naming only .profile-dialog-actions left the Create form's button row
                       and its error line as half-width grid items under the left column. */
                    .create-profile-container.is-two-col > *:not(.form-col) {
                        grid-column: 1 / -1;
                    }
                }

                .create-profile-container {
                    width: 100%; max-width: var(--jpf-w-form); box-sizing: border-box;
                    display: flex; flex-direction: column; gap: 1.5rem;
                    background: rgba(255,255,255,0.03); border: 1px solid rgba(255,255,255,0.07);
                    border-radius: var(--jpf-r-lg); padding: 2rem; box-shadow: 0 20px 50px rgba(0,0,0,0.4);
                    text-align: left; max-height: 75vh; overflow-y: auto;
                }
                .profile-dialog-actions {
                    margin-top: 1rem; display: flex; justify-content: space-between; width: 100%; gap: var(--jpf-gap);
                }
                .dialog-action-buttons {
                    display: flex; gap: var(--jpf-gap);
                }
                .btn-danger {
                    background: rgba(230,0,0,0.85); color:#fff; border:none;
                }
                /* The trigger, not the confirmation. Delete Profile and Delete Group sat
                   beside Save as equally solid red buttons, so the destructive option
                   carried the same weight as the safe one. btn-danger itself stays filled
                   because it is also the Confirm button on the dialog these open, where
                   loud red is correct — that is the moment you mean it. */
                .btn-danger-quiet {
                    background: transparent; color: #ff8787;
                    border: 1.5px solid rgba(230,0,0,0.40);
                }
                .btn-danger-quiet:hover,
                .btn-danger-quiet:focus {
                    background: rgba(230,0,0,0.15);
                    border-color: rgba(230,0,0,0.85);
                    color: #ffa8a8;
                    outline: none;
                }
                .btn-danger:hover,
                .btn-danger:focus {
                    background: rgba(200,0,0,0.95);
                    outline: none;
                }

                /* Mobile Responsiveness Media Queries */
                @media (max-width: 600px) {
                    #profiles-floating-bubble.profiles-floating-fallback {
                        left: 12px;
                        right: auto;
                        bottom: 12px;
                    }
                }
                /* Wrap Bonfire join row on very small phones so the button
                   doesn't overflow or clip its label on screens under 360px. */
                @media (max-width: 360px) {
                    #bonfire-join-input,
                    #bonfire-join-btn {
                        flex: 1 1 100% !important;
                        width: 100% !important;
                        box-sizing: border-box !important;
                    }
                }
                @media (max-width: 480px) {
                    .profiles-title {
                        font-size: 2.2rem;
                        margin-bottom: 2rem;
                    }
                    .profiles-home-section {
                        padding: 1.25rem 1rem;
                        margin-bottom: 1.5rem;
                        border-radius: var(--jpf-r-md);
                    }
                    .profiles-home-title {
                        font-size: 1.25rem;
                    }
                    .profiles-grid {
                        gap: 1.5rem;
                    }
                    .profile-dialog-actions {
                        flex-direction: column;
                    }
                    .dialog-action-buttons {
                        width: 100%;
                    }
                    .dialog-action-buttons button, #edit-delete-btn {
                        flex: 1;
                        width: 100%;
                        text-align: center;
                    }
                }
                .form-group,
                .bonfire-form-group {
                    display: flex; flex-direction: column; gap: 0.5rem;
                }
                .form-group label,
                .bonfire-form-group label {
                    font-size: 0.9rem; font-weight: 600; color: rgba(255,255,255,0.6);
                }
                .form-group input[type="text"],
                .bonfire-form-group input[type="text"],
                .form-group input[type="password"] {
                    background: rgba(255,255,255,0.06); border: 1px solid rgba(255,255,255,0.15);
                    border-radius: var(--jpf-r-md); padding: 10px; color: #fff; font-size: 1rem;
                }
                .form-group select {
                    background: rgba(255, 255, 255, 0.06) url("data:image/svg+xml;charset=utf-8,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='%23ffffff'%3E%3Cpath d='M7 10l5 5 5-5H7z'/%3E%3C/svg%3E") no-repeat right 12px center;
                    background-size: 20px;
                    border: 1px solid rgba(255,255,255,0.15);
                    border-radius: var(--jpf-r-md); padding: 10px; color: #fff; font-size: 1rem;
                    cursor: pointer;
                    appearance: none;
                    -webkit-appearance: none;
                    -moz-appearance: none;
                    padding-right: 36px;
                }
                .form-group select option {
                    background-color: #1a1a1a;
                    color: #fff;
                }
                .form-group input:focus, .form-group select:focus {
                    border-color: var(--jpf-accent); outline: none;
                    box-shadow: 0 0 10px var(--jpf-accent-a40);
                }
                .avatar-color-picker {
                    display: flex; flex-wrap: wrap; gap: var(--jpf-gap);
                }
                .color-dot {
                    width: 32px; height: 32px; border-radius: 50%; cursor: pointer;
                    border: 2px solid transparent; transition: transform 0.2s ease, border-color 0.2s ease;
                }
                .color-dot:hover,
                .color-dot:focus {
                    border-color: rgba(255,255,255,0.8);
                    transform: scale(1.1);
                    outline: none;
                }
                .color-dot.active {
                    border-color: #fff; transform: scale(1.1);
                }
                .library-checklist {
                    background: rgba(255,255,255,0.04); border-radius: var(--jpf-r-md);
                    padding: 10px; display: flex; flex-direction: column; gap: 0.5rem;
                    max-height: 140px; overflow-y: auto; border: 1px solid rgba(255,255,255,0.1);
                }
                /* Per-library artwork rows in the edit form (issue #19). */
                /* Deliberately no max-height and no overflow. This list used to scroll
                   inside a form that also scrolled, next to a second list that scrolled
                   too — three nested scroll areas, and on a D-pad no way to tell which
                   one the remote had hold of. The dialog is the only scroller now. */
                .libart-list {
                    display: flex;
                    flex-direction: column;
                    gap: 6px;
                    background: rgba(255,255,255,0.04);
                    border: 1px solid rgba(255,255,255,0.1);
                    border-radius: var(--jpf-r-md);
                    padding: 8px;
                }
                .libart-row {
                    display: flex;
                    align-items: center;
                    /* Wrap rather than crush the name. The row is thumb + name + mode,
                       and in a narrow dialog the name was the only flexible part, so it
                       ellipsised down to "3D Movi…" while the select kept its width. */
                    flex-wrap: wrap;
                    gap: var(--jpf-gap);
                    padding: 6px 8px;
                    border-radius: var(--jpf-r-sm);
                    background: rgba(255, 255, 255, 0.04);
                }
                .libart-thumb {
                    width: 44px;
                    height: 26px;
                    flex-shrink: 0;
                    border-radius: var(--jpf-r-sm);
                    background-color: rgba(255, 255, 255, 0.08);
                    background-size: cover;
                    background-position: center;
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    font-size: 0.8rem;
                    color: rgba(255, 255, 255, 0.35);
                }
                /* Wraps the tick, the thumbnail and the name — the part of the row that
                   is one target. initTVCheckboxes binds to .library-check-label, which is
                   how a remote toggles it: Enter does not tick a native checkbox. */
                .libart-check {
                    flex: 1 1 220px;
                    min-width: 0;
                    margin-left: 0;
                }
                .libart-name {
                    /* Enough to read a real library name before anything else gives. */
                    flex: 1 1 150px;
                    min-width: 150px;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                    font-size: 0.9rem;
                }
                .libart-mode {
                    flex-shrink: 0;
                    width: auto !important;
                    min-width: 100px;
                    padding: 4px 6px !important;
                    font-size: 0.82rem !important;
                    margin: 0 !important;
                }
                /* Kept in the layout when hidden so rows do not jump as modes change. */
                .libart-choose {
                    flex-shrink: 0;
                }
                .library-check-label {
                    display: flex !important; align-items: center !important; gap: 0.6rem; cursor: pointer;
                    font-size: 0.95rem; color: rgba(255,255,255,0.85);
                    border-radius: var(--jpf-r-sm); padding: 4px 8px; margin-left: -8px;
                    transition: background 0.2s, color 0.2s;
                    position: relative !important;
                }
                .library-check-label input {
                    cursor: pointer; accent-color: var(--jpf-accent);
                    position: relative !important;
                    opacity: 1 !important;
                    width: 18px !important;
                    height: 18px !important;
                    margin: 0 !important;
                    padding: 0 !important;
                    flex-shrink: 0 !important;
                }
                .library-check-label:focus, .library-check-label:hover {
                    background: rgba(255, 255, 255, 0.05);
                    color: #fff;
                    outline: none;
                }
                .library-check-label:focus input, .library-check-label:hover input {
                    box-shadow: 0 0 8px var(--jpf-accent-a60);
                }
                /* ── Titled sections in the create/edit forms ───────────────────── */
                .profile-section {
                    background: rgba(255, 255, 255, 0.025);
                    border: 1px solid rgba(255, 255, 255, 0.08);
                    border-radius: var(--jpf-r-md);
                    padding: 1rem 1.15rem 1.15rem;
                    margin-bottom: 1.25rem;
                }
                .profile-section-header {
                    display: flex;
                    align-items: flex-start;
                    gap: 0.7rem;
                    padding-bottom: 0.75rem;
                    margin-bottom: 1rem;
                    border-bottom: 1px solid rgba(255, 255, 255, 0.08);
                }
                .profile-section-icon {
                    font-size: 1.35rem;
                    color: var(--jpf-accent);
                    flex-shrink: 0;
                    line-height: 1.3;
                }
                .profile-section-heading {
                    display: flex;
                    flex-direction: column;
                    gap: 2px;
                    min-width: 0;
                }
                .profile-section-title {
                    font-size: 1.05rem;
                    font-weight: 700;
                    color: #fff;
                    margin: 0;
                    line-height: 1.3;
                }
                .profile-section-subtitle {
                    font-size: 0.8rem;
                    color: rgba(255, 255, 255, 0.5);
                    line-height: 1.35;
                }
                .profile-section-body {
                    display: flex;
                    flex-direction: column;
                    gap: 1.1rem;
                }
                /* Sections already space their children, so the per-field margin that the
                   flat layout relied on would double up here. */
                .profile-section-body .form-group {
                    margin-bottom: 0;
                }

                /* Header row that sits inside a section, e.g. "Enabled Libraries | Select all".
                   Wraps rather than squashing the label on narrow phones. */
                .section-inline-header {
                    display: flex;
                    justify-content: space-between;
                    align-items: center;
                    gap: 0.5rem;
                    flex-wrap: wrap;
                    margin-bottom: 0.35rem;
                }

                .form-divider {
                    display: flex;
                    align-items: center;
                    gap: var(--jpf-gap);
                    opacity: 0.5;
                    font-size: 0.8rem;
                    margin: 2px 0;
                }
                .form-divider::before, .form-divider::after {
                    content: "";
                    flex: 1;
                    border-top: 1px solid rgba(255, 255, 255, 0.2);
                }
                .form-hint-warn {
                    color: rgba(245, 159, 0, 0.85) !important;
                }
                .form-error {
                    color: #ff6b6b;
                    font-size: 0.88rem;
                    font-weight: 600;
                    text-align: center;
                    padding: 8px 12px;
                    background: rgba(255, 107, 107, 0.1);
                    border-radius: var(--jpf-r-md);
                    border: 1px solid rgba(255, 107, 107, 0.25);
                }

                .image-upload-row {
                    display: flex;
                    align-items: center;
                    gap: var(--jpf-gap);
                    flex-wrap: wrap;
                }
                .image-upload-preview {
                    width: 64px; height: 64px;
                    border-radius: 50%;
                    color: #fff;
                    display: flex; align-items: center; justify-content: center;
                    font-size: 1.8rem; font-weight: bold; text-transform: uppercase;
                    overflow: hidden;
                    border: 2px solid rgba(255, 255, 255, 0.2);
                    flex-shrink: 0;
                }
                .image-upload-btn {
                    cursor: pointer;
                    display: inline-flex;
                    align-items: center;
                    justify-content: center;
                    gap: 8px;
                    padding: 10px 20px;
                    font-size: 0.95rem;
                    align-self: flex-start;
                }

                .device-dropdown-item {
                    display: flex;
                    align-items: center;
                    justify-content: space-between;
                    gap: 8px;
                    padding: 8px 12px;
                    border-bottom: 1px solid rgba(255, 255, 255, 0.05);
                }
                .device-delete-btn {
                    background: transparent; border: none; color: #ff6b6b;
                    cursor: pointer; padding: 6px; border-radius: var(--jpf-r-sm);
                    display: flex; align-items: center; justify-content: center;
                    font-size: 1.1rem; flex-shrink: 0;
                    transition: background 0.2s;
                }
                .device-delete-btn:hover, .device-delete-btn:focus {
                    background: rgba(255, 107, 107, 0.15);
                    outline: none;
                }

                /* The dropdown is absolutely positioned so it overlays following fields
                   instead of pushing the form around when it opens. */
                .devices-dropdown-list {
                    position: absolute;
                    top: 100%; left: 0; right: 0;
                    z-index: 10000;
                    margin-top: 4px;
                    max-height: 240px;
                    overflow-y: auto;
                }

                /* Phones: reclaim horizontal space and stop the section chrome from
                   eating the width the form fields need. */
                @media (max-width: 600px) {
                    .profile-section {
                        padding: 0.85rem 0.8rem 0.9rem;
                        margin-bottom: 1rem;
                        border-radius: var(--jpf-r-md);
                    }
                    .profile-section-body { gap: 0.95rem; }
                    .profile-section-title { font-size: 1rem; }
                    .profile-section-subtitle { font-size: 0.75rem; }
                    .image-upload-btn { width: 100%; }
                }

                /* TV / D-pad: the focus ring must be obvious from across a room, and a
                   focused control inside a scrolling section has to scroll itself into
                   view rather than sitting behind a section header. */
                .profile-section :focus-visible {
                    outline: 2px solid var(--jpf-accent);
                    outline-offset: 2px;
                    scroll-margin-top: 4rem;
                    scroll-margin-bottom: 4rem;
                }

                .tag-editor {
                    display: flex; flex-direction: column; gap: 8px;
                }
                .tag-chip-list {
                    display: flex; flex-wrap: wrap; gap: 6px;
                    background: rgba(255,255,255,0.04); border-radius: var(--jpf-r-md);
                    border: 1px solid rgba(255,255,255,0.1);
                    padding: 8px; min-height: 40px;
                    max-height: 120px; overflow-y: auto;
                    align-content: flex-start;
                }
                .tag-chip-list[data-empty="true"]::before {
                    content: "No tags — this filter is off";
                    font-size: 0.8rem; color: rgba(255,255,255,0.35);
                    align-self: center;
                }
                .tag-chip {
                    display: inline-flex; align-items: center; gap: 6px;
                    background: var(--jpf-accent-a18);
                    border: 1px solid var(--jpf-accent-a45);
                    color: #fff; border-radius: 999px;
                    padding: 3px 6px 3px 12px; font-size: 0.85rem;
                    max-width: 100%; word-break: break-word;
                }
                .tag-chip-remove {
                    background: transparent; border: none; color: rgba(255,255,255,0.7);
                    cursor: pointer; font-size: 1rem; line-height: 1;
                    padding: 0; width: 18px; height: 18px; border-radius: 50%;
                    display: flex; align-items: center; justify-content: center;
                    transition: background 0.2s, color 0.2s;
                }
                .tag-chip-remove:hover, .tag-chip-remove:focus {
                    background: rgba(255,255,255,0.18); color: #fff; outline: none;
                }
                .tag-input-row {
                    display: flex; gap: 8px;
                }
                .tag-input-row .tag-input {
                    flex: 1; min-width: 0;
                }
                .tag-input-row .tag-add-btn {
                    padding: 8px 18px; font-size: 0.9rem; flex-shrink: 0;
                }
                .form-hint {
                    font-size: 0.78rem;
                    color: rgba(255,255,255,0.4);
                    margin-top: -0.2rem;
                    text-align: left;
                    position: relative !important;
                    display: block !important;
                }

                /* Switch-Profile bubble — fade transition */
                #profiles-floating-bubble {
                    transition: opacity 0.15s ease;
                }
                #profiles-floating-bubble.profiles-bubble-hiding {
                    opacity: 0 !important;
                    pointer-events: none;
                }
                /* Fallback bubble — fade transition (defers all positioning to ID rule above) */
                .profiles-floating-fallback {
                    transition: opacity 0.15s ease;
                }
                .profiles-floating-fallback.profiles-bubble-hiding {
                    opacity: 0 !important;
                    pointer-events: none;
                }

                /* Keyframe Animations */
                .anim-fade-in {
                    animation: fadeIn 0.4s cubic-bezier(0.165, 0.84, 0.44, 1) forwards;
                }
                @keyframes fadeIn {
                    from { opacity: 0; transform: scale(0.96) translateY(10px); }
                    to { opacity: 1; transform: scale(1) translateY(0); }
                }

                /* Devices Dropdown Styles */
                /* Allowed Devices is the one hand-built control among native selects, and
                   it sat directly above two of them. It cannot become a <select> — it is
                   multi-select with checkboxes — so it borrows their exact appearance
                   instead, chevron included, rather than carrying a text arrow. */
                .devices-dropdown-trigger {
                    display: flex;
                    justify-content: space-between;
                    align-items: center;
                    background: rgba(255, 255, 255, 0.06) url("data:image/svg+xml;charset=utf-8,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='%23ffffff'%3E%3Cpath d='M7 10l5 5 5-5H7z'/%3E%3C/svg%3E") no-repeat right 12px center;
                    background-size: 20px;
                    border: 1px solid rgba(255,255,255,0.15);
                    border-radius: var(--jpf-r-md);
                    padding: 10px;
                    padding-right: 36px;
                    color: #fff;
                    font-size: 1rem;
                    cursor: pointer;
                    user-select: none;
                    transition: border-color 0.2s, box-shadow 0.2s;
                }
                .devices-dropdown-trigger:focus {
                    outline: none;
                    border-color: var(--jpf-accent) !important;
                    box-shadow: 0 0 10px var(--jpf-accent-a50) !important;
                }
                /* background-color, not background: the shorthand would reset the chevron
                   that now lives in this element's background-image. */
                .devices-dropdown-trigger:hover {
                    background-color: rgba(255,255,255,0.05);
                }
                .devices-dropdown-list {
                    background: #202020;
                    border: 1px solid rgba(255,255,255,0.15);
                    border-radius: var(--jpf-r-sm);
                    max-height: 250px;
                    overflow-y: auto;
                    box-shadow: 0 4px 15px rgba(0,0,0,0.5);
                }
                .device-dropdown-item {
                    display: flex;
                    align-items: center;
                    justify-content: space-between;
                    padding: 8px 12px;
                    border-bottom: 1px solid rgba(255,255,255,0.05);
                    transition: background 0.2s;
                }
                .device-dropdown-item:hover, .device-dropdown-item:focus-within {
                    background: rgba(255,255,255,0.03);
                }
                .device-delete-btn:focus {
                    outline: none;
                    background: rgba(255,107,107,0.2) !important;
                }
            `;
            document.head.appendChild(style);
        }
    };

    ProfilesPlugin.init();
})();
