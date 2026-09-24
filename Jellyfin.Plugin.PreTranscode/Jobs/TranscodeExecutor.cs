using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Encoding;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using Jellyfin.Plugin.PreTranscode.Library;
using Jellyfin.Plugin.PreTranscode.Media;
using Jellyfin.Plugin.PreTranscode.Safety;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode.Jobs;

internal sealed class TranscodeExecutor
{
    private readonly IJobQueue queue;
    private readonly IMediaProber prober;
    private readonly IMediaEncoder encoder;
    private readonly CompressionCoordinator coordinator;
    private readonly ContentRegistry registry;
    private readonly ReplacementService replacement;
    private readonly ReplacedItemUpdater updater;
    private readonly ILibraryMonitor monitor;
    private readonly ILogger<TranscodeExecutor> logger;
    private readonly string tempDirectory;
    private DateTime nextMaintenance;
    public string MaintenanceError { get; private set; } = "";

    public TranscodeExecutor(IJobQueue queue, IMediaProber prober, IMediaEncoder encoder, CompressionCoordinator coordinator,
        ContentRegistry registry, ReplacementService replacement, ReplacedItemUpdater updater, ILibraryMonitor monitor,
        IApplicationPaths paths, ILogger<TranscodeExecutor> logger)
    {
        this.queue = queue; this.prober = prober; this.encoder = encoder; this.coordinator = coordinator;
        this.registry = registry; this.replacement = replacement; this.updater = updater; this.monitor = monitor; this.logger = logger;
        tempDirectory = Path.Combine(paths.DataPath, "jellyfin-compressor", "tmp");
    }
    public async Task RecoverAndMaintainAsync(CancellationToken token)
    {
        if (DateTime.UtcNow < nextMaintenance) return;
        try
        {
            await replacement.RecoverAsync(token).ConfigureAwait(false);
            await RefreshPendingAsync(token).ConfigureAwait(false);
            await replacement.PurgeExpiredAsync(DateTimeOffset.UtcNow, token).ConfigureAwait(false);
            var config = CompressionCoordinator.Config;
            if (config.QuarantineMaxBytes > 0 && !string.IsNullOrEmpty(config.QuarantineDirectory))
                await replacement.EnforceQuotaAsync(config.QuarantineDirectory, config.QuarantineMaxBytes, token).ConfigureAwait(false);
            TemporaryFiles.Clean(tempDirectory, queue.GetJobs());
            MaintenanceError = ""; coordinator.MaintenanceError = "";
            nextMaintenance = DateTime.UtcNow.AddMinutes(1);
        }
        catch (Exception ex) { MaintenanceError = ex.Message; coordinator.MaintenanceError = ex.Message; throw; }
    }
    private Task RefreshPendingAsync(CancellationToken token) => replacement.RefreshPendingAsync((r, ct) =>
        updater.RefreshSameItemAsync(r.Request.ItemId!, r.Request.SourcePath, r.Request.ItemDateCreated!.Value, ct), token);
    public async Task ExecuteAsync(TranscodeJob job, CancellationToken token, Action<Process>? onProcessStarted = null)
    {
        string? temp = null;
        try
        {
            var config = CompressionCoordinator.Config;
            var snapshot = job.Snapshot ?? throw new InvalidOperationException("Trabajo sin instantánea segura; vuelve a analizar la película.");
            if (!CompressionPolicy.CanRun(job, config)) { Hold(job, "Compresión automática desactivada."); return; }
            var scope = FolderPolicy.Evaluate(job.SourcePath, config, coordinator.Roots());
            if (!scope.Allowed) { Finish(job, JobStatus.Skipped, scope.Reason); return; }
            // Validate saved retention against the current library roots, not a later retention setting.
            var savedConfig = new Configuration.PluginConfiguration { QuarantineDirectory = snapshot.QuarantineRoot,
                RetentionDays = snapshot.RetentionDays, MinSavingsPercent = snapshot.MinSavingsPercent };
            FolderPolicy.ValidateConfiguration(savedConfig, coordinator.Roots());
            var sourceIdentity = await ContentRegistry.IdentifyAsync(job.SourcePath, token).ConfigureAwait(false);
            var prior = registry.Find(sourceIdentity.Sha256);
            if (prior is not null && (prior.Kind != "no-savings" || prior.ProfileKey == snapshot.ProfileKey))
            {
                var transaction = replacement.List().FirstOrDefault(r => r.Request.Output == sourceIdentity && r.Request.Source == snapshot.Source && r.Request.SourcePath == job.SourcePath);
                if (transaction is not null) { job.OutputPath = job.SourcePath; job.OutputSizeBytes = sourceIdentity.Length; Finish(job, JobStatus.Completed, "Recuperada tras reinicio."); }
                else Finish(job, JobStatus.Skipped, "Ya procesada; no se recomprime.");
                return;
            }
            if (sourceIdentity != snapshot.Source) throw new IOException("La película cambió desde que se encoló; vuelve a analizarla.");
            if (job.Automatic && !CompressionPolicy.MeetsMinimumMovieSize(sourceIdentity.Length, config.MinMovieSizeGb))
            { Finish(job, JobStatus.Skipped, $"No supera el mínimo de {config.MinMovieSizeGb} GB."); return; }
            if (!coordinator.MayPublish(job)) { Hold(job, "Esperando reproducción, horario o permisos de procesamiento."); return; }
            var source = await prober.ProbeAsync(job.SourcePath, token).ConfigureAwait(false) ?? throw new IOException("No se puede leer el vídeo.");
            var eligibility = CompressionPolicy.EligibilityError(source);
            if (eligibility is not null) { Finish(job, JobStatus.Skipped, eligibility); return; }
            var maxOutputBytes = (long)Math.Ceiling(snapshot.Source.Length * (1 - snapshot.MinSavingsPercent / 100));
            void SkipNoSavings(string detail)
            {
                registry.Save(new(snapshot.Source.Sha256, snapshot.Source.Length, "no-savings", snapshot.ProfileKey, null, job.SourcePath));
                TryDelete(temp);
                Finish(job, JobStatus.Skipped, detail);
            }
            Directory.CreateDirectory(tempDirectory);
            temp = Path.Combine(tempDirectory, job.Id + Path.GetExtension(job.SourcePath));
            if (job.VerifiedOutputIdentity is null || !File.Exists(temp))
            {
                Detail(job, "Comprimiendo");
                var args = CompressionPolicy.BuildArguments(snapshot.Profile, source, job.SourcePath, temp);
                (int ExitCode, string StdErrTail) encode;
                try
                {
                    encode = await FfmpegExecutor.RunAsync(FfmpegPaths.ResolveFfmpeg(encoder), args, source.DurationSeconds,
                        p => job.Progress = p, token, onProcessStarted, temp, maxOutputBytes).ConfigureAwait(false);
                }
                catch (OutputSizeLimitExceededException)
                {
                    token.ThrowIfCancellationRequested();
                    SkipNoSavings("La salida ya superaba el tamaño permitido para ahorrar "
                        + snapshot.MinSavingsPercent.ToString("0.#", CultureInfo.InvariantCulture) + " %. Se conserva el original.");
                    return;
                }
                if (encode.ExitCode != 0) { job.LogExcerpt = encode.StdErrTail; throw new IOException("FFmpeg no terminó correctamente (" + encode.ExitCode + ")."); }
                // The final mux can grow after the last poll. Avoid decoding an output that cannot be published.
                if (FileSizeOrZero(temp) >= maxOutputBytes)
                {
                    SkipNoSavings("La salida no alcanzó el ahorro mínimo; se conserva el original.");
                    return;
                }
                Detail(job, "Verificando pistas, capítulos y vídeo completo");
                var output = await prober.ProbeAsync(temp, token).ConfigureAwait(false) ?? throw new IOException("Resultado ilegible.");
                var invalid = CompressionPolicy.VerificationError(source, output, snapshot.Profile);
                if (invalid is not null) throw new IOException(invalid);
                var (decodeExit, decodeTail) = await FfmpegExecutor.RunAsync(FfmpegPaths.ResolveFfmpeg(encoder),
                    new[] { "-nostdin", "-v", "error", "-xerror", "-i", temp, "-map", "0:V", "-map", "0:a?", "-f", "null", "-" }, source.DurationSeconds,
                    _ => { }, token, onProcessStarted).ConfigureAwait(false);
                if (decodeExit != 0) { job.LogExcerpt = decodeTail; throw new IOException("La decodificación completa detectó errores."); }
                var identity = await ContentRegistry.IdentifyAsync(temp, token).ConfigureAwait(false);
                if (identity.Length >= snapshot.Source.Length * (1 - snapshot.MinSavingsPercent / 100))
                {
                    var savings = 100d * (snapshot.Source.Length - identity.Length) / snapshot.Source.Length;
                    var actual = savings.ToString("0.#", CultureInfo.InvariantCulture);
                    var required = snapshot.MinSavingsPercent.ToString("0.#", CultureInfo.InvariantCulture);
                    SkipNoSavings($"Ahorro {actual} %; mínimo {required} %. Se conserva el original.");
                    return;
                }
                job.VerifiedOutputPath = temp;
                job.VerifiedOutputIdentity = identity;
                queue.Update(job);
            }
            else if (await ContentRegistry.IdentifyAsync(temp, token).ConfigureAwait(false) != job.VerifiedOutputIdentity)
                throw new IOException("El temporal verificado cambió.");
            if (!FolderPolicy.SameFolder(CompressionCoordinator.Config.QuarantineDirectory, snapshot.QuarantineRoot))
            {
                TryDelete(temp);
                Finish(job, JobStatus.Skipped, "La carpeta de originales cambió; vuelve a analizar la película.");
                return;
            }
            if (!coordinator.MayPublish(job)) { Hold(job, "Comprimida y verificada; esperando para sustituir."); return; }
            Detail(job, "Guardando el original y sustituyendo");
            monitor.ReportFileSystemChangeBeginning(job.SourcePath);
            try
            {
                var request = new ReplacementRequest(Guid.NewGuid().ToString("N"), job.SourcePath, temp, snapshot.LibraryRoot,
                    snapshot.QuarantineRoot, snapshot.RetentionDays, snapshot.ProfileKey, snapshot.Source, job.VerifiedOutputIdentity!, job.ItemId, snapshot.ItemDateCreated);
                await replacement.PublishAsync(request, token, () => coordinator.MayPublish(job),
                    CompressionCoordinator.Config.QuarantineMaxBytes).ConfigureAwait(false);
                await RefreshPendingAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally { monitor.ReportFileSystemChangeComplete(job.SourcePath, false); }
            job.OutputPath = job.SourcePath;
            job.OutputSizeBytes = job.VerifiedOutputIdentity!.Length;
            Finish(job, JobStatus.Completed, "Comprimida; original sujeto al plazo y al límite de tamaño configurados.");
            TryDelete(temp);
        }
        catch (OperationCanceledException)
        {
            // Verified files remain available for an orderly restart; partial encodes are safe to remove.
            if (job.VerifiedOutputIdentity is null) TryDelete(temp);
            Finish(job, JobStatus.Cancelled, "Cancelada");
            nextMaintenance = DateTime.MinValue;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Compression failed for {Path}", job.SourcePath);
            job.ErrorMessage = ex.Message;
            Finish(job, JobStatus.Failed, ex.Message);
            if (job.VerifiedOutputIdentity is null) TryDelete(temp);
            nextMaintenance = DateTime.MinValue;
        }
    }
    private void Detail(TranscodeJob job, string text) { job.StatusDetail = text; queue.Update(job); }
    private void Hold(TranscodeJob job, string text)
    { job.Status = JobStatus.Pending; job.NotBeforeUtc = DateTime.UtcNow.AddMinutes(1); Detail(job, text); }
    private void Finish(TranscodeJob job, JobStatus status, string text)
    { job.Status = status; job.StatusDetail = text; job.FinishedUtc = DateTime.UtcNow; if (status == JobStatus.Completed) job.Progress = 100; queue.Update(job); }
    private void TryDelete(string? path)
    {
        if (path is null) return;
        try { FolderPolicy.RejectLinks(path); File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Could not remove temporary file {Path}", path); }
    }
    internal static long FileSizeOrZero(string path)
    { try { var f = new FileInfo(path); return f.Exists ? f.Length : 0; } catch (IOException) { return 0; } catch (UnauthorizedAccessException) { return 0; } }
}
