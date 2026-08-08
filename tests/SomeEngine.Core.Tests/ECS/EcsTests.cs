using System.Numerics;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Core.Tests.ECS;

public class EcsTests
{
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
