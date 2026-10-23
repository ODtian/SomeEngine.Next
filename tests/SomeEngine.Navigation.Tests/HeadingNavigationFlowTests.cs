using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class HeadingNavigationFlowTests
{
    [Fact]
    public void SharedFollowersTurnThroughAOneWayPassageAndHandOffTheSelectedAction()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new Vector2(-0.2f, -0.8f), new(0.2f, -0.8f), new(0.2f, 0.8f), new(-0.2f, 0.8f)],
            1.5f, maximumSpeed: 2, maximumAngularSpeed: MathF.PI);
        var policy = new NavigationPolicy();
        HeadingNavigationFlowRegion[] regions = Regions();
        var links = new NavigationLinkSet(new NavigationLink[]
        {
            new(71, new(10, 1), new(20, 2), 9, Parameters: new(0.3f, 0, 0, 0), Capacity: 1),
        });
        HeadingNavigationFlowGraph graph = HeadingNavigationFlowBuilder.Schedule(regions, links, profile, policy,
            scheduler: scheduler).Complete().Graph;
        var flow = new NavigationFlow(graph, profile, new NavigationFlowGoal[] { new(new(2, 0, 12), 0.05f, 0) });
        var solve = new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(flow, scheduler);
        var navigation = new NavigationSystems<TestMotion, TestMotionReader>(world);
        Entity[] actors = [world.CreateEntity(new TestMotion { Position = new(2, 0, 2), Heading = MathF.PI / 2 }),
            world.CreateEntity(new TestMotion { Position = new(2.1f, 0, 2), Heading = MathF.PI / 2 })];
        foreach (Entity actor in actors)
        {
            world.Add(actor, new TestActionMotion());
            navigation.AddFlowAgent(actor, flow, 2);
        }
        solve.Schedule().Complete();
        Assert.Equal(NavigationFlowAction.Rotate, flow.Field!.Sample(new(new(2, 0, 2), MathF.PI / 2)).Action);
        Assert.Equal(NavigationFlowAction.Move, flow.Field.Sample(new(new(2, 0, 5), 0)).Action);
        Assert.Equal(NavigationFlowAction.Traversal, flow.Field.Sample(new(new(2, 0, 7), 0)).Action);
        for (int frame = 0; frame < 150 && actors.Any(actor => world.Read<NavigationProgress>(actor).Status != NavigationStatus.Arrived); frame++)
        {
            NavigationFlowStep step = navigation.Schedule(0.1f);
            navigation.ScheduleProgress(new ConsumeActionOrSteering(0.1f).ScheduleParallel(world, step.Steering)).Complete();
            foreach (Entity actor in actors)
            {
                TestMotion motion = world.Read<TestMotion>(actor);
                if (motion.Position.Z >= 4 && motion.Position.Z <= 8)
                    Assert.InRange(MathF.Abs(NavigationHeading.Difference(0, motion.Heading)), 0, 0.01f);
            }
            Assert.InRange(links.GetPassage(71).InUse.Length, 0, 1);
        }
        Assert.All(actors, actor =>
        {
            Assert.Equal(1, world.Read<TestActionMotion>(actor).Count);
            Assert.Equal(9, world.Read<TestActionMotion>(actor).Action);
            Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(actor).Status);
        });
    }

    // These completed polygons, heading channels and graph positions are a fixture example of the public representation.
    internal static HeadingNavigationFlowRegion[] Regions()
    {
        var entryGeometry = new NavigationMesh(
            [new(0, 0, 0), new(4, 0, 0), new(4, 0, 4), new(0, 0, 4),
             new(1.5f, 0, 4), new(2.5f, 0, 4), new(2.5f, 0, 8), new(1.5f, 0, 8)],
            [0, 1, 2, 3, 4, 5, 6, 7], [new(0, 4, 0, 1), new(4, 4, 1, 0, Tile: new(0, 1))],
            [new(1, new(1.5f, 0, 4), new(2.5f, 0, 4))]);
        HeadingNavigationInterval north = new(0, -MathF.PI / 4, MathF.PI / 4);
        HeadingNavigationInterval east = new(1, MathF.PI / 4, MathF.PI * 3 / 4);
        var entry = new HeadingNavigationMesh(entryGeometry, 4,
            new HeadingNavigationRegion[] { new(0, 2, 0, 1, 0, 2, 0, 3), new(0, 1, 1, 0, 2, 0, 3, 2) },
            new[] { north, east },
            new HeadingNavigationTranslation[] { new(1, 0, new(1.5f, 0, 4), new(2.5f, 0, 4), 0) },
            new HeadingNavigationRotation[] { new(0, 1, MathF.PI / 2), new(1, 0, -MathF.PI / 2) },
            new(new HeadingNavigationNode[]
                { new(0, 1, new(2, 0, 2), 0, 1), new(0, 0, new(2, 0, 2), 1, 2), new(0, 0, new(2, 0, 4), 3, 2),
                  new(1, 0, new(2, 0, 4), 5, 1), new(1, 0, new(2, 0, 7), 6, 1) },
                new HeadingNavigationEdge[] { new(1, HeadingNavigationAction.Rotation), new(0, HeadingNavigationAction.Rotation),
                    new(2, HeadingNavigationAction.Translation), new(1, HeadingNavigationAction.Translation), new(3, HeadingNavigationAction.Translation, 0),
                    new(4, HeadingNavigationAction.Translation), new(3, HeadingNavigationAction.Translation) }));
        var exitGeometry = new NavigationMesh([new(0, 0, 10), new(4, 0, 10), new(4, 0, 14), new(0, 0, 14)],
            [0, 1, 2, 3], [new(0, 4, 0, 0)], []);
        var exit = new HeadingNavigationMesh(exitGeometry, 4, new HeadingNavigationRegion[] { new(0, 1, 0, 0, 0, 0, 0, 1) },
            new[] { north }, Array.Empty<HeadingNavigationTranslation>(), Array.Empty<HeadingNavigationRotation>(),
            new(new HeadingNavigationNode[] { new(0, 0, new(2, 0, 11), 0, 0) }, Array.Empty<HeadingNavigationEdge>()));
        return [new(10, entry, new HeadingNavigationFlowPort[] { new(1, new(2, 0, 7)) }),
            new(20, exit, new HeadingNavigationFlowPort[] { new(2, new(2, 0, 11)) })];
    }
}
