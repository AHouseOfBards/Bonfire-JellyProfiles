using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>
    /// First-time setup: a few screens a main account is walked through the first time it opens
    /// Bonfire in a full app (jellyfin-web and the apps that wrap it). Off by default.
    ///
    /// <para><b>Who sees it.</b> Main accounts only, never a profile, and once per account rather
    /// than per device: finishing on the phone means the TV does not ask again. Accounts that
    /// existed when the administrator first turned it on count as having seen it; "show again to
    /// everyone" bumps <see cref="Generation"/>, which un-sees it for every account at once.</para>
    ///
    /// <para><b>Every step is always offered.</b> There used to be a list of steps to show, a flag
    /// for whether the administrator had picked, and the administrator's own welcome text. All three
    /// were cut: the Show boxes had no entry for the email step, so saving the tab dropped it for
    /// good, and nobody needed to hide a step each person can skip. A configuration that still has
    /// the elements loads (XmlSerializer ignores what the class no longer has) and the next save
    /// leaves them out; see the "first-time setup steps and welcome" case in tests/cs/goldenconfig.</para>
    /// </summary>
    public class OnboardingSettings
    {
        public bool Enabled { get; set; }

        /// <summary>The administrator has been asked, on a new install, whether they want
        /// the setup, and answered either way. Never read by the server: the dashboard asks
        /// until it is set, and only while nothing has been configured yet.</summary>
        public bool Asked { get; set; }

        /// <summary>The Status tab's Getting started items the administrator dismissed, by
        /// key (<see cref="GettingStartedItems"/>). Never read by the server: the dashboard
        /// leaves them out. Each key at most once, so the list can never grow past the set.</summary>
        public List<string> Dismissed { get; set; } = new();

        /// <summary>The keys the dashboard's Getting started list uses.</summary>
        public static readonly string[] GettingStartedItems = { "pins", "zone" };

        // There used to be a RequiredSteps list here ("can't skip" per step). It was only a client
        // screen with no Skip button, and nothing on the server held anyone to it (RequireMainPin
        // does that). Every step can be skipped now. A file or backup that still has the element
        // loads; the next save leaves it out.

        /// <summary>Bumped by "show again to everyone". An account has seen the current
        /// setup when it has a record at this generation or later.</summary>
        public int Generation { get; set; } = 1;

        /// <summary>Set the first time the setup is turned on, when every account that
        /// already exists is counted as having seen it. Never cleared, so turning it off and
        /// on again does not skip accounts made in between.</summary>
        public bool ExistingAccountsExempted { get; set; }

        /// <summary>Who has seen which generation. One row per main account.</summary>
        public List<OnboardingRecord> Seen { get; set; } = new();

        /// <summary>A copy for <see cref="PluginConfiguration.SnapshotForReading"/>.</summary>
        public OnboardingSettings CopyForReading()
        {
            var copy = (OnboardingSettings)MemberwiseClone();
            copy.Seen = Seen?.Select(r => new OnboardingRecord { UserId = r.UserId, Generation = r.Generation }).ToList()
                        ?? new List<OnboardingRecord>();
            copy.Dismissed = Dismissed?.ToList() ?? new List<string>();
            return copy;
        }

        /// <summary>Every step, in the order they are shown.</summary>
        public List<string> OfferedSteps() => OnboardingSteps.All.ToList();

        public bool HasSeen(Guid userId) => Seen.Any(r => r.UserId == userId && r.Generation >= Generation);

        /// <summary>Records that the account has seen the current setup. Call under ConfigLock.</summary>
        public void MarkSeen(Guid userId)
        {
            var row = Seen.FirstOrDefault(r => r.UserId == userId);
            if (row == null) Seen.Add(new OnboardingRecord { UserId = userId, Generation = Generation });
            else row.Generation = Generation;
        }
    }

    public class OnboardingRecord
    {
        public Guid UserId { get; set; }
        public int Generation { get; set; }
    }

    /// <summary>The steps first-time setup can offer, in the order they are shown.</summary>
    public static class OnboardingSteps
    {
        public const string Welcome = "welcome";
        public const string MainPin = "mainPin";

        /// <summary>An address for PIN reset and notices. Offered only while email works.</summary>
        public const string ResetEmail = "resetEmail";
        public const string FirstProfile = "firstProfile";
        public const string Switcher = "switcher";
        public const string Theme = "theme";

        public static readonly string[] All = { Welcome, MainPin, ResetEmail, FirstProfile, Switcher, Theme };
    }
}
