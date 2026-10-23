using SomeEngine.Job;

namespace SomeEngine.Navigation;

public sealed class ReverseDijkstraFlowState : NavigationFlowBuildState
{
    internal NavigationTileFlowState<DijkstraTileWork> Tiles { get; }
    public ReverseDijkstraFlowState(NavigationFlowInput input) : base(input) => Tiles = new(this);
    internal override void AddWorkAccesses(Scheduler scheduler, List<JobResourceAccess> accesses) => Tiles.AddReadAccesses(scheduler, accesses);
}

/// <summary>Dijkstra's directed predecessor expansion, retaining queues and values across input changes.</summary>
public readonly struct ReverseDijkstraFlow : INavigationFlowSolver<ReverseDijkstraFlowState>
{
    public static string Id => "reverse-dijkstra";
    public static ReverseDijkstraFlowState Create(NavigationFlowInput input) => new(input);
    public static ReverseDijkstraFlowState Update(ReverseDijkstraFlowState state, NavigationFlowInput input)
    {
        NavigationTileFlow<DijkstraTileSolver, DijkstraTileWork>.Update(state.Tiles, input);
        return state;
    }
    public static JobHandle Schedule(Scheduler scheduler, ReverseDijkstraFlowState state, int budget, JobHandle dependency) =>
        NavigationTileFlow<DijkstraTileSolver, DijkstraTileWork>.Schedule(scheduler, state.Tiles, budget, dependency);
}
