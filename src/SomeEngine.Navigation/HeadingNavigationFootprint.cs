using System.Numerics;

namespace SomeEngine.Navigation;

internal static class HeadingNavigationFootprint
{
    internal static NavigationCellOffset[] Rasterize(ReadOnlySpan<Vector2> footprint, float cellSize,
        float minimum, float maximum, float maximumError)
    {
        float radius = NavigationSurfaceTraversal.GetRotationRadius(footprint);
        float tolerance = MathF.Min(maximumError, cellSize * 0.25f);
        int intervals = radius == 0 ? 1 : Math.Max(1,
            (int)MathF.Ceiling((maximum - minimum) / (4 * MathF.Asin(MathF.Min(1, tolerance / (2 * radius))))));
        float step = (maximum - minimum) / intervals;
        // Every intermediate point is within this distance of its nearest angular sample.
        float displacement = 2 * radius * MathF.Sin(step * 0.25f);
        float padding = displacement + cellSize * 0.5f;
        var rotated = new Vector2[footprint.Length];
        var cells = new HashSet<NavigationCellOffset>();
        for (int sample = 0; sample <= intervals; sample++)
        {
            float angle = minimum + step * sample;
            float cosine = MathF.Cos(angle), sine = MathF.Sin(angle);
            for (int vertex = 0; vertex < footprint.Length; vertex++)
            {
                Vector2 point = footprint[vertex];
                rotated[vertex] = new(point.X * cosine + point.Y * sine, -point.X * sine + point.Y * cosine);
            }
            foreach (NavigationCellOffset cell in NavigationSurfaceTraversal.RasterizeFootprint(rotated, cellSize, padding))
                cells.Add(cell);
        }
        return cells.OrderBy(cell => cell.Z).ThenBy(cell => cell.X).ToArray();
    }
}
