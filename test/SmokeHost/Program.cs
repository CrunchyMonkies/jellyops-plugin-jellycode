using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Jellyfin.Plugin.DistributedTranscoding.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedTranscoding.SmokeHost;

// Transport-level smoke test for the worker + proto + data plane, WITHOUT Jellyfin.
// 1. Starts a minimal TranscodeMesh gRPC server (h2c).
// 2. On Register, assigns a job whose "ffmpeg" is a fake script that writes HLS-like files.
// 3. Collects streamed SegmentData and verifies the expected files round-tripped.
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        const int port = 19090;
        var root = Path.Combine(Path.GetTempPath(), "jellycode-smoke-" + Environment.ProcessId);
        var collectDir = Path.Combine(root, "server-out");
        var scratch = Path.Combine(root, "worker-scratch");
        Directory.CreateDirectory(collectDir);
        Directory.CreateDirectory(scratch);

        var fakeFfmpeg = WriteFakeFfmpeg(root);
        var workerDll = ResolveWorkerDll();

        Console.WriteLine($"[smoke] root={root}");
        Console.WriteLine($"[smoke] worker={workerDll}");

        var state = new SmokeState(collectDir);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(port, lo => lo.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(state);
        var app = builder.Build();
        app.MapGrpcService<SmokeMeshService>();

        await app.StartAsync().ConfigureAwait(false);
        Console.WriteLine($"[smoke] gRPC server up on :{port}");

        using var worker = StartWorker(workerDll, port, fakeFfmpeg, scratch);

        var ok = await state.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30)).ContinueWith(t => t.Status == TaskStatus.RanToCompletion && t.Result).ConfigureAwait(false);

        try
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
        }

        await app.StopAsync().ConfigureAwait(false);

        var expected = new[] { "out.m3u8", "out0.ts", "out1.ts", "out2.ts" };
        var allPresent = true;
        foreach (var name in expected)
        {
            var path = Path.Combine(collectDir, name);
            var exists = File.Exists(path);
            var len = exists ? new FileInfo(path).Length : 0;
            Console.WriteLine($"[smoke] {(exists ? "OK " : "MISS")} {name} ({len} bytes)");
            allPresent &= exists && len > 0;
        }

        Console.WriteLine(allPresent ? "[smoke] PASS" : "[smoke] FAIL");
        return allPresent ? 0 : 1;
    }

    private static Process StartWorker(string workerDll, int port, string fakeFfmpeg, string scratch)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false
        };
        psi.ArgumentList.Add(workerDll);
        psi.ArgumentList.Add("--server");
        psi.ArgumentList.Add($"http://127.0.0.1:{port}");
        psi.ArgumentList.Add("--worker-id");
        psi.ArgumentList.Add("smoke");
        psi.ArgumentList.Add("--ffmpeg");
        psi.ArgumentList.Add(fakeFfmpeg);
        psi.ArgumentList.Add("--max-concurrent");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("--scratch");
        psi.ArgumentList.Add(scratch);
        return Process.Start(psi)!;
    }

    private static string WriteFakeFfmpeg(string root)
    {
        var path = Path.Combine(root, "fake-ffmpeg.sh");
        const string script = "#!/usr/bin/env bash\n"
            + "set -e\n"
            + "rm -f out.m3u8\n"
            + "for i in 0 1 2; do\n"
            + "  printf 'segment-%s-payload' \"$i\" > \"out$i.ts\"\n"
            + "  sleep 0.2\n"
            + "done\n"
            + "printf '#EXTM3U\\n#EXT-X-VERSION:3\\nout0.ts\\nout1.ts\\nout2.ts\\n#EXT-X-ENDLIST\\n' > out.m3u8\n"
            + "sleep 0.2\n";
        File.WriteAllText(path, script);
        var chmod = Process.Start(new ProcessStartInfo { FileName = "chmod", ArgumentList = { "+x", path }, UseShellExecute = false })!;
        chmod.WaitForExit();
        return path;
    }

    private static string ResolveWorkerDll()
    {
        var here = AppContext.BaseDirectory;
        // test/SmokeHost/bin/Release/net10.0 -> repo root
        var repo = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", ".."));
        var dll = Path.Combine(repo, "src", "Worker", "bin", "Release", "net10.0", "Jellyfin.Plugin.DistributedTranscoding.Worker.dll");
        return dll;
    }
}

public sealed class SmokeState
{
    public SmokeState(string collectDir) => CollectDir = collectDir;

    public string CollectDir { get; }

    public TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class SmokeMeshService : TranscodeMesh.TranscodeMeshBase
{
    private readonly SmokeState _state;
    private readonly Dictionary<string, FileStream> _open = new(StringComparer.Ordinal);

    public SmokeMeshService(SmokeState state) => _state = state;

    public override async Task Connect(
        IAsyncStreamReader<WorkerFrame> requestStream,
        IServerStreamWriter<ServerFrame> responseStream,
        ServerCallContext context)
    {
        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            return;
        }

        Console.WriteLine($"[smoke] register: {requestStream.Current.Register?.WorkerId}");

        var assign = new AssignJob
        {
            JobId = "job1",
            EncoderPath = string.Empty, // worker falls back to its --ffmpeg
            Arguments = "-i /server/in.mkv -f hls /server/out/out.m3u8",
            Type = JobType.Hls,
            OutputDir = "/server/out"
        };
        assign.OutputGlobs.Add("*.ts");
        assign.OutputGlobs.Add("*.m3u8");
        await responseStream.WriteAsync(new ServerFrame { Assign = assign }, context.CancellationToken).ConfigureAwait(false);

        try
        {
            while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            {
                var frame = requestStream.Current;
                switch (frame.MsgCase)
                {
                    case WorkerFrame.MsgOneofCase.Accepted:
                        Console.WriteLine($"[smoke] accepted={frame.Accepted.Accepted} reason={frame.Accepted.Reason}");
                        break;
                    case WorkerFrame.MsgOneofCase.Segment:
                        WriteSegment(frame.Segment);
                        break;
                    case WorkerFrame.MsgOneofCase.Exited:
                        Console.WriteLine($"[smoke] job exited code={frame.Exited.ExitCode}");
                        _state.Completed.TrySetResult(true);
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[smoke] stream error: {ex.Message}");
        }

        _state.Completed.TrySetResult(true);
    }

    private void WriteSegment(SegmentData seg)
    {
        var final = Path.Combine(_state.CollectDir, seg.RelPath);
        if (!_open.TryGetValue(seg.RelPath, out var fs))
        {
            fs = new FileStream(final, FileMode.Create, FileAccess.Write, FileShare.None);
            _open[seg.RelPath] = fs;
        }

        if (!seg.Chunk.IsEmpty)
        {
            var bytes = seg.Chunk.ToByteArray();
            fs.Write(bytes, 0, bytes.Length);
        }

        if (seg.Eof)
        {
            fs.Dispose();
            _open.Remove(seg.RelPath);
            Console.WriteLine($"[smoke] received complete file: {seg.RelPath}");
        }
    }
}
