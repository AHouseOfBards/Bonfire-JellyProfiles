using System;

namespace Jellyfin.Profiles.Controllers
{
    /// <summary>
    /// One allowance of PIN guesses per account, shared by every door a PIN goes through:
    /// the web switcher and verify-pin, the main-PIN check on household changes (which a
    /// child reaches through "more time"), another app's sign-in box and the Moonfin
    /// picker's keypad. Five a quarter hour and <see cref="DailyLimit"/> a day (R-1002,
    /// R-1298).
    ///
    /// <para><b>Why one.</b> There were three, each with its own count and only one with a
    /// daily cap: ten a quarter hour on the web, ten more on the main-PIN check, five and
    /// twenty a day on apps. About 1,900 guesses a day at the same main PIN, so four digits
    /// fell in about three days to anyone sitting at the gate signed in as the household.
    /// Counted on the account whose PIN it is, whichever door the guess came through.</para>
    ///
    /// <para><b>The day is spent only by a guess the quarter hour let through</b> (R-1001).
    /// Both were counted on every request, so twenty refused requests from anyone closed a
    /// profile's PIN for a day, renewable one request at a time.</para>
    ///
    /// <para><b>What it costs.</b> Someone who keeps guessing can stop the owner's PIN for
    /// the rest of the day. The password still works everywhere, and a right PIN starts
    /// both counts again; a guesser can't make a right PIN, so that reset can't be bought.
    /// Logan's decision: twenty a day, a fixed limit an administrator may lower.</para>
    /// </summary>
    internal static class PinBudget
    {
        /// <summary>Guesses a day per account. A limit, not a setting: never raised.</summary>
        internal const int DailyLimit = 20;

        private static string Key(Guid accountId) => accountId.ToString("N");

        /// <summary>
        /// Counts one guess at <paramref name="accountId"/>'s PIN before it is checked, and
        /// says whether it may be checked at all.
        /// </summary>
        internal static bool TryBegin(Guid accountId)
        {
            var key = Key(accountId);
            if (!RateLimiter.AccountPin.TryBegin(key)) return false;
            return RateLimiter.AccountPinDaily.TryBegin(key);
        }

        /// <summary>The PIN was right: both counts start again.</summary>
        internal static void End(Guid accountId)
        {
            var key = Key(accountId);
            RateLimiter.AccountPin.Reset(key);
            RateLimiter.AccountPinDaily.Reset(key);
        }

        /// <summary>How long until a guess is let through again; zero if it would be now.</summary>
        internal static TimeSpan RetryAfter(Guid accountId)
        {
            var key = Key(accountId);
            var a = RateLimiter.AccountPin.RetryAfter(key);
            var b = RateLimiter.AccountPinDaily.RetryAfter(key);
            return a > b ? a : b;
        }
    }
}
