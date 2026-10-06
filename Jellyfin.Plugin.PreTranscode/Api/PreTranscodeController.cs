using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using Jellyfin.Plugin.PreTranscode.Jobs;
using Jellyfin.Plugin.PreTranscode.Library;
using Jellyfin.Plugin.PreTranscode.Safety;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PreTranscode.Api;

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinCompressor")]
public class PreTranscodeController : ControllerBase
{
    private readonly CompressionCoordinator coordinator;
    private readonly IJobQueue queue;
    private readonly IQueueController control;
    private readonly ReplacementService replacement;
    private readonly ILibraryManager library;
    private readonly ILibraryMonitor monitor;
    private readonly ReplacedItemUpdater updater;
    private readonly IFfmpegCapabilitiesService capabilities;
    private readonly ManualOperationRunner manual;
    public PreTranscodeController(CompressionCoordinator coordinator, IJobQueue queue, IQueueController control,
        ReplacementService replacement, ILibraryManager library, ILibraryMonitor monitor, ReplacedItemUpdater updater, IFfmpegCapabilitiesService capabilities,
        ManualOperationRunner manual)
    { this.coordinator = coordinator; this.queue = queue; this.control = control; this.replacement = replacement; this.library = library; this.monitor = monitor; this.updater = updater; this.capabilities = capabilities; this.manual = manual; }

