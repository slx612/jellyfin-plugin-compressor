using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.PreTranscode.Safety;

public sealed record ContentIdentity(string Sha256, long Length);
public sealed record ContentRecord(string Sha256, long Length, string Kind, string ProfileKey, string? RelatedSha256, string LastKnownPath);

internal static class AtomicFile
{
    public static void WriteJson<T>(string path, T value)
    {
        FolderPolicy.RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value);
            stream.Flush(true);
        }
        File.Move(temp, path, true);
    }
}

public sealed class ContentRegistry
{
    private readonly string directory;
    private readonly object sync = new();
    public ContentRegistry(string directory) { this.directory = directory; }

    private string PathFor(string hash)
    {
        if (hash.Length != 64 || !System.Linq.Enumerable.All(hash, Uri.IsHexDigit)) throw new InvalidOperationException("Identidad inválida.");
        return Path.Combine(directory, hash.ToLowerInvariant() + ".json");
    }

    public ContentRecord? Find(string hash)
    {
        lock (sync)
        {
            var path = PathFor(hash);
            FolderPolicy.RejectLinks(path);
            if (!File.Exists(path)) return null;
            var record = JsonSerializer.Deserialize<ContentRecord>(File.ReadAllText(path)) ?? throw new InvalidDataException("Registro vacío.");
            if (!string.Equals(record.Sha256, hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Identidad de registro incorrecta.");
            return record;
        }
    }

    public void Save(ContentRecord record) { lock (sync) AtomicFile.WriteJson(PathFor(record.Sha256), record); }
    public void UpdateLocation(string hash, string path)
    {
        lock (sync)
        {
            var existing = Find(hash) ?? throw new InvalidOperationException("Identidad desconocida.");
            Save(existing with { LastKnownPath = path });
        }
    }

    public static async Task<ContentIdentity> IdentifyAsync(string path, CancellationToken token)
    {
        FolderPolicy.RejectLinks(path);
        var before = new FileInfo(path);
        var length = before.Length;
        var modified = before.LastWriteTimeUtc;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        var after = new FileInfo(path);
        if (length != after.Length || modified != after.LastWriteTimeUtc) throw new IOException("El archivo cambió durante su lectura.");
        return new(Convert.ToHexString(hash).ToLowerInvariant(), length);
    }
}
