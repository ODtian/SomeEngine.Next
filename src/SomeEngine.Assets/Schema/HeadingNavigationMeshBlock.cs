using System.Numerics;
using SomeEngine.Navigation;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Resident heading-mesh variants, including continuous permissions and the complete action graph.</summary>
[AssetBlock("SomeEngine.Assets.Schema.HeadingNavigationMeshBlock")]
public sealed partial class HeadingNavigationMeshBlock : AssetBlock, IAssetBlockWriter
{
    private readonly Dictionary<NavigationMeshSelection, HeadingNavigationMesh> _meshes = [];
    public HeadingNavigationMeshBlock() { }
    private HeadingNavigationMeshBlock(HeadingNavigationMeshBlock source, object? syncRoot = null) : base(source, syncRoot)
    {
        foreach (var pair in source._meshes) _meshes.Add(pair.Key, pair.Value);
    }

    public override HeadingNavigationMeshBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override HeadingNavigationMeshBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }
    public IReadOnlyList<NavigationMeshSelection> Variants
    {
        get { lock (SyncRoot) { RequireIdle(); return _meshes.Keys.ToArray(); } }
    }

    public void SetMesh(NavigationMeshSelection selection, HeadingNavigationMesh mesh)
    {
        lock (SyncRoot) { RequireIdle(); _meshes[selection] = mesh; MarkChanged(); }
    }

    public HeadingNavigationMesh Create(NavigationMeshSelection selection)
    {
        lock (SyncRoot) { RequireIdle(); return _meshes[selection]; }
    }

    public ValueTask<HeadingNavigationMesh> CreateAsync(NavigationMeshSelection selection, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Create(selection));
    }

    public override IEnumerable<Guid> References() => Variants.Select(selection => selection.Profile.Value);

    public static async ValueTask<HeadingNavigationMeshBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var data = new HeadingNavigationMeshData();
        await source.ReadIntoAsync(data, cancellationToken).ConfigureAwait(false);
        var result = new HeadingNavigationMeshBlock();
        async ValueTask<T[]> Read<T>(ulong key) where T : unmanaged =>
            NavigationArrayData.Decode<T>(await source.ReadBytesAsync(key, cancellationToken).ConfigureAwait(false));
        foreach (HeadingNavigationMeshVariantData variant in data.Variants)
        {
            ulong key = variant.FirstChunkKey;
            var geometry = new NavigationMesh(await Read<Vector3>(key), await Read<int>(key + 1),
                await Read<NavigationPolygon>(key + 2), await Read<NavigationPortal>(key + 3),
                await Read<Vector3>(key + 4), await Read<NavigationDetailTriangle>(key + 5), variant.ContentVersion);
            HeadingNavigationRegion[] regions = await Read<HeadingNavigationRegion>(key + 6);
            HeadingNavigationInterval[] headings = await Read<HeadingNavigationInterval>(key + 7);
            HeadingNavigationTranslation[] translations = await Read<HeadingNavigationTranslation>(key + 8);
            HeadingNavigationRotation[] rotations = await Read<HeadingNavigationRotation>(key + 9);
            var graph = new HeadingNavigationGraph(await Read<HeadingNavigationNode>(key + 10), await Read<HeadingNavigationEdge>(key + 11));
            result._meshes.Add(new(variant.Profile, variant.Variant),
                new(geometry, variant.ChannelCount, regions, headings, translations, rotations, graph));
        }
        result.AttachSource(source);
        return result;
    }

    public ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        KeyValuePair<NavigationMeshSelection, HeadingNavigationMesh>[] meshes;
        lock (SyncRoot) { RequireIdle(); meshes = _meshes.ToArray(); }
        var data = new HeadingNavigationMeshData();
        for (int index = 0; index < meshes.Length; index++)
            data.Variants.Add(new()
            {
                Profile = meshes[index].Key.Profile, Variant = meshes[index].Key.Variant,
                ChannelCount = meshes[index].Value.ChannelCount, ContentVersion = meshes[index].Value.ContentVersion,
                FirstChunkKey = (ulong)index * 12 + 1,
            });
        writer.WriteMetadata(BlockTypeId, data);
        for (int index = 0; index < meshes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HeadingNavigationMesh mesh = meshes[index].Value;
            NavigationMesh geometry = mesh.Geometry;
            ulong key = data.Variants[index].FirstChunkKey;
            writer.WriteChunk(key, NavigationArrayData.Encode(geometry.Positions));
            writer.WriteChunk(key + 1, NavigationArrayData.Encode(geometry.PolygonVertices));
            writer.WriteChunk(key + 2, NavigationArrayData.Encode(geometry.Polygons));
            writer.WriteChunk(key + 3, NavigationArrayData.Encode(geometry.Portals));
            writer.WriteChunk(key + 4, NavigationArrayData.Encode(geometry.DetailPositions));
            writer.WriteChunk(key + 5, NavigationArrayData.Encode(geometry.DetailTriangles));
            writer.WriteChunk(key + 6, NavigationArrayData.Encode(mesh.Regions));
            writer.WriteChunk(key + 7, NavigationArrayData.Encode(mesh.Headings));
            writer.WriteChunk(key + 8, NavigationArrayData.Encode(mesh.Translations));
            writer.WriteChunk(key + 9, NavigationArrayData.Encode(mesh.Rotations));
            writer.WriteChunk(key + 10, NavigationArrayData.Encode(mesh.Graph.Nodes));
            writer.WriteChunk(key + 11, NavigationArrayData.Encode(mesh.Graph.Edges));
        }
        return ValueTask.CompletedTask;
    }

    protected override void ReleaseContent()
    {
        _meshes.Clear();
        base.ReleaseContent();
    }
}

[BinarySerializable]
internal sealed partial class HeadingNavigationMeshData
{
    public List<HeadingNavigationMeshVariantData> Variants { get; set; } = [];
}

[BinarySerializable]
internal sealed partial class HeadingNavigationMeshVariantData
{
    public AssetId<NavigationProfileAsset> Profile { get; set; }
    public string Variant { get; set; } = "";
    public int ChannelCount { get; set; }
    public ulong ContentVersion { get; set; }
    public ulong FirstChunkKey { get; set; }
}
