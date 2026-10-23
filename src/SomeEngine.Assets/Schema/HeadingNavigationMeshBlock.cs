using SomeEngine.Navigation;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Heading variants with independently retained entrance summaries and detailed tile sources.</summary>
[AssetBlock("SomeEngine.Assets.Schema.HeadingNavigationMeshBlock")]
public sealed partial class HeadingNavigationMeshBlock : AssetBlock, IAssetBlockWriter
{
    private readonly Dictionary<NavigationMeshSelection, MeshEntry> _variants = [];
    public HeadingNavigationMeshBlock() { }
    private HeadingNavigationMeshBlock(HeadingNavigationMeshBlock source, object? syncRoot = null) : base(source, syncRoot)
    {
        foreach (var pair in source._variants) _variants.Add(pair.Key, pair.Value);
    }
    public override HeadingNavigationMeshBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override HeadingNavigationMeshBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }
    public IReadOnlyList<NavigationMeshSelection> Variants
    {
        get { lock (SyncRoot) { RequireIdle(); return _variants.Keys.ToArray(); } }
    }
    public void SetMesh(NavigationMeshSelection selection, HeadingNavigationMesh mesh)
    {
        lock (SyncRoot) { RequireIdle(); _variants[selection] = new() { Mesh = mesh, Graph = mesh.RegionGraph }; MarkChanged(); }
    }
    public HeadingNavigationMesh Create(NavigationMeshSelection selection)
    {
        lock (SyncRoot) { RequireIdle(); return _variants[selection].Mesh ?? throw new InvalidOperationException("Acquire this variant with CreateAsync first."); }
    }
    public ValueTask<HeadingNavigationMesh> CreateAsync(NavigationMeshSelection selection, CancellationToken cancellationToken = default)
    {
        lock (SyncRoot)
        {
            RequireIdle();
            MeshEntry entry = _variants[selection];
            lock (entry)
            {
                if (entry.Mesh is { } mesh) return ValueTask.FromResult(mesh);
                entry.Read ??= ReadMeshAsync(entry, RetainSource()!);
                return new(entry.Read.WaitAsync(cancellationToken));
            }
        }
    }
    public HeadingNavigationMeshTileSource CreateTileSource(NavigationMeshSelection selection)
    {
        lock (SyncRoot)
        {
            RequireIdle(); MeshEntry entry = _variants[selection];
            return new(entry.Graph!, entry.Description, entry.Mesh, RetainSource());
        }
    }
    public override IEnumerable<Guid> References() => Variants.Select(selection => selection.Profile.Value);

    public static async ValueTask<HeadingNavigationMeshBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var data = new HeadingNavigationMeshData();
        await source.ReadIntoAsync(data, cancellationToken).ConfigureAwait(false);
        var result = new HeadingNavigationMeshBlock();
        foreach (HeadingNavigationMeshVariantData variant in data.Variants)
        {
            HeadingNavigationRegionGraph? graph = null;
            if (variant.GraphChunkKey != 0)
            {
                ulong key = variant.GraphChunkKey;
                graph = new(
                    NavigationArrayData.Decode<NavigationRegionTile>(await source.ReadBytesAsync(key, cancellationToken).ConfigureAwait(false)),
                    NavigationArrayData.Decode<HeadingNavigationRegionEntrance>(await source.ReadBytesAsync(key + 1, cancellationToken).ConfigureAwait(false)),
                    NavigationArrayData.Decode<HeadingNavigationRegionEdge>(await source.ReadBytesAsync(key + 2, cancellationToken).ConfigureAwait(false)),
                    NavigationArrayData.Decode<HeadingNavigationTilePortal>(await source.ReadBytesAsync(key + 3, cancellationToken).ConfigureAwait(false)),
                    variant.ChannelCount, variant.ContentVersion);
            }
            result._variants.Add(new(variant.Profile, variant.Variant), new() { Description = variant, Graph = graph });
        }
        result.AttachSource(source);
        return result;
    }

    private static async Task<HeadingNavigationMesh> ReadMeshAsync(MeshEntry entry, AssetBlockSource source)
    {
        using (source)
        {
            HeadingNavigationMesh mesh;
            if (entry.Graph is null)
                mesh = HeadingNavigationMeshTileSource.DecodeOnWorker(await HeadingNavigationMeshTileSource.ReadChunksAsync(source,
                    entry.Description.FirstChunkKey, default).ConfigureAwait(false), entry.Description.ChannelCount, entry.Description.ContentVersion);
            else
            {
                var tiles = new HeadingNavigationMeshTile[entry.Description.Tiles.Count];
                for (int index = 0; index < tiles.Length; index++)
                {
                    NavigationMeshTileDescription tile = entry.Description.Tiles[index];
                    HeadingNavigationMesh data = HeadingNavigationMeshTileSource.DecodeOnWorker(await HeadingNavigationMeshTileSource.ReadChunksAsync(source,
                        tile.FirstChunkKey, default).ConfigureAwait(false), entry.Description.ChannelCount, tile.ContentVersion);
                    tiles[index] = new(new(tile.X, tile.Z), data, entry.Graph.Tiles.Span[index].Bounds);
                }
                mesh = HeadingNavigationMeshTileSource.CombineOnWorker(tiles, entry.Graph);
            }
            lock (entry) entry.Mesh = mesh;
            return mesh;
        }
    }

    public async ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken = default)
    {
        NavigationMeshSelection[] selections = Variants.ToArray();
        var meshes = new HeadingNavigationMesh[selections.Length];
        for (int index = 0; index < meshes.Length; index++) meshes[index] = await CreateAsync(selections[index], cancellationToken).ConfigureAwait(false);
        var data = new HeadingNavigationMeshData();
        ulong next = 1;
        for (int index = 0; index < meshes.Length; index++)
        {
            HeadingNavigationMesh mesh = meshes[index];
            var variant = new HeadingNavigationMeshVariantData
            {
                Profile = selections[index].Profile, Variant = selections[index].Variant,
                ChannelCount = mesh.ChannelCount, ContentVersion = mesh.ContentVersion,
            };
            data.Variants.Add(variant);
            if (mesh.RegionGraph is null) { variant.FirstChunkKey = next; next += 12; }
            else
            {
                variant.GraphChunkKey = next; next += 4;
                foreach (HeadingNavigationMeshTile tile in mesh.Tiles.Span)
                {
                    variant.Tiles.Add(new() { X = tile.Tile.X, Z = tile.Tile.Z, ContentVersion = tile.Mesh.ContentVersion, FirstChunkKey = next });
                    next += 12;
                }
            }
        }
        writer.WriteMetadata(BlockTypeId, data);
        for (int index = 0; index < meshes.Length; index++)
        {
            HeadingNavigationMesh mesh = meshes[index];
            HeadingNavigationMeshVariantData variant = data.Variants[index];
            if (mesh.RegionGraph is not { } graph) WriteMesh(writer, variant.FirstChunkKey, mesh);
            else
            {
                ulong key = variant.GraphChunkKey;
                writer.WriteChunk(key, NavigationArrayData.Encode(graph.Tiles));
                writer.WriteChunk(key + 1, NavigationArrayData.Encode(graph.Entrances));
                writer.WriteChunk(key + 2, NavigationArrayData.Encode(graph.Edges));
                writer.WriteChunk(key + 3, NavigationArrayData.Encode(graph.Portals));
                for (int tile = 0; tile < mesh.Tiles.Length; tile++) WriteMesh(writer, variant.Tiles[tile].FirstChunkKey, mesh.Tiles.Span[tile].Mesh);
            }
        }
    }
    private static void WriteMesh(AssetBlockWriter writer, ulong key, HeadingNavigationMesh mesh)
    {
        NavigationMesh geometry = mesh.Geometry;
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
    protected override void ReleaseContent() { _variants.Clear(); base.ReleaseContent(); }
    private sealed class MeshEntry
    {
        public HeadingNavigationMeshVariantData Description = new();
        public HeadingNavigationRegionGraph? Graph;
        public HeadingNavigationMesh? Mesh;
        public Task<HeadingNavigationMesh>? Read;
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
    public ulong GraphChunkKey { get; set; }
    public List<NavigationMeshTileDescription> Tiles { get; set; } = [];
}
