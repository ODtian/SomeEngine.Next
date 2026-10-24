using System.Numerics;
using SomeEngine.Navigation;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

public readonly record struct NavigationMeshSelection(AssetId<NavigationProfileAsset> Profile, string Variant);

/// <summary>Small variant directories and entrance graphs with explicitly acquired detailed meshes.</summary>
[AssetBlock("SomeEngine.Assets.Schema.NavigationMeshBlock")]
public sealed partial class NavigationMeshBlock : AssetBlock, IAssetBlockWriter
{
    private readonly Dictionary<NavigationMeshSelection, MeshEntry> _variants = [];
    public NavigationMeshBlock() { }
    private NavigationMeshBlock(NavigationMeshBlock source, object? syncRoot = null) : base(source, syncRoot)
    {
        foreach (var pair in source._variants) _variants.Add(pair.Key, pair.Value);
    }
    public override NavigationMeshBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override NavigationMeshBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }
    public IReadOnlyList<NavigationMeshSelection> Variants
    {
        get { lock (SyncRoot) { RequireIdle(); return _variants.Keys.ToArray(); } }
    }
    public void SetMesh(NavigationMeshSelection selection, NavigationMesh mesh)
    {
        lock (SyncRoot) { RequireIdle(); _variants[selection] = new() { Mesh = mesh, Graph = mesh.RegionGraph }; MarkChanged(); }
    }
    /// <summary>Shares the complete mesh previously acquired with SetMesh or CreateAsync.</summary>
    public NavigationMesh Create(NavigationMeshSelection selection)
    {
        lock (SyncRoot)
        {
            RequireIdle();
            return _variants[selection].Mesh ?? throw new InvalidOperationException("Acquire this variant with CreateAsync first.");
        }
    }
    public ValueTask<NavigationMesh> CreateAsync(NavigationMeshSelection selection, CancellationToken cancellationToken = default)
    {
        lock (SyncRoot)
        {
            RequireIdle();
            MeshEntry entry = _variants[selection];
            lock (entry)
            {
                if (entry.Mesh is { } mesh) return ValueTask.FromResult(mesh);
                Task<NavigationMesh> read = entry.Read ??= ReadMeshAsync(entry, RetainSource()!);
                if (read.IsCompleted) entry.Read = null;
                return new(read.WaitAsync(cancellationToken));
            }
        }
    }
    public NavigationMeshTileSource CreateTileSource(NavigationMeshSelection selection)
    {
        lock (SyncRoot)
        {
            RequireIdle();
            MeshEntry entry = _variants[selection];
            return new(entry.Graph!, entry.Description, entry.Mesh, RetainSource());
        }
    }
    public override IEnumerable<Guid> References() => Variants.Select(selection => selection.Profile.Value);

    public static async ValueTask<NavigationMeshBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var data = new NavigationMeshData();
        await source.ReadIntoAsync(data, cancellationToken).ConfigureAwait(false);
        var result = new NavigationMeshBlock();
        foreach (NavigationMeshVariantData variant in data.Variants)
        {
            NavigationRegionGraph? graph = null;
            if (variant.GraphChunkKey != 0)
            {
                ulong key = variant.GraphChunkKey;
                graph = new(
                    NavigationArrayData.Decode<NavigationRegionTile>(await source.ReadBytesAsync(key, cancellationToken).ConfigureAwait(false)),
                    NavigationArrayData.Decode<NavigationRegionEntrance>(await source.ReadBytesAsync(key + 1, cancellationToken).ConfigureAwait(false)),
                    NavigationArrayData.Decode<NavigationRegionEdge>(await source.ReadBytesAsync(key + 2, cancellationToken).ConfigureAwait(false)),
                    NavigationArrayData.Decode<NavigationRegionPort>(await source.ReadBytesAsync(key + 3, cancellationToken).ConfigureAwait(false)), variant.ContentVersion);
            }
            result._variants.Add(new(variant.Profile, variant.Variant), new() { Description = variant, Graph = graph });
        }
        result.AttachSource(source);
        return result;
    }

    private static async Task<NavigationMesh> ReadMeshAsync(MeshEntry entry, AssetBlockSource source)
    {
        try
        {
            using (source)
            {
                NavigationMesh mesh;
                if (entry.Graph is null)
                    mesh = NavigationMeshTileSource.DecodeMeshOnWorker(await NavigationMeshTileSource.ReadChunksAsync(source,
                        entry.Description.FirstChunkKey, default).ConfigureAwait(false), entry.Description.ContentVersion, entry.Description.BakedRadius);
                else
                {
                    var tiles = new NavigationMeshTile[entry.Description.Tiles.Count];
                    for (int index = 0; index < tiles.Length; index++)
                    {
                        NavigationMeshTileDescription tile = entry.Description.Tiles[index];
                        NavigationTileBytes bytes = await NavigationMeshTileSource.ReadChunksAsync(source, tile.FirstChunkKey, default).ConfigureAwait(false);
                        tiles[index] = new(new(tile.X, tile.Z), NavigationMeshTileSource.DecodeMeshOnWorker(bytes, tile.ContentVersion, tile.BakedRadius));
                    }
                    NavigationTilePortal[] portals = NavigationArrayData.Decode<NavigationTilePortal>(
                        await source.ReadBytesAsync(entry.Description.GraphChunkKey + 4).ConfigureAwait(false));
                    mesh = NavigationMeshTileSource.Join(tiles, portals, entry.Graph, entry.Description.ContentVersion, entry.Description.BakedRadius);
                }
                lock (entry) entry.Mesh = mesh;
                return mesh;
            }
        }
        finally { lock (entry) entry.Read = null; }
    }

    public async ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken = default)
    {
        NavigationMeshSelection[] selections = Variants.ToArray();
        var meshes = new NavigationMesh[selections.Length];
        for (int index = 0; index < selections.Length; index++)
            meshes[index] = await CreateAsync(selections[index], cancellationToken).ConfigureAwait(false);
        var data = new NavigationMeshData();
        ulong next = 1;
        for (int index = 0; index < meshes.Length; index++)
        {
            NavigationMesh mesh = meshes[index];
            var variant = new NavigationMeshVariantData
            {
                Profile = selections[index].Profile, Variant = selections[index].Variant, ContentVersion = mesh.ContentVersion, BakedRadius = mesh.BakedRadius,
            };
            data.Variants.Add(variant);
            if (mesh.RegionGraph is null) { variant.FirstChunkKey = next; next += 6; }
            else
            {
                variant.GraphChunkKey = next; next += 5;
                foreach (NavigationMeshTile tile in mesh.Tiles.Span)
                {
                    variant.Tiles.Add(new() { X = tile.Tile.X, Z = tile.Tile.Z, ContentVersion = tile.Mesh.ContentVersion,
                        BakedRadius = tile.Mesh.BakedRadius, FirstChunkKey = next });
                    next += 6;
                }
            }
        }
        writer.WriteMetadata(BlockTypeId, data);
        for (int index = 0; index < meshes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NavigationMesh mesh = meshes[index];
            NavigationMeshVariantData variant = data.Variants[index];
            if (mesh.RegionGraph is not { } graph) WriteMesh(writer, variant.FirstChunkKey, mesh);
            else
            {
                ulong key = variant.GraphChunkKey;
                writer.WriteChunk(key, NavigationArrayData.Encode(graph.Tiles));
                writer.WriteChunk(key + 1, NavigationArrayData.Encode(graph.Entrances));
                writer.WriteChunk(key + 2, NavigationArrayData.Encode(graph.Edges));
                writer.WriteChunk(key + 3, NavigationArrayData.Encode(graph.Ports));
                writer.WriteChunk(key + 4, NavigationArrayData.Encode<NavigationTilePortal>(NavigationMeshTileSource.Crossings(mesh)));
                for (int tile = 0; tile < variant.Tiles.Count; tile++)
                    WriteMesh(writer, variant.Tiles[tile].FirstChunkKey, mesh.Tiles.Span[tile].Mesh);
            }
        }
    }

    private static void WriteMesh(AssetBlockWriter writer, ulong key, NavigationMesh mesh)
    {
        writer.WriteChunk(key, NavigationArrayData.Encode(mesh.Positions));
        writer.WriteChunk(key + 1, NavigationArrayData.Encode(mesh.PolygonVertices));
        writer.WriteChunk(key + 2, NavigationArrayData.Encode(mesh.Polygons));
        writer.WriteChunk(key + 3, NavigationArrayData.Encode(mesh.Portals));
        writer.WriteChunk(key + 4, NavigationArrayData.Encode(mesh.DetailPositions));
        writer.WriteChunk(key + 5, NavigationArrayData.Encode(mesh.DetailTriangles));
    }
    protected override void ReleaseContent() { _variants.Clear(); base.ReleaseContent(); }
    private sealed class MeshEntry
    {
        public NavigationMeshVariantData Description = new();
        public NavigationRegionGraph? Graph;
        public NavigationMesh? Mesh;
        public Task<NavigationMesh>? Read;
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
    public ulong GraphChunkKey { get; set; }
    public float BakedRadius { get; set; }
    public List<NavigationMeshTileDescription> Tiles { get; set; } = [];
}
[BinarySerializable]
internal sealed partial class NavigationMeshTileDescription
{
    public int X { get; set; }
    public int Z { get; set; }
    public ulong ContentVersion { get; set; }
    public float BakedRadius { get; set; }
    public ulong FirstChunkKey { get; set; }
}
