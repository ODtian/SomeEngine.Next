using System.Numerics;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Winner protocol used by hardware and software rasterization. There is deliberately no
/// split 32-bit depth/id compatibility protocol because it cannot publish an atomic winner.
/// </summary>
public enum ClusterVisibilityMode : byte
{
    ImageAtomic64,
    BufferAtomic64,
    HardwareOnly,
}

/// <summary>Capacity and quality policy for Cluster visibility graph construction.</summary>
public sealed record ClusterPipelineOptions
{
    private const uint CandidateThreadsPerGroup = 64;
    private const uint PortableDispatchDimension = 65_535;

    /// <summary>
    /// Bounds candidates, visible geometry items/page windows, the final surface-patch pool,
    /// and each level of a compatible surface group's work queue. Raster-batch storage uses
    /// the largest per-cluster batch count in resident geometry. Rejected required work
    /// fails the frame through ClusterWorkOverflowException.
    /// </summary>
    public uint MaxCandidates { get; init; } = 1_048_576;


    public float LodThreshold { get; init; } = 0.25f;

    public uint MaxTessellationFactor { get; init; } = 256;

    public float TessellationTargetEdgePixels { get; init; } = 8.0f;

    public bool EnableAsyncCompute { get; init; }

    public bool ForceHardwareRaster { get; init; }

    /// <summary>Tests spatial groups before individual instances. Disabled to avoid maintaining groups unless their culling benefit warrants it.</summary>
    public bool UseInstanceGroups { get; init; }

    /// <summary>
    /// Maximum projected triangle edge routed to software rasterization in every Cluster view.
    /// </summary>
    public float SoftwareRasterEdgeThreshold { get; init; } = 32.0f;

    /// <summary>Order pixel-programmable raster work by logarithmic view-depth buckets.</summary>
    public bool EnableDepthBuckets { get; init; } = true;

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
        if (!float.IsFinite(LodThreshold) || LodThreshold < 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LodThreshold),
                "Cluster LOD threshold must be finite and non-negative.");
        }
        if (!float.IsFinite(SoftwareRasterEdgeThreshold) || SoftwareRasterEdgeThreshold < 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SoftwareRasterEdgeThreshold),
                "Cluster software-raster edge threshold must be finite and non-negative.");
        }
        if (MaxTessellationFactor is 0 or > 256 || !BitOperations.IsPow2(MaxTessellationFactor))
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxTessellationFactor),
                "Cluster tessellation factor must be a power of two in [1, 256].");
        }
        if (!float.IsFinite(TessellationTargetEdgePixels) || TessellationTargetEdgePixels < 1.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TessellationTargetEdgePixels),
                "Cluster tessellation target edge length must be finite and at least one pixel.");
        }
        _ = checked((ulong)MaxCandidates * 12ul);
        _ = checked((ulong)MaxCandidates * 16ul);
        _ = checked((ulong)MaxCandidates * 16ul * 2ul);
    }

    internal ClusterVisibilityMode SelectVisibilityMode(
        SomeEngine.Graphics.DeviceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (ForceHardwareRaster)
            return ClusterVisibilityMode.HardwareOnly;
        if (capabilities.SupportsImageInt64Atomics)
            return ClusterVisibilityMode.ImageAtomic64;
        if (capabilities.SupportsBufferInt64Atomics)
            return ClusterVisibilityMode.BufferAtomic64;
        return ClusterVisibilityMode.HardwareOnly;
    }
}
