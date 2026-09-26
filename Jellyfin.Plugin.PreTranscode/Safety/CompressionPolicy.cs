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
    string QuarantineRoot, int RetentionDays, double MinSavingsPercent, string ProfileKey, DateTime ItemDateCreated)
{
    public bool SingleMovieSelection { get; init; }
}

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
    public static string? EligibilityError(MediaProbeInfo info, EncodingProfile? profile = null, PluginConfiguration? config = null,
        bool automatic = false, bool manualSingle = true)
    {
        if (info.CompressorMarker) return "Ya comprimida (marca del contenedor).";
        if (info.HasHdr10Plus) return "HDR10+: se omite para no perder metadatos dinámicos.";
        if (info.VideoStreamCount != 1 || info.HasAttachedPicture || info.HasDataStream) return "Estructura de vídeo no admitida en v1.";
        if (info.Width <= 0 || info.Height <= 0 || !double.IsFinite(info.DurationSeconds) || info.DurationSeconds <= 0) return "Información de vídeo incompleta.";
        if (info.PixelFormat is not ("yuv420p" or "yuv420p10le")) return "Formato de píxel no admitido en v1.";
        if (!info.IsHdr && (info.MasteringDisplayMetadata.Length > 0 || info.ContentLightMetadata.Length > 0))
            return "Metadatos HDR sin señal de color HDR verificable.";
        if (info.IsHdr || info.IsDolbyVision)
        {
            if (automatic || !manualSingle) return "HDR / Dolby Vision: elige una sola película para la prueba experimental.";
            if (profile?.VideoEncoder != "libx265") return "HDR / Dolby Vision: se requiere libx265.";
            if (info.BitDepth != 10 || info.ColorPrimaries != "bt2020" || info.ColorTransfer != "smpte2084"
                || info.ColorSpace != "bt2020nc") return "HDR: señal de color no compatible o incompleta.";
            if (info.IsDolbyVision)
            {
                if (config?.EnableExperimentalDolbyVision != true) return "Dolby Vision experimental desactivado.";
                if (info.DolbyVisionProfile != 8 || info.DolbyVisionCompatibilityId != 1 || !info.DolbyVisionHasRpu
                    || !info.DolbyVisionHasBaseLayer || info.DolbyVisionHasEnhancementLayer)
                    return "Solo se admite Dolby Vision 8.1 con base HDR10 y RPU, sin capa de mejora.";
                if (profile is not null && TargetDimensions(profile, info) != (info.Width, info.Height))
                    return "Dolby Vision experimental: conserva la resolución original hasta validar la reducción con RPU.";
            }
            else if (config?.EnableExperimentalHdr != true) return "HDR10 experimental desactivado.";
        }
        return null;
    }
    public static string? VerificationError(MediaProbeInfo source, MediaProbeInfo output, EncodingProfile? profile = null)
    {
        if (!output.CompressorMarker || output.VideoCodec != "hevc") return "Falta el vídeo HEVC o la marca de compresión.";
        if (source.IsHdr && !output.IsHdr) return "La salida ha perdido HDR.";
        if (source.IsHdr && (source.ColorPrimaries != output.ColorPrimaries || source.ColorTransfer != output.ColorTransfer
            || source.ColorSpace != output.ColorSpace || (source.ColorRange.Length > 0 && source.ColorRange != output.ColorRange)
            || (source.MasteringDisplayMetadata.Length > 0 && source.MasteringDisplayMetadata != output.MasteringDisplayMetadata)
            || (source.ContentLightMetadata.Length > 0 && source.ContentLightMetadata != output.ContentLightMetadata)))
            return "La salida ha cambiado los metadatos HDR.";
        if (source.IsDolbyVision && (!output.IsDolbyVision || source.DolbyVisionProfile != output.DolbyVisionProfile
            || source.DolbyVisionCompatibilityId != output.DolbyVisionCompatibilityId || !output.DolbyVisionHasRpu
            || source.DolbyVisionHasBaseLayer != output.DolbyVisionHasBaseLayer
            || source.DolbyVisionHasEnhancementLayer != output.DolbyVisionHasEnhancementLayer))
            return "La salida ha perdido Dolby Vision o su RPU.";
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
        if (source.HasHdr10Plus) throw new InvalidOperationException("HDR10+ no se conserva en esta ruta de codificación.");
        if ((source.IsHdr || source.IsDolbyVision) && profile.VideoEncoder != "libx265")
            throw new InvalidOperationException("HDR / Dolby Vision requiere libx265.");
        if (source.IsDolbyVision && TargetDimensions(profile, source) != (source.Width, source.Height))
            throw new InvalidOperationException("Dolby Vision experimental requiere conservar la resolución original.");
        var args = new List<string> { "-nostdin", "-y", "-hide_banner", "-protocol_whitelist", "file", "-i", input,
            "-map", "0:V:0", "-map", "0:a?", "-map", "0:s?", "-map", "0:t?", "-map_metadata", "0", "-map_chapters", "0", "-c", "copy", "-c:v:0", profile.VideoEncoder,
            "-pix_fmt", source.PixelFormat, "-metadata", "JELLYFIN_COMPRESSOR=v1" };
        if (source.IsHdr)
        {
            args.AddRange(new[] { "-color_primaries", source.ColorPrimaries, "-color_trc", source.ColorTransfer,
                "-colorspace", source.ColorSpace });
            if (source.ColorRange.Length > 0) args.AddRange(new[] { "-color_range", source.ColorRange });
        }
        if (source.IsDolbyVision) args.AddRange(new[] { "-dolbyvision", "1" });
        var quality = profile.Crf.ToString(CultureInfo.InvariantCulture);
        switch (profile.VideoEncoder)
        {
            case "libx265":
                var x265Parameters = "pools=2:frame-threads=2" + (source.IsHdr ? ":hdr-opt=1" : "");
                if (source.IsDolbyVision)
                {
                    // x265 refuses Dolby Vision without VBV/HRD. Keep the ceiling well above normal CRF output.
                    var ceiling = (long)source.Width * source.Height > 1920L * 1080 ? "100000" : "40000";
                    x265Parameters += ":vbv-maxrate=" + ceiling + ":vbv-bufsize=" + ceiling;
                }
                args.AddRange(new[] { "-crf", quality, "-preset", "medium", "-x265-params", x265Parameters });
                break;
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
