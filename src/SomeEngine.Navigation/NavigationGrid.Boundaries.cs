using System.Numerics;
using static SomeEngine.Navigation.NavigationBoundaryBuilder;

namespace SomeEngine.Navigation;

public sealed partial class NavigationGrid
{
    public static void CollectBoundaries(NavigationGrid space, Vector3 position, float radius,
        NavigationProfile profile, NavigationPolicy policy, ref NavigationBoundaryBuilder builder)
    {
        int first = space.FindCell(position);
        if (first < 0) return;
        builder.Visit(first);
        ulong capabilities = profile.Capabilities & policy.AllowedCapabilities;
        float half = space.CellSize * 0.5f;
        float bakedRadius = space.Shape.FootprintMode == NavigationGridFootprintMode.AllHeadings
            ? NavigationSurfaceTraversal.GetRotationRadius(space.Shape.Footprint.Span) : 0;
        ReadOnlySpan<NavigationCellOffset> offsets = [new(-1, 0), new(1, 0), new(0, -1), new(0, 1)];
        Span<bool> openings = stackalloc bool[4];
        while (builder.TryPop(out int index))
        {
            NavigationGridCell cell = space.Cells.Span[index];
            Vector3 center = cell.Position;
            Vector2 offset = Vector2.Max(Vector2.Abs(Xz(center - position)) - new Vector2(half), Vector2.Zero);
            if (offset.LengthSquared() > radius * radius) continue;
            NavigationCellOffset coordinate = space.GetCoordinate(center);
            openings.Clear();
            foreach (NavigationGridEdge edge in space.Edges.Span.Slice(cell.EdgeStart, cell.EdgeCount))
            {
                NavigationGridCell target = space.Cells.Span[edge.ToCell];
                if (edge.Action != 0 || (edge.RequiredCapabilities & ~capabilities) != 0 ||
                    !float.IsFinite(policy.GetAreaCost(target.Area)) || !float.IsFinite(edge.Cost)) continue;
                builder.Visit(edge.ToCell);
                NavigationCellOffset destination = space.GetCoordinate(target.Position);
                for (int direction = 0; direction < 4; direction++)
                    if (destination == new NavigationCellOffset(coordinate.X + offsets[direction].X, coordinate.Z + offsets[direction].Z) &&
                        MathF.Abs(target.Position.Y - center.Y) <= profile.MaximumStepHeight)
                        openings[direction] = true;
            }
            if (!openings[0]) builder.Add(center + new Vector3(-half, 0, -half), center + new Vector3(-half, 0, half), bakedRadius);
            if (!openings[1]) builder.Add(center + new Vector3(half, 0, half), center + new Vector3(half, 0, -half), bakedRadius);
            if (!openings[2]) builder.Add(center + new Vector3(half, 0, -half), center + new Vector3(-half, 0, -half), bakedRadius);
            if (!openings[3]) builder.Add(center + new Vector3(-half, 0, half), center + new Vector3(half, 0, half), bakedRadius);
        }
    }
}
