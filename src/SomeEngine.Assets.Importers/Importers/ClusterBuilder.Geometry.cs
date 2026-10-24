using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Geometry.Cluster;
using BuilderMeshlet = SomeEngine.Geometry.Cluster.ClusterMeshBuilder.ClusterMeshlet;

namespace SomeEngine.Assets.Importers;

public static partial class ClusterBuilder
{
    private const byte SourceVertexLockBit = ClusterMeshBuilder.SourceVertexLockBit;

    internal static ClusterGeometryBlock BuildGeometry(VertexStreamsBlock streams, CancellationToken cancellationToken)
    {
        List<VertexRow> vertices = [];
        var vertexIndices = new Dictionary<VertexRow, uint>(VertexRowComparer.Instance);
        var triangles = new List<uint>[4];
        var triangleSources = new List<ClusterTriangleSource>[4];
        for (int partition = 0; partition < triangles.Length; partition++)
        {
            triangles[partition] = [];
            triangleSources[partition] = [];
        }
        foreach (VertexPrimitive primitive in streams.Primitives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = new PrimitiveRows(primitive);
            uint[] remap = new uint[primitive.SourceVertices.Length];
            Array.Fill(remap, uint.MaxValue);
            // Only referenced rows take part in the pipeline's vertex identity.
            for (int corner = 0; corner < primitive.Indices.Length; corner++)
            {
                uint sourceVertex = primitive.Indices.Span[corner];
                if (corner % 3 == 0) triangleSources[(int)rows.Deform].Add(new(primitive.Material, primitive.SourcePrimitive));
                AddCorner(sourceVertex);
            }
            void AddCorner(uint sourceVertex)
            {
                uint vertex = remap[sourceVertex];
                if (vertex == uint.MaxValue)
                {
                    var row = new VertexRow(rows, (int)sourceVertex);
                    if (!vertexIndices.TryGetValue(row, out vertex))
                    {
                        vertex = (uint)vertices.Count;
                        vertexIndices.Add(row, vertex);
                        vertices.Add(row);
                    }
                    remap[sourceVertex] = vertex;
                }
                triangles[(int)rows.Deform].Add(vertex);
            }
        }

        Vector3[] positions = vertices.Select(ReadPosition).ToArray();
        byte[] vertexLocks = FindVertexLocks(vertices, positions, triangles);
        List<ClusterGeometry> clusters = [];
        int groupOffset = 0;
        for (int partition = 0; partition < triangles.Length; partition++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (triangles[partition].Count == 0) continue;
            // Working vertices distinguish triangle regions, while the persisted vertex identity stays welded.
            var workingVertices = new Dictionary<(uint Vertex, int Region), uint>();
            List<Vector3> workingPositions = [];
            List<uint> workingIdentities = [];
            List<ClusterTriangleSource> workingSources = [];
            List<uint> workingTriangles = [];
            List<byte> workingLocks = [];
            Dictionary<uint, int> firstRegions = [];
            HashSet<uint> regionBoundaries = [];
            for (int triangle = 0; triangle < triangleSources[partition].Count; triangle++)
            {
                ClusterTriangleSource source = triangleSources[partition][triangle];
                for (int corner = 0; corner < 3; corner++)
                {
                    uint identity = triangles[partition][triangle * 3 + corner];
                    if (firstRegions.TryGetValue(identity, out int region) && region != source.Region) regionBoundaries.Add(identity);
                    else firstRegions.TryAdd(identity, source.Region);
                    if (!workingVertices.TryGetValue((identity, source.Region), out uint vertex))
                    {
                        vertex = (uint)workingPositions.Count;
                        workingVertices.Add((identity, source.Region), vertex);
                        workingPositions.Add(positions[identity]);
                        workingIdentities.Add(identity);
                        workingSources.Add(source);
                        workingLocks.Add(vertexLocks[identity]);
                    }
                    workingTriangles.Add(vertex);
                }
            }
            for (int vertex = 0; vertex < workingIdentities.Count; vertex++)
                if (regionBoundaries.Contains(workingIdentities[vertex])) workingLocks[vertex] |= SourceVertexLockBit;
            Vector3[] partitionPositions = workingPositions.ToArray();
            List<BuilderMeshlet> meshlets = [];
            List<uint> indices = [];
            ClusterMeshBuilder.BuildLod(ClusterLodConfig.GetDefault() with
                { ClusterSpatial = true, SimplifyPermissive = false, SimplifyFallbackSloppy = false },
                partitionPositions, CollectionsMarshal.AsSpan(workingTriangles), new float[partitionPositions.Length],
                meshlets, indices, CollectionsMarshal.AsSpan(workingLocks));
            foreach (BuilderMeshlet meshlet in meshlets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                clusters.Add(EncodeGeometry(meshlet, CollectionsMarshal.AsSpan(indices), workingIdentities,
                    workingSources, partitionPositions, (ClusterDeform)partition, groupOffset));
            }
            groupOffset += meshlets.Max(meshlet => Math.Max(meshlet.GroupId, meshlet.ParentGroupId)) + 1;
        }
        return new ClusterGeometryBlock
        {
            Vertices = vertices.Select(row => new ClusterVertexSource(row.Rows.Primitive.SourcePrimitive,
                row.Vertex, row.Rows.Primitive.SourceVertices.Span[row.Vertex])).ToArray(),
            Clusters = clusters,
        };
    }

