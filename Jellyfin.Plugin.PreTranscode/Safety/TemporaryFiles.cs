using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.PreTranscode.Jobs;

namespace Jellyfin.Plugin.PreTranscode.Safety;

public static class TemporaryFiles
{
    public static void Clean(string directory, IReadOnlyList<TranscodeJob> jobs)
    {
        FolderPolicy.RejectLinks(directory);
        if (!Directory.Exists(directory)) return;
        var active = jobs.Where(j => j.Status is JobStatus.Pending or JobStatus.Processing).Select(j => j.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(id, "N", out _) || active.Contains(id) || Path.GetExtension(path).ToLowerInvariant() is not (".mkv" or ".mp4" or ".m4v")) continue;
            FolderPolicy.RejectLinks(path);
            File.Delete(path);
        }
    }
}
