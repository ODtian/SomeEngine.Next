using System.Numerics;

namespace SomeEngine.Navigation;

internal static class NavigationFlowSampler
{
    internal static int FindEdge(NavigationFlowInput input, int cell, int x, int z)
    {
        NavigationGrid grid = input.Costs.Grid;
        NavigationGridCell from = grid.Cells.Span[cell];
        NavigationCellOffset coordinate = grid.GetCoordinate(from.Position);
        ulong capabilities = input.Profile.Capabilities & input.Costs.Policy.AllowedCapabilities;
        int selected = -1;
        float heightDistance = float.PositiveInfinity;
        for (int edgeIndex = from.EdgeStart; edgeIndex < from.EdgeStart + from.EdgeCount; edgeIndex++)
        {
            NavigationGridEdge edge = grid.Edges.Span[edgeIndex];
            NavigationGridCell to = grid.Cells.Span[edge.ToCell];
            if (edge.Action != 0 || (edge.RequiredCapabilities & capabilities) != edge.RequiredCapabilities ||
                !float.IsFinite(input.Costs.EdgeCosts.Span[edgeIndex]) ||
                grid.GetCoordinate(to.Position) != new NavigationCellOffset(coordinate.X + x, coordinate.Z + z)) continue;
            bool reciprocal = false;
            for (int reverse = to.EdgeStart; reverse < to.EdgeStart + to.EdgeCount; reverse++)
            {
                NavigationGridEdge other = grid.Edges.Span[reverse];
                if (other.ToCell == cell && other.Action == 0 &&
                    (other.RequiredCapabilities & capabilities) == other.RequiredCapabilities &&
                    float.IsFinite(input.Costs.EdgeCosts.Span[reverse]))
                {
                    reciprocal = true;
                    break;
                }
            }
            float distance = MathF.Abs(from.Position.Y - to.Position.Y);
            if (!reciprocal || distance >= heightDistance) continue;
            selected = edgeIndex;
            heightDistance = distance;
        }
        return selected;
    }

    private static int Neighbor(NavigationFlowInput input, int cell, int x, int z)
    {
        int edge = FindEdge(input, cell, x, z);
        return edge >= 0 ? input.Costs.Grid.Edges.Span[edge].ToCell : -1;
    }

    internal static Vector3 Direction(NavigationFlowField field, int cell, Vector3 position)
    {
        if (field.Sampling == NavigationFlowSampling.Successor) return field.Directions.Span[cell];
        if (!float.IsFinite(field.CumulativeCosts.Span[cell])) return Vector3.Zero;
        NavigationGrid grid = field.Input.Costs.Grid;
        Vector3 offset = (position - grid.Cells.Span[cell].Position) / grid.CellSize;
        int signX = offset.X >= 0 ? 1 : -1, signZ = offset.Z >= 0 ? 1 : -1;
        float weightX = MathF.Abs(offset.X), weightZ = MathF.Abs(offset.Z);
        int x = Neighbor(field.Input, cell, signX, 0), z = Neighbor(field.Input, cell, 0, signZ);
        int diagonalX = x >= 0 ? Neighbor(field.Input, x, 0, signZ) : -1;
        int diagonalZ = z >= 0 ? Neighbor(field.Input, z, signX, 0) : -1;
        Vector3 direction = field.Directions.Span[cell] * ((1 - weightX) * (1 - weightZ));
        int goal = field.GoalIndices.Span[cell];
        AddDirection(field, x, goal, weightX * (1 - weightZ), ref direction);
        AddDirection(field, z, goal, (1 - weightX) * weightZ, ref direction);
        if (diagonalX == diagonalZ) AddDirection(field, diagonalX, goal, weightX * weightZ, ref direction);
        if (direction.X != 0 && Neighbor(field.Input, cell, Math.Sign(direction.X), 0) < 0) direction.X = 0;
        if (direction.Z != 0 && Neighbor(field.Input, cell, 0, Math.Sign(direction.Z)) < 0) direction.Z = 0;
        return direction.LengthSquared() > 0 ? Vector3.Normalize(direction) : Vector3.Zero;
    }

    private static void AddDirection(NavigationFlowField field, int cell, int goal, float weight, ref Vector3 direction)
    {
        if (cell >= 0 && field.GoalIndices.Span[cell] == goal)
            direction += field.Directions.Span[cell] * weight;
    }

    internal static Vector3 Displacement(NavigationFlowField field, int cell, Vector3 position, float distance)
    {
        NavigationGrid grid = field.Input.Costs.Grid;
        NavigationFlowGoal goal = field.Input.Goals.Span[field.GoalIndices.Span[cell]];
        if (field.CumulativeCosts.Span[cell] == 0)
        {
            Vector3 target = goal.Position - position;
            float length = target.Length();
            return length > 0 ? target * (MathF.Min(distance, MathF.Max(0, length - goal.ArrivalRange)) / length) : Vector3.Zero;
        }
        Vector3 direction = Direction(field, cell, position);
        Vector3 movement = direction * MathF.Min(distance, grid.CellSize * 0.45f);
        NavigationCellOffset coordinate = grid.GetCoordinate(position), destination = grid.GetCoordinate(position + movement);
        int x = destination.X - coordinate.X, z = destination.Z - coordinate.Z;
        int next = cell;
        if (x != 0 && z != 0)
        {
            int acrossX = Neighbor(field.Input, cell, x, 0), acrossZ = Neighbor(field.Input, cell, 0, z);
            int diagonalX = acrossX >= 0 ? Neighbor(field.Input, acrossX, 0, z) : -1;
            int diagonalZ = acrossZ >= 0 ? Neighbor(field.Input, acrossZ, x, 0) : -1;
            if (diagonalX >= 0 && diagonalX == diagonalZ) next = diagonalX;
            else
            {
                // Keep the segment inside one available passage at an incomplete corner.
                if (acrossX >= 0 && (acrossZ < 0 || MathF.Abs(movement.X) >= MathF.Abs(movement.Z)))
                { movement.Z = 0; next = acrossX; }
                else if (acrossZ >= 0) { movement.X = 0; next = acrossZ; }
                else return Vector3.Zero;
            }
        }
        else if (x != 0) next = Neighbor(field.Input, cell, x, 0);
        else if (z != 0) next = Neighbor(field.Input, cell, 0, z);
        if (next < 0) return Vector3.Zero;
        movement.Y = grid.Cells.Span[next].Position.Y - position.Y;
        return movement;
    }
}
