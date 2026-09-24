using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Safety;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public sealed class CompressorSafetyTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("compressor-tests-").FullName;
    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
    private async Task<string> FileAt(string folder, string name, byte[] data)
    {
        var path = Path.Combine(Folder(folder), name);
        await File.WriteAllBytesAsync(path, data);
        return path;
    }

    [Fact]
    public void FoldersExcludeDescendantsButNotPrefixSiblings()
    {
        var movies = Folder("Movies");
        var keep = Folder("Movies/Keep");
        var config = new PluginConfiguration { IncludedFolders = new() { movies }, ExcludedFolders = new() { keep } };
        Assert.True(FolderPolicy.Evaluate(Path.Combine(movies, "a.mkv"), config, new[] { movies }).Allowed);
        Assert.False(FolderPolicy.Evaluate(Path.Combine(keep, "a.mkv"), config, new[] { movies }).Allowed);
        Assert.False(FolderPolicy.Evaluate(Path.Combine(root, "Movies2/a.mkv"), config, new[] { movies }).Allowed);
        Assert.False(FolderPolicy.Evaluate(Path.Combine(movies, "a.mkv"), new PluginConfiguration(), new[] { movies }).Allowed);
        config.QuarantineDirectory = keep;
        Assert.Throws<InvalidOperationException>(() => FolderPolicy.ValidateConfiguration(config, new[] { movies }));
    }

    [Fact]
    public void OriginalsBrowserMarksLibraryAncestorsAndDescendantsAsUnsafe()
    {
        var movies = Folder("Movies");
        Folder("Movies/Child");
        var originals = Folder("Originals");
        var entries = QuarantineFolderBrowser.List(root, new[] { movies });
        Assert.False(entries.Single(e => e.Path == movies).Selectable);
        Assert.True(entries.Single(e => e.Path == originals).Selectable);
        Assert.False(QuarantineFolderBrowser.CanSelect(root, new[] { movies }));
        Assert.False(QuarantineFolderBrowser.CanSelect(Path.Combine(movies, "Child"), new[] { movies }));
        Assert.True(QuarantineFolderBrowser.CanSelect(originals, new[] { movies }));
    }

    [Fact]
    public async Task IdentitySurvivesRenameAndRestartAndDetectsChangedContent()
    {
        var source = await FileAt("Movies", "film.mkv", new byte[] { 1, 2, 3 });
        var identity = await ContentRegistry.IdentifyAsync(source, default);
        var state = Folder("State");
        var registry = new ContentRegistry(state);
        registry.Save(new ContentRecord(identity.Sha256, identity.Length, "compressed", "p", null, source));
        var moved = Path.Combine(Folder("Elsewhere"), "renamed.mkv");
        File.Move(source, moved);
        Assert.Equal(identity, await ContentRegistry.IdentifyAsync(moved, default));
        Assert.Equal("compressed", new ContentRegistry(state).Find(identity.Sha256)!.Kind);
        await File.WriteAllBytesAsync(moved, new byte[] { 3, 2, 1 });
        Assert.NotEqual(identity, await ContentRegistry.IdentifyAsync(moved, default));
    }

    [Fact]
    public async Task CorruptRegistryFailsClosed()
    {
        var source = await FileAt("Movies", "film.mkv", new byte[] { 1 });
        var identity = await ContentRegistry.IdentifyAsync(source, default);
        var state = Folder("State");
        var registry = new ContentRegistry(state);
        registry.Save(new ContentRecord(identity.Sha256, 1, "compressed", "p", null, source));
        await File.WriteAllTextAsync(Path.Combine(state, identity.Sha256 + ".json"), "broken");
        Assert.ThrowsAny<Exception>(() => new ContentRegistry(state).Find(identity.Sha256));
    }

    private async Task<(ReplacementService Service, ContentRegistry Registry, ReplacementRequest Request)> Prepare()
    {
        var source = await FileAt("Movies", "film.mkv", Enumerable.Repeat((byte)42, 1000).ToArray());
        var output = await FileAt("Work", "result.mkv", new byte[] { 5, 6, 7 });
        File.SetLastWriteTimeUtc(source, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var registry = new ContentRegistry(Folder("Registry"));
        var service = new ReplacementService(Folder("Transactions"), registry);
        var request = new ReplacementRequest(Guid.NewGuid().ToString("N"), source, output,
            Folder("Movies"), Folder("Originals"), 7, "profile", await ContentRegistry.IdentifyAsync(source, default),
            await ContentRegistry.IdentifyAsync(output, default));
        return (service, registry, request);
    }

    [Fact]
    public async Task PublishKeepsExactPathDatesAndOriginalThenExpiresOnlyRegisteredFile()
    {
        var (service, registry, request) = await Prepare();
        var modified = File.GetLastWriteTimeUtc(request.SourcePath);
        var result = await service.PublishAsync(request, default);
        Assert.Equal(request.SourcePath, result.FinalPath);
        Assert.Equal(new byte[] { 5, 6, 7 }, await File.ReadAllBytesAsync(result.FinalPath));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(result.FinalPath));
        Assert.Equal(1000, new FileInfo(result.OriginalPath).Length);
        Assert.Equal("compressed", registry.Find(request.Output.Sha256)!.Kind);
        var unknown = await FileAt("Originals", "leave-me.mkv", new byte[] { 1 });
        var expires = Assert.Single(service.List()).ExpiresUtc!.Value;
        Assert.Equal(0, await service.PurgeExpiredAsync(expires.AddSeconds(-1), default));
        Assert.Equal(1, await service.PurgeExpiredAsync(expires, default));
        Assert.False(File.Exists(result.OriginalPath));
        Assert.True(File.Exists(unknown));
        Assert.NotNull(new ContentRegistry(Folder("Registry")).Find(request.Output.Sha256));
        Assert.Equal(0, await service.PurgeExpiredAsync(expires.AddDays(1), default));
    }

    [Fact]
    public async Task RestorePreservesOriginalAndRecompressionProtection()
    {
        var (service, registry, request) = await Prepare();
        await service.PublishAsync(request, default);
        await service.RestoreAsync(request.Id, default);
        Assert.Equal(request.Source, await ContentRegistry.IdentifyAsync(request.SourcePath, default));
        Assert.NotNull(registry.Find(request.Source.Sha256));
        Assert.Equal(ReplacementPhase.Restored, Assert.Single(service.List()).Phase);
    }

    [Fact]
    public async Task ChangedSourceCannotBeOverwritten()
    {
        var (service, _, request) = await Prepare();
        await File.WriteAllTextAsync(request.SourcePath, "a new download");
        await Assert.ThrowsAnyAsync<Exception>(() => service.PublishAsync(request, default));
        Assert.Equal("a new download", await File.ReadAllTextAsync(request.SourcePath));
    }

    [Fact]
    public async Task MissingOrChangedOutputBlocksPurgeAndRestore()
    {
        var (service, _, request) = await Prepare();
        var result = await service.PublishAsync(request, default);
        await File.WriteAllTextAsync(result.FinalPath, "unrelated movie");
        Assert.Equal(0, await service.PurgeExpiredAsync(DateTimeOffset.UtcNow.AddDays(8), default));
        Assert.True(File.Exists(result.OriginalPath));
        await Assert.ThrowsAnyAsync<Exception>(() => service.RestoreAsync(request.Id, default));
        Assert.Equal("unrelated movie", await File.ReadAllTextAsync(result.FinalPath));
    }

    [Fact]
    public async Task OutputMovedToRegisteredLocationStillAllowsPurge()
    {
        var (service, registry, request) = await Prepare();
        var result = await service.PublishAsync(request, default);
        var moved = Path.Combine(Folder("Other"), "film.mkv");
        File.Move(result.FinalPath, moved);
        registry.UpdateLocation(request.Output.Sha256, moved);
        Assert.Equal(1, await service.PurgeExpiredAsync(DateTimeOffset.UtcNow.AddDays(8), default));
    }

    [Fact]
    public async Task RecoveryCompletesAnAtomicPublicationBeforeItsJournalWasUpdated()
    {
        var (service, registry, request) = await Prepare();
        await service.PublishAsync(request, default);
        var record = Assert.Single(service.List());
        record.Phase = ReplacementPhase.Publishing;
        record.CompletedUtc = null;
        record.ExpiresUtc = null;
        AtomicFile.WriteJson(Path.Combine(Folder("Transactions"), request.Id + ".json"), record);
        var restarted = new ReplacementService(Folder("Transactions"), registry);
        await restarted.RecoverAsync(default);
        Assert.Equal(ReplacementPhase.Completed, Assert.Single(restarted.List()).Phase);
        Assert.Equal(request.Output, await ContentRegistry.IdentifyAsync(request.SourcePath, default));
    }

    [Fact]
    public async Task DifferentExtensionIsRejectedToProtectJellyfinIdentity()
    {
        var (service, _, request) = await Prepare();
        var mp4 = Path.ChangeExtension(request.VerifiedOutputPath, ".mp4");
        File.Move(request.VerifiedOutputPath, mp4);
        await Assert.ThrowsAnyAsync<Exception>(() => service.PublishAsync(request with { VerifiedOutputPath = mp4 }, default));
        Assert.Equal(request.Source, await ContentRegistry.IdentifyAsync(request.SourcePath, default));
    }

    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public async Task JellyfinRefreshIntentSurvivesRestartAndBlocksExpiryUntilAcknowledged()
    {
        var (service, registry, request) = await Prepare();
        request = request with { ItemId = Guid.NewGuid().ToString("N"), ItemDateCreated = new DateTime(2019, 1, 1) };
        await service.PublishAsync(request, default);
        Assert.True(Assert.Single(service.List()).LibraryRefreshPending);
        Assert.Equal(0, await service.PurgeExpiredAsync(DateTimeOffset.UtcNow.AddDays(8), default));
        var restarted = new ReplacementService(Folder("Transactions"), registry);
        await Assert.ThrowsAsync<IOException>(() => restarted.RefreshPendingAsync((_, _) => throw new IOException("Jellyfin temporarily unavailable"), default));
        Assert.True(Assert.Single(restarted.List()).LibraryRefreshPending);
        var calls = 0;
        await restarted.RefreshPendingAsync((record, _) => { Assert.Equal(request.ItemId, record.Request.ItemId); calls++; return Task.CompletedTask; }, default);
        Assert.Equal(1, calls);
        Assert.False(Assert.Single(restarted.List()).LibraryRefreshPending);
        Assert.Equal(1, await restarted.PurgeExpiredAsync(DateTimeOffset.UtcNow.AddDays(8), default));
    }

    [Fact]
    public void TemporaryCleanupKeepsActiveJobsAndUnknownFiles()
    {
        var temp = Folder("Temp");
        var activeId = Guid.NewGuid().ToString("N");
        var finishedId = Guid.NewGuid().ToString("N");
        var active = Path.Combine(temp, activeId + ".mkv");
        var finished = Path.Combine(temp, finishedId + ".mkv");
        var unknown = Path.Combine(temp, "unrelated.mkv");
        File.WriteAllText(active, "active"); File.WriteAllText(finished, "finished"); File.WriteAllText(unknown, "unknown");
        TemporaryFiles.Clean(temp, new[] { new Jellyfin.Plugin.PreTranscode.Jobs.TranscodeJob { Id = activeId } });
        Assert.True(File.Exists(active)); Assert.False(File.Exists(finished)); Assert.True(File.Exists(unknown));
    }
}
