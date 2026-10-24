using System.Numerics;
using SomeEngine.Job;
using SomeEngine.Navigation;

namespace SomeEngine.Assets.Schema;

/// <summary>Independently retains an asset file and reads one detailed mesh tile at a time.</summary>
public sealed class NavigationMeshTileSource : NavigationTileSource<NavigationMesh>
{
    private readonly AssetBlockSource? _source;
    private readonly NavigationMeshVariantData _description;
    private readonly NavigationMesh? _mesh;
    public NavigationRegionGraph Graph { get; }
    public override ReadOnlyMemory<NavigationRegionTile> Directory => Graph.Tiles;
    internal NavigationMeshTileSource(NavigationRegionGraph graph, NavigationMeshVariantData description,
        NavigationMesh? mesh, AssetBlockSource? source) => (Graph, _description, _mesh, _source) = (graph, description, mesh, source);
    public override NavigationMeshTileSource Retain() => new(Graph, _description, _mesh, _source?.Retain());
    public override void Dispose() => _source?.Dispose();
    public override ValueTask<NavigationTileBytes> ReadAsync(NavigationTileCoordinate tile, CancellationToken cancellationToken = default)
    {
        if (_mesh is { } mesh)
        {
            NavigationMesh data = mesh.Tiles.Span.Find(tile);
            return ValueTask.FromResult(new NavigationTileBytes([
                NavigationArrayData.EncodeNow(data.Positions), NavigationArrayData.EncodeNow(data.PolygonVertices),
                NavigationArrayData.EncodeNow(data.Polygons), NavigationArrayData.EncodeNow(data.Portals),
                NavigationArrayData.EncodeNow(data.DetailPositions), NavigationArrayData.EncodeNow(data.DetailTriangles)]));
        }
        NavigationMeshTileDescription description = _description.Tiles.Single(value => value.X == tile.X && value.Z == tile.Z);
        return ReadChunksAsync(_source!, description.FirstChunkKey, cancellationToken);
    }
    public override NavigationMesh Decode(NavigationTileCoordinate tile, NavigationTileBytes bytes)
    {
        float radius = _mesh is { } mesh ? mesh.Tiles.Span.Find(tile).BakedRadius :
            _description.Tiles.Single(value => value.X == tile.X && value.Z == tile.Z).BakedRadius;
        return DecodeMesh(bytes, Graph.Tiles.Span.Find(tile).ContentVersion, radius);
    }

    internal static async ValueTask<NavigationTileBytes> ReadChunksAsync(AssetBlockSource source, ulong first, CancellationToken cancellationToken)
    {
        using AssetBlockSource retained = source.Retain();
        var chunks = new ReadOnlyMemory<byte>[6];
        for (int index = 0; index < chunks.Length; index++)
            chunks[index] = await retained.ReadBytesAsync(first + (ulong)index, cancellationToken).ConfigureAwait(false);
        return new(chunks);
    }
    internal static NavigationMesh DecodeMesh(NavigationTileBytes bytes, ulong version, float bakedRadius)
    {
        ReadOnlySpan<ReadOnlyMemory<byte>> chunks = bytes.Chunks.Span;
        return new(NavigationArrayData.DecodeNow<Vector3>(chunks[0]), NavigationArrayData.DecodeNow<int>(chunks[1]),
            NavigationArrayData.DecodeNow<NavigationPolygon>(chunks[2]), NavigationArrayData.DecodeNow<NavigationPortal>(chunks[3]),
            NavigationArrayData.DecodeNow<Vector3>(chunks[4]), NavigationArrayData.DecodeNow<NavigationDetailTriangle>(chunks[5]), version, bakedRadius: bakedRadius);
    }

    internal static NavigationMesh DecodeMeshOnWorker(NavigationTileBytes bytes, ulong version, float bakedRadius)
    {
        var result = new NavigationMesh[1];
        var accesses = new List<JobResourceAccess> { JobResourceAccess.Write(result) };
        foreach (ReadOnlyMemory<byte> chunk in bytes.Chunks.Span) accesses.Add(JobResourceAccess.Read(chunk));
        JobSystem.Schedule(new DecodeMeshData(bytes, version, bakedRadius, result), accesses.ToArray()).Complete();
        return result[0];
    }

    private readonly struct DecodeMeshData(NavigationTileBytes bytes, ulong version, float bakedRadius, NavigationMesh[] result) : IJob
    {
        public void Execute() => result[0] = DecodeMesh(bytes, version, bakedRadius);
    }

