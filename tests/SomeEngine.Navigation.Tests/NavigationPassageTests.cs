using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationPassageTests
{
    [Fact]
    public void ActorsUseACapacityOneConnectionInTheirWaitingOrder()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)],
            1, maximumSpeed: 2);
        var policy = new NavigationPolicy();
        // These platforms and three actors are a fixture example of the shared passage's public capacity and queue.
        var mesh = new NavigationMesh([new(0, 0, 0), new(3, 0, 0), new(3, 0, 2), new(0, 0, 2)],
            [0, 1, 2, 3], [new(0, 4, 0, 0)], []);
        mesh = NavigationRegionBuilder.Schedule(mesh, profile, policy,
            [new NavigationRegionPort(1, default, new(2.5f, 0, 1)), new(2, default, new(0.5f, 0, 1))], scheduler: scheduler).Complete().Mesh;
        NavigationScene scene = NavigationSceneBuilder.Schedule(
            [new NavigationRegionPlacement(10, mesh, Matrix4x4.Identity), new(20, mesh, Matrix4x4.CreateTranslation(5, 0, 0))],
            Array.Empty<NavigationSceneConnection>(), profile, policy, scheduler: scheduler).Complete().Scene;
        var links = new NavigationLinkSet(new NavigationLink[]
        {
            new(71, new(10, 1), new(20, 2), 9, Parameters: new(0.3f, 0, 0, 0), Capacity: 1),
        });
        scene.ScheduleLinks(links, scheduler: scheduler).Complete();
        NavigationPassage passage = scene.Content.Links!.GetPassage(71);
        var passages = new NavigationPassageSystem(world);
        var navigation = new NavigationSystems<NavigationScene, SceneNavigationSearch, SceneSearchState,
            TestMotion, TestMotionReader>(world, passages);
        var actors = new Entity[3];
        for (int index = 0; index < actors.Length; index++)
        {
            Entity actor = world.CreateEntity(new TestMotion { Position = new(2.5f, 0, 1) });
            actors[index] = actor;
            world.Add(actor, new TestActionMotion());
            navigation.AddAgent(actor, scene, profile, policy, new(new Vector3(7, 0, 1), 0.05f), 2);
        }
        var began = new List<Entity>();
        for (int step = 0; step < 100 && actors.Any(actor => world.Read<NavigationProgress>(actor).Status != NavigationStatus.Arrived); step++)
        {
            NavigationStep next = navigation.Schedule(0.1f, 128);
            next.Steering.Complete();
            Assert.InRange(passage.InUse.Length, 0, 1);
            navigation.ScheduleProgress(new ConsumeActionOrSteering(0.1f).ScheduleParallel(world, next.Steering)).Complete();
            foreach (Entity actor in actors)
                if (world.Read<TestActionMotion>(actor).Count == 1 && !began.Contains(actor)) began.Add(actor);
        }

        Assert.Equal(actors, began);
        Assert.Equal(1, passage.Capacity);
        Assert.Empty(passage.Waiting.ToArray());
        Assert.Empty(passage.InUse.ToArray());
        Assert.All(actors, actor => Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(actor).Status));
    }
}
