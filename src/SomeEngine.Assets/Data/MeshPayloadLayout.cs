using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Assets.Data;

/// <summary>Validated location and upload metadata for one page inside a mesh payload chunk.</summary>
public readonly record struct MeshPayloadPage(
    long Offset,
    int Size,
    uint ClusterCount,
    uint VertexStride,
    Vector3 QuantOrigin,
    float QuantStep,
    ReadOnlyMemory<byte> Sha256);

/// <summary>Shared validation for materialized and range-streamed mesh payloads.</summary>
public static class MeshPayloadLayout
{
    public static MeshPayloadPage ReadPage(
        ReadOnlySpan<byte> headerBytes,
        long offset,
        long pageRegionLength)
    {
        if (offset < 0 || offset >= pageRegionLength)
            throw new ArgumentOutOfRangeException(nameof(offset));

        long remaining = checked(pageRegionLength - offset);
        if (remaining < MeshPageHeader.Size)
        {
            throw new InvalidDataException(
                $"Cluster page data ends with a truncated {remaining}-byte header fragment.");
        }
        if (headerBytes.Length < MeshPageHeader.Size)
            throw new ArgumentException($"A {MeshPageHeader.Size}-byte page header is required.", nameof(headerBytes));

        MeshPageHeader header = MemoryMarshal.Read<MeshPageHeader>(headerBytes);
        ulong expectedPositionsOffset = checked(
            (ulong)MeshPageHeader.Size + ((ulong)header.ClusterCount * GPUCluster.SizeInBytes));
        ulong expectedAttributesOffset = checked(
            expectedPositionsOffset + ((ulong)header.TotalVertexCount * 3 * sizeof(ushort)));
        ulong expectedIndicesOffset = checked(
            expectedAttributesOffset + ((ulong)header.TotalVertexCount * header.VertexStride));
        ulong pageSize = checked((ulong)header.IndicesOffset + ((ulong)header.TotalTriangleCount * 3));

        if (header.ClusterCount == 0)
        {
            throw new InvalidDataException(
                $"Cluster page at byte {offset} must contain at least one cluster record.");
        }
        if (header.ClustersOffset != MeshPageHeader.Size ||
            header.PositionsOffset != expectedPositionsOffset ||
            header.AttributesOffset != expectedAttributesOffset ||
            header.IndicesOffset != expectedIndicesOffset)
        {
            throw new InvalidDataException(
                $"Cluster page at byte {offset} has an inconsistent stream layout: " +
                $"clusters={header.ClustersOffset}, positions={header.PositionsOffset}, " +
                $"attributes={header.AttributesOffset}, indices={header.IndicesOffset}.");
        }
        if (!float.IsFinite(header.QuantOriginX) ||
            !float.IsFinite(header.QuantOriginY) ||
            !float.IsFinite(header.QuantOriginZ) ||
            !float.IsFinite(header.QuantStep) ||
            header.QuantStep <= 0)
        {
            throw new InvalidDataException(
                $"Cluster page at byte {offset} has invalid quantization parameters.");
        }
        if (pageSize < MeshPageHeader.Size || pageSize > MeshPageHeader.MaxPageSize)
        {
            throw new InvalidDataException(
                $"Cluster page at byte {offset} declares invalid size {pageSize}; valid pages are " +
                $"{MeshPageHeader.Size}..{MeshPageHeader.MaxPageSize} bytes.");
        }
        if (pageSize > checked((ulong)remaining))
        {
            throw new InvalidDataException(
                $"Cluster page at byte {offset} declares {pageSize} bytes, but only {remaining} remain before the BVH.");
        }

        return new MeshPayloadPage(
            offset,
            checked((int)pageSize),
            header.ClusterCount,
            header.VertexStride,
            new Vector3(header.QuantOriginX, header.QuantOriginY, header.QuantOriginZ),
            header.QuantStep,
            ReadOnlyMemory<byte>.Empty);
    }
}
