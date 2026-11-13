using System.Runtime.InteropServices;

namespace SomeEngine.Resources.Layers;

/// <summary>One visible layer intersection and its tile pixel, pass and draw order.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 12)]
public readonly record struct LayerFragment(float Depth, uint Identity, uint Packed)
{
    public ushort Pixel => (ushort)(Packed & 1023u);
    public byte PassIndex => (byte)((Packed >> 10) & 63u);
    public byte DrawOrder => (byte)(Packed >> 16);
    public byte Reserved => (byte)(Packed >> 24);
    public bool IsFront => (Packed & (1u << 24)) != 0;

    public static LayerFragment Create(float depth, uint identity, ushort pixel, byte passIndex,
        byte drawOrder, byte reserved = 0) =>
        new(depth, identity, ((uint)pixel & 1023u) | (((uint)passIndex & 63u) << 10) |
            ((uint)drawOrder << 16) | ((uint)reserved << 24));
}

/// <summary>Addresses the next block and the valid records in a tile's fragment chain.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 12)]
public readonly record struct LayerBlockHeader(uint Tile, uint Next, uint Count);
