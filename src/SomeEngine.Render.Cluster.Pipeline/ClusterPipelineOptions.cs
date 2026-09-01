namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Capacity and quality policy for Cluster visibility graph construction.</summary>
public sealed record ClusterPipelineOptions
{
    private const uint CandidateThreadsPerGroup = 64;
    private const uint PortableDispatchDimension = 65_535;
    internal const ulong MaximumDeformCacheBytes = 0xFFFFF000UL;

    public uint MaxCandidates { get; init; } = 1_048_576;

    public uint MaxTraversalDepth { get; init; } = 128;

    public float LodThreshold { get; init; } = 1.0f;

    public ulong DeformCacheBytes { get; init; } = 64UL * 1024UL * 1024UL;

    public uint MaxTessellationSplitWorkItems { get; init; } = 1_048_576;

    public uint MaxDicedTriangles { get; init; } = 1_048_576;

    public uint MaxTessellationFactor { get; init; } = 8;

    public float TessellationTargetEdgePixels { get; init; } = 8.0f;

    public bool EnableAsyncCompute { get; init; }

    public bool ForceHardwareRaster { get; init; }

    public float SoftwareRasterAreaThreshold { get; init; } = 2_000.0f;

    public bool EnableFrameMetricsReadback { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfZero(MaxCandidates);
        if (MaxCandidates > CandidateThreadsPerGroup * PortableDispatchDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxCandidates),
                "Cluster candidate capacity must fit one portable indirect-dispatch dimension.");
        }
        if (MaxTraversalDepth is 0 or > 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxTraversalDepth),
                "Cluster traversal depth must fit the shader's proven 128-entry pending stack.");
        }
        if (!float.IsFinite(LodThreshold) || LodThreshold < 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LodThreshold),
                "Cluster LOD threshold must be finite and non-negative.");
        }
        if (!float.IsFinite(SoftwareRasterAreaThreshold) || SoftwareRasterAreaThreshold < 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SoftwareRasterAreaThreshold),
                "Cluster software-raster area threshold must be finite and non-negative.");
        }
        if (DeformCacheBytes is < 16 or > MaximumDeformCacheBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DeformCacheBytes),
                "Cluster deform cache must fit below the shader's reserved invalid-offset range.");
        }
        ArgumentOutOfRangeException.ThrowIfZero(MaxTessellationSplitWorkItems);
        ArgumentOutOfRangeException.ThrowIfZero(MaxDicedTriangles);
        if (MaxTessellationFactor is 0 or > 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxTessellationFactor),
                "Cluster tessellation factor must fit the bounded dicing policy [1, 16].");
        }
        if (!float.IsFinite(TessellationTargetEdgePixels) || TessellationTargetEdgePixels < 1.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TessellationTargetEdgePixels),
                "Cluster tessellation target edge length must be finite and at least one pixel.");
        }
        _ = checked((ulong)MaxCandidates * 12ul);
        _ = checked((ulong)MaxCandidates * 16ul);
        _ = checked((ulong)MaxTessellationSplitWorkItems * 20ul);
        _ = checked((ulong)MaxDicedTriangles * 48ul);
    }
}
