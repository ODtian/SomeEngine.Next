using System.Numerics;
using static SomeEngine.Navigation.NavigationBoundaryBuilder;

namespace SomeEngine.Navigation;

public sealed partial class NavigationMesh
{
    public static void CollectBoundaries(NavigationMesh space, Vector3 position, float radius,
        NavigationProfile profile, NavigationPolicy policy, ref NavigationBoundaryBuilder builder)
    {
        int start = space.FindPolygon(position);
        if (start < 0) return;
        builder.Visit(start);
        ulong capabilities = profile.Capabilities & policy.AllowedCapabilities;
        while (builder.TryPop(out int index))
        {
            NavigationPolygon polygon = space.Polygons.Span[index];
            ReadOnlySpan<int> vertices = space.PolygonVertices.Span.Slice(polygon.VertexStart, polygon.VertexCount);
            Vector2 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
            foreach (int vertex in vertices)
            {
                Vector2 point = Xz(space.Positions.Span[vertex]);
                minimum = Vector2.Min(minimum, point);
                maximum = Vector2.Max(maximum, point);
            }
            if (Vector2.DistanceSquared(Xz(position), Vector2.Clamp(Xz(position), minimum, maximum)) > radius * radius) continue;
            Vector3 center = NavigationMeshBuilder.Center(space, index);
            ReadOnlySpan<NavigationPortal> portals = space.Portals.Span.Slice(polygon.PortalStart, polygon.PortalCount);
            for (int vertex = 0; vertex < vertices.Length; vertex++)
            {
                Vector3 a = space.Positions.Span[vertices[vertex]], b = space.Positions.Span[vertices[(vertex + 1) % vertices.Length]];
                if (Cross(Xz(b - a), Xz(center - a)) > 0) (a, b) = (b, a);
                Vector2 direction = Xz(b - a);
                float lengthSquared = direction.LengthSquared();
                builder.BeginOpenings();
                foreach (NavigationPortal portal in portals)
                {
                    NavigationPolygon destination = space.Polygons.Span[portal.ToPolygon];
                    if (portal.Action != 0 || ((portal.RequiredCapabilities | destination.RequiredCapabilities) & ~capabilities) != 0 ||
                        !float.IsFinite(policy.GetAreaCost(destination.Area)) || !float.IsFinite(portal.Cost)) continue;
                    builder.Visit(portal.ToPolygon);
                    Vector2 first = Xz(portal.Left - a), second = Xz(portal.Right - a);
                    if (MathF.Abs(Cross(direction, first)) > 0.0001f || MathF.Abs(Cross(direction, second)) > 0.0001f) continue;
                    float firstT = Vector2.Dot(first, direction) / lengthSquared, secondT = Vector2.Dot(second, direction) / lengthSquared;
                    float lower = MathF.Max(0, MathF.Min(firstT, secondT)), upper = MathF.Min(1, MathF.Max(firstT, secondT));
                    if (upper > lower) builder.Open(lower, upper);
                }
                builder.AddClosedParts(a, b, space.BakedRadius);
            }
        }
    }
}
