using System.Numerics;
using SomeEngine.Navigation;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

public readonly record struct NavigationMeshSelection(AssetId<NavigationProfileAsset> Profile, string Variant);

/// <summary>Resident mesh variants selected by their profile identity and build name.</summary>
[AssetBlock("SomeEngine.Assets.Schema.NavigationMeshBlock")]
public sealed partial class NavigationMeshBlock : AssetBlock, IAssetBlockWriter
{
    private readonly Dictionary<NavigationMeshSelection, NavigationMesh> _meshes = [];

    public NavigationMeshBlock() { }
    private NavigationMeshBlock(NavigationMeshBlock source, object? syncRoot = null) : base(source, syncRoot)
    {
        foreach (var pair in source._meshes) _meshes.Add(pair.Key, pair.Value);
    }

    public override NavigationMeshBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override NavigationMeshBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }

    public IReadOnlyList<NavigationMeshSelection> Variants
    {
        get { lock (SyncRoot) { RequireIdle(); return _meshes.Keys.ToArray(); } }
    }

    public void SetMesh(NavigationMeshSelection selection, NavigationMesh mesh)
    {
        lock (SyncRoot) { RequireIdle(); _meshes[selection] = mesh; MarkChanged(); }
    }

    /// <summary>Shares a complete immutable mesh acquired by the block's explicit read.</summary>
    public NavigationMesh Create(NavigationMeshSelection selection)
    {
        lock (SyncRoot) { RequireIdle(); return _meshes[selection]; }
    }

    public ValueTask<NavigationMesh> CreateAsync(NavigationMeshSelection selection, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Create(selection));
    }

    public override IEnumerable<Guid> References() => Variants.Select(selection => selection.Profile.Value);

    public static async ValueTask<NavigationMeshBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var data = new NavigationMeshData();
        await source.ReadIntoAsync(data, cancellationToken).ConfigureAwait(false);
        var result = new NavigationMeshBlock();
        foreach (NavigationMeshVariantData variant in data.Variants)
        {
            ulong key = variant.FirstChunkKey;
            ReadOnlyMemory<byte> positions = await source.ReadBytesAsync(key, cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<byte> vertices = await source.ReadBytesAsync(key + 1, cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<byte> polygons = await source.ReadBytesAsync(key + 2, cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<byte> portals = await source.ReadBytesAsync(key + 3, cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<byte> detailPositions = await source.ReadBytesAsync(key + 4, cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<byte> detailTriangles = await source.ReadBytesAsync(key + 5, cancellationToken).ConfigureAwait(false);
            var mesh = new NavigationMesh(
                NavigationArrayData.Decode<Vector3>(positions), NavigationArrayData.Decode<int>(vertices),
                NavigationArrayData.Decode<NavigationPolygon>(polygons), NavigationArrayData.Decode<NavigationPortal>(portals),
                NavigationArrayData.Decode<Vector3>(detailPositions), NavigationArrayData.Decode<NavigationDetailTriangle>(detailTriangles),
                variant.ContentVersion);
            result._meshes.Add(new(variant.Profile, variant.Variant), mesh);
        }
        result.AttachSource(source);
        return result;
    }

    public ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        KeyValuePair<NavigationMeshSelection, NavigationMesh>[] meshes;
        lock (SyncRoot) { RequireIdle(); meshes = _meshes.ToArray(); }
        var data = new NavigationMeshData();
        for (int index = 0; index < meshes.Length; index++)
            data.Variants.Add(new()
            {
                Profile = meshes[index].Key.Profile, Variant = meshes[index].Key.Variant,
                ContentVersion = meshes[index].Value.ContentVersion, FirstChunkKey = (ulong)index * 6 + 1,
            });
        writer.WriteMetadata(BlockTypeId, data);
        for (int index = 0; index < meshes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NavigationMesh mesh = meshes[index].Value;
            ulong key = data.Variants[index].FirstChunkKey;
            writer.WriteChunk(key, NavigationArrayData.Encode(mesh.Positions));
            writer.WriteChunk(key + 1, NavigationArrayData.Encode(mesh.PolygonVertices));
            writer.WriteChunk(key + 2, NavigationArrayData.Encode(mesh.Polygons));
            writer.WriteChunk(key + 3, NavigationArrayData.Encode(mesh.Portals));
            writer.WriteChunk(key + 4, NavigationArrayData.Encode(mesh.DetailPositions));
            writer.WriteChunk(key + 5, NavigationArrayData.Encode(mesh.DetailTriangles));
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
internal sealed partial class NavigationMeshData
{
    public List<NavigationMeshVariantData> Variants { get; set; } = [];
}

[BinarySerializable]
internal sealed partial class NavigationMeshVariantData
{
    public AssetId<NavigationProfileAsset> Profile { get; set; }
    public string Variant { get; set; } = "";
    public ulong ContentVersion { get; set; }
    public ulong FirstChunkKey { get; set; }
}
