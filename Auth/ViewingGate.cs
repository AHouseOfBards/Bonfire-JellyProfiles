using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Mail;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Viewing hours and daily limits, at the door (1.7.2): a limited profile outside its
    /// hours or over its limit is refused new video with 403 and an <c>X-Bonfire-Error</c> of
    /// <see cref="OutsideHours"/> or <see cref="LimitReached"/>. Browsing is never refused.
    ///
    /// <para><b>What is refused:</b> PlaybackInfo for a video, which is how every Jellyfin app
    /// starts one, and a video stream of anything the profile is not already playing, which is how
    /// an app that builds its own URL would. "Playing" is what this gate let start, kept in
    /// <see cref="PlaybackRecord"/>; a session's own NowPlayingItem is the client's word and proves
    /// nothing (R-1297). A stream of what is playing is let through until the timer's grace is
    /// over (<see cref="ViewingTimer"/>), then refused, for an app that ignored the stop. Progress
    /// and stop reports always go through.</para>
    ///
    /// <para><b>Cost:</b> one dictionary count per request while no profile is limited. Otherwise
    /// a path check, and a token lookup (remembered 30 seconds) only on the requests above.</para>
    ///
    /// <para><b>Not a security boundary</b>, as with <see cref="MaintenanceGate"/>: an app that
    /// reports progress on an item it is not playing can make that item "playing". It keeps an
    /// honest app to the household's rules.</para>
    /// </summary>
    public static class ViewingGate
    {
        public const string OutsideHours = "outside_viewing_hours";
        public const string LimitReached = "daily_limit_reached";

        private static readonly TimeSpan MessageQuiet = TimeSpan.FromMinutes(1);

        internal enum Kind
        {
            /// <summary>Not playback, or audio: never refused here. That includes a film's
            /// soundtrack asked for as <c>/Audio/{id}/stream</c> (R-1028); documented, not gated,
            /// because audio is not what the limits are for.</summary>
            None,

            /// <summary><c>/Items/{id}/PlaybackInfo</c>.</summary>
            PlaybackInfo,

            /// <summary><c>/Videos/{id}/…</c>.</summary>
            VideoStream,

            /// <summary><c>/Items/{id}/Download</c> and <c>/Items/{id}/File</c>: the whole original file.</summary>
            Download,
        }

        internal static Kind Classify(string path)
        {
            if (string.IsNullOrEmpty(path)) return Kind.None;
            // ASP.NET routes "/PlaybackInfo/" to the same action but the endswith checks below
            // did not, so a trailing slash classified as None and walked past the gate (R-1020).
            path = path.TrimEnd('/');
            var at = path.IndexOf("/Videos/", StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                // Only an item's own routes; and not the two an item's page reads while browsing.
                var start = at + "/Videos/".Length;
                var end = path.IndexOf('/', start);
                if (!Guid.TryParse(end < 0 ? path[start..] : path[start..end], out _)) return Kind.None;
                // Exactly those two routes, by position: hls1 has an unused playlistId segment, so
                // /Videos/{id}/hls1/Trickplay/0.mp4 is a video stream, not trickplay (r2-06-01).
                var rest = end < 0 ? "" : path[(end + 1)..];
                if (rest.Equals("AdditionalParts", StringComparison.OrdinalIgnoreCase)
                    || rest.StartsWith("Trickplay/", StringComparison.OrdinalIgnoreCase)) return Kind.None;
                return Kind.VideoStream;
            }
            if (path.EndsWith("/PlaybackInfo", StringComparison.OrdinalIgnoreCase)
                && path.Contains("/Items/", StringComparison.OrdinalIgnoreCase)) return Kind.PlaybackInfo;
            // Jellyfin serves the whole original file from these two, so a profile outside its
            // hours could download a film and watch it offline; only /Videos and PlaybackInfo were
            // gated (R-1019, R-1296). Only an item's own two routes, with a real id: nothing an
            // item page reads while browsing ends this way.
            if ((path.EndsWith("/Download", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("/File", StringComparison.OrdinalIgnoreCase))
                && PlaybackInfoItem(path + "/") != null) return Kind.Download;
            return Kind.None;
        }

        /// <summary>The id in <c>/Items/{id}/PlaybackInfo</c>, or null.</summary>
        internal static Guid? PlaybackInfoItem(string path)
        {
            var at = path.IndexOf("/Items/", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            var start = at + "/Items/".Length;
            var end = path.IndexOf('/', start);
            return end > start && Guid.TryParse(path[start..end], out var id) ? id : null;
        }

        /// <summary>
        /// Records a PlaybackInfo or video stream the gates let through, which is what
        /// "already playing" means afterwards (<see cref="PlaybackRecord"/>, R-1297). Also
        /// called by the profile picker for the profile it is acting as.
        /// </summary>
        internal static void NoteAllowed(string path, Guid userId)
        {
            switch (Classify(path))
            {
                case Kind.PlaybackInfo:
                    var played = PlaybackInfoItem(path.TrimEnd('/') + "/");
                    if (played != null) PlaybackRecord.Note(userId, played.Value);
                    break;
                case Kind.VideoStream:
                    var streamed = MaintenanceGate.StreamItemId(path);
                    if (streamed != null) PlaybackRecord.Note(userId, streamed.Value);
                    break;
            }
        }

        /// <summary>True for something playing that counts: video, or anything not known to be audio.</summary>
        internal static bool IsVideo(BaseItemDto? item)
        {
            if (item == null) return false;
            return item.MediaType != Jellyfin.Data.Enums.MediaType.Audio
                && item.MediaType != Jellyfin.Data.Enums.MediaType.Book
                && item.MediaType != Jellyfin.Data.Enums.MediaType.Photo;
        }

        /// <summary>
        /// The refusal for a request by <paramref name="userId"/>, or null to let it through.
        /// Also used by the profile picker for the profile it is acting as.
        /// </summary>
        internal static (string Code, ViewingState State, ViewingTracker.Limited Limited)? Check(
            string path, Guid userId, ISessionManager? sessions, ILibraryManager? library, DateTime nowUtc)
        {
            var index = ViewingTracker.Index();
            if (index.Count == 0) return null;
            var kind = Classify(path);
            if (kind == Kind.None) return null;
            if (!index.TryGetValue(userId, out var limited)) return null;

            var state = ViewingTracker.State(limited, nowUtc);
            if (state.Allowed) return null;

            if (kind == Kind.PlaybackInfo || kind == Kind.Download)
            {
                // Music and books are not limited. An item that can't be found is judged as video.
                // A download is never "what is playing", so it gets no grace either.
                var id = PlaybackInfoItem(path.TrimEnd('/') + "/");
                if (id != null && library != null)
                {
                    var item = library.GetItemById(id.Value);
                    if (item != null && item.MediaType != Jellyfin.Data.Enums.MediaType.Video
                        && item.MediaType != Jellyfin.Data.Enums.MediaType.Unknown) return null;
                }

                // A player that changes audio track, subtitles or quality asks for PlaybackInfo of
                // the item it is already playing. Refusing that ended a stream the grace is meant
                // to let finish. Same rule as the stream itself.
                if (kind == Kind.PlaybackInfo && id != null && PlaybackRecord.Has(userId, id.Value))
                {
                    DateTime? playGrace;
                    lock (ViewingTracker.UsageLock) playGrace = ViewingTracker.EntryFor(userId).GraceEndsUtc;
                    // Only inside a grace that was set: with none there is nothing to finish, and
                    // every request let through would renew the record.
                    if (playGrace.HasValue && nowUtc < playGrace.Value.AddMinutes(1)) return null;
                }
            }
            else
            {
                DateTime? grace;
                lock (ViewingTracker.UsageLock) grace = ViewingTracker.EntryFor(userId).GraceEndsUtc;
                var graceOver = grace.HasValue && nowUtc >= grace.Value.AddMinutes(1);
                if (!graceOver && MaintenanceGate.ContinuesPlayback(path, userId, sessions)) return null;
            }

            return (state.Reason == ViewingClock.Limit ? LimitReached : OutsideHours, state, limited);
        }

        /// <summary>Answers the request with 403 if it is refused. False means it was not touched.</summary>
        public static async Task<bool> TryRefuseAsync(
            HttpContext context, IDeviceManager? devices, ISessionManager? sessions, ILogger logger)
        {
            var request = context.Request;
            var path = request.Path.Value ?? string.Empty;

            // The whole cost while nobody is limited, except a PlaybackInfo: noted so "already
            // playing" means something when a limit or maintenance starts later (see
            // PlaybackRecord). Once per playback, not per segment.
            var limitsActive = ViewingTracker.Index().Count > 0;
            var kind = limitsActive ? Classify(path) : (Classify(path) == Kind.PlaybackInfo ? Kind.PlaybackInfo : Kind.None);
            if (kind == Kind.None) return false;

            try
            {
                // Jellyfin's own reading of the request, not ours (R-1295; see RequestAuth).
                var userId = await RequestAuth.UserIdAsync(context, () =>
                {
                    var token = ProxySwitcher.ReadToken(request);
                    return string.IsNullOrEmpty(token) ? Guid.Empty : MaintenanceGate.UserOf(token, devices);
                }).ConfigureAwait(false);
                if (userId == Guid.Empty) return false;

                // Asked for here, not injected: only a refusal of PlaybackInfo needs it.
                var library = context.RequestServices?.GetService(typeof(ILibraryManager)) as ILibraryManager;
                var refusal = limitsActive ? Check(path, userId, sessions, library, ViewingTracker.Now()) : null;
                if (refusal == null)
                {
                    NoteAllowed(path, userId);
                    return false;
                }

                var deviceId = Controllers.ProfilesBaseController.ParseAuthorizationParameter(
                    request.Headers["Authorization"], request.Headers["X-Emby-Authorization"], "DeviceId");
                await TellRefusedAsync(sessions, userId, deviceId, refusal.Value.State, refusal.Value.Limited, logger).ConfigureAwait(false);
                await WriteRefusalAsync(context, refusal.Value.Code, refusal.Value.State).ConfigureAwait(false);
                return true;
            }
            catch (MediaBrowser.Controller.Net.SecurityException)
            {
                // Jellyfin refusing the token itself: it answers 401 when the request goes on.
                return false;
            }
            catch (Exception ex) when (!limitsActive && ex is not OperationCanceledException)
            {
                // Nobody is limited, so no rule is kept by refusing: a break here only noted a
                // PlaybackInfo, and refusing it told everyone, administrators included, that it was
                // outside viewing hours (r3-01-02).
                logger.LogWarning(ex, "ProfilesPlugin: noting a playback for {Path} failed; passing it on.", path);
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !context.Response.HasStarted)
            {
                // Fails CLOSED (r2-17-02): a broken check must not let a limited child past the
                // rule. MaintenanceGate and ProxySwitcher fail open, where a break only costs a
                // rule.
                logger.LogError(ex, "ProfilesPlugin: the viewing-hours check failed for {Path}; refusing it.", path);
                var response = context.Response;
                response.StatusCode = StatusCodes.Status403Forbidden;
                response.ContentType = "text/plain; charset=utf-8";
                response.Headers[Controllers.ErrorCodes.Header] = OutsideHours;
                response.Headers[HeaderNames.CacheControl] = "no-store";
                await response.WriteAsync("Ask a parent, or try again when viewing hours open. It is outside viewing hours.", context.RequestAborted).ConfigureAwait(false);
                return true;
            }
        }

        /// <summary>403, the reason in words, the code, and when to try again.</summary>
        internal static async Task WriteRefusalAsync(HttpContext context, string code, ViewingState state)
        {
            var response = context.Response;
            response.StatusCode = StatusCodes.Status403Forbidden;
            response.ContentType = "text/plain; charset=utf-8";
            response.Headers[Controllers.ErrorCodes.Header] = code;
            response.Headers[HeaderNames.CacheControl] = "no-store";
            if (state.NextStartUtc.HasValue)
            {
                var seconds = (long)Math.Ceiling((state.NextStartUtc.Value - ViewingTracker.Now()).TotalSeconds);
                if (seconds > 0) response.Headers[HeaderNames.RetryAfter] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            // Swiftfin ignores DisplayMessage and Roku shows none, so on those apps this body is
            // the only explanation a child gets, if the app shows the error text at all. Fix first,
            // then the reason. English only: the saved language is not known here, and Bonfire
            // cannot localize an app that shows this text.
            var text = code == LimitReached
                ? "Ask a parent for more time, or try again tomorrow. The daily viewing limit is reached."
                : "Ask a parent, or try again when viewing hours open. It is outside viewing hours.";
            await response.WriteAsync(text, context.RequestAborted).ConfigureAwait(false);
        }

        /// <summary>
        /// Says why on the app that asked, since an app shows a 403 as a generic playback
        /// error. Once a minute per profile: an app retrying should not stack messages.
        /// </summary>
        internal static async Task TellRefusedAsync(
            ISessionManager? sessions, Guid userId, string? deviceId, ViewingState state, ViewingTracker.Limited limited, ILogger logger)
        {
            if (sessions == null) return;
            var now = ViewingTracker.Now();
            lock (ViewingTracker.UsageLock)
            {
                var e = ViewingTracker.EntryFor(userId);
                if (e.LastRefusalMessageUtc.HasValue && now - e.LastRefusalMessageUtc.Value < MessageQuiet) return;
                e.LastRefusalMessageUtc = now;
            }

            var language = limited.Rules.Language;
            var text = RefusalText(state, limited.Zone, now, language);
            foreach (var s in sessions.Sessions.ToList())
            {
                if (s.UserId != userId) continue;
                if (!string.IsNullOrEmpty(deviceId) && !string.Equals(s.DeviceId, deviceId, StringComparison.Ordinal)) continue;
                await ViewingTimer.TellAsync(sessions, s.Id, EmailText.Get("notice.timeUp.header", language), text, 10000, logger).ConfigureAwait(false);
            }
        }

        internal static string RefusalText(ViewingState state, TimeZoneInfo zone, DateTime nowUtc, string? language)
        {
            if (state.NextStartUtc is DateTime next)
            {
                var (day, time, today) = ViewingTracker.Format(next, zone, nowUtc, language);
                if (state.Reason == ViewingClock.Limit && !today) return EmailText.Get("notice.doneToday", language);
                return today
                    ? EmailText.Get("notice.againAt", language, new System.Collections.Generic.Dictionary<string, string> { ["time"] = time })
                    : EmailText.Get("notice.againOn", language, new System.Collections.Generic.Dictionary<string, string> { ["day"] = day, ["time"] = time });
            }
            return state.Reason == ViewingClock.Limit ? EmailText.Get("notice.doneToday", language) : EmailText.Get("notice.notNow", language);
        }
    }
}
