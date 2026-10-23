using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Navigation;

namespace SomeEngine.Assets.Tests;

public sealed class NavigationGridStreamingTests
{
    [Fact]
    public async Task StreamedFieldChoosesTheResidentRouteAndMovesItsFollowerAcrossThreeTiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SomeEngine-GridStreaming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var signals = new TileSignals();
        try
        {
            using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3 });
            var profile = new NavigationProfile([new(-0.1f, -0.1f), new(0.1f, -0.1f), new(0.1f, 0.1f), new(-0.1f, 0.1f)], 1);
            var policy = new NavigationPolicy();
            NavigationFlowGoal[] goals = [new(new(11.5f, 0, 1.5f), 0.05f)];
            NavigationGrid grid = NavigationGridRegionBuilder.Schedule(TwoRoutes(), profile, policy, scheduler: scheduler).Complete().Grid;
            NavigationCosts residentCosts = NavigationCostSystem.Schedule(grid, policy, scheduler: scheduler).Complete().Costs;
            var resident = new NavigationFlow(residentCosts, profile, goals);
            new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(resident, scheduler).Schedule().Complete();
            var selection = new NavigationGridSelection(new(Guid.NewGuid()), "main");
            string path = Path.Combine(directory, "route.navigation.asset");
            using (var asset = new NavigationAsset())
            using (var block = new NavigationGridBlock())
            {
                block.SetGrid(selection, grid);
                asset.SetBlock(block);
                await asset.SaveAsync(path);
            }
            NavigationGridTileSource source;
            using (var asset = await NavigationAsset.OpenAsync(path))
                source = (await asset.GetBlockAsync<NavigationGridBlock>()).CreateTileSource(selection);
            NavigationGrid first = source.ScheduleRead(new(0, 0), scheduler).Complete();
            NavigationFlow flow = NavigationGridFlowBuilder.Schedule(source.Graph,
                new NavigationGridTile[] { new(new(0, 0), first) }, profile, policy, goals, scheduler: scheduler).Complete().Flow;
            using var controlled = new ControlledGridSource(source, signals);
            using var streaming = new NavigationGridStreaming(flow, controlled, scheduler);
            source.Dispose();
            controlled.Dispose();
            var interest = new NavigationInterest(new(0.5f, 0, 1.5f), 0.1f) { LookAheadDistance = 5 };
            streaming.Interests.Add(interest);
            var solver = new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(flow, scheduler);
            using World world = WorldJobs.CreateWorld(scheduler: scheduler);
            var navigation = new NavigationSystems<Motion, MotionReader>(world);
            var actor = world.CreateEntity(new Motion(interest.Position, default, 0));
            navigation.AddFlowAgent(actor, flow, 2);
            var visited = new List<Vector3>();
            try
            {
                for (int tile = 1; tile <= 3; tile++)
                {
                    for (int frame = 0; frame < 100; frame++)
                    {
                        interest.Position = world.Read<Motion>(actor).Position;
                        streaming.Update();
                        JobHandle solved = solver.Schedule(64, streaming.Applied).Completion;
                        navigation.Schedule(0.1f, solved).Steering.Complete();
                        Motion motion = world.Read<Motion>(actor);
                        NavigationSteering steering = world.Read<NavigationSteering>(actor);
                        Vector3 position = motion.Position + steering.Velocity * 0.1f;
                        world.Replace(actor, new Motion(position, steering.Velocity, steering.Heading));
                        visited.Add(position);
                        navigation.ScheduleProgress(default).Complete();
                        if (world.Read<NavigationProgress>(actor).Status is NavigationStatus.WaitingForTiles or NavigationStatus.Arrived) break;
                    }
                    if (tile == 3) break;
                    Assert.Equal(tile * 4 - 0.5f, world.Read<Motion>(actor).Position.X, 3);
                    Assert.Contains(new NavigationTileCoordinate(tile, 0), flow.Field!.RequiredTiles.ToArray());
                    streaming.Update();
                    await signals.Started[tile - 1].Task;
                    signals.Release[tile - 1].SetResult();
                    streaming.Reads.Single(read => read.Tile == new NavigationTileCoordinate(tile, 0)).Completion.Complete();
                }
            }
            finally { foreach (TaskCompletionSource signal in signals.Release) signal.TrySetResult(); }
            Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(actor).Status);
            Assert.True(visited.Max(position => position.Z) > 2.4f);
            Assert.True(visited.Min(position => position.Z) > 1.4f);
            Vector3 start = new(0.5f, 0, 1.5f);
            int streamedCell = flow.Field!.Input.Costs.Grid.FindCell(start);
            int residentCell = resident.Field!.Input.Costs.Grid.FindCell(start);
            Assert.Equal(resident.Field.CumulativeCosts.Span[residentCell], flow.Field.CumulativeCosts.Span[streamedCell]);
            Assert.Equal(resident.Field.SampleDirection(start), flow.Field.SampleDirection(start));
            Assert.Equal(3, flow.Field.Tiles.Length);
            Assert.All(flow.Field.BoundaryDependencies.ToArray(), dependency => Assert.Equal(flow.Field.Round, dependency.Round));
        }
        finally
        {
            foreach (TaskCompletionSource signal in signals.Release) signal.TrySetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    // These three tiles and two passages are an example; the lower passage carries fivefold terrain cost.
    private static NavigationGrid TwoRoutes()
    {
        var cells = new List<NavigationGridCell>();
        var indices = new Dictionary<(int X, int Z), int>();
        for (int x = 0; x < 12; x++)
        for (int z = 0; z < 3; z++)
        {
            if (x >= 2 && x <= 9 && z == 1) continue;
            indices.Add((x, z), cells.Count);
            cells.Add(new(new(x + 0.5f, 0, z + 0.5f), 0, 0, BaseCost: z == 0 ? 5 : 1, Tile: new(x / 4, 0)));
        }
        var edges = new List<NavigationGridEdge>();
        foreach (var pair in indices)
        {
            int first = edges.Count;
            foreach ((int x, int z) in new (int, int)[] { (pair.Key.X - 1, pair.Key.Z), (pair.Key.X + 1, pair.Key.Z),
                (pair.Key.X, pair.Key.Z - 1), (pair.Key.X, pair.Key.Z + 1) })
                if (indices.TryGetValue((x, z), out int target)) edges.Add(new(target, 1));
            cells[pair.Value] = cells[pair.Value] with { EdgeStart = first, EdgeCount = edges.Count - first };
        }
        return new(new(new(0, 0, 0), new(12, 1, 3)), 1, cells.ToArray(), edges.ToArray());
    }

    private sealed class TileSignals
    {
        public TaskCompletionSource[] Started { get; } = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        public TaskCompletionSource[] Release { get; } = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
    }
    private sealed class ControlledGridSource(NavigationTileSource<NavigationGrid> source, TileSignals signals) : NavigationTileSource<NavigationGrid>
    {
        private readonly NavigationTileSource<NavigationGrid> _source = source.Retain();
        public override ReadOnlyMemory<NavigationRegionTile> Directory => _source.Directory;
        public override NavigationTileSource<NavigationGrid> Retain() => new ControlledGridSource(_source, signals);
        public override async ValueTask<NavigationTileBytes> ReadAsync(NavigationTileCoordinate tile, CancellationToken cancellationToken = default)
        {
            if (tile.X > 0)
            {
                signals.Started[tile.X - 1].TrySetResult();
                await signals.Release[tile.X - 1].Task.WaitAsync(cancellationToken);
            }
            return await _source.ReadAsync(tile, cancellationToken);
        }
        public override NavigationGrid Decode(NavigationTileCoordinate tile, NavigationTileBytes bytes) => _source.Decode(tile, bytes);
        public override void Dispose() => _source.Dispose();
    }
    private readonly record struct Motion(Vector3 Position, Vector3 Velocity, float Heading) : IComponent;
    private readonly struct MotionReader : INavigationMotionReader<Motion>
    {
        public static NavigationMotion Read(in Motion value) => new(value.Position, value.Velocity, value.Heading, true);
    }
}
