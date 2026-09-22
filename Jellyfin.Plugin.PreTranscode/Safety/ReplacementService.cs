using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.PreTranscode.Safety;

public enum ReplacementPhase { Prepared, BackupVerified, Publishing, Completed, Purging, Purged, Restoring, Restored, Aborted }
public sealed record ReplacementRequest(string Id, string SourcePath, string VerifiedOutputPath, string LibraryRoot,
    string QuarantineRoot, int RetentionDays, string ProfileKey, ContentIdentity Source, ContentIdentity Output);
public sealed record ReplacementResult(string Id, string FinalPath, string OriginalPath);
public sealed class ReplacementRecord
{
    public ReplacementRequest Request { get; set; } = null!;
    public string OriginalPath { get; set; } = "";
    public string StagedPath { get; set; } = "";
    public DateTime ModifiedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public ReplacementPhase Phase { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
}

// One lock owns publication, recovery, expiry and restore: these operations must never overlap.
public sealed class ReplacementService
{
    private readonly string directory;
    private readonly ContentRegistry registry;
    private readonly SemaphoreSlim gate = new(1, 1);
    public ReplacementService(string directory, ContentRegistry registry) { this.directory = directory; this.registry = registry; }
    private string Journal(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Identificador de transacción inválido.");
        return Path.Combine(directory, id + ".json");
    }
    private void Save(ReplacementRecord record) => AtomicFile.WriteJson(Journal(record.Request.Id), record);
    private static string Original(ReplacementRequest r) => Path.Combine(r.QuarantineRoot, r.Id, Path.GetRelativePath(r.LibraryRoot, r.SourcePath));
    private static string Stage(ReplacementRequest r) => Path.Combine(Path.GetDirectoryName(r.SourcePath)!, ".compressor-" + r.Id + ".tmp");
    private static void Validate(ReplacementRecord record)
    {
        var r = record.Request;
        if (!Guid.TryParseExact(r.Id, "N", out _) || !Path.IsPathFullyQualified(r.SourcePath)
            || !FolderPolicy.Contains(r.LibraryRoot, r.SourcePath) || r.RetentionDays is < 1 or > 3650
            || FolderPolicy.Contains(r.LibraryRoot, r.QuarantineRoot) || FolderPolicy.Contains(r.QuarantineRoot, r.LibraryRoot)
            || !string.Equals(record.OriginalPath, Original(r), FolderPolicy.Comparison)
            || !string.Equals(record.StagedPath, Stage(r), FolderPolicy.Comparison))
            throw new InvalidDataException("Rutas de transacción inválidas.");
        foreach (var path in new[] { r.SourcePath, r.QuarantineRoot, record.OriginalPath, record.StagedPath }) FolderPolicy.RejectLinks(path);
    }
    public IReadOnlyList<ReplacementRecord> List()
    {
        FolderPolicy.RejectLinks(directory);
        if (!Directory.Exists(directory)) return Array.Empty<ReplacementRecord>();
        return Directory.EnumerateFiles(directory, "*.json").Select(path =>
        {
            FolderPolicy.RejectLinks(path);
            var record = JsonSerializer.Deserialize<ReplacementRecord>(File.ReadAllText(path)) ?? throw new InvalidDataException("Diario vacío.");
            Validate(record);
            if (Path.GetFileName(path) != record.Request.Id + ".json") throw new InvalidDataException("Diario incorrecto.");
            return record;
        }).ToArray();
    }
    private static async Task Require(string path, ContentIdentity expected, CancellationToken token)
    {
        if (await ContentRegistry.IdentifyAsync(path, token).ConfigureAwait(false) != expected)
            throw new IOException("El contenido cambió; se conserva sin modificar: " + path);
    }
    private static async Task CopyVerified(string source, string target, ContentIdentity expected, CancellationToken token)
    {
        FolderPolicy.RejectLinks(source);
        FolderPolicy.RejectLinks(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, true))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1048576, true))
        {
            await input.CopyToAsync(output, token).ConfigureAwait(false);
            output.Flush(true);
        }
        await Require(target, expected, token).ConfigureAwait(false);
    }
    private static void Dates(ReplacementRecord r, string path)
    {
        File.SetCreationTimeUtc(path, r.CreatedUtc);
        File.SetLastWriteTimeUtc(path, r.ModifiedUtc);
    }
    private void Complete(ReplacementRecord record)
    {
        var r = record.Request;
        registry.Save(new(r.Source.Sha256, r.Source.Length, "original", r.ProfileKey, r.Output.Sha256, record.OriginalPath));
        registry.Save(new(r.Output.Sha256, r.Output.Length, "compressed", r.ProfileKey, r.Source.Sha256, r.SourcePath));
        record.CompletedUtc ??= DateTimeOffset.UtcNow;
        record.ExpiresUtc ??= record.CompletedUtc.Value.AddDays(r.RetentionDays);
        record.Phase = ReplacementPhase.Completed;
        Save(record);
    }
    public async Task<ReplacementResult> PublishAsync(ReplacementRequest request, CancellationToken token, Func<bool>? mayPublish = null)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var r = new ReplacementRecord { Request = request, OriginalPath = Original(request), StagedPath = Stage(request),
                ModifiedUtc = File.GetLastWriteTimeUtc(request.SourcePath), CreatedUtc = File.GetCreationTimeUtc(request.SourcePath) };
            Validate(r);
            if (!string.Equals(Path.GetExtension(request.SourcePath), Path.GetExtension(request.VerifiedOutputPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("La extensión debe conservarse para mantener la identidad en Jellyfin.");
            if (File.Exists(Journal(request.Id))) throw new InvalidOperationException("La transacción ya existe; recuperarla antes de continuar.");
            await Require(request.SourcePath, request.Source, token).ConfigureAwait(false);
            await Require(request.VerifiedOutputPath, request.Output, token).ConfigureAwait(false);
            Save(r);
            await CopyVerified(request.SourcePath, r.OriginalPath, request.Source, token).ConfigureAwait(false);
            r.Phase = ReplacementPhase.BackupVerified;
            Save(r);
            await CopyVerified(request.VerifiedOutputPath, r.StagedPath, request.Output, token).ConfigureAwait(false);
            Dates(r, r.StagedPath);
            await Require(request.SourcePath, request.Source, token).ConfigureAwait(false);
            if (mayPublish is not null && !mayPublish()) throw new InvalidOperationException("Reemplazo aplazado: reproducción activa o configuración modificada.");
            token.ThrowIfCancellationRequested();
            r.Phase = ReplacementPhase.Publishing;
            Save(r);
            // Same-volume atomic rename: the library path is never absent, even for a cross-volume backup.
            File.Move(r.StagedPath, request.SourcePath, true);
            Dates(r, request.SourcePath);
            Complete(r);
            return new(request.Id, request.SourcePath, r.OriginalPath);
        }
        finally { gate.Release(); }
    }
    public async Task RecoverAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var r in List())
            {
                var q = r.Request;
                if (r.Phase is ReplacementPhase.Prepared or ReplacementPhase.BackupVerified or ReplacementPhase.Publishing)
                {
                    var current = await ContentRegistry.IdentifyAsync(q.SourcePath, token).ConfigureAwait(false);
                    if (current == q.Output)
                    {
                        await Require(r.OriginalPath, q.Source, token).ConfigureAwait(false);
                        Dates(r, q.SourcePath);
                        Complete(r);
                    }
                    else if (current == q.Source)
                    {
                        // Roll back the intent only. A verified or partial backup is retained for inspection.
                        r.Phase = ReplacementPhase.Aborted;
                        Save(r);
                        if (File.Exists(r.StagedPath)) File.Delete(r.StagedPath);
                    }
                    else throw new IOException("Recuperación bloqueada: el archivo no coincide con el original ni el resultado.");
                }
                else if (r.Phase == ReplacementPhase.Restoring)
                {
                    var current = await ContentRegistry.IdentifyAsync(q.SourcePath, token).ConfigureAwait(false);
                    if (current == q.Source) { r.Phase = ReplacementPhase.Restored; Save(r); registry.UpdateLocation(q.Source.Sha256, q.SourcePath); }
                    else if (current == q.Output) { r.Phase = ReplacementPhase.Completed; Save(r); if (File.Exists(r.StagedPath)) File.Delete(r.StagedPath); }
                    else throw new IOException("Restauración interrumpida con contenido desconocido.");
                }
                else if (r.Phase == ReplacementPhase.Purging && !File.Exists(r.OriginalPath)) { r.Phase = ReplacementPhase.Purged; Save(r); }
            }
        }
        finally { gate.Release(); }
    }
    private async Task<bool> OutputExists(ReplacementRecord r, CancellationToken token)
    {
        var paths = new[] { r.Request.SourcePath, registry.Find(r.Request.Output.Sha256)?.LastKnownPath };
        foreach (var path in paths.Where(p => p is not null).Distinct())
        {
            if (File.Exists(path) && await ContentRegistry.IdentifyAsync(path!, token).ConfigureAwait(false) == r.Request.Output) return true;
        }
        return false;
    }
    public async Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var count = 0;
            foreach (var r in List().Where(r => r.Phase is ReplacementPhase.Completed or ReplacementPhase.Purging && r.ExpiresUtc <= now))
            {
                if (!File.Exists(r.OriginalPath) || !await OutputExists(r, token).ConfigureAwait(false)) continue;
                await Require(r.OriginalPath, r.Request.Source, token).ConfigureAwait(false);
                r.Phase = ReplacementPhase.Purging;
                Save(r);
                File.Delete(r.OriginalPath);
                r.Phase = ReplacementPhase.Purged;
                Save(r);
                count++;
            }
            return count;
        }
        finally { gate.Release(); }
    }
    public async Task RestoreAsync(string id, CancellationToken token, Func<bool>? mayPublish = null)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var r = List().Single(r => r.Request.Id == id);
            if (r.Phase != ReplacementPhase.Completed) throw new InvalidOperationException("El original no está disponible para restaurar.");
            var q = r.Request;
            await Require(r.OriginalPath, q.Source, token).ConfigureAwait(false);
            await Require(q.SourcePath, q.Output, token).ConfigureAwait(false);
            var compressedBackup = r.OriginalPath + ".compressed";
            if (!File.Exists(compressedBackup)) await CopyVerified(q.SourcePath, compressedBackup, q.Output, token).ConfigureAwait(false);
            else await Require(compressedBackup, q.Output, token).ConfigureAwait(false);
            r.Phase = ReplacementPhase.Restoring;
            Save(r);
            await CopyVerified(r.OriginalPath, r.StagedPath, q.Source, token).ConfigureAwait(false);
            Dates(r, r.StagedPath);
            await Require(q.SourcePath, q.Output, token).ConfigureAwait(false);
            if (mayPublish is not null && !mayPublish()) throw new InvalidOperationException("Hay una reproducción activa.");
            File.Move(r.StagedPath, q.SourcePath, true);
            Dates(r, q.SourcePath);
            registry.UpdateLocation(q.Source.Sha256, q.SourcePath);
            registry.UpdateLocation(q.Output.Sha256, compressedBackup);
            r.Phase = ReplacementPhase.Restored;
            Save(r);
        }
        finally { gate.Release(); }
    }
}
