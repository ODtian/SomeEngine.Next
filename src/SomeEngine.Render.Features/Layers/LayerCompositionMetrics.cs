using System.Runtime.InteropServices;

namespace SomeEngine.Render.Features.Layers;

public readonly record struct LayerCompositionMetrics(ulong FrameIdentity, uint Fragments,
    uint MaxFragmentsPerPixel, uint BlocksUsed, uint FragmentOverflow, uint SplitTiles, uint SlowPathPixels)
{
    public static LayerCompositionMetrics Read(ReadOnlySpan<byte> bytes, ulong frameIdentity)
    {
        ReadOnlySpan<uint> values = MemoryMarshal.Cast<byte, uint>(bytes);
        return new(frameIdentity, values[0], values[4], Math.Min(values[1], values[7]), values[2], values[5], values[6]);
    }
}
