using System.Numerics;

namespace SomeEngine.Navigation;

public readonly record struct NavigationCellOffset(int X, int Z);

/// <summary>Shared profile filtering for surface-derived navigation representations.</summary>
public static class NavigationSurfaceTraversal
{
    public static bool IsWalkable(in NavigationSurfaceSpan span, float ceiling,
        NavigationProfile profile, NavigationPolicy policy) =>
        span.Normal.Y >= MathF.Cos(profile.MaximumSlopeRadians) &&
        ceiling - span.Height >= profile.Height && float.IsFinite(policy.GetAreaCost(span.Area));

    public static bool CanTraverse(in NavigationSurfaceSpan first, float firstCeiling,
        in NavigationSurfaceSpan second, float secondCeiling, NavigationProfile profile) =>
        MathF.Abs(first.Height - second.Height) <= profile.MaximumStepHeight &&
        MathF.Min(firstCeiling, secondCeiling) - MathF.Max(first.Height, second.Height) >= profile.Height;

    /// <summary>Cells intersecting the complete footprint at heading zero (+Z), including contour edges.</summary>
    public static NavigationCellOffset[] RasterizeFootprint(ReadOnlySpan<Vector2> footprint, float cellSize)
    {
        Vector2 minimum = Vector2.Zero, maximum = Vector2.Zero;
        foreach (Vector2 point in footprint)
        {
            minimum = Vector2.Min(minimum, point);
            maximum = Vector2.Max(maximum, point);
        }
        var offsets = new List<NavigationCellOffset>();
        for (int z = (int)MathF.Ceiling(minimum.Y / cellSize - 0.5f); z <= (int)MathF.Floor(maximum.Y / cellSize + 0.5f); z++)
        for (int x = (int)MathF.Ceiling(minimum.X / cellSize - 0.5f); x <= (int)MathF.Floor(maximum.X / cellSize + 0.5f); x++)
        {
            Vector2 lower = new((x - 0.5f) * cellSize, (z - 0.5f) * cellSize);
            Vector2 upper = lower + new Vector2(cellSize);
            if (Intersects(footprint, lower, upper)) offsets.Add(new(x, z));
        }
        return offsets.ToArray();
    }

    private static bool Intersects(ReadOnlySpan<Vector2> footprint, Vector2 lower, Vector2 upper)
    {
        foreach (Vector2 point in footprint)
            if (point.X >= lower.X && point.X <= upper.X && point.Y >= lower.Y && point.Y <= upper.Y)
                return true;
        Span<Vector2> corners = [lower, new(upper.X, lower.Y), upper, new(lower.X, upper.Y)];
        foreach (Vector2 corner in corners)
        {
            bool inside = false;
            for (int i = 0, j = footprint.Length - 1; i < footprint.Length; j = i++)
            {
                Vector2 a = footprint[j], b = footprint[i];
                if ((a.Y > corner.Y) != (b.Y > corner.Y) &&
                    corner.X < (b.X - a.X) * (corner.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
                for (int edge = 0; edge < corners.Length; edge++)
                    if (SegmentsIntersect(a, b, corners[edge], corners[(edge + 1) % corners.Length]))
                        return true;
            }
            if (inside) return true;
        }
        return false;
    }

    private static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        static float Cross(Vector2 first, Vector2 second) => first.X * second.Y - first.Y * second.X;
        Vector2 first = b - a, second = d - c;
        float area = Cross(first, second);
        if (area == 0) return false;
        float alongFirst = Cross(c - a, second) / area;
        float alongSecond = Cross(c - a, first) / area;
        return alongFirst >= 0 && alongFirst <= 1 && alongSecond >= 0 && alongSecond <= 1;
    }
}
