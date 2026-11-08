using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class PhysicsStageCompositionTests
{
    [Fact]
    public void IndependentFramesConsumeAccordingToTheirDeclaredDelay()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var immediateConsumed = new ManualResetEventSlim();
        using var batchConsumed = new ManualResetEventSlim();
        var external = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Entity interactive = CreateBody(world, Vector3.UnitX);
        Entity batch = CreateBody(world, Vector3.UnitY);
        world.AddTag<InteractivePhysicsBody>(interactive);
        world.AddTag<BatchPhysicsBody>(batch);
        var interactiveBodies = new QueryDefinitionBuilder().All<InteractivePhysicsBody>().Build();
        var batchBodies = new QueryDefinitionBuilder().All<BatchPhysicsBody>().Build();
        var immediate = PhysicsSimulation.Create(world, interactiveBodies,
            new PredictBodies(Vector3.Zero, 64).Then(new UpdateBodyVelocities(64)), 1)
            .CreateFrame(new Signal(immediateConsumed), .5, 0, 0, 0);
        var deferred = PhysicsSimulation.Create(world, batchBodies,
            new ExternalWork(external.Task).Then(new PredictBodies(Vector3.Zero, 64)).Then(new UpdateBodyVelocities(64)), 1)
            .CreateFrame(new Signal(batchConsumed), .5, 10, 0, 0);
        using var frames = new PhysicsFrames(scheduler, deferred, immediate);
        try
        {
            frames.AdvanceTime(.5);
            frames.SubmitDueSteps();
            frames.ConsumeCompleted().Complete();
            Assert.True(immediateConsumed.IsSet);
            Assert.Equal(new Vector3(.5f, 0, 0), world.Read<BodyPose>(interactive).Position);
            Assert.Equal(.5, immediate.Result.EndTime);
            Assert.Equal(0, deferred.Result.EndTime);
        }
        finally
        {
            external.SetResult();
            frames.Completion.Complete();
        }
        deferred.CompleteForRead();
        Assert.True(batchConsumed.IsSet);
        Assert.Equal(new Vector3(0, .5f, 0), world.Read<BodyPose>(batch).Position);
        Assert.Equal(.5, deferred.Result.EndTime);
    }

    [Fact]
    public void IndependentWorkSetAdvancesWhileExternalWorkIsPending()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var completed = new ManualResetEventSlim();
        var external = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Entity interactive = CreateBody(world, Vector3.UnitX);
        Entity batch = CreateBody(world, Vector3.UnitY);
        world.AddTag<InteractivePhysicsBody>(interactive);
        world.AddTag<BatchPhysicsBody>(batch);
        var interactiveBodies = new QueryDefinitionBuilder().All<InteractivePhysicsBody>().Build();
        var batchBodies = new QueryDefinitionBuilder().All<BatchPhysicsBody>().Build();
        var interactiveWork = PhysicsSimulation.Create(world, interactiveBodies,
            new PredictBodies(Vector3.Zero, 64).Then(new UpdateBodyVelocities(64)).Then(new Signal(completed)), 1);
        var batchWork = PhysicsSimulation.Create(world, batchBodies,
            new ExternalWork(external.Task).Then(new PredictBodies(Vector3.Zero, 64)).Then(new UpdateBodyVelocities(64)), 2);
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, batchWork.Parallel(interactiveWork), 1);
        PhysicsStepResult result = physics.Schedule(7, 3, .5);
        try
        {
            Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(new Vector3(.5f, 0, 0), world.Read<BodyPose>(interactive).Position);
            Assert.Equal(3.5, world.Read<BodyStepState>(interactive).Time);
        }
        finally
        {
            external.SetResult();
            result.Complete();
        }
        Assert.Equal(new Vector3(0, .5f, 0), world.Read<BodyPose>(batch).Position);
        Assert.Equal(3.5, world.Read<BodyStepState>(batch).Time);
        Assert.Equal(7, result.Number);
    }

    private static Entity CreateBody(World world, Vector3 velocity)
    {
        Entity body = world.CreateEntity();
        world.Add(body, new BodyPose { Rotation = Quaternion.Identity });
        world.Add(body, new BodyVelocity { Linear = velocity });
        world.Add(body, new BodyMass { InverseMass = 1, InverseInertia = Matrix4x4.Identity, Motion = BodyMotion.Dynamic });
        world.Add(body, new BodyStepState());
        return body;
    }

    private readonly struct ExternalWork(Task completion) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
        {
            Scheduler scheduler = WorldJobs.GetScheduler(context.World);
            return scheduler.Schedule(new Wait(scheduler, completion), dependency);
        }
        private readonly struct Wait(Scheduler scheduler, Task completion) : IJob
        {
            public void Execute() => scheduler.CreateExternalFenceHandle(completion);
        }
    }

    private readonly struct Signal(ManualResetEventSlim completed) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
            WorldJobs.GetScheduler(context.World).Schedule(new Set(completed), dependency);
        private readonly struct Set(ManualResetEventSlim completed) : IJob
        {
            public void Execute() => completed.Set();
        }
    }
}

public struct InteractivePhysicsBody : ITag;
public struct BatchPhysicsBody : ITag;
