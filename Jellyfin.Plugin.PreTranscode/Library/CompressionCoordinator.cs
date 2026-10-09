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
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.PreTranscode.Library;

public sealed record Candidate(string ItemId, string Name, string Path, bool Eligible, string Reason, long Size);
public sealed record FolderMovieCount(string Path, int Movies);
public sealed record CatalogFolder(string Name, string Path);
public sealed record CatalogMovie(string Id, string Name, string Path, int? Year, long Size, int? Height, string Format, bool HasImage, bool Selectable, string Reason);
public sealed record MovieCatalogInfo(IReadOnlyList<CatalogMovie> Items, IReadOnlyList<CatalogFolder> Folders);

public sealed class CompressionCoordinator
{
    public string MaintenanceError { get; set; } = "";
    private readonly IJobQueue queue;
    private readonly IMediaProber prober;
    private readonly ILibraryManager library;
    private readonly IMediaSourceManager mediaSources;
    private readonly ISessionManager sessions;
    private readonly ContentRegistry registry;
    private readonly SemaphoreSlim analysis = new(1, 1);
    public CompressionCoordinator(IJobQueue queue, IMediaProber prober, ILibraryManager library, ISessionManager sessions,
        ContentRegistry registry, IMediaSourceManager mediaSources)
    { this.queue = queue; this.prober = prober; this.library = library; this.sessions = sessions; this.registry = registry; this.mediaSources = mediaSources; }
    internal static void MergeLibraryHdrFacts(MediaProbeInfo info, IEnumerable<MediaStream> streams)
    {
        foreach (var stream in streams.Where(s => s.Type == MediaStreamType.Video))
        {
            info.HasHdr10Plus |= stream.Hdr10PlusPresentFlag == true;
            info.IsDolbyVision |= stream.DvProfile is > 0;
            info.IsHdr |= stream.ColorTransfer is "smpte2084" or "arib-std-b67";
        }
    }
    internal void MergeLibraryHdrFacts(Guid itemId, MediaProbeInfo info)
    {
        if (!info.IsHdr && !info.IsDolbyVision && info.BitDepth < 10) return;
        var streams = mediaSources.GetMediaStreams(itemId)
            ?? throw new InvalidOperationException("No se pueden verificar los metadatos HDR de Jellyfin.");
        MergeLibraryHdrFacts(info, streams);
    }
    public string[] Roots() => library.GetVirtualFolders().SelectMany(f => f.Locations).Distinct().ToArray();
    public MovieCatalogInfo MovieCatalog()
    {
        var config = Config;
        var folders = library.GetVirtualFolders().SelectMany(f => f.Locations.Select(path => new CatalogFolder(f.Name ?? path, path))).DistinctBy(f => f.Path).ToArray();
        var roots = folders.Select(f => f.Path).ToArray();
        var jobs = queue.GetJobs();
        var result = new List<CatalogMovie>();
        foreach (var movie in library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Movie }, IsVirtualItem = false, Recursive = true }))
        {
            if (string.IsNullOrEmpty(movie.Path) || !Path.IsPathFullyQualified(movie.Path) || !roots.Any(root => FolderPolicy.Contains(root, movie.Path))) continue;
            long size = 0;
            string reason = "";
            MediaStream? video = null;
            var format = "Sin datos";
            try
            {
                size = new FileInfo(movie.Path).Length;
                var decision = FolderPolicy.Evaluate(movie.Path, config, roots);
                if (!decision.Allowed) reason = decision.Reason;
                video = mediaSources.GetMediaStreams(movie.Id)?.FirstOrDefault(stream => stream.Type == MediaStreamType.Video);
                if (video is not null)
                {
                    var tags = new List<string>();
                    if (video.ColorTransfer == "smpte2084") tags.Add("HDR10");
                    else if (video.ColorTransfer == "arib-std-b67") tags.Add("HLG");
                    if (video.Hdr10PlusPresentFlag == true) tags.Add("HDR10+");
                    if (video.DvProfile is > 0) tags.Add("Dolby Vision");
                    format = tags.Count == 0 ? "SDR" : string.Join(" · ", tags);
                    if (reason.Length == 0 && ((video.DvProfile is not > 0 && tags.Count > 0 && !config.EnableExperimentalHdr)
                        || (video.DvProfile is > 0 && !config.EnableExperimentalDolbyVision)
                        || (video.Hdr10PlusPresentFlag == true && !config.EnableExperimentalHdr10Plus)))
                        reason = "Opciones HDR desactivadas. Actívalas en Ajustes para comprimir manualmente.";
                }
                if (jobs.Any(job => job.ItemId == movie.Id.ToString("N") && job.Status is JobStatus.Pending or JobStatus.Processing)) reason = "Ya está en la cola.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { reason = ex.Message; }
            result.Add(new(movie.Id.ToString("N"), movie.Name, movie.Path, movie.ProductionYear, size, video?.Height, format,
                movie.ImageInfos.Any(image => image.Type == ImageType.Primary), reason.Length == 0, reason));
        }
        return new(result.OrderByDescending(movie => movie.Size).ThenBy(movie => movie.Name, StringComparer.OrdinalIgnoreCase).ToArray(), folders);
    }
    public static PluginConfiguration Config => Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Configuración no disponible.");
    private List<BaseItem> SelectedMovies(PluginConfiguration config, IReadOnlyList<string> roots) =>
        library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Movie }, IsVirtualItem = false, Recursive = true })
            .Where(item => item.Path is { Length: > 0 } path && Path.IsPathFullyQualified(path)
                && roots.Any(root => FolderPolicy.Contains(root, path))
                && config.IncludedFolders.Any(folder => roots.Any(root => FolderPolicy.Contains(root, folder)) && FolderPolicy.Contains(folder, path))
                && !config.ExcludedFolders.Any(folder => roots.Any(root => FolderPolicy.Contains(root, folder)) && FolderPolicy.Contains(folder, path))).ToList();
    public IReadOnlyList<FolderMovieCount> FolderMovieCounts()
    {
        var config = Config;
        var roots = Roots();
        FolderPolicy.ValidateConfiguration(config, roots);
        var movies = SelectedMovies(config, roots);
        return config.IncludedFolders.Where(folder => roots.Any(root => FolderPolicy.Contains(root, folder))).Select(folder => new FolderMovieCount(folder,
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
        var key = CompressionPolicy.Key(effective, config.MinSavingsPercent);
        if (previewOnly)
        {
            var preview = await prober.ProbeAsync(item.Path, token).ConfigureAwait(false);
            if (preview is not null) MergeLibraryHdrFacts(item.Id, preview);
            var previewError = preview is null ? "No se puede leer el vídeo." : CompressionPolicy.EligibilityError(preview, effective, config, automatic, !applyMinimumSize);
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
        if (probe is not null) MergeLibraryHdrFacts(item.Id, probe);
        var error = probe is null ? "No se puede leer el vídeo." : CompressionPolicy.EligibilityError(probe, effective, config, automatic, !applyMinimumSize);
        if (error is not null) return (Result(false, error, identity.Length), null);
        return (Result(true, "Lista para comprimir", identity.Length), new CompressionSnapshot(effective, identity, decision.Root!, config.QuarantineDirectory,
            config.RetentionDays, config.MinSavingsPercent, key, item.DateCreated) { SingleMovieSelection = !automatic && !applyMinimumSize });
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
