using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.PreTranscode.Configuration;

namespace Jellyfin.Plugin.PreTranscode.Safety;

public sealed record FolderDecision(bool Allowed, string Reason, string? Root = null);

public static class FolderPolicy
{
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool SameFolder(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), Comparison);

    public static bool Contains(string root, string path)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var child = Path.GetFullPath(path);
        return string.Equals(parent, Path.TrimEndingDirectorySeparator(child), Comparison)
            || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, Comparison);
    }

    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("No se admiten enlaces simbólicos ni junctions: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static FolderDecision Evaluate(string path, PluginConfiguration config, IReadOnlyList<string> roots)
    {
        if (!Path.IsPathFullyQualified(path)) return new(false, "La ruta debe ser absoluta.");
        RejectLinks(path);
        var root = roots.Where(r => Contains(r, path)).OrderByDescending(r => r.Length).FirstOrDefault();
        if (root is null) return new(false, "Fuera de las bibliotecas de películas.");
        if (!config.IncludedFolders.Any(f => Contains(root, f) && Contains(f, path))) return new(false, "Carpeta no seleccionada.");
        if (config.ExcludedFolders.Any(f => Contains(f, path))) return new(false, "Carpeta excluida.");
        return new(true, "Seleccionada", root);
    }

    public static void ValidateConfiguration(PluginConfiguration config, IReadOnlyList<string> roots)
    {
        if (!Path.IsPathFullyQualified(config.QuarantineDirectory) || config.RetentionDays is < 1 or > 3650)
            throw new InvalidOperationException("Elige una carpeta absoluta para originales y una retención de 1 a 3650 días.");
        if (config.QuarantineMaxBytes is < 0 or > 1125899906842624L)
            throw new InvalidOperationException("El límite de originales debe estar entre 0 y 1048576 GB.");
        RejectLinks(config.QuarantineDirectory);
        if (roots.Any(r => Contains(r, config.QuarantineDirectory) || Contains(config.QuarantineDirectory, r)))
            throw new InvalidOperationException("La carpeta de originales debe estar separada de todas las bibliotecas.");
        if (!double.IsFinite(config.MinSavingsPercent) || config.MinSavingsPercent is < 1 or > 95)
            throw new InvalidOperationException("El ahorro mínimo debe estar entre 1 y 95 %.");
        if (config.MinMovieSizeGb is < 0 or > 1048576)
            throw new InvalidOperationException("El tamaño mínimo de película debe estar entre 0 y 1048576 GB.");
        foreach (var folder in config.IncludedFolders.Concat(config.ExcludedFolders))
        {
            if (!Path.IsPathFullyQualified(folder) || !roots.Any(r => Contains(r, folder)))
                throw new InvalidOperationException("Carpeta ajena a las bibliotecas: " + folder);
            RejectLinks(folder);
        }
    }
}
