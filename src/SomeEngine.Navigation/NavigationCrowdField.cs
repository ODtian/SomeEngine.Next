using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Navigation;

/// <summary>Density contribution weight for an actor's actual movement feedback.</summary>
public readonly record struct NavigationCrowdAgent(float Weight) : IEnableableComponent
{
    public NavigationCrowdAgent() : this(1) { }
}

public readonly record struct NavigationCrowdCell(float Density, Vector3 AverageVelocity);

/// <summary>One completed density and actual mean-velocity field on a multi-layer support grid.</summary>
public sealed class NavigationCrowdField(NavigationGrid grid, ReadOnlyMemory<float> density,
    ReadOnlyMemory<Vector3> averageVelocity, ulong version)
{
    public NavigationGrid Grid { get; } = grid;
    public ReadOnlyMemory<float> Density { get; } = density;
    public ReadOnlyMemory<Vector3> AverageVelocity { get; } = averageVelocity;
    public ulong Version { get; } = version;

    public NavigationCrowdCell Sample(Vector3 position)
    {
        int cell = Grid.FindCell(position);
        return cell >= 0 && Grid.IsDetailedCell(cell) ? new(Density.Span[cell], AverageVelocity.Span[cell]) : default;
    }
}

internal readonly record struct NavigationCrowdSample(NavigationMotion Motion, float Weight, int Cell = -1);
internal readonly record struct NavigationCrowdContribution(int Cell, float Density, Vector3 Momentum);
internal readonly record struct NavigationCrowdRange(int Start, int Count);

internal static class NavigationCrowdSupport
{
    internal static int Find(NavigationGrid grid, int support, NavigationCellOffset target)
    {
        NavigationCellOffset origin = grid.GetCoordinate(grid.Cells.Span[support].Position);
        int x = target.X - origin.X, z = target.Z - origin.Z;
        int first = x == 0 ? support : Neighbor(grid, support, x, 0);
        int result = first >= 0 && z != 0 ? Neighbor(grid, first, 0, z) : first;
        if (result >= 0 || x == 0 || z == 0) return result;
        first = Neighbor(grid, support, 0, z);
        return first >= 0 ? Neighbor(grid, first, x, 0) : -1;
    }

    internal static int Neighbor(NavigationGrid grid, int cell, int x, int z)
    {
        NavigationGridCell from = grid.Cells.Span[cell];
        NavigationCellOffset origin = grid.GetCoordinate(from.Position), target = new(origin.X + x, origin.Z + z);
        int selected = -1;
        float height = float.PositiveInfinity;
        foreach (NavigationGridEdge edge in grid.Edges.Span.Slice(from.EdgeStart, from.EdgeCount))
            if (edge.Action == 0) Consider(edge.ToCell);
        NavigationGridRange incoming = grid.IncomingRanges.Span[cell];
        foreach (NavigationGridIncoming edge in grid.IncomingEdges.Span.Slice(incoming.Start, incoming.Count))
            if (grid.Edges.Span[edge.Edge].Action == 0) Consider(edge.FromCell);
        return selected;

        void Consider(int candidate)
        {
            if (!grid.IsDetailedCell(candidate)) return;
            NavigationGridCell value = grid.Cells.Span[candidate];
            if (grid.GetCoordinate(value.Position) != target) return;
            float distance = MathF.Abs(value.Position.Y - from.Position.Y);
            if (distance >= height) return;
            height = distance;
            selected = candidate;
        }
    }
}
