using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Jobs;
using Jellyfin.Plugin.PreTranscode.Media;
using Jellyfin.Plugin.PreTranscode.Safety;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.PreTranscode.Library;

public sealed record Candidate(string ItemId, string Name, string Path, bool Eligible, string Reason, long Size);
public sealed record FolderMovieCount(string Path, int Movies);

public sealed class CompressionCoordinator
{
    public string MaintenanceError { get; set; } = "";
    private readonly IJobQueue queue;
    private readonly IMediaProber prober;
    private readonly ILibraryManager library;
    private readonly ISessionManager sessions;
    private readonly ContentRegistry registry;
    private readonly SemaphoreSlim analysis = new(1, 1);
    public CompressionCoordinator(IJobQueue queue, IMediaProber prober, ILibraryManager library, ISessionManager sessions, ContentRegistry registry)
    { this.queue = queue; this.prober = prober; this.library = library; this.sessions = sessions; this.registry = registry; }
    public string[] Roots() => library.GetVirtualFolders().SelectMany(f => f.Locations).Distinct().ToArray();
    public static PluginConfiguration Config => Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Configuración no disponible.");
    private List<BaseItem> SelectedMovies(PluginConfiguration config, IReadOnlyList<string> roots) =>
        library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Movie }, IsVirtualItem = false, Recursive = true })
            .Where(item => item.Path is { Length: > 0 } path && Path.IsPathFullyQualified(path)
                && roots.Any(root => FolderPolicy.Contains(root, path))
                && config.IncludedFolders.Any(folder => FolderPolicy.Contains(folder, path))
                && !config.ExcludedFolders.Any(folder => FolderPolicy.Contains(folder, path))).ToList();
    public IReadOnlyList<FolderMovieCount> FolderMovieCounts()
    {
        var config = Config;
        var roots = Roots();
        FolderPolicy.ValidateConfiguration(config, roots);
        var movies = SelectedMovies(config, roots);
        return config.IncludedFolders.Select(folder => new FolderMovieCount(folder,
            movies.Count(movie => FolderPolicy.Contains(folder, movie.Path)))).ToArray();
    }
    public bool IsPlaying(string path, string itemId) => sessions.Sessions.Any(s => s.NowPlayingItem is not null &&
        (s.NowPlayingItem.Id.ToString("N").Equals(itemId.Replace("-", ""), StringComparison.OrdinalIgnoreCase)
         || string.Equals(s.NowPlayingItem.Path, path, FolderPolicy.Comparison)));
    public bool MayPublish(TranscodeJob job)
    {
        var config = Config;
        return CompressionPolicy.CanRun(job, config) && job.Snapshot is { } snapshot
            && !string.IsNullOrWhiteSpace(config.QuarantineDirectory)
            && FolderPolicy.SameFolder(config.QuarantineDirectory, snapshot.QuarantineRoot)
            && !config.QueuePaused && ProcessingWindow.IsOpen(config, DateTime.Now)
            && FolderPolicy.Evaluate(job.SourcePath, config, Roots()).Allowed && !IsPlaying(job.SourcePath, job.ItemId)
            && library.GetItemById(Guid.Parse(job.ItemId)) is Movie item && string.Equals(item.Path, job.SourcePath, FolderPolicy.Comparison);
    }
    public async Task<(Candidate Candidate, CompressionSnapshot? Snapshot)> InspectAsync(BaseItem item, bool automatic, CancellationToken token, bool applyMinimumSize = false, bool previewOnly = false)
    {
        Candidate Result(bool eligible, string reason, long size = 0) => new(item.Id.ToString("N"), item.Name, item.Path ?? "", eligible, reason, size);
        var config = Config;
        if (!CompressionPolicy.CanRun(new TranscodeJob { Automatic = automatic }, config)) return (Result(false, "Compresión desactivada."), null);
        if (item is not Movie || !File.Exists(item.Path)) return (Result(false, "Solo películas con archivo local disponible."), null);
        var roots = Roots();
        var decision = FolderPolicy.Evaluate(item.Path, config, roots);
        if (!decision.Allowed) return (Result(false, decision.Reason), null);
        FolderPolicy.ValidateConfiguration(config, roots);
        if (!ItemEvaluator.IsStable(item.Path, Math.Max(60, config.FileStabilitySeconds))) return (Result(false, "Archivo reciente; esperando estabilidad."), null);
        var size = new FileInfo(item.Path).Length;
        if (automatic || applyMinimumSize)
        {
            if (!CompressionPolicy.MeetsMinimumMovieSize(size, config.MinMovieSizeGb))
                return (Result(false, $"No supera el mínimo de {config.MinMovieSizeGb} GB.", size), null);
        }
        if (IsPlaying(item.Path, item.Id.ToString("N"))) return (Result(false, "En reproducción; se aplaza."), null);
        if (queue.GetJobs().Any(j => string.Equals(j.SourcePath, item.Path, FolderPolicy.Comparison) && j.Status is JobStatus.Pending or JobStatus.Processing))
            return (Result(false, "En cola o en curso."), null);
        var profile = config.Profiles.FirstOrDefault(p => p.Id == config.DefaultProfileId) ?? config.Profiles.FirstOrDefault() ?? throw new InvalidOperationException("Selecciona un perfil.");
        var effective = CompressionPolicy.EffectiveProfile(profile, item.Path);
        var key = CompressionPolicy.Key(effective);
        if (previewOnly)
        {
            var preview = await prober.ProbeAsync(item.Path, token).ConfigureAwait(false);
            var previewError = preview is null ? "No se puede leer el vídeo." : CompressionPolicy.EligibilityError(preview);
            return (Result(previewError is null, previewError ?? "Apta preliminarmente; se verificará antes de encolar.", size), null);
        }
        var identity = await ContentRegistry.IdentifyAsync(item.Path, token).ConfigureAwait(false);
        var prior = registry.Find(identity.Sha256);
        if (prior is not null)
        {
            registry.UpdateLocation(identity.Sha256, item.Path);
            if (prior.Kind != "no-savings" || prior.ProfileKey == key) return (Result(false, prior.Kind == "no-savings" ? "Sin ahorro con este perfil." : "Ya procesada; no se recomprime.", identity.Length), null);
        }
        var probe = await prober.ProbeAsync(item.Path, token).ConfigureAwait(false);
        var error = probe is null ? "No se puede leer el vídeo." : CompressionPolicy.EligibilityError(probe);
        if (error is not null) return (Result(false, error, identity.Length), null);
        return (Result(true, "Lista para comprimir", identity.Length), new(effective, identity, decision.Root!, config.QuarantineDirectory,
            config.RetentionDays, config.MinSavingsPercent, key, item.DateCreated));
    }
    public async Task<Candidate> EnqueueAsync(BaseItem item, bool automatic, CancellationToken token, bool applyMinimumSize = false)
    {
        var (candidate, snapshot) = await InspectAsync(item, automatic, token, applyMinimumSize).ConfigureAwait(false);
        if (snapshot is null) return candidate;
        var job = new TranscodeJob { SourcePath = item.Path, ItemId = item.Id.ToString("N"), DisplayName = item.Name,
            CreatedUtc = DateTime.UtcNow, ProfileId = snapshot.Profile.Id, Snapshot = snapshot, Automatic = automatic };
        var added = queue.Enqueue(job, _ =>
        {
            var prior = registry.Find(snapshot.Source.Sha256);
            return prior is not null && (prior.Kind != "no-savings" || prior.ProfileKey == snapshot.ProfileKey);
        });
        return candidate with { Reason = added ? "En cola" : "Ya en cola o procesada", Eligible = false };
    }
    public async Task<IReadOnlyList<Candidate>> ScanAsync(bool enqueue, bool automatic, IProgress<double>? progress, CancellationToken token)
    {
        if (automatic && !Config.AutomaticCompressionEnabled) return Array.Empty<Candidate>();
        if (!await analysis.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("Ya hay un análisis en curso.");
        try
        {
            var config = Config;
            var roots = Roots();
            FolderPolicy.ValidateConfiguration(config, roots);
            var items = SelectedMovies(config, roots);
            var results = new List<Candidate>();
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                try { results.Add(enqueue ? await EnqueueAsync(item, automatic, token, true).ConfigureAwait(false) : (await InspectAsync(item, automatic, token, true, previewOnly: true).ConfigureAwait(false)).Candidate); }
                catch (Exception ex) when (ex is not OperationCanceledException) { results.Add(new(item.Id.ToString("N"), item.Name, item.Path, false, ex.Message, 0)); }
                progress?.Report(results.Count * 100d / Math.Max(1, items.Count));
            }
            return results;
        }
        finally { analysis.Release(); }
    }
}
