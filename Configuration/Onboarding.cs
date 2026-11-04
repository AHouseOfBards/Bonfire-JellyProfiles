using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>
    /// First-time setup: a few screens a main account is walked through the first time it
    /// opens Bonfire in a full app (jellyfin-web and the apps that wrap it). Off by default.
    ///
    /// <para><b>Who sees it.</b> Main accounts only, never a profile, and once per account
    /// rather than per device: what was seen is stored here, so finishing on the phone means
    /// the TV does not ask again. Accounts that already existed when the administrator first
    /// turned it on are counted as having seen it; "show again to everyone" bumps
    /// <see cref="Generation"/>, which un-sees it for every account at once.</para>
    ///
    /// <para><b>Lists start empty.</b> Jellyfin reads this through XmlSerializer, which adds
    /// to a list that a constructor has already filled instead of replacing it, so a default
    /// of five steps would come back as ten after the first save. Nor can null stand for
    /// "the default": the serializer creates every list it reads, element or not, so a null
    /// came back from the first save as an empty list and the setup offered nothing. Whether
    /// the administrator has chosen is therefore its own flag, <see cref="StepsChosen"/>.</para>
    /// </summary>
    public class OnboardingSettings
    {
        public bool Enabled { get; set; }

        /// <summary>The steps offered, as <see cref="OnboardingSteps"/> ids. Read only once
        /// <see cref="StepsChosen"/> is set; until then every step is offered.</summary>
        public List<string> Steps { get; set; } = new();

        /// <summary>The administrator has picked the steps. An empty pick offers none.</summary>
        public bool StepsChosen { get; set; }

        /// <summary>Steps that cannot be skipped. A step not offered is ignored here.</summary>
        public List<string> RequiredSteps { get; set; } = new();

        /// <summary>The administrator's own words on the first screen. Plain text.</summary>
        public string WelcomeText { get; set; } = string.Empty;

        /// <summary>Bumped by "show again to everyone". An account has seen the current
        /// setup when it has a record at this generation or later.</summary>
        public int Generation { get; set; } = 1;

        /// <summary>Set the first time the setup is turned on, when every account that
        /// already exists is counted as having seen it. Never cleared, so turning it off and
        /// on again does not skip accounts made in between.</summary>
        public bool ExistingAccountsExempted { get; set; }

        /// <summary>Who has seen which generation. One row per main account.</summary>
        public List<OnboardingRecord> Seen { get; set; } = new();

        /// <summary>Longest welcome accepted, the same as an announcement.</summary>
        public const int MaxWelcomeLength = Announcements.MaxMessageLength;

        /// <summary>A copy for <see cref="PluginConfiguration.SnapshotForReading"/>.</summary>
        public OnboardingSettings CopyForReading()
        {
            var copy = (OnboardingSettings)MemberwiseClone();
            copy.Steps = Steps?.ToList() ?? new List<string>();
            copy.RequiredSteps = RequiredSteps?.ToList() ?? new List<string>();
            copy.Seen = Seen?.Select(r => new OnboardingRecord { UserId = r.UserId, Generation = r.Generation }).ToList()
                        ?? new List<OnboardingRecord>();
            return copy;
        }

        public List<string> OfferedSteps() => StepsChosen ? OnboardingSteps.Normalize(Steps) : OnboardingSteps.All.ToList();

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
        public const string FirstProfile = "firstProfile";
        public const string Switcher = "switcher";
        public const string Theme = "theme";

        public static readonly string[] All = { Welcome, MainPin, FirstProfile, Switcher, Theme };

        public static bool IsKnown(string? id) => id != null && All.Any(s => string.Equals(s, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>Known ids only, each once, in the canonical order, spelled canonically.</summary>
        public static List<string> Normalize(IEnumerable<string>? ids)
        {
            var wanted = (ids ?? Enumerable.Empty<string>()).Where(IsKnown).ToList();
            return All.Where(s => wanted.Any(w => string.Equals(w, s, StringComparison.OrdinalIgnoreCase))).ToList();
        }
    }
}
