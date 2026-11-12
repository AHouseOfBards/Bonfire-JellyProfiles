using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Profiles.Auth;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Mail;
using Jellyfin.Profiles.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Controllers
{
    /// <summary>
    /// Viewing hours and daily limits (1.7.2): the rules, more time, and asking for it. A
    /// partial of <see cref="ProfilesController"/>, so tests/js/apidocs.js reads these routes
    /// with the rest. Enforcement is not here: see <see cref="ViewingGate"/> and
    /// <see cref="ViewingTimer"/>.
    /// </summary>
    public partial class ProfilesController
    {
        internal const string NotLimitedMessage = "This profile has no viewing limits.";
        internal const string LimitsForProfilesMessage = "Limits are for profiles, not main accounts.";
        internal const string AlreadyAskedMessage = "You already asked. Try again in a few minutes.";

        // With a message, so it goes out as 403 not_your_profile. A bare 401 here would tell
        // the client its token was rejected, and some clients sign out on that.
        internal const string OtherHouseholdMessage = "That profile does not belong to this account.";

        internal const string DailyGrantCapMessage = "That's past the 4 hours of extra time allowed in a day.";

        /// <summary>The longest single grant: four hours. The daily cap is the same number, and is the one that decides.</summary>
        internal const int MaxGrantMinutes = 240;

        /// <summary>The main account a user belongs to: its own id for a main account.</summary>
        private static Guid HouseholdOf(PluginConfiguration config, Guid userId)
        {
            var row = config.Mappings.FirstOrDefault(m => m.ProfileUserId == userId);
            return row != null && row.MasterUserId != Guid.Empty ? row.MasterUserId : userId;
        }

        private bool CallerIsAdministrator(Guid? id)
        {
            if (CallerIsApiKey()) return true;
            if (id == null) return false;
            var user = _userManager.GetUserById(id.Value);
            return user != null && IsAdministratorAccount(user);
        }

        private static ViewingResponse ViewingView(ProfileMapping profile, PluginConfiguration config, bool withRules)
        {
            var main = config.Mappings.FirstOrDefault(m => m.ProfileUserId == profile.MasterUserId);
            var zone = HouseholdTime.Resolve(main?.TimeZone, config.DefaultTimeZone);
            var view = new ViewingResponse { ProfileId = profile.ProfileUserId, TimeZone = HouseholdTime.IdOf(zone) };
            var rules = profile.Viewing;
            if (withRules)
            {
                view.Rules = new ViewingRulesView
                {
                    HoursEnabled = rules?.HoursEnabled ?? false,
                    Windows = rules?.Windows?.Select(w => w.Copy()).ToList() ?? new List<ViewingWindow>(),
                    DailyMinutes = rules?.DailyMinutes ?? 0,
                    WeekendMinutes = rules?.WeekendMinutes,
                };
            }
            if (rules == null || !rules.IsLimited) return view;

            // The tracker's own index can be up to 30 seconds behind a save; the configuration
            // in hand is not, so the state is worked out from it.
            var now = ViewingTracker.Now();
            var limited = new ViewingTracker.Limited
            {
                ProfileId = profile.ProfileUserId,
                MasterId = profile.MasterUserId,
                Name = profile.ProfileName,
                Rules = rules,
                Zone = zone,
            };
            var state = ViewingTracker.State(limited, now);
            view.Limited = true;
            view.Allowed = state.Allowed;
            view.Reason = state.Reason;
            view.Binding = state.Binding;
            view.MinutesLeft = state.MinutesLeft;
            view.UntilUtc = state.UntilUtc;
            view.NextStartUtc = state.NextStartUtc;
            view.UsedMinutes = state.UsedMinutes;
            view.LimitMinutes = state.LimitMinutes;
            view.ExtraUntilUtc = state.ExtraUntilUtc;
            view.AskedUtc = ViewingTracker.AskedUtc(profile.ProfileUserId);
            return view;
        }

        /// <summary>The viewing part of a profile's tile in /list, or null for a profile with no limits.</summary>
        internal static ViewingResponse? ViewingSummary(ProfileMapping profile, PluginConfiguration config) =>
            profile.Viewing != null && profile.Viewing.IsLimited ? ViewingView(profile, config, false) : null;

        /// <summary>
        /// A profile's rules and where it stands now: minutes left, when it ends, when it opens
        /// again. Anyone in the household may read it, so the profile's own app can show it.
        /// </summary>
        [HttpGet("limits/{profileId}")]
        [ProducesResponseType(typeof(ViewingResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<ViewingResponse> GetViewingLimits([FromRoute] Guid profileId)
        {
            var caller = GetCurrentUserId();
            if (caller == null && !CallerIsApiKey()) return Unauthorized();
            var config = ReadConfigSnapshot();
            if (config == null) return BadRequest("Plugin configuration missing.");

            var profile = config.Mappings.FirstOrDefault(m => m.ProfileUserId == profileId);
            var household = profile != null ? HouseholdOf(config, profileId) : profileId;
            if (!CallerIsAdministrator(caller) && (caller == null || HouseholdOf(config, caller.Value) != household))
                return Unauthorized(OtherHouseholdMessage);
            if (profile == null)
            {
                if (_userManager.GetUserById(profileId) == null) return NotFound();
                return Ok(new ViewingResponse { ProfileId = profileId });
            }
            return Ok(ViewingView(profile, config, true));
        }

        /// <summary>
        /// Sets a profile's rules. Its main account, with the main PIN, or an administrator.
        /// Switching limits on needs a main PIN to exist: without one the child could open the
        /// form and switch them off again.
        /// </summary>
        [HttpPost("limits/{profileId}")]
        [ProducesResponseType(typeof(ViewingResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public ActionResult<ViewingResponse> SetViewingLimits([FromRoute] Guid profileId, [FromBody] SetViewingRulesRequest request)
        {
            var caller = GetCurrentUserId();
            if (caller == null && !CallerIsApiKey()) return Unauthorized();
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");
            if (request == null) return BadRequest("Missing request body.");

            var profile = FindMapping(profileId);
            if (profile == null) return NotFound("Profile not found.");
            if (profile.MasterUserId == Guid.Empty || profile.MasterUserId == profile.ProfileUserId)
                return BadRequest(LimitsForProfilesMessage);
            var mainId = profile.MasterUserId;

            var admin = CallerIsAdministrator(caller);
            if (!admin)
            {
                if (caller == null || caller.Value != mainId) return Unauthorized("Only the main account can do that.");
                if (!ManagementAllowed(mainId, config, request.MasterPin, out var refusal)) return refusal!;
            }

            // What is stored now, for every field the request leaves out (R-1326). These were
            // replaced whole, and the request's plain bool and int read a left-out field as
            // off and 0.
            var current = profile.Viewing ?? new ViewingRules();

            var windows = request.Windows ?? current.Windows ?? new List<ViewingWindow>();
            if (windows.Count > ViewingRules.MaxWindows) return BadRequest("Too many viewing windows.");
            if (windows.Any(w => w == null || !w.IsValid)) return BadRequest("A viewing window isn't valid. Times are HH:mm.");
            var daily = request.DailyMinutes ?? current.DailyMinutes;
            var weekend = request.WeekendMinutesSent ? request.WeekendMinutes : current.WeekendMinutes;
            if (daily < 0 || daily > ViewingRules.MaxMinutes
                || (weekend.HasValue && (weekend < 0 || weekend > ViewingRules.MaxMinutes)))
                return BadRequest("A daily limit is between 0 and 1440 minutes.");

            var rules = new ViewingRules
            {
                HoursEnabled = request.HoursEnabled ?? current.HoursEnabled,
                Windows = windows.Select(w => new ViewingWindow { Days = w.Days, Start = w.Start, End = w.End }).ToList(),
                DailyMinutes = daily,
                WeekendMinutes = weekend,
                Language = request.Language != null ? EmailText.CleanLanguage(request.Language) : current.Language,
            };
            if (rules.IsLimited && !admin && !MainHasPin(mainId, config)) return BadRequest(MainPinRequiredMessage);

            PluginConfiguration? saved;
            ProfileMapping? row;
            lock (ConfigLock)
            {
                row = config.Mappings.FirstOrDefault(m => m.ProfileUserId == profileId);
                if (row == null) return NotFound("Profile not found.");
                row.Viewing = rules.IsLimited || rules.Windows.Count > 0 ? rules : null;
                Plugin.Instance?.SaveConfiguration();
                saved = config.SnapshotForReading();
            }
            ViewingTracker.Invalidate();
            // Nothing reads what a profile with no rules has watched, and it would stay in the file (R-1023).
            if (!rules.IsLimited) ViewingTracker.ForgetUnlessRecent(profileId);
            LogActivity("Viewing limits for " + row.ProfileName + (rules.IsLimited ? " changed" : " turned off"), "ViewingLimits", profileId);
            var savedRow = saved.Mappings.First(m => m.ProfileUserId == profileId);
            return Ok(ViewingView(savedRow, saved, true));
        }

        /// <summary>
        /// More time, now: playback is allowed for the next <c>minutes</c> whatever the hours
        /// or the limit say, added to any time already given. 0 answers a request with no. Any
        /// device in the household can do it with the main PIN, which is how a parent gives
        /// time on the child's television; or the main account, or an administrator.
        /// </summary>
        [HttpPost("limits/{profileId}/grant")]
        [ProducesResponseType(typeof(ViewingResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public ActionResult<ViewingResponse> GrantViewingTime([FromRoute] Guid profileId, [FromBody] GrantTimeRequest request)
        {
            var caller = GetCurrentUserId();
            if (caller == null && !CallerIsApiKey()) return Unauthorized();
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");
            if (request == null) return BadRequest("Missing request body.");

            var snapshot = ReadConfigSnapshot() ?? config;
            var profile = snapshot.Mappings.FirstOrDefault(m => m.ProfileUserId == profileId);
            if (profile == null) return NotFound("Profile not found.");
            var mainId = profile.MasterUserId;

            if (!CallerIsAdministrator(caller))
            {
                if (caller == null || HouseholdOf(snapshot, caller.Value) != mainId) return Unauthorized(OtherHouseholdMessage);
                // From the child's own device the main PIN is the whole of the permission, so
                // there has to be one.
                if (caller.Value != mainId && !MainHasPin(mainId, snapshot)) return BadRequest(MainPinRequiredMessage);
                if (!ManagementAllowed(mainId, config, request.MasterPin, out var refusal)) return refusal!;
            }

            if (profile.Viewing == null || !profile.Viewing.IsLimited) return BadRequest(NotLimitedMessage);
            if (request.Minutes < 0 || request.Minutes > MaxGrantMinutes) return BadRequest("More time is between 1 and 240 minutes.");

            if (request.Minutes == 0)
            {
                ViewingTracker.Decline(profileId);
                LogActivity(profile.ProfileName + " was told no to more time", "ViewingLimits", profileId);
            }
            else
            {
                // The daily cap is checked and counted in one step, so it holds for an
                // administrator and for two grants at once. It is the household's day.
                var main = snapshot.Mappings.FirstOrDefault(m => m.ProfileUserId == mainId);
                var zone = HouseholdTime.Resolve(main?.TimeZone, snapshot.DefaultTimeZone);
                if (!ViewingTracker.TryGrant(profileId, request.Minutes, ViewingTracker.Now(), zone))
                    return BadRequest(DailyGrantCapMessage);
                LogActivity(profile.ProfileName + " was given " + request.Minutes + " more minutes", "ViewingLimits", profileId);
            }
            ViewingTracker.Save(_logger);
            return Ok(ViewingView(profile, snapshot, false));
        }

        /// <summary>
        /// The profile asks for more time. Its main account's open apps get a message, and an
        /// email if it asked for those. Once every ten minutes.
        /// </summary>
        [HttpPost("limits/{profileId}/ask")]
        [ProducesResponseType(typeof(ViewingResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<ActionResult<ViewingResponse>> AskForViewingTime([FromRoute] Guid profileId)
        {
            var caller = GetCurrentUserId();
            if (caller == null) return Unauthorized();
            var config = ReadConfigSnapshot();
            if (config == null) return BadRequest("Plugin configuration missing.");

            var profile = config.Mappings.FirstOrDefault(m => m.ProfileUserId == profileId);
            if (profile == null) return NotFound("Profile not found.");
            var mainId = profile.MasterUserId;
            if (HouseholdOf(config, caller.Value) != mainId) return Unauthorized(OtherHouseholdMessage);
            // One ask per profile every ten minutes, sent to the parent in the profile's name:
            // a sibling must not spend it. The profile itself or its main account.
            if (caller.Value != profileId && caller.Value != mainId) return Unauthorized(OtherHouseholdMessage);
            if (profile.Viewing == null || !profile.Viewing.IsLimited) return BadRequest(NotLimitedMessage);

            if (!ViewingTracker.Ask(profileId, ViewingTracker.Now()))
                return StatusCode(StatusCodes.Status429TooManyRequests, AlreadyAskedMessage);
            // The parent's own language if their address recorded one, else the one the rules were saved in.
            var language = config.AccountEmails.FirstOrDefault(a => a.UserId == mainId)?.Language;
            if (string.IsNullOrEmpty(language)) language = profile.Viewing.Language;
            var values = new Dictionary<string, string> { ["name"] = profile.ProfileName };
            foreach (var s in _sessionManager.Sessions.ToList())
            {
                if (s.UserId != mainId) continue;
                await ViewingTimer.TellAsync(_sessionManager, s.Id, EmailText.Get("notice.asked.header", language),
                    EmailText.Get("notice.asked", language, values), null, _logger).ConfigureAwait(false);
            }
            Notifications.TimeAsked(mainId, profileId, profile.ProfileName, _logger);
            LogActivity(profile.ProfileName + " asked for more time", "ViewingLimits", profileId);
            ViewingTracker.Save(_logger);
            return Ok(ViewingView(profile, config, false));
        }

        /// <summary>
        /// The profiles in the caller's household that have asked for more time and are still
        /// waiting, oldest first. For the row above the tiles on the picker. Anyone in the
        /// household may read it, as with the limits themselves; the answer, with the main PIN,
        /// is <c>limits/{profileId}/grant</c>. Never another household's: the household is the
        /// caller's own, not a parameter. An API key has no household and is refused.
        /// </summary>
        [HttpGet("time-requests")]
        [ProducesResponseType(typeof(TimeRequestsResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<TimeRequestsResponse> GetTimeRequests()
        {
            var caller = GetCurrentUserId();
            if (caller == null) return Unauthorized();
            var config = ReadConfigSnapshot();
            if (config == null) return BadRequest("Plugin configuration missing.");

            var household = HouseholdOf(config, caller.Value);
            var main = config.Mappings.FirstOrDefault(m => m.ProfileUserId == household);
            var zone = HouseholdTime.Resolve(main?.TimeZone, config.DefaultTimeZone);
            var response = new TimeRequestsResponse();
            foreach (var m in config.Mappings)
            {
                if (m.MasterUserId != household || m.ProfileUserId == household) continue;
                if (m.Viewing == null || !m.Viewing.IsLimited) continue;
                var asked = ViewingTracker.AskedUtc(m.ProfileUserId);
                if (!asked.HasValue) continue;
                response.Requests.Add(new TimeRequestView
                {
                    ProfileId = m.ProfileUserId,
                    ProfileName = m.ProfileName,
                    AskedUtc = DateTime.SpecifyKind(asked.Value, DateTimeKind.Utc),
                    TimeZone = HouseholdTime.IdOf(zone),
                    GrantMinutesLeft = ViewingTracker.GrantMinutesLeft(m.ProfileUserId, zone, ViewingTracker.Now()),
                });
            }
            response.Requests.Sort((a, b) => a.AskedUtc.CompareTo(b.AskedUtc));
            return Ok(response);
        }

        /// <summary>
        /// The time zones this server knows, and the one the caller's household uses. For the
        /// zone picker in Your account and on the settings page.
        /// </summary>
        [HttpGet("timezones")]
        [ProducesResponseType(typeof(TimeZonesResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<TimeZonesResponse> GetTimeZones()
        {
            var caller = GetCurrentUserId();
            if (caller == null && !CallerIsApiKey()) return Unauthorized();
            var config = ReadConfigSnapshot() ?? new PluginConfiguration();
            var household = caller == null ? null : config.Mappings.FirstOrDefault(m => m.ProfileUserId == HouseholdOf(config, caller.Value))?.TimeZone;
            return Ok(new TimeZonesResponse
            {
                Zones = HouseholdTime.Zones(),
                Server = HouseholdTime.IdOf(TimeZoneInfo.Local),
                Default = config.DefaultTimeZone ?? string.Empty,
                Household = household ?? string.Empty,
                Effective = HouseholdTime.IdOf(HouseholdTime.Resolve(household, config.DefaultTimeZone)),
            });
        }
    }
}
