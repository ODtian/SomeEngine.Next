namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>A memory-safe rejection of required GPU work, not a successful quality reduction.</summary>
public sealed class ClusterWorkOverflowException(RenderGeometryWorkFlags work, uint capacity)
    : Exception($"Cluster geometry work exceeded its {capacity} entry capacity ({work}).")
{
    public RenderGeometryWorkFlags Work { get; } = work;
    public uint Capacity { get; } = capacity;
}
