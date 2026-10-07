using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Resources.Geometry;

/// <summary>
/// Packed Cluster-owned geometry metadata. Vertex bytes are opaque; only the selected Vertex
/// implementation interprets the blob referenced by <see cref="VertexBlobOffset"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PackedCluster
{
    //  0: byte offset relative to MeshPageHeader.VertexBlobsOffset
    public uint VertexBlobOffset;

    //  4: exact opaque blob size in bytes
    public uint VertexBlobSize;

    //  8: optional page-relative extension metadata (uint.MaxValue when absent)
    public uint ExtensionOffset;

    // 12: optional extension metadata size
    public uint ExtensionSize;

    // 16: object-space LOD sphere center
    public Vector3 LODCenter;

    // 28: object-space LOD sphere radius
    public float LODRadius;

    // 32: conservative object-space AABB
    public Vector3 BoundMin;

    // 44: conservative object-space AABB
    public Vector3 BoundMax;

    // 56: [LODErrorHalf:16][TriangleStart:16]
    public uint PackedLodErrorTriangleStart;

    // 60: [VertexCount:8][TriangleCount:8][LODLevel:8][Reserved:8]
    public uint PackedCounts;

    // 64: [GroupId(i16):16][MaximumEdgeLengthHalf:16]
    public uint PackedGroup;

    // 68: [mat0:8][mat1:8][mat2:8][Reserved:8]
    public uint PackedMaterials;

    // 72: [range0End:8][range1End:8][extendedRangeCount:16]
    public uint PackedRanges;

    // 76: page-relative extended material table, or uint.MaxValue for three inline ranges.
    public uint MaterialTableOffset;

    // 80..95: three-range inline batch data, or extended batch byte offset,
    // range count and dword count. Batch counts use four bits, triangle counts five.
    public uint VertReuseBatchWord0;
    public uint VertReuseBatchWord1;
    public uint VertReuseBatchWord2;
    public uint VertReuseBatchWord3;

    public const int SizeInBytes = 96;

    public readonly int MaterialRangeCount => MaterialTableOffset != uint.MaxValue
        ? checked((int)(PackedRanges >> 16))
        : (PackedRanges & 0xFFu) == ((PackedCounts >> 8) & 0xFFu) ? 1
        : ((PackedRanges >> 8) & 0xFFu) == ((PackedCounts >> 8) & 0xFFu) ? 2 : 3;

    public readonly uint GetMaterialRange(ReadOnlySpan<byte> page, int rangeIndex)
    {
        if (MaterialTableOffset != uint.MaxValue)
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                page.Slice(checked((int)MaterialTableOffset + rangeIndex * sizeof(uint)), sizeof(uint)));
        uint start = rangeIndex == 0 ? 0u : rangeIndex == 1 ? PackedRanges & 0xFFu : (PackedRanges >> 8) & 0xFFu;
        uint end = rangeIndex == 0 ? PackedRanges & 0xFFu : rangeIndex == 1
            ? (PackedRanges >> 8) & 0xFFu : (PackedCounts >> 8) & 0xFFu;
        return start | ((end - start) << 8) | (((PackedMaterials >> (rangeIndex * 8)) & 0xFFu) << 16);
    }

    public readonly uint GetVertexReuseBits(ReadOnlySpan<byte> page, int bitOffset, int bitCount)
    {
        int word = bitOffset >> 5;
        int shift = bitOffset & 31;
        uint value = ReadVertexReuseWord(page, word) >> shift;
        if (shift + bitCount > 32)
            value |= ReadVertexReuseWord(page, word + 1) << (32 - shift);
        return value & ((1u << bitCount) - 1u);
    }

    private readonly uint ReadVertexReuseWord(ReadOnlySpan<byte> page, int index)
        => MaterialTableOffset != uint.MaxValue
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                page.Slice(checked((int)VertReuseBatchWord0 + index * sizeof(uint)), sizeof(uint)))
            : index switch
            {
                0 => VertReuseBatchWord0, 1 => VertReuseBatchWord1,
                2 => VertReuseBatchWord2, 3 => VertReuseBatchWord3,
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };

    public ushort TriangleStart
    {
        readonly get => checked((ushort)(PackedLodErrorTriangleStart >> 16));
        set => PackedLodErrorTriangleStart =
            (PackedLodErrorTriangleStart & 0xFFFFu) | ((uint)value << 16);
    }

    public ushort LODErrorHalf
    {
        readonly get => (ushort)(PackedLodErrorTriangleStart & 0xFFFFu);
        set => PackedLodErrorTriangleStart =
            (PackedLodErrorTriangleStart & 0xFFFF0000u) | value;
    }

    public short GroupId
    {
        readonly get => unchecked((short)PackedGroup);
        set => PackedGroup = (PackedGroup & 0xFFFF0000u) | unchecked((ushort)value);
    }

    public ushort MaximumEdgeLengthHalf
    {
        readonly get => (ushort)(PackedGroup >> 16);
        set => PackedGroup = (PackedGroup & 0xFFFFu) | ((uint)value << 16);
    }
}
