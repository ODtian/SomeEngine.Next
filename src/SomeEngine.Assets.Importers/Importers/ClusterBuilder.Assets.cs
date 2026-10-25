using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Resources;
using SomeEngine.Job;
using System.Collections.Immutable;

namespace SomeEngine.Assets.Importers;

public static partial class ClusterBuilder
{
    public static MeshAsset ProcessRaw(Vector3[] rawPos, List<RawAttribute> rawAttributes,
        uint[] rawIndices, IReadOnlyList<MeshMaterialSlot> materialSlots, string name,
        IClusterVertexEncoder? vertexEncoder = null)
    {
        var values = new Dictionary<string, VertexStream>(StringComparer.Ordinal);
        double[] positions = new double[rawPos.Length * 3];
        for (int vertex = 0; vertex < rawPos.Length; vertex++)
        {
            positions[vertex * 3] = rawPos[vertex].X;
            positions[vertex * 3 + 1] = rawPos[vertex].Y;
            positions[vertex * 3 + 2] = rawPos[vertex].Z;
        }
        values.Add("POSITION", new VertexStream(3, positions));
        foreach (RawAttribute attribute in rawAttributes)
            if (attribute.Name is not "_MATERIAL_INDEX" and not "POSITION")
                values.Add(attribute.Name, new VertexStream(attribute.Dimension,
                    attribute.Data.Select(static value => (double)value).ToArray()));
        RawAttribute? materialIndices = rawAttributes.FirstOrDefault(static attribute => attribute.Name == "_MATERIAL_INDEX");
        var triangles = new SortedDictionary<int, List<uint>>();
        for (int triangle = 0; triangle < rawIndices.Length; triangle += 3)
        {
            int material = materialIndices is null ? 0 : (int)materialIndices.Data[rawIndices[triangle]];
            if (!triangles.TryGetValue(material, out List<uint>? indices)) triangles.Add(material, indices = []);
            indices.AddRange(rawIndices.AsSpan(triangle, 3));
        }
        uint[] sources = Enumerable.Range(0, rawPos.Length).Select(static value => (uint)value).ToArray();
        VertexPrimitive[] primitives = triangles.Select((pair, primitive) =>
            new VertexPrimitive(primitive, pair.Key, pair.Value.ToArray(), sources, values, [])).ToArray();
        using var streams = new VertexStreamsBlock(primitives, []);
        using ClusterGeometryBlock geometry = BuildGeometry(streams, default);
        using ClusterVertexAttributesBlock attributes = vertexEncoder is null or StandardClusterVertexEncoder
            ? ClusterVertexAttributesImporter.BuildAttributes(streams, geometry)
            : EncodeAttributes(rawPos, rawAttributes, geometry, vertexEncoder);
        Vector3 minimum = rawPos.Aggregate(Vector3.Min), maximum = rawPos.Aggregate(Vector3.Max);
        using var shape = new MeshBlock
        {
            Bounds = new Bounds { Center = CreateVec3((minimum + maximum) * 0.5f), Radius = (maximum - minimum).Length() * 0.5f },
            Regions = Enumerable.Range(0, materialSlots.Count).Select(static index => new MeshRegion { Name = $"region_{index}" }).ToArray(),
        };
        using var materials = new MeshMaterialsBlock
        {
            Materials = materialSlots.Select(static material => new AssetId<MaterialAsset>(material.MaterialGuid)).ToArray(),
        };
        var asset = new MeshAsset { Name = name };
        try
        {
            asset.SetBlock(shape);
            asset.SetBlock(geometry);
            asset.SetBlock(attributes);
            asset.SetBlock(materials);
            return asset;
        }
        catch { asset.Dispose(); throw; }
    }

