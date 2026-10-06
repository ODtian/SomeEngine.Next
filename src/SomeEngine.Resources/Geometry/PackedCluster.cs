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

    // 72: [range0End:8][range1End:8][Reserved:16]
    public uint PackedRanges;

    // 76: undefined slow material path sentinel (must be uint.MaxValue)
    public uint MaterialTableOffset;

    // 80..95: inline vertex-reuse batch data. The first 12 bits store up to three
    // material-range batch counts; the remaining bits store 5-bit triangle counts.
    public uint VertReuseBatchWord0;
    public uint VertReuseBatchWord1;
    public uint VertReuseBatchWord2;
    public uint VertReuseBatchWord3;

    public const int SizeInBytes = 96;

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
