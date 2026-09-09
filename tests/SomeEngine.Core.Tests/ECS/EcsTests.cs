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
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using var game = new GameWorld(new WorldJobs(scheduler));
        Entity entity = game.World.CreateEntity(new LocalTransform
        {
            Value = new TransformQvvs(Vector3.Zero, Quaternion.Identity),
        });
        game.World.Add(entity, new WorldTransform());
        game.Systems.Add(new AdvanceSystem(entity));

        game.Update(0.5);
        Assert.Equal(new Vector3(0.5f, 0, 0), game.World.Read<WorldTransform>(entity).Qvvs.Position);
        Assert.True(game.World.Has<UpdatedTag>(entity));
        game.Update(0.25);
        Assert.Equal(new Vector3(0.75f, 0, 0), game.World.Read<WorldTransform>(entity).Qvvs.Position);
    }

    private readonly struct AdvanceSystem(Entity entity) : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context)
        {
            ComponentJobAccess<LocalTransform>.ScheduleWrite(
                context.World, new AdvanceJob(context.World, entity, (float)context.DeltaTime));
            if (!context.World.Has<UpdatedTag>(entity))
                context.World.Commands().AddTag<UpdatedTag>(entity);
        }
    }

    private readonly struct AdvanceJob(World world, Entity entity, float distance) : IJob
    {
        public void Execute()
        {
            LocalTransform transform = world.Read<LocalTransform>(entity);
            transform.Value.Position.X += distance;
            world.Replace(entity, transform);
        }
    }

    private readonly struct UpdatedTag : SomeEngine.ECS.Components.ITag;

    [Fact]
    public void TestTransformHierarchy()
    {
        var gameWorld = new GameWorld();

        Entity root = gameWorld.World.CreateEntity();
        gameWorld.World.Add(root, new LocalTransform
        {
            Value = new TransformQvvs(new Vector3(10, 0, 0), Quaternion.Identity),
        });
        gameWorld.World.Add(root, new WorldTransform());

        Entity child = gameWorld.World.CreateEntity();
        gameWorld.World.Add(child, new LocalTransform
        {
            Value = new TransformQvvs(new Vector3(0, 5, 0), Quaternion.Identity),
        });
        gameWorld.World.Add(child, new WorldTransform());

        Hierarchy.SetParent(gameWorld.World, child, root);
        gameWorld.Update(0);

        var rootWorld = gameWorld.World.Read<WorldTransform>(root);
        Assert.Equal(new Vector3(10, 0, 0), rootWorld.Qvvs.Position);

        var childWorld = gameWorld.World.Read<WorldTransform>(child);
        Assert.Equal(new Vector3(10, 5, 0), childWorld.Qvvs.Position);
    }

    [Fact]
    public void TestRotationHierarchy()
    {
        var gameWorld = new GameWorld();

        var rotation90Y = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2.0f);
        Entity root = gameWorld.World.CreateEntity();
        gameWorld.World.Add(root, new LocalTransform
        {
            Value = new TransformQvvs(Vector3.Zero, rotation90Y),
        });
        gameWorld.World.Add(root, new WorldTransform());

        Entity child = gameWorld.World.CreateEntity();
        gameWorld.World.Add(child, new LocalTransform
        {
            Value = new TransformQvvs(new Vector3(1, 0, 0), Quaternion.Identity),
        });
        gameWorld.World.Add(child, new WorldTransform());

        Hierarchy.SetParent(gameWorld.World, child, root);
        gameWorld.Update(0);

        var childWorld = gameWorld.World.Read<WorldTransform>(child);

        Assert.InRange(childWorld.Qvvs.Position.X, 0 - 1e-5, 0 + 1e-5);
        Assert.InRange(childWorld.Qvvs.Position.Y, 0 - 1e-5, 0 + 1e-5);
        Assert.InRange(childWorld.Qvvs.Position.Z, -1 - 1e-5, -1 + 1e-5);
    }
}
