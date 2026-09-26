using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Encoding;

namespace Jellyfin.Plugin.PreTranscode.Media;

internal sealed record DynamicHdrFacts(bool HasHdr10Plus, bool HasDolbyVisionRpu,
    bool HasMasteringDisplay, bool HasContentLight);

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
        if ((facts.HasMasteringDisplay && info.MasteringDisplayMetadata.Length == 0)
            || (facts.HasContentLight && info.ContentLightMetadata.Length == 0))
            return "Metadatos HDR presentes solo en fotogramas; no se pueden verificar tras codificar.";
        return null;
    }

    internal static bool ContainsFrame(IEnumerable<string> frameHashLines) => frameHashLines.Any(line =>
    {
        var value = line.TrimStart();
        return value.Length > 0 && value[0] != '#';
    });

    // Stream metadata and Jellyfin's catalog can miss side data carried only on decoded frames.
    // Each filter writes one framehash row on its first match. A missing type is checked to EOF;
    // -xerror ensures a decode error cannot turn a partial scan into a false negative.
    internal static async Task<DynamicHdrFacts> ScanAsync(string ffmpegPath, string sourcePath, string temporaryDirectory,
        double durationSeconds, CancellationToken token, Action<Process>? onProcessStarted = null)
    {
        Directory.CreateDirectory(temporaryDirectory);
        var prefix = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N"));
        var paths = new[] { prefix + "-hdr10plus.framehash", prefix + "-rpu.framehash",
            prefix + "-mastering.framehash", prefix + "-light.framehash" };
        try
        {
            var args = new[] { "-xerror", "-hide_banner", "-loglevel", "error", "-protocol_whitelist", "file", "-i", sourcePath,
                "-filter_complex", "[0:v:0]split=4[h][d][m][c];"
                    + "[h]sidedata=mode=select:type=DYNAMIC_HDR_PLUS[ho];"
                    + "[d]sidedata=mode=select:type=DOVI_RPU_BUFFER[do];"
                    + "[m]sidedata=mode=select:type=MASTERING_DISPLAY_METADATA[mo];"
                    + "[c]sidedata=mode=select:type=CONTENT_LIGHT_LEVEL[co]",
                "-map", "[ho]", "-frames:v", "1", "-f", "framehash", paths[0],
                "-map", "[do]", "-frames:v", "1", "-f", "framehash", paths[1],
                "-map", "[mo]", "-frames:v", "1", "-f", "framehash", paths[2],
                "-map", "[co]", "-frames:v", "1", "-f", "framehash", paths[3] };
            var (exitCode, error) = await FfmpegExecutor.RunAsync(ffmpegPath, args, durationSeconds, null,
                token, onProcessStarted).ConfigureAwait(false);
            if (exitCode != 0 || paths.Any(path => !File.Exists(path)))
                throw new IOException("No se pudieron comprobar los metadatos HDR de todos los fotogramas. " + error);
            var matches = paths.Select(path => ContainsFrame(File.ReadLines(path))).ToArray();
            return new DynamicHdrFacts(matches[0], matches[1], matches[2], matches[3]);
        }
        finally
        {
            foreach (var path in paths) if (File.Exists(path)) File.Delete(path);
        }
    }
}
