using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using Jellyfin.Plugin.PreTranscode.Jobs;
using Jellyfin.Plugin.PreTranscode.Library;
using Jellyfin.Plugin.PreTranscode.Safety;
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
    public PreTranscodeController(CompressionCoordinator coordinator, IJobQueue queue, IQueueController control,
        ReplacementService replacement, ILibraryManager library, ILibraryMonitor monitor, ReplacedItemUpdater updater, IFfmpegCapabilitiesService capabilities)
    { this.coordinator = coordinator; this.queue = queue; this.control = control; this.replacement = replacement; this.library = library; this.monitor = monitor; this.updater = updater; this.capabilities = capabilities; }

    [HttpGet("Configuration")]
    public ActionResult<PluginConfiguration> GetConfiguration() => Ok(CompressionCoordinator.Config);
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
            config.FileStabilitySeconds = Math.Max(60, config.FileStabilitySeconds);
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
    [HttpGet("Capabilities")]
    public async Task<IActionResult> Capabilities(CancellationToken token)
    {
        var all = await capabilities.GetCapabilitiesAsync(false, token).ConfigureAwait(false);
        return Ok(all);
    }
    [HttpPost("Analyze")]
    public async Task<IActionResult> Analyze(CancellationToken token)
    {
        try { return Ok(await coordinator.ScanAsync(false, false, null, token).ConfigureAwait(false)); }
        catch (InvalidOperationException ex) { return BadRequest(new { Message = ex.Message }); }
    }
    [HttpPost("Compress")]
    public async Task<IActionResult> Compress(CancellationToken token)
    {
        try { return Ok(await coordinator.ScanAsync(true, false, null, token).ConfigureAwait(false)); }
        catch (InvalidOperationException ex) { return BadRequest(new { Message = ex.Message }); }
    }
    [HttpPost("Items/{id}/Compress")]
    public async Task<IActionResult> CompressItem(Guid id, CancellationToken token)
    {
        var item = library.GetItemById(id);
        if (item is null) return NotFound();
        try { return Ok(await coordinator.EnqueueAsync(item, false, token).ConfigureAwait(false)); }
        catch (InvalidOperationException ex) { return BadRequest(new { Message = ex.Message }); }
    }
    [HttpGet("Status")]
    public IActionResult Status() => Ok(new { Jobs = queue.GetJobs(), Paused = queue.IsPaused, Schedule = control.GetScheduleState(), Error = coordinator.MaintenanceError });
    [HttpPost("Pause")]
    public IActionResult Pause() { control.Pause(); return Ok(); }
    [HttpPost("Resume")]
    public IActionResult Resume() { control.Resume(); return Ok(); }
    [HttpPost("Jobs/{id}/Cancel")]
    public IActionResult Cancel(string id) => control.CancelJob(id) ? Ok() : NotFound();
    [HttpPost("Jobs/{id}/Retry")]
    public async Task<IActionResult> Retry(string id, CancellationToken token)
    {
        var job = queue.Get(id);
        if (job is null) return NotFound();
        if (job.Status is not (JobStatus.Failed or JobStatus.Cancelled)) return BadRequest(new { Message = "Solo se reintentan errores o cancelaciones." });
        return await CompressItem(Guid.Parse(job.ItemId), token).ConfigureAwait(false);
    }
    [HttpDelete("History")]
    public IActionResult ClearHistory() { queue.ClearFinished(); return Ok(); }
    [HttpGet("Originals")]
    public IActionResult Originals() => Ok(replacement.List());
    [HttpPost("Originals/Purge")]
    public async Task<IActionResult> Purge(CancellationToken token) => Ok(new { Deleted = await replacement.PurgeExpiredAsync(DateTimeOffset.UtcNow, token).ConfigureAwait(false) });
    [HttpPost("Originals/{id}/Restore")]
    public async Task<IActionResult> Restore(string id, CancellationToken token)
    {
        var record = replacement.List().SingleOrDefault(r => r.Request.Id == id);
        if (record is null) return NotFound();
        var path = record.Request.SourcePath;
        var item = library.FindByPath(path, false);
        if (item is null || coordinator.IsPlaying(path, item.Id.ToString("N"))) return BadRequest(new { Message = "No se puede restaurar: ficha ausente o reproducción activa." });
        if (queue.GetJobs().Any(j => j.SourcePath == path && j.Status is JobStatus.Processing or JobStatus.Pending)) return BadRequest(new { Message = "Cancela primero el trabajo activo de esta película." });
        var dateCreated = item.DateCreated;
        monitor.ReportFileSystemChangeBeginning(path);
        try
        {
            await replacement.RestoreAsync(id, token, () => !coordinator.IsPlaying(path, item.Id.ToString("N"))).ConfigureAwait(false);
            await replacement.RefreshPendingAsync((r, ct) => updater.RefreshSameItemAsync(r.Request.ItemId!, r.Request.SourcePath,
                r.Request.ItemDateCreated ?? dateCreated, ct), CancellationToken.None).ConfigureAwait(false);
            return Ok();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { return BadRequest(new { Message = ex.Message }); }
        finally { monitor.ReportFileSystemChangeComplete(path, false); }
    }
}
