using System; // TEMPORARY (R-1032/R-1033): remove before 2.0
using System.Collections.Concurrent; // TEMPORARY (R-1032/R-1033): remove before 2.0
using System.IO; // TEMPORARY (R-1032/R-1033): remove before 2.0
using System.Text; // TEMPORARY (R-1032/R-1033): remove before 2.0
using System.Text.Json; // TEMPORARY (R-1032/R-1033): remove before 2.0
using System.Threading; // TEMPORARY (R-1032/R-1033): remove before 2.0
using System.Threading.Tasks; // TEMPORARY (R-1032/R-1033): remove before 2.0
using Microsoft.AspNetCore.Http; // TEMPORARY (R-1032/R-1033): remove before 2.0
using Microsoft.AspNetCore.Http.Features; // TEMPORARY (R-1032/R-1033): remove before 2.0
using Microsoft.Extensions.Logging; // TEMPORARY (R-1032/R-1033): remove before 2.0

namespace Jellyfin.Profiles.Auth // TEMPORARY (R-1032/R-1033): remove before 2.0
{ // TEMPORARY (R-1032/R-1033): remove before 2.0
    /// <summary>
    /// Temporary diagnostic logging for the profile picker, for one beta. It answers two
    /// questions about requests made while a profile is acting: whether a stream or playlist
    /// request carries a token in its query (and whether a playlist hands one back in its
    /// URLs), and whether a PlaybackInfo or LiveStreams/Open body names a UserId and whose.
    /// Facts only (yes/no, main/profile/other). No token, id, name or URL value is logged.
    /// Every line carries the marker below so the whole thing can be found and removed.
    /// </summary>
    internal static class PickerDiag // TEMPORARY (R-1032/R-1033): remove before 2.0
    { // TEMPORARY (R-1032/R-1033): remove before 2.0
        internal const int Cap = 50; // TEMPORARY (R-1032/R-1033): remove before 2.0
        private const int MaxBody = 1048576; // TEMPORARY (R-1032/R-1033): remove before 2.0
        private static int _written; // TEMPORARY (R-1032/R-1033): remove before 2.0
        private static readonly ConcurrentDictionary<string, byte> Seen = new(StringComparer.Ordinal); // TEMPORARY (R-1032/R-1033): remove before 2.0

        /// <summary>For the harness: a fresh server start.</summary>
        internal static void Reset() // TEMPORARY (R-1032/R-1033): remove before 2.0
        { // TEMPORARY (R-1032/R-1033): remove before 2.0
            _written = 0; // TEMPORARY (R-1032/R-1033): remove before 2.0
            Seen.Clear(); // TEMPORARY (R-1032/R-1033): remove before 2.0
        } // TEMPORARY (R-1032/R-1033): remove before 2.0

        // A line already said is not said again, so the cap is not spent on a thousand segments.
        private static void Say(ILogger logger, string line) // TEMPORARY (R-1032/R-1033): remove before 2.0
        { // TEMPORARY (R-1032/R-1033): remove before 2.0
            if (!Seen.TryAdd(line, 0)) return; // TEMPORARY (R-1032/R-1033): remove before 2.0
            if (Interlocked.Increment(ref _written) > Cap) return; // TEMPORARY (R-1032/R-1033): remove before 2.0
            logger.LogInformation("ProfilesPlugin: [picker-diag] {Line}", line); // TEMPORARY (R-1032/R-1033): remove before 2.0
        } // TEMPORARY (R-1032/R-1033): remove before 2.0

        internal static string? Kind(string path) // TEMPORARY (R-1032/R-1033): remove before 2.0
        { // TEMPORARY (R-1032/R-1033): remove before 2.0
            if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) return "playlist"; // TEMPORARY (R-1032/R-1033): remove before 2.0
            if (path.Contains("/hls", StringComparison.OrdinalIgnoreCase)) return "segment"; // TEMPORARY (R-1032/R-1033): remove before 2.0
            if (path.Contains("/stream", StringComparison.OrdinalIgnoreCase)) return "stream"; // TEMPORARY (R-1032/R-1033): remove before 2.0
            return null; // TEMPORARY (R-1032/R-1033): remove before 2.0
        } // TEMPORARY (R-1032/R-1033): remove before 2.0