    internal static NavigationTilePortal[] Crossings(NavigationMesh mesh)
    {
        var local = new int[mesh.Polygons.Length];
        var counts = new Dictionary<NavigationTileCoordinate, int>();
        for (int index = 0; index < local.Length; index++)
        {
            NavigationTileCoordinate tile = mesh.Polygons.Span[index].Tile;
            local[index] = counts.GetValueOrDefault(tile);
            counts[tile] = local[index] + 1;
        }
        var result = new List<NavigationTilePortal>();
        for (int index = 0; index < mesh.Polygons.Length; index++)
        {
            NavigationPolygon polygon = mesh.Polygons.Span[index];
            foreach (NavigationPortal portal in mesh.Portals.Span.Slice(polygon.PortalStart, polygon.PortalCount))
            {
                NavigationTileCoordinate target = mesh.Polygons.Span[portal.ToPolygon].Tile;
                if (polygon.Tile != target) result.Add(new(polygon.Tile, local[index], target, portal with { ToPolygon = local[portal.ToPolygon] }));
            }
        }
        return result.ToArray();
    }

    internal static NavigationMesh Join(NavigationMeshTile[] tiles, NavigationTilePortal[] crossings,
        NavigationRegionGraph graph, ulong version, float bakedRadius)
    {
        var offsets = new Dictionary<NavigationTileCoordinate, int>();
        int count = 0;
        foreach (NavigationMeshTile tile in tiles) { offsets.Add(tile.Tile, count); count += tile.Mesh.Polygons.Length; }
        var positions = new List<Vector3>();
        var vertices = new List<int>();
        var polygons = new List<NavigationPolygon>();
        var portals = new List<NavigationPortal>();
        var details = new List<Vector3>();
        var triangles = new List<NavigationDetailTriangle>();
        foreach (NavigationMeshTile tile in tiles)
        {
            NavigationMesh mesh = tile.Mesh;
            int vertexOffset = vertices.Count, positionOffset = positions.Count, detailOffset = details.Count, triangleOffset = triangles.Count;
            positions.AddRange(mesh.Positions.Span);
            foreach (int vertex in mesh.PolygonVertices.Span) vertices.Add(positionOffset + vertex);
            details.AddRange(mesh.DetailPositions.Span);
            foreach (NavigationDetailTriangle triangle in mesh.DetailTriangles.Span)
                triangles.Add(new(triangle.A + detailOffset, triangle.B + detailOffset, triangle.C + detailOffset));
            for (int index = 0; index < mesh.Polygons.Length; index++)
            {
                NavigationPolygon polygon = mesh.Polygons.Span[index];
                int first = portals.Count;
                foreach (NavigationPortal portal in mesh.Portals.Span.Slice(polygon.PortalStart, polygon.PortalCount))
                    portals.Add(portal with { ToPolygon = portal.ToPolygon + offsets[tile.Tile] });
                foreach (NavigationTilePortal crossing in crossings)
                    if (crossing.From == tile.Tile && crossing.Polygon == index)
                        portals.Add(crossing.Portal with { ToPolygon = crossing.Portal.ToPolygon + offsets[crossing.To] });
                polygons.Add(polygon with { VertexStart = polygon.VertexStart + vertexOffset, PortalStart = first,
                    PortalCount = portals.Count - first, DetailStart = polygon.DetailStart + triangleOffset });
            }
        }
        return new(positions.ToArray(), vertices.ToArray(), polygons.ToArray(), portals.ToArray(),
            details.ToArray(), triangles.ToArray(), version, graph, tiles, bakedRadius);
    }
}

internal readonly record struct NavigationTilePortal(NavigationTileCoordinate From, int Polygon,
    NavigationTileCoordinate To, NavigationPortal Portal);

internal static class NavigationTileDirectory
{
    internal static NavigationMesh Find(this ReadOnlySpan<NavigationMeshTile> tiles, NavigationTileCoordinate coordinate)
    {
        foreach (NavigationMeshTile tile in tiles) if (tile.Tile == coordinate) return tile.Mesh;
        throw new KeyNotFoundException();
    }
    internal static NavigationRegionTile Find(this ReadOnlySpan<NavigationRegionTile> tiles, NavigationTileCoordinate coordinate)
    {
        foreach (NavigationRegionTile tile in tiles) if (tile.Tile == coordinate) return tile;
        throw new KeyNotFoundException();
    }
}