    private static SomeEngine.Resources.Vec3 CreateVec3(Vector3 value) => new() { X = value.X, Y = value.Y, Z = value.Z };

    private static Vector3 ReadPosition(VertexRow row)
    {
        VertexStream positions = row.Rows.Primitive.Attributes["POSITION"];
        ReadOnlySpan<double> values = positions.Values.Span.Slice(row.Vertex * positions.ComponentCount, 3);
        return new((float)values[0], (float)values[1], (float)values[2]);
    }

    private static byte[] FindVertexLocks(List<VertexRow> vertices, Vector3[] positions, List<uint>[] partitions)
    {
        byte[] locks = new byte[vertices.Count];
        var positionVertices = new Dictionary<Vector3, int>();
        for (int vertex = 0; vertex < vertices.Count; vertex++)
        {
            if (positionVertices.TryGetValue(positions[vertex], out int other))
                locks[vertex] = locks[other] = SourceVertexLockBit;
            else positionVertices.Add(positions[vertex], vertex);
        }
        foreach (List<uint> indices in partitions)
        {
            for (int triangle = 0; triangle < indices.Count; triangle += 3)
            {
                for (int edge = 0; edge < 3; edge++)
                {
                    int first = (int)indices[triangle + edge];
                    int second = (int)indices[triangle + (edge + 1) % 3];
                    // Conservatively preserve every changing influence/delta, with no source stream limit.
                    if (!SameDeformValues(vertices[first], vertices[second])) locks[first] = locks[second] = SourceVertexLockBit;
                }
            }
        }
        return locks;
    }

    private static bool SameDeformValues(VertexRow first, VertexRow second)
    {
        if (first.Rows.Deform == ClusterDeform.Static && second.Rows.Deform == ClusterDeform.Static) return true;
        if (first.Rows.Deform != second.Rows.Deform || first.Rows.Primitive.MorphTargets.Count != second.Rows.Primitive.MorphTargets.Count) return false;
        KeyValuePair<string, VertexStream>[] left = first.Rows.Streams;
        KeyValuePair<string, VertexStream>[] right = second.Rows.Streams;
        if (left.Length != right.Length) return false;
        for (int stream = 0; stream < left.Length; stream++)
        {
            string name = left[stream].Key;
            if (name.StartsWith("morph:", StringComparison.Ordinal) || name.StartsWith("JOINTS_", StringComparison.Ordinal)
                || name.StartsWith("WEIGHTS_", StringComparison.Ordinal))
            {
                if (name != right[stream].Key || !SameStreamValues(left[stream].Value, first.Vertex, right[stream].Value, second.Vertex)) return false;
            }
        }
        return true;
    }

    private static ClusterGeometry EncodeGeometry(BuilderMeshlet meshlet, ReadOnlySpan<uint> indices,
        List<uint> identities, List<ClusterTriangleSource> sources, Vector3[] positions, ClusterDeform deform, int groupOffset)
    {
        ReadOnlySpan<uint> triangles = indices.Slice(meshlet.IndicesOffset, meshlet.IndicesCount);
        (List<uint> vertices, byte[] localIndices) = CreateLocalIndices(triangles);
        int[] materials = new int[triangles.Length / 3];
        int[] regions = new int[triangles.Length / 3];
        for (int triangle = 0; triangle < materials.Length; triangle++)
        {
            ClusterTriangleSource source = sources[(int)triangles[triangle * 3]];
            materials[triangle] = source.Material;
            regions[triangle] = source.Region;
        }
        EncodedClusterPositions encoded = StandardClusterPositionEncoder.Encode(positions, CollectionsMarshal.AsSpan(vertices));
        return new()
        {
            Deform = deform, Vertices = [.. vertices.Select(vertex => identities[(int)vertex])], Indices = [.. localIndices], TriangleMaterials = [.. materials], TriangleRegions = [.. regions],
            PositionData = encoded.Data,
            BoundMin = CreateVec3(encoded.BoundMin), BoundMax = CreateVec3(encoded.BoundMax),
            Level = meshlet.Level, Error = meshlet.Error, ParentError = meshlet.ParentError,
            Group = meshlet.GroupId + groupOffset, ParentGroup = meshlet.ParentGroupId < 0 ? -1 : meshlet.ParentGroupId + groupOffset,
            Center = CreateVec3(meshlet.Center), Radius = meshlet.Radius + encoded.RadiusError,
            LodCenter = CreateVec3(meshlet.LodCenter), LodRadius = meshlet.LodRadius + encoded.RadiusError,
            SelfLodCenter = CreateVec3(meshlet.SelfLodCenter), SelfLodRadius = meshlet.SelfLodRadius + encoded.RadiusError,
        };
    }

