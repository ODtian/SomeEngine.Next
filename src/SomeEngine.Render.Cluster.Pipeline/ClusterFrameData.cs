using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Cluster.Pipeline;

// Managed mirrors of the cooked Cluster frame contracts. Explicit sizes retain HLSL constant-
// buffer register padding and make accidental ABI drift fail at construction rather than on GPU.

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 144)]
internal struct ClusterDrawUniforms
{
    internal Matrix4x4 ViewProj;
    internal Matrix4x4 View;
    internal uint DebugMode;
    internal uint ScreenWidth;
    internal uint ScreenHeight;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
internal struct ClusterDrawDispatchUniforms
{
    internal uint DrawArgsByteOffset;
    internal uint Pad0;
    internal uint Pad1;
    internal uint Pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 96)]
internal struct ClusterSoftwareRasterUniforms
{
    internal Matrix4x4 ViewProj;
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
    internal uint RasterBinFieldIndex;
    internal uint DeformBinFieldIndex;
    internal uint MaxVisibleClusters;
    internal uint ResetCacheAllocationState;
    internal uint Pad0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
internal struct ClusterDeformUniforms
{
    internal uint MaxDeformCacheBytes;
    internal uint MaxClusterVertices;
    internal uint CurrentBin;
    internal uint Pad0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
internal struct ClusterShadeBinUniforms
{
    internal uint ScreenWidth;
    internal uint ScreenHeight;
    internal uint MaterialCount;
    internal uint SlotCapacity;
    internal uint BinFieldIndex;
    internal uint Pad0;
    internal uint Pad1;
    internal uint Pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 448)]
internal struct ClusterShadeUniforms
{
    internal Matrix4x4 ViewProj;
    internal Matrix4x4 ClipToWorld;
    internal Matrix4x4 View;
    internal Matrix4x4 PrevViewProj;
    internal Matrix4x4 MotionViewProj;
    internal Matrix4x4 PrevMotionViewProj;
    internal uint DebugMode;
    internal uint ScreenWidth;
    internal uint ScreenHeight;
    internal uint ShadingBin;
    internal uint MaterialCount;
    internal uint LightLayerMask;
    internal uint Pad1;
    internal Vector3 CameraPos;
    internal float Pad2;
    internal uint HasPreviousFrame;
    internal uint WriteMotionVectors;
    internal uint Pad3;
    internal uint Pad4;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
internal struct ClusterLightCounts
{
    internal uint DirectionalCount;
    internal uint PointCount;
    internal uint SpotCount;
    internal uint Pad0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
internal struct ClusterLightGridUniforms
{
    internal uint TileSizeX;
    internal uint TileSizeY;
    internal uint TileCountX;
    internal uint TileCountY;
    internal Vector4 ZParams;
    internal uint DepthSliceCount;
    internal uint Pad0;
    internal uint Pad1;
    internal uint Pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 144)]
internal struct ClusterResolveUniforms
{
    internal Matrix4x4 ViewProj;
    internal Matrix4x4 View;
    internal uint DebugMode;
    internal uint ScreenWidth;
    internal uint ScreenHeight;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 144)]
internal struct ClusterMotionUniforms
{
    internal Matrix4x4 ViewProj;
    internal Matrix4x4 PrevViewProj;
    internal uint ScreenWidth;
    internal uint ScreenHeight;
    internal uint HasPreviousFrame;
    internal uint Pad0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
internal struct ClusterTemporalUniforms
{
    internal float HistoryWeight;
    internal float NeighborhoodClampScale;
    internal float NeighborhoodClampMin;
    internal float MotionRejectionScale;
}
