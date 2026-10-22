using System.Numerics;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Navigation;

/// <summary>A grid and shared motion primitives, with car-model footprint sweep queries.</summary>
public sealed class NavigationVehicleSpace(NavigationGrid grid, NavigationMotionSet motions, NavigationCosts? costs = null)
    : INavigationSpace<NavigationVehicleSpace>
{
    public NavigationGrid Grid { get; } = grid;
    public NavigationMotionSet Motions { get; } = motions;
    public NavigationCosts? Costs { get; } = costs;

    public static void CollectResources(NavigationVehicleSpace space, ref JobResourceAccessBuilder resources)
    {
        NavigationGrid.CollectResources(space.Grid, ref resources);
        resources.Read(space.Motions.Headings);
        resources.Read(space.Motions.Primitives);
        resources.Read(space.Motions.Controls);
        resources.Read(space.Motions.Samples);
        resources.Read(space.Motions.SweptGeometry);
        if (space.Costs is { } costs)
        {
            resources.Read(costs.CellCosts);
            resources.Read(costs.EdgeCosts);
        }
    }

    public static bool TryGetHeight(NavigationVehicleSpace space, Vector3 position, out float height) =>
        NavigationGrid.TryGetHeight(space.Grid, position, out height);

    public static void CollectBoundaries(NavigationVehicleSpace space, Vector3 position, float radius,
        NavigationProfile profile, NavigationPolicy policy, ref NavigationBoundaryBuilder builder) =>
        NavigationGrid.CollectBoundaries(space.Grid, position, radius, profile, policy, ref builder);

    public bool CanOccupy(NavigationPose pose, NavigationProfile profile, NavigationPolicy policy) =>
        CheckSweep(pose, pose, 0, profile, policy);

    /// <summary>Checks the complete swept footprint and directed support transitions of a constant control.</summary>
    public bool TryTraverse(NavigationPose start, NavigationVehicleControl control,
        NavigationProfile profile, NavigationPolicy policy, out NavigationPose end, out float cost)
    {
        end = start;
        cost = 0;
        if (MathF.Abs(control.Curvature) > Motions.Model.MaximumCurvature + 0.000001f ||
            MathF.Abs(control.Speed) > profile.MaximumSpeed + 0.000001f ||
            MathF.Abs(control.Speed * control.Curvature) > profile.MaximumAngularSpeed + 0.000001f ||
            control.Speed < 0 && !Motions.Model.AllowReverse)
            return false;
        int cell = Grid.FindCell(start.Position);
        if (cell < 0 || !CanOccupy(start, profile, policy)) return false;
        float travel = MathF.Abs(control.Speed) * control.Duration;
        float angle = MathF.Abs(control.Speed * control.Curvature) * control.Duration;
        int steps = Math.Max(1, Math.Max((int)MathF.Ceiling(travel / (Grid.CellSize / 4)),
            (int)MathF.Ceiling(angle / (MathF.PI / 36))));
        float deltaTime = control.Duration / steps;
        float footprintRadius = NavigationSurfaceTraversal.GetRotationRadius(profile.Footprint.Span);
        float subAngle = angle / steps;
        float padding = MathF.Abs(control.Curvature) < 0.000001f ? 0 :
            (1 / MathF.Abs(control.Curvature) + footprintRadius) * (1 - MathF.Cos(subAngle / 2));
        ulong capabilities = profile.Capabilities & policy.AllowedCapabilities;
        for (int step = 1; step <= steps; step++)
        {
            NavigationPose next = Motions.Model.Integrate(start, control, deltaTime * step);
            int nextCell = FindSupport(Grid.GetCoordinate(next.Position), end.Position.Y, profile);
            if (nextCell < 0 || !HasTransition(cell, nextCell, capabilities, profile)) return false;
            next = next with { Position = next.Position with { Y = Grid.Cells.Span[nextCell].Position.Y } };
            if (!CheckSweep(end, next, padding, profile, policy)) return false;
            float firstCost = CellCost(cell, policy), secondCost = CellCost(nextCell, policy);
            if (!float.IsFinite(firstCost) || !float.IsFinite(secondCost)) return false;
            cost += travel / steps * (firstCost + secondCost) * 0.5f;
            end = next;
            cell = nextCell;
        }
        return true;
    }

    private float CellCost(int cell, NavigationPolicy policy) => Costs is { } costs
        ? costs.CellCosts.Span[cell]
        : Grid.Cells.Span[cell].BaseCost * policy.GetAreaCost(Grid.Cells.Span[cell].Area);

    private int FindSupport(NavigationCellOffset coordinate, float height, NavigationProfile profile)
    {
        int closest = -1;
        float distance = float.PositiveInfinity;
        foreach (int index in Grid.GetColumn(coordinate))
        {
            NavigationGridCell candidate = Grid.Cells.Span[index];
            float difference = MathF.Abs(candidate.Position.Y - height);
            if (difference > profile.MaximumStepHeight + 0.00001f || difference >= distance ||
                candidate.CeilingHeight - MathF.Max(candidate.Position.Y, height) < profile.Height) continue;
            closest = index;
            distance = difference;
        }
        return closest;
    }

    private bool HasTransition(int from, int to, ulong capabilities, NavigationProfile profile)
    {
        if (from == to) return true;
        if (HasEdge(from, to, capabilities)) return true;
        NavigationCellOffset first = Grid.GetCoordinate(Grid.Cells.Span[from].Position);
        NavigationCellOffset second = Grid.GetCoordinate(Grid.Cells.Span[to].Position);
        if (Math.Abs(first.X - second.X) != 1 || Math.Abs(first.Z - second.Z) != 1) return false;
        int middle = FindSupport(new(second.X, first.Z), Grid.Cells.Span[from].Position.Y, profile);
        if (middle >= 0 && HasEdge(from, middle, capabilities) && HasEdge(middle, to, capabilities)) return true;
        middle = FindSupport(new(first.X, second.Z), Grid.Cells.Span[from].Position.Y, profile);
        return middle >= 0 && HasEdge(from, middle, capabilities) && HasEdge(middle, to, capabilities);
    }

    private bool HasEdge(int from, int to, ulong capabilities)
    {
        NavigationGridCell cell = Grid.Cells.Span[from];
        for (int index = cell.EdgeStart; index < cell.EdgeStart + cell.EdgeCount; index++)
        {
            NavigationGridEdge edge = Grid.Edges.Span[index];
            if (edge.ToCell == to && edge.Action == 0 && (edge.RequiredCapabilities & capabilities) == edge.RequiredCapabilities &&
                (Costs is null || float.IsFinite(Costs.EdgeCosts.Span[index]))) return true;
        }
        return false;
    }

    private bool CheckSweep(NavigationPose first, NavigationPose second, float padding,
        NavigationProfile profile, NavigationPolicy policy)
    {
        if (Grid.Shape.FootprintMode == NavigationGridFootprintMode.AllHeadings)
        {
            int cell = Grid.FindCell(second.Position);
            return cell >= 0 && float.IsFinite(CellCost(cell, policy));
        }
        var points = new List<Vector2>(profile.Footprint.Length * 2);
        AddBody(first);
        AddBody(second);
        Vector2[] hull = Hull(points);
        NavigationCellOffset reference = Grid.GetCoordinate((first.Position + second.Position) * 0.5f);
        Vector2 center = new(Grid.Bounds.Minimum.X + (reference.X + 0.5f) * Grid.CellSize,
            Grid.Bounds.Minimum.Z + (reference.Z + 0.5f) * Grid.CellSize);
        for (int index = 0; index < hull.Length; index++) hull[index] -= center;
        foreach (NavigationCellOffset offset in NavigationSurfaceTraversal.RasterizeFootprint(hull, Grid.CellSize, padding))
        {
            int cell = FindSupport(new(reference.X + offset.X, reference.Z + offset.Z),
                (first.Position.Y + second.Position.Y) * 0.5f, profile);
            if (cell < 0 || !float.IsFinite(CellCost(cell, policy))) return false;
        }
        return true;

        void AddBody(NavigationPose pose)
        {
            float sine = MathF.Sin(pose.Heading), cosine = MathF.Cos(pose.Heading);
            foreach (Vector2 vertex in profile.Footprint.Span)
                points.Add(new(pose.Position.X + cosine * vertex.X + sine * vertex.Y,
                    pose.Position.Z - sine * vertex.X + cosine * vertex.Y));
        }
    }

    private static Vector2[] Hull(List<Vector2> points)
    {
        Vector2[] sorted = points.Distinct().OrderBy(point => point.X).ThenBy(point => point.Y).ToArray();
        var hull = new List<Vector2>(sorted.Length * 2);
        foreach (Vector2 point in sorted)
        {
            while (hull.Count > 1 && Cross(hull[^1] - hull[^2], point - hull[^1]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        int lower = hull.Count;
        for (int index = sorted.Length - 2; index >= 0; index--)
        {
            Vector2 point = sorted[index];
            while (hull.Count > lower && Cross(hull[^1] - hull[^2], point - hull[^1]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        if (hull.Count > 1) hull.RemoveAt(hull.Count - 1);
        return hull.ToArray();
    }

    private static float Cross(Vector2 first, Vector2 second) => first.X * second.Y - first.Y * second.X;
}
