using System.Collections.Concurrent;
using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.ECS.Systems;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Elements;
using SomeEngine.Job;
using SomeEngine.Layout;
using SomeEngine.Physics;
using SomeEngine.Views;

namespace SomeEngine.Runtime.Tests;

public sealed partial class GameFrameTests
{
    [Fact]
    public async Task ActivePhysicsWritesAllowIndependentLocalTransformsToReachTheWorld()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var frames = new GameFrameSystems(world, includeInterface: false);
        Entity body = CreateFramePhysicsBody(world);
        frames.UpdateViews(0);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var events = new ConcurrentQueue<string>();
        JobHandle writer = ComponentJobAccess<BodyPose>.ScheduleWrite(world,
            new WriteActiveBodyPose(world, body, started, release, events));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        var local = new LocalTransform { Value = TransformQvvs.Identity };
        local.Value.Position = Vector3.UnitY;
        world.Replace(body, local);
        Task update = Task.Run(() =>
        {
            frames.UpdateViews(0.1);
            events.Enqueue("transforms-updated");
        });
        try
        {
            await update.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(Vector3.UnitY, world.Read<WorldTransform>(body).Qvvs.Position);
        }
        finally
        {
            release.Set();
            writer.Complete();
            await update;
        }
        Assert.Equal(["body-writing", "transforms-updated", "body-written"], events.ToArray());
    }

    [Fact]
    public void FixedPhysicsStepsKeepTheirTimesAcrossFasterAndSlowerPresentationFrames()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var game = new GameWorld(new WorldJobs(jobs));
        World world = game.World;
        Entity body = CreateFramePhysicsBody(world);
        QueryDefinition bodies = world.QueryDefinition().All<BodyPose>().Build();
        QueryHandle copy = world.Query(world.QueryDefinition().Read<BodyPose>().ReadWrite<LocalTransform>());
        var simulation = PhysicsSimulation.Create(world, bodies, new PredictBodies(Vector3.Zero, 64), 1);
        var physics = simulation.CreateFrame(new CopyBodyTransforms(copy, new ConcurrentQueue<string>()),
            stepDuration: 1d / 60, maximumDelay: 0, firstStepNumber: 1, startTime: 0);
        game.Physics = physics;
        double[] intervals = [1d / 120, 1d / 120, 1d / 30, 1d / 120, 1d / 120, 1d / 30];
        var numbers = new List<long>();
        var times = new List<double>();
        var positions = new List<float>();

        foreach (double interval in intervals)
            game.Update(interval, () =>
            {
                numbers.Add(physics.Result.Number);
                times.Add(physics.Result.EndTime);
                positions.Add(world.Read<WorldTransform>(body).Qvvs.Position.X);
            });

        Assert.Equal([0L, 1, 3, 3, 4, 6], numbers);
        double[] expectedTimes = [0, 1d / 60, 3d / 60, 3d / 60, 4d / 60, 6d / 60];
        for (int index = 0; index < expectedTimes.Length; index++)
        {
            Assert.Equal(expectedTimes[index], times[index], precision: 10);
            Assert.Equal((float)expectedTimes[index], positions[index], precision: 6);
        }
        for (int number = 7; number <= 180; number++)
        {
            game.Update(1d / 60);
            Assert.Equal(number, physics.Result.Number);
            Assert.Equal(number / 60d, physics.Result.EndTime, precision: 10);
            Assert.InRange(MathF.Abs(number / 60f - world.Read<WorldTransform>(body).Qvvs.Position.X), 0, .00001f);
        }
        world.ReleaseQuery(copy);
    }

    [Fact]
    public async Task GameWorldCompletesTheRequiredStepAtTheGameplayReadPointAndFlushesItsCommands()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var game = new GameWorld(new WorldJobs(jobs));
        World world = game.World;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var readSubmitted = new ManualResetEventSlim();
        Entity body = CreateFramePhysicsBody(world);
        QueryDefinition bodies = world.QueryDefinition().All<BodyPose>().Build();
        QueryHandle copy = world.Query(world.QueryDefinition().Read<BodyPose>().ReadWrite<LocalTransform>());
        var events = new ConcurrentQueue<string>();
        var stages = new WaitForPhysicsInput(started, release, events).Then(new PredictBodies(Vector3.Zero, 64));
        var simulation = PhysicsSimulation.Create(world, bodies, stages, 1);
        var consumer = new MarkScheduledPhysicsRead<CopyBodyTransforms>(new(copy, events), readSubmitted);
        var physics = simulation.CreateFrame(consumer, stepDuration: 1, maximumDelay: 10,
            firstStepNumber: 1, startTime: 0);
        game.Physics = physics;
        game.Update(1, () => events.Enqueue("first-readers"));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        game.Systems.Add(new ReadPhysicsForGameplay<PhysicsStages<WaitForPhysicsInput, PredictBodies>,
            MarkScheduledPhysicsRead<CopyBodyTransforms>>(physics, events));
        world.Commands().Add(body, new ReadStepMarker());
        var positions = new List<float>();
        Task update = Task.Run(() => game.Update(0.1, () =>
        {
            positions.Add(world.Read<WorldTransform>(body).Qvvs.Position.X);
            Assert.True(world.Has<ReadStepMarker>(body));
            events.Enqueue("readers-finished");
        }));
        try { Assert.True(readSubmitted.Wait(TimeSpan.FromSeconds(10))); }
        finally { release.Set(); }

        await update.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([1f], positions);
        Assert.Equal(1, physics.Result.Number);
        Assert.Equal(1, physics.Result.EndTime);
        Assert.Equal(["first-readers", "submit-1", "physics-started", "physics-input-ready",
            "consume-1", "strong-1", "readers-finished"], events.ToArray());
        world.ReleaseQuery(copy);
    }

    [Fact]
    public async Task WeakPhysicsConsumesByItsDeclaredDelayAtTheGameReadBoundary()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var game = new GameWorld(new WorldJobs(jobs));
        World world = game.World;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var readSubmitted = new ManualResetEventSlim();
        Entity body = CreateFramePhysicsBody(world);
        QueryDefinition bodies = world.QueryDefinition().All<BodyPose>().Build();
        QueryHandle copy = world.Query(world.QueryDefinition().Read<BodyPose>().ReadWrite<LocalTransform>());
        var events = new ConcurrentQueue<string>();
        var stages = new WaitForPhysicsInput(started, release, events).Then(new PredictBodies(Vector3.Zero, 64));
        var simulation = PhysicsSimulation.Create(world, bodies, stages, 1);
        var consumer = new MarkScheduledPhysicsRead<CopyBodyTransforms>(new(copy, events), readSubmitted);
        var physics = simulation.CreateFrame(consumer, stepDuration: 0.25, maximumDelay: 0.5,
            firstStepNumber: 1, startTime: 0);
        game.Physics = physics;
        game.Update(0.25);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        Task update = Task.Run(() =>
        {
            game.Update(0.125);
            Assert.Equal(Vector3.Zero, world.Read<WorldTransform>(body).Qvvs.Position);
            events.Enqueue("before-deadline");
            game.Update(0.125, () => events.Enqueue("deadline-read"));
        });
        try { Assert.True(readSubmitted.Wait(TimeSpan.FromSeconds(10))); }
        finally { release.Set(); }

        await update.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new Vector3(0.5f, 0, 0), world.Read<WorldTransform>(body).Qvvs.Position);
        Assert.Equal(2, physics.Result.Number);
        Assert.Equal(0.5, physics.Result.EndTime);
        Assert.Equal(["submit-1", "physics-started", "before-deadline", "submit-2", "physics-input-ready",
            "physics-started", "physics-input-ready", "consume-2", "deadline-read"], events.ToArray());
        world.ReleaseQuery(copy);
    }

    [Fact]
    public async Task WeakPhysicsKeepsIndependentAnimationMovingAndSubmitsAfterResultReadersFinish()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var frames = new GameFrameSystems(world);
        Entity body = CreateFramePhysicsBody(world);
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(world, canvas, new MovingElement());
        frames.UpdateViews(0);
        Entity container = Hierarchy<ElementTree>.GetChildren(world, root)[0];
        Entity node = Hierarchy<ElementTree>.GetChildren(world, container)[0];
        frames.AddInterfaceSystem(new ResizeElement(node));
        var position = Channel<Vector2>.Of<AbsolutePosition>(static (ref AbsolutePosition value) => ref value.Value);
        PropertyAnimation.To(world, node, position, new Vector2(100, 0), Spec.Tween(3));
        QueryDefinition bodies = world.QueryDefinition().All<BodyPose>().Build();
        QueryHandle copy = world.Query(world.QueryDefinition().Read<BodyPose>().ReadWrite<LocalTransform>());
        QueryHandle read = world.Query(world.QueryDefinition().Read<BodyPose>());
        var events = new ConcurrentQueue<string>();
        var stages = new WaitForPhysicsInput(started, release, events).Then(new PredictBodies(Vector3.Zero, 64));
        var simulation = PhysicsSimulation.Create(world, bodies, stages, 1);
        var physics = simulation.CreateFrame(new CopyBodyTransforms(copy, events), stepDuration: 1,
            maximumDelay: 10, firstStepNumber: 1, startTime: 0);
        frames.Physics = physics;
        frames.UpdateFrame(1);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            await Task.Run(() => frames.UpdateFrame(0.5)).WaitAsync(TimeSpan.FromSeconds(10));
            events.Enqueue("independent-views");
            Assert.Equal(new Rect(50, 0, 30, 40), world.Read<LayoutRect>(node).Value);
            Assert.Equal(Vector3.Zero, world.Read<WorldTransform>(body).Qvvs.Position);
        }
        finally { release.Set(); }
        physics.SubmittedStep!.Value.Complete();
        var positions = new List<float>();

        frames.UpdateFrame(0.5, () =>
        {
            world.ExecuteReadQuery(read, query =>
            {
                foreach (var row in query.Rows) positions.Add(row.Read<BodyPose>().Position.X);
                events.Enqueue("query-result");
            });
            events.Enqueue("readers-finished");
        });

        Assert.Equal([1f], positions);
        Assert.Equal(Vector3.UnitX, world.Read<WorldTransform>(body).Qvvs.Position);
        Assert.Equal(1, physics.Result.Number);
        Assert.Equal(1, physics.Result.EndTime);
        Assert.Equal(["submit-1", "physics-started", "independent-views", "physics-input-ready", "consume-1",
            "query-result", "readers-finished", "submit-2"], events.Take(8).ToArray());
        world.ReleaseQuery(read);
        world.ReleaseQuery(copy);
    }

    private readonly struct WaitForPhysicsInput(ManualResetEventSlim started, ManualResetEventSlim release,
        ConcurrentQueue<string> events) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
        {
            events.Enqueue($"submit-{context.Number}");
            WorldJobs.TryGetScheduler(context.World, out Scheduler? jobs);
            return jobs!.Schedule(new ReadInput(started, release, events), dependency);
        }

        private readonly struct ReadInput(ManualResetEventSlim started, ManualResetEventSlim release,
            ConcurrentQueue<string> events) : IJob
        {
            public void Execute()
            {
                events.Enqueue("physics-started");
                started.Set();
                release.Wait();
                events.Enqueue("physics-input-ready");
            }
        }
    }

    internal readonly struct CopyBodyTransforms(QueryHandle query, ConcurrentQueue<string> events) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
        {
            World world = context.World;
            QueryDefinition definition = world.GetQueryDefinition(query);
            var accesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
            QueryJobAccess.CopyAccesses(world, definition, accesses);
            WorldJobs.TryGetScheduler(world, out Scheduler? jobs);
            return jobs!.Schedule(new CopyTransforms(world, query, context.Number, events), accesses, dependency);
        }

        private readonly struct CopyTransforms(World world, QueryHandle query, long number,
            ConcurrentQueue<string> events) : IJob
        {
            public void Execute()
            {
                world.ExecuteQuery(query, cursor =>
                {
                    foreach (QueryChunkView chunk in cursor.Chunks)
                    {
                        ReadOnlySpan<BodyPose> poses = chunk.Read<BodyPose>();
                        Span<LocalTransform> transforms = chunk.ReadWrite<LocalTransform>();
                        foreach (int row in chunk.RowIndices)
                        {
                            transforms[row].Value.Position = poses[row].Position;
                            transforms[row].Value.Rotation = poses[row].Rotation;
                        }
                    }
                });
                events.Enqueue($"consume-{number}");
            }
        }
    }

    private static Entity CreateFramePhysicsBody(World world)
    {
        Entity body = world.CreateEntity(new BodyPose { Rotation = Quaternion.Identity });
        world.Add(body, new BodyVelocity { Linear = Vector3.UnitX });
        world.Add(body, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 });
        world.Add(body, new BodyStepState());
        world.Add(body, new LocalTransform { Value = TransformQvvs.Identity });
        world.Add(body, new WorldTransform { Qvvs = TransformQvvs.Identity });
        return body;
    }

    private readonly struct ReadStepMarker : IComponent;

    private readonly struct WriteActiveBodyPose(World world, Entity body, ManualResetEventSlim started,
        ManualResetEventSlim release, ConcurrentQueue<string> events) : IJob
    {
        public void Execute()
        {
            events.Enqueue("body-writing");
            started.Set();
            release.Wait();
            world.Replace(body, new BodyPose { Position = Vector3.UnitX, Rotation = Quaternion.Identity });
            events.Enqueue("body-written");
        }
    }

    private readonly struct MarkScheduledPhysicsRead<TStage>(TStage stage, ManualResetEventSlim scheduled) : IPhysicsStage
        where TStage : struct, IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
        {
            JobHandle result = stage.Schedule(in context, dependency);
            scheduled.Set();
            return result;
        }
    }

    private readonly struct ReadPhysicsForGameplay<TStage, TConsumer>(PhysicsFrame<TStage, TConsumer> physics,
        ConcurrentQueue<string> events) : ISystem<ImmediateSystemContext>
        where TStage : struct, IPhysicsStage
        where TConsumer : struct, IPhysicsStage
    {
        public void OnUpdate(ref ImmediateSystemContext context) =>
            events.Enqueue($"strong-{physics.CompleteForRead().Number}");
    }
}
