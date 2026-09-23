using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Encoding;
using Jellyfin.Plugin.PreTranscode.Jobs;
using Jellyfin.Plugin.PreTranscode.Media;
using Jellyfin.Plugin.PreTranscode.Rules;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode.Library;

/// <summary>
/// Shared logic that evaluates a Jellyfin library item against the active rules for its library and,
/// if it matches (and is not already compliant), enqueues a pre-transcode job. Used by the library
/// scan hook, the scheduled sweep and the item-added monitor.
/// </summary>
public sealed class ItemEvaluator
{
    // After this many failed attempts for the same source+profile, stop auto-retrying it on every
    // sweep; the admin can still Requeue it manually from the queue page.
    private const int MaxAutoFailedAttempts = 3;

    private readonly IJobQueue _queue;
    private readonly IMediaProber _prober;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ItemEvaluator> _logger;

    private readonly CompressionCoordinator _coordinator;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemEvaluator"/> class.
    /// </summary>
    /// <param name="queue">The job queue.</param>
    /// <param name="prober">The media prober.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public ItemEvaluator(IJobQueue queue, IMediaProber prober, ILibraryManager libraryManager, ILogger<ItemEvaluator> logger, CompressionCoordinator coordinator)
    {
        _coordinator = coordinator;
        _queue = queue;
        _prober = prober;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    // Only one sweep runs at a time: the scheduled task, the post-scan hook and the dashboard's
    // "Scan library now" button can all land at once, and each sweep ffprobes every item.
    internal async Task<int> SweepAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.AutomaticCompressionEnabled != true) return 0;
        var results = await _coordinator.ScanAsync(true, true, progress, cancellationToken).ConfigureAwait(false);
        return results.Count(r => r.Reason == "En cola");
    }

    internal async Task<bool> EvaluateAndEnqueueAsync(BaseItem item, CancellationToken cancellationToken, IReadOnlyList<TranscodeJob>? knownJobs = null)
    {
        if (Plugin.Instance?.Configuration.AutomaticCompressionEnabled != true) return false;
        var result = await _coordinator.EnqueueAsync(item, true, cancellationToken).ConfigureAwait(false);
        return result.Reason == "En cola";
    }

    private void Explain(bool verbose, string? path, string reason)
    {
        if (verbose)
        {
            _logger.LogInformation("Jellyfin Compressor skipped {Path}: {Reason}", string.IsNullOrEmpty(path) ? "(item with no path)" : path, reason);
        }
    }

    // A file is "stable" once its last-write time is at least stabilitySeconds in the past — a guard
    // against grabbing an in-progress download/copy. Shared with LibraryMonitor so a newly-added item
    // that is not stable yet can be deferred and re-checked rather than skipped outright.
    internal static bool IsStable(string path, int stabilitySeconds)
    {
        if (stabilitySeconds <= 0)
        {
            return true;
        }

        try
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            return age.TotalSeconds >= stabilitySeconds;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Treat an unreadable/odd path as "not settled yet" and skip it, rather than letting the
            // exception escape and abort the whole sweep loop. (IOException alone missed these siblings.)
            return false;
        }
    }

    // Queue-independent: true when the profile's expected output for this source already exists on disk
    // (a distinct file, not the source itself). Survives clearing the job queue.
    private static bool OutputAlreadyExists(EncodingProfile profile, string sourcePath)
    {
        var expected = OutputApplier.ExpectedOutputPath(profile, sourcePath);
        return expected is not null
            && !OutputApplier.IsSameFile(expected, sourcePath)
            && File.Exists(expected);
    }

    // Delegates to the single definition shared with the executor, which a manually-queued job reaches
    // without ever passing through this evaluator.
    internal static bool WritesOverItsOwnSource(EncodingProfile profile, string sourcePath)
    {
        return OutputApplier.WritesOverItsOwnSource(profile, sourcePath);
    }

    // True when this source should not be auto-queued again for this profile: it either already has a
    // resolved transcode whose recorded output still exists, or it has failed at least maxFailedAttempts
    // times. A Completed job records its produced output; a Skipped job records an existing file it stood
    // down for — the pre-existing output it found, or (with DiscardOutputIfLarger) the original it kept
    // because the transcode was not smaller. Either way the source is handled and must not be re-queued
    // and re-transcoded on every sweep. Pure over the job list (outputExists is injected) so it is unit-testable.
    internal static bool AlreadyHandled(
        IEnumerable<TranscodeJob> jobs,
        string sourcePath,
        string profileId,
        Func<string, long> outputSize,
        int maxFailedAttempts)
    {
        var failed = 0;
        foreach (var job in jobs)
        {
            if (!string.Equals(job.ProfileId, profileId, StringComparison.Ordinal))
            {
                continue;
            }

            var done = job.Status == JobStatus.Completed || job.Status == JobStatus.Skipped;

            // This path IS the output a previous run produced. Replace-in-place with a profile container
            // that differs from the source's extension renames the file, so the finished job records the
            // old name (Movie.mp4) and the file that now exists (Movie.mkv) has no record of its own.
            // Matching only on the source path, the next sweep saw an unfamiliar file, matched the same
            // size/codec rule that started all this, and re-encoded the previous encode — losing a
            // generation every sweep, for as long as the file stayed over the threshold. Recognising the
            // output by name closes that. Scoped to the same profile, like the check below: a different
            // profile transcoding this file is a legitimate chain, not a loop.
            if (done && string.Equals(job.OutputPath, sourcePath, StringComparison.OrdinalIgnoreCase)
                && IsStillThatOutput(job, outputSize))
            {
                return true;
            }

            if (!string.Equals(job.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (done && !string.IsNullOrEmpty(job.OutputPath) && outputSize(job.OutputPath) > 0)
            {
                return true;
            }

            if (job.Status == JobStatus.Failed)
            {
                failed++;
            }
        }

        return maxFailedAttempts > 0 && failed >= maxFailedAttempts;
    }

    // Path identity is not file identity. Recognising a candidate purely by the name a finished job
    // recorded as its output meant that deleting that transcode and dropping a different file in under
    // the same name blacklisted the path for this profile for ever — and the explanation in the log named
    // a source that is not this file at all. A record from a build that did not store the size is trusted
    // as before, so upgrading does not re-transcode a library.
    private static bool IsStillThatOutput(TranscodeJob job, Func<string, long> outputSize)
    {
        return job.OutputSizeBytes <= 0 || outputSize(job.OutputPath) == job.OutputSizeBytes;
    }

    private (EncodingProfile? Profile, IReadOnlyList<TriggerRule> Rules, bool Enabled) ResolveForLibrary(PluginConfiguration config, BaseItem item)
    {
        // No overrides configured: skip the per-item collection-folder lookup entirely and go straight to
        // the default profile + global rules. This runs for every item in a sweep, so avoiding the
        // library query when it can never match a thing matters on large libraries.
        if (config.LibraryOverrides.Count == 0)
        {
            return (DefaultProfile(config), config.GlobalRules, true);
        }

        LibraryOverride? found = null;
        try
        {
            foreach (var folder in _libraryManager.GetCollectionFolders(item))
            {
                var folderId = folder.Id.ToString("N");
                found = config.LibraryOverrides.FirstOrDefault(o => string.Equals(NormalizeGuid(o.LibraryId), folderId, StringComparison.Ordinal));
                if (found is not null)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve collection folders for {Path}", item.Path);
        }

        if (found is not null)
        {
            if (!found.Enabled)
            {
                return (null, Array.Empty<TriggerRule>(), false);
            }

            var overrideProfile = config.Profiles.FirstOrDefault(p => string.Equals(p.Id, found.ProfileId, StringComparison.Ordinal)) ?? DefaultProfile(config);
            var overrideRules = found.UseGlobalRules ? config.GlobalRules : found.Rules;
            return (overrideProfile, overrideRules, true);
        }

        return (DefaultProfile(config), config.GlobalRules, true);
    }

    private static EncodingProfile? DefaultProfile(PluginConfiguration config)
    {
        return config.Profiles.FirstOrDefault(p => string.Equals(p.Id, config.DefaultProfileId, StringComparison.Ordinal))
            ?? config.Profiles.FirstOrDefault();
    }

    internal static string NormalizeGuid(string value)
    {
        return Guid.TryParse(value, out var guid)
            ? guid.ToString("N")
            : (value ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }
}
