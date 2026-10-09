using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Api;
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
using Microsoft.AspNetCore.Mvc;
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
        var coordinator = new CompressionCoordinator(queue.Object, probe.Object, library.Object, Mock.Of<ISessionManager>(), new ContentRegistry(Path.Combine(root, "identities")), Mock.Of<IMediaSourceManager>());
        var evaluator = new ItemEvaluator(queue.Object, probe.Object, library.Object, NullLogger<ItemEvaluator>.Instance, coordinator);
        await new PreTranscodeSweepTask(evaluator).ExecuteAsync(new Progress<double>(), default);
        await new PreTranscodeScanTask(evaluator, NullLogger<PreTranscodeScanTask>.Instance).Run(new Progress<double>(), default);
        Assert.False(await evaluator.EvaluateAndEnqueueAsync(new Folder(), default));
        Assert.Empty(await coordinator.ScanAsync(true, true, null, default));
        queue.VerifyNoOtherCalls(); library.VerifyNoOtherCalls(); probe.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task SelectionReviewKeepsTheSingleMovieExceptionAndTheBatchMinimum(int count, bool eligible)
    {
        var movieRoot = Directory.CreateDirectory(Path.Combine(root, "movies")).FullName;
        plugin.Configuration.IncludedFolders.Add(movieRoot);
        plugin.Configuration.QuarantineDirectory = Path.Combine(root, "originals");
        plugin.Configuration.RetentionDays = 7;
        var movies = Enumerable.Range(0, count).Select(i => new Movie { Id = Guid.NewGuid(), Name = $"Movie {i}", Path = Path.Combine(movieRoot, $"{i}.mkv") }).ToArray();
        foreach (var movie in movies) { File.WriteAllBytes(movie.Path, new byte[1024]); File.SetLastWriteTimeUtc(movie.Path, DateTime.UtcNow.AddMinutes(-2)); }
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetVirtualFolders()).Returns([new VirtualFolderInfo { Locations = [movieRoot] }]);
        library.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => movies.Single(m => m.Id == id));
        var queue = new Mock<IJobQueue>(); queue.Setup(q => q.GetJobs()).Returns(Array.Empty<TranscodeJob>());
        var probe = new Mock<IMediaProber>(); probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new MediaProbeInfo
        { VideoStreamCount = 1, Width = 1920, Height = 1080, DurationSeconds = 60, PixelFormat = "yuv420p" });
        var coordinator = new CompressionCoordinator(queue.Object, probe.Object, library.Object, Mock.Of<ISessionManager>(), new ContentRegistry(Path.Combine(root, "identities")), Mock.Of<IMediaSourceManager>());
        var runner = new ManualOperationRunner(default, NullLogger<ManualOperationRunner>.Instance);
        var controller = new PreTranscodeController(coordinator, queue.Object, null!, null!, library.Object, null!, null!, null!, runner);
        var ids = movies.Select(m => m.Id).ToArray();
        Assert.IsType<BadRequestObjectResult>(controller.ReviewSelection([]));
        Assert.IsType<BadRequestObjectResult>(controller.QueueSelection([ids[0], ids[0]]));
        var operation = Assert.IsType<ManualOperationInfo>(Assert.IsType<AcceptedResult>(controller.ReviewSelection(ids)).Value);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (runner.Get(operation.Id)!.State == "Running") await Task.Delay(10, timeout.Token);
        var finished = runner.Get(operation.Id)!;
        Assert.Equal("Completed", finished.State);
        var results = Assert.IsAssignableFrom<IEnumerable<Candidate>>(finished.Result).ToArray();
        Assert.Equal(count, results.Length);
        Assert.All(results, result => Assert.Equal(eligible, result.Eligible));
        var batch = Assert.IsType<ManualOperationInfo>(Assert.IsType<AcceptedResult>(controller.QueueSelection([ids[0]], batch: true)).Value);
        while (runner.Get(batch.Id)!.State == "Running") await Task.Delay(10, timeout.Token);
        var refused = Assert.Single(Assert.IsAssignableFrom<IEnumerable<Candidate>>(runner.Get(batch.Id)!.Result));
        Assert.False(refused.Eligible);
        Assert.Contains("10 GB", refused.Reason);
        queue.Verify(q => q.Enqueue(It.IsAny<TranscodeJob>(), It.IsAny<Func<IReadOnlyList<TranscodeJob>, bool>>()), Times.Never);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ManualHdrBatchIsReviewedAndQueuedWithoutLosingExecutorEligibility(bool dolby, bool hdr10Plus)
    {
        var movieRoot = Directory.CreateDirectory(Path.Combine(root, "movies")).FullName;
        plugin.Configuration.IncludedFolders.Add(movieRoot);
        plugin.Configuration.QuarantineDirectory = Path.Combine(root, "originals");
        plugin.Configuration.MinMovieSizeGb = 0;
        plugin.Configuration.RetentionDays = 7;
        plugin.Configuration.EnableExperimentalHdr = true;
        plugin.Configuration.EnableExperimentalDolbyVision = true;
        plugin.Configuration.EnableExperimentalHdr10Plus = true;
        plugin.Configuration.Profiles[0].VideoEncoder = "hevc_nvenc";
        var movies = Enumerable.Range(0, 2).Select(i => new Movie
        { Id = Guid.NewGuid(), Name = $"Movie {i}", Path = Path.Combine(movieRoot, $"{i}.mkv") }).ToArray();
        foreach (var movie in movies)
        {
            File.WriteAllText(movie.Path, movie.Name);
            File.SetLastWriteTimeUtc(movie.Path, DateTime.UtcNow.AddMinutes(-2));
        }
        MediaProbeInfo Probe(string path) => new()
        {
            Path = path, VideoCodec = "hevc", VideoStreamCount = 1, Width = 1920, Height = 1080,
            DurationSeconds = 60, PixelFormat = "yuv420p10le", BitDepth = 10, IsHdr = true,
            ColorPrimaries = "bt2020", ColorTransfer = "smpte2084", ColorSpace = "bt2020nc",
            IsDolbyVision = dolby, DolbyVisionProfile = dolby ? 8 : 0, DolbyVisionCompatibilityId = dolby ? 1 : 0,
            DolbyVisionHasRpu = dolby, DolbyVisionHasBaseLayer = dolby, HasHdr10Plus = hdr10Plus
        };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetVirtualFolders()).Returns([new VirtualFolderInfo { Locations = [movieRoot] }]);
        library.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => movies.Single(m => m.Id == id));
        var prober = new Mock<IMediaProber>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(movies);
        prober.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) => Probe(path));
        var mediaSources = new Mock<IMediaSourceManager>();
        mediaSources.Setup(m => m.GetMediaStreams(It.IsAny<Guid>())).Returns(Array.Empty<MediaStream>());
        using var queue = new JobQueue(paths.Object, NullLogger<JobQueue>.Instance);
        var coordinator = new CompressionCoordinator(queue, prober.Object, library.Object, Mock.Of<ISessionManager>(),
            new ContentRegistry(Path.Combine(root, "identities")), mediaSources.Object);
        var runner = new ManualOperationRunner(default, NullLogger<ManualOperationRunner>.Instance);
        var controller = new PreTranscodeController(coordinator, queue, null!, null!, library.Object, null!, null!, null!, runner);
        var ids = movies.Select(m => m.Id).ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var review = Assert.IsType<ManualOperationInfo>(Assert.IsType<AcceptedResult>(controller.ReviewSelection(ids)).Value);
        while (runner.Get(review.Id)!.State == "Running") await Task.Delay(10, timeout.Token);
        Assert.Equal("Completed", runner.Get(review.Id)!.State);
        var reviewed = Assert.IsAssignableFrom<IEnumerable<Candidate>>(runner.Get(review.Id)!.Result).ToArray();
        Assert.Equal(2, reviewed.Length);
        Assert.All(reviewed, c => Assert.True(c.Eligible, c.Reason));
        var folderReview = await coordinator.ScanAsync(false, false, null, timeout.Token);
        Assert.Equal(2, folderReview.Count);
        Assert.All(folderReview, c => Assert.True(c.Eligible, c.Reason));
        var enqueue = Assert.IsType<ManualOperationInfo>(Assert.IsType<AcceptedResult>(controller.QueueSelection(ids, batch: true)).Value);
        while (runner.Get(enqueue.Id)!.State == "Running") await Task.Delay(10, timeout.Token);
        Assert.Equal("Completed", runner.Get(enqueue.Id)!.State);
        Assert.All(Assert.IsAssignableFrom<IEnumerable<Candidate>>(runner.Get(enqueue.Id)!.Result), c => Assert.Equal("En cola", c.Reason));
        Assert.Equal(2, queue.GetJobs().Count);
        Assert.All(queue.GetJobs(), job =>
        {
            Assert.Equal(JobStatus.Pending, job.Status);
            Assert.False(job.Automatic);
            Assert.False(job.Snapshot!.SingleMovieSelection);
            Assert.Null(CompressionPolicy.EligibilityError(Probe(job.SourcePath), job.Snapshot.Profile,
                plugin.Configuration, job.Automatic, job.Snapshot.SingleMovieSelection));
            Assert.NotNull(CompressionPolicy.EligibilityError(Probe(job.SourcePath), job.Snapshot.Profile,
                plugin.Configuration, automatic: true));
        });
        Assert.False(plugin.Configuration.AutomaticCompressionEnabled);
        Assert.Equal(1, plugin.Configuration.MaxConcurrentJobs);
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
        var coordinator = new CompressionCoordinator(queue.Object, probe.Object, library.Object, Mock.Of<ISessionManager>(), new ContentRegistry(Path.Combine(root, "identities")), Mock.Of<IMediaSourceManager>());

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
    public void FolderMovieCountsWarnsAboutEmptySelectionsWithoutReadingMedia()
    {
        var movieRoot = Directory.CreateDirectory(Path.Combine(root, "movies")).FullName;
        var emptyRoot = Directory.CreateDirectory(Path.Combine(root, "empty")).FullName;
        var excluded = Directory.CreateDirectory(Path.Combine(movieRoot, "excluded")).FullName;
        plugin.Configuration.IncludedFolders.Add(movieRoot);
        plugin.Configuration.IncludedFolders.Add(emptyRoot);
        plugin.Configuration.ExcludedFolders.Add(excluded);
        plugin.Configuration.QuarantineDirectory = Path.Combine(root, "originals");
        plugin.Configuration.RetentionDays = 7;

        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetVirtualFolders()).Returns([new VirtualFolderInfo { Locations = [movieRoot, emptyRoot] }]);
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([
            new Movie { Name = "Selected", Path = Path.Combine(movieRoot, "selected.mkv") },
            new Movie { Name = "Excluded", Path = Path.Combine(excluded, "excluded.mkv") }]);
        var coordinator = new CompressionCoordinator(Mock.Of<IJobQueue>(MockBehavior.Strict), Mock.Of<IMediaProber>(MockBehavior.Strict),
            library.Object, Mock.Of<ISessionManager>(), new ContentRegistry(Path.Combine(root, "identities")), Mock.Of<IMediaSourceManager>());

        var counts = coordinator.FolderMovieCounts();

        Assert.Equal([(movieRoot, 1), (emptyRoot, 0)], counts.Select(c => (c.Path, c.Movies)).ToArray());
    }

    [Fact]
    public async Task PreviewOnlyInspectsSelectedMoviesWithoutReadingEntireFile()
    {
        var movieRoot = Directory.CreateDirectory(Path.Combine(root, "movies")).FullName;
        var otherRoot = Directory.CreateDirectory(Path.Combine(root, "other")).FullName;
        var source = Path.Combine(movieRoot, "large.mkv");
        var other = Path.Combine(otherRoot, "outside.mkv");
        File.WriteAllBytes(source, new byte[1024]);
        File.WriteAllBytes(other, new byte[1024]);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddMinutes(-2));
        plugin.Configuration.IncludedFolders.Add(movieRoot);
        plugin.Configuration.QuarantineDirectory = Path.Combine(root, "originals");
        plugin.Configuration.RetentionDays = 7;
        plugin.Configuration.MinMovieSizeGb = 0;

        var queue = new Mock<IJobQueue>();
        queue.Setup(q => q.GetJobs()).Returns(Array.Empty<TranscodeJob>());
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetVirtualFolders()).Returns([new VirtualFolderInfo { Locations = [movieRoot, otherRoot] }]);
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([
            new Movie { Name = "Selected", Path = source },
            new Movie { Name = "Outside", Path = other }]);
        var probe = new Mock<IMediaProber>();
        probe.Setup(p => p.ProbeAsync(source, It.IsAny<CancellationToken>())).ReturnsAsync(new MediaProbeInfo
        { VideoStreamCount = 1, Width = 1920, Height = 1080, DurationSeconds = 60, PixelFormat = "yuv420p" });
        var coordinator = new CompressionCoordinator(queue.Object, probe.Object, library.Object, Mock.Of<ISessionManager>(), new ContentRegistry(Path.Combine(root, "identities")), Mock.Of<IMediaSourceManager>());

        using var locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await coordinator.ScanAsync(false, false, null, default);

        var selected = Assert.Single(result);
        Assert.Equal("Selected", selected.Name);
        Assert.True(selected.Eligible);
        Assert.Equal(1024, selected.Size);
        Assert.Contains("preliminar", selected.Reason, StringComparison.OrdinalIgnoreCase);
        probe.Verify(p => p.ProbeAsync(source, It.IsAny<CancellationToken>()), Times.Once);
        probe.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CountsAndReviewContinueAfterAnUnrelatedLibraryIsRemoved()
    {
        var movies = Directory.CreateDirectory(Path.Combine(root, "movies")).FullName;
        var retired = Directory.CreateDirectory(Path.Combine(root, "retired")).FullName;
        var source = Path.Combine(movies, "selected.mkv");
        File.WriteAllBytes(source, new byte[1024]);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-2));
        plugin.Configuration.IncludedFolders.AddRange([movies, retired]);
        plugin.Configuration.ExcludedFolders.Add(Path.Combine(root, "retired-series"));
        plugin.Configuration.QuarantineDirectory = Path.Combine(root, "originals");
        plugin.Configuration.RetentionDays = 7;
        plugin.Configuration.MinMovieSizeGb = 0;
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetVirtualFolders()).Returns([new VirtualFolderInfo { Locations = [movies] }]);
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([
            new Movie { Name = "Current", Path = source },
            new Movie { Name = "Retired", Path = Path.Combine(retired, "outside.mkv") }]);
        var probe = new Mock<IMediaProber>();
        probe.Setup(p => p.ProbeAsync(source, It.IsAny<CancellationToken>())).ReturnsAsync(new MediaProbeInfo
        { VideoStreamCount = 1, Width = 1920, Height = 1080, DurationSeconds = 60, PixelFormat = "yuv420p" });
        var queue = new Mock<IJobQueue>();
        queue.Setup(q => q.GetJobs()).Returns(Array.Empty<TranscodeJob>());
        var coordinator = new CompressionCoordinator(queue.Object, probe.Object, library.Object, Mock.Of<ISessionManager>(),
            new ContentRegistry(Path.Combine(root, "identities")), Mock.Of<IMediaSourceManager>());

        var count = Assert.Single(coordinator.FolderMovieCounts());
        Assert.Equal(movies, count.Path);
        Assert.Equal(1, count.Movies);
        var reviewed = Assert.Single(await coordinator.ScanAsync(false, false, null, default));
        Assert.Equal(source, reviewed.Path);
        Assert.True(reviewed.Eligible, reviewed.Reason);
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
        await replacement.PublishAsync(new ReplacementRequest(Guid.NewGuid().ToString("N"), source, output, movieRoot, originals,
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
        var coordinator = new CompressionCoordinator(queue.Object, probe.Object, library.Object, Mock.Of<ISessionManager>(), registry, Mock.Of<IMediaSourceManager>());
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
    public async Task LoweringSavingsMinimumAllowsRetryButNeverRecompressesProcessedContent()
    {
        var movieRoot = Directory.CreateDirectory(Path.Combine(root, "movies")).FullName;
        var source = Path.Combine(movieRoot, "movie.mkv");
        await File.WriteAllBytesAsync(source, new byte[1024]);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-2));
        plugin.Configuration.IncludedFolders.Add(movieRoot);
        plugin.Configuration.QuarantineDirectory = Path.Combine(root, "originals");
        plugin.Configuration.RetentionDays = 7;
        var registry = new ContentRegistry(Path.Combine(root, "identities"));
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetVirtualFolders()).Returns([new VirtualFolderInfo { Locations = [movieRoot] }]);
        var probe = new Mock<IMediaProber>();
        probe.Setup(p => p.ProbeAsync(source, It.IsAny<CancellationToken>())).ReturnsAsync(new MediaProbeInfo
        { VideoStreamCount = 1, Width = 1920, Height = 1080, DurationSeconds = 60, PixelFormat = "yuv420p" });
        var coordinator = new CompressionCoordinator(Mock.Of<IJobQueue>(q => q.GetJobs() == Array.Empty<TranscodeJob>()),
            probe.Object, library.Object, Mock.Of<ISessionManager>(), registry, Mock.Of<IMediaSourceManager>());
        var movie = new Movie { Path = source, Name = "Movie" };
        var first = (await coordinator.InspectAsync(movie, false, default)).Snapshot!;
        registry.Save(new(first.Source.Sha256, first.Source.Length, "no-savings", first.ProfileKey, null, source));

        Assert.False((await coordinator.InspectAsync(movie, false, default)).Candidate.Eligible);
        plugin.Configuration.MinSavingsPercent = 5;
        Assert.True((await coordinator.InspectAsync(movie, false, default)).Candidate.Eligible);
        foreach (var kind in new[] { "original", "compressed" })
        {
            registry.Save(new(first.Source.Sha256, first.Source.Length, kind, first.ProfileKey, null, source));
            Assert.False((await coordinator.InspectAsync(movie, false, default)).Candidate.Eligible);
        }
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

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void CatalogUsesLibraryMetadataWithoutProbingOrEnqueueing(bool dolby, bool hdr10plus, bool selectable)
    {
        var movieRoot = Directory.CreateDirectory(Path.Combine(root, "movies")).FullName;
        var source = Path.Combine(movieRoot, "movie.mkv");
        File.WriteAllBytes(source, new byte[1234]);
        plugin.Configuration.IncludedFolders.Add(movieRoot);
        plugin.Configuration.EnableExperimentalDolbyVision = dolby;
        plugin.Configuration.EnableExperimentalHdr10Plus = hdr10plus;
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Wonka", Path = source, ProductionYear = 2023 };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetVirtualFolders()).Returns([new VirtualFolderInfo { Name = "Películas", Locations = [movieRoot] }]);
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([movie]);
        var sources = new Mock<IMediaSourceManager>();
        sources.Setup(s => s.GetMediaStreams(movie.Id)).Returns([new MediaStream { Type = MediaStreamType.Video, Height = 2160,
            ColorTransfer = "smpte2084", DvProfile = 8, Hdr10PlusPresentFlag = true }]);
        var queue = new Mock<IJobQueue>(MockBehavior.Strict);
        queue.Setup(q => q.GetJobs()).Returns(Array.Empty<TranscodeJob>());
        var probe = new Mock<IMediaProber>(MockBehavior.Strict);
        var coordinator = new CompressionCoordinator(queue.Object, probe.Object, library.Object, Mock.Of<ISessionManager>(),
            new ContentRegistry(Path.Combine(root, "identities")), sources.Object);

        var catalog = coordinator.MovieCatalog();

        var entry = Assert.Single(catalog.Items);
        Assert.Equal(1234, entry.Size);
        Assert.Equal(2160, entry.Height);
        Assert.Contains("Dolby Vision", entry.Format);
        Assert.Contains("HDR10+", entry.Format);
        Assert.Equal(selectable, entry.Selectable);
        if (!selectable) Assert.Contains("HDR", entry.Reason);
        Assert.Equal("Películas", Assert.Single(catalog.Folders).Name);
        probe.VerifyNoOtherCalls();
        queue.Verify(q => q.GetJobs(), Times.Once);
        queue.VerifyNoOtherCalls();
    }
}
