using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.DistributedTranscoding;

/// <summary>
/// Scheduled task that registers an index.html transformation with the
/// jellyfin-plugin-file-transformation plugin (if installed) to inject the Distributed Transcoding
/// dashboard client script. The script hides the native transcoding controls the plugin overrides and
/// links to the plugin's own settings page.
/// </summary>
public class FileTransformationIntegration : IScheduledTask
{
    private const string ScriptRoute = "/DistributedTranscoding/ClientScript";

    private readonly ILogger<FileTransformationIntegration> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileTransformationIntegration"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public FileTransformationIntegration(ILogger<FileTransformationIntegration> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Distributed Transcoding File Transformation Registration";

    /// <inheritdoc />
    public string Key => "DistributedTranscodingFileTransformation";

    /// <inheritdoc />
    public string Description => "Registers dashboard script injection with the File Transformation plugin";

    /// <inheritdoc />
    public string Category => "Distributed Transcoding";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger }
        };
    }

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);

        try
        {
            var ftAssembly = AssemblyLoadContext.All
                .SelectMany(ctx => ctx.Assemblies)
                .FirstOrDefault(asm => asm.FullName?.Contains("Jellyfin.Plugin.FileTransformation") ?? false);

            if (ftAssembly == null)
            {
                _logger.LogInformation("[DistributedTranscoding] File Transformation plugin not found. "
                    + "The transcoding dashboard controls will not be adjusted automatically.");
                progress.Report(100);
                return Task.CompletedTask;
            }

            var pluginInterface = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            if (pluginInterface == null)
            {
                _logger.LogWarning("[DistributedTranscoding] File Transformation plugin found but PluginInterface type "
                    + "not available. The installed version may be incompatible.");
                progress.Report(100);
                return Task.CompletedTask;
            }

            var registerMethod = pluginInterface.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
            if (registerMethod == null)
            {
                _logger.LogWarning("[DistributedTranscoding] File Transformation plugin found but RegisterTransformation "
                    + "method not available. The installed version may be incompatible.");
                progress.Report(100);
                return Task.CompletedTask;
            }

            var payload = new JObject
            {
                ["id"] = Plugin.PluginGuid,
                ["fileNamePattern"] = "index.html",
                ["callbackAssembly"] = typeof(FileTransformationIntegration).Assembly.FullName,
                ["callbackClass"] = typeof(FileTransformationIntegration).FullName,
                ["callbackMethod"] = nameof(TransformIndexHtml)
            };

            registerMethod.Invoke(null, new object?[] { payload });

            _logger.LogInformation("[DistributedTranscoding] Registered index.html transformation with File Transformation plugin.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DistributedTranscoding] Failed to register with File Transformation plugin. "
                + "The transcoding dashboard controls will not be adjusted automatically.");
        }

        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Callback invoked by the File Transformation plugin to inject the Distributed Transcoding
    /// dashboard script tag into index.html.
    /// </summary>
    /// <param name="payload">The transformation payload carrying the file <c>contents</c>.</param>
    /// <returns>The transformed file contents.</returns>
    public static string TransformIndexHtml(object payload)
    {
        var contents = payload is JObject jobj
            ? jobj["contents"]?.ToString()
            : payload?.GetType()
                .GetProperty("contents")?
                .GetValue(payload)?
                .ToString();

        if (string.IsNullOrEmpty(contents) || contents.Contains(ScriptRoute))
        {
            return contents ?? string.Empty;
        }

        var bodyEndIndex = contents.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyEndIndex >= 0)
        {
            return contents.Insert(bodyEndIndex, "    <script src=\"" + ScriptRoute + "\" defer></script>\n");
        }

        return contents;
    }
}
