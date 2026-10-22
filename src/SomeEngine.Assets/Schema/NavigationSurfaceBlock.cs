using System.Numerics;
using SomeEngine.Navigation;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[BinarySerializable]
public sealed partial record NavigationSurfaceTileDescription
{
    public int X { get; init; }
    public int Z { get; init; }
    public Vec3 Minimum { get; init; } = new();
    public Vec3 Maximum { get; init; } = new();
    public float OriginX { get; init; }
    public float OriginZ { get; init; }
    public int Width { get; init; }
    public int Depth { get; init; }
    public ulong ColumnsChunkKey { get; init; }
    public ulong SpansChunkKey { get; init; }
}

/// <summary>Surface directory and independently acquired multi-layer tile data.</summary>
[AssetBlock("SomeEngine.Assets.Schema.NavigationSurfaceBlock"), BinarySerializable]
[BinaryFields(nameof(Minimum), nameof(Maximum), nameof(CellSize), nameof(CellHeight),
    nameof(TileSize), nameof(BorderSize), nameof(Tiles))]
public sealed partial class NavigationSurfaceBlock : AssetBlock
{
    public partial Vec3 Minimum { get; set; }
    public partial Vec3 Maximum { get; set; }
    public partial float CellSize { get; set; }
    public partial float CellHeight { get; set; }
    public partial int TileSize { get; set; }
    public partial float BorderSize { get; set; }
    public partial IList<NavigationSurfaceTileDescription> Tiles { get; set; }

    public void SetSurface(NavigationSurface surface)
    {
        lock (SyncRoot)
        {
            foreach (ulong key in Chunks.Keys.ToArray()) RemoveBytes(key);
            Minimum = ToValue(surface.Bounds.Minimum);
            Maximum = ToValue(surface.Bounds.Maximum);
            CellSize = surface.Settings.CellSize;
            CellHeight = surface.Settings.CellHeight;
            TileSize = surface.Settings.TileSize;
            BorderSize = surface.Settings.BorderSize;
            var descriptions = new NavigationSurfaceTileDescription[surface.Tiles.Length];
            for (int index = 0; index < descriptions.Length; index++)
            {
                NavigationSurfaceTile tile = surface.Tiles.Span[index];
                ulong columnsKey = (ulong)index * 2 + 1, spansKey = columnsKey + 1;
                descriptions[index] = new()
                {
                    X = tile.Coordinate.X, Z = tile.Coordinate.Z,
                    Minimum = ToValue(tile.InteriorBounds.Minimum), Maximum = ToValue(tile.InteriorBounds.Maximum),
                    OriginX = tile.Origin.X, OriginZ = tile.Origin.Y, Width = tile.Width, Depth = tile.Depth,
                    ColumnsChunkKey = columnsKey, SpansChunkKey = spansKey,
                };
                SetBytes(columnsKey, NavigationArrayData.Encode(tile.Columns));
                SetBytes(spansKey, NavigationArrayData.Encode(tile.Spans));
            }
            Tiles = descriptions;
        }
    }

    public async ValueTask<NavigationSurface> CreateAsync(CancellationToken cancellationToken = default)
    {
        using NavigationSurfaceBlock copy = Duplicate();
        var tiles = new NavigationSurfaceTile[copy.Tiles.Count];
        for (int index = 0; index < tiles.Length; index++)
            tiles[index] = await copy.ReadTileDataAsync(copy.Tiles[index], cancellationToken).ConfigureAwait(false);
        return new(new(ToVector(copy.Minimum), ToVector(copy.Maximum)), new()
        {
            CellSize = copy.CellSize, CellHeight = copy.CellHeight, TileSize = copy.TileSize, BorderSize = copy.BorderSize,
        }, tiles);
    }

    public async ValueTask<NavigationSurfaceTile> ReadTileAsync(int index, CancellationToken cancellationToken = default)
    {
        using NavigationSurfaceBlock copy = Duplicate();
        return await copy.ReadTileDataAsync(copy.Tiles[index], cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask WriteChunksAsync(AssetBlockWriter writer, CancellationToken cancellationToken)
    {
        using AssetBlockSource? source = RetainSource();
        foreach (NavigationSurfaceTileDescription tile in Tiles)
        {
            await WriteTileChunkAsync(writer, source, tile.ColumnsChunkKey, cancellationToken).ConfigureAwait(false);
            await WriteTileChunkAsync(writer, source, tile.SpansChunkKey, cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask WriteTileChunkAsync(AssetBlockWriter writer, AssetBlockSource? source,
        ulong key, CancellationToken cancellationToken)
    {
        if (Chunks.TryGetValue(key, out ReadOnlyMemory<byte> bytes))
        {
            writer.WriteChunk(key, bytes);
            return ValueTask.CompletedTask;
        }
        return writer.CopyChunkAsync(source!, key, cancellationToken);
    }

    private async ValueTask<NavigationSurfaceTile> ReadTileDataAsync(
        NavigationSurfaceTileDescription description, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> columns = await ReadBytesAsync(description.ColumnsChunkKey, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> spans = await ReadBytesAsync(description.SpansChunkKey, cancellationToken).ConfigureAwait(false);
        return new(new(description.X, description.Z), new(ToVector(description.Minimum), ToVector(description.Maximum)),
            new(description.OriginX, description.OriginZ), CellSize, description.Width, description.Depth,
            NavigationArrayData.Decode<NavigationSurfaceColumn>(columns), NavigationArrayData.Decode<NavigationSurfaceSpan>(spans));
    }

    private static Vec3 ToValue(Vector3 value) => new() { X = value.X, Y = value.Y, Z = value.Z };
    private static Vector3 ToVector(Vec3 value) => new(value.X, value.Y, value.Z);
}
