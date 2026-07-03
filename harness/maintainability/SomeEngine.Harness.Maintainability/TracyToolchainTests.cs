using System.IO;
using System.Linq;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Maintainability;

public sealed class TracyToolchainTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void ProfilerBridgeFilesExist()
    {
        var root = HarnessConfig.ResolveRepoRoot();
        var missing = Config.Profiler.BridgeFiles
            .Where(path => !File.Exists(Path.Combine(root, path)))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Profiler bridge files are missing:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void RequiredTracyToolsExistAtConfiguredExternalToolRoot()
    {
        var dir = Config.Profiler.ExternalToolRoot;
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        var missing = Config.Profiler.RequiredTools
            .Select(tool => Path.Combine(dir, tool))
            .Where(path => !File.Exists(path))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Required Tracy tools are missing at configured external tool root:\n" + string.Join("\n", missing));
    }
}
