using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Shadows;

/// <summary>
/// One shadow-caster projection and virtual-atlas mapping. A geometry pipeline may consume a span
/// of these records in one multi-view submission.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = SizeInBytes)]
public struct VirtualShadowView
{
    public const int SizeInBytes = 144;
    public const int PageTableEntrySizeInBytes = 16;

    public Matrix4x4 LightViewProjection;
    public uint VirtualResolution;
    public uint PageSize;
    public uint AtlasSize;
    public uint PhysicalPagesPerRow;
    public uint MaxPhysicalPages;
    public int VirtualPageOriginX;
    public float DepthBias;
    public uint PageTableOffset;
    public uint CacheGeneration;
    public int VirtualPageOriginY;
    public int ClipmapLevel;
    public uint LightIndex;
    public Vector3 ClipmapWorldOrigin;
    public float ResolutionLodBias;
    public int FirstClipmapLevel;
    public uint ClipmapLevelCount;
    public uint Pad0;
    public uint Pad1;
}
