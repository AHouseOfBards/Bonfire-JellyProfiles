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
            ("Set a PIN for your account first", "main_pin_required"),
            ("Enter your account's PIN", "pin_missing"),
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

            // Making a profile its own account.
            ("A username can use letters", "name_invalid"),
            ("A main account is already its own", "main_account_protected"),
            ("Set a password for the new account", "password_required"),

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

            // Email (1.7.2).
            ("Only the main account can do that", "not_main_account"),
            ("Email isn't set up on this server", "email_unavailable"),
            ("Set up email and send a test first", "email_unavailable"),
            ("PIN reset by email isn't available", "pin_reset_unavailable"),
            ("That email address isn't valid", "email_invalid"),
            ("Enter an address to send the test to", "email_invalid"),
            ("Add an email address first", "email_missing"),
            ("That code isn't right", "code_wrong"),
            ("That code has expired", "code_expired"),
            ("Send a code first", "code_missing"),
            ("The email couldn't be sent", "email_send_failed"),
            ("Port 465 isn't supported", "email_settings_invalid"),
            ("The port must be between", "email_settings_invalid"),
            ("Security must be 'starttls'", "email_settings_invalid"),
            ("That server name isn't valid", "email_settings_invalid"),
            ("The From address isn't valid", "email_settings_invalid"),
            ("The From name can be at most", "email_settings_invalid"),
            ("Fill in the server and the From address", "email_settings_invalid"),

            // Viewing hours and limits (1.7.2). The refusals of playback itself are written
            // by Auth.ViewingGate: outside_viewing_hours and daily_limit_reached.
            ("This profile has no viewing limits", "not_limited"),
            ("Limits are for profiles, not main accounts", "limits_for_profiles"),
            ("Too many viewing windows", "limits_invalid"),
            ("A viewing window isn't valid", "limits_invalid"),
            ("A daily limit is between", "limits_invalid"),
            ("More time is between", "limits_invalid"),
            ("That time zone isn't known", "time_zone_invalid"),

            // Other accounts in a switcher, and an account made a profile (1.7.2).
            ("That username or password isn't right", "credentials_wrong"),
            ("That account signs in another way", "admin_must_add"),
            ("That's your own account", "not_linkable"),
            ("That's a profile. Add its main account", "not_linkable"),
            ("That account is already here", "already_linked"),
            ("A household can show up to", "link_limit"),
            ("An administrator account needs a PIN", "admin_pin_required"),
            ("That account isn't shown there", "link_not_found"),
            ("User not found", "user_not_found"),
            ("An administrator can't be made a profile", "not_convertible"),
            ("That account is already a profile", "not_convertible"),
            ("Choose a main account, not a profile", "not_convertible"),
            ("Choose another account's household", "not_convertible"),
            ("It has profiles of its own", "not_convertible"),
            ("It's in a Bonfire. Leave it first", "not_convertible"),

            // Custom themes and fonts (1.7.2). "That theme isn't valid" and an unreadable font
            // file carry their codes from the route: theme_invalid and font_invalid.
            ("Delete a custom theme first", "theme_limit"),
            ("No such theme", "theme_not_found"),
            ("That's the default theme. Choose another default first", "theme_in_use"),
            ("Only built-in themes can be hidden", "theme_invalid"),
            ("Bonfire can't be hidden", "theme_invalid"),
            ("Send the list of themes to hide", "theme_invalid"),
            ("Confirm you have the right to use this font", "font_rights"),
            ("A font falls back to", "font_invalid"),
            ("A font name can use", "font_invalid"),
            ("That font is larger than", "font_invalid"),
            ("Choose a WOFF2 font file", "font_invalid"),
            ("Bonfire already has a font called", "font_taken"),
            ("Delete a font first", "font_limit"),
            ("No such font", "font_not_found"),
            ("A theme uses that font", "font_in_use"),
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
