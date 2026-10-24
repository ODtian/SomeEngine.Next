using System.Numerics;
using SomeEngine.Job;
using SomeEngine.Navigation;

namespace SomeEngine.Assets.Schema;

/// <summary>Retains the heading variant's file independently and decodes only explicitly requested tiles.</summary>
public sealed class HeadingNavigationMeshTileSource : NavigationTileSource<HeadingNavigationMesh>
{
    private readonly AssetBlockSource? _source;
    private readonly HeadingNavigationMeshVariantData _description;
    private readonly HeadingNavigationMesh? _mesh;
    public HeadingNavigationRegionGraph Graph { get; }
    public override ReadOnlyMemory<NavigationRegionTile> Directory => Graph.Tiles;
    internal HeadingNavigationMeshTileSource(HeadingNavigationRegionGraph graph, HeadingNavigationMeshVariantData description,
        HeadingNavigationMesh? mesh, AssetBlockSource? source) => (Graph, _description, _mesh, _source) = (graph, description, mesh, source);
    public override HeadingNavigationMeshTileSource Retain() => new(Graph, _description, _mesh, _source?.Retain());
    public override void Dispose() => _source?.Dispose();
    public override ValueTask<NavigationTileBytes> ReadAsync(NavigationTileCoordinate tile, CancellationToken cancellationToken = default)
    {
        if (_mesh is { } mesh)
        {
            HeadingNavigationMesh data = mesh.Tiles.ToArray().Single(value => value.Tile == tile).Mesh;
            NavigationMesh geometry = data.Geometry;
            return ValueTask.FromResult(new NavigationTileBytes([
                NavigationArrayData.EncodeNow(geometry.Positions), NavigationArrayData.EncodeNow(geometry.PolygonVertices),
                NavigationArrayData.EncodeNow(geometry.Polygons), NavigationArrayData.EncodeNow(geometry.Portals),
                NavigationArrayData.EncodeNow(geometry.DetailPositions), NavigationArrayData.EncodeNow(geometry.DetailTriangles),
                NavigationArrayData.EncodeNow(data.Regions), NavigationArrayData.EncodeNow(data.Headings),
                NavigationArrayData.EncodeNow(data.Translations), NavigationArrayData.EncodeNow(data.Rotations),
                NavigationArrayData.EncodeNow(data.Graph.Nodes), NavigationArrayData.EncodeNow(data.Graph.Edges)]));
        }
        return ReadChunksAsync(_source!, _description.Tiles.Single(value => value.X == tile.X && value.Z == tile.Z).FirstChunkKey, cancellationToken);
    }
    public override HeadingNavigationMesh Decode(NavigationTileCoordinate tile, NavigationTileBytes bytes) =>
        DecodeMesh(bytes, Graph.ChannelCount, Graph.Tiles.ToArray().Single(value => value.Tile == tile).ContentVersion);
    internal static async ValueTask<NavigationTileBytes> ReadChunksAsync(AssetBlockSource source, ulong first, CancellationToken cancellationToken)
    {
        using AssetBlockSource retained = source.Retain();
        var chunks = new ReadOnlyMemory<byte>[12];
        for (int index = 0; index < chunks.Length; index++) chunks[index] = await retained.ReadBytesAsync(first + (ulong)index, cancellationToken).ConfigureAwait(false);
        return new(chunks);
    }
    internal static HeadingNavigationMesh DecodeMesh(NavigationTileBytes bytes, int channels, ulong version)
    {
        ReadOnlySpan<ReadOnlyMemory<byte>> chunks = bytes.Chunks.Span;
        var geometry = new NavigationMesh(NavigationArrayData.DecodeNow<Vector3>(chunks[0]), NavigationArrayData.DecodeNow<int>(chunks[1]),
            NavigationArrayData.DecodeNow<NavigationPolygon>(chunks[2]), NavigationArrayData.DecodeNow<NavigationPortal>(chunks[3]),
            NavigationArrayData.DecodeNow<Vector3>(chunks[4]), NavigationArrayData.DecodeNow<NavigationDetailTriangle>(chunks[5]), version);
        return new(geometry, channels, NavigationArrayData.DecodeNow<HeadingNavigationRegion>(chunks[6]),
            NavigationArrayData.DecodeNow<HeadingNavigationInterval>(chunks[7]), NavigationArrayData.DecodeNow<HeadingNavigationTranslation>(chunks[8]),
            NavigationArrayData.DecodeNow<HeadingNavigationRotation>(chunks[9]), new(
                NavigationArrayData.DecodeNow<HeadingNavigationNode>(chunks[10]), NavigationArrayData.DecodeNow<HeadingNavigationEdge>(chunks[11])));
    }
    internal static HeadingNavigationMesh DecodeOnWorker(NavigationTileBytes bytes, int channels, ulong version)
    {
        var result = new HeadingNavigationMesh[1];
        var resources = new List<JobResourceAccess> { JobResourceAccess.Write(result) };
        foreach (ReadOnlyMemory<byte> chunk in bytes.Chunks.Span) resources.Add(JobResourceAccess.Read(chunk));
        JobSystem.Schedule(new DecodeData(bytes, channels, version, result), resources.ToArray()).Complete();
        return result[0];
    }
    internal static HeadingNavigationMesh CombineOnWorker(HeadingNavigationMeshTile[] tiles, HeadingNavigationRegionGraph graph)
    {
        var result = new HeadingNavigationMesh[1];
        var resources = new List<JobResourceAccess> { JobResourceAccess.Write(result), JobResourceAccess.Read(tiles), JobResourceAccess.Read(graph.Portals) };
        foreach (HeadingNavigationMeshTile tile in tiles)
        {
            NavigationMesh mesh = tile.Mesh.Geometry;
            resources.AddRange([JobResourceAccess.Read(mesh.Positions), JobResourceAccess.Read(mesh.PolygonVertices), JobResourceAccess.Read(mesh.Polygons),
                JobResourceAccess.Read(mesh.Portals), JobResourceAccess.Read(mesh.DetailPositions), JobResourceAccess.Read(mesh.DetailTriangles),
                JobResourceAccess.Read(tile.Mesh.Regions), JobResourceAccess.Read(tile.Mesh.Headings)]);
        }
        JobSystem.Schedule(new CombineData(tiles, graph, result), resources.ToArray()).Complete();
        return result[0];
    }
    private readonly struct DecodeData(NavigationTileBytes bytes, int channels, ulong version, HeadingNavigationMesh[] result) : IJob
    {
        public void Execute() => result[0] = DecodeMesh(bytes, channels, version);
    }
    private readonly struct CombineData(HeadingNavigationMeshTile[] tiles, HeadingNavigationRegionGraph graph, HeadingNavigationMesh[] result) : IJob
    {
        public void Execute() => result[0] = HeadingNavigationMesh.Combine(tiles, graph);
    }
}
