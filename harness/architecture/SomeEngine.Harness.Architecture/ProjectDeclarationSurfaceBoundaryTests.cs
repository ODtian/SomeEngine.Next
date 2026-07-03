using System.Collections.Generic;
using System.IO;
using System.Linq;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed class ProjectDeclarationSurfaceBoundaryTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();
    private static readonly string[] AutomaticDeclarationFileNames =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
    ];

    [Fact]
    public void FirstRoundProjectsDoNotUseIntermediateAutomaticBuildDeclarations()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.AllProjects())
        {
            string projectPath = Path.Combine(repoRoot, project.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(projectPath))
            {
                continue;
            }

            string projectDirectory = Path.GetDirectoryName(projectPath) ?? repoRoot;
            foreach (string declarationFile in IntermediateAutomaticDeclarationFiles(projectDirectory, repoRoot))
            {
                string relative = Normalize(Path.GetRelativePath(repoRoot, declarationFile));
                failures.Add($"{project.Name} is affected by automatic build declaration {relative} outside the root/project-local declaration surface.");
            }
        }

        Assert.True(
            failures.Count == 0,
            "First-round projects are affected by intermediate automatic build declarations that are not part of the accepted declaration surface:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void IntermediateAutomaticDeclarationScanCoversPropsTargetsAndCentralPackages()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessAutomaticDeclarations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string sourceRoot = Path.Combine(tempRoot, "src");
            string projectDirectory = Path.Combine(sourceRoot, "SomeEngine.Sample");
            Directory.CreateDirectory(projectDirectory);

            foreach (string fileName in AutomaticDeclarationFileNames)
            {
                File.WriteAllText(Path.Combine(tempRoot, fileName), "<Project />");
                File.WriteAllText(Path.Combine(sourceRoot, fileName), "<Project />");
                File.WriteAllText(Path.Combine(projectDirectory, fileName), "<Project />");
            }

            string[] intermediateFiles = IntermediateAutomaticDeclarationFiles(projectDirectory, tempRoot)
                .Select(path => Normalize(Path.GetRelativePath(tempRoot, path)))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                [
                    "src/Directory.Build.props",
                    "src/Directory.Build.targets",
                    "src/Directory.Packages.props",
                ],
                intermediateFiles);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static IEnumerable<string> IntermediateAutomaticDeclarationFiles(string projectDirectory, string repoRoot)
    {
        string repoFullPath = TrimDirectory(Path.GetFullPath(repoRoot));
        DirectoryInfo? directory = Directory.GetParent(Path.GetFullPath(projectDirectory));

        while (directory is not null)
        {
            string directoryFullPath = TrimDirectory(directory.FullName);
            if (string.Equals(directoryFullPath, repoFullPath, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            if (!IsUnder(directoryFullPath, repoFullPath))
            {
                yield break;
            }

            foreach (string fileName in AutomaticDeclarationFileNames)
            {
                string declarationFile = Path.Combine(directoryFullPath, fileName);
                if (File.Exists(declarationFile))
                {
                    yield return declarationFile;
                }
            }

            directory = directory.Parent;
        }
    }

    private static bool IsUnder(string path, string root)
        => path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string TrimDirectory(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string Normalize(string path)
        => path.Replace('\\', '/');
}
