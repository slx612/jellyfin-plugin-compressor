using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Jellyfin.Plugin.PreTranscode.Media;

internal static class HdrMetadataShape
{
    // Only spatially invariant metadata has been validated for the GPU resize route.
    internal static void ValidateDolby(string path, long frames, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (new FileInfo(path).Length > 2 * 1048576) throw new IOException("Dolby: metadatos de áreas activas demasiado grandes.");
        try
        {
            using var file = File.OpenRead(path);
            using var doc = JsonDocument.Parse(file);
            var area = doc.RootElement.GetProperty("active_area");
            var ids = new HashSet<int>();
            foreach (var preset in area.GetProperty("presets").EnumerateArray())
            {
                if (!ids.Add(preset.GetProperty("id").GetInt32())) throw new IOException("Dolby: área activa duplicada.");
                foreach (var side in new[] { "left", "right", "top", "bottom" })
                    if (preset.GetProperty(side).GetInt32() != 0)
                        throw new IOException("Dolby: áreas activas con recortes; esta ruta GPU aún no las admite.");
            }
            var ranges = area.GetProperty("edits").EnumerateObject().Select(edit =>
            {
                if (!ids.Contains(edit.Value.GetInt32())) throw new IOException("Dolby: área activa desconocida.");
                var parts = edit.Name.Split('-');
                if (parts.Length is < 1 or > 2) throw new IOException("Dolby: intervalo de fotogramas desconocido.");
                return (Start: long.Parse(parts[0], CultureInfo.InvariantCulture), End: long.Parse(parts[^1], CultureInfo.InvariantCulture));
            }).OrderBy(range => range.Start);
            long next = 0;
            foreach (var range in ranges)
            {
                token.ThrowIfCancellationRequested();
                if (range.Start != next || range.End < range.Start) throw new IOException("Dolby: cobertura de fotogramas incompleta.");
                next = checked(range.End + 1);
            }
            if (frames <= 0 || next != frames) throw new IOException("Dolby: las áreas activas no cubren todos los fotogramas.");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new IOException("Dolby: estructura de metadatos no compatible.", error); }
    }

    internal static void ValidateHdr10Plus(string path, long frames, CancellationToken token)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[1048576]; var retained = 0; var state = new JsonReaderState();
            long scenes = 0, validated = 0; var profiles = 0; var arrays = 0; var windows = 0;
            var inScenes = false; string? property = null;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, retained, buffer.Length - retained);
                var filled = retained + read;
                var reader = new Utf8JsonReader(buffer.AsSpan(0, filled), read == 0, state);
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName) { property = reader.GetString(); continue; }
                    if (property == "HDR10plusProfile" && reader.CurrentDepth == 2)
                    {
                        if (reader.TokenType != JsonTokenType.String || reader.GetString() != "B" || ++profiles != 1)
                            throw new IOException("HDR10+: esta ruta GPU solo admite el perfil B.");
                    }
                    if (property == "SceneInfo" && reader.CurrentDepth == 1)
                    {
                        if (reader.TokenType != JsonTokenType.StartArray || ++arrays != 1) throw new IOException("HDR10+: secuencia de fotogramas no compatible.");
                        inScenes = true;
                    }
                    if (inScenes && reader.TokenType == JsonTokenType.StartObject && reader.CurrentDepth == 2) { scenes++; windows = 0; }
                    if (inScenes && property == "NumberOfWindows" && reader.CurrentDepth == 3)
                    {
                        if (reader.TokenType != JsonTokenType.Number || reader.GetInt32() != 1 || ++windows != 1)
                            throw new IOException("HDR10+: ventanas espaciales no compatibles con esta ruta GPU.");
                    }
                    if (inScenes && reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 2)
                    { if (windows != 1) throw new IOException("HDR10+: fotograma sin ventana verificable."); validated++; }
                    if (inScenes && reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == 1) inScenes = false;
                    property = null;
                }
                var consumed = (int)reader.BytesConsumed; state = reader.CurrentState;
                retained = filled - consumed; buffer.AsSpan(consumed, retained).CopyTo(buffer);
                if (read == 0) break;
                if (retained == buffer.Length) throw new IOException("HDR10+: un campo JSON supera el límite de memoria.");
            }
            if (retained != 0 || profiles != 1 || arrays != 1 || scenes <= 0 || scenes != validated || scenes != frames)
                throw new IOException("HDR10+: los metadatos no cubren todos los fotogramas.");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw new IOException("HDR10+: estructura de metadatos no compatible.", error); }
    }
}
