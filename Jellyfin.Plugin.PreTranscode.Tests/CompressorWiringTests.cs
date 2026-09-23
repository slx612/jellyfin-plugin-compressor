using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Jobs;
using Jellyfin.Plugin.PreTranscode.Library;
using Jellyfin.Plugin.PreTranscode.Media;
using Jellyfin.Plugin.PreTranscode.Safety;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
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
