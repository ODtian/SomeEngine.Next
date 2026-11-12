using System.Numerics;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Core.Tests.ECS;

public class EcsTests
{
    [Fact]
    public void UpdateCompletesSimulationAndCommandsBeforeComputingTransforms()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        using var game = new GameWorld(new WorldJobs(scheduler));
        Entity entity = game.World.Create(new LocalTransform
        {
            Value = new TransformQvvs(Vector3.Zero, Quaternion.Identity),
        });
        game.World.Add(entity, new WorldTransform());
        game.Systems.Add(new AdvanceSystem(entity));

        game.Update(0.5);
        Assert.Equal(new Vector3(0.5f, 0, 0), game.World.ReadComponentData<WorldTransform>(entity).Qvvs.Position);
        Assert.True(game.World.Has<UpdatedTag>(entity));
        game.Update(0.25);
        Assert.Equal(new Vector3(0.75f, 0, 0), game.World.ReadComponentData<WorldTransform>(entity).Qvvs.Position);
    }

    private readonly struct AdvanceSystem(Entity entity) : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context)
        {
            var jobs = (WorldJobs)context.World.Execution!;
            JobHandle advanced = jobs.Scheduler.Schedule(
                new AdvanceJob(context.World, entity, (float)context.DeltaTime),
                jobs.CollectAccess(new SystemAccesses().WriteRandom<LocalTransform>()));
            if (!context.World.Has<UpdatedTag>(entity))
            {
                var commands = new global::SomeEngine.ECS.Commands.CommandBuffer(context.World);
                commands.Add(entity, new UpdatedTag());
                jobs.Scheduler.Schedule(new ApplyCommands(commands), jobs.StructureWrite, dependency: advanced);
            }
        }
    }

    private readonly struct ApplyCommands(global::SomeEngine.ECS.Commands.CommandBuffer commands) : IJob
    {
        public void Execute() { using (commands) commands.Playback(); }
    }

    private readonly struct AdvanceJob(World world, Entity entity, float distance) : IJob
    {
        public void Execute()
        {
            LocalTransform transform = world.ReadComponentData<LocalTransform>(entity);
            transform.Value.Position.X += distance;
            world.Set(entity, transform);
        }
    }

    private readonly struct UpdatedTag : SomeEngine.ECS.ITag;

    [Fact]
    public void TestTransformHierarchy()
    {
        var gameWorld = new GameWorld();

        Entity root = gameWorld.World.Create();
        gameWorld.World.Add(root, new LocalTransform
        {
            Value = new TransformQvvs(new Vector3(10, 0, 0), Quaternion.Identity),
        });
        gameWorld.World.Add(root, new WorldTransform());

        Entity child = gameWorld.World.Create();
        gameWorld.World.Add(child, new LocalTransform
        {
            Value = new TransformQvvs(new Vector3(0, 5, 0), Quaternion.Identity),
        });
        gameWorld.World.Add(child, new WorldTransform());

        Hierarchy.SetParent(gameWorld.World, child, root);
        gameWorld.Update(0);

        var rootWorld = gameWorld.World.ReadComponentData<WorldTransform>(root);
        Assert.Equal(new Vector3(10, 0, 0), rootWorld.Qvvs.Position);

        var childWorld = gameWorld.World.ReadComponentData<WorldTransform>(child);
        Assert.Equal(new Vector3(10, 5, 0), childWorld.Qvvs.Position);
    }

    [Fact]
    public void TestRotationHierarchy()
    {
        var gameWorld = new GameWorld();

        var rotation90Y = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2.0f);
        Entity root = gameWorld.World.Create();
        gameWorld.World.Add(root, new LocalTransform
        {
            Value = new TransformQvvs(Vector3.Zero, rotation90Y),
        });
        gameWorld.World.Add(root, new WorldTransform());

        Entity child = gameWorld.World.Create();
        gameWorld.World.Add(child, new LocalTransform
        {
            Value = new TransformQvvs(new Vector3(1, 0, 0), Quaternion.Identity),
        });
        gameWorld.World.Add(child, new WorldTransform());

        Hierarchy.SetParent(gameWorld.World, child, root);
        gameWorld.Update(0);

        var childWorld = gameWorld.World.ReadComponentData<WorldTransform>(child);

        Assert.InRange(childWorld.Qvvs.Position.X, 0 - 1e-5, 0 + 1e-5);
        Assert.InRange(childWorld.Qvvs.Position.Y, 0 - 1e-5, 0 + 1e-5);
        Assert.InRange(childWorld.Qvvs.Position.Z, -1 - 1e-5, -1 + 1e-5);
    }
}
