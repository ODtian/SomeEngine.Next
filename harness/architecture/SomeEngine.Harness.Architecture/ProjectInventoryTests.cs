using System.Collections.Generic;
using System.IO;
using System.Linq;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed class ProjectInventoryTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void DeclaredProjectsExistOnDisk()
    {
        var missing = Config.Projects.AllProjects()
            .Where(project => !File.Exists(Path.Combine(HarnessConfig.ResolveRepoRoot(), project.Path)))
            .Select(project => $"{project.Name} must exist at {project.Path}")
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Declared harness project facts are not present on disk:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void DeclaredProjectNamesMatchFileNames()
    {
        var failures = new List<string>();

        foreach (var project in Config.Projects.AllProjects())
        {
            var fileName = Path.GetFileNameWithoutExtension(project.Path.Replace('\\', '/'));
            if (!string.Equals(fileName, project.Name, StringComparison.Ordinal))
            {
                failures.Add($"{project.Path} file name must match declared project name {project.Name}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Declared harness project facts must match project file names:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void AllSourceAndTestProjectsAreClassifiedByFirstRoundBoundary()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var declaredProjectPaths = Config.Projects.AllProjects()
            .Select(project => Normalize(project.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedProjectNames = Config.Architecture.ExcludedProjectNames
            .ToHashSet(StringComparer.Ordinal);
        var excludedRoots = Config.Architecture.ExcludedWorkspaceRoots
            .Select(Normalize)
            .ToArray();
        var failures = new List<string>();

        foreach (string projectPath in EnumerateSourceAndTestProjects(repoRoot))
        {
            string relativePath = Normalize(Path.GetRelativePath(repoRoot, projectPath));
            string projectName = Path.GetFileNameWithoutExtension(projectPath);

            if (declaredProjectPaths.Contains(relativePath)
                || excludedProjectNames.Contains(projectName)
                || IsUnderExcludedRoot(relativePath, excludedRoots))
            {
                continue;
            }

            failures.Add($"{relativePath} is neither declared for the first-round boundary nor explicitly excluded from it.");
        }

        Assert.True(
            failures.Count == 0,
            "Source and test projects must be classified by the accepted first-round boundary:\n" + string.Join("\n", failures));
    }

    private static IEnumerable<string> EnumerateSourceAndTestProjects(string repoRoot)
    {
        foreach (string rootName in new[] { "src", "tests" })
        {
            string rootPath = Path.Combine(repoRoot, rootName);
            if (!Directory.Exists(rootPath))
            {
                continue;
            }

            foreach (string projectPath in Directory.EnumerateFiles(rootPath, "*.csproj", SearchOption.AllDirectories)
                         .Where(path => !IsGeneratedOutputPath(path)))
            {
                yield return projectPath;
            }
        }
    }

    private static bool IsUnderExcludedRoot(string relativePath, string[] excludedRoots)
    {
        foreach (string excludedRoot in excludedRoots)
        {
            if (relativePath.Equals(excludedRoot, StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith(excludedRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGeneratedOutputPath(string path)
        => path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
           || path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
        => path.Replace('\\', '/');
}
