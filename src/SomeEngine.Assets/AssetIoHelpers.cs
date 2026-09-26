using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SomeEngine.Assets;

internal static class AssetIoHelpers
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    internal static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path).Replace('\\', '/');
        var parts = new List<string>();
        foreach (string part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == ".." && parts.Count != 0 && parts[^1] != "..") parts.RemoveAt(parts.Count - 1);
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }

    internal static string ToManifestPath(string projectRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string fullRoot = Path.GetFullPath(projectRoot);
        string fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(fullRoot, path));
        string relative = Path.GetRelativePath(fullRoot, fullPath);
        bool outside = Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        return (outside ? fullPath : relative).Replace('\\', '/');
    }
}

