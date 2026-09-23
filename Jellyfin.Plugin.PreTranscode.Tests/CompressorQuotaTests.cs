using Jellyfin.Plugin.PreTranscode.Safety;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public sealed class CompressorQuotaTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("compressor-quota-").FullName;
    private readonly string movies;
    private readonly string work;
    private readonly string originals;
    private readonly ContentRegistry registry;
    private readonly ReplacementService service;

    public CompressorQuotaTests()
    {
        movies = Directory.CreateDirectory(Path.Combine(root, "Movies")).FullName;
        work = Directory.CreateDirectory(Path.Combine(root, "Work")).FullName;
        originals = Directory.CreateDirectory(Path.Combine(root, "Originals")).FullName;
        registry = new ContentRegistry(Path.Combine(root, "Registry"));
        service = new ReplacementService(Path.Combine(root, "Transactions"), registry);
    }

    private async Task<ReplacementRequest> Request(int number, bool refreshPending = false)
    {
        var source = Path.Combine(movies, $"film{number}.mkv");
        var output = Path.Combine(work, $"film{number}.mkv");
        await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)number, 1000).ToArray());
        await File.WriteAllBytesAsync(output, new[] { (byte)(number + 100), (byte)(number + 101) });
        return new ReplacementRequest(Guid.NewGuid().ToString("N"), source, output, movies, originals, 7, "profile",
            await ContentRegistry.IdentifyAsync(source, default), await ContentRegistry.IdentifyAsync(output, default),
            refreshPending ? Guid.NewGuid().ToString("N") : null, refreshPending ? new DateTime(2020, 1, 1) : null);
    }

    [Fact]
    public async Task LimitEvictsOldestVerifiedOriginalBeforeNewReplacement()
    {
        var first = await Request(1);
        var second = await Request(2);
        var third = await Request(3);
        var firstResult = await service.PublishAsync(first, default, maxQuarantineBytes: 2500);
        await service.PublishAsync(second, default, maxQuarantineBytes: 2500);
        await service.PublishAsync(third, default, maxQuarantineBytes: 2500);
        Assert.False(File.Exists(firstResult.OriginalPath));
        Assert.Equal(ReplacementPhase.Purged, service.List().Single(r => r.Request.Id == first.Id).Phase);
        Assert.Equal(2, service.List().Count(r => r.Phase == ReplacementPhase.Completed));
        Assert.Equal(third.Output, await ContentRegistry.IdentifyAsync(third.SourcePath, default));
        Assert.NotNull(registry.Find(first.Output.Sha256));
    }

    [Fact]
    public async Task ForeignFilesCountTowardLimitButAreNeverDeleted()
    {
        var unrelated = Path.Combine(originals, "unrelated.mkv");
        await File.WriteAllBytesAsync(unrelated, new byte[900]);
        var request = await Request(4);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PublishAsync(request, default, maxQuarantineBytes: 1000));
        Assert.True(File.Exists(unrelated));
        Assert.Equal(request.Source, await ContentRegistry.IdentifyAsync(request.SourcePath, default));
        Assert.Empty(service.List());
    }

    [Fact]
    public async Task PendingRefreshIsSkippedAndOldestVerifiedOriginalIsEvicted()
    {
        var first = await Request(5, refreshPending: true);
        var second = await Request(6);
        var third = await Request(7);
        var firstResult = await service.PublishAsync(first, default, maxQuarantineBytes: 2500);
        var secondResult = await service.PublishAsync(second, default, maxQuarantineBytes: 2500);
        await service.PublishAsync(third, default, maxQuarantineBytes: 2500);
        Assert.True(File.Exists(firstResult.OriginalPath));
        Assert.False(File.Exists(secondResult.OriginalPath));
        Assert.Equal(third.Output, await ContentRegistry.IdentifyAsync(third.SourcePath, default));
    }

    [Fact]
    public async Task NoVerifiedOriginalPreservesNewSource()
    {
        var first = await Request(14, refreshPending: true);
        var second = await Request(15, refreshPending: true);
        var third = await Request(16);
        var firstResult = await service.PublishAsync(first, default, maxQuarantineBytes: 2500);
        var secondResult = await service.PublishAsync(second, default, maxQuarantineBytes: 2500);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PublishAsync(third, default, maxQuarantineBytes: 2500));
        Assert.True(File.Exists(firstResult.OriginalPath));
        Assert.True(File.Exists(secondResult.OriginalPath));
        Assert.Equal(third.Source, await ContentRegistry.IdentifyAsync(third.SourcePath, default));
    }

    [Fact]
    public async Task EquivalentFolderPathStillFindsRecordedOriginals()
    {
        var first = await Request(17);
        var second = await Request(18);
        var old = await service.PublishAsync(first, default);
        await service.PublishAsync(second with { QuarantineRoot = originals + Path.DirectorySeparatorChar }, default, maxQuarantineBytes: 1500);
        Assert.False(File.Exists(old.OriginalPath));
        Assert.Equal(ReplacementPhase.Purged, service.List().Single(r => r.Request.Id == first.Id).Phase);
    }

    [Fact]
    public async Task RecoveryReturnsIntactPurgingOriginalToRestorableState()
    {
        var request = await Request(19);
        var published = await service.PublishAsync(request, default);
        var record = Assert.Single(service.List());
        record.Phase = ReplacementPhase.Purging;
        AtomicFile.WriteJson(Path.Combine(root, "Transactions", request.Id + ".json"), record);
        var restarted = new ReplacementService(Path.Combine(root, "Transactions"), registry);
        await restarted.RecoverAsync(default);
        Assert.Equal(ReplacementPhase.Completed, Assert.Single(restarted.List()).Phase);
        await restarted.RestoreAsync(request.Id, default);
        Assert.False(File.Exists(published.OriginalPath));
        Assert.Equal(request.Source, await ContentRegistry.IdentifyAsync(request.SourcePath, default));
    }

    [Fact]
    public async Task LoweringLimitIsEnforcedWithoutNewCompression()
    {
        var first = await Request(8);
        var second = await Request(9);
        var old = await service.PublishAsync(first, default);
        await service.PublishAsync(second, default);
        Assert.Equal(1, await service.EnforceQuotaAsync(originals, 1500, default));
        Assert.False(File.Exists(old.OriginalPath));
        Assert.Equal(0, await service.EnforceQuotaAsync(originals, 1500, default));
    }

    [Fact]
    public async Task VerifiedRestorationCleansQuarantineAndSurvivesRestart()
    {
        var request = await Request(10, refreshPending: true);
        var published = await service.PublishAsync(request, default);
        await service.RestoreAsync(request.Id, default);
        Assert.True(File.Exists(published.OriginalPath));
        await File.WriteAllBytesAsync(published.OriginalPath + ".compressed", new[] { (byte)110, (byte)111 });
        var restarted = new ReplacementService(Path.Combine(root, "Transactions"), registry);
        await restarted.RefreshPendingAsync((_, _) => Task.CompletedTask, default);
        Assert.Equal(request.Source, await ContentRegistry.IdentifyAsync(request.SourcePath, default));
        Assert.False(File.Exists(published.OriginalPath));
        Assert.False(File.Exists(published.OriginalPath + ".compressed"));
        Assert.Equal(ReplacementPhase.Restored, Assert.Single(restarted.List()).Phase);
        Assert.NotNull(registry.Find(request.Source.Sha256));
    }

    [Fact]
    public async Task RestorationWithoutJellyfinItemCleansImmediately()
    {
        var request = await Request(11);
        var published = await service.PublishAsync(request, default);
        await service.RestoreAsync(request.Id, default);
        Assert.False(File.Exists(published.OriginalPath));
        Assert.Equal(request.Source, await ContentRegistry.IdentifyAsync(request.SourcePath, default));
    }

    [Fact]
    public async Task ChangedRestoredMovieRetainsQuarantineCopyForInspection()
    {
        var request = await Request(12, refreshPending: true);
        var published = await service.PublishAsync(request, default);
        await service.RestoreAsync(request.Id, default);
        await File.WriteAllTextAsync(request.SourcePath, "changed after restoration");
        await service.RefreshPendingAsync((_, _) => Task.CompletedTask, default);
        Assert.True(File.Exists(published.OriginalPath));
        Assert.Contains("verificación", Assert.Single(service.List()).RetentionStatus);
    }

    [Fact]
    public async Task AlteredLegacyBackupDefersOnlyItsCleanup()
    {
        var first = await Request(20, refreshPending: true);
        var second = await Request(21, refreshPending: true);
        var firstResult = await service.PublishAsync(first, default);
        var secondResult = await service.PublishAsync(second, default);
        await service.RestoreAsync(first.Id, default);
        await service.RestoreAsync(second.Id, default);
        var legacy = firstResult.OriginalPath + ".compressed";
        await File.WriteAllTextAsync(legacy, "modified copy");
        await service.RefreshPendingAsync((_, _) => Task.CompletedTask, default);
        Assert.True(File.Exists(firstResult.OriginalPath));
        Assert.True(File.Exists(legacy));
        Assert.False(File.Exists(secondResult.OriginalPath));
        Assert.Contains("aplazada", service.List().Single(r => r.Request.Id == first.Id).RetentionStatus);
        await File.WriteAllBytesAsync(legacy, new[] { (byte)120, (byte)121 });
        await service.RefreshPendingAsync((_, _) => Task.CompletedTask, default);
        Assert.False(File.Exists(firstResult.OriginalPath));
        Assert.False(File.Exists(legacy));
    }

    [Fact]
    public void CapacityLimitMustBeNonnegativeAndBounded()
    {
        var config = new Jellyfin.Plugin.PreTranscode.Configuration.PluginConfiguration
            { QuarantineDirectory = originals, RetentionDays = 7, QuarantineMaxBytes = -1 };
        Assert.Throws<InvalidOperationException>(() => FolderPolicy.ValidateConfiguration(config, new[] { movies }));
        config.QuarantineMaxBytes = 1125899906842625L;
        Assert.Throws<InvalidOperationException>(() => FolderPolicy.ValidateConfiguration(config, new[] { movies }));
        config.QuarantineMaxBytes = 0;
        FolderPolicy.ValidateConfiguration(config, new[] { movies });
    }

    public void Dispose() => Directory.Delete(root, true);
}
