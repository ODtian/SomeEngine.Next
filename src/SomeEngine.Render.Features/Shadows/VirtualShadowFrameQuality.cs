using SomeEngine.Render.Shadows;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Render.Features.Shadows;

/// <summary>Completed frame-used shadow quality. Raster units version 2: HW page area; each SW batch weighs 64 pages.</summary>
public sealed record VirtualShadowFrameQuality(ulong RenderingFrame, float DirectionalLodAddition, float LocalLodAddition,
    uint RequestedPages, uint DirtyPages, uint AllocationFailures, uint FineAllocationFailures,
    uint FreePhysicalPages, uint RasterPageArea, ClusterWorkOverflow WorkOverflow, VirtualShadowViewQuality[] Views)
{
    public double RasterBatchEquivalents => RasterPageArea / 64.0;
}

/// <summary>Actual submitted projection and mip-resolution/completed-depth populations, not next-frame policy.</summary>
public sealed record VirtualShadowViewQuality(VirtualShadowLightKind LightKind, uint LightIndex, uint FaceIndex,
    int ClipmapLevel, uint VirtualResolution, float BaseResolutionLodBias, float AppliedResolutionLodBias,
    VirtualShadowMipQuality[] Mips);

/// <summary>Receiver coverage uses an 8x8 cell population per page. Missing/unfinished pages contribute no completed cells.</summary>
public sealed record VirtualShadowMipQuality(uint Mip, uint Resolution, uint MappedPages,
    uint WholeDepthPages, uint ReceiverDepthPages, uint UnfinishedPages, ulong CoveredReceiverCells);
