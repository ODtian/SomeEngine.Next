using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Resources.Geometry;

namespace SomeEngine.Assets.Pipeline;

// Builds the renderer's topology and byte views once per pair of Block versions.
// Attribute bytes remain opaque here; their importer and material shader define their format.
internal sealed class ClusterMeshData
{
    internal Mesh Mesh { get; }
    private readonly ClusterGeometry[] _clusters;
    private readonly ClusterVertexAttributes[] _attributes;
    private readonly Guid _vertexIdentity;
    private ClusterMeshData(Mesh mesh, ClusterGeometryBlock geometry, ClusterVertexAttributesBlock attributes)
    {
        Mesh = mesh;
        _clusters = geometry.Clusters.ToArray();
        _attributes = attributes.Clusters.ToArray();
        _vertexIdentity = geometry.VertexIdentity;
    }
    internal bool Matches(ClusterGeometryBlock geometry, ClusterVertexAttributesBlock attributes)
        => _vertexIdentity == geometry.VertexIdentity && _vertexIdentity == attributes.VertexIdentity
            && Mesh.VertexEncoding == geometry.PositionEncoding + "+" + attributes.Encoding
            && geometry.Clusters.SequenceEqual(_clusters) && attributes.Clusters.SequenceEqual(_attributes);
    private struct ClusterInfo
    {
        public Vector3 BoundMin, BoundMax;
        public Vector4 LODSphere;
        public float LODError;
        public uint PageIndex, ClusterStart;
        public int GroupId, ParentGroupId;
    }
    private readonly record struct BvhBounds(Vector3 Min, Vector3 Max, Vector3 Center, float Radius, float Error);

