using System.Runtime.InteropServices;

namespace SomeEngine.Transparency.Research;

/// <summary>One GPU allocation containing the research material's geometry and colors.</summary>
public sealed class TransparencyGeometry
{
    public TransparencyGeometry(TriangleHierarchy geometry)
    {
        BoundsOffset = Align(geometry.Coverage.Length * Marshal.SizeOf<TriangleCoverage>());
        CoverageOffset = Align(BoundsOffset + geometry.Bounds.Length * Marshal.SizeOf<TriangleBounds>());
        ColorsOffset = Align(CoverageOffset + geometry.CoverageRanges.Length * Marshal.SizeOf<CoverageRange>());
        Bytes = new byte[ColorsOffset + geometry.Colors.Length * Marshal.SizeOf<System.Numerics.Vector4>()];
        MemoryMarshal.AsBytes(geometry.Coverage.AsSpan()).CopyTo(Bytes);
        MemoryMarshal.AsBytes(geometry.Bounds.AsSpan()).CopyTo(Bytes.AsSpan(BoundsOffset));
        MemoryMarshal.AsBytes(geometry.CoverageRanges.AsSpan()).CopyTo(Bytes.AsSpan(CoverageOffset));
        MemoryMarshal.AsBytes(geometry.Colors.AsSpan()).CopyTo(Bytes.AsSpan(ColorsOffset));
    }

    public byte[] Bytes { get; }
    public int BoundsOffset { get; }
    public int CoverageOffset { get; }
    public int ColorsOffset { get; }

    public uint[] CreateSettings(int width, int height, TriangleHierarchy geometry, float colorError)
    {
        var settings = new uint[64];
        settings[0] = (uint)width;
        settings[1] = (uint)height;
        settings[2] = (uint)geometry.Bounds.Length;
        settings[3] = BitConverter.SingleToUInt32Bits(colorError);
        settings[4] = (uint)BoundsOffset;
        settings[5] = (uint)ColorsOffset;
        settings[6] = (uint)CoverageOffset;
        settings[7] = (uint)geometry.CoverageRanges.Length;
        return settings;
    }

    private static int Align(int bytes) => (bytes + 15) & ~15;
}
