using System;
using Jellyfin.Plugin.DistributedTranscoding.Contracts;

namespace Jellyfin.Plugin.DistributedTranscoding.Worker;

/// <summary>
/// Translates the server's canonical paths to pod-local paths inside the ffmpeg argument string.
///
/// Phase 0 keeps the INPUT mapping as identity (server and worker share the media path), but always
/// rewrites the OUTPUT directory to a worker-local scratch dir. That way ffmpeg writes locally and
/// the ONLY way the files reach the server is the gRPC stream — which is exactly what the POC must prove.
/// </summary>
public static class PathMapper
{
    public static string RewriteArguments(string arguments, AssignJob assign, string localOutputDir)
    {
        var rewritten = arguments;

        // Output: server output_dir -> local scratch dir.
        if (!string.IsNullOrEmpty(assign.OutputDir))
        {
            rewritten = rewritten.Replace(assign.OutputDir, localOutputDir, StringComparison.Ordinal);
        }

        // Input: identity in Phase 0; honor an explicit map if provided.
        var map = assign.PathMap;
        if (map is not null && !string.IsNullOrEmpty(map.InputFrom))
        {
            rewritten = rewritten.Replace(map.InputFrom, map.InputTo, StringComparison.Ordinal);
        }

        return rewritten;
    }
}