    internal static ClusterMeshData Build(ClusterGeometryBlock geometry, ClusterVertexAttributesBlock attributes)
    {
        if (geometry.VertexIdentity != attributes.VertexIdentity || geometry.Clusters.Count != attributes.Clusters.Count)
            throw new InvalidDataException("Cluster geometry and attributes must describe the same vertices.");
        string encoding = geometry.PositionEncoding + "+" + attributes.Encoding;
        List<ClusterInfo> infos = [];
        List<uint> sourceClusters = [];
        List<ReadOnlyMemory<byte>> pages = [];
        List<PackedCluster> clusters = [];
        List<ReadOnlyMemory<byte>> positions = [], attributeBytes = [];
        List<uint[]> materialNumbers = [];
        List<byte> indices = [];
        int size = MeshPageHeader.Size;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (int index in Enumerable.Range(0, geometry.Clusters.Count)
            .OrderBy(index => geometry.Clusters[index].ParentGroup)
            .ThenBy(index => geometry.Clusters[index].Group))
        {
            ClusterGeometry input = geometry.Clusters[index];
            ClusterVertexAttributes values = attributes.Clusters[index];
            ReadOnlyMemory<byte> position = input.PositionData.AsMemory();
            ReadOnlyMemory<byte> attribute = values.Data.AsMemory();
            // The draw representation can contain multiple material ranges for the same geometric cluster.
            var triangleRegions = input.TriangleRegions.IsEmpty ? input.TriangleMaterials : input.TriangleRegions;
            int[] triangles = Enumerable.Range(0, input.Indices.Length / 3)
                .OrderBy(triangle => triangleRegions[triangle]).ToArray();
            int[] materials = triangles.Select(triangle => triangleRegions[triangle]).Distinct().ToArray();
            for (int materialStart = 0; materialStart < materials.Length; materialStart += 3)
            {
                int[] selectedMaterials = materials.Skip(materialStart).Take(3).ToArray();
                int[] selected = triangles.Where(triangle => Array.IndexOf(selectedMaterials, triangleRegions[triangle]) >= 0).ToArray();
                int bytes = PackedCluster.SizeInBytes + 28 + AlignedSize(position.Length) + AlignedSize(attribute.Length) + selected.Length * 3;
                if (clusters.Count > 0 && (size + bytes > 131072 || indices.Count + selected.Length * 3 > ushort.MaxValue)) Flush();
                Vector3 boundMin = Vector(input.BoundMin);
                Vector3 boundMax = Vector(input.BoundMax);
                min = Vector3.Min(min, boundMin);
                max = Vector3.Max(max, boundMax);
                var packed = new PackedCluster
                {
                    VertexBlobSize = checked((uint)(28 + AlignedSize(position.Length))),
                    ExtensionOffset = uint.MaxValue,
                    LODCenter = Vector(input.SelfLodCenter),
                    LODRadius = input.SelfLodRadius,
                    BoundMin = boundMin,
                    BoundMax = boundMax,
                    TriangleStart = checked((ushort)indices.Count),
                    LODErrorHalf = BitConverter.HalfToUInt16Bits((Half)FiniteError(input.Error)),
                    GroupId = checked((short)input.Group),
                    PackedCounts = (uint)input.Vertices.Length | ((uint)selected.Length << 8) | ((uint)input.Level << 16),
                    MaterialTableOffset = uint.MaxValue,
                };
                uint[] batchWords = new uint[4];
                int batch = 0, rangeEnd = 0;
                for (int range = 0; range < selectedMaterials.Length; range++)
                {
                    packed.PackedMaterials |= ((uint)Math.Max(0, selectedMaterials[range]) & 255u) << (range * 8);
                    int count = 0, batchCount = 0;
                    HashSet<byte> vertices = [];
                    foreach (int triangle in selected.Where(triangle => triangleRegions[triangle] == selectedMaterials[range]))
                    {
                        byte a = input.Indices[triangle * 3], b = input.Indices[triangle * 3 + 1], c = input.Indices[triangle * 3 + 2];
                        int extra = (vertices.Contains(a) ? 0 : 1)
                            + (b != a && !vertices.Contains(b) ? 1 : 0)
                            + (c != a && c != b && !vertices.Contains(c) ? 1 : 0);
                        if (count > 0 && (count == 32 || vertices.Count + extra > 32)) CloseBatch();
                        vertices.Add(a);
                        vertices.Add(b);
                        vertices.Add(c);
                        count++;
                        indices.Add(a);
                        indices.Add(b);
                        indices.Add(c);
                        rangeEnd++;
                    }
                    CloseBatch();
                    PutBits(batchWords, range * 4, 4, (uint)batchCount);
                    if (range < 2) packed.PackedRanges |= (uint)rangeEnd << (range * 8);
                    void CloseBatch()
                    {
                        if (count == 0) return;
                        PutBits(batchWords, 12 + batch * 5, 5, (uint)(count - 1));
                        batch++;
                        batchCount++;
                        count = 0;
                        vertices.Clear();
                    }
                }
                if (selectedMaterials.Length == 1) packed.PackedRanges |= (uint)rangeEnd << 8;
                packed.VertReuseBatchWord0 = batchWords[0];
                packed.VertReuseBatchWord1 = batchWords[1];
                packed.VertReuseBatchWord2 = batchWords[2];
                packed.VertReuseBatchWord3 = batchWords[3];
                infos.Add(new()
                {
                    BoundMin = boundMin,
                    BoundMax = boundMax,
                    LODSphere = new(Vector(input.LodCenter), input.LodRadius),
                    LODError = FiniteError(input.ParentError),
                    PageIndex = (uint)pages.Count,
                    ClusterStart = (uint)clusters.Count,
                    GroupId = input.Level == 0 ? -1 : input.Group,
                    ParentGroupId = input.ParentGroup
                });
                clusters.Add(packed);
                sourceClusters.Add((uint)index);
                positions.Add(position);
                attributeBytes.Add(attribute);
                materialNumbers.Add(Enumerable.Range(0, 3).Select(range => (uint)Math.Max(0, selectedMaterials[Math.Min(range, selectedMaterials.Length - 1)])).ToArray());
                size += bytes;
            }
        }
        Flush();
        List<ClusterBVHNode> nodes = BuildBvh(infos);
        byte[] bvh = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(nodes)).ToArray();
        List<MeshPayloadPageDigest> descriptions = [];
        ulong offset = 0;
        foreach (ReadOnlyMemory<byte> page in pages)
        {
            MeshPageHeader header = MemoryMarshal.Read<MeshPageHeader>(page.Span);
            descriptions.Add(new()
            {
                Offset = offset,
                Length = (uint)page.Length,
                ClusterCount = header.ClusterCount,
                VertexEncodingHash = header.VertexEncodingHash,
                Sha256 = MeshIntegrity.Hash(page)
            });
            offset += (uint)page.Length;
        }
        uint[] lowest = infos.Where(info => info.LODError >= (float)Half.MaxValue).Select(info => info.PageIndex).Distinct().Order().ToArray();
        if (lowest.Length == 0) lowest = [(uint)(pages.Count - 1)];
        var bounds = new Bounds { Center = new() { X = (min.X + max.X) * .5f, Y = (min.Y + max.Y) * .5f, Z = (min.Z + max.Z) * .5f }, Radius = Vector3.Distance(min, max) * .5f };
        return new(Mesh.Create("Cluster geometry", encoding, descriptions, bvh, pages, bounds,
            lowestDetailPageIndices: lowest, clusterGeometryIndices: sourceClusters), geometry, attributes);

