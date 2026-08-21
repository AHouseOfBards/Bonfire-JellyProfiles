using System.Text.Json.Serialization;

namespace Jellyfin.Profiles.Models
{
    public class FileTransformationRequest
    {
        [JsonPropertyName("contents")]
        public string? Contents { get; set; }
    }
}
