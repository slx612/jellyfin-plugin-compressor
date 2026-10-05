using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Security.Cryptography;
using System.Buffers.Binary;

// Bounded NAL buffers; never reads an entire movie into memory.
namespace Jellyfin.Plugin.PreTranscode.Media;
internal static class HevcStaticMetadata
{
    private static readonly byte[] Marker = { 0, 0, 1 };
    private const int MaximumNalBytes = 32 * 1024 * 1024;

    internal static Dictionary<int, byte[]> Inspect(string path, CancellationToken token, Action<double>? progress = null)
    {
        var values = new Dictionary<int, byte[]>();
        Walk(path, (prefix, nal) =>
        {
            if (((nal[0] >> 1) & 63) is not (39 or 40)) return;
            foreach (var (kind, payload) in Messages(nal))
            {
                if (kind is not (137 or 144)) continue;
                if (payload.Length != (kind == 137 ? 24 : 4)) throw new IOException("Invalid static HDR SEI length.");
                if (values.TryGetValue(kind, out var previous) && !previous.AsSpan().SequenceEqual(payload))
                    throw new IOException("HDR: los metadatos estáticos varían en la película; no se admite su compresión GPU.");
                values[kind] = payload;
            }
        }, token, progress);
        if (!values.ContainsKey(137) || !values.ContainsKey(144)) throw new IOException("HDR: faltan metadatos estáticos completos para la ruta GPU.");
        return values;
    }

    internal static void Restore(string input, string output, Dictionary<int, byte[]> values, CancellationToken token, Action<double>? progress = null)
    {
        using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1048576);
        using var before = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var after = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var counts = new Dictionary<int, long> { [137] = 0, [144] = 0 };
        Walk(input, (prefix, nal) =>
        {
            var patched = nal;
            if (((nal[0] >> 1) & 63) is 39 or 40)
            {
                var original = Messages(nal);
                var modified = original.Select(item =>
                {
                    if (item.Kind is not (137 or 144)) return item;
                    if (item.Payload.Length != (item.Kind == 137 ? 24 : 4)) throw new IOException("Encoded static HDR SEI length invalid.");
                    counts[item.Kind]++;
                    return (item.Kind, values[item.Kind]);
                }).ToList();
                if (original.Any(item => item.Kind is 137 or 144)) patched = Sei(nal[..2], modified);
                AddOther(before, original);
                AddOther(after, Messages(patched));
            }
            else
            {
                before.AppendData(nal);
                after.AppendData(patched);
            }
            destination.Write(prefix);
            destination.Write(patched);
        }, token, progress);
        var originalHash = before.GetHashAndReset();
        if (!originalHash.AsSpan().SequenceEqual(after.GetHashAndReset()) || counts.Values.Any(count => count == 0))
            throw new IOException("HDR repair changed unrelated video/Dolby/HDR10+ payloads or found no static messages.");

    }

    private static void AddOther(IncrementalHash digest, List<(int Kind, byte[] Payload)> messages)
    {
        Span<byte> header = stackalloc byte[8];
        foreach (var (kind, payload) in messages.Where(item => item.Kind is not (137 or 144)))
        {
            BinaryPrimitives.WriteInt32BigEndian(header, kind);
            BinaryPrimitives.WriteInt32BigEndian(header[4..], payload.Length);
            digest.AppendData(header);
            digest.AppendData(payload);
        }
    }

    private static void Walk(string path, Action<byte[], byte[]> visit, CancellationToken token, Action<double>? progress, int blockSize = 1048576)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, blockSize, FileOptions.SequentialScan);
        var buffer = new byte[blockSize + 3];
        using var nal = new MemoryStream();
        byte[]? prefix = null;
        var retained = 0;
        void Append(ReadOnlySpan<byte> bytes)
        {
            if (nal.Length + bytes.Length > MaximumNalBytes) throw new IOException("HEVC: unidad NAL demasiado grande para procesarla con seguridad.");
            nal.Write(bytes);
        }
        void Finish()
        {
            if (prefix is null)
            {
                if (nal.Length != 0) throw new IOException("Unexpected bytes before Annex B HEVC start code.");
                return;
            }
            var body = nal.ToArray();
            if (body.Length < 2 || (body[0] & 128) != 0 || (body[1] & 7) == 0)
                throw new IOException("Invalid HEVC NAL header.");
            visit(prefix, body);
            nal.SetLength(0);
        }
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, retained, blockSize);
            if (read == 0)
            {
                Append(buffer.AsSpan(0, retained));
                Finish();
                if (prefix is null) throw new IOException("Empty HEVC stream.");
                break;
            }
            var count = retained + read;
            var cursor = 0;
            while (true)
            {
                var index = buffer.AsSpan(cursor, count - cursor).IndexOf(Marker);
                if (index < 0) break;
                var marker = cursor + index;
                var start = marker > cursor && buffer[marker - 1] == 0 ? marker - 1 : marker;
                Append(buffer.AsSpan(cursor, start - cursor));
                Finish();
                prefix = buffer.AsSpan(start, marker + 3 - start).ToArray();
                cursor = marker + 3;
            }
            retained = Math.Min(3, count - cursor);
            Append(buffer.AsSpan(cursor, count - cursor - retained));
            buffer.AsSpan(count - retained, retained).CopyTo(buffer);
            progress?.Invoke(100.0 * stream.Position / stream.Length);
        }
    }

    private static List<(int Kind, byte[] Payload)> Messages(byte[] nal)
    {
        using var decoded = new MemoryStream();
        var zeros = 0;
        for (var i = 2; i < nal.Length; i++)
        {
            var value = nal[i];
            if (zeros >= 2 && value == 3)
            {
                if (i + 1 == nal.Length || nal[i + 1] > 3) throw new IOException("Invalid HEVC emulation prevention byte.");
                zeros = 0;
                continue;
            }
            decoded.WriteByte(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }
        var data = decoded.ToArray();
        var length = data.Length;
        while (length > 0 && data[length - 1] == 0) length--;
        if (length == 0 || data[length - 1] != 128) throw new IOException("Invalid SEI trailing bits.");
        var offset = 0;
        int Number()
        {
            var result = 0;
            while (offset < length - 1 && data[offset] == 255) { result = checked(result + 255); offset++; }
            if (offset >= length - 1) throw new IOException("Truncated SEI field.");
            return checked(result + data[offset++]);
        }
        var items = new List<(int, byte[])>();
        while (offset < length - 1)
        {
            var kind = Number();
            var size = Number();
            if (size > length - 1 - offset) throw new IOException("Truncated SEI payload.");
            items.Add((kind, data.AsSpan(offset, size).ToArray()));
            offset += size;
        }
        return items;
    }

    private static byte[] Sei(byte[] header, List<(int Kind, byte[] Payload)> items)
    {
        using var rbsp = new MemoryStream();
        void Number(int value) { while (value >= 255) { rbsp.WriteByte(255); value -= 255; } rbsp.WriteByte((byte)value); }
        foreach (var (kind, payload) in items) { Number(kind); Number(payload.Length); rbsp.Write(payload); }
        rbsp.WriteByte(128);
        using var encoded = new MemoryStream();
        encoded.Write(header);
        var zeros = 0;
        foreach (var value in rbsp.ToArray())
        {
            if (zeros >= 2 && value <= 3) { encoded.WriteByte(3); zeros = 0; }
            encoded.WriteByte(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }
        return encoded.ToArray();
    }

}
