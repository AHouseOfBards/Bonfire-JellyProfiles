using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Queries;
using Jellyfin.Profiles.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// The profile picker for apps that cannot run the switcher, served as the app's own
    /// home screen. A stopgap until each app has its own; Moonfin first.
    ///
    /// <para><b>How it looks from the couch.</b> The household signs into its main account
    /// as normal. The app's home shows one library, "Profiles", with a folder per profile.
    /// Opening a folder opens that profile. A profile with a PIN opens a keypad made of
    /// folders instead (1–9, 0, Delete, OK). From then on the app shows that profile's
    /// libraries, plus a "Switch profile" folder that goes back to the picker.</para>
    ///
    /// <para><b>How it works.</b> No lobby account and no extra password. The main account's
    /// own token is the key: Bonfire remembers, in memory, which profile that token is
    /// acting as. Picking one mints the profile's session server-side, exactly as the web
    /// switcher does, and keeps its token here; the app never sees it. Every later request
    /// on the main token has the token and the main account's id swapped for the profile's
    /// before Jellyfin sees it, so Jellyfin enforces the profile's libraries, ratings and
    /// watched state with its own code. The app is always told the main account's id, so
    /// nothing it stored goes stale; the server always sees the profile's.</para>
    ///
    /// <para><b>Why opening a folder is the trigger.</b> It is the one action every app has
    /// and does on purpose. Moonfin loads a folder's children only when it is opened, never
    /// while scrolling past (it prefetches images on focus, nothing else). It does load
    /// them twice per open, which is why every tile id encodes its whole state — a keypad
    /// tile is "profile P, with 12 typed, key 3" — so the second load is a repeat, not a
    /// second key press.</para>
    ///
    /// <para><b>The live connection.</b> Moonfin drops watched-state messages whose UserId
    /// is not the signed-in user's (<c>user_data_sync.dart</c>). The server writes the
    /// profile's id; the app knows the main account's. So the socket is accepted here (see
    /// <see cref="ProxyWebSocketFeature"/>) and the profile's id rewritten in every text
    /// message. On every switch the sockets are closed; the app reconnects within a second
    /// or two and gets the new profile's.</para>
    ///
    /// <para><b>A restart</b> forgets every pick, and the app is back at the picker on its
    /// next request. The profile sessions minted before it are left to expire, and are
    /// replaced the next time the same profile is picked on that device.</para>
    ///
    /// <para><b>What this is not.</b> A security boundary for the main account: whoever holds
    /// its token could sign into it anywhere. The PIN keypad, its throttle, the device list
    /// and a disabled account are what guard a profile, the same as on every other path.</para>
    /// </summary>
    public static class ProxySwitcher
    {
        private static readonly ConcurrentDictionary<string, ProxySession> Sessions = new(StringComparer.Ordinal);

        /// <summary>Tokens looked at and found not to be ours, until when. Saves a database read per request.</summary>
        private static readonly ConcurrentDictionary<string, DateTime> NotOurs = new(StringComparer.Ordinal);

        private static readonly TimeSpan NotOursFor = TimeSpan.FromMinutes(5);

        /// <summary>How long a wrong PIN's result is reused for the app's second load of the same OK tile.</summary>
        private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(10);

        private const int MaxPinLength = 8;

        // ── Entry point ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Answers or rewrites a request from an app on the picker. False means "not ours":
        /// the request has not been touched and the caller carries on as normal.
        /// </summary>
        public static async Task<bool> TryHandleAsync(HttpContext context, RequestDelegate next, ILogger logger)
        {
            if (Plugin.IsPanicDisabled) return false;
            if (!ProxyClients.AnyEnabled(Plugin.Instance?.Configuration)) return false;

            var forwarded = false;
            Task Forward()
            {
                forwarded = true;
                return next(context);
            }

            try
            {
                return await HandleAsync(context, Forward, logger).ConfigureAwait(false);
            }
            catch (Exception ex) when (!forwarded && ex is not OperationCanceledException && !context.Response.HasStarted)
            {
                // Our own logic failed before Jellyfin saw the request. The app gets what it
                // would have got without us, rather than an error.
                logger.LogWarning(ex, "ProfilesPlugin: the profile picker could not handle {Path}; passing it on.", context.Request.Path);
                return false;
            }
        }

        /// <summary>Drops the "not ours" memory, so an app ticked a moment ago is picked up on its next request.</summary>
        public static void ForgetUnproxied() => NotOurs.Clear();

        /// <summary>For harnesses: forget everything.</summary>
        internal static void Reset()
        {
            Sessions.Clear();
            NotOurs.Clear();
        }

        private static async Task<bool> HandleAsync(HttpContext context, Func<Task> forward, ILogger logger)
        {
            var request = context.Request;

            // This runs on every request on the server once any app is ticked, video
            // segments included. Everything before a session is found has to be cheap: a
            // substring test on a header, then one dictionary lookup. No configuration copy
            // (that takes the global lock), no header scan, no database.
            var auth = request.Headers[HeaderNames.Authorization].ToString();
            var emby = request.Headers["X-Emby-Authorization"].ToString();
            var named = auth.Length > 0 || emby.Length > 0;
            var live = Plugin.Instance?.Configuration;
            if (named && !ProxyClients.MayBe(live, auth, emby)) return false;

            var token = ReadToken(request);
            if (string.IsNullOrEmpty(token)) return false;

            var services = new Services(context.RequestServices);

            // A request that does not name its app (an image, a stream) can only be one of a
            // session already known. Only a request naming a ticked app can start one.
            if (!Sessions.TryGetValue(token, out var session))
            {
                if (!named) return false;
                session = FindOrRegister(token, request, Controllers.ProfilesBaseController.ReadConfigSnapshot(), services, logger);
                if (session == null) return false;
            }

            // Unticked since: behave as today. The list is replaced, never edited, when the
            // settings are saved, so reading it here without the lock is safe.
            if (!ProxyClients.IsEnabled(live, session.Client))
            {
                await LeaveAsync(session, services, logger, signOutMaster: false).ConfigureAwait(false);
                Sessions.TryRemove(token, out _);
                return false;
            }

            var route = Route.Classify(request, BaseUrl(services));

            // Only the routes the picker answers itself need the configuration.
            PluginConfiguration? config = null;
            if (route.Kind != RouteKind.Other && route.Kind != RouteKind.Socket)
            {
                config = Controllers.ProfilesBaseController.ReadConfigSnapshot();

                // The main account's last profile was deleted: nothing to pick.
                if (!HasProfiles(config, session.MasterId))
                {
                    await LeaveAsync(session, services, logger, signOutMaster: false).ConfigureAwait(false);
                    Sessions.TryRemove(token, out _);
                    return false;
                }
            }

            // The app is signing out. The main account's token goes as the app asked; the
            // profile's goes with it, so nothing is left signed in that the app cannot see.
            if (route.Kind == RouteKind.Logout)
            {
                await LeaveAsync(session, services, logger, signOutMaster: false).ConfigureAwait(false);
                Sessions.TryRemove(token, out _);
                return false;
            }

            // Synthetic items are ours in every state: an app keeps the picker's folders on
            // its back stack after a profile is opened.
            if (route.Kind == RouteKind.Children && route.ParentId is Guid parent && session.Tiles.TryGetValue(parent, out var opened))
            {
                await OpenAsync(context, session, opened, config, services, logger, forward).ConfigureAwait(false);
                return true;
            }

            if (route.Kind == RouteKind.Item && route.ItemId is Guid itemId && session.Tiles.TryGetValue(itemId, out var tile))
            {
                await WriteJsonAsync(context, ItemDto(session, tile, services, config)).ConfigureAwait(false);
                return true;
            }

            if (route.Kind == RouteKind.Image && route.ItemId is Guid imageId && session.Tiles.TryGetValue(imageId, out var pictured))
            {
                if (pictured.Kind != TileKind.Profile || !HasImage(config, pictured.ProfileId))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return true;
                }

                // Served by Bonfire's own image route, which is anonymous, so no swap.
                request.Path = new PathString(route.Prefix + "/plugins/profiles/image/" + pictured.ProfileId.ToString("N"));
                request.QueryString = QueryString.Empty;
                await forward().ConfigureAwait(false);
                return true;
            }

            var acting = session.Acting;
            if (acting == null)
            {
                return await AnswerPickerAsync(context, session, route, config, services, forward).ConfigureAwait(false);
            }

            // Closed for maintenance. The gate in front of us saw the main account's token,
            // which got this far, so it is an administrator's; the profile it is acting as is
            // judged here, on the gate's own rules. Refused, the app goes back to the picker
            // rather than being handed 503s it has no way out of: the picker is how it would
            // switch, and the picker will not open the profile again while closed. A segment
            // of something already playing is let through as the gate would.
            var closed = MaintenanceGate.Active();
            if (closed != null
                && acting.ProfileId != session.MasterId
                && services.Users != null
                && !MaintenanceGate.IsAdmin(services.Users, acting.ProfileId)
                && MaintenanceGate.Refuses(closed, request.Method, request.Path.Value ?? string.Empty, acting.ProfileId))
            {
                logger.LogInformation(
                    "ProfilesPlugin: closed for maintenance; {Client} on {Device} is back at the picker.",
                    session.Client, session.Device);
                await LeaveAsync(session, services, logger, signOutMaster: false).ConfigureAwait(false);
                config ??= Controllers.ProfilesBaseController.ReadConfigSnapshot();
                return await AnswerPickerAsync(context, session, route, config, services, forward).ConfigureAwait(false);
            }

            // Acting as a profile: the request goes to Jellyfin as that profile.
            Swap(context, session, acting);

            switch (route.Kind)
            {
                case RouteKind.Views:
                    await ForwardAndEditAsync(context, forward, body => AppendSwitchView(body, session, services, config)).ConfigureAwait(false);
                    return true;

                case RouteKind.Me:
                case RouteKind.User when route.UserId == session.MasterId:
                    await ForwardAndEditAsync(context, forward, body => PresentAsMaster(body, session, acting, config)).ConfigureAwait(false);
                    return true;

                case RouteKind.Socket:
                    InstallSocket(context, session, acting);
                    await forward().ConfigureAwait(false);
                    return true;

                default:
                    await forward().ConfigureAwait(false);
                    await DropIfRevokedAsync(context, session, acting, services, logger).ConfigureAwait(false);
                    return true;
            }
        }

        /// <summary>
        /// The profile's token was refused: an administrator signed it out, or the profile was
        /// deleted. The app has had one 401 and may sign out, which we cannot take back; the
        /// next request at least finds the picker instead of a dead profile.
        /// </summary>
        private static async Task DropIfRevokedAsync(HttpContext context, ProxySession session, ActingAs acting, Services services, ILogger logger)
        {
            if (context.Response.StatusCode != StatusCodes.Status401Unauthorized) return;
            if (!ReferenceEquals(session.Acting, acting) || acting.Token == session.MasterToken) return;

            logger.LogInformation(
                "ProfilesPlugin: the picker's session for profile {ProfileId} was refused by Jellyfin; back to the picker.",
                acting.ProfileId);
            await LeaveAsync(session, services, logger, signOutMaster: false).ConfigureAwait(false);
        }

        // ── Sessions ────────────────────────────────────────────────────────────────

        private sealed class ProxySession
        {
            public string MasterToken = string.Empty;
            public Guid MasterId;
            public string Client = string.Empty;
            public string Device = string.Empty;
            public string DeviceId = string.Empty;
            public string Version = string.Empty;
            public readonly byte[] Secret = RandomNumberGenerator.GetBytes(16);
            public readonly SemaphoreSlim Gate = new(1, 1);
            public readonly ConcurrentDictionary<Guid, Tile> Tiles = new();
            public readonly ConcurrentDictionary<RewritingWebSocket, byte> Sockets = new();
            public volatile ActingAs? Acting;
            public Guid LastWrongOk;
            public DateTime LastWrongOkAt;
        }

        /// <summary>The profile a session is in. A record so it can be swapped in one write.</summary>
        private sealed record ActingAs(Guid ProfileId, string Token);

        private static ProxySession? FindOrRegister(
            string token, HttpRequest request, PluginConfiguration? config, Services services, ILogger logger)
        {
            if (Sessions.TryGetValue(token, out var known)) return known;

            var now = DateTime.UtcNow;
            if (NotOurs.TryGetValue(token, out var until) && until > now) return null;

            // An app that names itself and is not ticked costs nothing more.
            var auth = request.Headers[HeaderNames.Authorization];
            var emby = request.Headers["X-Emby-Authorization"];
            var client = Controllers.ProfilesBaseController.ParseAuthorizationParameter(auth, emby, "Client");
            if (!string.IsNullOrEmpty(client) && !ProxyClients.IsEnabled(config, client)) return null;

            if (services.Devices == null) return null;

            var device = services.Devices.GetDevices(new DeviceQuery { AccessToken = token })?.Items?.FirstOrDefault();
            if (device == null
                || !ProxyClients.IsEnabled(config, device.AppName)
                || !HasProfiles(config, device.UserId))
            {
                if (NotOurs.Count > 10_000) NotOurs.Clear();
                NotOurs[token] = now + NotOursFor;
                return null;
            }

            var session = new ProxySession
            {
                MasterToken = token,
                MasterId = device.UserId,
                Client = device.AppName ?? client ?? string.Empty,
                Device = device.DeviceName ?? string.Empty,
                DeviceId = device.DeviceId ?? string.Empty,
                Version = device.AppVersion ?? string.Empty,
            };

            session = Sessions.GetOrAdd(token, session);
            logger.LogInformation(
                "ProfilesPlugin: {Client} on {Device} signed in as a main account; serving it the profile picker.",
                session.Client, session.Device);
            return session;
        }

        private static bool HasProfiles(PluginConfiguration? config, Guid masterId)
            => config?.Mappings != null
               && masterId != Guid.Empty
               && config.Mappings.Any(m => m.MasterUserId == masterId && m.ProfileUserId != masterId);

        // ── The picker ──────────────────────────────────────────────────────────────

        private enum TileKind
        {
            ProfilesView,
            SwitchView,
            Profile,
            Key,
            Notice,
        }

        /// <summary>
        /// One made-up folder. <see cref="Entered"/> and <see cref="Key"/> are the keypad's
        /// whole state, carried in the tile so that opening it twice means the same thing.
        /// </summary>
        private sealed record Tile(TileKind Kind, Guid Id, Guid ParentId, Guid ProfileId, string Entered, string Key, string Name);

        private static Tile MakeTile(ProxySession session, TileKind kind, Guid parentId, Guid profileId, string entered, string key, string name)
        {
            var meaning = string.Join('|', (int)kind, profileId.ToString("N"), entered, key, name);
            var hash = MD5.HashData(session.Secret.Concat(Encoding.UTF8.GetBytes(meaning)).ToArray());
            var tile = new Tile(kind, new Guid(hash), parentId, profileId, entered, key, name);

            if (session.Tiles.Count > 5_000) session.Tiles.Clear();
            session.Tiles[tile.Id] = tile;
            return tile;
        }

        private static Tile ProfilesView(ProxySession s) => MakeTile(s, TileKind.ProfilesView, Guid.Empty, Guid.Empty, string.Empty, string.Empty, "Profiles");

        private static Tile SwitchView(ProxySession s) => MakeTile(s, TileKind.SwitchView, Guid.Empty, Guid.Empty, string.Empty, string.Empty, "Switch profile");

        private static async Task<bool> AnswerPickerAsync(
            HttpContext context, ProxySession session, Route route, PluginConfiguration? config, Services services, Func<Task> forward)
        {
            switch (route.Kind)
            {
                case RouteKind.Views:
                    await WriteJsonAsync(context, Page(context, new[] { ItemDto(session, ProfilesView(session), services, config) })).ConfigureAwait(false);
                    return true;

                case RouteKind.Children:
                case RouteKind.EmptyList:
                    // Nothing of the main account's shows behind the picker: not its
                    // libraries, not its Continue Watching.
                    await WriteJsonAsync(context, Page(context, Array.Empty<JsonObject>())).ConfigureAwait(false);
                    return true;

                case RouteKind.EmptyArray:
                    await WriteJsonAsync(context, new JsonArray()).ConfigureAwait(false);
                    return true;

                case RouteKind.EmptySearch:
                    await WriteJsonAsync(context, new JsonObject { ["SearchHints"] = new JsonArray(), ["TotalRecordCount"] = 0 }).ConfigureAwait(false);
                    return true;

                case RouteKind.Filters:
                    await WriteJsonAsync(context, new JsonObject
                    {
                        ["Genres"] = new JsonArray(),
                        ["Tags"] = new JsonArray(),
                        ["OfficialRatings"] = new JsonArray(),
                        ["Years"] = new JsonArray(),
                    }).ConfigureAwait(false);
                    return true;

                case RouteKind.Socket:
                    // Held so a pick can close it; nothing to rewrite while on the picker.
                    InstallSocket(context, session, null);
                    await forward().ConfigureAwait(false);
                    return true;

                default:
                    // Everything else is the main account's own business: server info,
                    // display preferences, its own user record.
                    return false;
            }
        }

        /// <summary>A folder was opened. This is where every pick, key press and switch happens.</summary>
        private static async Task OpenAsync(
            HttpContext context, ProxySession session, Tile tile, PluginConfiguration? config, Services services, ILogger logger, Func<Task> forward)
        {
            // One at a time per session: the app loads each folder twice, at once, and the
            // second must see what the first did rather than mint a second session.
            await session.Gate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            IReadOnlyList<Tile>? children;
            try
            {
                children = await DecideAsync(context, session, tile, config, services, logger).ConfigureAwait(false);
            }
            finally
            {
                session.Gate.Release();
            }

            if (children != null)
            {
                await WriteJsonAsync(context, Page(context, children.Select(t => ItemDto(session, t, services, config)))).ConfigureAwait(false);
                return;
            }

            // The profile is open: its folder shows its libraries, from Jellyfin, as it.
            var acting = session.Acting!;
            var route = Route.Classify(context.Request, BaseUrl(services));
            context.Request.Path = new PathString(route.Prefix + "/UserViews");
            context.Request.QueryString = QueryString.Create("userId", session.MasterId.ToString("N"));
            Swap(context, session, acting);
            await forward().ConfigureAwait(false);
        }

        /// <summary>The folder's children, or null when a profile is open and its libraries should be shown.</summary>
        private static async Task<IReadOnlyList<Tile>?> DecideAsync(
            HttpContext context, ProxySession session, Tile tile, PluginConfiguration? config, Services services, ILogger logger)
        {
            switch (tile.Kind)
            {
                case TileKind.ProfilesView:
                    return ProfileTiles(session, tile.Id, config, services);

                case TileKind.SwitchView:
                    await LeaveAsync(session, services, logger, signOutMaster: false).ConfigureAwait(false);
                    return ProfileTiles(session, tile.Id, config, services);

                case TileKind.Notice:
                    return tile.ProfileId != Guid.Empty
                        ? Keypad(session, tile.ProfileId, string.Empty, tile.ParentId)
                        : ProfileTiles(session, ProfilesView(session).Id, config, services);

                case TileKind.Profile:
                    if (session.Acting?.ProfileId == tile.ProfileId) return null;
                    if (NeedsPin(context, session, tile.ProfileId, config, services))
                    {
                        return Keypad(session, tile.ProfileId, string.Empty, tile.Id);
                    }

                    return await EnterOrNoticeAsync(context, session, tile, config, services, logger).ConfigureAwait(false);

                case TileKind.Key:
                    if (session.Acting?.ProfileId == tile.ProfileId) return null;
                    return await PressAsync(context, session, tile, config, services, logger).ConfigureAwait(false);

                default:
                    return Array.Empty<Tile>();
            }
        }

        private static List<Tile> ProfileTiles(ProxySession session, Guid parentId, PluginConfiguration? config, Services services)
        {
            var tiles = new List<Tile>();
            if (config?.Mappings == null) return tiles;

            tiles.Add(MakeTile(session, TileKind.Profile, parentId, session.MasterId, string.Empty, string.Empty, DisplayName(config, session.MasterId, services)));

            foreach (var m in config.Mappings.Where(m => m.MasterUserId == session.MasterId && m.ProfileUserId != session.MasterId))
            {
                tiles.Add(MakeTile(session, TileKind.Profile, parentId, m.ProfileUserId, string.Empty, string.Empty, DisplayName(config, m.ProfileUserId, services)));
            }

            return tiles;
        }

        private static string DisplayName(PluginConfiguration? config, Guid userId, Services services)
        {
            var row = config?.Mappings?.FirstOrDefault(m => m.ProfileUserId == userId && !string.IsNullOrWhiteSpace(m.ProfileName));
            if (row != null) return row.ProfileName;
            return services.Users?.GetUserById(userId)?.Username ?? "Profile";
        }

        private static bool NeedsPin(HttpContext context, ProxySession session, Guid profileId, PluginConfiguration? config, Services services)
        {
            // The main account has already given its password to get here.
            if (profileId == session.MasterId) return false;

            var mapping = config?.Mappings?.FirstOrDefault(m => m.ProfileUserId == profileId && m.MasterUserId == session.MasterId);
            if (mapping == null || string.IsNullOrEmpty(mapping.PinHash)) return false;

            return !(mapping.BypassPinOnLocalNetwork && IsLocal(context, services));
        }

        private static List<Tile> Keypad(ProxySession session, Guid profileId, string entered, Guid parentId, string? notice = null)
        {
            var tiles = new List<Tile>();
            if (notice != null)
            {
                tiles.Add(MakeTile(session, TileKind.Notice, parentId, profileId, string.Empty, "notice", notice));
            }

            foreach (var digit in "1234567890")
            {
                var d = digit.ToString();
                tiles.Add(MakeTile(session, TileKind.Key, parentId, profileId, entered, d, d));
            }

            tiles.Add(MakeTile(session, TileKind.Key, parentId, profileId, entered, "del", "Delete"));
            tiles.Add(MakeTile(session, TileKind.Key, parentId, profileId, entered, "ok", entered.Length == 0 ? "OK" : "OK " + new string('•', entered.Length)));
            return tiles;
        }

        private static async Task<IReadOnlyList<Tile>?> PressAsync(
            HttpContext context, ProxySession session, Tile key, PluginConfiguration? config, Services services, ILogger logger)
        {
            switch (key.Key)
            {
                case "del":
                    return Keypad(session, key.ProfileId, key.Entered.Length == 0 ? string.Empty : key.Entered[..^1], key.Id);

                case "ok":
                    break;

                default:
                    var entered = key.Entered.Length >= MaxPinLength ? key.Entered : key.Entered + key.Key;
                    return Keypad(session, key.ProfileId, entered, key.Id);
            }

            // The app's second load of the same OK tile. Counting it would spend two of the
            // five tries on one press.
            if (session.LastWrongOk == key.Id && DateTime.UtcNow - session.LastWrongOkAt < RepeatWindow)
            {
                return Keypad(session, key.ProfileId, string.Empty, key.Id, "Wrong PIN");
            }

            var mapping = config?.Mappings?.FirstOrDefault(m => m.ProfileUserId == key.ProfileId && m.MasterUserId == session.MasterId);
            if (mapping == null) return ProfileTiles(session, ProfilesView(session).Id, config, services);

            // The same allowance as typing the PIN into the app's own sign-in form: one
            // account, five tries per quarter hour and twenty a day, wherever they come from.
            if (!BonfirePinAuthenticationProvider.BeginPinAttempt(key.ProfileId))
            {
                logger.LogWarning("ProfilesPlugin: too many wrong PINs for profile {ProfileId} on the picker.", key.ProfileId);
                return Keypad(session, key.ProfileId, string.Empty, key.Id, "Too many tries. Try later.");
            }

            if (PinHasher.Verify(key.Entered, mapping.PinHash) != PinHasher.PinResult.Match)
            {
                session.LastWrongOk = key.Id;
                session.LastWrongOkAt = DateTime.UtcNow;
                return Keypad(session, key.ProfileId, string.Empty, key.Id, "Wrong PIN");
            }

            BonfirePinAuthenticationProvider.EndPinAttempt(key.ProfileId);
            var profileTile = MakeTile(session, TileKind.Profile, ProfilesView(session).Id, key.ProfileId, string.Empty, string.Empty, DisplayName(config, key.ProfileId, services));
            return await EnterOrNoticeAsync(context, session, profileTile, config, services, logger).ConfigureAwait(false);
        }

        /// <summary>Opens the profile, or says why not. Null means it is open.</summary>
        private static async Task<IReadOnlyList<Tile>?> EnterOrNoticeAsync(
            HttpContext context, ProxySession session, Tile profile, PluginConfiguration? config, Services services, ILogger logger)
        {
            var refusal = await EnterAsync(context, session, profile.ProfileId, config, services, logger).ConfigureAwait(false);
            if (refusal == null) return null;

            return new[] { MakeTile(session, TileKind.Notice, profile.Id, Guid.Empty, string.Empty, "notice", refusal) };
        }

        /// <summary>
        /// Makes the session act as <paramref name="profileId"/>. The same checks as the web
        /// switcher's <c>/switch</c>, in the same order. Returns null, or the message to show.
        /// </summary>
        private static async Task<string?> EnterAsync(
            HttpContext context, ProxySession session, Guid profileId, PluginConfiguration? config, Services services, ILogger logger)
        {
            if (profileId == session.MasterId)
            {
                await LeaveAsync(session, services, logger, signOutMaster: false).ConfigureAwait(false);
                session.Acting = new ActingAs(profileId, session.MasterToken);
                return null;
            }

            var mapping = config?.Mappings?.FirstOrDefault(m => m.ProfileUserId == profileId && m.MasterUserId == session.MasterId);
            if (mapping == null || services.Users == null || services.Sessions == null) return "Couldn't open this profile.";

            if (MaintenanceGate.Active() != null && !MaintenanceGate.IsAdmin(services.Users, profileId))
            {
                return "Closed for maintenance.";
            }

            var access = Controllers.ProfilesBaseController.EvaluateDeviceRestriction(
                mapping, session.DeviceId, config?.KnownDevices, config?.Mappings);
            if (access != Controllers.ProfilesBaseController.DeviceAccess.NotRestricted
                && access != Controllers.ProfilesBaseController.DeviceAccess.Allowed)
            {
                logger.LogWarning("ProfilesPlugin: the picker refused profile {ProfileId} on device {DeviceId} ({Reason}).", profileId, session.DeviceId, access);
                return "Not allowed on this device.";
            }

            var user = services.Users.GetUserById(profileId);
            if (user == null) return "Couldn't open this profile.";

            if (services.Users.GetUserDto(user, string.Empty).Policy?.IsDisabled == true)
            {
                return "This profile is turned off.";
            }

            if (services.Libraries != null
                && Controllers.ProfilesBaseController.ShouldInheritMasterPolicy(mapping, profileId, session.MasterId))
            {
                await ProfilePolicySync.ApplyAsync(services.Users, services.Libraries, logger, profileId).ConfigureAwait(false);
            }

            MediaBrowser.Controller.Authentication.AuthenticationResult result;
            try
            {
                result = await services.Sessions.AuthenticateDirect(new AuthenticationRequest
                {
                    // UserId, or SessionManager falls back to a name lookup (issue #15).
                    UserId = user.Id,
                    Username = user.Username,
                    RemoteEndPoint = context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1",
                    App = string.IsNullOrEmpty(session.Client) ? "Moonfin" : session.Client,
                    DeviceName = string.IsNullOrEmpty(session.Device) ? "Bonfire picker" : session.Device,
                    DeviceId = string.IsNullOrEmpty(session.DeviceId) ? "bonfire-picker-" + user.Id.ToString("N")[..8] : session.DeviceId,
                    AppVersion = string.IsNullOrEmpty(session.Version) ? "1.0.0" : session.Version,
                }).ConfigureAwait(false);
            }
            catch (System.Security.SecurityException ex)
            {
                logger.LogWarning(ex, "ProfilesPlugin: the picker could not open {User}.", user.Username);
                return ex.Message.Contains("device", StringComparison.OrdinalIgnoreCase)
                    ? "Not allowed on this device."
                    : "Too many sessions for this profile.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "ProfilesPlugin: the picker could not open {User}.", user.Username);
                return "Couldn't open this profile.";
            }

            await LeaveAsync(session, services, logger, signOutMaster: false).ConfigureAwait(false);
            session.Acting = new ActingAs(profileId, result.AccessToken);

            logger.LogInformation(
                "ProfilesPlugin: {Client} on {Device} opened profile {Profile} from the picker.",
                session.Client, session.Device, user.Username);
            return null;
        }

        /// <summary>Back to the picker: the profile's session ends and the live connection is closed.</summary>
        private static async Task LeaveAsync(ProxySession session, Services services, ILogger logger, bool signOutMaster)
        {
            var was = session.Acting;
            session.Acting = null;
            CloseSockets(session);

            if (was == null || was.Token == session.MasterToken || services.Sessions == null) return;

            try
            {
                await services.Sessions.Logout(was.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "ProfilesPlugin: the picker's profile session was already gone.");
            }
        }

        // ── Acting as a profile ─────────────────────────────────────────────────────

        /// <summary>
        /// Rewrites the request to come from the profile: its token wherever the app put the
        /// main account's, and its id wherever the app put the main account's.
        /// </summary>
        private static void Swap(HttpContext context, ProxySession session, ActingAs acting)
        {
            if (acting.Token == session.MasterToken) return;

            var request = context.Request;
            foreach (var name in new[] { HeaderNames.Authorization, "X-Emby-Authorization", "X-Emby-Token", "X-MediaBrowser-Token" })
            {
                if (request.Headers.TryGetValue(name, out var value) && !StringValues.IsNullOrEmpty(value))
                {
                    request.Headers[name] = value.ToString().Replace(session.MasterToken, acting.Token, StringComparison.Ordinal);
                }
            }

            var path = request.Path.Value ?? string.Empty;
            var swappedPath = SwapIds(path, session.MasterId, acting.ProfileId);
            if (!ReferenceEquals(path, swappedPath)) request.Path = new PathString(swappedPath);

            var query = request.QueryString.Value;
            if (!string.IsNullOrEmpty(query))
            {
                var swapped = SwapIds(query.Replace(session.MasterToken, acting.Token, StringComparison.Ordinal), session.MasterId, acting.ProfileId);
                if (!string.Equals(query, swapped, StringComparison.Ordinal)) request.QueryString = new QueryString(swapped);
            }
        }

        /// <summary>Both spellings of a Jellyfin id, any case. Returns the same instance when nothing changed.</summary>
        internal static string SwapIds(string text, Guid from, Guid to)
        {
            if (string.IsNullOrEmpty(text) || from == to) return text;

            var result = text;
            result = ReplaceIgnoreCase(result, from.ToString("N"), to.ToString("N"));
            result = ReplaceIgnoreCase(result, from.ToString("D"), to.ToString("D"));
            return result;
        }

        private static string ReplaceIgnoreCase(string text, string from, string to)
            => text.Contains(from, StringComparison.OrdinalIgnoreCase)
                ? text.Replace(from, to, StringComparison.OrdinalIgnoreCase)
                : text;

        private static byte[]? AppendSwitchView(byte[] body, ProxySession session, Services services, PluginConfiguration? config)
        {
            if (JsonNode.Parse(body) is not JsonObject root) return null;

            var key = root.ContainsKey("Items") ? "Items" : root.ContainsKey("items") ? "items" : null;
            if (key == null || root[key] is not JsonArray items) return null;

            var camel = key == "items";
            var dto = ItemDto(session, SwitchView(session), services, config);
            items.Add(camel ? Camel(dto) : dto);

            var countKey = camel ? "totalRecordCount" : "TotalRecordCount";
            if (root[countKey] is JsonValue count && count.TryGetValue<int>(out var n)) root[countKey] = n + 1;

            return Encoding.UTF8.GetBytes(root.ToJsonString());
        }

        /// <summary>The profile's own record, with the main account's id and the profile's own name.</summary>
        private static byte[]? PresentAsMaster(byte[] body, ProxySession session, ActingAs acting, PluginConfiguration? config)
        {
            if (acting.ProfileId == session.MasterId) return null;

            var text = Encoding.UTF8.GetString(body);
            var rewritten = SwapIds(text, acting.ProfileId, session.MasterId);
            if (JsonNode.Parse(rewritten) is not JsonObject root) return null;

            var friendly = ProfileNameRewriter.FriendlyNameFor(config, acting.ProfileId);
            if (friendly != null)
            {
                if (root.ContainsKey("Name")) root["Name"] = friendly;
                else if (root.ContainsKey("name")) root["name"] = friendly;
            }

            return Encoding.UTF8.GetBytes(root.ToJsonString());
        }

        // ── The live connection ─────────────────────────────────────────────────────

        private static void InstallSocket(HttpContext context, ProxySession session, ActingAs? acting)
        {
            var upgrade = context.Features.Get<IHttpUpgradeFeature>();
            if (upgrade == null || !upgrade.IsUpgradableRequest) return;

            Func<string, string> rewrite = acting == null || acting.ProfileId == session.MasterId
                ? text => text
                : text => SwapIds(text, acting.ProfileId, session.MasterId);

            context.Features.Set<IHttpWebSocketFeature>(new ProxyWebSocketFeature(context, upgrade, inner =>
            {
                var socket = new RewritingWebSocket(inner, rewrite, s => session.Sockets.TryRemove(s, out _));
                session.Sockets[socket] = 0;
                return socket;
            }));
        }

        private static void CloseSockets(ProxySession session)
        {
            foreach (var socket in session.Sockets.Keys)
            {
                try
                {
                    socket.Abort();
                }
                catch (Exception)
                {
                    // Already gone.
                }

                session.Sockets.TryRemove(socket, out _);
            }
        }

        // ── Building answers ────────────────────────────────────────────────────────

        private static JsonObject ItemDto(ProxySession session, Tile tile, Services services, PluginConfiguration? config)
        {
            var isView = tile.Kind is TileKind.ProfilesView or TileKind.SwitchView;
            var id = tile.Id.ToString("N");

            var dto = new JsonObject
            {
                ["Name"] = tile.Name,
                ["SortName"] = tile.Name,
                ["ServerId"] = services.ServerId,
                ["Id"] = id,
                ["Type"] = isView ? "CollectionFolder" : "Folder",
                ["IsFolder"] = true,
                ["ChildCount"] = 1,
                ["PrimaryImageAspectRatio"] = 1.0,
                ["BackdropImageTags"] = new JsonArray(),
                ["UserData"] = new JsonObject
                {
                    ["PlaybackPositionTicks"] = 0,
                    ["PlayCount"] = 0,
                    ["IsFavorite"] = false,
                    ["Played"] = false,
                    ["Key"] = id,
                    ["ItemId"] = id,
                },
            };

            if (tile.ParentId != Guid.Empty) dto["ParentId"] = tile.ParentId.ToString("N");

            var tags = new JsonObject();
            if (tile.Kind == TileKind.Profile && HasImage(config, tile.ProfileId))
            {
                var image = config!.Mappings.First(m => m.ProfileUserId == tile.ProfileId).ProfileImage ?? string.Empty;
                tags["Primary"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(image))).ToLowerInvariant();
            }

            dto["ImageTags"] = tags;
            return dto;
        }

        private static bool HasImage(PluginConfiguration? config, Guid profileId)
        {
            var image = config?.Mappings?.FirstOrDefault(m => m.ProfileUserId == profileId)?.ProfileImage;

            // An image hosted elsewhere is not served by Bonfire's image route.
            return !string.IsNullOrEmpty(image) && !image.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        }

        private static JsonObject Page(HttpContext context, IEnumerable<JsonObject> all)
        {
            var list = all.ToList();
            var start = ReadInt(context.Request, "StartIndex") ?? 0;
            var limit = ReadInt(context.Request, "Limit") ?? int.MaxValue;
            var page = new JsonArray();
            foreach (var item in list.Skip(Math.Max(0, start)).Take(Math.Max(0, limit))) page.Add(item);

            return new JsonObject
            {
                ["Items"] = page,
                ["TotalRecordCount"] = list.Count,
                ["StartIndex"] = start,
            };
        }

        private static int? ReadInt(HttpRequest request, string name)
            => int.TryParse(request.Query[name].ToString(), out var n) ? n : null;

        private static async Task WriteJsonAsync(HttpContext context, JsonNode body)
        {
            var camel = context.Request.Headers[HeaderNames.Accept].ToString().Contains("CamelCase", StringComparison.OrdinalIgnoreCase);
            var bytes = Encoding.UTF8.GetBytes((camel ? Camel(body) : body).ToJsonString());

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength = bytes.Length;
            context.Response.Headers[HeaderNames.CacheControl] = "no-cache, no-store, must-revalidate";
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        }

        /// <summary>Jellyfin answers camelCase when the Accept header asks for it; so do we.</summary>
        private static JsonNode Camel(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    var copy = new JsonObject();
                    foreach (var (key, value) in obj)
                    {
                        var name = key.Length == 0 ? key : char.ToLowerInvariant(key[0]) + key[1..];
                        copy[name] = value == null ? null : Camel(value.DeepClone());
                    }

                    return copy;
                case JsonArray array:
                    return new JsonArray(array.Select(v => v == null ? null : Camel(v.DeepClone())).ToArray());
                default:
                    return node.DeepClone();
            }
        }

        /// <summary>
        /// Passes the request on, then hands the body to <paramref name="edit"/>. Jellyfin's
        /// own bytes go back whenever the edit returns null or anything about the answer is
        /// not a plain JSON 200.
        /// </summary>
        private static async Task ForwardAndEditAsync(HttpContext context, Func<Task> forward, Func<byte[], byte[]?> edit)
        {
            var original = context.Features.Get<IHttpResponseBodyFeature>();
            if (original == null)
            {
                await forward().ConfigureAwait(false);
                return;
            }

            var encoding = context.Request.Headers[HeaderNames.AcceptEncoding];
            context.Request.Headers.Remove(HeaderNames.AcceptEncoding);
            var ifNoneMatch = context.Request.Headers[HeaderNames.IfNoneMatch];
            context.Request.Headers.Remove(HeaderNames.IfNoneMatch);

            byte[] produced;
            using (var captured = new MemoryStream())
            {
                var capture = new StreamResponseBodyFeature(captured);
                context.Features.Set<IHttpResponseBodyFeature>(capture);
                try
                {
                    await forward().ConfigureAwait(false);
                    await capture.CompleteAsync().ConfigureAwait(false);
                }
                finally
                {
                    context.Features.Set(original);
                    if (!StringValues.IsNullOrEmpty(encoding)) context.Request.Headers[HeaderNames.AcceptEncoding] = encoding;
                    if (!StringValues.IsNullOrEmpty(ifNoneMatch)) context.Request.Headers[HeaderNames.IfNoneMatch] = ifNoneMatch;
                }

                produced = captured.ToArray();
            }

            if (context.Response.HasStarted) return;

            byte[]? edited = null;
            if (context.Response.StatusCode == StatusCodes.Status200OK
                && !context.Response.Headers.ContainsKey(HeaderNames.ContentEncoding)
                && produced.Length > 0)
            {
                try
                {
                    edited = edit(produced);
                }
                catch (Exception)
                {
                    edited = null;
                }
            }

            var bytes = edited ?? produced;
            if (edited != null)
            {
                context.Response.ContentLength = bytes.Length;
                context.Response.Headers.Remove(HeaderNames.ETag);
                context.Response.Headers[HeaderNames.CacheControl] = "no-cache, no-store, must-revalidate";
            }

            if (bytes.Length > 0) await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        }

        // ── Request plumbing ────────────────────────────────────────────────────────

        /// <summary>Every place Jellyfin accepts a token, in the order it reads them.</summary>
        internal static string? ReadToken(HttpRequest request)
        {
            var token = Controllers.ProfilesBaseController.ParseAuthorizationParameter(
                request.Headers[HeaderNames.Authorization], request.Headers["X-Emby-Authorization"], "Token");
            if (!string.IsNullOrEmpty(token)) return token;

            foreach (var header in new[] { "X-Emby-Token", "X-MediaBrowser-Token" })
            {
                var value = request.Headers[header].ToString();
                if (!string.IsNullOrEmpty(value)) return value;
            }

            foreach (var key in new[] { "ApiKey", "api_key" })
            {
                var value = request.Query[key].ToString();
                if (!string.IsNullOrEmpty(value)) return value;
            }

            return null;
        }

        private static bool IsLocal(HttpContext context, Services services)
        {
            try
            {
                var ip = context.Connection.RemoteIpAddress;
                return ip != null && services.Network != null && services.Network.IsInLocalNetwork(ip);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string? BaseUrl(Services services)
        {
            try
            {
                return services.ServerConfig?.GetNetworkConfiguration().BaseUrl;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The server's services, looked up per request so the middleware's signature stays as it is.</summary>
        private sealed class Services
        {
            private readonly IServiceProvider? _provider;

            public Services(IServiceProvider? provider) => _provider = provider;

            public IUserManager? Users => Get<IUserManager>();

            public IDeviceManager? Devices => Get<IDeviceManager>();

            public ISessionManager? Sessions => Get<ISessionManager>();

            public ILibraryManager? Libraries => Get<ILibraryManager>();

            public INetworkManager? Network => Get<INetworkManager>();

            public IServerConfigurationManager? ServerConfig => Get<IServerConfigurationManager>();

            public string ServerId => Get<IServerApplicationHost>()?.SystemId ?? string.Empty;

            private T? Get<T>()
                where T : class
                => _provider?.GetService(typeof(T)) as T;
        }

        // ── Routes ──────────────────────────────────────────────────────────────────

        internal enum RouteKind
        {
            Other,
            Views,
            Children,
            Item,
            Image,
            Me,
            User,
            EmptyList,
            EmptyArray,
            EmptySearch,
            Filters,
            Socket,
            Logout,
        }

        /// <summary>What a request asks for, as far as the picker cares.</summary>
        internal sealed class Route
        {
            private static readonly HashSet<string> EmptyLists = new(StringComparer.OrdinalIgnoreCase)
            {
                "useritems/resume", "shows/nextup", "shows/upcoming", "genres", "musicgenres", "studios",
                "persons", "artists", "artists/albumartists", "items/suggestions", "channels", "livetv/channels",
                "livetv/programs", "livetv/programs/recommended", "livetv/recordings", "trailers", "playlists",
            };

            public RouteKind Kind { get; private set; }

            /// <summary>Everything before Jellyfin's own route: the base URL, if any.</summary>
            public string Prefix { get; private set; } = string.Empty;

            public Guid? ParentId { get; private set; }

            public Guid? ItemId { get; private set; }

            public Guid? UserId { get; private set; }

            public static Route Classify(HttpRequest request, string? baseUrl)
            {
                var route = new Route();
                var path = request.Path.Value ?? string.Empty;

                var prefix = string.IsNullOrEmpty(baseUrl) ? string.Empty : "/" + baseUrl.Trim('/');
                if (prefix.Length > 1 && path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                {
                    route.Prefix = path[..prefix.Length];
                    path = path[prefix.Length..];
                }

                var s = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var get = HttpMethods.IsGet(request.Method);
                var rel = string.Join('/', s);

                bool Is(int i, string text) => s.Length > i && string.Equals(s[i], text, StringComparison.OrdinalIgnoreCase);
                Guid? Id(int i) => s.Length > i && Guid.TryParse(s[i], out var g) ? g : null;

                if (HttpMethods.IsPost(request.Method) && s.Length == 2 && Is(0, "Sessions") && Is(1, "Logout"))
                {
                    route.Kind = RouteKind.Logout;
                    return route;
                }

                if (!get) return route;

                if (s.Length == 1 && (Is(0, "socket") || Is(0, "embywebsocket")))
                {
                    route.Kind = RouteKind.Socket;
                    return route;
                }

                // /Users/{id}/... becomes the same shape as the route without a user.
                var offset = 0;
                if (Is(0, "Users") && s.Length >= 2)
                {
                    if (s.Length == 2)
                    {
                        if (Is(1, "Me")) route.Kind = RouteKind.Me;
                        else if (Id(1) is Guid u)
                        {
                            route.Kind = RouteKind.User;
                            route.UserId = u;
                        }

                        return route;
                    }

                    route.UserId = Id(1);
                    offset = 2;
                    if (s.Length == 3 && Is(2, "Views"))
                    {
                        route.Kind = RouteKind.Views;
                        return route;
                    }
                }

                var tail = s.Skip(offset).ToArray();
                var tailText = string.Join('/', tail);

                if (offset == 0 && tail.Length == 1 && Is(0, "UserViews"))
                {
                    route.Kind = RouteKind.Views;
                    return route;
                }

                if (tail.Length >= 1 && string.Equals(tail[0], "Items", StringComparison.OrdinalIgnoreCase))
                {
                    if (tail.Length == 1)
                    {
                        route.Kind = RouteKind.Children;
                        route.ParentId = Guid.TryParse(request.Query["ParentId"].ToString(), out var p) ? p : null;
                        return route;
                    }

                    var second = tail[1];
                    if (string.Equals(second, "Latest", StringComparison.OrdinalIgnoreCase))
                    {
                        route.Kind = RouteKind.EmptyArray;
                        return route;
                    }

                    if (string.Equals(second, "Resume", StringComparison.OrdinalIgnoreCase))
                    {
                        route.Kind = RouteKind.EmptyList;
                        return route;
                    }

                    if (string.Equals(second, "Filters", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(second, "Filters2", StringComparison.OrdinalIgnoreCase))
                    {
                        route.Kind = RouteKind.Filters;
                        return route;
                    }

                    if (Guid.TryParse(second, out var item))
                    {
                        route.ItemId = item;
                        if (tail.Length == 2) route.Kind = RouteKind.Item;
                        else if (string.Equals(tail[2], "Images", StringComparison.OrdinalIgnoreCase)) route.Kind = RouteKind.Image;
                        return route;
                    }
                }

                if (offset == 0)
                {
                    if (EmptyLists.Contains(rel) || EmptyLists.Contains(tailText))
                    {
                        route.Kind = RouteKind.EmptyList;
                    }
                    else if (string.Equals(rel, "Movies/Recommendations", StringComparison.OrdinalIgnoreCase))
                    {
                        route.Kind = RouteKind.EmptyArray;
                    }
                    else if (string.Equals(rel, "Search/Hints", StringComparison.OrdinalIgnoreCase))
                    {
                        route.Kind = RouteKind.EmptySearch;
                    }
                }

                return route;
            }
        }
    }
}
