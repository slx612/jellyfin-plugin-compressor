using System;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.PreTranscode.Jobs;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class JobProgressTests
{
    [Fact]
    public void PhaseTransitions_DoNotCarryPercentage_AndPersistElapsedTime()
    {
        var start = new DateTime(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc);
        var job = new TranscodeJob();
        job.StartPhase("Comprimiendo", true, start);
        job.ReportProgress(74);
        job.ReportProgress(12); // delayed sample must not make a phase go backwards
        Assert.Equal(74, job.Phase!.Progress);
        job.ReportProgress(double.NaN);
        Assert.Equal(74, job.Phase.Progress);
        job.ReportProgress(120);
        Assert.Equal(100, job.Phase.Progress);
        job.StartPhase("Reinsertando Dolby Vision 8.1", false, start.AddMinutes(5));
        Assert.Null(job.Phase!.Progress);
        job.ReportProgress(83); // a tool without a duration must remain indeterminate
        Assert.Null(job.Phase.Progress);
        Assert.Equal(start.AddMinutes(5), job.Phase.StartedUtc);
        Assert.Single(job.CompletedPhases);
        Assert.Equal(TimeSpan.FromMinutes(5), job.CompletedPhases[0].FinishedUtc - job.CompletedPhases[0].StartedUtc);
        job.FinishPhase(start.AddMinutes(7));
        Assert.Null(job.Phase);
        var loaded = JsonSerializer.Deserialize<TranscodeJob>(JsonSerializer.Serialize(job))!;
        Assert.Equal(2, loaded.CompletedPhases.Length);
        Assert.Equal(100, loaded.CompletedPhases[0].Progress);
        Assert.Null(loaded.CompletedPhases[1].Progress);
    }

    [Fact]
    public void RestartAndRequeue_ClearStalePhase_WithoutDiscardingVerifiedOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "compressor-phase-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new Mock<IApplicationPaths>(); paths.SetupGet(p => p.DataPath).Returns(root);
            using (var queue = new JobQueue(paths.Object, NullLogger<JobQueue>.Instance))
            {
                var job = new TranscodeJob { SourcePath = "/movies/a.mkv", VerifiedOutputPath = "/tmp/verified.mkv" };
                queue.Enqueue(job); queue.ClaimNextPending();
                job.StartPhase("Comprimiendo", true); job.ReportProgress(95); queue.Update(job);
            }
            using var reopened = new JobQueue(paths.Object, NullLogger<JobQueue>.Instance);
            var recovered = Assert.Single(reopened.GetJobs());
            Assert.Equal(JobStatus.Pending, recovered.Status);
            Assert.Null(recovered.Phase);
            Assert.Empty(recovered.CompletedPhases);
            Assert.Equal("/tmp/verified.mkv", recovered.VerifiedOutputPath);
            reopened.ClaimNextPending();
            recovered.StartPhase("Verificando", true); recovered.ReportProgress(50);
            recovered.Status = JobStatus.Failed; reopened.Update(recovered);
            Assert.True(reopened.Requeue(recovered.Id));
            Assert.Null(recovered.Phase);
            Assert.Empty(recovered.CompletedPhases);
        }
        finally { Directory.Delete(root, true); }
    }
}
