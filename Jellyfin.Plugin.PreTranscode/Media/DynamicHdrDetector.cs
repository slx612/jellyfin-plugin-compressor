using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Encoding;

namespace Jellyfin.Plugin.PreTranscode.Media;

internal sealed record DynamicHdrFacts(long Hdr10PlusFrames, long DolbyVisionRpuFrames,
    long MasteringDisplayFrames, long ContentLightFrames, long TotalFrames = 0)
{
    internal bool HasHdr10Plus => Hdr10PlusFrames > 0;
    internal bool HasDolbyVisionRpu => DolbyVisionRpuFrames > 0;
    internal bool HasMasteringDisplay => MasteringDisplayFrames > 0;
    internal bool HasContentLight => ContentLightFrames > 0;
}

internal static class DynamicHdrDetector
{
    internal static string? ApplyFacts(MediaProbeInfo info, DynamicHdrFacts facts)
    {
        info.HasHdr10Plus |= facts.HasHdr10Plus;
        if (facts.HasDolbyVisionRpu)
        {
            info.IsDolbyVision = true;
            info.DolbyVisionHasRpu = true;
        }
        if (info.IsDolbyVision && !facts.HasDolbyVisionRpu)
            return "Dolby Vision declarado sin RPU verificable en los fotogramas.";
        if ((facts.HasMasteringDisplay || facts.HasContentLight) && !info.IsHdr)
            return "Metadatos HDR sin señal de color HDR verificable.";
        if ((facts.HasMasteringDisplay && info.MasteringDisplayMetadata.Length == 0)
            || (facts.HasContentLight && info.ContentLightMetadata.Length == 0))
            return "Metadatos HDR presentes solo en fotogramas; no se pueden verificar tras codificar.";
        return null;
    }

    internal static long CountFrames(IEnumerable<string> frameLogLines) => frameLogLines.LongCount(line =>
    {
        var value = line.TrimStart();
        return value.Length > 0 && value[0] != '#';
    });

    internal static string? PreservationError(DynamicHdrFacts source, DynamicHdrFacts output)
    {
        if ((source.TotalFrames > 0 && source.TotalFrames != output.TotalFrames)
            || (source.Hdr10PlusFrames != output.Hdr10PlusFrames)
            || (source.DolbyVisionRpuFrames > 0 && source.DolbyVisionRpuFrames != output.DolbyVisionRpuFrames)
            || (source.MasteringDisplayFrames > 0 && source.MasteringDisplayFrames != output.MasteringDisplayFrames)
            || (source.ContentLightFrames > 0 && source.ContentLightFrames != output.ContentLightFrames))
            return "La salida ha perdido metadatos HDR o Dolby Vision de sus fotogramas.";
        return null;
    }

    // Stream metadata and Jellyfin's catalog can miss side data carried only on decoded frames.
    // Each filter writes one small framecrc row per matching frame. All frames are decoded, so a
    // missing type cannot be inferred from a partial sample. -xerror prevents decode errors from
    // turning a partial scan into a false negative.
    internal static async Task<DynamicHdrFacts> ScanAsync(string ffmpegPath, string sourcePath, string temporaryDirectory,
        double durationSeconds, CancellationToken token, Action<Process>? onProcessStarted = null)
    {
        Directory.CreateDirectory(temporaryDirectory);
        var prefix = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N"));
        var paths = new[] { prefix + "-hdr10plus.framecrc", prefix + "-rpu.framecrc",
            prefix + "-mastering.framecrc", prefix + "-light.framecrc", prefix + "-all.framecrc" };
        try
        {
            var args = new[] { "-xerror", "-hide_banner", "-loglevel", "error", "-protocol_whitelist", "file", "-i", sourcePath,
                "-filter_complex", "[0:v:0]split=4[h][d][m][c];"
                    + "[h]sidedata=mode=select:type=DYNAMIC_HDR_PLUS[ho];"
                    + "[d]sidedata=mode=select:type=DOVI_RPU_BUFFER[do];"
                    + "[m]sidedata=mode=select:type=MASTERING_DISPLAY_METADATA[mo];"
                    + "[c]sidedata=mode=select:type=CONTENT_LIGHT_LEVEL[co]",
                "-map", "[ho]", "-c:v", "wrapped_avframe", "-f", "framecrc", paths[0],
                "-map", "[do]", "-c:v", "wrapped_avframe", "-f", "framecrc", paths[1],
                "-map", "[mo]", "-c:v", "wrapped_avframe", "-f", "framecrc", paths[2],
                "-map", "[co]", "-c:v", "wrapped_avframe", "-f", "framecrc", paths[3] };
            var (exitCode, error) = await FfmpegExecutor.RunAsync(ffmpegPath, args, durationSeconds, null,
                token, onProcessStarted).ConfigureAwait(false);
            if (exitCode != 0 || paths.Take(4).Any(path => !File.Exists(path)))
                throw new IOException("No se pudieron comprobar los metadatos HDR de todos los fotogramas (FFmpeg "
                    + exitCode + ", salidas " + string.Join(',', paths.Take(4).Select(File.Exists)) + "). " + error);
            var matches = paths.Take(4).Select(path => CountFrames(File.ReadLines(path))).ToArray();
            long total = 0;
            if (matches[0] > 0)
            {
                var countArgs = new[] { "-xerror", "-hide_banner", "-loglevel", "error", "-protocol_whitelist", "file",
                    "-i", sourcePath, "-map", "0:v:0", "-c:v", "wrapped_avframe", "-f", "framecrc", paths[4] };
                var (countExit, countError) = await FfmpegExecutor.RunAsync(ffmpegPath, countArgs, durationSeconds, null,
                    token, onProcessStarted).ConfigureAwait(false);
                if (countExit != 0 || !File.Exists(paths[4]))
                    throw new IOException("No se pudieron contar los fotogramas HDR10+: " + countError);
                total = CountFrames(File.ReadLines(paths[4]));
            }
            return new DynamicHdrFacts(matches[0], matches[1], matches[2], matches[3], total);
        }
        finally
        {
            foreach (var path in paths) if (File.Exists(path)) File.Delete(path);
        }
    }
}
