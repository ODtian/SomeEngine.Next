using System.Numerics;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Core.Tests.ECS;

public sealed class GameWorldPhysicsTests
{
    [Fact]
    public void TheReaderSeesConsumedTransformsBeforeFurtherPhysicsIsSubmitted()
    {
        using var game = new GameWorld();
        Entity entity = game.World.Create(new LocalTransform { Value = TransformQvvs.Identity },
            new WorldTransform { Qvvs = TransformQvvs.Identity });
        var result = new FrameData();
        game.Physics = new MovingFrame(game.World, entity, result);

        game.Update(.25, new ReadTransform(entity, result));

        Assert.Equal(new Vector3(2, 0, 0), result.Position);
        Assert.Equal(.25, result.Elapsed);
        Assert.True(result.SubmittedAfterRead);
        game.Dispose();
        Assert.True(result.Disposed);
    }

    private sealed class FrameData
    {
        internal double Elapsed;
        internal Vector3 Position;
        internal bool Read, SubmittedAfterRead, Disposed;
    }
    private sealed class MovingFrame(World world, Entity entity, FrameData result) : IGameFramePhysics
    {
        public JobHandle Completion => default;
        public void AdvanceTime(double deltaTime) => result.Elapsed += deltaTime;
        public JobHandle ConsumeCompleted()
        {
            world.Set(entity, new LocalTransform { Value = new(new Vector3(2, 0, 0), Quaternion.Identity) });
            return default;
        }
        public void SubmitDueSteps() => result.SubmittedAfterRead = result.Read;
        public void Dispose() => result.Disposed = true;
    }
    private readonly struct ReadTransform(Entity entity, FrameData result) : IGameFrameReader
    {
        public void Read(World world)
        {
            result.Position = world.ReadComponentData<WorldTransform>(entity).Qvvs.Position;
            result.Read = true;
        }
    }
}
