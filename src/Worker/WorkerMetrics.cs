using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Jellyfin.Plugin.DistributedTranscoding.Contracts;

namespace Jellyfin.Plugin.DistributedTranscoding.Worker;

/// <summary>
/// Owns a <see cref="Meter"/> named "Jellycode.Worker" and exposes Prometheus-scrapable instruments.
/// Per-job gauges use an internal dictionary keyed by job ID so their labels vanish when the job ends.
/// </summary>
public sealed class WorkerMetrics : IDisposable
{
    public const string MeterName = "Jellycode.Worker";

    private readonly Meter _meter;
    private readonly ConcurrentDictionary<string, JobSample> _liveJobs = new(StringComparer.Ordinal);

    private readonly Counter<long> _framesProcessed;
    private readonly Counter<long> _bytesTranscoded;
    private readonly Counter<long> _jobsStarted;
    private readonly Counter<long> _jobsCompleted;
    private readonly Counter<long> _jobsFailed;

    private Func<int>? _activeCount;
    private Func<int>? _freeSlots;
    private Func<int>? _maxConcurrent;

    private string _workerId = string.Empty;
    private string _ffmpegVersion = string.Empty;
    private string _hwAccels = string.Empty;

    public WorkerMetrics()
    {
        _meter = new Meter(MeterName);

        _meter.CreateObservableGauge("jellycode_worker_active_streams", () =>
            _activeCount is not null ? _activeCount() : 0);

        _meter.CreateObservableGauge("jellycode_worker_free_slots", () =>
            _freeSlots is not null ? _freeSlots() : 0);

        _meter.CreateObservableGauge("jellycode_worker_max_concurrent", () =>
            _maxConcurrent is not null ? _maxConcurrent() : 0);

        _meter.CreateObservableGauge("jellycode_worker_encode_fps", ObserveFps);
        _meter.CreateObservableGauge("jellycode_worker_encode_speed_ratio", ObserveSpeed);
        _meter.CreateObservableGauge("jellycode_worker_bitrate_kbps", ObserveBitrate);

        _framesProcessed = _meter.CreateCounter<long>("jellycode_worker_frames_processed_total");
        _bytesTranscoded = _meter.CreateCounter<long>("jellycode_worker_bytes_transcoded_total");
        _jobsStarted = _meter.CreateCounter<long>("jellycode_worker_jobs_started_total");
        _jobsCompleted = _meter.CreateCounter<long>("jellycode_worker_jobs_completed_total");
        _jobsFailed = _meter.CreateCounter<long>("jellycode_worker_jobs_failed_total");

        _meter.CreateObservableGauge("jellycode_worker_info", () =>
            new Measurement<int>(1, new KeyValuePair<string, object?>("worker_id", _workerId),
                                    new KeyValuePair<string, object?>("ffmpeg_version", _ffmpegVersion),
                                    new KeyValuePair<string, object?>("hwaccels", _hwAccels)));
    }

    public void SetIdentity(string workerId, string ffmpegVersion, string hwAccels)
    {
        _workerId = workerId;
        _ffmpegVersion = ffmpegVersion;
        _hwAccels = hwAccels;
    }

    public void SetCapacitySource(Func<int> activeCount, Func<int> freeSlots, Func<int> maxConcurrent)
    {
        _activeCount = activeCount;
        _freeSlots = freeSlots;
        _maxConcurrent = maxConcurrent;
    }

    public void JobStarted(string jobType) => _jobsStarted.Add(1,
        new KeyValuePair<string, object?>("job_type", jobType));

    public void JobCompleted(string jobType) => _jobsCompleted.Add(1,
        new KeyValuePair<string, object?>("job_type", jobType));

    public void JobFailed(string jobType) => _jobsFailed.Add(1,
        new KeyValuePair<string, object?>("job_type", jobType));

    public void AddFrames(long delta)
    {
        if (delta > 0)
        {
            _framesProcessed.Add(delta);
        }
    }

    public void AddBytes(long delta)
    {
        if (delta > 0)
        {
            _bytesTranscoded.Add(delta);
        }
    }

    public void UpdateJob(string jobId, float fps, float speedRatio, float bitrateKbps, string jobType = "")
    {
        _liveJobs.AddOrUpdate(jobId,
            _ => new JobSample { Fps = fps, SpeedRatio = speedRatio, BitrateKbps = bitrateKbps, Type = jobType },
            (_, existing) =>
            {
                existing.Fps = fps;
                existing.SpeedRatio = speedRatio;
                existing.BitrateKbps = bitrateKbps;
                return existing;
            });
    }

    public void RemoveJob(string jobId)
    {
        _liveJobs.TryRemove(jobId, out _);
    }

    public void Dispose()
    {
        _meter.Dispose();
    }

    private IEnumerable<Measurement<double>> ObserveFps()
    {
        foreach (var kvp in _liveJobs)
        {
            yield return new Measurement<double>(kvp.Value.Fps,
                new KeyValuePair<string, object?>("job_id", kvp.Key),
                new KeyValuePair<string, object?>("job_type", kvp.Value.Type));
        }
    }

    private IEnumerable<Measurement<double>> ObserveSpeed()
    {
        foreach (var kvp in _liveJobs)
        {
            yield return new Measurement<double>(kvp.Value.SpeedRatio,
                new KeyValuePair<string, object?>("job_id", kvp.Key),
                new KeyValuePair<string, object?>("job_type", kvp.Value.Type));
        }
    }

    private IEnumerable<Measurement<double>> ObserveBitrate()
    {
        foreach (var kvp in _liveJobs)
        {
            yield return new Measurement<double>(kvp.Value.BitrateKbps,
                new KeyValuePair<string, object?>("job_id", kvp.Key),
                new KeyValuePair<string, object?>("job_type", kvp.Value.Type));
        }
    }

    public static string JobTypeTag(JobType type) => type switch
    {
        JobType.Progressive => "progressive",
        JobType.Hls => "hls",
        JobType.Dash => "dash",
        JobType.Trickplay => "trickplay",
        _ => "unknown",
    };

    private sealed class JobSample
    {
        public float Fps;
        public float SpeedRatio;
        public float BitrateKbps;
        public string Type = string.Empty;
    }
}