        private static string? BodyKind(HttpRequest request) // TEMPORARY (R-1032/R-1033): remove before 2.0
        { // TEMPORARY (R-1032/R-1033): remove before 2.0
            if (!HttpMethods.IsPost(request.Method)) return null; // TEMPORARY (R-1032/R-1033): remove before 2.0
            var path = request.Path.Value ?? string.Empty; // TEMPORARY (R-1032/R-1033): remove before 2.0
            if (path.EndsWith("/PlaybackInfo", StringComparison.OrdinalIgnoreCase)) return "PlaybackInfo"; // TEMPORARY (R-1032/R-1033): remove before 2.0
            if (path.EndsWith("/LiveStreams/Open", StringComparison.OrdinalIgnoreCase)) return "LiveStreams/Open"; // TEMPORARY (R-1032/R-1033): remove before 2.0
            return null; // TEMPORARY (R-1032/R-1033): remove before 2.0
        } // TEMPORARY (R-1032/R-1033): remove before 2.0

        /// <summary>Called with the request as the app sent it, before the profile's token and id are swapped in.</summary>
        internal static async Task BeforeSwapAsync(HttpContext context, Guid masterId, Guid profileId, ILogger logger) // TEMPORARY (R-1032/R-1033): remove before 2.0
        { // TEMPORARY (R-1032/R-1033): remove before 2.0
            try // TEMPORARY (R-1032/R-1033): remove before 2.0
            { // TEMPORARY (R-1032/R-1033): remove before 2.0
                var request = context.Request; // TEMPORARY (R-1032/R-1033): remove before 2.0
                var kind = Kind(request.Path.Value ?? string.Empty); // TEMPORARY (R-1032/R-1033): remove before 2.0
                if (kind != null) // TEMPORARY (R-1032/R-1033): remove before 2.0
                { // TEMPORARY (R-1032/R-1033): remove before 2.0
                    var carried = !string.IsNullOrEmpty(request.Query["api_key"].ToString()) || !string.IsNullOrEmpty(request.Query["ApiKey"].ToString()); // TEMPORARY (R-1032/R-1033): remove before 2.0
                    Say(logger, kind + " request, token in query: " + (carried ? "yes" : "no")); // TEMPORARY (R-1032/R-1033): remove before 2.0
                } // TEMPORARY (R-1032/R-1033): remove before 2.0

                var bodyKind = BodyKind(request); // TEMPORARY (R-1032/R-1033): remove before 2.0
                if (bodyKind != null) Say(logger, bodyKind + " body, UserId: " + await ReadUserIdAsync(request, masterId, profileId).ConfigureAwait(false)); // TEMPORARY (R-1032/R-1033): remove before 2.0
            } // TEMPORARY (R-1032/R-1033): remove before 2.0
            catch (Exception) // TEMPORARY (R-1032/R-1033): remove before 2.0
            { // TEMPORARY (R-1032/R-1033): remove before 2.0
                // Diagnostics never get in the way of a request.
            } // TEMPORARY (R-1032/R-1033): remove before 2.0
        } // TEMPORARY (R-1032/R-1033): remove before 2.0

