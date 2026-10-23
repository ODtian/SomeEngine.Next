using System.Buffers.Binary;
using System.Numerics;
using SomeEngine.Serialization;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Schema;

[Flags]
public enum ClusterDeform
{
    Static = 0,
    Skin = 1,
    Morph = 2,
}

/// <summary>The expanded stream row and original accessor row represented by a welded vertex.</summary>
[BinarySerializable, BinaryValue]
[BinaryFields(nameof(Primitive), nameof(Vertex), nameof(SourceVertex))]
public readonly partial record struct ClusterVertexSource(int Primitive, int Vertex, uint SourceVertex);

/// <summary>One cluster's quantized positions, local triangles and LOD group correspondence.</summary>
[BinarySerializable]
public sealed partial record ClusterGeometry
{
    public ClusterDeform Deform { get; init; }
    public ImmutableArray<uint> Vertices { get; init; } = [];
    public ImmutableArray<byte> Indices { get; init; } = [];
    public ImmutableArray<int> TriangleMaterials { get; init; } = [];
    public ImmutableArray<int> TriangleRegions { get; init; } = [];
    [BinaryIgnore] public SomeEngine.Resources.Vec3 PositionOrigin => new()
    {
        X = BinaryPrimitives.ReadSingleLittleEndian(PositionData.AsSpan()),
        Y = BinaryPrimitives.ReadSingleLittleEndian(PositionData.AsSpan()[4..]),
        Z = BinaryPrimitives.ReadSingleLittleEndian(PositionData.AsSpan()[8..]),
    };
    [BinaryIgnore] public float PositionStep => BinaryPrimitives.ReadSingleLittleEndian(PositionData.AsSpan()[12..]);
    public ImmutableArray<byte> PositionData { get; init; } = [];
    public SomeEngine.Resources.Vec3 BoundMin { get; init; } = new();
    public SomeEngine.Resources.Vec3 BoundMax { get; init; } = new();
    public int Level { get; init; }
    public float Error { get; init; }
    public float ParentError { get; init; }
    public int Group { get; init; }
    public int ParentGroup { get; init; }
    public SomeEngine.Resources.Vec3 Center { get; init; } = new();
    public float Radius { get; init; }
    public SomeEngine.Resources.Vec3 LodCenter { get; init; } = new();
    public float LodRadius { get; init; }
    public SomeEngine.Resources.Vec3 SelfLodCenter { get; init; } = new();
    public float SelfLodRadius { get; init; }

    public Vector3 DecodePosition(int vertex)
    {
        ReadOnlySpan<byte> data = PositionData.AsSpan().Slice(checked(16 + vertex * 6), 6);
        ReadOnlySpan<byte> header = PositionData.AsSpan();
        Vector3 origin = new(BinaryPrimitives.ReadSingleLittleEndian(header),
            BinaryPrimitives.ReadSingleLittleEndian(header[4..]), BinaryPrimitives.ReadSingleLittleEndian(header[8..]));
        return origin + new Vector3(BinaryPrimitives.ReadUInt16LittleEndian(data),
            BinaryPrimitives.ReadUInt16LittleEndian(data[2..]), BinaryPrimitives.ReadUInt16LittleEndian(data[4..])) * PositionStep;
    }
}

/// <summary>Cluster vertex identity, geometry and LOD, shared by the pipeline's per-vertex blocks.</summary>
[AssetBlock("SomeEngine.Assets.Schema.ClusterGeometryBlock"), BinarySerializable]
[BinaryFields(nameof(VertexIdentity), nameof(PositionEncoding), nameof(Vertices), nameof(Clusters))]
public sealed partial class ClusterGeometryBlock : AssetBlock
{
    public const string EncodingCompatibility = "someengine.cluster.positions.uint16.v1";
    public partial Guid VertexIdentity { get; set; }
    public partial string PositionEncoding { get; set; }
    public partial IList<ClusterVertexSource> Vertices { get; set; }
    public partial IList<ClusterGeometry> Clusters { get; set; }

    partial void Initialize()
    {
        VertexIdentity = Guid.NewGuid();
        PositionEncoding = EncodingCompatibility;
    }

    private MeshData _meshData = new();
    private sealed class MeshData { internal ClusterMeshData? Value; }
    partial void CopyFrom(ClusterGeometryBlock source) => _meshData = source._meshData;

    private Mesh GetMeshData(ClusterVertexAttributesBlock attributes)
    {
        lock (SyncRoot)
            lock (_meshData)
            {
                RequireIdle();
                if (_meshData.Value is null || !_meshData.Value.Matches(this, attributes))
                    _meshData.Value = ClusterMeshData.Build(this, attributes);
                return _meshData.Value.Mesh;
            }
    }

    /// <summary>Retains the two Block representations as opaque page data for GPU streaming.</summary>
    public ValueTask<MeshPayloadSource> OpenPayloadSourceAsync(ClusterVertexAttributesBlock attributes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(MeshPayloadSource.FromMesh(GetMeshData(attributes)));
    }

    /// <summary>Creates RenderWorld geometry with its lowest-detail pages ready.</summary>
    public ValueTask<Mesh> CreateAsync(MeshBlock shape, ClusterVertexAttributesBlock attributes, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Mesh data = GetMeshData(attributes);
        var ready = new ReadOnlyMemory<byte>[data.PageCount];
        foreach (uint index in data.LowestDetailPageIndices) data.TryGetPage((int)index, out ready[index]);
        return ValueTask.FromResult(Mesh.Create(name, data.VertexEncoding, data.PageDigests, data.Bvh, ready,
            shape.Bounds, shape.Regions.ToArray(), data.LowestDetailPageIndices, data.ClusterGeometryIndices));
    }

    public async ValueTask LoadPagesAsync(Mesh mesh, ClusterVertexAttributesBlock attributes, IEnumerable<int> pageIndices, CancellationToken cancellationToken = default)
    {
        using MeshPayloadSource source = await OpenPayloadSourceAsync(attributes, cancellationToken).ConfigureAwait(false);
        if (!source.Matches(mesh)) throw new InvalidOperationException("The mesh and selected Blocks describe different geometry.");
        ulong version = mesh.Revision;
        List<(int Index, ReadOnlyMemory<byte> Bytes)> pages = [];
        foreach (int index in pageIndices.Distinct())
            if (!mesh.TryGetPage(index, out _)) pages.Add((index, await source.ReadPageAsync(index, cancellationToken).ConfigureAwait(false)));
        cancellationToken.ThrowIfCancellationRequested();
        mesh.AddPages(pages, version);
    }
}
