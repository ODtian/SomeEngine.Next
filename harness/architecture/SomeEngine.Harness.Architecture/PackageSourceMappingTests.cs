using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed class PackageSourceMappingTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void RootNuGetConfigMapsLocalPackagesToRepositoryFeed()
    {
        var repoRoot = HarnessConfig.ResolveRepoRoot();
        var nugetConfigPath = Path.Combine(repoRoot, "NuGet.config");
        Assert.True(File.Exists(nugetConfigPath), "Repository root NuGet.config must exist.");

        var document = XDocument.Load(nugetConfigPath);
        var failures = new List<string>();

        foreach (var package in Config.ExternalDependencies.LocalPackages)
        {
            var source = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "add"
                    && string.Equals(element.Attribute("key")?.Value, package.PackageSourceKey, StringComparison.OrdinalIgnoreCase));

            if (source is null)
            {
                failures.Add($"NuGet.config packageSources must contain {package.PackageSourceKey}");
            }
            else if (!PathEquals(source.Attribute("value")?.Value, package.LocalFeed))
            {
                failures.Add($"NuGet.config source {package.PackageSourceKey} must point at {package.LocalFeed}, not {source.Attribute("value")?.Value}");
            }

            var mappedPatterns = document.Descendants()
                .Where(element => element.Name.LocalName == "packageSource"
                    && string.Equals(element.Attribute("key")?.Value, package.PackageSourceKey, StringComparison.OrdinalIgnoreCase))
                .Descendants()
                .Where(element => element.Name.LocalName == "package")
                .Select(element => element.Attribute("pattern")?.Value)
                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!mappedPatterns.Contains(package.PackageId))
            {
                failures.Add($"NuGet.config packageSourceMapping for {package.PackageSourceKey} must include {package.PackageId}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Local package source mapping is invalid:\n" + string.Join("\n", failures));
    }

    private static bool PathEquals(string? actual, string expected)
        => string.Equals(Normalize(actual ?? ""), Normalize(expected), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
        => path.Replace('\\', '/').TrimEnd('/');
}