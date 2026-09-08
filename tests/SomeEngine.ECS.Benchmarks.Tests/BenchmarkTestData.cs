namespace SomeEngine.ECS.Benchmarks.Tests;

internal static class BenchmarkTestData
{
    internal static EcsBenchmarkSample Sample(double elapsedMilliseconds) => new(
        Sample: 1,
        ElapsedMilliseconds: elapsedMilliseconds,
        AllocatedBytes: 0,
        TotalAllocatedBytes: 0,
        GarbageCollections: new GarbageCollectionCounts(0, 0, 0),
        ManagedMemoryBeforeBytes: 0,
        ManagedMemoryAfterBytes: 0,
        WorkingSetBeforeBytes: 0,
        WorkingSetAfterBytes: 0,
        WorkingSetDeltaBytes: 0,
        StructuralMetrics: EmptyStructuralSample(),
        WorkloadMetrics: BenchmarkWorkloadMetricSample.Empty,
        Checksum: "test");

    private static StructuralMetricSample EmptyStructuralSample() => new(
        Started: 0,
        Published: 0,
        Aborted: 0,
        PrepareMilliseconds: 0,
        CommitMilliseconds: 0,
        LifetimeMilliseconds: 0,
        WorldMaximumPrepareMilliseconds: 0,
        WorldMaximumCommitMilliseconds: 0,
        WorldMaximumLifetimeMilliseconds: 0,
        ClonedArchetypeShells: 0,
        WorldMaximumClonedArchetypeShells: 0,
        ClonedChunkShells: 0,
        WorldMaximumClonedChunkShells: 0,
        ClonedQueryMatches: 0,
        WorldMaximumClonedQueryMatches: 0);
}

internal sealed class TemporaryDirectory : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        "SomeEngine.ECS.Benchmarks.Tests",
        Guid.NewGuid().ToString("N"));

    internal TemporaryDirectory()
    {
        Directory.CreateDirectory(_path);
    }

    internal string Write(string fileName, string contents)
    {
        string path = Path.Combine(_path, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
