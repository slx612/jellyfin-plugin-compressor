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

    // Count named showinfo filters without checksum calculation. Empty metadata branches end
    // at nullsink, so FFmpeg never tries to initialize an encoder for an absent HDR type.
    internal static async Task<DynamicHdrFacts> ScanAsync(string ffmpegPath, string sourcePath, string temporaryDirectory,
        double durationSeconds, CancellationToken token, Action<Process>? onProcessStarted = null, Action<double>? progress = null)
    {
        var names = new[] { "hdrplus", "rpu", "mastering", "light", "total" };
        var counts = new long[5];
        var args = new[] { "-xerror", "-hide_banner", "-loglevel", "info", "-protocol_whitelist", "file", "-i", sourcePath,
            "-filter_complex", "[0:v:0]split=6[h][d][m][c][all][output];"
                + "[h]sidedata=mode=select:type=DYNAMIC_HDR_PLUS,showinfo@hdrplus=checksum=0,nullsink;"
                + "[d]sidedata=mode=select:type=DOVI_RPU_BUFFER,showinfo@rpu=checksum=0,nullsink;"
                + "[m]sidedata=mode=select:type=MASTERING_DISPLAY_METADATA,showinfo@mastering=checksum=0,nullsink;"
                + "[c]sidedata=mode=select:type=CONTENT_LIGHT_LEVEL,showinfo@light=checksum=0,nullsink;"
                + "[all]showinfo@total=checksum=0,nullsink",
            "-map", "[output]", "-c:v", "wrapped_avframe", "-fps_mode", "passthrough", "-f", "null", "-" };
        var (exit, error) = await FfmpegExecutor.RunAsync(ffmpegPath, args, durationSeconds, progress,
            token, onProcessStarted, onErrorLine: line =>
            {
                if (!line.Contains(" n:", StringComparison.Ordinal)) return;
                for (var i = 0; i < names.Length; i++)
                    if (line.StartsWith("[showinfo@" + names[i] + " @", StringComparison.Ordinal)) counts[i]++;
            }).ConfigureAwait(false);
        if (exit != 0 || counts[4] == 0)
            throw new IOException("No se pudieron comprobar los metadatos HDR de todos los fotogramas (FFmpeg " + exit + "). " + error);
        return new DynamicHdrFacts(counts[0], counts[1], counts[2], counts[3], counts[4]);
    }
}
