using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Jobs;
using Jellyfin.Plugin.PreTranscode.Media;

namespace Jellyfin.Plugin.PreTranscode.Safety;

public sealed record CompressionSnapshot(EncodingProfile Profile, ContentIdentity Source, string LibraryRoot,
    string QuarantineRoot, int RetentionDays, double MinSavingsPercent, string ProfileKey, DateTime ItemDateCreated);

public static class CompressionPolicy
{
    public static readonly string[] Encoders = { "libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf" };
    public static bool CanRun(TranscodeJob job, PluginConfiguration config) => config.Enabled && (!job.Automatic || config.AutomaticCompressionEnabled);
    public static bool MeetsMinimumMovieSize(long bytes, int minimumGb) => minimumGb == 0 || bytes > minimumGb * 1073741824L;
    public static EncodingProfile EffectiveProfile(EncodingProfile profile, string path)
    {
        var container = Path.GetExtension(path).ToLowerInvariant() switch
        { ".mkv" => "matroska", ".mp4" or ".m4v" => "mp4", _ => throw new InvalidOperationException("Contenedor no admitido; se mantiene el archivo original.") };
        if (!Encoders.Contains(profile.VideoEncoder) || profile.Crf is < 16 or > 32) throw new InvalidOperationException("Codificador o calidad no admitidos.");
        var width = profile.ResolutionMode switch
        {
            ResolutionMode.Unchanged => 0,
            ResolutionMode.CapHeight => profile.MaxHeight switch
            {
                2160 => 3840,
                1080 => 1920,
                720 => 1280,
                480 => 854,
                _ => throw new InvalidOperationException("Elige una resolución máxima admitida.")
            },
            _ => throw new InvalidOperationException("Modo de resolución no admitido en v1.")
        };
        return new EncodingProfile { Id = profile.Id, Name = profile.Name, VideoCodec = "hevc", VideoEncoder = profile.VideoEncoder,
            Crf = profile.Crf, Preset = "medium", AudioCodec = "copy", Container = container, OutputMode = OutputHandlingMode.ReplaceInPlace,
            ResolutionMode = profile.ResolutionMode, MaxWidth = width, MaxHeight = profile.ResolutionMode == ResolutionMode.CapHeight ? profile.MaxHeight : 0,
            PixelFormatMode = PixelFormatMode.KeepSourceBitDepth };
    }
    public static string Key(EncodingProfile profile) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile)))).ToLowerInvariant();
    public static string? EligibilityError(MediaProbeInfo info)
    {
        if (info.CompressorMarker) return "Ya comprimida (marca del contenedor).";
        if (info.IsHdr || info.IsDolbyVision) return "HDR / Dolby Vision: omitida en v1.";
        if (info.VideoStreamCount != 1 || info.HasAttachedPicture || info.HasDataStream) return "Estructura de vídeo no admitida en v1.";
        if (info.Width <= 0 || info.Height <= 0 || !double.IsFinite(info.DurationSeconds) || info.DurationSeconds <= 0) return "Información de vídeo incompleta.";
        if (info.PixelFormat is not ("yuv420p" or "yuv420p10le")) return "Formato de píxel no admitido en v1.";
        return null;
    }
    public static string? VerificationError(MediaProbeInfo source, MediaProbeInfo output, EncodingProfile? profile = null)
    {
        if (!output.CompressorMarker || output.VideoCodec != "hevc") return "Falta el vídeo HEVC o la marca de compresión.";
        var target = TargetDimensions(profile, source);
        if (source.BitDepth != output.BitDepth || output.Width > source.Width || output.Height > source.Height
            || (profile?.ResolutionMode == ResolutionMode.CapHeight && (output.Width > profile.MaxWidth || output.Height > profile.MaxHeight))
            || Math.Abs(target.Width - output.Width) > 2 || Math.Abs(target.Height - output.Height) > 2)
            return "La resolución o profundidad de color no coincide con el límite configurado.";
        if (!double.IsFinite(output.DurationSeconds) || output.DurationSeconds <= 0 || Math.Abs(source.DurationSeconds - output.DurationSeconds) > Math.Max(0.5, Math.Min(2, source.DurationSeconds * 0.001))) return "Duración diferente.";
        if (!source.PreservedStreams.SequenceEqual(output.PreservedStreams)) return "Diferencia en pistas, idiomas, canales, adjuntos o disposiciones.";
        if (source.Chapters.Count != output.Chapters.Count || source.Chapters.Where((c, i) =>
            Math.Abs(c.Start - output.Chapters[i].Start) > .05 || Math.Abs(c.End - output.Chapters[i].End) > .05 || c.Title != output.Chapters[i].Title).Any()) return "Capítulos diferentes.";
        return null;
    }
    private static (int Width, int Height) TargetDimensions(EncodingProfile? profile, MediaProbeInfo source)
    {
        if (profile?.ResolutionMode != ResolutionMode.CapHeight || (source.Width <= profile.MaxWidth && source.Height <= profile.MaxHeight))
            return (source.Width, source.Height);
        var ratio = Math.Min((double)profile.MaxWidth / source.Width, (double)profile.MaxHeight / source.Height);
        return (Math.Max(2, (int)Math.Round(source.Width * ratio / 2, MidpointRounding.AwayFromZero) * 2),
            Math.Max(2, (int)Math.Round(source.Height * ratio / 2, MidpointRounding.AwayFromZero) * 2));
    }
    public static IReadOnlyList<string> BuildArguments(EncodingProfile profile, MediaProbeInfo source, string input, string output)
    {
        var args = new List<string> { "-nostdin", "-y", "-hide_banner", "-protocol_whitelist", "file", "-i", input,
            "-map", "0:V:0", "-map", "0:a?", "-map", "0:s?", "-map", "0:t?", "-map_metadata", "0", "-map_chapters", "0", "-c", "copy", "-c:v:0", profile.VideoEncoder,
            "-pix_fmt", source.PixelFormat, "-metadata", "JELLYFIN_COMPRESSOR=v1" };
        var quality = profile.Crf.ToString(CultureInfo.InvariantCulture);
        switch (profile.VideoEncoder)
        {
            case "libx265": args.AddRange(new[] { "-crf", quality, "-preset", "medium", "-x265-params", "pools=2:frame-threads=2" }); break;
            case "hevc_nvenc": args.AddRange(new[] { "-preset", "p4", "-rc", "vbr", "-cq", quality, "-b:v", "0" }); break;
            case "hevc_qsv": args.AddRange(new[] { "-global_quality", quality }); break;
            case "hevc_amf": args.AddRange(new[] { "-rc", "cqp", "-qp_i", quality, "-qp_p", quality }); break;
            default: throw new InvalidOperationException("Codificador no permitido.");
        }
        if (profile.ResolutionMode == ResolutionMode.CapHeight && (source.Width > profile.MaxWidth || source.Height > profile.MaxHeight))
            args.AddRange(new[] { "-vf", "scale=w=" + profile.MaxWidth.ToString(CultureInfo.InvariantCulture)
                + ":h=" + profile.MaxHeight.ToString(CultureInfo.InvariantCulture)
                + ":force_original_aspect_ratio=decrease:force_divisible_by=2" });
        // Explicit dispositions prevent FFmpeg from making the first unmarked track default.
        for (var i = 0; i < source.Dispositions.Count; i++) args.AddRange(new[] { "-disposition:" + i.ToString(CultureInfo.InvariantCulture), source.Dispositions[i] });
        if (profile.Container == "mp4") args.AddRange(new[] { "-tag:v", "hvc1", "-movflags", "+faststart+use_metadata_tags" });
        args.AddRange(new[] { "-f", profile.Container, output });
        return args;
    }
}
