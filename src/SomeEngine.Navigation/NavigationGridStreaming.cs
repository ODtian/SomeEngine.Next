using System.Numerics;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public sealed class NavigationGridFlowBuildState
{
    public NavigationFlow Flow { get; internal set; } = null!;
}

public static class NavigationGridFlowBuilder
{
    public static NavigationBuild<NavigationGridFlowBuildState> Schedule(NavigationGridRegionGraph graph,
        ReadOnlyMemory<NavigationGridTile> tiles, NavigationProfile profile, NavigationPolicy policy,
        ReadOnlyMemory<NavigationFlowGoal> goals, Scheduler? scheduler = null, JobHandle dependency = default)
    {
        scheduler ??= JobSystem.Default;
        var state = new NavigationGridFlowBuildState();
        var accesses = new List<JobResourceAccess>
        {
            NavigationJobAccess.Read(scheduler, tiles), NavigationJobAccess.Read(scheduler, goals),
            NavigationJobAccess.Read(scheduler, policy.AreaCosts), NavigationJobAccess.Read(scheduler, policy.ActionCosts),
        };
        graph.AddReadAccesses(scheduler, accesses);
        foreach (NavigationGridTile tile in tiles.Span) tile.Grid.AddReadAccesses(scheduler, accesses);
        return new(state, scheduler.Schedule(new BuildFlow(state, graph, tiles, profile, policy, goals), accesses.ToArray(), dependency));
    }

    private readonly struct BuildFlow(NavigationGridFlowBuildState state, NavigationGridRegionGraph graph,
        ReadOnlyMemory<NavigationGridTile> tiles, NavigationProfile profile, NavigationPolicy policy,
        ReadOnlyMemory<NavigationFlowGoal> goals) : IJob
    {
        public void Execute()
        {
            NavigationGrid grid = NavigationGrid.Compose(graph, tiles, goals, policy);
            state.Flow = new(NavigationCostSystem.Create(grid, policy, graph.CostVersion), profile, goals);
        }
    }
}

public readonly record struct NavigationGridStreamingRead(NavigationTileCoordinate Tile, JobHandle Completion);

/// <summary>Reads a shared field's detailed grid tiles from ordinary interests and its forward boundary route.</summary>
public sealed class NavigationGridStreaming : IDisposable
{
    private readonly NavigationFlow _flow;
    private readonly Scheduler _scheduler;
    private NavigationTileSource<NavigationGrid> _source;
    private readonly Dictionary<NavigationTileCoordinate, NavigationTileRead<NavigationGrid>> _reads = [];
    public IList<NavigationInterest> Interests { get; } = new List<NavigationInterest>();
    public int ResidentTileBudget { get; set; } = int.MaxValue;
    public JobHandle Applied { get; private set; }
    public IReadOnlyList<NavigationGridStreamingRead> Reads => _reads.Select(pair => new NavigationGridStreamingRead(pair.Key, pair.Value.Completion)).ToArray();

    public NavigationGridStreaming(NavigationFlow flow, NavigationTileSource<NavigationGrid> source, Scheduler? scheduler = null) =>
        (_flow, _source, _scheduler) = (flow, source.Retain(), scheduler ?? JobSystem.Default);

    public void SetSource(NavigationTileSource<NavigationGrid> source)
    {
        NavigationTileSource<NavigationGrid> previous = _source;
        _source = source.Retain();
        previous.Dispose();
    }

