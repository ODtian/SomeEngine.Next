using System.Numerics;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationMppiResourceTests
{
    [Fact]
    public void CandidateAndSelectedTrajectoryReadSpaceBeforeAnIndependentWriterChangesIt()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var space = new MutableSpace(scheduler);
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)], 1);
        var actor = world.CreateEntity(new TestMotion());
        world.Add(actor, new NavigationAgent(profile, new NavigationPolicy(), 1));
        world.Add(actor, new NavigationGoal(new Vector3(0, 0, 5)));
        world.Add(actor, default(NavigationPathResult));
        world.Add(actor, new NavigationGuidance(Vector3.UnitZ, 0, 1, true));
        world.Add(actor, default(NavigationSteering));
        NavigationComponents.AddMppi<MutableSpace, MutableSpaceModel>(world, actor, space,
            new() { CandidateCount = 1, HorizonSteps = 1, NoiseStandardDeviation = Vector2.Zero }, new(0.1f));
        using var neighbors = new NavigationNeighborSystem<TestMotion, TestMotionReader>(world, scheduler);
        var mppi = new NavigationAvoidanceSystem<MutableSpace, MutableSpaceModel, TestMotion, TestMotionReader>(world, neighbors);

        mppi.Schedule(0, default, neighbors.Schedule()).Complete();
        scheduler.CombineDependencies(space.Writes.ToArray()).Complete();

        Assert.Equal(NavigationMppiStatus.Ready, world.Read<NavigationMppiResult>(actor).Status);
        Assert.NotEmpty(space.Writes);
        for (int read = 0; read < space.Writes.Count; read++)
            Assert.True(space.Events.IndexOf($"read {read}") < space.Events.IndexOf($"write {read}"));
    }

    // A mutable travel multiplier is an example resource supplied by a game-defined space.
    private sealed class MutableSpace(Scheduler scheduler) : INavigationSpace<MutableSpace>
    {
        public Scheduler Scheduler { get; } = scheduler;
        public float[] Values { get; } = [1];
        public List<JobHandle> Writes { get; } = [];
        public List<string> Events { get; } = [];
        public static void CollectResources(MutableSpace space, ref JobResourceAccessBuilder resources) => resources.Read(space.Values.AsMemory());
    }

    private readonly struct MutableSpaceModel : INavigationMppiModel<MutableSpace>
    {
        public static Vector2 ReferenceControl(in NavigationMotion actual, NavigationPose reference, float desiredSpeed, NavigationProfile profile) => new(desiredSpeed, 0);
        public static Vector2 Constrain(Vector2 control, in NavigationMotion actual, NavigationProfile profile, float timeStep) => control;
        public static NavigationMotion Integrate(in NavigationMotion actual, Vector2 control, NavigationProfile profile, float time) =>
            actual with { Position = actual.Position + Vector3.UnitZ * control.X * time, Velocity = Vector3.UnitZ * control.X };
        public static bool TryStep(MutableSpace space, in NavigationMotion actual, Vector2 control,
            NavigationProfile profile, NavigationPolicy policy, float time, out NavigationMotion next, out float cost)
        {
            int read = space.Writes.Count;
            // A separate submitting thread gives this writer its own scope. With no workers,
            // the low-priority checkpoint dispatches any ready high-priority writer first.
            JobHandle writer = Task.Run(() => space.Scheduler.Schedule(new ChangeSpace(space, read),
                JobResourceAccess.Write(space.Scheduler.GetResourceToken(space.Values)), new JobScheduleOptions(JobPriority.High))).GetAwaiter().GetResult();
            space.Writes.Add(writer);
            space.Scheduler.Schedule(new EvaluationCheckpoint(space, read), new JobScheduleOptions(JobPriority.Low)).Complete();
            cost = space.Values[0];
            space.Events.Add($"read {read}");
            next = Integrate(in actual, control, profile, time);
            return true;
        }
        public static NavigationSteering Steering(Vector2 control, in NavigationMotion actual, in NavigationMotion next,
            NavigationProfile profile, in NavigationGuidance guidance, float timeStep) => new(next.Velocity, next.Heading, guidance.RequestVersion, true);
    }

    private readonly struct ChangeSpace(MutableSpace space, int read) : IJob
    {
        public void Execute() { space.Values[0]++; space.Events.Add($"write {read}"); }
    }
    private readonly struct EvaluationCheckpoint(MutableSpace space, int read) : IJob
    {
        public void Execute() => space.Events.Add($"checkpoint {read}");
    }
}
