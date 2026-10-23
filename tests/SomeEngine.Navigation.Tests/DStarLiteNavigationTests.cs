using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class DStarLiteNavigationTests
{
    [Fact]
    public void MovingRequestRepairsChangedEntranceCostsAndArrivesThroughTheNewBestEntrance()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)], 1);
        var policy = new NavigationPolicy();
        // The two rectangles and two crossings are example scene inputs for changing directed connection costs.
        var floor = new NavigationMesh([new(0, 0, 0), new(0, 0, 8), new(6, 0, 8), new(6, 0, 0)], [0, 1, 2, 3], [new(0, 4, 0, 0)], []);
        NavigationMesh mesh = NavigationRegionBuilder.Schedule(floor, profile, policy,
            [new NavigationRegionPort(7, default, new(5.5f, 0, 2)), new(8, default, new(5.5f, 0, 6)),
             new(9, default, new(0.5f, 0, 2)), new(10, default, new(0.5f, 0, 6))], scheduler: scheduler).Complete().Mesh;
        NavigationScene scene = NavigationSceneBuilder.Schedule(
            [new NavigationRegionPlacement(10, mesh, Matrix4x4.Identity), new(20, mesh, Matrix4x4.CreateTranslation(6, 0, 0))],
            [new NavigationSceneConnection(90, 10, 7, 20, 9), new(91, 10, 8, 20, 10, Cost: 10)],
            profile, policy, scheduler: scheduler).Complete().Scene;
        var navigation = new NavigationSystems<NavigationScene, ObservedSearch, DStarLiteSearchState, TestMotion, TestMotionReader>(world);
        Entity actor = world.CreateEntity(new TestMotion { Position = new(1, 0, 2) });
        navigation.AddAgent(actor, scene, profile, policy, new(new Vector3(11, 0, 2), 0.05f), 2);
        navigation.Schedule(0.1f, 1).Steering.Complete();
        Assert.Equal(NavigationSearchStatus.Searching, world.Read<NavigationPathResult>(actor).Status);
        DStarLiteSearchState state = ObservedSearch.Current;
        for (int frame = 0; frame < 6; frame++)
            navigation.ScheduleProgress(new ConsumeSteering(0.1f).ScheduleParallel(world, navigation.Schedule(0.1f, 128).Steering)).Complete();
        NavigationPath original = world.Read<NavigationPathResult>(actor).Path!;
        Assert.Contains(new NavigationConnectionDependency(90, 1), original.ConnectionDependencies.ToArray());
        ulong version = world.Read<NavigationPathResult>(actor).RequestVersion;
        Assert.True(world.Read<TestMotion>(actor).Position.X > 1);

        NavigationSceneContent current = scene.Content;
        NavigationSceneConnection[] changed = current.Connections.ToArray();
        changed[0] = changed[0] with { Cost = 30, ContentVersion = 2 };
        changed[1] = changed[1] with { Cost = 0, ContentVersion = 2 };
        JobHandle updated = scene.ScheduleApply(new(current.Regions, changed, current.Tiles), scheduler: scheduler);
        navigation.Schedule(0.1f, 1, updated).Steering.Complete();
        Assert.Equal(NavigationSearchStatus.Searching, world.Read<NavigationPathResult>(actor).Status);
        var positions = new List<Vector3>();
        for (int frame = 0; frame < 300 && world.Read<NavigationProgress>(actor).Status != NavigationStatus.Arrived; frame++)
        {
            navigation.ScheduleProgress(new ConsumeSteering(0.1f).ScheduleParallel(world, navigation.Schedule(0.1f, 32).Steering)).Complete();
            positions.Add(world.Read<TestMotion>(actor).Position);
        }
        NavigationPath repaired = world.Read<NavigationPathResult>(actor).Path!;
        Assert.Same(state, ObservedSearch.Current);
        Assert.True(state.KeyModifier > 0);
        Assert.True(state.ReusedVertexCount > 0);
        Assert.Equal(version, world.Read<NavigationPathResult>(actor).RequestVersion);
        Assert.Contains(new NavigationConnectionDependency(91, 2), repaired.ConnectionDependencies.ToArray());
        Assert.Contains(positions, position => position.X > 5.4f && position.X < 6.6f && position.Z > 5.9f);
        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(actor).Status);

        world.Replace(actor, new NavigationGoal(new Vector3(1, 0, 2), 0.05f));
        navigation.Schedule(0.1f, 1).Steering.Complete();
        Assert.NotSame(state, ObservedSearch.Current);
        Assert.True(world.Read<NavigationPathResult>(actor).RequestVersion > version);
    }

    // This adapter observes the public search workspace while delegating all work to the production solver.
    private readonly struct ObservedSearch : INavigationSearch<NavigationScene, DStarLiteSearchState>
    {
        public static DStarLiteSearchState Current { get; private set; } = null!;
        public static DStarLiteSearchState Create(in NavigationSearchInput<NavigationScene> input) => Current = DStarLiteNavigationSearch.Create(in input);
        public static NavigationSearchStep Advance(DStarLiteSearchState state, int expansionBudget) => DStarLiteNavigationSearch.Advance(state, expansionBudget);
        public static bool Update(DStarLiteSearchState state, in NavigationSearchInput<NavigationScene> input) => DStarLiteNavigationSearch.Update(state, in input);
    }
}
