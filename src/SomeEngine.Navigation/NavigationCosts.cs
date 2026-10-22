using SomeEngine.Job;

namespace SomeEngine.Navigation;

/// <summary>Completed policy costs shared by every field using this grid and policy.</summary>
public sealed class NavigationCosts(NavigationGrid grid, NavigationPolicy policy,
    ReadOnlyMemory<float> cellCosts, ReadOnlyMemory<float> edgeCosts, ulong version = 1)
{
    public NavigationGrid Grid { get; } = grid;
    public NavigationPolicy Policy { get; } = policy;
    public ReadOnlyMemory<float> CellCosts { get; } = cellCosts;
    public ReadOnlyMemory<float> EdgeCosts { get; } = edgeCosts;
    public ulong Version { get; } = version;
}

public sealed class NavigationCostBuildState(NavigationGrid grid, NavigationPolicy policy, ulong version)
{
    public NavigationGrid Grid { get; } = grid;
    public NavigationPolicy Policy { get; } = policy;
    public ulong Version { get; } = version;
    public NavigationCosts Costs { get; internal set; } = null!;
    internal float[] Cells { get; } = new float[grid.Cells.Length];
    internal float[] Edges { get; } = new float[grid.Edges.Length];
}

public static class NavigationCostSystem
{
    public static NavigationBuild<NavigationCostBuildState> Schedule(NavigationGrid grid, NavigationPolicy policy,
        ulong version = 1, Scheduler? scheduler = null, JobHandle dependency = default)
    {
        scheduler ??= JobSystem.Default;
        var state = new NavigationCostBuildState(grid, policy, version);
        JobHandle completion = scheduler.Schedule(new BuildCosts(state),
            [NavigationJobAccess.Read(scheduler, grid.Cells), NavigationJobAccess.Read(scheduler, grid.Edges),
             NavigationJobAccess.Read(scheduler, policy.AreaCosts), NavigationJobAccess.Read(scheduler, policy.ActionCosts),
             NavigationJobAccess.Write(scheduler, state.Cells), NavigationJobAccess.Write(scheduler, state.Edges)], dependency);
        return new(state, completion);
    }

    private readonly struct BuildCosts(NavigationCostBuildState state) : IJob
    {
        public void Execute()
        {
            NavigationGrid grid = state.Grid;
            for (int cell = 0; cell < grid.Cells.Length; cell++)
            {
                NavigationGridCell from = grid.Cells.Span[cell];
                float cost = from.BaseCost * state.Policy.GetAreaCost(from.Area);
                state.Cells[cell] = cost;
                for (int edge = from.EdgeStart; edge < from.EdgeStart + from.EdgeCount; edge++)
                {
                    NavigationGridEdge transition = grid.Edges.Span[edge];
                    state.Edges[edge] = transition.Cost * cost + state.Policy.GetActionCost(transition.Action);
                }
            }
            state.Costs = new(grid, state.Policy, state.Cells, state.Edges, state.Version);
        }
    }
}