        private static async Task<string> ReadUserIdAsync(HttpRequest request, Guid masterId, Guid profileId) // TEMPORARY (R-1032/R-1033): remove before 2.0
        { // TEMPORARY (R-1032/R-1033): remove before 2.0
            if (request.ContentLength is > MaxBody) return "not read (too large)"; // TEMPORARY (R-1032/R-1033): remove before 2.0
            try // TEMPORARY (R-1032/R-1033): remove before 2.0
            { // TEMPORARY (R-1032/R-1033): remove before 2.0
                request.EnableBuffering(30720, 4194304); // TEMPORARY (R-1032/R-1033): remove before 2.0
                string text; // TEMPORARY (R-1032/R-1033): remove before 2.0
                using (var reader = new StreamReader(request.Body, Encoding.UTF8, false, 1024, leaveOpen: true)) // TEMPORARY (R-1032/R-1033): remove before 2.0
                { // TEMPORARY (R-1032/R-1033): remove before 2.0
                    text = await reader.ReadToEndAsync().ConfigureAwait(false); // TEMPORARY (R-1032/R-1033): remove before 2.0
                } // TEMPORARY (R-1032/R-1033): remove before 2.0

                request.Body.Position = 0; // TEMPORARY (R-1032/R-1033): remove before 2.0
                return Who(text, masterId, profileId); // TEMPORARY (R-1032/R-1033): remove before 2.0
            } // TEMPORARY (R-1032/R-1033): remove before 2.0
            catch (Exception) // TEMPORARY (R-1032/R-1033): remove before 2.0
            { // TEMPORARY (R-1032/R-1033): remove before 2.0
                try { request.Body.Position = 0; } catch (Exception) { } // TEMPORARY (R-1032/R-1033): remove before 2.0
                return "unreadable"; // TEMPORARY (R-1032/R-1033): remove before 2.0
            } // TEMPORARY (R-1032/R-1033): remove before 2.0
        } // TEMPORARY (R-1032/R-1033): remove before 2.0

        internal static string Who(string json, Guid masterId, Guid profileId) // TEMPORARY (R-1032/R-1033): remove before 2.0
        { // TEMPORARY (R-1032/R-1033): remove before 2.0
            try // TEMPORARY (R-1032/R-1033): remove before 2.0
            { // TEMPORARY (R-1032/R-1033): remove before 2.0
                using var doc = JsonDocument.Parse(json); // TEMPORARY (R-1032/R-1033): remove before 2.0
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return "absent"; // TEMPORARY (R-1032/R-1033): remove before 2.0
                foreach (var property in doc.RootElement.EnumerateObject()) // TEMPORARY (R-1032/R-1033): remove before 2.0
                { // TEMPORARY (R-1032/R-1033): remove before 2.0
                    if (!string.Equals(property.Name, "UserId", StringComparison.OrdinalIgnoreCase)) continue; // TEMPORARY (R-1032/R-1033): remove before 2.0
                    if (property.Value.ValueKind != JsonValueKind.String || !Guid.TryParse(property.Value.GetString(), out var id)) return "other"; // TEMPORARY (R-1032/R-1033): remove before 2.0
                    return id == masterId ? "main" : id == profileId ? "profile" : "other"; // TEMPORARY (R-1032/R-1033): remove before 2.0
                } // TEMPORARY (R-1032/R-1033): remove before 2.0

                return "absent"; // TEMPORARY (R-1032/R-1033): remove before 2.0
            } // TEMPORARY (R-1032/R-1033): remove before 2.0
            catch (JsonException) // TEMPORARY (R-1032/R-1033): remove before 2.0
            { // TEMPORARY (R-1032/R-1033): remove before 2.0
                return "unreadable"; // TEMPORARY (R-1032/R-1033): remove before 2.0
            } // TEMPORARY (R-1032/R-1033): remove before 2.0
        } // TEMPORARY (R-1032/R-1033): remove before 2.0

