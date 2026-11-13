using System.Runtime.InteropServices;

namespace SomeEngine.Render.Shadows;

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = SizeInBytes)]
public struct VirtualShadowPageMetadata
{
    public const int SizeInBytes = 36;
    public uint LastRequestedFrame;
    public uint StaticDepthGeneration;
    public uint RequestFlags;
    public uint Flags;
    public int WorldPageX;
    public int WorldPageY;
    public uint Generation;
    public uint RangeId;
    public uint RangeGeneration;
}

public static class VirtualShadowPageEntry
{
    public static uint Encode(uint page, uint pagesPerRow, uint levelOffset = 0)
        => (page % pagesPerRow) | ((page / pagesPerRow) << 10) |
            (levelOffset << 20) | 0x80000000u | (levelOffset == 0 ? 0x40000000u : 0u);
}
