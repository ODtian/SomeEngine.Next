using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public sealed partial class NavigationMesh
{
    internal NavigationMesh WithRegionGraph(NavigationRegionGraph graph, NavigationMeshTile[] tiles) =>
        new(Positions, PolygonVertices, Polygons, Portals, DetailPositions, DetailTriangles, ContentVersion, graph, tiles, BakedRadius);

    internal NavigationMeshTile[] SeparateTiles()
    {
        if (!Tiles.IsEmpty) return Tiles.ToArray();
        var groups = new Dictionary<NavigationTileCoordinate, List<int>>();
        for (int polygon = 0; polygon < Polygons.Length; polygon++)
        {
            NavigationTileCoordinate tile = Polygons.Span[polygon].Tile;
            if (!groups.TryGetValue(tile, out List<int>? indices)) groups.Add(tile, indices = []);
            indices.Add(polygon);
        }
        var tiles = new List<NavigationMeshTile>();
        foreach (var group in groups)
            tiles.Add(new(group.Key, CopyPolygons(group.Value)));
        return tiles.ToArray();
    }

    private NavigationMesh CopyPolygons(List<int> selected)
    {
        var positions = new List<Vector3>();
        var vertices = new List<int>();
        var polygons = new List<NavigationPolygon>();
        var portals = new List<NavigationPortal>();
        var details = new List<Vector3>();
        var triangles = new List<NavigationDetailTriangle>();
        var mapping = new Dictionary<int, int>();
        for (int index = 0; index < selected.Count; index++) mapping.Add(selected[index], index);
        foreach (int index in selected)
        {
            NavigationPolygon polygon = Polygons.Span[index];
            int vertexStart = vertices.Count, portalStart = portals.Count, detailStart = triangles.Count;
            foreach (int vertex in PolygonVertices.Span.Slice(polygon.VertexStart, polygon.VertexCount))
            {
                vertices.Add(positions.Count);
                positions.Add(Positions.Span[vertex]);
            }
            foreach (NavigationPortal portal in Portals.Span.Slice(polygon.PortalStart, polygon.PortalCount))
                if (mapping.TryGetValue(portal.ToPolygon, out int target))
                    portals.Add(portal with { ToPolygon = target });
            foreach (NavigationDetailTriangle triangle in DetailTriangles.Span.Slice(polygon.DetailStart, polygon.DetailCount))
            {
                int start = details.Count;
                details.Add(DetailPositions.Span[triangle.A]);
                details.Add(DetailPositions.Span[triangle.B]);
                details.Add(DetailPositions.Span[triangle.C]);
                triangles.Add(new(start, start + 1, start + 2));
            }
            polygons.Add(polygon with { VertexStart = vertexStart, PortalStart = portalStart,
                PortalCount = portals.Count - portalStart, DetailStart = detailStart });
        }
        return new(positions.ToArray(), vertices.ToArray(), polygons.ToArray(), portals.ToArray(),
            details.ToArray(), triangles.ToArray(), ContentVersion, bakedRadius: BakedRadius);
    }

    internal NavigationMesh Transform(Matrix4x4 transform)
    {
        var positions = new Vector3[Positions.Length];
        var details = new Vector3[DetailPositions.Length];
        for (int index = 0; index < positions.Length; index++) positions[index] = Vector3.Transform(Positions.Span[index], transform);
        for (int index = 0; index < details.Length; index++) details[index] = Vector3.Transform(DetailPositions.Span[index], transform);
        var portals = new NavigationPortal[Portals.Length];
        for (int polygon = 0; polygon < Polygons.Length; polygon++)
        {
            NavigationPolygon source = Polygons.Span[polygon];
            Vector3 from = Vector3.Transform(NavigationMeshBuilder.Center(this, polygon), transform);
            for (int index = source.PortalStart; index < source.PortalStart + source.PortalCount; index++)
            {
                NavigationPortal portal = Portals.Span[index];
                Vector3 to = Vector3.Transform(NavigationMeshBuilder.Center(this, portal.ToPolygon), transform);
                NavigationPortal oriented = NavigationMeshBuilder.Portal(portal.ToPolygon,
                    Vector3.Transform(portal.Left, transform), Vector3.Transform(portal.Right, transform), from, to);
                portals[index] = oriented with { Cost = portal.Cost, RequiredCapabilities = portal.RequiredCapabilities, Action = portal.Action };
            }
        }
        var tiles = new NavigationMeshTile[Tiles.Length];
        for (int index = 0; index < tiles.Length; index++)
            tiles[index] = new(Tiles.Span[index].Tile, Tiles.Span[index].Mesh.Transform(transform), Tiles.Span[index].Bounds?.Transform(transform));
        // The smallest XZ singular value is the disk radius contained in the transformed clearance ellipse.
        Vector2 xAxis = new(transform.M11, transform.M13), zAxis = new(transform.M31, transform.M33);
        float xx = xAxis.LengthSquared(), zz = zAxis.LengthSquared(), xz = Vector2.Dot(xAxis, zAxis);
        float minimumScale = MathF.Sqrt(MathF.Max(0, (xx + zz - MathF.Sqrt((xx - zz) * (xx - zz) + 4 * xz * xz)) * 0.5f));
        return new(positions, PolygonVertices.ToArray(), Polygons.ToArray(), portals,
            details, DetailTriangles.ToArray(), ContentVersion, tiles: tiles, bakedRadius: BakedRadius * minimumScale);
    }

    internal void AddReadAccesses(Scheduler scheduler, List<JobResourceAccess> accesses)
    {
        accesses.Add(NavigationJobAccess.Read(scheduler, Positions));
        accesses.Add(NavigationJobAccess.Read(scheduler, PolygonVertices));
        accesses.Add(NavigationJobAccess.Read(scheduler, Polygons));
        accesses.Add(NavigationJobAccess.Read(scheduler, Portals));
        accesses.Add(NavigationJobAccess.Read(scheduler, DetailPositions));
        accesses.Add(NavigationJobAccess.Read(scheduler, DetailTriangles));
        foreach (NavigationMeshTile tile in Tiles.Span) tile.Mesh.AddReadAccesses(scheduler, accesses);
    }
}
