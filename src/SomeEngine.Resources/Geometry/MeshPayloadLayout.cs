using System.Runtime.InteropServices;

namespace SomeEngine.Resources.Geometry;

/// <summary>Validated location and upload metadata for one page inside a mesh payload chunk.</summary>
public readonly record struct MeshPayloadPage(
    long Offset,
    int Size,
    uint ClusterCount,
    ulong VertexEncodingHash,
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
        ulong expectedVertexBlobsOffset = checked(
            (ulong)MeshPageHeader.Size + ((ulong)header.ClusterCount * PackedCluster.SizeInBytes));
        ulong expectedIndicesOffset = checked(
            expectedVertexBlobsOffset + header.VertexBlobBytes);
        ulong expectedExtensionOffset = checked(
            expectedIndicesOffset + ((ulong)header.TotalTriangleCount * 3));
        if (header.ExtensionDataSize != 0)
            expectedExtensionOffset = (expectedExtensionOffset + 3u) & ~3ul;
        ulong extensionOffset = header.ExtensionDataOffset;
        ulong pageSize = checked(extensionOffset + header.ExtensionDataSize);

        if (header.ClusterCount == 0 || header.ClusterCount > ((header.Flags & MeshPageHeader.RootPage) != 0 ? 64u : 256u))
        {
            throw new InvalidDataException(
                $"Cluster page at byte {offset} must contain at least one cluster record.");
        }
        if (header.ClustersOffset != MeshPageHeader.Size ||
            header.VertexBlobsOffset != expectedVertexBlobsOffset ||
            header.IndicesOffset != expectedIndicesOffset ||
            extensionOffset != expectedExtensionOffset)
        {
            throw new InvalidDataException(
                $"Cluster page at byte {offset} has an inconsistent stream layout: " +
                $"clusters={header.ClustersOffset}, vertexBlobs={header.VertexBlobsOffset}, " +
                $"indices={header.IndicesOffset}, extensions={header.ExtensionDataOffset}.");
        }
        if (header.VertexEncodingHash == 0 || (header.Flags & ~MeshPageHeader.RootPage) != 0)
        {
            throw new InvalidDataException(
                $"Cluster page at byte {offset} has invalid opaque vertex metadata.");
        }
        if (pageSize < MeshPageHeader.Size || pageSize > (ulong)((header.Flags & MeshPageHeader.RootPage) != 0 ? MeshPageHeader.RootPageSize : MeshPageHeader.MaxPageSize))
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
            header.VertexEncodingHash,
            ReadOnlyMemory<byte>.Empty);
    }
}
