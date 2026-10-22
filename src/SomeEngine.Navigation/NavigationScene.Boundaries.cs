using System.Numerics;
using static SomeEngine.Navigation.NavigationBoundaryBuilder;

namespace SomeEngine.Navigation;

public sealed partial class NavigationScene
{
    public static void CollectBoundaries(NavigationScene space, Vector3 position, float radius,
        NavigationProfile profile, NavigationPolicy policy, ref NavigationBoundaryBuilder builder)
    {
        NavigationSceneContent content = space.Content;
        NavigationSceneTile? start = content.FindTile(position, out _);
        if (start is null) return;
        builder.Visit(content.GetPolygonId(start.Id, start.Mesh.FindPolygon(position)));
        ulong capabilities = profile.Capabilities & policy.AllowedCapabilities;
        while (builder.TryPop(out int id))
        {
            NavigationSceneTile tile = content.GetPolygon(id, out int index);
            NavigationMesh mesh = tile.Mesh;
            NavigationPolygon polygon = mesh.Polygons.Span[index];
            ReadOnlySpan<int> vertices = mesh.PolygonVertices.Span.Slice(polygon.VertexStart, polygon.VertexCount);
            Vector2 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
            foreach (int vertex in vertices)
            {
                Vector2 point = Xz(mesh.Positions.Span[vertex]);
                minimum = Vector2.Min(minimum, point);
                maximum = Vector2.Max(maximum, point);
            }
            if (Vector2.DistanceSquared(Xz(position), Vector2.Clamp(Xz(position), minimum, maximum)) > radius * radius) continue;
            Vector3 center = NavigationMeshBuilder.Center(mesh, index);
            NavigationRegionGraph graph = content.GetRegion(tile.Region).Graph;
            for (int vertex = 0; vertex < vertices.Length; vertex++)
            {
                Vector3 a = mesh.Positions.Span[vertices[vertex]], b = mesh.Positions.Span[vertices[(vertex + 1) % vertices.Length]];
                if (Cross(Xz(b - a), Xz(center - a)) > 0) (a, b) = (b, a);
                builder.BeginOpenings();
                foreach (NavigationPortal portal in mesh.Portals.Span.Slice(polygon.PortalStart, polygon.PortalCount))
                {
                    if (portal.Action != 0 || !float.IsFinite(portal.Cost) || (portal.RequiredCapabilities & ~capabilities) != 0 ||
                        !CanEnter(mesh, portal.ToPolygon, capabilities, policy)) continue;
                    builder.Visit(content.GetPolygonId(tile.Id, portal.ToPolygon));
                    Open(a, b, portal.Left, portal.Right, ref builder);
                }
                for (int entranceIndex = 0; entranceIndex < graph.Entrances.Length; entranceIndex++)
                {
                    NavigationRegionEntrance entrance = graph.Entrances.Span[entranceIndex];
                    if (entrance.Tile != tile.Tile || entrance.Polygon != index) continue;
                    foreach (NavigationRegionEdge edge in graph.Edges.Span.Slice(entrance.EdgeStart, entrance.EdgeCount))
                    {
                        NavigationRegionEntrance target = graph.Entrances.Span[edge.To];
                        if (target.Tile == tile.Tile || edge.Action != 0 || !float.IsFinite(edge.Cost) ||
                            (edge.RequiredCapabilities & ~capabilities) != 0 ||
                            !content.TryGetTile(new(tile.Region, target.Tile), out NavigationSceneTile? destination) ||
                            !CanEnter(destination!.Mesh, target.Polygon, capabilities, policy)) continue;
                        builder.Visit(content.GetPolygonId(destination.Id, target.Polygon));
                        Open(a, b, entrance.Left, entrance.Right, ref builder);
                    }
                    if (entrance.Port >= 0)
                        OpenRegionConnections(content, tile, entrance, a, b, capabilities, profile, policy, ref builder);
                }
                builder.AddClosedParts(a, b, mesh.BakedRadius);
            }
        }
    }

    private static void OpenRegionConnections(NavigationSceneContent content, NavigationSceneTile source,
        NavigationRegionEntrance entrance, Vector3 a, Vector3 b, ulong capabilities,
        NavigationProfile profile, NavigationPolicy policy, ref NavigationBoundaryBuilder builder)
    {
        foreach (NavigationSceneConnection connection in content.Connections.Span)
        {
            if (connection.Action != 0 || !float.IsFinite(connection.Cost) || (connection.RequiredCapabilities & ~capabilities) != 0) continue;
            int region, port;
            if (connection.FromRegion == source.Region && connection.FromPort == entrance.Port)
                (region, port) = (connection.ToRegion, connection.ToPort);
            else if (connection.Bidirectional && connection.ToRegion == source.Region && connection.ToPort == entrance.Port)
                (region, port) = (connection.FromRegion, connection.FromPort);
            else continue;
            NavigationRegionGraph graph = content.GetRegion(region).Graph;
            NavigationRegionEntrance target = graph.Entrances.Span[graph.FindPort(port)];
            if (Vector2.DistanceSquared(Xz(entrance.Position), Xz(target.Position)) > 0.000001f ||
                MathF.Abs(entrance.Position.Y - target.Position.Y) > profile.MaximumStepHeight ||
                !content.TryGetTile(new(region, target.Tile), out NavigationSceneTile? destination) ||
                !CanEnter(destination!.Mesh, target.Polygon, capabilities, policy)) continue;
            NavigationMesh mesh = destination.Mesh;
            NavigationPolygon polygon = mesh.Polygons.Span[target.Polygon];
            ReadOnlySpan<int> vertices = mesh.PolygonVertices.Span.Slice(polygon.VertexStart, polygon.VertexCount);
            for (int vertex = 0; vertex < vertices.Length; vertex++)
                if (Open(a, b, mesh.Positions.Span[vertices[vertex]], mesh.Positions.Span[vertices[(vertex + 1) % vertices.Length]], ref builder))
                    builder.Visit(content.GetPolygonId(destination.Id, target.Polygon));
        }
    }

    private static bool CanEnter(NavigationMesh mesh, int index, ulong capabilities, NavigationPolicy policy)
    {
        NavigationPolygon polygon = mesh.Polygons.Span[index];
        return (polygon.RequiredCapabilities & ~capabilities) == 0 && float.IsFinite(policy.GetAreaCost(polygon.Area));
    }

    private static bool Open(Vector3 a, Vector3 b, Vector3 left, Vector3 right, ref NavigationBoundaryBuilder builder)
    {
        Vector2 direction = Xz(b - a), first = Xz(left - a), second = Xz(right - a);
        if (MathF.Abs(Cross(direction, first)) > 0.0001f || MathF.Abs(Cross(direction, second)) > 0.0001f) return false;
        float firstT = Vector2.Dot(first, direction) / direction.LengthSquared();
        float secondT = Vector2.Dot(second, direction) / direction.LengthSquared();
        float lower = MathF.Max(0, MathF.Min(firstT, secondT)), upper = MathF.Min(1, MathF.Max(firstT, secondT));
        if (upper <= lower) return false;
        builder.Open(lower, upper);
        return true;
    }
}
