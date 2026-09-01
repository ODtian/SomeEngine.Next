using System.Runtime.InteropServices;

namespace SomeEngine.Render.Cluster.Pipeline;

// Managed mirrors of the cooked Cluster frame contracts. Explicit sizes retain HLSL constant-
// buffer register padding and make accidental ABI drift fail at construction rather than on GPU.

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
internal struct ClusterDrawUniforms
{
    internal uint DebugMode;
    internal uint ScreenWidth;
    internal uint ScreenHeight;
    internal uint Pad0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
internal struct ClusterDrawDispatchUniforms
{
    internal uint DrawArgsByteOffset;
    internal uint Pad0;
    internal uint Pad1;
    internal uint Pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
internal struct ClusterSoftwareRasterUniforms
{
    internal uint ScreenWidth;
    internal uint ScreenHeight;
    internal uint MaxBins;
    internal uint DebugDump;
    internal uint CurrentBin;
    internal uint Pad0;
    internal uint Pad1;
    internal uint Pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
internal struct ClusterRasterDeformBinningUniforms
{
    internal uint RasterMaxBins;
    internal uint DeformMaxBins;
    internal uint SlotCapacity;
    internal uint MaxVisibleClusters;
    internal uint ResetCacheAllocationState;
    internal uint Pad0;
    internal uint Pad1;
    internal uint Pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
internal struct ClusterDeformUniforms
{
    internal uint MaxDeformCacheBytes;
    internal uint MaxClusterVertices;
    internal uint CurrentBin;
    internal uint Pass;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
internal struct ClusterDeformPrefixUniforms
{
    internal uint MaxDeformCacheBytes;
    internal uint Pad0;
    internal uint Pad1;
    internal uint Pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
internal struct ClusterShadeBinUniforms
{
    internal uint ScreenWidth;
    internal uint ScreenHeight;
    internal uint ShadeBinCount;
    internal uint SlotCapacity;
    internal uint Pad0;
    internal uint Pad1;
    internal uint Pad2;
    internal uint Pad3;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
internal struct ClusterShadeUniforms
{
    internal uint DebugMode;
    internal uint ScreenWidth;
    internal uint ScreenHeight;
    internal uint ShadingBin;
    internal uint ShadeBinCount;
    internal uint Pad1;
    internal uint Pad2;
    internal uint Pad3;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
internal struct ClusterResolveUniforms
{
    internal uint DebugMode;
    internal uint ScreenWidth;
    internal uint ScreenHeight;
    internal uint Pad0;
}
