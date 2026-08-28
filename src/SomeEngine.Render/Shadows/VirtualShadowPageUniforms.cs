using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Shadows;

/// <summary>Maps one receiver depth image into every virtual-shadow address space.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 96)]
public struct VirtualShadowPageDemandUniforms
{
    public Matrix4x4 ClipToWorld;
    public uint ReceiverWidth;
    public uint ReceiverHeight;
    public uint ViewCount;
    public uint PageTableEntryCount;
    public uint DirectionalLightCount;
    public uint DirectionalClipmapLevels;
    public uint Pad0;
    public uint Pad1;
}

/// <summary>
/// Bounds one virtual-page allocation, dirty-page activation and indirect shadow traversal setup.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public struct VirtualShadowPageAllocationUniforms
{
    public uint PageTableEntryCount;
    public uint MaxPhysicalPages;
    public uint ClearTilesPerPageAxis;
    public uint VirtualPagesPerView;
    public uint ViewCount;
    public uint TraversalGroupCount;
    public uint MaxPagesToRasterPerFrame;
    public uint PhysicalPageEvictionProbeCount;
}

/// <summary>Maps active physical page numbers to atlas pixels during depth reset.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public struct VirtualShadowPageClearUniforms
{
    public uint PageSize;
    public uint PhysicalPagesPerRow;
    public uint AtlasSize;
    public uint Pad0;
}

/// <summary>Bounds directional-light to virtual-shadow-view sampling.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public struct VirtualShadowSamplingUniforms
{
    public uint ViewCount;
    public uint DirectionalLightCount;
    public uint DirectionalClipmapLevels;
    public uint Pad2;
}
