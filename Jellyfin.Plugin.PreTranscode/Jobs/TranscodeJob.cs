using System;

namespace Jellyfin.Plugin.PreTranscode.Jobs;

/// <summary>
/// A unit of pre-transcoding work. Persisted to disk so the queue survives server restarts.
/// </summary>
public class TranscodeJob
{
    public bool Automatic { get; set; }
    public DateTime? NotBeforeUtc { get; set; }
    public Safety.CompressionSnapshot? Snapshot { get; set; }
    public string VerifiedOutputPath { get; set; } = string.Empty;
    public Safety.ContentIdentity? VerifiedOutputIdentity { get; set; }
    /// <summary>
    /// Gets or sets the unique job id.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Gets or sets the Jellyfin item id this job relates to (for reference), if known.
    /// </summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a human-readable name for display in the queue UI.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source file path.
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the id of the encoding profile to use (empty = global default).
    /// </summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current status.
    /// </summary>
    public JobStatus Status { get; set; } = JobStatus.Pending;

    /// <summary>
    /// Gets or sets the progress percentage (0-100).
    /// </summary>
    public double Progress { get; set; }

    // Immutable snapshots keep the dashboard/queue serializer from observing half a phase change.
    public JobPhase? Phase { get; set; }
    public JobPhase[] CompletedPhases { get; set; } = Array.Empty<JobPhase>();
    public int OutputWidth { get; set; }
    public int OutputHeight { get; set; }
    public string VerifiedFormat { get; set; } = string.Empty;

    internal void StartPhase(string name, bool measured = false, DateTime? utc = null)
    {
        var now = utc ?? DateTime.UtcNow;
        FinishPhase(now);
        StatusDetail = name;
        Progress = 0;
        Phase = new(name, now, null, measured ? 0d : null);
    }

    internal void ReportProgress(double value)
    {
        var phase = Phase;
        if (phase?.Progress is not { } previous || !double.IsFinite(value)) return;
        var progress = Math.Max(previous, Math.Clamp(value, 0, 100));
        Phase = phase with { Progress = progress };
        Progress = progress; // legacy clients; the dashboard uses Phase.Progress exclusively
    }

    internal void FinishPhase(DateTime? utc = null)
    {
        if (Phase is not { } phase) return;
        var now = utc ?? DateTime.UtcNow;
        var phases = new JobPhase[CompletedPhases.Length + 1];
        CompletedPhases.CopyTo(phases, 0);
        phases[^1] = phase with { FinishedUtc = now < phase.StartedUtc ? phase.StartedUtc : now };
        CompletedPhases = phases;
        Phase = null;
    }

    internal void ResetPhases()
    {
        Phase = null;
        CompletedPhases = Array.Empty<JobPhase>();
    }

    /// <summary>
    /// Gets or sets a short human-readable status detail (e.g. "probing", "verifying").
    /// </summary>
    public string StatusDetail { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the error message when <see cref="Status"/> is <see cref="JobStatus.Failed"/>.
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an excerpt of the ffmpeg log captured on failure.
    /// </summary>
    public string LogExcerpt { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the final output path when completed.
    /// </summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the size in bytes of the file at <see cref="OutputPath"/> when the job finished.
    /// Used to tell that path still holding <em>this</em> job's output apart from it holding some other
    /// file that has since been put there. <c>0</c> on records written by builds before it existed.
    /// </summary>
    public long OutputSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the number of processing attempts.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Gets or sets when the job was created (UTC).
    /// </summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// Gets or sets when processing last started (UTC).
    /// </summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>
    /// Gets or sets when the job finished (UTC).
    /// </summary>
    public DateTime? FinishedUtc { get; set; }
}

/// <summary>A measured percentage belongs to this phase only; null means no reliable denominator.</summary>
public sealed record JobPhase(string Name, DateTime StartedUtc, DateTime? FinishedUtc, double? Progress);
