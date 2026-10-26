using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Resources;
using SomeEngine.Resources.Geometry;

namespace SomeEngine.Geometry.Cluster;

/// <summary>Encoded vertex bytes, topology and reduction metadata for one source cluster.</summary>
public sealed record EncodedClusterGeometry
{
    public required ReadOnlyMemory<byte> VertexBlob { get; init; }
    public required ReadOnlyMemory<byte> TriangleIndices { get; init; }
    public required IReadOnlyList<uint> TriangleMaterials { get; init; }
    public required int VertexCount { get; init; }
    public required Vector3 BoundMin { get; init; }
    public required Vector3 BoundMax { get; init; }
    public Vector3 Center { get; init; }
    public Vector4 LodSphere { get; init; }
    public Vector4 SelfLodSphere { get; init; }
    public double MaximumEdgeLength { get; init; }
    public float Error { get; init; }
    public float ParentError { get; init; } = float.MaxValue;
    public int Level { get; init; }
    public int Group { get; init; } = -1;
    public int ParentGroup { get; init; } = -1;
    public uint SourceClusterIndex { get; init; }
}

/// <summary>Shared page packing, material ranges and four-child reduction hierarchy.</summary>
public static partial class ClusterMeshPages
{
    private struct ClusterInfo
    {
        public Vector3 BoundMin, BoundMax;
        public Vector4 LODSphere;
        public float LODError, OwnError;
        public int Level, GroupId, ParentGroupId;
        public uint PageIndex, ClusterStart;
    }

    private sealed record MaterialTriangles(uint Material, int[] Triangles);
    private sealed record PackedGeometry(EncodedClusterGeometry Source, byte[] Indices,
        ClusterMaterialRange[] Ranges, uint[] Batches);

