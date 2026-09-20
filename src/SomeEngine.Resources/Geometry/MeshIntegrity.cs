using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Resources.Geometry;

/// <summary>Geometry layout and authenticated page descriptions, without asset file access.</summary>
public static class MeshIntegrity
{
    internal static bool SamePages(IReadOnlyList<MeshPayloadPageDigest> first, IReadOnlyList<MeshPayloadPageDigest> second)
    {
        if (first.Count != second.Count) return false;
        for (int index = 0; index < first.Count; index++)
        {
            MeshPayloadPageDigest left = first[index];
            MeshPayloadPageDigest right = second[index];
            if (left.Offset != right.Offset || left.Length != right.Length || left.ClusterCount != right.ClusterCount
                || left.VertexEncodingHash != right.VertexEncodingHash || !left.Sha256.AsSpan().SequenceEqual(right.Sha256.AsSpan()))
                return false;
        }
        return true;
    }

    public static ImmutableArray<MeshPayloadPageDigest> Inspect(PageBuffer payload, long bvhOffset, string vertexEncoding)
    {
        ValidateBvh(payload.Length, bvhOffset);
        ulong encoding = VertexEncodingCompatibility.Hash(vertexEncoding);
        var pages = ImmutableArray.CreateBuilder<MeshPayloadPageDigest>();
        Span<byte> header = stackalloc byte[MeshPageHeader.Size];
        long offset = 0;
        while (offset < bvhOffset)
        {
            payload.Read(offset, header);
            MeshPayloadPage page = MeshPayloadLayout.ReadPage(header, offset, bvhOffset);
            if (page.VertexEncodingHash != encoding)
                throw new InvalidDataException($"Mesh page at byte {offset} uses a different vertex encoding.");
            using PageRange<byte> bytes = payload.Retain(offset, page.Size);
            pages.Add(new()
            {
                Offset = checked((ulong)offset), Length = checked((uint)page.Size),
                ClusterCount = page.ClusterCount, VertexEncodingHash = page.VertexEncodingHash,
                Sha256 = Hash(bytes),
            });
            offset = checked(offset + page.Size);
        }
        if (offset != bvhOffset || pages.Count == 0)
            throw new InvalidDataException("Mesh pages must fill the region before the BVH.");
        return pages.ToImmutable();
    }

    public static long Validate(IReadOnlyList<MeshPayloadPageDigest> pages, long bvhLength, string vertexEncoding)
    {
        if (pages.Count == 0) throw new InvalidDataException("Mesh contains no authenticated page descriptions.");
        if (bvhLength <= 0 || bvhLength % Unsafe.SizeOf<ClusterBVHNode>() != 0)
            throw new InvalidDataException("Mesh BVH must contain a whole number of non-empty nodes.");
        ulong encoding = VertexEncodingCompatibility.Hash(vertexEncoding);
        long offset = 0;
        foreach (MeshPayloadPageDigest page in pages)
        {
            if (page.Offset != checked((ulong)offset) || page.Length < MeshPageHeader.Size
                || page.Length > MeshPageHeader.MaxPageSize || page.ClusterCount == 0 || page.VertexEncodingHash != encoding)
                throw new InvalidDataException("Mesh page descriptions contain an invalid layout or vertex encoding.");
            if (page.Sha256.IsDefault || page.Sha256.Length != Digest256.Size)
                throw new InvalidDataException("Mesh pages must carry one SHA-256 digest.");
            offset = checked(offset + page.Length);
        }
        return offset;
    }

    public static void ValidatePage(PageBuffer bytes, MeshPayloadPageDigest description)
    {
        if (bytes.Length != description.Length) throw new InvalidDataException("Mesh page length differs from its description.");
        Span<byte> header = stackalloc byte[MeshPageHeader.Size];
        bytes.Read(0, header);
        MeshPayloadPage page = MeshPayloadLayout.ReadPage(header, checked((long)description.Offset),
            checked((long)description.Offset + bytes.Length));
        if (page.ClusterCount != description.ClusterCount || page.VertexEncodingHash != description.VertexEncodingHash)
            throw new InvalidDataException("Mesh page header differs from its authenticated description.");
        using PageRange<byte> content = bytes.Retain();
        if (!CryptographicOperations.FixedTimeEquals(Hash(content).AsSpan(), description.Sha256.AsSpan()))
            throw new InvalidDataException("Mesh page bytes do not match their authenticated SHA-256 digest.");
    }

    public static ImmutableArray<byte> Hash(PageRange<byte> bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (ReadOnlyMemory<byte> segment in bytes.Sequence) hash.AppendData(segment.Span);
        return ImmutableArray.Create(hash.GetHashAndReset());
    }

    private static void ValidateBvh(long length, long offset)
    {
        if (offset <= 0 || offset >= length || (length - offset) % Unsafe.SizeOf<ClusterBVHNode>() != 0)
            throw new InvalidDataException("Mesh BVH offset must split the payload into complete pages and whole BVH nodes.");
    }
}
