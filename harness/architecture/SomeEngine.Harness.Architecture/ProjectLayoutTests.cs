using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed class ProjectLayoutTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void DeclaredProjectsUsePackageDirectoryLayout()
    {
        var failures = new List<string>();

        foreach (var project in Config.Projects.AllProjects())
        {
            var normalizedPath = Normalize(project.Path);
            var expectedPath = ExpectedPath(project, normalizedPath);
            if (expectedPath is not null && normalizedPath != expectedPath)
            {
                failures.Add($"{project.Name} must live at {expectedPath}, not {project.Path}");
                continue;
            }

            var fullPath = Path.Combine(HarnessConfig.ResolveRepoRoot(), project.Path);
            if (!File.Exists(fullPath))
            {
                failures.Add($"{project.Name} project file must exist before package layout can be checked");
                continue;
            }

            var document = XDocument.Load(fullPath);
            var assemblyName = ReadProperty(document, "AssemblyName");
            var rootNamespace = ReadProperty(document, "RootNamespace");

            if (!string.IsNullOrWhiteSpace(assemblyName) && assemblyName != project.Name)
            {
                failures.Add($"{project.Name} AssemblyName must be {project.Name}, not {assemblyName}");
            }

            if (!string.IsNullOrWhiteSpace(rootNamespace) && rootNamespace != project.Name)
            {
                failures.Add($"{project.Name} RootNamespace must be {project.Name}, not {rootNamespace}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Declared project package layout is invalid:\n" + string.Join("\n", failures));
    }

    private static string? ExpectedPath(ProjectConfig project, string normalizedPath)
    {
        if (normalizedPath.StartsWith("src/", StringComparison.Ordinal))
        {
            return $"src/{project.Name}/{project.Name}.csproj";
        }

        if (normalizedPath.StartsWith("tests/", StringComparison.Ordinal))
        {
            return $"tests/{project.Name}/{project.Name}.csproj";
        }

        return null;
    }

    private static string? ReadProperty(XDocument document, string propertyName)
        => document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == propertyName)
            ?.Value;

    private static string Normalize(string path)
        => path.Replace('\\', '/');
}