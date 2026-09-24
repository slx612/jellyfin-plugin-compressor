using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Jobs;
using Jellyfin.Plugin.PreTranscode.Library;
using Jellyfin.Plugin.PreTranscode.Media;
using Jellyfin.Plugin.PreTranscode.Safety;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.PreTranscode.Tests;

[CollectionDefinition("Compressor plugin instance", DisableParallelization = true)]
public class CompressorPluginCollection { }

[Collection("Compressor plugin instance")]
public sealed class CompressorWiringTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("compressor-wiring-").FullName;
    private readonly Plugin? previous = Plugin.Instance;
    private readonly Plugin plugin;
    private readonly Mock<IApplicationPaths> paths = new();
    public CompressorWiringTests()
    {
        paths.SetupGet(p => p.DataPath).Returns(root);
        paths.SetupGet(p => p.PluginsPath).Returns(root);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(root);
        var serializer = new Mock<IXmlSerializer>();
        serializer.Setup(s => s.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(new PluginConfiguration());
        plugin = new Plugin(paths.Object, serializer.Object);
    }

    [Fact]
    public async Task ScheduledAndLibraryEntriesDoNothingByDefault()
    {
        var queue = new Mock<IJobQueue>(MockBehavior.Strict);
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        var probe = new Mock<IMediaProber>(MockBehavior.Strict);
        var coordinator = new CompressionCoordinator(queue.Object, probe.Object, library.Object, Mock.Of<ISessionManager>(), new ContentRegistry(Path.Combine(root, "identities")));
        var evaluator = new ItemEvaluator(queue.Object, probe.Object, library.Object, NullLogger<ItemEvaluator>.Instance, coordinator);
        await new PreTranscodeSweepTask(evaluator).ExecuteAsync(new Progress<double>(), default);
        await new PreTranscodeScanTask(evaluator, NullLogger<PreTranscodeScanTask>.Instance).Run(new Progress<double>(), default);
        Assert.False(await evaluator.EvaluateAndEnqueueAsync(new Folder(), default));
        Assert.Empty(await coordinator.ScanAsync(true, true, null, default));
        queue.VerifyNoOtherCalls(); library.VerifyNoOtherCalls(); probe.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AutomaticInspectionSkipsSmallMovieBeforeHashingOrProbing()
    {
        var movieRoot = Directory.CreateDirectory(Path.Combine(root, "movies")).FullName;
        var source = Path.Combine(movieRoot, "small.mkv");
        File.WriteAllBytes(source, new byte[1024]);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-2));
        plugin.Configuration.AutomaticCompressionEnabled = true;
        plugin.Configuration.IncludedFolders.Add(movieRoot);
        plugin.Configuration.QuarantineDirectory = Path.Combine(root, "originals");
        plugin.Configuration.RetentionDays = 7;

        var queue = new Mock<IJobQueue>(MockBehavior.Strict);
        queue.Setup(q => q.GetJobs()).Returns(Array.Empty<TranscodeJob>());
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetVirtualFolders()).Returns([new VirtualFolderInfo { Locations = [movieRoot] }]);
        var movie = new Movie { Name = "Small", Path = source };
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([movie]);
        var probe = new Mock<IMediaProber>(MockBehavior.Strict);
        var coordinator = new CompressionCoordinator(queue.Object, probe.Object, library.Object, Mock.Of<ISessionManager>(), new ContentRegistry(Path.Combine(root, "identities")));

        var (candidate, snapshot) = await coordinator.InspectAsync(movie, true, default);

        Assert.False(candidate.Eligible);
        Assert.Null(snapshot);
        Assert.Contains("10 GB", candidate.Reason);
        var batch = await coordinator.ScanAsync(false, false, null, default);
        Assert.Contains("10 GB", Assert.Single(batch).Reason);
        probe.VerifyNoOtherCalls();

        probe.Setup(p => p.ProbeAsync(source, It.IsAny<CancellationToken>())).ReturnsAsync(new MediaProbeInfo
        { VideoStreamCount = 1, Width = 1920, Height = 1080, DurationSeconds = 60, PixelFormat = "yuv420p" });
        var (manual, manualSnapshot) = await coordinator.InspectAsync(movie, false, default);
        Assert.True(manual.Eligible);
        Assert.NotNull(manualSnapshot);
    }

    [Fact]
    public async Task RestartedAutomaticJobRecognizesAlreadyPublishedMovieBelowCurrentThreshold()
    {
        var movieRoot = Directory.CreateDirectory(Path.Combine(root, "movies")).FullName;
        var originals = Path.Combine(root, "originals");
        var source = Path.Combine(movieRoot, "movie.mkv");
        var output = Path.Combine(root, "encoded.mkv");
        await File.WriteAllBytesAsync(source, new byte[4096]);
        await File.WriteAllBytesAsync(output, new byte[1024]);
        var sourceIdentity = await ContentRegistry.IdentifyAsync(source, default);
        var outputIdentity = await ContentRegistry.IdentifyAsync(output, default);
        var registry = new ContentRegistry(Path.Combine(root, "identities"));
        var replacement = new ReplacementService(Path.Combine(root, "transactions"), registry);
        await replacement.PublishAsync(new ReplacementRequest("published", source, output, movieRoot, originals,
            7, "profile", sourceIdentity, outputIdentity), default);
        plugin.Configuration.AutomaticCompressionEnabled = true;
        plugin.Configuration.IncludedFolders.Add(movieRoot);
        plugin.Configuration.QuarantineDirectory = originals;
        plugin.Configuration.RetentionDays = 7;

        var queue = new Mock<IJobQueue>(MockBehavior.Strict);
        queue.Setup(q => q.Update(It.IsAny<TranscodeJob>()));
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetVirtualFolders()).Returns([new VirtualFolderInfo { Locations = [movieRoot] }]);
        var probe = new Mock<IMediaProber>(MockBehavior.Strict);
        var coordinator = new CompressionCoordinator(queue.Object, probe.Object, library.Object, Mock.Of<ISessionManager>(), registry);
        var executor = new TranscodeExecutor(queue.Object, probe.Object, Mock.Of<IMediaEncoder>(), coordinator,
            registry, replacement, null!, Mock.Of<ILibraryMonitor>(), paths.Object, NullLogger<TranscodeExecutor>.Instance);
        var job = new TranscodeJob { Automatic = true, SourcePath = source, Status = JobStatus.Pending,
            Snapshot = new CompressionSnapshot(new EncodingProfile { Id = "profile" }, sourceIdentity, movieRoot,
                originals, 7, 15, "profile", DateTime.UtcNow) };

        await executor.ExecuteAsync(job, default);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(source, job.OutputPath);
        Assert.Equal(outputIdentity.Length, job.OutputSizeBytes);
        probe.VerifyNoOtherCalls();
    }

    [Fact]
    public void DisabledAutomaticJobsCannotStarveManualJobsAndSnapshotsSurviveReload()
    {
        var snapshot = new CompressionSnapshot(new EncodingProfile { Crf = 25 }, new(new string('a', 64), 1000), root, root + "-originals", 7, 15, "p", DateTime.UtcNow);
        string manualId;
        using (var queue = new JobQueue(paths.Object, NullLogger<JobQueue>.Instance))
        {
            queue.Enqueue(new TranscodeJob { Automatic = true, SourcePath = "auto.mkv", CreatedUtc = DateTime.UtcNow.AddMinutes(-2), Snapshot = snapshot });
            var manual = new TranscodeJob { SourcePath = "manual.mkv", CreatedUtc = DateTime.UtcNow, Snapshot = snapshot };
            manualId = manual.Id;
            queue.Enqueue(manual);
            Assert.Equal(manual.Id, queue.ClaimNextPending()!.Id);
            Assert.Null(queue.ClaimNextPending());
        }
        using (var reloaded = new JobQueue(paths.Object, NullLogger<JobQueue>.Instance))
        {
            var job = reloaded.ClaimNextPending()!;
            Assert.Equal(manualId, job.Id);
            Assert.Equal(25, job.Snapshot!.Profile.Crf);
            Assert.Equal(snapshot.Source, job.Snapshot.Source);
            plugin.Configuration.AutomaticCompressionEnabled = true;
            Assert.True(reloaded.ClaimNextPending()!.Automatic);
        }
    }

    public void Dispose()
    {
        typeof(Plugin).GetProperty(nameof(Plugin.Instance))!.SetValue(null, previous);
        Directory.Delete(root, true);
    }
}
