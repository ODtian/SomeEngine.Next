using System.Numerics;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Core.Tests;

public sealed class QvvsTransformConsumerTests
{
    [Fact]
    public void ParentChildHierarchyProducesExpectedWorldTransform()
    {
        using var world = new GameWorld();
        var root = world.World.CreateEntity();
        var child = world.World.CreateEntity();

        world.World.Add(root, new LocalTransform { Value = new TransformQvvs(new Vector3(10, 0, 0), Quaternion.Identity) });
        world.World.Add(root, new WorldTransform());
        world.World.Add(child, new LocalTransform { Value = new TransformQvvs(new Vector3(2, 0, 0), Quaternion.Identity) });
        world.World.Add(child, new WorldTransform());
        Hierarchy.SetParent(world.World, child, root);

        world.Update(0);

        Assert.Equal(new Vector3(12, 0, 0), world.World.Read<WorldTransform>(child).Qvvs.Position);
    }
}
