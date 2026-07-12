namespace SomeEngine.Graphics;

public enum PipelineStatus : byte
{
    Ready,
    Pending,
    Failed,
}

public readonly record struct PipelineCacheKey(Guid StableId, ulong Version = 0)
{
    public bool IsValid => StableId != Guid.Empty;
}

public readonly record struct PipelineCacheStats(
    int Entries,
    long Hits,
    long Misses,
    long Invalidations);