    [HttpGet("Configuration")]
    public ActionResult<PluginConfiguration> GetConfiguration() => Ok(CompressionCoordinator.Config);
    [HttpGet("FolderStatus")]
    public IActionResult FolderStatus()
    {
        try { return Ok(coordinator.FolderMovieCounts()); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        { return BadRequest(new { Message = ex.Message }); }
    }
    [HttpPost("Configuration")]
    public IActionResult SaveConfiguration([FromBody] PluginConfiguration config)
    {
        try
        {
            FolderPolicy.ValidateConfiguration(config, coordinator.Roots());
            if (config.Profiles.Count != 1) throw new InvalidOperationException("La v1 usa un único perfil.");
            CompressionPolicy.EffectiveProfile(config.Profiles[0], "validation.mkv");
            config.MaxConcurrentJobs = 1;
            config.QueuePaused = queue.IsPaused;
            config.FileStabilitySeconds = Math.Clamp(config.FileStabilitySeconds, 60, 86400);
            Plugin.Instance!.UpdateConfiguration(config);
            return Ok(config);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { return BadRequest(new { Message = ex.Message }); }
    }
    [HttpGet("Folders")]
    public IActionResult Folders([FromQuery] string? path = null)
    {
        try
        {
            var roots = coordinator.Roots();
            if (string.IsNullOrEmpty(path)) return Ok(roots.Select(p => new { Path = p, Name = p }));
            if (!roots.Any(r => FolderPolicy.Contains(r, path))) return BadRequest(new { Message = "Fuera de las bibliotecas." });
            FolderPolicy.RejectLinks(path);
            return Ok(Directory.EnumerateDirectories(path).Where(p => (System.IO.File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0)
                .OrderBy(p => p).Select(p => new { Path = p, Name = Path.GetFileName(p) }).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return BadRequest(new { Message = ex.Message }); }
    }
    [HttpGet("OriginalFolders")]
    public IActionResult OriginalFolders([FromQuery] string? path = null)
    {
        try
        {
            var roots = coordinator.Roots();
            var folders = QuarantineFolderBrowser.List(path, roots);
            return Ok(new { Path = path, Parent = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)),
                Selectable = path is not null && QuarantineFolderBrowser.CanSelect(path, roots), Folders = folders });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return BadRequest(new { Message = ex.Message }); }
    }
    [HttpGet("Movies")]
    public IActionResult Movies([FromQuery] string? query)
    {
        var tokens = ItemSearch.Tokenize(query);
        if (query is null || query.Trim().Length < 2 || query.Length > 100 || tokens.Length == 0)
            return BadRequest(new { Message = "Escribe al menos dos caracteres para buscar una película." });
        var items = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Movie }, IsVirtualItem = false, Recursive = true });
        return Ok(items.Where(item => !string.IsNullOrEmpty(item.Path) && ItemSearch.Matches(item.Name, item.Path, tokens))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Take(30)
            .Select(item => new { Id = item.Id, item.Name, item.Path }).ToArray());
    }
    [HttpGet("Catalog")]
    public IActionResult Catalog() => Ok(coordinator.MovieCatalog());

    [HttpPost("Selection/Review")]
    public IActionResult ReviewSelection([FromBody] Guid[] ids) => Selection(ids, false);
    [HttpPost("Selection/Queue")]
    public IActionResult QueueSelection([FromBody] Guid[] ids, [FromQuery] bool batch = false) => Selection(ids, true, batch);
    private IActionResult Selection(Guid[] ids, bool enqueue, bool batch = false)
    {
        if (ids.Length is < 1 or > 100 || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length)
            return BadRequest(new { Message = "Elige entre 1 y 100 películas diferentes." });
        try
        {
            return Accepted(manual.Start(enqueue ? "Selection" : "Review", async (progress, token) =>
            {
                var results = new List<Candidate>();
                foreach (var id in ids)
                {
                    token.ThrowIfCancellationRequested();
                    var item = library.GetItemById(id);
                    if (item is null) results.Add(new(id.ToString("N"), "Película no disponible", "", false, "Jellyfin ya no encuentra esta película.", 0));
                    else
                    {
                        try { results.Add(enqueue ? await coordinator.EnqueueAsync(item, false, token, ids.Length > 1 || batch).ConfigureAwait(false)
                            : (await coordinator.InspectAsync(item, false, token, ids.Length > 1, previewOnly: true).ConfigureAwait(false)).Candidate); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { results.Add(new(id.ToString("N"), item.Name, item.Path ?? "", false, ex.Message, 0)); }
                    }
                    progress.Report(results.Count * 100d / ids.Length);
                }
                return results;
            }));
        }
        catch (InvalidOperationException ex) { return BadRequest(new { Message = ex.Message }); }
    }
    [HttpGet("Capabilities")]
    public async Task<IActionResult> Capabilities(CancellationToken token)
    {
        var all = await capabilities.GetCapabilitiesAsync(false, token).ConfigureAwait(false);
        return Ok(all);
    }
    [HttpPost("Analyze")]
    public IActionResult Analyze()
    {
        try { return Accepted(manual.Start("Analyze", async (progress, token) =>
            await coordinator.ScanAsync(false, false, progress, token).ConfigureAwait(false))); }
        catch (InvalidOperationException ex) { return BadRequest(new { Message = ex.Message }); }
    }
    [HttpPost("Compress")]
    public IActionResult Compress()
    {
        try { return Accepted(manual.Start("Compress", async (progress, token) =>
            await coordinator.ScanAsync(true, false, progress, token).ConfigureAwait(false))); }
        catch (InvalidOperationException ex) { return BadRequest(new { Message = ex.Message }); }
    }
    [HttpPost("Items/{id}/Compress")]
    public IActionResult CompressItem(Guid id)
    {
        var item = library.GetItemById(id);
        if (item is null) return NotFound();
        try { return Accepted(manual.Start("Movie", async (_, token) =>
            await coordinator.EnqueueAsync(item, false, token).ConfigureAwait(false))); }
        catch (InvalidOperationException ex) { return BadRequest(new { Message = ex.Message }); }
    }
    [HttpGet("Manual")]
    public IActionResult ActiveManual() => Ok(new { Operation = manual.Latest() });
    [HttpGet("Manual/{id}")]
    public IActionResult Manual(Guid id) => manual.Get(id) is { } operation ? Ok(operation) : NotFound();
    [HttpPost("Manual/{id}/Cancel")]
    public IActionResult CancelAnalysis(Guid id) => manual.Cancel(id) ? Ok() : Conflict(new { Message = "No hay un análisis activo con ese identificador." });
    [HttpGet("Status")]
    public IActionResult Status() => Ok(new { Jobs = queue.GetJobs(), Paused = queue.IsPaused, Schedule = control.GetScheduleState(), Error = coordinator.MaintenanceError });
    [HttpPost("Pause")]
    public IActionResult Pause() { control.Pause(); return Ok(); }
    [HttpPost("Resume")]
    public IActionResult Resume() { control.Resume(); return Ok(); }
    [HttpPost("Jobs/{id}/Cancel")]
    public IActionResult Cancel(string id) => control.CancelJob(id) ? Ok() : NotFound();
    [HttpPost("Jobs/{id}/Retry")]
    public IActionResult Retry(string id)
    {
        var job = queue.Get(id);
        if (job is null) return NotFound();
        if (job.Status is not (JobStatus.Failed or JobStatus.Cancelled)) return BadRequest(new { Message = "Solo se reintentan errores o cancelaciones." });
        return CompressItem(Guid.Parse(job.ItemId));
    }
    [HttpDelete("History")]
    public IActionResult ClearHistory() { queue.ClearFinished(); return Ok(); }
    [HttpGet("Originals")]
    public IActionResult Originals() => Ok(replacement.List());
    [HttpPost("Originals/Purge")]
    public IActionResult Purge()
    {
        try { return Accepted(manual.Start("Purge", async (_, token) =>
            new { Deleted = await replacement.PurgeExpiredAsync(DateTimeOffset.UtcNow, token).ConfigureAwait(false) })); }
        catch (InvalidOperationException ex) { return BadRequest(new { Message = ex.Message }); }
    }
    [HttpPost("Originals/{id}/Restore")]
    public IActionResult Restore(string id)
    {
        var record = replacement.List().SingleOrDefault(r => r.Request.Id == id);
        if (record is null) return NotFound();
        var path = record.Request.SourcePath;
        var item = library.FindByPath(path, false);
        if (item is null || coordinator.IsPlaying(path, item.Id.ToString("N"))) return BadRequest(new { Message = "No se puede restaurar: ficha ausente o reproducción activa." });
        if (queue.GetJobs().Any(j => j.SourcePath == path && j.Status is JobStatus.Processing or JobStatus.Pending)) return BadRequest(new { Message = "Cancela primero el trabajo activo de esta película." });
        var dateCreated = item.DateCreated;
        try
        {
            return Accepted(manual.Start("Restore", async (progress, token) =>
            {
                monitor.ReportFileSystemChangeBeginning(path);
                try
                {
                    await replacement.RestoreAsync(id, token, () => !coordinator.IsPlaying(path, item.Id.ToString("N")), progress).ConfigureAwait(false);
                    progress.Report(95);
                    await replacement.RefreshPendingAsync((r, ct) => updater.RefreshSameItemAsync(r.Request.ItemId!, r.Request.SourcePath,
                        r.Request.ItemDateCreated ?? dateCreated, ct), CancellationToken.None).ConfigureAwait(false);
                    return null;
                }
                finally { monitor.ReportFileSystemChangeComplete(path, false); }
            }));
        }
        catch (InvalidOperationException ex) { return BadRequest(new { Message = ex.Message }); }
    }
}
