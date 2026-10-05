namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>The GPU could not retain all required geometry work. The frame is incomplete.</summary>
[Flags]
public enum ClusterWorkOverflow : uint
{
    None = 0,
    Hierarchy = 1,
    Candidates = 2,
    Deferred = 4,
    Visible = 8,
}

/// <summary>A memory-safe rejection of required GPU work, not a successful quality reduction.</summary>
public sealed class ClusterWorkOverflowException(ClusterWorkOverflow work, uint capacity)
    : Exception($"Cluster geometry work exceeded its {capacity} entry capacity ({work}).")
{
    public ClusterWorkOverflow Work { get; } = work;
    public uint Capacity { get; } = capacity;
}