    private static (List<uint> Vertices, byte[] Indices) CreateLocalIndices(ReadOnlySpan<uint> triangles)
    {
        Dictionary<uint, byte> local = [];
        List<uint> vertices = [];
        byte[] indices = new byte[triangles.Length];
        for (int corner = 0; corner < triangles.Length; corner++)
        {
            uint identity = triangles[corner];
            if (!local.TryGetValue(identity, out byte vertex))
            {
                vertex = checked((byte)vertices.Count);
                local.Add(identity, vertex);
                vertices.Add(identity);
            }
            indices[corner] = vertex;
        }
        return (vertices, indices);
    }

    private readonly record struct ClusterTriangleSource(int Material, int Region);

    private sealed class PrimitiveRows
    {
        internal VertexPrimitive Primitive { get; }
        internal ClusterDeform Deform { get; }
        internal KeyValuePair<string, VertexStream>[] Streams { get; }

        internal PrimitiveRows(VertexPrimitive primitive)
        {
            Primitive = primitive;
            Deform = (primitive.Attributes.Keys.Any(name => name.StartsWith("JOINTS_", StringComparison.Ordinal)
                || name.StartsWith("WEIGHTS_", StringComparison.Ordinal)) ? ClusterDeform.Skin : ClusterDeform.Static)
                | (primitive.MorphTargets.Count > 0 ? ClusterDeform.Morph : ClusterDeform.Static);
            List<KeyValuePair<string, VertexStream>> streams = [.. primitive.Attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal)];
            if (primitive.SourceTangents is not null) streams.Add(new("source:TANGENT", primitive.SourceTangents));
            for (int target = 0; target < primitive.MorphTargets.Count; target++)
            {
                foreach (var pair in primitive.MorphTargets[target].OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    streams.Add(new($"morph:{target}:{pair.Key}", pair.Value));
                if (target < primitive.SourceMorphTangents.Count && primitive.SourceMorphTangents[target] is { } tangents)
                    streams.Add(new($"morph:{target}:source:TANGENT", tangents));
            }
            Streams = streams.ToArray();
        }
    }

    private readonly record struct VertexRow(PrimitiveRows Rows, int Vertex);

    private sealed class VertexRowComparer : IEqualityComparer<VertexRow>
    {
        internal static VertexRowComparer Instance { get; } = new();
        public bool Equals(VertexRow left, VertexRow right)
        {
            if (left.Rows.Primitive.Material != right.Rows.Primitive.Material || left.Rows.Deform != right.Rows.Deform
                || left.Rows.Primitive.MorphTargets.Count != right.Rows.Primitive.MorphTargets.Count) return false;
            var first = left.Rows.Streams;
            var second = right.Rows.Streams;
            if (first.Length != second.Length) return false;
            for (int stream = 0; stream < first.Length; stream++)
                if (first[stream].Key != second[stream].Key || !SameStreamValues(first[stream].Value, left.Vertex, second[stream].Value, right.Vertex)) return false;
            return true;
        }

        public int GetHashCode(VertexRow row)
        {
            var hash = new HashCode();
            hash.Add(row.Rows.Primitive.Material);
            hash.Add(row.Rows.Deform);
            hash.Add(row.Rows.Primitive.MorphTargets.Count);
            foreach (var stream in row.Rows.Streams)
            {
                hash.Add(stream.Key, StringComparer.Ordinal);
                hash.Add(stream.Value.ComponentCount);
                foreach (double value in stream.Value.Values.Span.Slice(row.Vertex * stream.Value.ComponentCount, stream.Value.ComponentCount))
                    hash.Add(BitConverter.DoubleToInt64Bits(value));
            }
            return hash.ToHashCode();
        }
    }

    private static bool SameStreamValues(VertexStream left, int leftVertex, VertexStream right, int rightVertex)
    {
        if (left.ComponentCount != right.ComponentCount) return false;
        ReadOnlySpan<double> first = left.Values.Span.Slice(leftVertex * left.ComponentCount, left.ComponentCount);
        ReadOnlySpan<double> second = right.Values.Span.Slice(rightVertex * right.ComponentCount, right.ComponentCount);
        return MemoryMarshal.AsBytes(first).SequenceEqual(MemoryMarshal.AsBytes(second));
    }
}
