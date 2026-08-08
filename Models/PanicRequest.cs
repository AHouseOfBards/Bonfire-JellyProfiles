namespace Jellyfin.Profiles.Models
{
    public class PanicRequest
    {
        /// <summary>
        /// The emergency disable code. On the admin endpoint, an empty value clears the
        /// configured code and turns the feature off.
        /// </summary>
        public string? Code { get; set; }
    }
}