        void Flush()
        {
            if (clusters.Count == 0) return;
            int positionSize = positions.Sum(value => 28 + AlignedSize(value.Length)), attributeSize = attributeBytes.Sum(value => AlignedSize(value.Length));
            int positionStart = MeshPageHeader.Size + clusters.Count * PackedCluster.SizeInBytes;
            int indexStart = positionStart + positionSize, attributeStart = indexStart + indices.Count;
            int attributePadding = ((attributeStart + 3) & ~3) - attributeStart;
            byte[] page = new byte[attributeStart + attributePadding + attributeSize];
            MeshPageHeader header = new()
            {
                ClusterCount = (uint)clusters.Count,
                FirstCluster = (uint)(infos.Count - clusters.Count),
                TotalVertexCount = (uint)clusters.Sum(cluster => (int)(cluster.PackedCounts & 255)),
                TotalTriangleCount = (uint)(indices.Count / 3),
                VertexBlobBytes = (uint)positionSize,
                ClustersOffset = MeshPageHeader.Size,
                VertexBlobsOffset = (uint)positionStart,
                IndicesOffset = (uint)indexStart,
                ExtensionDataOffset = (uint)attributeStart,
                ExtensionDataSize = (uint)(attributePadding + attributeSize),
                VertexEncodingHash = VertexEncodingCompatibility.Hash(encoding)
            };
            MemoryMarshal.Write(page, in header);
            int positionOffset = positionStart, attributeOffset = attributeStart + attributePadding;
            for (int cluster = 0; cluster < clusters.Count; cluster++)
            {
                PackedCluster packed = clusters[cluster];
                packed.VertexBlobOffset = (uint)(positionOffset - positionStart);
                packed.MaterialTableOffset = (uint)(positionOffset + 16 + AlignedSize(positions[cluster].Length));
                MemoryMarshal.Write(page.AsSpan(MeshPageHeader.Size + cluster * PackedCluster.SizeInBytes), in packed);
                BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(positionOffset), 16);
                BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(positionOffset + 4), (uint)positions[cluster].Length);
                BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(positionOffset + 8), (uint)(attributeOffset - positionOffset));
                BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(positionOffset + 12), (uint)attributeBytes[cluster].Length);
                positions[cluster].Span.CopyTo(page.AsSpan(positionOffset + 16));
                for (int material = 0; material < 3; material++)
                    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan((int)packed.MaterialTableOffset + material * 4), materialNumbers[cluster][material]);
                attributeBytes[cluster].Span.CopyTo(page.AsSpan(attributeOffset));
                positionOffset += 28 + AlignedSize(positions[cluster].Length);
                attributeOffset += AlignedSize(attributeBytes[cluster].Length);
            }
            CollectionsMarshal.AsSpan(indices).CopyTo(page.AsSpan(indexStart));
            pages.Add(page);
            clusters.Clear();
            positions.Clear();
            attributeBytes.Clear();
            materialNumbers.Clear();
            indices.Clear();
            size = MeshPageHeader.Size;
        }
    }
    private static int AlignedSize(int length) => checked((length + 3) & ~3);
    private static Vector3 Vector(Vec3 value) => new(value.X, value.Y, value.Z);
    private static float FiniteError(float value) => (float)(Half)MathF.Min(value, (float)Half.MaxValue);
    private static void PutBits(uint[] words, int offset, int count, uint value)
    {
        int word = offset / 32, shift = offset % 32;
        words[word] |= value << shift;
        if (shift + count > 32) words[word + 1] |= value >> (32 - shift);
    }

    private static List<ClusterBVHNode> BuildBvh(List<ClusterInfo> clusters)
    {
        var nodes = new List<ClusterBVHNode>();
        if (clusters.Count == 0)
        {
            return nodes;
        }

        List<int> currentLevelIndices = CreateBvhLeafNodes(clusters, nodes);
        AddBvhInternalLevels(nodes, currentLevelIndices);
        return nodes;
    }

    private static List<int> CreateBvhLeafNodes(
        List<ClusterInfo> clusters,
        List<ClusterBVHNode> nodes)
    {
        var currentLevelIndices = new List<int>();
        int clusterIndex = 0;
        while (clusterIndex < clusters.Count)
        {
            int count = CountLeafClusters(clusters, clusterIndex);
            nodes.Add(CreateBvhLeafNode(clusters, clusterIndex, count));
            currentLevelIndices.Add(nodes.Count - 1);
            clusterIndex += count;
        }

        return currentLevelIndices;
    }

    private static int CountLeafClusters(List<ClusterInfo> clusters, int start)
    {
        uint currentPage = clusters[start].PageIndex;
        int currentParent = clusters[start].ParentGroupId;
        int currentGroup = clusters[start].GroupId;
        int count = 0;
        while (start + count < clusters.Count
            && clusters[start + count].PageIndex == currentPage
            && clusters[start + count].ParentGroupId == currentParent
            && clusters[start + count].GroupId == currentGroup
            && count < 128)
        {
            count++;
        }

        return count;
    }

    private static ClusterBVHNode CreateBvhLeafNode(
        List<ClusterInfo> clusters,
        int start,
        int count)
    {
        BvhBounds bounds = ComputeLeafBounds(clusters, start, count);
        ClusterInfo first = clusters[start];
        var node = new ClusterBVHNode
        {
            BoundMin = new Vector4(bounds.Min, first.GroupId + 1),
            BoundMax = new Vector4(bounds.Max, first.ParentGroupId + 1),
            LODSphere = first.LODSphere,
            LODError = first.LODError,
            ChildPointer = first.PageIndex,
            NodeType = 1,
        };
        node.SetLeafData(first.ClusterStart, (uint)count);
        return node;
    }

    private static BvhBounds ComputeLeafBounds(
        List<ClusterInfo> clusters,
        int start,
        int count)
    {
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        for (int offset = 0; offset < count; offset++)
        {
            ClusterInfo cluster = clusters[start + offset];
            min = Vector3.Min(min, cluster.BoundMin);
            max = Vector3.Max(max, cluster.BoundMax);
        }

        return new BvhBounds(min, max, Vector3.Zero, 0, 0);
    }

    private static void AddBvhInternalLevels(
        List<ClusterBVHNode> nodes,
        List<int> currentLevelIndices)
    {
        while (currentLevelIndices.Count > 1)
        {
            var nextLevelIndices = new List<int>();
            for (int start = 0; start < currentLevelIndices.Count; start += 16)
            {
                int count = Math.Min(16, currentLevelIndices.Count - start);
                nodes.Add(CreateBvhInternalNode(nodes, currentLevelIndices, start, count));
                nextLevelIndices.Add(nodes.Count - 1);
            }

            currentLevelIndices = nextLevelIndices;
        }
    }

    private static ClusterBVHNode CreateBvhInternalNode(
        List<ClusterBVHNode> nodes,
        List<int> currentLevelIndices,
        int start,
        int count)
    {
        BvhBounds bounds = ComputeInternalBounds(nodes, currentLevelIndices, start, count);
        return new ClusterBVHNode
        {
            ChildPointer = (uint)currentLevelIndices[start],
            ChildCount = (uint)count,
            NodeType = 0,
            BoundMin = new Vector4(bounds.Min, 0),
            BoundMax = new Vector4(bounds.Max, 0),
            LODSphere = new Vector4(bounds.Center.X, bounds.Center.Y, bounds.Center.Z, bounds.Radius),
            LODError = bounds.Error,
        };
    }

    private static BvhBounds ComputeInternalBounds(
        List<ClusterBVHNode> nodes,
        List<int> currentLevelIndices,
        int start,
        int count)
    {
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        float maxError = 0;
        Vector3 centerSum = Vector3.Zero;
        for (int offset = 0; offset < count; offset++)
        {
            ClusterBVHNode child = nodes[currentLevelIndices[start + offset]];
            min = Vector3.Min(min, ToVector3(child.BoundMin));
            max = Vector3.Max(max, ToVector3(child.BoundMax));
            maxError = Math.Max(maxError, child.LODError);
            centerSum += ToVector3(child.LODSphere);
        }

        Vector3 center = centerSum / count;
        float radius = ComputeInternalLodRadius(nodes, currentLevelIndices, start, count, center);
        return new BvhBounds(min, max, center, radius, maxError);
    }

    private static float ComputeInternalLodRadius(
        List<ClusterBVHNode> nodes,
        List<int> currentLevelIndices,
        int start,
        int count,
        Vector3 center)
    {
        float radius = 0;
        for (int offset = 0; offset < count; offset++)
        {
            ClusterBVHNode child = nodes[currentLevelIndices[start + offset]];
            float distance = Vector3.Distance(center, ToVector3(child.LODSphere)) + child.LODSphere.W;
            radius = Math.Max(radius, distance);
        }

        return radius;
    }

    private static Vector3 ToVector3(Vector4 value)
        => new(value.X, value.Y, value.Z);
}
