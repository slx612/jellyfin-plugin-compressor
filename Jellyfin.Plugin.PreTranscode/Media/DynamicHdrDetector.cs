using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Encoding;

namespace Jellyfin.Plugin.PreTranscode.Media;

internal static class DynamicHdrDetector
{
    internal static bool ContainsFrame(IEnumerable<string> frameHashLines) => frameHashLines.Any(line =>
    {
        var value = line.TrimStart();
        return value.Length > 0 && value[0] != '#';
    });

    // Stream metadata and Jellyfin's catalog can both miss HDR10+ carried only on decoded frames.
    // The sidedata filter selects those frames, and framehash writes a row for the first match.
    // A source with no match is decoded to EOF; a failed or cancelled scan never permits encoding.
    internal static async Task<bool> HasHdr10PlusAsync(string ffmpegPath, string sourcePath, string temporaryDirectory,
        double durationSeconds, CancellationToken token, Action<Process>? onProcessStarted = null)
    {
        Directory.CreateDirectory(temporaryDirectory);
        var hashPath = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N") + ".framehash");
        try
        {
            var args = new[] { "-hide_banner", "-loglevel", "error", "-protocol_whitelist", "file", "-i", sourcePath,
                "-map", "0:V:0", "-an", "-sn", "-vf", "sidedata=mode=select:type=DYNAMIC_HDR_PLUS",
                "-frames:v", "1", "-f", "framehash", hashPath };
            var (exitCode, error) = await FfmpegExecutor.RunAsync(ffmpegPath, args, durationSeconds, null,
                token, onProcessStarted).ConfigureAwait(false);
            if (exitCode != 0 || !File.Exists(hashPath))
                throw new IOException("No se pudo comprobar HDR10+ en todos los fotogramas. " + error);
            return ContainsFrame(File.ReadLines(hashPath));
        }
        finally
        {
            if (File.Exists(hashPath)) File.Delete(hashPath);
        }
    }
}
