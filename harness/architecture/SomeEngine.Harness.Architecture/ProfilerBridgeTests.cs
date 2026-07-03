using System.Collections.Generic;
using System.IO;
using System.Linq;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed class ProfilerBridgeTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void ProfilerBridgeDoesNotRegrowEngineLocalProfilerCenters()
    {
        var root = HarnessConfig.ResolveRepoRoot();
        var bridgeFiles = Config.Profiler.BridgeFiles
            .Select(path => Path.GetFullPath(Path.Combine(root, path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.ProductProjects.Concat(Config.Projects.BuildSupportProjects))
        {
            string projectPath = Path.Combine(root, project.Path.Replace('/', Path.DirectorySeparatorChar));
            string projectDirectory = Path.GetDirectoryName(projectPath) ?? root;
            if (!Directory.Exists(projectDirectory))
            {
                failures.Add($"{project.Name} source root must exist before profiler boundary can be checked.");
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
            {
                var normalized = file.Replace('\\', '/');
                if (normalized.Contains("/bin/", StringComparison.Ordinal)
                    || normalized.Contains("/obj/", StringComparison.Ordinal)
                    || bridgeFiles.Contains(Path.GetFullPath(file)))
                {
                    continue;
                }

                var source = File.ReadAllText(file);
                foreach (var token in Config.Profiler.ForbiddenProfilerCenters)
                {
                    if (source.Contains(token, StringComparison.Ordinal))
                    {
                        failures.Add($"{Path.GetRelativePath(root, file)} contains profiler-local center token '{token}'");
                    }
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Profiler must remain an external-profiler bridge only:\n" + string.Join("\n", failures));
    }
}
