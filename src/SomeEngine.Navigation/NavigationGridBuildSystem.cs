using SomeEngine.Job;

namespace SomeEngine.Navigation;

public sealed class NavigationGridUpdate(NavigationRegionSources region)
{
    public NavigationRegionSources Region { get; } = region;
    public NavigationSourceInput Input { get; internal set; } = null!;
    public NavigationGrid Grid { get; internal set; } = null!;
    public NavigationCosts Costs { get; internal set; } = null!;
}

/// <summary>Derives shared grid costs from a fixed source build after its region Apply completes.</summary>
public sealed class NavigationGridBuildSystem(NavigationRegionSources region,
    ReadOnlyMemory<NavigationGridConnection> connections = default,
    NavigationGridFootprintMode footprintMode = NavigationGridFootprintMode.AllHeadings, Scheduler? scheduler = null)
{
    private readonly Scheduler _scheduler = scheduler ?? JobSystem.Default;
    private JobHandle _completion;
    private NavigationGridUpdate? _completed;
    public NavigationGridUpdate? Completed => Volatile.Read(ref _completed);
    public ReadOnlyMemory<NavigationGridConnection> Connections => connections;
    public NavigationGridFootprintMode FootprintMode => footprintMode;

    public NavigationBuild<NavigationGridUpdate> Schedule(NavigationBuild<NavigationRegionUpdate> build, JobHandle regionApplied)
    {
        var state = new NavigationGridUpdate(region);
        _completion = _scheduler.Schedule(new Begin(this, state, build.State, connections, footprintMode),
            [JobResourceAccess.Read(_scheduler.GetResourceToken(region)), JobResourceAccess.Exclusive(_scheduler.GetResourceToken(this))],
            _scheduler.CombineDependencies([_completion, build.Completion, regionApplied]));
        return new(state, _completion);
    }

    public NavigationTileSource<NavigationGrid> CreateTileSource()
    {
        NavigationGrid grid = Completed!.Grid;
        return new BuiltTileSource(grid.RegionGraph!.Tiles, grid.Tiles.ToArray().ToDictionary(tile => tile.Tile, tile => tile.Grid));
    }

    private readonly struct Begin(NavigationGridBuildSystem system, NavigationGridUpdate state, NavigationRegionUpdate built,
        ReadOnlyMemory<NavigationGridConnection> links, NavigationGridFootprintMode mode) : IJob
    {
        public void Execute()
        {
            state.Input = built.Input;
            NavigationGridUpdate? previous = system.Completed;
            if (ReferenceEquals(previous?.Input, state.Input))
            {
                state.Grid = previous!.Grid;
                state.Costs = previous.Costs;
                return;
            }
            NavigationBuild<NavigationGridBuildState> grid = NavigationGridBuilder.Schedule(built.Surface, state.Input.Settings.Profile,
                links, state.Input.Version, system._scheduler, footprintMode: mode);
            system._scheduler.Schedule(new BuildGraph(system, state, grid.State, previous), grid.Completion);
        }
    }

    private readonly struct BuildGraph(NavigationGridBuildSystem system, NavigationGridUpdate state,
        NavigationGridBuildState grid, NavigationGridUpdate? previous) : IJob
    {
        public void Execute()
        {
            NavigationBuild<NavigationGridRegionBuildState> graph = NavigationGridRegionBuilder.Schedule(grid.Grid,
                state.Input.Settings.Profile, state.Input.Settings.Policy, system._scheduler);
            system._scheduler.Schedule(new ScheduleComplete(system, state, graph.State, previous), graph.Completion);
        }
    }

    private readonly struct ScheduleComplete(NavigationGridBuildSystem system, NavigationGridUpdate state,
        NavigationGridRegionBuildState graph, NavigationGridUpdate? previous) : IJob
    {
        public void Execute()
        {
            Scheduler scheduler = system._scheduler;
            var accesses = new List<JobResourceAccess>
            {
                JobResourceAccess.Exclusive(scheduler.GetResourceToken(system)), JobResourceAccess.Read(scheduler.GetResourceToken(state.Region)),
                NavigationJobAccess.Read(scheduler, state.Input.TileVersions),
                NavigationJobAccess.Read(scheduler, state.Input.Settings.Policy.AreaCosts), NavigationJobAccess.Read(scheduler, state.Input.Settings.Policy.ActionCosts),
                NavigationJobAccess.Read(scheduler, graph.Grid.Tiles),
            };
            graph.Grid.AddReadAccesses(scheduler, accesses);
            foreach (NavigationGridTile tile in graph.Grid.Tiles.Span) tile.Grid.AddReadAccesses(scheduler, accesses);
            if (previous is not null)
            {
                accesses.Add(NavigationJobAccess.Read(scheduler, previous.Grid.Tiles));
                foreach (NavigationGridTile tile in previous.Grid.Tiles.Span) tile.Grid.AddReadAccesses(scheduler, accesses);
            }
            scheduler.Schedule(new CompleteBuild(system, state, graph.Grid, previous), accesses.ToArray());
        }
    }

    private readonly struct CompleteBuild(NavigationGridBuildSystem system, NavigationGridUpdate state,
        NavigationGrid grid, NavigationGridUpdate? previous) : IJob
    {
        public void Execute()
        {
            NavigationSourceInput input = state.Input;
            NavigationGridRegionGraph built = grid.RegionGraph!;
            NavigationRegionTile[] directory = built.Tiles.ToArray();
            float tileWidth = input.Geometry.Settings.CellSize * input.Geometry.Settings.TileSize;
            int width = (int)MathF.Ceiling((state.Region.Bounds.Maximum.X - state.Region.Bounds.Minimum.X) / tileWidth);
            var old = previous?.Grid.Tiles.ToArray().ToDictionary(tile => tile.Tile, tile => tile.Grid);
            var tiles = new NavigationGridTile[grid.Tiles.Length];
            for (int index = 0; index < tiles.Length; index++)
            {
                NavigationGridTile tile = grid.Tiles.Span[index];
                ulong version = input.TileVersions.Span[tile.Tile.Z * width + tile.Tile.X];
                NavigationGrid replacement = old is not null && old.TryGetValue(tile.Tile, out NavigationGrid? same) && same.ContentVersion == version
                    ? same : new(tile.Grid.Bounds, tile.Grid.CellSize, tile.Grid.Cells, tile.Grid.Edges, tile.Grid.Shape, version);
                tiles[index] = new(tile.Tile, replacement);
                directory[index] = directory[index] with { ContentVersion = version };
            }
            var graph = new NavigationGridRegionGraph(built.Bounds, built.CellSize, built.Shape, directory, built.Entrances,
                built.Edges, built.Crossings, input.Version, input.Version);
            state.Grid = NavigationGrid.Compose(graph, tiles, default, input.Settings.Policy, previous?.Grid.ResidenceVersion ?? 1);
            state.Costs = NavigationCostSystem.Create(state.Grid, input.Settings.Policy, input.Version);
            lock (state.Region.Gate)
                if (ReferenceEquals(state.Region.Input, input) && ReferenceEquals(state.Region.Completed?.Input, input))
                    Volatile.Write(ref system._completed, state);
        }
    }

    private sealed class BuiltTileSource(ReadOnlyMemory<NavigationRegionTile> directory,
        Dictionary<NavigationTileCoordinate, NavigationGrid> tiles) : NavigationTileSource<NavigationGrid>
    {
        public override ReadOnlyMemory<NavigationRegionTile> Directory => directory;
        public override NavigationTileSource<NavigationGrid> Retain() => new BuiltTileSource(directory, tiles);
        public override ValueTask<NavigationTileBytes> ReadAsync(NavigationTileCoordinate tile, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new NavigationTileBytes([]));
        }
        public override NavigationGrid Decode(NavigationTileCoordinate tile, NavigationTileBytes bytes) => tiles[tile];
        public override void Dispose() { }
    }
}