    public static Mesh Build(string name, string vertexEncoding, IReadOnlyList<EncodedClusterGeometry> geometry,
        Bounds? bounds = null, IReadOnlyList<MeshRegion>? regions = null)
    {
        EncodedClusterGeometry[] sorted = SortGeometry(geometry.Select(static source =>
            source.ParentGroup < 0 && source.Group >= 0 ? source with { ParentGroup = -source.Group - 2 } : source).ToArray());
        var inputs = new List<PackedGeometry>();
        foreach (EncodedClusterGeometry source in sorted)
        {
            MaterialTriangles[] materials = Enumerable.Range(0, source.TriangleIndices.Length / 3)
                .GroupBy(index => source.TriangleMaterials[index]).OrderByDescending(static group => group.Count()).ThenBy(static group => group.Key)
                .Select(static group => new MaterialTriangles(group.Key, group.ToArray())).ToArray();
            foreach (MaterialTriangles[] ranges in materials.Chunk(64))
                inputs.Add(PackMaterials(source, ranges));
        }

        var pages = new List<ReadOnlyMemory<byte>>();
        var infos = new List<ClusterInfo>(inputs.Count);
        var sources = new List<uint>(inputs.Count);
        var records = new List<PackedCluster>();
        var vertices = new List<byte>();
        var indices = new List<byte>();
        var materialData = new List<uint>();
        int vertexCount = 0, currentBytes = MeshPageHeader.Size;
        ulong encoding = VertexEncodingCompatibility.Hash(vertexEncoding);
        foreach (PackedGeometry input in inputs)
        {
            EncodedClusterGeometry source = input.Source;
            bool external = input.Ranges.Length > 3 || input.Ranges.Any(static range => range.MaterialIndex > 255u);
            int materialBytes = external ? (input.Ranges.Length * 2 + input.Batches.Length) * sizeof(uint) + 3 : 0;
            int bytes = PackedCluster.SizeInBytes + Align4(source.VertexBlob.Length) + input.Indices.Length + materialBytes;
            int capacity = pages.Count == 0 ? MeshPageHeader.RootPageSize : MeshPageHeader.MaxPageSize;
            int clusterLimit = pages.Count == 0 ? 64 : 256;
            if (currentBytes + bytes > capacity || records.Count == clusterLimit) Flush();
            capacity = pages.Count == 0 ? MeshPageHeader.RootPageSize : MeshPageHeader.MaxPageSize;
            if (currentBytes + bytes > capacity)
                throw new InvalidDataException("The encoded cluster exceeds the current mesh page payload capacity.");

            uint rangeOffset = uint.MaxValue, batchOffset = uint.MaxValue;
            if (external)
            {
                rangeOffset = checked((uint)materialData.Count * sizeof(uint));
                foreach (ClusterMaterialRange range in input.Ranges)
                {
                    materialData.Add(range.FirstTriangle | (range.TriangleCount << 8));
                    materialData.Add(range.MaterialIndex);
                }
                batchOffset = checked((uint)materialData.Count * sizeof(uint));
                materialData.AddRange(input.Batches);
            }
            uint packedMaterials = 0;
            for (int range = 0; range < Math.Min(3, input.Ranges.Length); range++)
                packedMaterials |= (input.Ranges[range].MaterialIndex & 255u) << (range * 8);
            uint firstEnd = input.Ranges[0].TriangleCount;
            ClusterMaterialRange second = input.Ranges[Math.Min(1, input.Ranges.Length - 1)];
            Half maximumEdge = (Half)source.MaximumEdgeLength;
            if ((double)maximumEdge < source.MaximumEdgeLength) maximumEdge = Half.BitIncrement(maximumEdge);
            var packed = new PackedCluster
            {
                VertexBlobOffset = checked((uint)vertices.Count),
                VertexBlobSize = checked((uint)source.VertexBlob.Length),
                ExtensionOffset = uint.MaxValue,
                LODCenter = new(source.SelfLodSphere.X, source.SelfLodSphere.Y, source.SelfLodSphere.Z),
                LODRadius = source.SelfLodSphere.W,
                BoundMin = source.BoundMin,
                BoundMax = source.BoundMax,
                LODErrorHalf = BitConverter.HalfToUInt16Bits((Half)source.Error),
                TriangleStart = checked((ushort)(indices.Count / 3)),
                MaximumEdgeLengthHalf = BitConverter.HalfToUInt16Bits(maximumEdge),
                PackedCounts = (uint)source.VertexCount | ((uint)(input.Indices.Length / 3) << 8) | ((uint)source.Level << 16),
                Flags = source.Level == 0 ? PackedCluster.FullLeaf : PackedCluster.StreamingLeaf,
                PackedMaterials = packedMaterials,
                PackedRanges = firstEnd | ((second.FirstTriangle + second.TriangleCount) << 8) |
                    (external ? (uint)input.Ranges.Length << 16 : 0u),
                MaterialTableOffset = rangeOffset,
                VertReuseBatchWord0 = external ? batchOffset : input.Batches[0],
                VertReuseBatchWord1 = external ? (uint)input.Ranges.Length : input.Batches[1],
                VertReuseBatchWord2 = external ? (uint)input.Batches.Length : input.Batches[2],
                VertReuseBatchWord3 = external ? 0u : input.Batches[3],
            };
            records.Add(packed);
            vertices.AddRange(source.VertexBlob.ToArray());
            while ((vertices.Count & 3) != 0) vertices.Add(0);
            indices.AddRange(input.Indices);
            infos.Add(new ClusterInfo
            {
                BoundMin = source.BoundMin, BoundMax = source.BoundMax,
                LODSphere = source.LodSphere, OwnError = source.Error, LODError = source.ParentError,
                Level = source.Level, GroupId = source.Level == 0 ? -1 : source.Group,
                ParentGroupId = source.ParentGroup,
                PageIndex = (uint)pages.Count, ClusterStart = (uint)records.Count - 1u,
            });
            sources.Add(source.SourceClusterIndex);
            vertexCount += source.VertexCount;
            currentBytes += bytes;
        }
        Flush();
        List<ClusterBVHNode> nodes = BuildBvh(infos);
        MeshClusterGroup[] groups = BuildMeshGroups(infos, nodes);
        uint[] lowest = groups.Where(static group => group.Parents.IsEmpty)
            .SelectMany(static group => Enumerable.Range((int)group.FirstPage, (int)group.PageCount).Select(static page => (uint)page))
            .Distinct().Order().ToArray();
        var descriptions = new List<MeshPayloadPageDigest>(pages.Count);
        ulong offset = 0;
        foreach (ReadOnlyMemory<byte> page in pages)
        {
            MeshPageHeader header = MemoryMarshal.Read<MeshPageHeader>(page.Span);
            descriptions.Add(new()
            {
                Offset = offset, Length = (uint)page.Length, ClusterCount = header.ClusterCount,
                VertexEncodingHash = encoding, Sha256 = MeshIntegrity.Hash(page),
            });
            offset += (uint)page.Length;
        }
        return Mesh.Create(name, vertexEncoding, descriptions,
            MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(nodes)).ToArray(), pages, bounds, regions, lowest, sources, groups);

