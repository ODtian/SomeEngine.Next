using System.Numerics;
using SomeEngine.Job;
using SomeEngine.Navigation;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

public readonly record struct NavigationGridSelection(AssetId<NavigationProfileAsset> Profile, string Variant);

/// <summary>Profile-specific grid directories, entrance graphs and explicitly acquired detailed tiles.</summary>
[AssetBlock("SomeEngine.Assets.Schema.NavigationGridBlock")]
public sealed partial class NavigationGridBlock : AssetBlock, IAssetBlockWriter
{
    private readonly Dictionary<NavigationGridSelection, GridEntry> _grids = [];
    public NavigationGridBlock() { }
    private NavigationGridBlock(NavigationGridBlock source, object? syncRoot = null) : base(source, syncRoot)
    {
        foreach (var pair in source._grids) _grids.Add(pair.Key, pair.Value);
    }
    public override NavigationGridBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override NavigationGridBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }
    public IReadOnlyList<NavigationGridSelection> Variants
    {
        get { lock (SyncRoot) { RequireIdle(); return _grids.Keys.ToArray(); } }
    }
    public void SetGrid(NavigationGridSelection selection, NavigationGrid grid)
    {
        lock (SyncRoot) { RequireIdle(); _grids[selection] = new() { Grid = grid, Graph = grid.RegionGraph, Shape = grid.Shape }; MarkChanged(); }
    }
    public NavigationGrid Build(NavigationGridSelection selection, NavigationSurface surface, NavigationProfile profile,
        ReadOnlyMemory<NavigationGridConnection> connections = default, ulong contentVersion = 1, Scheduler? scheduler = null,
        NavigationGridFootprintMode footprintMode = NavigationGridFootprintMode.AllHeadings)
    {
        NavigationGrid grid = NavigationGridBuilder.Schedule(surface, profile, connections, contentVersion,
            scheduler, footprintMode: footprintMode).Complete().Grid;
        SetGrid(selection, grid);
        return grid;
    }
    public NavigationGrid Create(NavigationGridSelection selection)
    {
        lock (SyncRoot) { RequireIdle(); return _grids[selection].Grid ?? throw new InvalidOperationException("Acquire the grid with CreateAsync first."); }
    }
    public ValueTask<NavigationGrid> CreateAsync(NavigationGridSelection selection, CancellationToken cancellationToken = default)
    {
        lock (SyncRoot)
        {
            RequireIdle();
            GridEntry entry = _grids[selection];
            lock (entry)
            {
                if (entry.Grid is { } grid) return ValueTask.FromResult(grid);
                Task<NavigationGrid> read = entry.Read ??= ReadGridAsync(entry, RetainSource()!);
                if (read.IsCompleted) entry.Read = null;
                return new(read.WaitAsync(cancellationToken));
            }
        }
    }
    public NavigationGridTileSource CreateTileSource(NavigationGridSelection selection)
    {
        lock (SyncRoot)
        {
            RequireIdle();
            GridEntry entry = _grids[selection];
            return new(entry.Graph!, entry.Description.Tiles, entry.Grid, RetainSource());
        }
    }
    public override IEnumerable<Guid> References() => Variants.Select(selection => selection.Profile.Value);

    public static async ValueTask<NavigationGridBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var data = new NavigationGridData();
        await source.ReadIntoAsync(data, cancellationToken).ConfigureAwait(false);
        var result = new NavigationGridBlock();
        foreach (NavigationGridVariantData variant in data.Variants)
        {
            ulong footprintKey = variant.GraphChunkKey == 0 ? variant.FirstChunkKey + 2 : variant.GraphChunkKey + 4;
            Vector2[] footprint = NavigationArrayData.Decode<Vector2>(await source.ReadBytesAsync(footprintKey, cancellationToken).ConfigureAwait(false));
            var shape = new NavigationGridShape(footprint, variant.Height, variant.MaximumSlopeRadians,
                variant.MaximumStepHeight, (NavigationGridFootprintMode)variant.FootprintMode);
            NavigationGridRegionGraph? graph = null;
            if (variant.GraphChunkKey != 0)
            {
                ulong key = variant.GraphChunkKey;
                graph = new(new(ToVector(variant.Minimum), ToVector(variant.Maximum)), variant.CellSize, shape,
                    NavigationArrayData.Decode<NavigationRegionTile>(await source.ReadBytesAsync(key, cancellationToken).ConfigureAwait(false)),
                    NavigationArrayData.Decode<NavigationGridEntrance>(await source.ReadBytesAsync(key + 1, cancellationToken).ConfigureAwait(false)),
                    NavigationArrayData.Decode<NavigationGridRegionEdge>(await source.ReadBytesAsync(key + 2, cancellationToken).ConfigureAwait(false)),
                    NavigationArrayData.Decode<NavigationGridCrossing>(await source.ReadBytesAsync(key + 3, cancellationToken).ConfigureAwait(false)),
                    variant.ContentVersion, variant.CostVersion);
            }
            result._grids.Add(new(variant.Profile, variant.Variant), new() { Description = variant, Graph = graph, Shape = shape });
        }
        result.AttachSource(source);
        return result;
    }

    private static async Task<NavigationGrid> ReadGridAsync(GridEntry entry, AssetBlockSource source)
    {
        try
        {
            using (source)
            {
                NavigationGrid grid;
                if (entry.Graph is not { } graph)
                {
                    NavigationGridVariantData data = entry.Description;
                    NavigationTileBytes bytes = await NavigationGridTileSource.ReadChunksAsync(source, data.FirstChunkKey, default).ConfigureAwait(false);
                    grid = NavigationGridTileSource.DecodeOnWorker(bytes, new(ToVector(data.Minimum), ToVector(data.Maximum)),
                        data.CellSize, entry.Shape, data.ContentVersion);
                }
                else
                {
                    var tiles = new NavigationGridTile[entry.Description.Tiles.Count];
                    for (int index = 0; index < tiles.Length; index++)
                    {
                        NavigationGridTileDescription tile = entry.Description.Tiles[index];
                        NavigationRegionTile description = graph.Tiles.Span.Find(new(tile.X, tile.Z));
                        NavigationTileBytes bytes = await NavigationGridTileSource.ReadChunksAsync(source, tile.FirstChunkKey, default).ConfigureAwait(false);
                        tiles[index] = new(description.Tile, NavigationGridTileSource.DecodeOnWorker(bytes,
                            description.Bounds, graph.CellSize, graph.Shape, description.ContentVersion));
                    }
                    var result = new NavigationGrid[1];
                    var accesses = new List<JobResourceAccess>
                    {
                        JobResourceAccess.Read(graph.Tiles), JobResourceAccess.Read(graph.Entrances), JobResourceAccess.Read(graph.Edges),
                        JobResourceAccess.Read(graph.Crossings), JobResourceAccess.Write(result),
                    };
                    foreach (NavigationGridTile tile in tiles)
                    {
                        accesses.Add(JobResourceAccess.Read(tile.Grid.Cells));
                        accesses.Add(JobResourceAccess.Read(tile.Grid.Edges));
                    }
                    JobSystem.Schedule(new JoinGrid(graph, tiles, result), accesses.ToArray()).Complete();
                    grid = result[0];
                }
                lock (entry) entry.Grid = grid;
                return grid;
            }
        }
        finally { lock (entry) entry.Read = null; }
    }

    private readonly struct JoinGrid(NavigationGridRegionGraph graph, NavigationGridTile[] tiles, NavigationGrid[] result) : IJob
    {
        public void Execute() => result[0] = NavigationGrid.CombineTiles(graph, tiles);
    }

    public async ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken = default)
    {
        NavigationGridSelection[] selections = Variants.ToArray();
        var grids = new NavigationGrid[selections.Length];
        var data = new NavigationGridData();
        ulong next = 1;
        for (int index = 0; index < selections.Length; index++)
        {
            NavigationGrid grid = grids[index] = await CreateAsync(selections[index], cancellationToken).ConfigureAwait(false);
            var variant = new NavigationGridVariantData
            {
                Profile = selections[index].Profile, Variant = selections[index].Variant,
                Minimum = ToValue(grid.Bounds.Minimum), Maximum = ToValue(grid.Bounds.Maximum),
                CellSize = grid.CellSize, Height = grid.Shape.Height, MaximumSlopeRadians = grid.Shape.MaximumSlopeRadians,
                MaximumStepHeight = grid.Shape.MaximumStepHeight, FootprintMode = (int)grid.Shape.FootprintMode,
                ContentVersion = grid.ContentVersion, CostVersion = grid.RegionGraph?.CostVersion ?? 1,
            };
            if (grid.RegionGraph is null) { variant.FirstChunkKey = next; next += 3; }
            else
            {
                variant.GraphChunkKey = next; next += 5;
                foreach (NavigationGridTile tile in grid.Tiles.Span)
                {
                    variant.Tiles.Add(new() { X = tile.Tile.X, Z = tile.Tile.Z, FirstChunkKey = next });
                    next += 2;
                }
            }
            data.Variants.Add(variant);
        }
        writer.WriteMetadata(BlockTypeId, data);
        for (int index = 0; index < grids.Length; index++)
        {
            NavigationGrid grid = grids[index];
            NavigationGridVariantData variant = data.Variants[index];
            if (grid.RegionGraph is not { } graph)
            {
                WriteTile(writer, variant.FirstChunkKey, grid);
                writer.WriteChunk(variant.FirstChunkKey + 2, NavigationArrayData.Encode(grid.Shape.Footprint));
            }
            else
            {
                ulong key = variant.GraphChunkKey;
                writer.WriteChunk(key, NavigationArrayData.Encode(graph.Tiles));
                writer.WriteChunk(key + 1, NavigationArrayData.Encode(graph.Entrances));
                writer.WriteChunk(key + 2, NavigationArrayData.Encode(graph.Edges));
                writer.WriteChunk(key + 3, NavigationArrayData.Encode(graph.Crossings));
                writer.WriteChunk(key + 4, NavigationArrayData.Encode(graph.Shape.Footprint));
                for (int tile = 0; tile < variant.Tiles.Count; tile++) WriteTile(writer, variant.Tiles[tile].FirstChunkKey, grid.Tiles.Span[tile].Grid);
            }
        }
    }
    private static void WriteTile(AssetBlockWriter writer, ulong key, NavigationGrid grid)
    {
        writer.WriteChunk(key, NavigationArrayData.Encode(grid.Cells));
        writer.WriteChunk(key + 1, NavigationArrayData.Encode(grid.Edges));
    }
    protected override void ReleaseContent() { _grids.Clear(); base.ReleaseContent(); }
    private static Vec3 ToValue(Vector3 value) => new() { X = value.X, Y = value.Y, Z = value.Z };
    private static Vector3 ToVector(Vec3 value) => new(value.X, value.Y, value.Z);
    private sealed class GridEntry
    {
        public NavigationGridVariantData Description = new();
        public NavigationGridShape Shape;
        public NavigationGridRegionGraph? Graph;
        public NavigationGrid? Grid;
        public Task<NavigationGrid>? Read;
    }
}

[BinarySerializable]
internal sealed partial class NavigationGridData
{
    public List<NavigationGridVariantData> Variants { get; set; } = [];
}
[BinarySerializable]
internal sealed partial class NavigationGridVariantData
{
    public AssetId<NavigationProfileAsset> Profile { get; set; }
    public string Variant { get; set; } = "";
    public Vec3 Minimum { get; set; } = new();
    public Vec3 Maximum { get; set; } = new();
    public float CellSize { get; set; }
    public float Height { get; set; }
    public float MaximumSlopeRadians { get; set; }
    public float MaximumStepHeight { get; set; }
    public int FootprintMode { get; set; }
    public ulong ContentVersion { get; set; }
    public ulong CostVersion { get; set; } = 1;
    public ulong FirstChunkKey { get; set; }
    public ulong GraphChunkKey { get; set; }
    public List<NavigationGridTileDescription> Tiles { get; set; } = [];
}
[BinarySerializable]
internal sealed partial class NavigationGridTileDescription
{
    public int X { get; set; }
    public int Z { get; set; }
    public ulong FirstChunkKey { get; set; }
}
