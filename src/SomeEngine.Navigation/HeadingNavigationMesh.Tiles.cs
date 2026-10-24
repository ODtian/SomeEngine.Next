using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public sealed partial class HeadingNavigationMesh
{
    internal HeadingNavigationMesh WithRegionGraph(HeadingNavigationRegionGraph graph, HeadingNavigationMeshTile[] tiles) =>
        new(Geometry, ChannelCount, Regions, Headings, Translations, Rotations, Graph, graph, tiles);

    internal BigInteger AllowedChannels(int region)
    {
        BigInteger channels = 0;
        foreach (HeadingNavigationInterval interval in GetAllowedHeadings(region).Span) channels |= BigInteger.One << interval.Channel;
        return channels;
    }

    internal HeadingNavigationMeshTile[] SeparateTiles()
    {
        if (!Tiles.IsEmpty) return Tiles.ToArray();
        NavigationMeshTile[] geometry = Geometry.SeparateTiles();
        var permissions = new Dictionary<NavigationTileCoordinate, List<BigInteger>>();
        for (int region = 0; region < Regions.Length; region++)
        {
            NavigationTileCoordinate tile = Geometry.Polygons.Span[region].Tile;
            if (!permissions.TryGetValue(tile, out var channels)) permissions.Add(tile, channels = []);
            channels.Add(AllowedChannels(region));
        }
        return geometry.Select(tile => new HeadingNavigationMeshTile(tile.Tile,
            HeadingNavigationGraphBuilder.Create(tile.Mesh, permissions.GetValueOrDefault(tile.Tile)?.ToArray() ?? [], ChannelCount),
            tile.Bounds ?? tile.Mesh.Bounds)).ToArray();
    }

    /// <summary>Joins complete detailed tiles using the heading representation's retained crossing directory.</summary>
    public static HeadingNavigationMesh Combine(ReadOnlyMemory<HeadingNavigationMeshTile> tiles, HeadingNavigationRegionGraph graph)
    {
        var offsets = new Dictionary<NavigationTileCoordinate, int>();
        int count = 0;
        foreach (HeadingNavigationMeshTile tile in tiles.Span) { offsets.Add(tile.Tile, count); count += tile.Mesh.Regions.Length; }
        var positions = new List<Vector3>(); var vertices = new List<int>();
        var polygons = new List<NavigationPolygon>(); var portals = new List<NavigationPortal>();
        var details = new List<Vector3>(); var triangles = new List<NavigationDetailTriangle>();
        var allowed = new List<BigInteger>();
        foreach (HeadingNavigationMeshTile tile in tiles.Span)
        {
            NavigationMesh mesh = tile.Mesh.Geometry;
            int regionOffset = offsets[tile.Tile], vertexOffset = positions.Count, detailOffset = details.Count;
            positions.AddRange(mesh.Positions.ToArray()); details.AddRange(mesh.DetailPositions.ToArray());
            for (int region = 0; region < mesh.Polygons.Length; region++)
            {
                NavigationPolygon polygon = mesh.Polygons.Span[region];
                int vertexStart = vertices.Count, portalStart = portals.Count, detailStart = triangles.Count;
                foreach (int vertex in mesh.PolygonVertices.Span.Slice(polygon.VertexStart, polygon.VertexCount)) vertices.Add(vertex + vertexOffset);
                foreach (NavigationPortal portal in mesh.Portals.Span.Slice(polygon.PortalStart, polygon.PortalCount))
                    portals.Add(portal with { ToPolygon = portal.ToPolygon + regionOffset });
                foreach (HeadingNavigationTilePortal portal in graph.Portals.Span)
                    if (portal.FromTile == tile.Tile && portal.FromPolygon == region && offsets.TryGetValue(portal.ToTile, out int to))
                        portals.Add(new(to + portal.ToPolygon, portal.Left, portal.Right, portal.Cost, portal.RequiredCapabilities, portal.Action));
                foreach (NavigationDetailTriangle triangle in mesh.DetailTriangles.Span.Slice(polygon.DetailStart, polygon.DetailCount))
                    triangles.Add(new(triangle.A + detailOffset, triangle.B + detailOffset, triangle.C + detailOffset));
                polygons.Add(polygon with { VertexStart = vertexStart, PortalStart = portalStart,
                    PortalCount = portals.Count - portalStart, DetailStart = detailStart });
                allowed.Add(tile.Mesh.AllowedChannels(region));
            }
        }
        var geometry = new NavigationMesh(positions.ToArray(), vertices.ToArray(), polygons.ToArray(), portals.ToArray(),
            details.ToArray(), triangles.ToArray(), graph.ContentVersion);
        return HeadingNavigationGraphBuilder.Create(geometry, allowed.ToArray(), graph.ChannelCount).WithRegionGraph(graph, tiles.ToArray());
    }

    internal void AddReadAccesses(Scheduler scheduler, List<JobResourceAccess> resources)
    {
        Geometry.AddReadAccesses(scheduler, resources);
        resources.Add(NavigationJobAccess.Read(scheduler, Regions)); resources.Add(NavigationJobAccess.Read(scheduler, Headings));
        resources.Add(NavigationJobAccess.Read(scheduler, Translations)); resources.Add(NavigationJobAccess.Read(scheduler, Rotations));
        resources.Add(NavigationJobAccess.Read(scheduler, Graph.Nodes)); resources.Add(NavigationJobAccess.Read(scheduler, Graph.Edges));
    }
}
