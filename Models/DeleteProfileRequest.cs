using System;

namespace Jellyfin.Profiles.Models
{
    public class DeleteProfileRequest
    {
        public Guid ProfileId { get; set; }
        public string? MasterPin { get; set; }

        /// <summary>
        /// The password the account signs in with afterwards, for a profile that was an account
        /// and is kept (see <c>ProfileMapping.KeepAccountOnDelete</c>). Empty gives it one nobody
        /// knows, to be set in Dashboard → Users. Ignored for any other profile.
        /// </summary>
        public string? Password { get; set; }
    }
}
