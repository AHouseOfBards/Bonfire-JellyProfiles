using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Profiles.Controllers
{
    /// <summary>
    /// A stable code for every error a Bonfire route returns, sent as the
    /// <c>X-Bonfire-Error</c> header. The body stays the human message it always was.
    ///
    /// <para><b>Why a header and not a JSON body.</b> The client script and the dashboard
    /// show the body as text, in about twenty places, and so would any app written against
    /// the routes so far. A header adds what a program needs without changing what a
    /// person sees, and nothing that reads the body today breaks.</para>
    ///
    /// <para><b>Why matched on the message here, rather than passed at each call site.</b>
    /// There are about two hundred error returns. Matching here gives every one of them a
    /// code today, and the rules sit in one table a harness can read. A message that
    /// matches no rule still gets the general code for its status, never none.</para>
    ///
    /// <para><b>Codes never change once released.</b> Messages can. A new case gets a new
    /// code; an old code is never reused for something else.</para>
    /// </summary>
    public static class ErrorCodes
    {
        /// <summary>The header carrying the code.</summary>
        public const string Header = "X-Bonfire-Error";

        /// <summary>A rule: when the message contains this text, this is its code. First match wins.</summary>
        internal static readonly IReadOnlyList<(string Contains, string Code)> Rules = new[]
        {
            // Who is asking.
            ("Only the master profile can", "not_main_account"),
            ("Only administrators can", "admin_only"),
            ("Unauthorized profile", "not_your_profile"),
            ("That profile does not belong", "not_your_profile"),
            ("does not let profiles choose a theme", "themes_locked"),

            // PINs and codes.
            ("Invalid PIN", "pin_wrong"),
            ("Invalid Master PIN", "pin_wrong"),
            ("PIN code must be", "pin_format"),
            ("has no PIN, so it cannot be opened from a shared Bonfire", "pin_needed_for_bonfire"),
            ("Invalid Bonfire Code", "bonfire_code_wrong"),
            ("Invalid code format", "bonfire_code_wrong"),
            ("Incorrect code", "code_wrong"),

            // Opening a profile.
            ("not allowed on this device", "device_not_allowed"),
            ("isn't allowed on this device", "device_not_allowed"),
            ("did not identify itself", "device_not_allowed"),
            ("allowed devices are known", "device_not_allowed"),
            ("account is turned off", "profile_disabled"),
            ("too many active sessions", "too_many_sessions"),
            ("Couldn't sign in to that profile", "sign_in_failed"),

            // Creating and editing.
            ("Maximum profile limit", "profile_limit"),
            ("already exists", "name_taken"),
            ("Enter a name for the profile", "name_invalid"),
            ("A profile name can", "name_invalid"),
            ("Cannot delete the master profile", "main_account_protected"),

            // Bonfire groups.
            ("You do not own a Bonfire group", "bonfire_not_owner"),
            ("You are not in any Bonfire group", "bonfire_not_member"),
            ("You cannot join your own Bonfire group", "bonfire_own_group"),
            ("Member not found in your Bonfire group", "bonfire_member_not_found"),

            // Announcements.
            ("Enter a message for the announcement", "announcement_invalid"),
            ("An announcement can be at most", "announcement_invalid"),
            ("Announcement severity must be", "announcement_invalid"),
            ("An announcement must end after it starts", "announcement_invalid"),
            ("Delete an announcement first", "announcement_limit"),
            ("No such announcement", "announcement_not_found"),

            // Maintenance. The refusal itself carries its code from the route: its text is
            // the administrator's own.
            ("maintenance message can be at most", "maintenance_invalid"),
            ("time maintenance ends must be", "maintenance_invalid"),

            // Things that are not there.
            ("Profile not found", "profile_not_found"),
            ("Profile mapping not found", "profile_not_found"),
            ("Missing profile", "profile_not_found"),
            ("Master user not found", "user_not_found"),
            ("Target user not found", "user_not_found"),
            ("Calling user not found", "user_not_found"),
            ("Underlying system user missing", "user_not_found"),
            ("That library no longer exists", "library_not_found"),
            ("No such avatar", "image_not_found"),
            ("That picture is no longer available", "image_not_found"),

            // Pictures.
            ("Not a supported image", "image_invalid"),
            ("That image could not be read", "image_invalid"),
            ("That image is too large", "image_invalid"),
            ("No image supplied", "image_invalid"),
            ("Choose a picture first", "image_invalid"),

            ("Plugin configuration missing", "not_configured"),
        };

        /// <summary>The code for an error, from its status and message.</summary>
        public static string For(int status, string? message)
        {
            if (status == StatusCodes.Status429TooManyRequests) return "rate_limited";

            if (!string.IsNullOrEmpty(message))
            {
                foreach (var (contains, code) in Rules)
                {
                    if (message.Contains(contains, StringComparison.OrdinalIgnoreCase)) return code;
                }
            }

            return status switch
            {
                StatusCodes.Status401Unauthorized => string.IsNullOrEmpty(message) ? "unauthenticated" : "forbidden",
                StatusCodes.Status403Forbidden => "forbidden",
                StatusCodes.Status404NotFound => "not_found",
                StatusCodes.Status500InternalServerError => "server_error",
                StatusCodes.Status503ServiceUnavailable => "unavailable",
                _ => "invalid_request",
            };
        }
    }

    /// <summary>
    /// Adds <see cref="ErrorCodes.Header"/> to every error from a Bonfire route, and sends a
    /// refusal to a signed-in caller as <c>403</c>.
    ///
    /// <para><b>The 403.</b> A 401 used to mean two things: the caller's token was rejected
    /// (empty body), or the caller is signed in and not allowed (a message). A client that
    /// signs out on any 401 signed a household out for asking to edit a profile as a
    /// profile. The client script tells them apart by the body (issue #15); everyone else
    /// should not have to. An empty 401 is still a 401.</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = true)]
    public sealed class BonfireErrorsAttribute : ResultFilterAttribute
    {
        /// <inheritdoc />
        public override void OnResultExecuting(ResultExecutingContext context)
        {
            int? status = null;
            string? message = null;

            switch (context.Result)
            {
                case ObjectResult obj when obj.StatusCode >= 400:
                    message = obj.Value as string;
                    if (obj.StatusCode == StatusCodes.Status401Unauthorized && !string.IsNullOrEmpty(message))
                    {
                        obj.StatusCode = StatusCodes.Status403Forbidden;
                    }

                    status = obj.StatusCode;
                    break;

                case StatusCodeResult plain when plain.StatusCode >= 400:
                    status = plain.StatusCode;
                    break;
            }

            // A route that set its own code keeps it. Maintenance does: its message is the
            // administrator's own words, so there is no text to match.
            if (status != null && !context.HttpContext.Response.Headers.ContainsKey(ErrorCodes.Header))
            {
                context.HttpContext.Response.Headers[ErrorCodes.Header] = ErrorCodes.For(status.Value, message);
            }

            base.OnResultExecuting(context);
        }
    }
}
