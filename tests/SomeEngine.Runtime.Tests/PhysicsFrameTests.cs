using System.Collections.Concurrent;
using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.ECS.Systems;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Hierarchy;
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
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(jobs));
        using var frames = new GameFrameSystems(world, includeInterface: false);
        Entity body = CreateFramePhysicsBody(world);
        frames.UpdateViews(0);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var events = new ConcurrentQueue<string>();
        JobHandle writer = jobs.Schedule(new WriteActiveBodyPose(world, body, started, release, events),
            ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<BodyPose>()));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        var local = new LocalTransform { Value = TransformQvvs.Identity };
        local.Value.Position = Vector3.UnitY;
        world.Set(body, local);
        Task update = Task.Run(() =>
        {
            frames.UpdateViews(0.1);
            events.Enqueue("transforms-updated");
        });
        try
        {
            await update.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(Vector3.UnitY, world.ReadComponentData<WorldTransform>(body).Qvvs.Position);
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
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using var game = new GameWorld(new WorldJobs(jobs));
        World world = game.World;
        Entity body = CreateFramePhysicsBody(world);
        Query bodies = world.Query(new QueryDescription().With<BodyPose>());
        Query copy = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<LocalTransform>());
        var simulation = PhysicsSimulation.Create(world, bodies, new PredictBodies(Vector3.Zero, 64), 1);
        var physics = simulation.CreateFrame(new CopyBodyTransforms(copy, new ConcurrentQueue<string>()),
            stepDuration: 1d / 60, maximumDelay: 0, firstStepNumber: 1, startTime: 0);
        game.Physics = physics;
        double[] intervals = [1d / 120, 1d / 120, 1d / 30, 1d / 120, 1d / 120, 1d / 30];
        var numbers = new List<long>();
        var times = new List<double>();
        var positions = new List<float>();

        foreach (double interval in intervals)
            game.Update(interval, new CaptureFrameTimes(physics, body, numbers, times, positions));

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
            Assert.InRange(MathF.Abs(number / 60f - world.ReadComponentData<WorldTransform>(body).Qvvs.Position.X), 0, .00001f);
        }
        
    }

    [Fact]
    public async Task GameWorldCompletesTheRequiredStepAtTheGameplayReadPointAndFlushesItsCommands()
    {
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using var game = new GameWorld(new WorldJobs(jobs));
        World world = game.World;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var readSubmitted = new ManualResetEventSlim();
        Entity body = CreateFramePhysicsBody(world);
        Query bodies = world.Query(new QueryDescription().With<BodyPose>());
        Query copy = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<LocalTransform>());
        var events = new ConcurrentQueue<string>();
        var stages = new WaitForPhysicsInput(started, release, events).Then(new PredictBodies(Vector3.Zero, 64));
        var simulation = PhysicsSimulation.Create(world, bodies, stages, 1);
        var consumer = new MarkScheduledPhysicsRead<CopyBodyTransforms>(new(copy, events), readSubmitted);
        var physics = simulation.CreateFrame(consumer, stepDuration: 1, maximumDelay: 10,
            firstStepNumber: 1, startTime: 0);
        game.Physics = physics;
        game.Update(1, new FrameEvent(events, "first-readers"));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        game.Systems.Add(new ReadPhysicsForGameplay<PhysicsStages<WaitForPhysicsInput, PredictBodies>,
            MarkScheduledPhysicsRead<CopyBodyTransforms>>(physics, events));
        using var commands = new CommandBuffer(world);
        commands.Add(body, new ReadStepMarker());
        game.Systems.Add(new PlaybackReadMarker(commands));
        var positions = new List<float>();
        Task update = Task.Run(() => game.Update(0.1, new ReadConsumedFrame(body, positions, events)));
        try { Assert.True(readSubmitted.Wait(TimeSpan.FromSeconds(10))); }
        finally { release.Set(); }

        await update.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([1f], positions);
        Assert.Equal(1, physics.Result.Number);
        Assert.Equal(1, physics.Result.EndTime);
        Assert.Equal(["first-readers", "submit-1", "physics-started", "physics-input-ready",
            "consume-1", "strong-1", "readers-finished"], events.ToArray());
        
    }

    [Fact]
    public async Task WeakPhysicsConsumesByItsDeclaredDelayAtTheGameReadBoundary()
    {
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using var game = new GameWorld(new WorldJobs(jobs));
        World world = game.World;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var readSubmitted = new ManualResetEventSlim();
        Entity body = CreateFramePhysicsBody(world);
        Query bodies = world.Query(new QueryDescription().With<BodyPose>());
        Query copy = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<LocalTransform>());
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
            Assert.Equal(Vector3.Zero, world.ReadComponentData<WorldTransform>(body).Qvvs.Position);
            events.Enqueue("before-deadline");
            game.Update(0.125, new FrameEvent(events, "deadline-read"));
        });
        try { Assert.True(readSubmitted.Wait(TimeSpan.FromSeconds(10))); }
        finally { release.Set(); }

        await update.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new Vector3(0.5f, 0, 0), world.ReadComponentData<WorldTransform>(body).Qvvs.Position);
        Assert.Equal(2, physics.Result.Number);
        Assert.Equal(0.5, physics.Result.EndTime);
        Assert.Equal(["submit-1", "physics-started", "before-deadline", "submit-2", "physics-input-ready",
            "physics-started", "physics-input-ready", "consume-2", "deadline-read"], events.ToArray());
        
    }

    [Fact]
    public async Task WeakPhysicsKeepsIndependentAnimationMovingAndSubmitsAfterResultReadersFinish()
    {
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(jobs));
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var frames = new GameFrameSystems(world);
        Entity body = CreateFramePhysicsBody(world);
        Entity canvas = world.Create(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(world, canvas, new MovingElement());
        frames.UpdateViews(0);
        Entity container = FirstChild(world, root);
        Entity node = FirstChild(world, container);
        frames.AddInterfaceSystem(new ResizeElement(node));
        var position = Channel<Vector2>.Of<AbsolutePosition>(static (ref AbsolutePosition value) => ref value.Value);
        PropertyAnimation.To(world, node, position, new Vector2(100, 0), Spec.Tween(3));
        Query bodies = world.Query(new QueryDescription().With<BodyPose>());
        Query copy = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<LocalTransform>());
        Query read = world.Query(new QueryDescription().WithStored<BodyPose>());
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
            Assert.Equal(new Rect(50, 0, 30, 40), world.ReadComponentData<LayoutRect>(node).Value);
            Assert.Equal(Vector3.Zero, world.ReadComponentData<WorldTransform>(body).Qvvs.Position);
        }
        finally { release.Set(); }
        physics.SubmittedStep!.Value.Complete();
        var positions = new List<float>();

        frames.UpdateFrame(0.5, new ReadVisibleBodies(read, positions, events));

        Assert.Equal([1f], positions);
        Assert.Equal(Vector3.UnitX, world.ReadComponentData<WorldTransform>(body).Qvvs.Position);
        Assert.Equal(1, physics.Result.Number);
        Assert.Equal(1, physics.Result.EndTime);
        Assert.Equal(["submit-1", "physics-started", "independent-views", "physics-input-ready", "consume-1",
            "query-result", "readers-finished", "submit-2"], events.Take(8).ToArray());
        
        
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

    internal readonly struct CopyBodyTransforms(Query query, ConcurrentQueue<string> events) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
        {
            World world = context.World;
            JobHandle copied = new SystemRunner(world).Schedule(query, new CopyTransforms(), dependency);
            Scheduler jobs = WorldJobs.GetScheduler(world);
            return jobs.Schedule(new RecordConsumed(events, context.Number),
                [JobResourceAccess.Write(jobs.GetResourceToken(events))], copied);
        }
        private readonly struct CopyTransforms : IChunkAction
        {
            public static ReadOnlySpan<ChunkAccess> Components => [ChunkAccess.Read<BodyPose>(), ChunkAccess.Write<LocalTransform>()];
            public void Execute(SystemChunk rows)
            {
                ReadOnlySpan<BodyPose> poses = rows.Read<BodyPose>();
                Span<LocalTransform> transforms = rows.Write<LocalTransform>();
                for (int row = 0; row < rows.Count; row++)
                {
                    transforms[row].Value.Position = poses[row].Position;
                    transforms[row].Value.Rotation = poses[row].Rotation;
                }
            }
        }
        private readonly struct RecordConsumed(ConcurrentQueue<string> events, long number) : IJob
        { public void Execute() => events.Enqueue($"consume-{number}"); }
    }

    private static Entity CreateFramePhysicsBody(World world) => world.Create(new BodyPose { Rotation = Quaternion.Identity },
        new BodyVelocity { Linear = Vector3.UnitX }, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 },
        new BodyStepState(), new LocalTransform { Value = TransformQvvs.Identity }, new WorldTransform { Qvvs = TransformQvvs.Identity });

    private readonly struct CaptureFrameTimes(IPhysicsFrame physics, Entity body, List<long> numbers,
        List<double> times, List<float> positions) : IGameFrameReader
    {
        public void Read(World world)
        {
            numbers.Add(physics.Result.Number); times.Add(physics.Result.EndTime);
            positions.Add(world.ReadComponentData<WorldTransform>(body).Qvvs.Position.X);
        }
    }
    private readonly struct FrameEvent(ConcurrentQueue<string> events, string value) : IGameFrameReader
    { public void Read(World world) => events.Enqueue(value); }
    private readonly struct ReadConsumedFrame(Entity body, List<float> positions, ConcurrentQueue<string> events) : IGameFrameReader
    {
        public void Read(World world)
        {
            positions.Add(world.ReadComponentData<WorldTransform>(body).Qvvs.Position.X);
            Assert.True(world.Has<ReadStepMarker>(body)); events.Enqueue("readers-finished");
        }
    }
    private readonly struct ReadVisibleBodies(Query query, List<float> positions, ConcurrentQueue<string> events) : IGameFrameReader
    {
        public void Read(World world)
        {
            using QueryScope rows = query.OpenRead();
            foreach (QueryChunk chunk in rows)
                foreach (ref readonly BodyPose pose in chunk.Read<BodyPose>()) positions.Add(pose.Position.X);
            events.Enqueue("query-result"); events.Enqueue("readers-finished");
        }
    }
    private readonly struct PlaybackReadMarker(CommandBuffer commands) : ISystem<ImmediateSystemContext>
    { public void OnUpdate(ref ImmediateSystemContext context) => commands.Playback(); }

    private readonly struct ReadStepMarker : IComponent;

    private readonly struct WriteActiveBodyPose(World world, Entity body, ManualResetEventSlim started,
        ManualResetEventSlim release, ConcurrentQueue<string> events) : IJob
    {
        public void Execute()
        {
            events.Enqueue("body-writing");
            started.Set();
            release.Wait();
            world.Set(body, new BodyPose { Position = Vector3.UnitX, Rotation = Quaternion.Identity });
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
