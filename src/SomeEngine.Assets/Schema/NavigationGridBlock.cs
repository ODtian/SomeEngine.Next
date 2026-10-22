using System.Numerics;
using SomeEngine.Job;
using SomeEngine.Navigation;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

public readonly record struct NavigationGridSelection(AssetId<NavigationProfileAsset> Profile, string Variant);

/// <summary>Completed profile-specific grid variants built from shared surface data.</summary>
[AssetBlock("SomeEngine.Assets.Schema.NavigationGridBlock")]
public sealed partial class NavigationGridBlock : AssetBlock, IAssetBlockWriter
{
    private readonly Dictionary<NavigationGridSelection, NavigationGrid> _grids = [];

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
        lock (SyncRoot) { RequireIdle(); _grids[selection] = grid; MarkChanged(); }
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
        lock (SyncRoot) { RequireIdle(); return _grids[selection]; }
    }

    public ValueTask<NavigationGrid> CreateAsync(NavigationGridSelection selection, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Create(selection));
    }

    public override IEnumerable<Guid> References() => Variants.Select(selection => selection.Profile.Value);

    public static async ValueTask<NavigationGridBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var data = new NavigationGridData();
        await source.ReadIntoAsync(data, cancellationToken).ConfigureAwait(false);
        var result = new NavigationGridBlock();
        foreach (NavigationGridVariantData variant in data.Variants)
        {
            ulong key = variant.FirstChunkKey;
            ReadOnlyMemory<byte> cells = await source.ReadBytesAsync(key, cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<byte> edges = await source.ReadBytesAsync(key + 1, cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<byte> footprint = await source.ReadBytesAsync(key + 2, cancellationToken).ConfigureAwait(false);
            var grid = new NavigationGrid(new(ToVector(variant.Minimum), ToVector(variant.Maximum)), variant.CellSize,
                NavigationArrayData.Decode<NavigationGridCell>(cells), NavigationArrayData.Decode<NavigationGridEdge>(edges),
                new(NavigationArrayData.Decode<Vector2>(footprint), variant.Height, variant.MaximumSlopeRadians,
                    variant.MaximumStepHeight, (NavigationGridFootprintMode)variant.FootprintMode), variant.ContentVersion);
            result._grids.Add(new(variant.Profile, variant.Variant), grid);
        }
        result.AttachSource(source);
        return result;
    }

    public ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        KeyValuePair<NavigationGridSelection, NavigationGrid>[] grids;
        lock (SyncRoot) { RequireIdle(); grids = _grids.ToArray(); }
        var data = new NavigationGridData();
        for (int index = 0; index < grids.Length; index++)
        {
            NavigationGrid grid = grids[index].Value;
            data.Variants.Add(new()
            {
                Profile = grids[index].Key.Profile, Variant = grids[index].Key.Variant,
                Minimum = ToValue(grid.Bounds.Minimum), Maximum = ToValue(grid.Bounds.Maximum),
                CellSize = grid.CellSize, Height = grid.Shape.Height, MaximumSlopeRadians = grid.Shape.MaximumSlopeRadians,
                MaximumStepHeight = grid.Shape.MaximumStepHeight, FootprintMode = (int)grid.Shape.FootprintMode,
                ContentVersion = grid.ContentVersion, FirstChunkKey = (ulong)index * 3 + 1,
            });
        }
        writer.WriteMetadata(BlockTypeId, data);
        for (int index = 0; index < grids.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NavigationGrid grid = grids[index].Value;
            ulong key = data.Variants[index].FirstChunkKey;
            writer.WriteChunk(key, NavigationArrayData.Encode(grid.Cells));
            writer.WriteChunk(key + 1, NavigationArrayData.Encode(grid.Edges));
            writer.WriteChunk(key + 2, NavigationArrayData.Encode(grid.Shape.Footprint));
        }
        return ValueTask.CompletedTask;
    }

    protected override void ReleaseContent() { _grids.Clear(); base.ReleaseContent(); }
    private static Vec3 ToValue(Vector3 value) => new() { X = value.X, Y = value.Y, Z = value.Z };
    private static Vector3 ToVector(Vec3 value) => new(value.X, value.Y, value.Z);
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
    public ulong FirstChunkKey { get; set; }
}
