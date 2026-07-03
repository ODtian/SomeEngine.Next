using System.IO;
using System.Linq;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Maintainability;

public sealed class RuntimeAllocationGateTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void RuntimeAllocationTargetsArePresent()
    {
        var targets = Config.Projects.ProductProjects
            .Concat(Config.Projects.TestProjects)
            .Where(project => project.Name is "SomeEngine.Runtime" or "SomeEngine.Runtime.Tests")
            .ToArray();

        if (targets.Length == 0)
        {
            return;
        }

        var missing = targets
            .Where(project => !File.Exists(Path.Combine(HarnessConfig.ResolveRepoRoot(), project.Path)))
            .Select(project => $"{project.Name} must exist at {project.Path}")
            .ToArray();

        Assert.True(
            targets.Length == 2 && missing.Length == 0,
            "Runtime allocation gate requires runtime product and runtime consumer tests:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void RuntimeAllocationBudgetConfigIsValid()
    {
        Assert.True(Config.RuntimeAllocation.MaxGcGen0PerFrame >= 0);
        Assert.True(Config.RuntimeAllocation.MaxAllocBytesPerFrame > 0);
    }
}