        void Flush()
        {
            if (records.Count == 0) return;
            int vertexStart = MeshPageHeader.Size + records.Count * PackedCluster.SizeInBytes;
            int indexStart = vertexStart + vertices.Count;
            int extensionStart = materialData.Count == 0 ? indexStart + indices.Count : Align4(indexStart + indices.Count);
            byte[] page = new byte[extensionStart + materialData.Count * sizeof(uint)];
            var header = new MeshPageHeader
            {
                ClusterCount = (uint)records.Count, TotalVertexCount = (uint)vertexCount,
                TotalTriangleCount = (uint)(indices.Count / 3), VertexBlobBytes = (uint)vertices.Count,
                ClustersOffset = MeshPageHeader.Size, VertexBlobsOffset = (uint)vertexStart,
                IndicesOffset = (uint)indexStart, ExtensionDataOffset = (uint)extensionStart,
                ExtensionDataSize = (uint)(materialData.Count * sizeof(uint)), VertexEncodingHash = encoding,
                Flags = pages.Count == 0 ? MeshPageHeader.RootPage : 0u,
                FirstCluster = (uint)(infos.Count - records.Count),
            };
            MemoryMarshal.Write(page, in header);
            for (int index = 0; index < records.Count; index++)
            {
                PackedCluster cluster = records[index];
                if (cluster.MaterialTableOffset != uint.MaxValue)
                {
                    cluster.MaterialTableOffset += (uint)extensionStart;
                    cluster.VertReuseBatchWord0 += (uint)extensionStart;
                }
                MemoryMarshal.Write(page.AsSpan(MeshPageHeader.Size + index * PackedCluster.SizeInBytes), in cluster);
            }
            CollectionsMarshal.AsSpan(vertices).CopyTo(page.AsSpan(vertexStart));
            CollectionsMarshal.AsSpan(indices).CopyTo(page.AsSpan(indexStart));
            MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(materialData)).CopyTo(page.AsSpan(extensionStart));
            pages.Add(page);
            records.Clear(); vertices.Clear(); indices.Clear(); materialData.Clear();
            vertexCount = 0; currentBytes = MeshPageHeader.Size;
        }
    }

    private static PackedGeometry PackMaterials(EncodedClusterGeometry source, MaterialTriangles[] materials)
    {
        var indices = new List<byte>();
        var ranges = new List<ClusterMaterialRange>();
        var batches = new List<List<byte>>();
        foreach (MaterialTriangles material in materials)
        {
            uint start = (uint)indices.Count / 3u;
            var triangleCounts = new List<byte>();
            var used = new HashSet<byte>();
            byte triangleCount = 0;
            foreach (int triangle in material.Triangles)
            {
                ReadOnlySpan<byte> vertices = source.TriangleIndices.Span.Slice(triangle * 3, 3);
                int newVertices = 0;
                for (int corner = 0; corner < 3; corner++)
                    if (!used.Contains(vertices[corner]) && !vertices[..corner].Contains(vertices[corner])) newVertices++;
                if (used.Count + newVertices > 32 || triangleCount == 32)
                {
                    triangleCounts.Add(triangleCount); used.Clear(); triangleCount = 0;
                }
                foreach (byte vertex in vertices) { used.Add(vertex); indices.Add(vertex); }
                triangleCount++;
            }
            triangleCounts.Add(triangleCount);
            ranges.Add(new(start, (uint)material.Triangles.Length, material.Material));
            batches.Add(triangleCounts);
        }
        int batchCount = batches.Sum(static range => range.Count);
        int batchStart = Math.Max(3, ranges.Count) * 4;
        var words = new uint[Math.Max(4, (batchStart + batchCount * 5 + 31) / 32)];
        int batch = 0;
        for (int range = 0; range < ranges.Count; range++)
        {
            PutBits(words, range * 4, 4, (uint)batches[range].Count);
            foreach (byte triangles in batches[range]) PutBits(words, batchStart + batch++ * 5, 5, (uint)triangles - 1u);
        }
        return new(source, indices.ToArray(), ranges.ToArray(), words);
    }

    private static void PutBits(uint[] words, int offset, int count, uint value)
    {
        int word = offset >> 5, shift = offset & 31;
        words[word] |= value << shift;
        if (shift + count > 32) words[word + 1] |= value >> (32 - shift);
    }

    private static EncodedClusterGeometry[] SortGeometry(IReadOnlyList<EncodedClusterGeometry> geometry)
    {
        var groups = geometry.GroupBy(static cluster => cluster.ParentGroup).ToArray();
        Vector3 minimum = new(float.MaxValue), maximum = new(float.MinValue);
        foreach (var group in groups)
        {
            Vector4 sphere = group.First().LodSphere;
            Vector3 center = new(sphere.X, sphere.Y, sphere.Z);
            minimum = Vector3.Min(minimum, center); maximum = Vector3.Max(maximum, center);
        }
        Vector3 size = maximum - minimum;
        float extent = Math.Max(Math.Max(Math.Max(size.X, size.Y), size.Z), 1e-20f);
        return groups.OrderByDescending(static group => group.Max(static cluster => cluster.Level))
            .ThenBy(group => Morton(group.First()))
            .SelectMany(static group => group.OrderBy(static cluster => cluster.Center.X + cluster.Center.Y + cluster.Center.Z))
            .ToArray();

        uint Morton(EncodedClusterGeometry cluster)
        {
            Vector3 center = (new Vector3(cluster.LodSphere.X, cluster.LodSphere.Y, cluster.LodSphere.Z) - minimum) / extent + new Vector3(.5f / 1023);
            uint code = Spread((uint)Math.Clamp(center.X * 1024, 0, 1023)) * 4u |
                Spread((uint)Math.Clamp(center.Y * 1024, 0, 1023)) * 2u | Spread((uint)Math.Clamp(center.Z * 1024, 0, 1023));
            return (cluster.Level & 1) != 0 ? code ^ uint.MaxValue : code;
        }
        static uint Spread(uint value)
        {
            value &= 1023u;
            value = (value | (value << 16)) & 0x030000ffu;
            value = (value | (value << 8)) & 0x0300f00fu;
            value = (value | (value << 4)) & 0x030c30c3u;
            return (value | (value << 2)) & 0x09249249u;
        }
    }

    private static int Align4(int length) => (length + 3) & ~3;
}
