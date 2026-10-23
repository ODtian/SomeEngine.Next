using SomeEngine.Job;
using SomeEngine.Navigation;

namespace SomeEngine.Assets.Schema;

/// <summary>Retains the grid asset's file while acquiring independently encoded support cells and transitions.</summary>
public sealed class NavigationGridTileSource : NavigationTileSource<NavigationGrid>
{
    private readonly AssetBlockSource? _source;
    private readonly IReadOnlyList<NavigationGridTileDescription> _tiles;
    private readonly NavigationGrid? _grid;
    public NavigationGridRegionGraph Graph { get; }
    public override ReadOnlyMemory<NavigationRegionTile> Directory => Graph.Tiles;
    internal NavigationGridTileSource(NavigationGridRegionGraph graph, IReadOnlyList<NavigationGridTileDescription> tiles,
        NavigationGrid? grid, AssetBlockSource? source) => (Graph, _tiles, _grid, _source) = (graph, tiles, grid, source);
    public override NavigationGridTileSource Retain() => new(Graph, _tiles, _grid, _source?.Retain());
    public override void Dispose() => _source?.Dispose();
    public override ValueTask<NavigationTileBytes> ReadAsync(NavigationTileCoordinate tile, CancellationToken cancellationToken = default)
    {
        if (_grid is { } grid)
        {
            foreach (NavigationGridTile value in grid.Tiles.Span)
                if (value.Tile == tile) return ValueTask.FromResult(new NavigationTileBytes([
                    NavigationArrayData.EncodeNow(value.Grid.Cells), NavigationArrayData.EncodeNow(value.Grid.Edges)]));
        }
        NavigationGridTileDescription description = _tiles.Single(value => value.X == tile.X && value.Z == tile.Z);
        return ReadChunksAsync(_source!, description.FirstChunkKey, cancellationToken);
    }
    public override NavigationGrid Decode(NavigationTileCoordinate tile, NavigationTileBytes bytes)
    {
        NavigationRegionTile description = Graph.Tiles.Span.Find(tile);
        return Decode(bytes, description.Bounds, Graph.CellSize, Graph.Shape, description.ContentVersion);
    }
    internal static async ValueTask<NavigationTileBytes> ReadChunksAsync(AssetBlockSource source, ulong first, CancellationToken cancellationToken)
    {
        using AssetBlockSource retained = source.Retain();
        return new([
            await retained.ReadBytesAsync(first, cancellationToken).ConfigureAwait(false),
            await retained.ReadBytesAsync(first + 1, cancellationToken).ConfigureAwait(false)]);
    }
    private static NavigationGrid Decode(NavigationTileBytes bytes, NavigationBounds bounds, float cellSize,
        NavigationGridShape shape, ulong version) => new(bounds, cellSize,
            NavigationArrayData.DecodeNow<NavigationGridCell>(bytes.Chunks.Span[0]),
            NavigationArrayData.DecodeNow<NavigationGridEdge>(bytes.Chunks.Span[1]), shape, version);

    internal static NavigationGrid DecodeOnWorker(NavigationTileBytes bytes, NavigationBounds bounds, float cellSize,
        NavigationGridShape shape, ulong version)
    {
        var result = new NavigationGrid[1];
        JobSystem.Schedule(new DecodeGrid(bytes, bounds, cellSize, shape, version, result),
            [JobResourceAccess.Read(bytes.Chunks.Span[0]), JobResourceAccess.Read(bytes.Chunks.Span[1]),
             JobResourceAccess.Write(result)]).Complete();
        return result[0];
    }
    private readonly struct DecodeGrid(NavigationTileBytes bytes, NavigationBounds bounds, float cellSize,
        NavigationGridShape shape, ulong version, NavigationGrid[] result) : IJob
    {
        public void Execute() => result[0] = Decode(bytes, bounds, cellSize, shape, version);
    }
}