        /// <summary>
        /// Forwards a playlist request and looks at what came back, then sends exactly those
        /// bytes on. Unlike ForwardAndEditAsync it touches no request header, so the app gets
        /// the response it would have got.
        /// </summary>
        internal static async Task ForwardAndPeekAsync(HttpContext context, Func<Task> forward, ILogger logger) // TEMPORARY (R-1032/R-1033): remove before 2.0
        { // TEMPORARY (R-1032/R-1033): remove before 2.0
            var original = context.Features.Get<IHttpResponseBodyFeature>(); // TEMPORARY (R-1032/R-1033): remove before 2.0
            if (original == null) // TEMPORARY (R-1032/R-1033): remove before 2.0
            { // TEMPORARY (R-1032/R-1033): remove before 2.0
                await forward().ConfigureAwait(false); // TEMPORARY (R-1032/R-1033): remove before 2.0
                return; // TEMPORARY (R-1032/R-1033): remove before 2.0
            } // TEMPORARY (R-1032/R-1033): remove before 2.0

            byte[] produced; // TEMPORARY (R-1032/R-1033): remove before 2.0
            using (var captured = new MemoryStream()) // TEMPORARY (R-1032/R-1033): remove before 2.0
            { // TEMPORARY (R-1032/R-1033): remove before 2.0
                var capture = new StreamResponseBodyFeature(captured); // TEMPORARY (R-1032/R-1033): remove before 2.0
                context.Features.Set<IHttpResponseBodyFeature>(capture); // TEMPORARY (R-1032/R-1033): remove before 2.0
                try // TEMPORARY (R-1032/R-1033): remove before 2.0
                { // TEMPORARY (R-1032/R-1033): remove before 2.0
                    await forward().ConfigureAwait(false); // TEMPORARY (R-1032/R-1033): remove before 2.0
                    await capture.CompleteAsync().ConfigureAwait(false); // TEMPORARY (R-1032/R-1033): remove before 2.0
                } // TEMPORARY (R-1032/R-1033): remove before 2.0
                finally // TEMPORARY (R-1032/R-1033): remove before 2.0
                { // TEMPORARY (R-1032/R-1033): remove before 2.0
                    context.Features.Set(original); // TEMPORARY (R-1032/R-1033): remove before 2.0
                } // TEMPORARY (R-1032/R-1033): remove before 2.0

                produced = captured.ToArray(); // TEMPORARY (R-1032/R-1033): remove before 2.0
            } // TEMPORARY (R-1032/R-1033): remove before 2.0

            // The captured bytes are always sent on, even if the response has somehow started:
            // dropping them would cut the playlist short. Only the look is skipped then.
            // TEMPORARY (R-1032/R-1033): remove before 2.0

            try // TEMPORARY (R-1032/R-1033): remove before 2.0
            { // TEMPORARY (R-1032/R-1033): remove before 2.0
                if (!context.Response.HasStarted // TEMPORARY (R-1032/R-1033): remove before 2.0
                    && context.Response.StatusCode == StatusCodes.Status200OK // TEMPORARY (R-1032/R-1033): remove before 2.0
                    && !context.Response.Headers.ContainsKey("Content-Encoding") // TEMPORARY (R-1032/R-1033): remove before 2.0
                    && produced.Length > 0 && produced.Length <= MaxBody) // TEMPORARY (R-1032/R-1033): remove before 2.0
                { // TEMPORARY (R-1032/R-1033): remove before 2.0
                    var text = Encoding.UTF8.GetString(produced); // TEMPORARY (R-1032/R-1033): remove before 2.0
                    var carried = text.Contains("api_key=", StringComparison.OrdinalIgnoreCase) || text.Contains("ApiKey=", StringComparison.OrdinalIgnoreCase); // TEMPORARY (R-1032/R-1033): remove before 2.0
                    Say(logger, "playlist response, token in its URLs: " + (carried ? "yes" : "no")); // TEMPORARY (R-1032/R-1033): remove before 2.0
                } // TEMPORARY (R-1032/R-1033): remove before 2.0
            } // TEMPORARY (R-1032/R-1033): remove before 2.0
            catch (Exception) // TEMPORARY (R-1032/R-1033): remove before 2.0
            { // TEMPORARY (R-1032/R-1033): remove before 2.0
                // Looking is optional; sending is not.
            } // TEMPORARY (R-1032/R-1033): remove before 2.0

            if (produced.Length > 0) await context.Response.Body.WriteAsync(produced, context.RequestAborted).ConfigureAwait(false); // TEMPORARY (R-1032/R-1033): remove before 2.0
        } // TEMPORARY (R-1032/R-1033): remove before 2.0
    } // TEMPORARY (R-1032/R-1033): remove before 2.0
} // TEMPORARY (R-1032/R-1033): remove before 2.0
