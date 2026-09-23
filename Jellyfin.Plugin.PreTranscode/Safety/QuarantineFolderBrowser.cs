using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.PreTranscode.Safety;

internal sealed record BrowsableFolder(string Path, string Name, bool Selectable);

internal static class QuarantineFolderBrowser
{
    internal static bool CanSelect(string path, IReadOnlyList<string> libraryRoots)
        => Path.IsPathFullyQualified(path) && Directory.Exists(path)
            && !libraryRoots.Any(root => FolderPolicy.Contains(root, path) || FolderPolicy.Contains(path, root));

    internal static IReadOnlyList<BrowsableFolder> List(string? path, IReadOnlyList<string> libraryRoots)
    {
        if (string.IsNullOrEmpty(path))
            return DriveInfo.GetDrives().Where(drive => drive.IsReady)
                .Select(drive => drive.RootDirectory.FullName)
                .Select(folder => new BrowsableFolder(folder, folder, CanSelect(folder, libraryRoots))).ToArray();

        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new InvalidOperationException("Carpeta no disponible en el servidor.");
        FolderPolicy.RejectLinks(path);
        return Directory.EnumerateDirectories(path)
            .Where(folder => (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0)
            .OrderBy(folder => folder, FolderPolicy.Comparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(folder => new BrowsableFolder(folder, System.IO.Path.GetFileName(folder), CanSelect(folder, libraryRoots))).ToArray();
    }
}
