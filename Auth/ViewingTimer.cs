using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Mail;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Auth
{
    /// <summary>
    /// Counts what limited profiles watch, and ends it when their time is up (1.7.2).
    ///
    /// <para><b>Every 30 seconds</b> it looks at Jellyfin's sessions. A limited profile playing
    /// video, not paused, has the time since the last look added to today. Two televisions at
    /// once count once: it is the child's time, not the screens'. So does a game: every
    /// Campground activity with a user that is not paused (<c>Sessions.ListActivities</c>)
    /// counts the same, and a video and a game together are one count. When time runs out the
    /// other plugin finds out by asking <see cref="LimitsProvider"/>; nothing is sent to it.</para>
    ///
    /// <para><b>Ending it.</b> Five minutes before the end, each app playing gets a message.
    /// At the end it stops. If the end came with no warning (the rules changed, the extra time
    /// was taken back, the timer had not looked yet) the message says five minutes and the stop
    /// comes five minutes later, so nobody loses the last scene to a clock.</para>
    ///
    /// <para><b>What apps do with it</b> varies: jellyfin-web and the apps built on it show
    /// the message and stop; Android TV shows it as a toast and stops; Swiftfin stops but
    /// shows nothing. An app that ignores the stop is cut off by <see cref="ViewingGate"/>,
    /// which refuses its next stream request once the grace is over.</para>
    ///
    /// <para><b>Never the reason anything fails.</b> Everything is caught and logged, as on
    /// every background path in Bonfire.</para>
    /// </summary>
    public sealed class ViewingTimer : IHostedService, IDisposable
    {
        internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(3);

        private readonly ISessionManager _sessions;
        private readonly ILogger<ViewingTimer> _logger;
        private Timer? _timer;
        private int _running;
        private DateTime _lastSave = DateTime.UtcNow;

        /// <summary>For the harness: when the last look was, so time between looks can be counted.</summary>
        internal static DateTime? LastTickUtc;

        public ViewingTimer(ISessionManager sessions, ILogger<ViewingTimer> logger)
        {
            _sessions = sessions;
            _logger = logger;
            ViewingTracker.Log = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _timer = new Timer(_ => OnTick(), null, Interval, Interval);
            _sessions.PlaybackStart += OnPlaybackStart;
            _sessions.PlaybackStopped += OnPlaybackStopped;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _sessions.PlaybackStart -= OnPlaybackStart;
            _sessions.PlaybackStopped -= OnPlaybackStopped;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            ViewingTracker.Save(_logger);
            return Task.CompletedTask;
        }

        // The tick only sees what is playing at the moment it looks, so a play that starts and
        // stops between two looks was never billed (R-1025): thirty-second clips, back to back,
        // were free. These two bill what the tick did not see. The start is remembered, and the
        // stop bills from the later of that and the last look to now; the tick bills the rest
        // while the play is going on, so no second is billed twice.
        private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
        {
            try
            {
                NotePlaybackStart(e?.Session?.UserId ?? Guid.Empty, e?.Session?.Id, ViewingGate.IsVideo(e?.MediaInfo), ViewingTracker.Now());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ProfilesPlugin: could not note the start of a playback.");
            }
        }

        private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
        {
            try
            {
                NotePlaybackStopped(e?.Session?.UserId ?? Guid.Empty, e?.Session?.Id, ViewingGate.IsVideo(e?.MediaInfo), ViewingTracker.Now());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ProfilesPlugin: could not bill the end of a playback.");
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> Started = new(StringComparer.Ordinal);
        private const int MaxStarted = 1000;

        /// <summary>A limited profile started a video on a session. Remembered until it stops.</summary>
        internal static void NotePlaybackStart(Guid userId, string? sessionId, bool video, DateTime nowUtc)
        {
            if (!video || userId == Guid.Empty || string.IsNullOrEmpty(sessionId)) return;
            if (!ViewingTracker.Index().ContainsKey(userId)) return;
            if (Started.Count >= MaxStarted)
            {
                foreach (var old in Started.Where(p => nowUtc - p.Value > TimeSpan.FromHours(12)).Select(p => p.Key).ToList())
                {
                    Started.TryRemove(old, out _);
                }

                if (Started.Count >= MaxStarted) return;
            }

            Started[sessionId] = nowUtc;
        }

        /// <summary>
        /// A video stopped on a session: bills what the tick did not. From the later of the
        /// start and the last look, to now, never more than the tick itself would bill.
        /// </summary>
        internal static void NotePlaybackStopped(Guid userId, string? sessionId, bool video, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            var hadStart = Started.TryRemove(sessionId, out var started);
            if (!video || userId == Guid.Empty) return;
            if (!ViewingTracker.Index().TryGetValue(userId, out var limited)) return;

            var from = LastTickUtc;
            if (hadStart && (!from.HasValue || started > from.Value)) from = started;
            if (!from.HasValue) return;

            var seconds = Math.Clamp((nowUtc - from.Value).TotalSeconds, 0, Interval.TotalSeconds * 2);
            ViewingTracker.AddSeconds(userId, ViewingClock.DayOf(limited.Zone, nowUtc), seconds);
        }

        public void Dispose() => _timer?.Dispose();

        private void OnTick()
        {
            // A slow tick (a session that takes long to message) is never overlapped by the next.
            if (Interlocked.Exchange(ref _running, 1) == 1) return;
            try
            {
                TickAsync(_sessions, ViewingTracker.Now(), _logger).GetAwaiter().GetResult();
                if (DateTime.UtcNow - _lastSave >= SaveEvery)
                {
                    _lastSave = DateTime.UtcNow;
                    ViewingTracker.Save(_logger);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ProfilesPlugin: the viewing-time check failed; trying again in 30 seconds.");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        /// <summary>One look at the sessions. Internal so the harness can drive the clock.</summary>
        internal static async Task TickAsync(ISessionManager sessions, DateTime nowUtc, ILogger logger)
        {
            var since = LastTickUtc;
            LastTickUtc = nowUtc;
            var index = ViewingTracker.Index();
            if (index.Count == 0) return;

            // Time since the last look, but never more than two intervals: a server that was
            // asleep, or a first look, does not bill an hour to whoever is playing now.
            var elapsed = since.HasValue ? (nowUtc - since.Value).TotalSeconds : 0;
            elapsed = Math.Clamp(elapsed, 0, Interval.TotalSeconds * 2);

            var playing = new Dictionary<Guid, List<SessionInfo>>();
            var counting = new HashSet<Guid>();
            foreach (var s in sessions.Sessions.ToList())
            {
                if (!index.ContainsKey(s.UserId) || !ViewingGate.IsVideo(s.NowPlayingItem)) continue;
                if (!playing.TryGetValue(s.UserId, out var list)) playing[s.UserId] = list = new List<SessionInfo>();
                list.Add(s);
                if (s.PlayState == null || !s.PlayState.IsPaused) counting.Add(s.UserId);
            }

            // What other Campground plugins are doing for a limited profile (a game in GameTent).
            // Into the same set as video, so a child watching and playing at once is billed once.
            // Bonfire's own activities are skipped: it publishes none, and one it did would be
            // the video already counted above.
            try
            {
                foreach (var a in Campground.Shared.Contracts.Sessions.ListActivities())
                {
                    if (a.Paused || a.UserId is not Guid user || user == Guid.Empty) continue;
                    if (string.Equals(a.PluginId, Campground.Shared.Registry.PluginIds.Bonfire, StringComparison.Ordinal)) continue;
                    if (index.ContainsKey(user)) counting.Add(user);
                }
            }
            catch (Exception ex)
            {
                // Another plugin's entry must never stop video being counted.
                logger.LogDebug(ex, "ProfilesPlugin: could not read other plugins' activities.");
            }

            foreach (var id in counting)
            {
                ViewingTracker.AddSeconds(id, ViewingClock.DayOf(index[id].Zone, nowUtc), elapsed);
            }

            foreach (var pair in playing)
            {
                var limited = index[pair.Key];
                var state = ViewingTracker.State(limited, nowUtc);
                var language = limited.Rules.Language;

                string? warn = null, header = null;
                var stop = false;
                var stoppedMessage = false;
                var limitNotice = false;

                lock (ViewingTracker.UsageLock)
                {
                    var e = ViewingTracker.EntryFor(pair.Key);
                    if (state.Allowed)
                    {
                        e.GraceEndsUtc = null;
                        e.StopSent = false;
                        if (state.MinutesLeft is int left && left <= ViewingTracker.WarnMinutes)
                        {
                            var endsAt = nowUtc.AddMinutes(left);
                            if (e.WarnedForUtc == null || Math.Abs((e.WarnedForUtc.Value - endsAt).TotalMinutes) > 2)
                            {
                                e.WarnedForUtc = endsAt;
                                header = "notice.warning.header";
                                warn = EmailText.Get("notice.stopsIn", language, Minutes(Math.Max(1, left)));
                            }
                        }
                    }
                    else
                    {
                        if (e.GraceEndsUtc == null)
                        {
                            // Warned for about now: the five minutes were given already.
                            if (e.WarnedForUtc.HasValue && e.WarnedForUtc.Value > nowUtc.AddMinutes(-2))
                            {
                                e.GraceEndsUtc = e.WarnedForUtc.Value > nowUtc ? e.WarnedForUtc.Value : nowUtc;
                            }
                            else
                            {
                                e.GraceEndsUtc = nowUtc.AddMinutes(ViewingTracker.GraceMinutes);
                                header = "notice.timeUp.header";
                                warn = EmailText.Get("notice.stopsIn", language, Minutes(ViewingTracker.GraceMinutes));
                            }

                            if (state.Reason == ViewingClock.Limit && e.LimitNoticeDay != state.Day)
                            {
                                e.LimitNoticeDay = state.Day;
                                limitNotice = true;
                            }
                        }

                        if (nowUtc >= e.GraceEndsUtc.Value)
                        {
                            stop = true;
                            stoppedMessage = !e.StopSent;
                            e.StopSent = true;
                        }
                    }
                }

                if (limitNotice)
                {
                    Notifications.LimitReached(limited.MasterId, limited.ProfileId, limited.Name, state.LimitMinutes ?? state.UsedMinutes, logger);
                }

                foreach (var session in pair.Value)
                {
                    if (warn != null)
                    {
                        await TellAsync(sessions, session.Id, EmailText.Get(header!, language), warn, 10000, logger).ConfigureAwait(false);
                    }

                    if (!stop) continue;
                    try
                    {
                        await sessions.SendPlaystateCommand(
                            null,
                            session.Id,
                            new PlaystateRequest { Command = PlaystateCommand.Stop },
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogDebug(ex, "ProfilesPlugin: could not stop playback on session {Session}.", session.Id);
                    }

                    if (stoppedMessage)
                    {
                        await TellAsync(sessions, session.Id, EmailText.Get("notice.timeUp.header", language),
                            EmailText.Get("notice.stopped", language), null, logger).ConfigureAwait(false);
                    }
                }

                if (stoppedMessage)
                {
                    logger.LogInformation("ProfilesPlugin: {Name}'s viewing time is up; playback stopped.", limited.Name);
                }
            }
        }

        private static Dictionary<string, string> Minutes(int minutes) =>
            new() { ["minutes"] = minutes.ToString(System.Globalization.CultureInfo.InvariantCulture) };

        /// <summary>A message on one app. Best effort.</summary>
        internal static async Task TellAsync(ISessionManager sessions, string sessionId, string header, string text, long? timeoutMs, ILogger logger)
        {
            try
            {
                await sessions.SendMessageCommand(
                    null,
                    sessionId,
                    new MessageCommand { Header = header, Text = text, TimeoutMs = timeoutMs },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "ProfilesPlugin: could not send a message to session {Session}.", sessionId);
            }
        }
    }
}
