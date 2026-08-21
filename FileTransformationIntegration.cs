using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Jellyfin.Profiles.Configuration;
using Jellyfin.Profiles.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles
{
    public static class FileTransformationIntegration
    {
        private static readonly Guid TransformationId = Guid.Parse("7a4d84a5-8c5a-4a9e-9bc1-0b6d0a0cf7c6");
        private static readonly Regex BodyScriptRegex = new(
            @"<script[^>]*src=[\"'][^\"']*/plugins/profiles/profiles\.js[^\"']*[\"'][^>]*>\s*(</script>)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HeadScriptRegex = new(
            @"<script id=[\"']jpf-eh[\"'][^>]*>[\s\S]*?</script>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool DependencyDetected { get; private set; }
        public static bool RegistrationSucceeded { get; private set; }
        public static string? FailureReason { get; private set; }

        public static string Transform(FileTransformationRequest request)
        {
            if (request?.Contents == null)
            {
                return request?.Contents ?? string.Empty;
            }

            return ProfilesHtmlTransformation.Transform(request.Contents);
        }

        public static void Register(ILogger logger)
        {
            DependencyDetected = false;
            RegistrationSucceeded = false;
            FailureReason = null;

            if (Plugin.Instance == null || ClientInjectionModes.Normalize(Plugin.Instance.Configuration.ClientInjectionMode) != ClientInjectionModes.FileTransformation)
            {
                return;
            }

            try
            {
                Assembly? assembly = AssemblyLoadContext.All
                    .SelectMany(context => context.Assemblies)
                    .FirstOrDefault(candidate => candidate.FullName?.Contains(".FileTransformation") ?? false);

                DependencyDetected = assembly != null;
                if (assembly == null)
                {
                    FailureReason = "The File Transformation plugin is not loaded.";
                    logger.LogWarning("ProfilesPlugin: File Transformation mode is enabled, but the File Transformation plugin was not found.");
                    return;
                }

                Type? interfaceType = assembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
                MethodInfo? registerMethod = interfaceType?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
                if (registerMethod == null)
                {
                    FailureReason = "The loaded File Transformation plugin does not expose its registration API.";
                    logger.LogWarning("ProfilesPlugin: File Transformation registration API was not found.");
                    return;
                }

                Type? jsonObjectType = assembly.GetType("Newtonsoft.Json.Linq.JObject");
                MethodInfo? parseMethod = jsonObjectType?.GetMethod("Parse", new[] { typeof(string) });
                if (parseMethod == null)
                {
                    FailureReason = "The loaded File Transformation plugin does not expose JObject.Parse.";
                    logger.LogWarning("ProfilesPlugin: Could not create the File Transformation registration payload.");
                    return;
                }

                string payloadJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    id = TransformationId,
                    fileNamePattern = "index.html",
                    callbackAssembly = typeof(FileTransformationIntegration).Assembly.FullName,
                    callbackClass = typeof(FileTransformationIntegration).FullName,
                    callbackMethod = nameof(Transform)
                });
                object payload = parseMethod.Invoke(null, new object?[] { payloadJson })!;

                registerMethod.Invoke(null, new[] { payload });
                RegistrationSucceeded = true;
                logger.LogInformation("ProfilesPlugin: Registered index.html transformation with File Transformation.");
            }
            catch (Exception ex)
            {
                FailureReason = "Could not register with File Transformation: " + (ex.InnerException?.Message ?? ex.Message);
                logger.LogError(ex, "ProfilesPlugin: Could not register the File Transformation callback.");
            }
        }
    }

    public sealed class FileTransformationRegistrationTask : IHostedService
    {
        private readonly ILogger<FileTransformationRegistrationTask> _logger;

        public FileTransformationRegistrationTask(ILogger<FileTransformationRegistrationTask> logger)
        {
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            FileTransformationIntegration.Register(_logger);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    internal static class ProfilesHtmlTransformation
    {
        private const string HeadScript =
            "<script id=\"jpf-eh\">" +
            "!function(){" +
            "if(localStorage.getItem('jpf-sw')){" +
            "var h=document.documentElement;" +
            "h.style.opacity='0';" +
            "h.style.background='#101010';" +
            "h.style.colorScheme='dark';" +
            "window.__jpReveal=setTimeout(function(){" +
            "h.style.opacity='';h.style.background='';h.style.colorScheme='';},4e3);" +
            "localStorage.removeItem('jpf-sw');}}();" +
            "</script>";

        public static string Transform(string html)
        {
            string scriptTag = $"<script src=\"/plugins/profiles/profiles.js?v={ProfilesBootstrapTask.ScriptVersion}\" defer></script>";
            if (HeadScriptRegex.IsMatch(html))
            {
                html = HeadScriptRegex.Replace(html, _ => HeadScript);
            }
            else
            {
                int headIndex = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
                if (headIndex >= 0)
                {
                    html = html.Insert(headIndex + 6, Environment.NewLine + HeadScript);
                }
            }

            if (BodyScriptRegex.IsMatch(html))
            {
                html = BodyScriptRegex.Replace(html, _ => scriptTag);
            }
            else
            {
                int bodyIndex = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (bodyIndex >= 0)
                {
                    html = html.Insert(bodyIndex, scriptTag + Environment.NewLine);
                }
            }

            return html;
        }
    }
}