public static class NavigationGridApplySystem
{
    public static JobHandle Schedule(NavigationBuild<NavigationGridUpdate> build, ReadOnlyMemory<NavigationFlow> flows,
        Scheduler? scheduler = null, JobHandle dependency = default)
    {
        scheduler ??= JobSystem.Default;
        var accesses = new List<JobResourceAccess> { JobResourceAccess.Read(scheduler.GetResourceToken(build.State)),
            JobResourceAccess.Read(scheduler.GetResourceToken(build.State.Region)), NavigationJobAccess.Read(scheduler, flows) };
        foreach (NavigationFlow flow in flows.Span) accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(flow)));
        return scheduler.Schedule(new ReadInputs(build.State, flows, scheduler), accesses.ToArray(),
            scheduler.CombineDependencies([build.Completion, dependency]));
    }

    private readonly struct ReadInputs(NavigationGridUpdate state, ReadOnlyMemory<NavigationFlow> flows, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            NavigationFlowInput[] inputs = flows.ToArray().Select(flow => flow.Input).ToArray();
            var accesses = new List<JobResourceAccess>
            {
                JobResourceAccess.Read(scheduler.GetResourceToken(state.Region)), NavigationJobAccess.Read(scheduler, state.Grid.Tiles),
                NavigationJobAccess.Read(scheduler, state.Costs.Policy.AreaCosts), NavigationJobAccess.Read(scheduler, state.Costs.Policy.ActionCosts),
            };
            state.Grid.AddReadAccesses(scheduler, accesses);
            foreach (NavigationGridTile tile in state.Grid.Tiles.Span) tile.Grid.AddReadAccesses(scheduler, accesses);
            foreach (NavigationFlowInput input in inputs)
            {
                input.Costs.Grid.AddReadAccesses(scheduler, accesses);
                accesses.Add(NavigationJobAccess.Read(scheduler, input.Costs.Grid.Tiles));
                accesses.Add(NavigationJobAccess.Read(scheduler, input.Goals));
            }
            foreach (NavigationFlow flow in flows.Span) accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(flow)));
            scheduler.Schedule(new ApplyCosts(state, flows, inputs, scheduler), accesses.ToArray());
        }
    }

    private readonly struct ApplyCosts(NavigationGridUpdate state, ReadOnlyMemory<NavigationFlow> flows,
        NavigationFlowInput[] inputs, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            Scheduler currentScheduler = scheduler;
            lock (state.Region.Gate)
            {
                if (!ReferenceEquals(state.Region.Input, state.Input) || !ReferenceEquals(state.Region.Completed?.Input, state.Input) ||
                    !ReferenceEquals(state.Region.Settings, state.Input.Settings)) return;
                for (int index = 0; index < inputs.Length; index++)
                    if (!ReferenceEquals(flows.Span[index].Input, inputs[index]))
                    {
                        scheduler.Schedule(new ReadInputs(state, flows, scheduler), flows.ToArray()
                            .Select(flow => JobResourceAccess.Write(currentScheduler.GetResourceToken(flow))).ToArray());
                        return;
                    }
                var shared = new Dictionary<NavigationCosts, NavigationCosts>();
                for (int index = 0; index < inputs.Length; index++)
                {
                    NavigationFlowInput input = inputs[index];
                    if (!shared.TryGetValue(input.Costs, out NavigationCosts? costs))
                    {
                        NavigationGrid old = input.Costs.Grid;
                        bool partial = old.HasCoarseTiles;
                        costs = state.Costs;
                        if (partial || old.ResidenceVersion != state.Grid.ResidenceVersion)
                        {
                            var resident = old.Tiles.ToArray().Select(tile => tile.Tile).ToHashSet();
                            NavigationGridTile[] tiles = partial ? state.Grid.Tiles.ToArray().Where(tile => resident.Contains(tile.Tile)).ToArray()
                                : state.Grid.Tiles.ToArray();
                            NavigationFlowGoal[] goals = inputs.Where(item => ReferenceEquals(item.Costs, input.Costs))
                                .SelectMany(item => item.Goals.ToArray()).Distinct().ToArray();
                            NavigationGrid grid = NavigationGrid.Compose(state.Grid.RegionGraph!, tiles, goals,
                                state.Costs.Policy, old.ResidenceVersion);
                            costs = NavigationCostSystem.Create(grid, state.Costs.Policy, state.Costs.Version);
                        }
                        shared.Add(input.Costs, costs);
                    }
                    flows.Span[index].ApplyCosts(costs, state.Input.Settings.Profile);
                }
            }
        }
    }
}
