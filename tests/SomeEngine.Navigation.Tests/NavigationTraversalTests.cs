using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationTraversalTests
{
    [Fact]
    public void ChangedActionLinksHandOffMovementAndResumeNavigationAfterActualCompletion()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)],
            1, maximumSpeed: 2, capabilities: 1);
        var policy = new NavigationPolicy();
        // The separated rectangles and action identifiers are the fixture example for general link inputs.
        var mesh = new NavigationMesh([new(0, 0, 0), new(3, 0, 0), new(3, 0, 2), new(0, 0, 2)],
            [0, 1, 2, 3], [new(0, 4, 0, 0)], []);
        mesh = NavigationRegionBuilder.Schedule(mesh, profile, policy,
            [new NavigationRegionPort(7, new(0, 0), new(2.5f, 0, 1)), new(8, new(0, 0), new(0.5f, 0, 1))], scheduler: scheduler).Complete().Mesh;
        NavigationScene scene = NavigationSceneBuilder.Schedule(
            [new NavigationRegionPlacement(10, mesh, Matrix4x4.Identity), new(20, mesh, Matrix4x4.CreateTranslation(5, 0, 0))],
            Array.Empty<NavigationSceneConnection>(), profile, policy, scheduler: scheduler).Complete().Scene;
        NavigationLink[] definitions = [
            new(90, new(10, 7), new(20, 8), 43, Cost: 4, RequiredCapabilities: 1, Parameters: new(0.4f, 0, 0, 0)),
            new(91, new(10, 7), new(20, 8), 42, Cost: 0.2f, RequiredCapabilities: 1, Parameters: new(0.4f, 0, 0, 0), Enabled: false),
            new(92, new(10, 7), new(20, 8), 44, RequiredCapabilities: 2),
            new(93, new(10, 7), new(20, 8), 45, Cost: 8, RequiredCapabilities: 1)];
        scene.ScheduleLinks(new(definitions), scheduler: scheduler).Complete();
        var navigation = new NavigationSystems<NavigationScene, SceneNavigationSearch, SceneSearchState, TestMotion, TestMotionReader>(world);
        Entity actor = world.CreateEntity(new TestMotion { Position = new(1, 0, 1) });
        world.Add(actor, new TestActionMotion());
        navigation.AddAgent(actor, scene, profile, policy, new(new Vector3(7, 0, 1), 0.05f), 2);
        navigation.Schedule(0.1f, 128).Steering.Complete();
        Assert.Equal(43, world.Read<NavigationPathResult>(actor).Path!.Traversals.Span[0].Action);

        definitions = definitions.ToArray();
        definitions[0] = definitions[0] with { Enabled = false };
        definitions[1] = definitions[1] with { Enabled = true };
        scene.ScheduleLinks(new(definitions), scheduler: scheduler).Complete();
        for (int step = 0; step < 100 && world.Read<NavigationProgress>(actor).Status != NavigationStatus.Arrived; step++)
        {
            NavigationStep scheduled = navigation.Schedule(0.1f, 32);
            navigation.ScheduleProgress(new ConsumeActionOrSteering(0.1f).ScheduleParallel(world, scheduled.Steering)).Complete();
            Vector3 position = world.Read<TestMotion>(actor).Position;
            if (position.X > 3 && position.X < 5)
                Assert.Equal(NavigationStatus.Traversing, world.Read<NavigationProgress>(actor).Status);
        }

        TestActionMotion executed = world.Read<TestActionMotion>(actor);
        Assert.Equal(1, executed.Count);
        Assert.Equal(42, executed.Action);
        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(actor).Status);
    }
}

public struct TestActionMotion : IComponent
{
    public ulong Request;
    public Vector3 Start;
    public float Elapsed;
    public int Count;
    public int Action;
}

public partial struct ConsumeActionOrSteering(float deltaTime) : IJobEntity
{
    private readonly float _deltaTime = deltaTime;

    public void Execute(in NavigationSteering steering, in NavigationTraversalRequest request,
        ref NavigationTraversalResult result, ref TestActionMotion action, ref TestMotion motion)
    {
        if (request.Status is NavigationTraversalStatus.Requested or NavigationTraversalStatus.Executing)
        {
            if (action.Request != request.RequestId)
            {
                action.Request = request.RequestId;
                action.Start = motion.Position;
                action.Elapsed = 0;
                action.Action = request.Action;
                action.Count++;
            }
            action.Elapsed += _deltaTime;
            float progress = MathF.Min(1, action.Elapsed / request.Parameters.X);
            Vector3 position = Vector3.Lerp(action.Start, request.Exit, progress);
            motion.Velocity = (position - motion.Position) / _deltaTime;
            motion.Position = position;
            result = new(request.RequestId, progress == 1 ? NavigationTraversalExecution.Completed : NavigationTraversalExecution.Running, progress);
            return;
        }
        motion.Velocity = steering.Active ? steering.Velocity : Vector3.Zero;
        motion.Position += motion.Velocity * _deltaTime;
        if (steering.Active) motion.Heading = steering.Heading;
    }
}
