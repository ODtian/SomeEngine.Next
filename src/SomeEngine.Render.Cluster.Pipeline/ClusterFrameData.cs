using System.Runtime.InteropServices;

namespace SomeEngine.Render.Cluster.Pipeline;

// Managed mirrors of the cooked Cluster frame contracts. Explicit sizes retain HLSL constant-
// buffer register padding and make accidental ABI drift fail at construction rather than on GPU.

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
internal struct ClusterDrawUniforms
{
    internal uint ScreenWidth;
    internal uint ScreenHeight;
    internal uint RasterWidth;
    internal uint RasterHeight;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
internal struct ClusterVisibilityWinnerUniforms
{
    internal uint Width;
    internal uint Height;
    internal uint ViewCount;
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
internal struct ClusterRasterBinningUniforms
{
    internal uint RasterMaxBins;
    internal uint SlotCapacity;
    internal uint MaxVisibleClusters;
    internal uint CollectMetrics;
    internal uint PolicyTarget;
    internal uint Pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
internal struct ClusterTessellationUniforms
{
    internal uint ScreenWidth;
    internal uint ScreenHeight;
    internal uint CurrentBin;
    internal uint MaxPatchItems;
    internal uint MaxFactor;
    internal float TargetEdgePixels;
    internal uint UsePreviousHiZ;
    internal uint SplitLevel;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
internal struct ClusterTessellationBinUniforms
{
    internal uint BinCount;
    internal uint MaxPatchItems;
    internal uint SourceCounterOffset;
    internal uint Pad0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
internal struct ClusterShadeBinUniforms
{
    internal uint ScreenWidth;
    internal uint ScreenHeight;
    internal uint ShadeBinCount;
    internal uint SlotCapacity;
    internal uint BinItemCapacity;
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
    internal uint MaterialSlotCapacity;
    internal uint Pad2;
    internal uint Pad3;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
internal struct ClusterResolveUniforms
{
    internal uint ScreenWidth;
    internal uint ScreenHeight;
    internal uint Pad0;
    internal uint Pad1;
}
