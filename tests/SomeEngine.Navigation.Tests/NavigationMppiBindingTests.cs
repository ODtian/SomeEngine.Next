using System.Numerics;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationMppiBindingTests
{
    [Fact]
    public void ReplacingSpaceDataBetweenCandidateAndReductionKeepsTheActualReadsProtected()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var space = new RebindingSpace(scheduler);
        var profile = new NavigationProfile([new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)], 1);
        var actor = world.CreateEntity(new TestMotion());
        world.Add(actor, new NavigationAgent(profile, new NavigationPolicy(), 1));
        world.Add(actor, new NavigationGoal(new Vector3(0, 0, 5)));
        world.Add(actor, default(NavigationPathResult));
        world.Add(actor, new NavigationGuidance(Vector3.UnitZ, 0, 1, true));
        world.Add(actor, default(NavigationSteering));
        NavigationComponents.AddMppi<RebindingSpace, RebindingModel>(world, actor, space,
            new() { CandidateCount = 2, HorizonSteps = 1, NoiseStandardDeviation = Vector2.Zero }, new(0.1f));
        var neighbors = new NavigationNeighborSystem<TestMotion, TestMotionReader>(world, scheduler);
        var mppi = new NavigationAvoidanceSystem<RebindingSpace, RebindingModel, TestMotion, TestMotionReader>(world, neighbors);

        mppi.Schedule(0, default, neighbors.Schedule()).Complete();
        scheduler.CombineDependencies(space.Writes.Append(space.Replacement).ToArray()).Complete();

        Assert.Equal(NavigationMppiStatus.Ready, world.Read<NavigationMppiResult>(actor).Status);
        Assert.Contains(space.Reads, values => ReferenceEquals(values, space.ReplacedValues));
        Assert.True(space.Events.IndexOf("replace") < space.Events.LastIndexOf($"read {space.Reads.Count - 1}"));
        for (int read = 0; read < space.Reads.Count; read++)
            Assert.True(space.Events.IndexOf($"read {read}") < space.Events.IndexOf($"write {read}"));
    }

    // Array replacement is an example of a mutable game space's ordinary binding update.
    private sealed class RebindingSpace(Scheduler scheduler) : INavigationSpace<RebindingSpace>
    {
        private SpaceData _data = new([1], 1);
        public Scheduler Scheduler { get; } = scheduler;
        public float[] Values => Volatile.Read(ref _data).Values;
        public float[] ReplacedValues { get; } = [10];
        public JobHandle Replacement;
        public List<float[]> Reads { get; } = [];
        public List<JobHandle> Writes { get; } = [];
        public List<string> Events { get; } = [];
        public static long GetVersion(RebindingSpace space) => Volatile.Read(ref space._data).Version;
        public static void CollectResources(RebindingSpace space, ref JobResourceAccessBuilder resources)
        {
            resources.Read(space);
            resources.Read(space.Values.AsMemory());
        }
        public void Replace()
        {
            Volatile.Write(ref _data, new(ReplacedValues, _data.Version + 1));
            Events.Add("replace");
        }
        private sealed record SpaceData(float[] Values, long Version);
    }

    private readonly struct RebindingModel : INavigationMppiModel<RebindingSpace>
    {
        public static Vector2 ReferenceControl(in NavigationMotion actual, NavigationPose reference, float desiredSpeed, NavigationProfile profile) => new(desiredSpeed, 0);
        public static Vector2 Constrain(Vector2 control, in NavigationMotion actual, NavigationProfile profile, float timeStep) => control;
        public static NavigationMotion Integrate(in NavigationMotion actual, Vector2 control, NavigationProfile profile, float time) =>
            actual with { Position = actual.Position + Vector3.UnitZ * control.X * time, Velocity = Vector3.UnitZ * control.X };
        public static bool TryStep(RebindingSpace space, in NavigationMotion actual, Vector2 control,
            NavigationProfile profile, NavigationPolicy policy, float time, out NavigationMotion next, out float cost)
        {
            int read = space.Reads.Count;
            float[] values = space.Values;
            space.Reads.Add(values);
            space.Writes.Add(Task.Run(() => space.Scheduler.Schedule(new WriteValue(space, values, read),
                JobResourceAccess.Write(space.Scheduler.GetResourceToken(values)), new JobScheduleOptions(JobPriority.High))).GetAwaiter().GetResult());
            space.Scheduler.Schedule(new ReadCheckpoint(), new JobScheduleOptions(JobPriority.Low)).Complete();
            cost = values[0];
            space.Events.Add($"read {read}");
            if (read == 0)
                space.Replacement = Task.Run(() => space.Scheduler.Schedule(new ReplaceValues(space),
                    JobResourceAccess.Write(space.Scheduler.GetResourceToken(space)), new JobScheduleOptions(JobPriority.High))).GetAwaiter().GetResult();
            next = Integrate(in actual, control, profile, time);
            return true;
        }
        public static NavigationSteering Steering(Vector2 control, in NavigationMotion actual, in NavigationMotion next,
            NavigationProfile profile, in NavigationGuidance guidance, float timeStep) => new(next.Velocity, next.Heading, guidance.RequestVersion, true);
    }

    private readonly struct WriteValue(RebindingSpace space, float[] values, int read) : IJob
    {
        public void Execute() { values[0]++; space.Events.Add($"write {read}"); }
    }
    private readonly struct ReplaceValues(RebindingSpace space) : IJob { public void Execute() => space.Replace(); }
    private readonly struct ReadCheckpoint : IJob { public void Execute() { } }
}
