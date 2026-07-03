using System.IO;
using System.Text.Json;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Maintainability;

public sealed class RuntimeAllocationTraceGateTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void RuntimeAllocationTraceSatisfiesBudget()
    {
        var tracePath = Path.Combine(HarnessConfig.ResolveRepoRoot(), Config.RuntimeAllocation.TracePath);
        Assert.True(
            File.Exists(tracePath),
            $"Runtime allocation trace fixture must exist at {Config.RuntimeAllocation.TracePath}. " +
            "Generate it from an external profiler trace, not from engine-local self profiling.");

        var json = File.ReadAllText(tracePath);
        var trace = JsonSerializer.Deserialize<RuntimeAllocationTrace>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        Assert.NotNull(trace);
        Assert.False(string.IsNullOrWhiteSpace(trace!.Scenario));
        Assert.Contains(trace.Source, Config.RuntimeAllocation.AllowedTraceSources);
        Assert.True(trace.Frames > 0);
        Assert.True(trace.GcGen0PerFrame <= Config.RuntimeAllocation.MaxGcGen0PerFrame,
            $"Runtime Gen0 budget exceeded: actual {trace.GcGen0PerFrame}, max {Config.RuntimeAllocation.MaxGcGen0PerFrame}.");
        Assert.True(trace.AllocBytesPerFrame <= Config.RuntimeAllocation.MaxAllocBytesPerFrame,
            $"Runtime allocation budget exceeded: actual {trace.AllocBytesPerFrame}, max {Config.RuntimeAllocation.MaxAllocBytesPerFrame}.");
    }

    private sealed class RuntimeAllocationTrace
    {
        public string Scenario { get; set; } = "";
        public string Source { get; set; } = "";
        public int Frames { get; set; }
        public double GcGen0PerFrame { get; set; }
        public double AllocBytesPerFrame { get; set; }
    }
}