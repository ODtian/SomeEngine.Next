using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public sealed class ReverseDijkstraFlowState(NavigationFlowInput input) : NavigationFlowBuildState(input)
{
    internal bool Started;
    internal PriorityQueue<int, float> Open { get; } = new();
    internal float[] Values { get; } = new float[input.Costs.Grid.Cells.Length];
    internal int[] Successors { get; } = new int[input.Costs.Grid.Cells.Length];
    internal int[] Goals { get; } = new int[input.Costs.Grid.Cells.Length];
    internal Vector3[] Directions { get; } = new Vector3[input.Costs.Grid.Cells.Length];
}

/// <summary>Dijkstra's minimum-cost expansion over incoming edges, seeded by every accepted goal cell.</summary>
public readonly struct ReverseDijkstraFlow : INavigationFlowSolver<ReverseDijkstraFlowState>
{
    public static string Id => "reverse-dijkstra";
    public static ReverseDijkstraFlowState Create(NavigationFlowInput input) => new(input);

    public static JobHandle Schedule(Scheduler scheduler, ReverseDijkstraFlowState state, int budget, JobHandle dependency)
    {
        NavigationFlowInput input = state.Input;
        NavigationGrid grid = input.Costs.Grid;
        return scheduler.Schedule(new Integrate(state, budget),
            [JobResourceAccess.Exclusive(scheduler.GetResourceToken(state)),
             NavigationJobAccess.Read(scheduler, grid.Cells), NavigationJobAccess.Read(scheduler, grid.Edges),
             NavigationJobAccess.Read(scheduler, grid.IncomingRanges), NavigationJobAccess.Read(scheduler, grid.IncomingEdges),
             NavigationJobAccess.Read(scheduler, input.Costs.CellCosts), NavigationJobAccess.Read(scheduler, input.Costs.EdgeCosts),
             NavigationJobAccess.Read(scheduler, input.Goals), NavigationJobAccess.Write(scheduler, state.Values),
             NavigationJobAccess.Write(scheduler, state.Successors), NavigationJobAccess.Write(scheduler, state.Goals),
             NavigationJobAccess.Write(scheduler, state.Directions)], dependency);
    }

    private readonly struct Integrate(ReverseDijkstraFlowState state, int budget) : IJob
    {
        public void Execute()
        {
            if (state.Status == NavigationFlowStatus.Ready) return;
            NavigationFlowInput input = state.Input;
            NavigationGrid grid = input.Costs.Grid;
            if (!state.Started) Initialize(state);
            ulong capabilities = input.Profile.Capabilities & input.Costs.Policy.AllowedCapabilities;
            int expanded = 0;
            while (expanded < budget && state.Open.TryDequeue(out int to, out float cost))
            {
                if (cost != state.Values[to]) continue;
                expanded++;
                NavigationGridRange range = grid.IncomingRanges.Span[to];
                foreach (NavigationGridIncoming incoming in grid.IncomingEdges.Span.Slice(range.Start, range.Count))
                {
                    NavigationGridEdge edge = grid.Edges.Span[incoming.Edge];
                    if ((edge.RequiredCapabilities & capabilities) != edge.RequiredCapabilities) continue;
                    float candidate = cost + input.Costs.EdgeCosts.Span[incoming.Edge];
                    if (candidate >= state.Values[incoming.FromCell]) continue;
                    state.Values[incoming.FromCell] = candidate;
                    state.Successors[incoming.FromCell] = incoming.Edge;
                    state.Goals[incoming.FromCell] = state.Goals[to];
                    state.Open.Enqueue(incoming.FromCell, candidate);
                }
            }
            if (state.Open.Count > 0) return;
            for (int cell = 0; cell < state.Values.Length; cell++)
            {
                int next = state.Successors[cell];
                if (next < 0) continue;
                Vector3 displacement = grid.Cells.Span[grid.Edges.Span[next].ToCell].Position - grid.Cells.Span[cell].Position;
                state.Directions[cell] = Vector3.Normalize(displacement);
            }
            NavigationFlowDependency[] dependencies = grid.Cells.Span.ToArray().Select(cell => cell.Tile).Distinct()
                .Select(tile => new NavigationFlowDependency(tile, grid.ContentVersion, input.Costs.Version, input.GoalVersion)).ToArray();
            state.Field = new(input, state.Values, state.Successors, state.Directions, state.Goals, dependencies, Id);
            state.Status = NavigationFlowStatus.Ready;
        }
    }

    private static void Initialize(ReverseDijkstraFlowState state)
    {
        state.Started = true;
        Array.Fill(state.Values, float.PositiveInfinity);
        Array.Fill(state.Successors, -1);
        Array.Fill(state.Goals, -1);
        NavigationGrid grid = state.Input.Costs.Grid;
        for (int goalIndex = 0; goalIndex < state.Input.Goals.Length; goalIndex++)
        {
            NavigationFlowGoal goal = state.Input.Goals.Span[goalIndex];
            bool seeded = false;
            for (int cell = 0; cell < grid.Cells.Length; cell++)
            {
                if (Vector3.Distance(grid.Cells.Span[cell].Position, goal.Position) > goal.ArrivalRange) continue;
                Seed(state, cell, goalIndex);
                seeded = true;
            }
            if (!seeded)
            {
                int cell = grid.FindCell(goal.Position);
                if (cell >= 0) Seed(state, cell, goalIndex);
            }
        }
    }

    private static void Seed(ReverseDijkstraFlowState state, int cell, int goal)
    {
        if (state.Values[cell] == 0 || float.IsPositiveInfinity(state.Input.Costs.CellCosts.Span[cell])) return;
        state.Values[cell] = 0;
        state.Goals[cell] = goal;
        state.Open.Enqueue(cell, 0);
    }
}
