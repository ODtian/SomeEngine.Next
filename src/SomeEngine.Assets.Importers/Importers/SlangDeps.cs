using SlangShaderSharp;
using Schema = global::SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

internal static class SlangDeps
{
    public static bool Matches(Schema.Shader.Asset asset, SourceOutput existingAsset)
    {
        return asset.Id.Value == existingAsset.AssetGuid
            && asset.ImportTrace?.ContentFingerprint == existingAsset.InputHash
;
    }

    public static FileDependency[] Collect(
        IModule module,
        string sourcePath,
        string projectRoot)
    {
        var dependencies = new Dictionary<string, FileDependency>(
            StringComparer.OrdinalIgnoreCase);

        void Add(string dependencyPath)
        {
            string fullPath = Path.GetFullPath(dependencyPath);
            if (!File.Exists(fullPath))
            {
                return;
            }

            string relativePath = RelPath(projectRoot, fullPath);
            dependencies[relativePath] = new FileDependency
            {
                RelativePath = relativePath,
                ContentHash = ImportHash.FileSha256(fullPath),
            };
        }

        Add(sourcePath);
        int dependencyCount = module.GetDependencyFileCount();
        for (int i = 0; i < dependencyCount; i++)
        {
            Add(module.GetDependencyFilePath(i));
        }

        return dependencies
            .Values.OrderBy(static x => x.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    public static ImportInputs? Refresh(
        IReadOnlyList<FileDependency> dependencies,
        string projectRoot,
        uint importerVersion,
        params string[] extraParts)
    {
        if (dependencies.Count == 0)
        {
            return null;
        }

        FileDependency[] currentDependencies = new FileDependency[dependencies.Count];
        for (int i = 0; i < dependencies.Count; i++)
        {
            string fullPath = AbsPath(projectRoot, dependencies[i].RelativePath);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            currentDependencies[i] = new FileDependency
            {
                RelativePath = RelPath(projectRoot, fullPath),
                ContentHash = ImportHash.FileSha256(fullPath),
            };
        }

        return ImportHash.Create(currentDependencies, importerVersion, extraParts);
    }

    public static string ProjectRoot(string sourcePath)
    {
        string? current = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "SomeEngine.slnx"))
                || File.Exists(Path.Combine(current, "Directory.Build.props")))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        string cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
        if (Path.Exists(cwd) && sourcePath.StartsWith(cwd, StringComparison.OrdinalIgnoreCase))
        {
            return cwd;
        }

        return Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? cwd;
    }

    private static string RelPath(string projectRoot, string fullPath)
    {
        string normalizedRoot = Path.GetFullPath(projectRoot);
        string normalizedPath = Path.GetFullPath(fullPath);
        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileName(normalizedPath);
        }

        return Path.GetRelativePath(normalizedRoot, normalizedPath).Replace('\\', '/');
    }

    private static string AbsPath(string projectRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            return Path.GetFullPath(relativePath);
        }

        return Path.GetFullPath(Path.Combine(projectRoot, relativePath));
    }
}