    private static ClusterVertexAttributesBlock EncodeAttributes(Vector3[] positions, List<RawAttribute> attributes,
        ClusterGeometryBlock geometry, IClusterVertexEncoder encoder)
    {
        const int clustersPerBatch = 16;
        string encoding = encoder.Compatibility;
        RawAttribute[] streams = attributes.Where(static attribute => attribute.Name != "_MATERIAL_INDEX").ToArray();
        ClusterGeometry[] clusters = geometry.Clusters.ToArray();
        ClusterVertexSource[] sources = geometry.Vertices.ToArray();
        var results = new ClusterVertexAttributes[clusters.Length];
        int batchCount = (clusters.Length + clustersPerBatch - 1) / clustersPerBatch;
        var encoders = new IClusterVertexEncoder?[batchCount];
        for (int batch = 0; batch < batchCount; batch++)
            encoders[batch] = encoder.CreateBatchEncoder();
        JobSystem.ExecuteParallel(new EncodeAttributeBatches(positions, streams, clusters, sources,
            encoder, encoders, new object(), results, clustersPerBatch), batchCount, 1);
        return new ClusterVertexAttributesBlock
        {
            VertexIdentity = geometry.VertexIdentity, Encoding = encoding, Clusters = results,
        };
    }

    private readonly record struct EncodeAttributeBatches(Vector3[] Positions, RawAttribute[] Streams,
        ClusterGeometry[] Clusters, ClusterVertexSource[] Sources, IClusterVertexEncoder SharedEncoder,
        IClusterVertexEncoder?[] Encoders, object Gate, ClusterVertexAttributes[] Results, int ClustersPerBatch) : IJobParallelFor
    {
        public void Execute(int batch)
        {
            ClusterVertexSource[] sources = Sources;
            int end = Math.Min((batch + 1) * ClustersPerBatch, Clusters.Length);
            for (int index = batch * ClustersPerBatch; index < end; index++)
            {
                uint[] vertices = Clusters[index].Vertices.Select(vertex => sources[(int)vertex].SourceVertex).ToArray();
                if (Encoders[batch] is { } encoder)
                    Results[index] = Encode(encoder, vertices);
                else
                    lock (Gate) Results[index] = Encode(SharedEncoder, vertices);
            }
        }

        private ClusterVertexAttributes Encode(IClusterVertexEncoder encoder, uint[] vertices)
        {
            EncodedClusterVertices encoded = encoder.Encode(Positions, Streams, vertices);
            ImmutableArray<byte> bytes = [.. encoded.Blob.Span];
            return new() { Data = bytes, Attributes = [new() { Name = "Encoded", ComponentCount = 1, Data = bytes }] };
        }
    }

    /// <summary>Stores completed Cluster geometry with its neutral bounds, regions and material references.</summary>
    public static MeshAsset CreateAsset(Mesh mesh, IReadOnlyList<MeshMaterialSlot> materialSlots)
    {
        mesh = mesh.CaptureData();
        using var geometry = new MeshBlock { Bounds = mesh.Bounds, Regions = mesh.Regions.ToArray() };
        using var materials = new MeshMaterialsBlock
        {
            Materials = materialSlots.Select(static material => new AssetId<MaterialAsset>(material.MaterialGuid)).ToArray(),
        };
        using var clusters = new ClusterMeshBlock
        {
            VertexEncoding = mesh.VertexEncoding,
            LowestDetailPageIndices = mesh.LowestDetailPageIndices.ToArray(),
        };
        byte[] payload = new byte[checked((int)mesh.BvhOffset + mesh.BvhByteLength)];
        for (int page = 0; page < mesh.PageCount; page++)
        {
            var description = mesh.PageDigests[page];
            mesh.ReadPage(page, payload.AsSpan((int)description.Offset, (int)description.Length));
        }
        mesh.Bvh.Span.CopyTo(payload.AsSpan((int)mesh.BvhOffset));
        clusters.SetPayload(payload, mesh.BvhOffset);
        var asset = new MeshAsset { Name = mesh.Name ?? string.Empty };
        try
        {
            asset.SetBlock(geometry);
            asset.SetBlock(clusters);
            asset.SetBlock(materials);
            return asset;
        }
        catch { asset.Dispose(); throw; }
    }
}
