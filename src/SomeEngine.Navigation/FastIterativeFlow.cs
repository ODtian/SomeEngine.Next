using SomeEngine.Job;

namespace SomeEngine.Navigation;

public sealed class FastIterativeFlowState : NavigationFlowBuildState
{
    internal NavigationTileFlowState<FastIterativeTileWork> Tiles { get; }
    public FastIterativeFlowState(NavigationFlowInput input) : base(input) => Tiles = new(this);
    internal override void AddWorkAccesses(Scheduler scheduler, List<JobResourceAccess> accesses) => Tiles.AddReadAccesses(scheduler, accesses);
}

/// <summary>Active-set Eikonal integration retaining both upwind dependencies and each tile's work between changes.</summary>
public readonly struct FastIterativeFlow : INavigationFlowSolver<FastIterativeFlowState>
{
    public static string Id => "fast-iterative";
    public static FastIterativeFlowState Create(NavigationFlowInput input) => new(input);
    public static FastIterativeFlowState Update(FastIterativeFlowState state, NavigationFlowInput input)
    {
        NavigationTileFlow<FastIterativeTileSolver, FastIterativeTileWork>.Update(state.Tiles, input);
        return state;
    }
    public static JobHandle Schedule(Scheduler scheduler, FastIterativeFlowState state, int budget, JobHandle dependency) =>
        NavigationTileFlow<FastIterativeTileSolver, FastIterativeTileWork>.Schedule(scheduler, state.Tiles, budget, dependency);
}
