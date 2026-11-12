using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Mail;
using Jellyfin.Profiles.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Controllers
{
    /// <summary>
    /// Email (1.7.2): the server's outgoing mail, a main account's confirmed address, PIN
    /// reset by email, and notices. A partial of <see cref="ProfilesController"/>, not a
    /// controller of its own, so tests/js/apidocs.js reads these routes with the rest and
    /// holds each one to its documented authorisation.
    /// </summary>
    public partial class ProfilesController
    {
        /// <summary>Codes sent per main account: enough for a typo or two, not for a mailbomb.</summary>
        internal static readonly RateLimiter EmailPerAccount = RateLimiter.Create(maxAttempts: 5, windowMinutes: 60);

        /// <summary>Codes sent by the whole server, against an account mailing strangers.</summary>
        internal static readonly RateLimiter EmailPerServer = RateLimiter.Create(maxAttempts: 60, windowMinutes: 60);

        /// <summary>PIN-reset codes sent by the whole server. Separate from the above (R-1063).</summary>
        internal static readonly RateLimiter EmailResetPerServer = RateLimiter.Create(maxAttempts: 60, windowMinutes: 60);

        private const string EmailUnavailable = "Email isn't set up on this server.";

        /// <summary>Whether this main account could reset its PIN by email right now.</summary>
        internal static bool PinResetPossible(Guid mainId, PluginConfiguration config) =>
            Features.Available(Features.PinResetByEmail, config)
            && config.AccountEmails.Any(a => a.UserId == mainId && !string.IsNullOrEmpty(a.Address));

        // ── The administrator's side ────────────────────────────────────────────────

        /// <summary>The outgoing mail settings, without the password, and every confirmed address.</summary>
        [HttpGet("admin/email")]
        [ProducesResponseType(typeof(AdminEmailResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<AdminEmailResponse> GetEmailSettings()
        {
            var adminError = RequireAdministrator("see the email settings");
            if (adminError != null) return adminError;
            var config = ReadConfigSnapshot() ?? new PluginConfiguration();
            return Ok(AdminEmailView(config));
        }

        private AdminEmailResponse AdminEmailView(PluginConfiguration config)
        {
            var e = config.Email ?? new EmailSettings();
            return new AdminEmailResponse
            {
                Host = e.Host,
                Port = e.Port,
                Security = EmailSecurity.Normalize(e.Security),
                Username = e.Username,
                HasPassword = !string.IsNullOrEmpty(e.Password),
                FromAddress = e.FromAddress,
                Configured = e.IsConfigured,
                Verified = e.IsVerified,
                PinResetByEmail = config.EnablePinResetByEmail,
                Accounts = config.AccountEmails
                    .Where(a => !string.IsNullOrEmpty(a.Address))
                    .Select(a => new AdminAccountEmail
                    {
                        UserId = a.UserId,
                        Name = _userManager.GetUserById(a.UserId)?.Username ?? string.Empty,
                        Address = a.Address,
                        ConfirmedUtc = a.ConfirmedUtc,
                    })
                    .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        }

        /// <summary>
        /// Saves the outgoing mail settings. Only what is sent changes. Anything that changes
        /// the connection makes it unverified until it is tested again (see
        /// <see cref="EmailSettings.Fingerprint"/>), and with it everything that needs email.
        /// </summary>
        [HttpPost("admin/email")]
        [ProducesResponseType(typeof(AdminEmailResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<AdminEmailResponse> UpdateEmailSettings([FromBody] AdminEmailRequest request)
        {
            var adminError = RequireAdministrator("change the email settings");
            if (adminError != null) return adminError;
            if (request == null) return BadRequest("No settings were sent.");

            if (request.Port.HasValue && (request.Port < 1 || request.Port > 65535))
                return BadRequest("The port must be between 1 and 65535.");
            if (request.Security != null && !EmailSecurity.IsKnown(request.Security))
                return BadRequest("Security must be 'starttls' or 'none'.");
            if (request.Host != null && (request.Host.Trim().Length > 253 || request.Host.Trim().IndexOfAny(new[] { ' ', '/', '\r', '\n' }) >= 0))
                return BadRequest("That server name isn't valid.");
            if (request.FromAddress != null && request.FromAddress.Trim().Length > 0 && !Mailer.IsValidAddress(request.FromAddress))
                return BadRequest("The From address isn't valid.");

            PluginConfiguration? saved;
            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");
                var e = config.Email ??= new EmailSettings();

                var port = request.Port ?? e.Port;
                var security = request.Security != null ? EmailSecurity.Normalize(request.Security) : EmailSecurity.Normalize(e.Security);
                // See SmtpMailSender: 465 is TLS from the first byte, which SmtpClient can't
                // speak, and a test against it hangs for the whole timeout.
                // Whatever the security setting says: 465 is TLS from the first byte, so
                // "none" hangs for the whole timeout just the same (R-1067).
                if (port == 465)
                    return BadRequest("Port 465 isn't supported. Use 587 with STARTTLS; every common provider offers it.");

                if (request.Host != null) e.Host = request.Host.Trim();
                e.Port = port;
                e.Security = security;
                if (request.Username != null) e.Username = request.Username.Trim();
                if (request.Password != null) e.Password = request.Password;
                if (request.FromAddress != null) e.FromAddress = request.FromAddress.Trim();

                Plugin.Instance?.SaveConfiguration();
                saved = config.SnapshotForReading();
            }
            return Ok(AdminEmailView(saved));
        }

        /// <summary>
        /// Sends a test message with the saved settings. Going through is what makes email
        /// available: it records the settings' fingerprint as verified.
        /// </summary>
        [HttpPost("admin/email/test")]
        [ProducesResponseType(typeof(AdminEmailResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult<AdminEmailResponse>> TestEmailSettings([FromBody] EmailTestRequest request)
        {
            var adminError = RequireAdministrator("test the email settings");
            if (adminError != null) return adminError;
            if (request == null || !Mailer.IsValidAddress(request.To)) return BadRequest("Enter an address to send the test to.");

            var snapshot = ReadConfigSnapshot();
            if (snapshot?.Email == null || !snapshot.Email.IsConfigured)
                return BadRequest("Fill in the server and the From address first.");

            var fingerprint = snapshot.Email.Fingerprint();
            try
            {
                await Mailer.SendAsync(snapshot.Email, request.To.Trim(),
                    EmailText.Get("email.test.subject", null), EmailText.Get("email.test.body", null),
                    HttpContext?.RequestAborted ?? default).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ProfilesPlugin: The test email failed.");
                return BadRequest(Mailer.Describe(ex));
            }

            PluginConfiguration? saved;
            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");
                // Only if nothing changed while the test was out: verifying settings nobody
                // tested would be a lie the dashboard then repeats.
                if (config.Email != null && config.Email.Fingerprint() == fingerprint)
                {
                    config.Email.VerifiedFingerprint = fingerprint;
                    Plugin.Instance?.SaveConfiguration();
                }
                saved = config.SnapshotForReading();
            }
            _logger.LogInformation("ProfilesPlugin: Test email sent; email is available.");
            return Ok(AdminEmailView(saved));
        }

        /// <summary>Removes a main account's address. It stops getting codes and notices.</summary>
        [HttpPost("admin/account-email/remove")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult RemoveAccountEmailAsAdmin([FromBody] RemoveAccountEmailRequest request)
        {
            var adminError = RequireAdministrator("remove an account's email");
            if (adminError != null) return adminError;
            if (request == null || request.UserId == Guid.Empty) return BadRequest("No account was named.");

            lock (ConfigLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return BadRequest("Plugin configuration missing.");
                config.AccountEmails.RemoveAll(a => a.UserId == request.UserId);
                Plugin.Instance?.SaveConfiguration();
            }
            EmailCodes.Forget(EmailCodes.ConfirmAddress, request.UserId);
            EmailCodes.Forget(EmailCodes.ResetPin, request.UserId);
            return Ok();
        }

        // ── The main account's own address ──────────────────────────────────────────

        /// <summary>
        /// The signed-in main account, or the refusal: 401 for nobody, an API key, or a
        /// profile. Every route below is the main account's alone. Takes the caller's id
        /// rather than reading it, so each route visibly asks for it (tests/js/apidocs.js).
        /// </summary>
        private ActionResult? RequireMainAccount(PluginConfiguration config, Guid? id, out Guid mainId)
        {
            mainId = Guid.Empty;
            if (id == null || id.Value == Guid.Empty) return Unauthorized();
            // Read under the lock: a create or a delete changes this list (r2-11-02c).
            var row = MappingIn(config, id.Value);
            if (row != null && row.MasterUserId != Guid.Empty && row.MasterUserId != id.Value)
                return Unauthorized("Only the main account can do that.");
            mainId = id.Value;
            return null;
        }

        private AccountEmailResponse AccountEmailView(Guid mainId, PluginConfiguration config)
        {
            var row = config.AccountEmails.FirstOrDefault(a => a.UserId == mainId);
            return new AccountEmailResponse
            {
                Available = Features.Available(Features.Email, config),
                Address = row?.Address ?? string.Empty,
                PendingAddress = EmailCodes.PendingAddress(EmailCodes.ConfirmAddress, mainId) ?? string.Empty,
                NotifyLockout = row?.NotifyLockout ?? true,
                NotifyNewDevice = row?.NotifyNewDevice ?? true,
                NotifyTimeAsked = row?.NotifyTimeAsked ?? true,
                NotifyLimitReached = row?.NotifyLimitReached ?? false,
                PinResetEnabled = Features.Available(Features.PinResetByEmail, config),
            };
        }

        /// <summary>
        /// The main account's address and notices. Needs the main PIN, as the rest of
        /// Settings does: the address is the parent's, and the TV may be signed in as them.
        /// </summary>
        [HttpGet("account/email")]
        [ProducesResponseType(typeof(AccountEmailResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public ActionResult<AccountEmailResponse> GetAccountEmail()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");
            var notMain = RequireMainAccount(config, GetCurrentUserId(), out var mainId);
            if (notMain != null) return notMain;
            if (!ManagementAllowed(mainId, config, null, out var refusal)) return refusal!;
            return Ok(AccountEmailView(mainId, ReadConfigSnapshot() ?? config));
        }

        /// <summary>
        /// Sends a code to a new address. Nothing is stored until the code comes back:
        /// the address waits in memory with it (see <see cref="EmailCodes"/>).
        /// </summary>
        [HttpPost("account/email")]
        [ProducesResponseType(typeof(AccountEmailResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<ActionResult<AccountEmailResponse>> SetAccountEmail([FromBody] AccountEmailRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");
            if (request == null) return BadRequest("Missing request body.");
            var notMain = RequireMainAccount(config, GetCurrentUserId(), out var mainId);
            if (notMain != null) return notMain;
            if (!ManagementAllowed(mainId, config, request.MasterPin, out var refusal)) return refusal!;

            var snapshot = ReadConfigSnapshot() ?? config;
            if (!Features.Available(Features.Email, snapshot)) return BadRequest(EmailUnavailable);
            if (!Mailer.IsValidAddress(request.Address)) return BadRequest("That email address isn't valid.");
            var address = request.Address.Trim();

            var sent = await SendCode(snapshot, mainId, EmailCodes.ConfirmAddress, address, request.Language,
                "email.confirm.subject", "email.confirm.body").ConfigureAwait(false);
            if (sent != null) return sent;
            return Ok(AccountEmailView(mainId, snapshot));
        }

        /// <summary>The code from the mail: the address is confirmed and stored.</summary>
        [HttpPost("account/email/confirm")]
        [ProducesResponseType(typeof(AccountEmailResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public ActionResult<AccountEmailResponse> ConfirmAccountEmail([FromBody] AccountEmailConfirmRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");
            if (request == null) return BadRequest("Missing request body.");
            var notMain = RequireMainAccount(config, GetCurrentUserId(), out var mainId);
            if (notMain != null) return notMain;
            if (!ManagementAllowed(mainId, config, request.MasterPin, out var refusal)) return refusal!;

            var result = EmailCodes.Check(EmailCodes.ConfirmAddress, mainId, request.Code, out var address);
            if (result != EmailCodes.Result.Ok) return BadRequest(CodeRefusal(result));

            PluginConfiguration? saved;
            string? replacedAddress = null;
            string? oldLanguage = null;
            lock (ConfigLock)
            {
                var live = Plugin.Instance?.Configuration;
                if (live == null) return BadRequest("Plugin configuration missing.");
                var row = live.AccountEmails.FirstOrDefault(a => a.UserId == mainId);
                if (row == null)
                {
                    row = new AccountEmail { UserId = mainId };
                    live.AccountEmails.Add(row);
                }
                replacedAddress = row.Address;
                oldLanguage = row.Language;
                row.Address = address;
                row.ConfirmedUtc = DateTime.UtcNow;
                row.Language = EmailText.CleanLanguage(request.Language);
                Plugin.Instance?.SaveConfiguration();
                saved = live.SnapshotForReading();
            }

            if (!string.IsNullOrEmpty(replacedAddress)
                && !string.Equals(replacedAddress, address, StringComparison.OrdinalIgnoreCase))
            {
                // R-1060: a reset code mailed to the old address would still finish a reset
                // for up to 15 minutes. Remove forgets it; a change has to as well.
                EmailCodes.Forget(EmailCodes.ResetPin, mainId);
                // R-1061: and the old address hears about it, in case it wasn't them.
                var name = saved.Mappings.FirstOrDefault(m => m.ProfileUserId == mainId)?.ProfileName
                           ?? _userManager.GetUserById(mainId)?.Username ?? string.Empty;
                var values = new Dictionary<string, string>
                {
                    ["account"] = name,
                    ["address"] = Mailer.Mask(address),
                };
                var settings = saved.Email;
                var oldTo = replacedAddress;
                var lang = oldLanguage;
                var logger = _logger;
                Notifications.Pending.Enqueue(BackgroundWork.Run(async _ =>
                {
                    try
                    {
                        await Mailer.SendAsync(settings, oldTo, EmailText.Get("email.changed.subject", lang, values),
                            EmailText.Get("email.changed.body", lang, values)).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning("ProfilesPlugin: Couldn't send the address-changed note: {Reason}", Mailer.Describe(ex));
                    }
                }));
            }
            return Ok(AccountEmailView(mainId, saved));
        }

        /// <summary>Which notices the main account gets. Only what is sent changes.</summary>
        [HttpPost("account/email/notifications")]
        [ProducesResponseType(typeof(AccountEmailResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public ActionResult<AccountEmailResponse> SetAccountEmailNotifications([FromBody] AccountEmailNotificationsRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");
            if (request == null) return BadRequest("Missing request body.");
            var notMain = RequireMainAccount(config, GetCurrentUserId(), out var mainId);
            if (notMain != null) return notMain;
            if (!ManagementAllowed(mainId, config, request.MasterPin, out var refusal)) return refusal!;

            PluginConfiguration? saved;
            lock (ConfigLock)
            {
                var live = Plugin.Instance?.Configuration;
                if (live == null) return BadRequest("Plugin configuration missing.");
                var row = live.AccountEmails.FirstOrDefault(a => a.UserId == mainId);
                if (row == null || string.IsNullOrEmpty(row.Address)) return BadRequest("Add an email address first.");
                if (request.NotifyLockout.HasValue) row.NotifyLockout = request.NotifyLockout.Value;
                if (request.NotifyNewDevice.HasValue) row.NotifyNewDevice = request.NotifyNewDevice.Value;
                if (request.NotifyTimeAsked.HasValue) row.NotifyTimeAsked = request.NotifyTimeAsked.Value;
                if (request.NotifyLimitReached.HasValue) row.NotifyLimitReached = request.NotifyLimitReached.Value;
                Plugin.Instance?.SaveConfiguration();
                saved = live.SnapshotForReading();
            }
            return Ok(AccountEmailView(mainId, saved));
        }

        /// <summary>Removes the main account's address, and any code waiting for one.</summary>
        [HttpPost("account/email/remove")]
        [ProducesResponseType(typeof(AccountEmailResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public ActionResult<AccountEmailResponse> RemoveAccountEmail([FromBody] AccountEmailRemoveRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");
            var notMain = RequireMainAccount(config, GetCurrentUserId(), out var mainId);
            if (notMain != null) return notMain;
            if (!ManagementAllowed(mainId, config, request?.MasterPin, out var refusal)) return refusal!;

            PluginConfiguration? saved;
            lock (ConfigLock)
            {
                var live = Plugin.Instance?.Configuration;
                if (live == null) return BadRequest("Plugin configuration missing.");
                live.AccountEmails.RemoveAll(a => a.UserId == mainId);
                Plugin.Instance?.SaveConfiguration();
                saved = live.SnapshotForReading();
            }
            EmailCodes.Forget(EmailCodes.ConfirmAddress, mainId);
            EmailCodes.Forget(EmailCodes.ResetPin, mainId);
            return Ok(AccountEmailView(mainId, saved));
        }

        // ── Forgotten PIN ───────────────────────────────────────────────────────────

        /// <summary>
        /// Sends a reset code to the main account's confirmed address. No PIN: this is the
        /// way in for someone who has forgotten it. The code goes only to the address the
        /// account confirmed, so being signed in as the account is not enough on its own.
        /// </summary>
        [HttpPost("account/pin-reset/send")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<ActionResult<object>> SendPinReset([FromBody] PinResetSendRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");
            var notMain = RequireMainAccount(config, GetCurrentUserId(), out var mainId);
            if (notMain != null) return notMain;

            var snapshot = ReadConfigSnapshot() ?? config;
            if (!PinResetPossible(mainId, snapshot)) return BadRequest("PIN reset by email isn't available for this account.");
            var address = snapshot.AccountEmails.First(a => a.UserId == mainId).Address;

            var sent = await SendCode(snapshot, mainId, EmailCodes.ResetPin, address, request?.Language,
                "email.reset.subject", "email.reset.body").ConfigureAwait(false);
            if (sent != null) return sent;
            return Ok(new { SentTo = Mailer.Mask(address) });
        }

        /// <summary>
        /// The code from the mail and a new PIN. Sets the main account's PIN and lifts any
        /// lock on it. The address gets a note saying so, in case it wasn't them.
        /// </summary>
        [HttpPost("account/pin-reset")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult> ResetPinByEmail([FromBody] PinResetRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return BadRequest("Plugin configuration missing.");
            if (request == null) return BadRequest("Missing request body.");
            var notMain = RequireMainAccount(config, GetCurrentUserId(), out var mainId);
            if (notMain != null) return notMain;

            var snapshot = ReadConfigSnapshot() ?? config;
            if (!PinResetPossible(mainId, snapshot)) return BadRequest("PIN reset by email isn't available for this account.");
            // Checked before the code, so a malformed PIN doesn't spend one.
            if (!IsValidPin(request.NewPin)) return BadRequest("PIN code must be a numeric value between 4 and 8 digits.");

            var result = EmailCodes.Check(EmailCodes.ResetPin, mainId, request.Code, out var address);
            if (result != EmailCodes.Result.Ok) return BadRequest(CodeRefusal(result));

            string name;
            lock (ConfigLock)
            {
                var live = Plugin.Instance?.Configuration;
                if (live == null) return BadRequest("Plugin configuration missing.");
                var row = live.Mappings.FirstOrDefault(m => m.ProfileUserId == mainId);
                if (row == null)
                {
                    row = new ProfileMapping
                    {
                        ProfileUserId = mainId,
                        MasterUserId = mainId,
                        ProfileName = _userManager.GetUserById(mainId)?.Username ?? "Master",
                        IsHidden = false
                    };
                    live.Mappings.Add(row);
                }
                row.PinHash = HashPin(request.NewPin);
                name = row.ProfileName;
                Plugin.Instance?.SaveConfiguration();
            }

            // The lock a forgotten PIN usually ends in.
            PinBudget.End(mainId);
            ProfilesBootstrapTask.ReconcileAuthProvidersNow();
            LogActivity(name + "'s PIN was reset by email", "PinReset", mainId,
                severity: LogLevel.Warning);
            // Another household that opened it with the old PIN is out (R-1302b).
            await SignOutAfterPinChangeAsync(mainId).ConfigureAwait(false);

            var language = snapshot.AccountEmails.FirstOrDefault(a => a.UserId == mainId)?.Language;
            var values = new Dictionary<string, string> { ["account"] = name };
            var settings = snapshot.Email;
            var logger = _logger;
            Notifications.Pending.Enqueue(BackgroundWork.Run(async _ =>
            {
                try
                {
                    await Mailer.SendAsync(settings, address, EmailText.Get("email.resetDone.subject", language, values),
                        EmailText.Get("email.resetDone.body", language, values)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("ProfilesPlugin: Couldn't send the PIN-changed note: {Reason}", Mailer.Describe(ex));
                }
            }));
            return Ok();
        }

        // ── Shared ──────────────────────────────────────────────────────────────────

        private static string CodeRefusal(EmailCodes.Result result) => result switch
        {
            EmailCodes.Result.Wrong => "That code isn't right.",
            EmailCodes.Result.Expired => "That code has expired. Send a new one.",
            _ => "Send a code first."
        };

        /// <summary>Issues a code and mails it, or returns the refusal.</summary>
        private async Task<ActionResult?> SendCode(PluginConfiguration snapshot, Guid mainId, string purpose, string address,
            string? language, string subjectKey, string bodyKey)
        {
            var accountKey = purpose + ":" + mainId.ToString("N");
            if (!EmailPerAccount.TryBegin(accountKey))
                return TooManyAttempts(EmailPerAccount, accountKey, "Too many codes sent.");
            // R-1063: confirmation mail goes to any address a main account types, so it has its
            // own server cap. A PIN-reset code goes only to an address already confirmed, and
            // must not be starved by someone mailing strangers.
            var serverLimit = purpose == EmailCodes.ResetPin ? EmailResetPerServer : EmailPerServer;
            if (!serverLimit.TryBegin("server"))
                return TooManyAttempts(serverLimit, "server", "Too many codes sent from this server.");

            var name = snapshot.Mappings.FirstOrDefault(m => m.ProfileUserId == mainId)?.ProfileName
                       ?? _userManager.GetUserById(mainId)?.Username
                       ?? string.Empty;
            var code = EmailCodes.Issue(purpose, mainId, address, out var undo);
            var values = new Dictionary<string, string> { ["code"] = code, ["account"] = name };
            var lang = EmailText.CleanLanguage(language);
            try
            {
                await Mailer.SendAsync(snapshot.Email, address,
                    EmailText.Get(subjectKey, lang, values), EmailText.Get(bodyKey, lang, values),
                    HttpContext?.RequestAborted ?? default).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                undo();   // R-1062: only the code this send made; one already in the inbox stays good
                _logger.LogWarning("ProfilesPlugin: Couldn't send a code: {Reason}", Mailer.Describe(ex));
                return BadRequest("The email couldn't be sent. Ask your server's administrator to check the email settings.");
            }
            return null;
        }
    }
}