    public void Update()
    {
        if (!Applied.IsCompleted) return;
        Applied.Complete();
        NavigationGrid grid = _flow.Input.Costs.Grid;
        NavigationGridRegionGraph graph = grid.RegionGraph!;
        var demands = new HashSet<NavigationTileCoordinate>();
        foreach (NavigationInterest interest in Interests)
        {
            foreach (NavigationRegionTile tile in graph.Tiles.Span)
                if (Distance(interest.Position, tile.Bounds) <= interest.Radius ||
                    Distance(interest.Position + interest.Direction * interest.LookAheadDistance, tile.Bounds) <= interest.Radius)
                    demands.Add(tile.Tile);
            if (_flow.Field is not { } field) continue;
            NavigationGrid current = field.Input.Costs.Grid;
            int cell = current.FindCell(interest.Position);
            float ahead = 0;
            for (int visited = 0; cell >= 0 && visited < current.Cells.Length; visited++)
            {
                demands.Add(current.Cells.Span[cell].Tile);
                int edge = field.SuccessorEdges.Span[cell];
                if (edge < 0) break;
                int next = current.Edges.Span[edge].ToCell;
                ahead += Vector3.Distance(current.Cells.Span[cell].Position, current.Cells.Span[next].Position);
                if (ahead > interest.LookAheadDistance) break;
                cell = next;
            }
        }
        float Near(NavigationTileCoordinate coordinate)
        {
            NavigationBounds bounds = graph.Tiles.Span.Find(coordinate).Bounds;
            float distance = float.PositiveInfinity;
            foreach (NavigationInterest interest in Interests) distance = MathF.Min(distance, Distance(interest.Position, bounds));
            return distance;
        }
        HashSet<NavigationTileCoordinate> selected = demands.OrderBy(Near).Take(ResidentTileBudget).ToHashSet();
        HashSet<NavigationTileCoordinate> resident = grid.Tiles.ToArray().Select(tile => tile.Tile).ToHashSet();
        foreach (NavigationTileCoordinate tile in selected)
            if (!resident.Contains(tile) && !_reads.ContainsKey(tile)) _reads.Add(tile, _source.ScheduleRead(tile, _scheduler));
        var loaded = new List<NavigationGridTile>();
        foreach (var pair in _reads.ToArray())
            if (pair.Value.Completion.IsCompleted)
            {
                loaded.Add(new(pair.Key, pair.Value.Complete()));
                _reads.Remove(pair.Key);
            }
        HashSet<NavigationTileCoordinate> keep = resident.Concat(loaded.Select(tile => tile.Tile)).Distinct()
            .OrderByDescending(selected.Contains).ThenBy(Near).Take(ResidentTileBudget).ToHashSet();
        NavigationTileCoordinate[] removed = resident.Where(tile => !keep.Contains(tile)).ToArray();
        loaded.RemoveAll(tile => !keep.Contains(tile.Tile));
        if (loaded.Count == 0 && removed.Length == 0) return;
        Applied = _scheduler.Schedule(new ScheduleApply(_flow, loaded.ToArray(), removed, _scheduler),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_flow)));
    }

    private static float Distance(Vector3 position, NavigationBounds bounds) => Vector3.Distance(position,
        Vector3.Clamp(position, bounds.Minimum, bounds.Maximum));

    public void Dispose()
    {
        foreach (NavigationTileRead<NavigationGrid> read in _reads.Values) read.Completion.Complete();
        Applied.Complete();
        _source.Dispose();
        _reads.Clear();
    }

    private readonly struct ScheduleApply(NavigationFlow flow, NavigationGridTile[] loaded,
        NavigationTileCoordinate[] removed, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            NavigationFlowInput input = flow.Input;
            var accesses = new List<JobResourceAccess>
            {
                JobResourceAccess.Write(scheduler.GetResourceToken(flow)), NavigationJobAccess.Read(scheduler, input.Goals),
                NavigationJobAccess.Read(scheduler, input.Costs.Policy.AreaCosts), NavigationJobAccess.Read(scheduler, input.Costs.Policy.ActionCosts),
                NavigationJobAccess.Read(scheduler, input.Costs.Grid.Tiles),
            };
            input.Costs.Grid.RegionGraph!.AddReadAccesses(scheduler, accesses);
            foreach (NavigationGridTile tile in input.Costs.Grid.Tiles.Span) tile.Grid.AddReadAccesses(scheduler, accesses);
            foreach (NavigationGridTile tile in loaded) tile.Grid.AddReadAccesses(scheduler, accesses);
            scheduler.Schedule(new ApplyTiles(flow, input, loaded, removed, scheduler), accesses.ToArray());
        }
    }

    private readonly struct ApplyTiles(NavigationFlow flow, NavigationFlowInput input, NavigationGridTile[] loaded,
        NavigationTileCoordinate[] removed, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            if (!ReferenceEquals(flow.Input, input)) { Retry(); return; }
            NavigationGridRegionGraph graph = input.Costs.Grid.RegionGraph!;
            var tiles = input.Costs.Grid.Tiles.ToArray().ToDictionary(tile => tile.Tile);
            foreach (NavigationTileCoordinate tile in removed) tiles.Remove(tile);
            foreach (NavigationGridTile tile in loaded)
                if (graph.Tiles.Span.Find(tile.Tile).ContentVersion == tile.Grid.ContentVersion) tiles[tile.Tile] = tile;
            NavigationGrid grid = NavigationGrid.Compose(graph, tiles.Values.ToArray(), input.Goals, input.Costs.Policy);
            if (!flow.ApplyCosts(input, NavigationCostSystem.Create(grid, input.Costs.Policy, input.Costs.Version))) Retry();
        }
        private void Retry() => scheduler.Schedule(new ScheduleApply(flow, loaded, removed, scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(flow)));
    }
}

public readonly struct NavigationGridStreamingSystem(NavigationGridStreaming streaming) : ISystem<ImmediateSystemContext>
{
    public void OnUpdate(ref ImmediateSystemContext context) => streaming.Update();
}

internal static class NavigationRegionDirectory
{
    internal static NavigationRegionTile Find(this ReadOnlySpan<NavigationRegionTile> tiles, NavigationTileCoordinate coordinate)
    {
        foreach (NavigationRegionTile tile in tiles) if (tile.Tile == coordinate) return tile;
        throw new KeyNotFoundException();
    }
}
