using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class BundleAddTests
{
    [Fact]
    public void AddTwoComponents()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.AddBundle(entity, new PhysicsBundle
        {
            Position = new Position { X = 1, Y = 2 },
            Velocity = new Velocity { X = 3, Y = 4 },
        });

        Assert.True(world.Has<Position>(entity));
        Assert.True(world.Has<Velocity>(entity));
        Assert.Equal(1f, world.Read<Position>(entity).X);
        Assert.Equal(3f, world.Read<Velocity>(entity).X);
        Assert.Equal(2, world.ArchetypeCount);
    }

    [Fact]
    public void AddDuplicateThrows()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 1, Y = 2 });

        Assert.Throws<InvalidOperationException>(() =>
            world.AddBundle(entity, new PhysicsBundle
            {
                Position = new Position { X = 3, Y = 4 },
                Velocity = new Velocity { X = 5, Y = 6 },
            }));
    }

    [Fact]
    public void AddThreeComponents()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.AddBundle(entity, new MotionHealthBundle
        {
            Position = new Position { X = 1, Y = 2 },
            Velocity = new Velocity { X = 3, Y = 4 },
            Health = new Health { Value = 9 },
        });

        Assert.Equal(2, world.ArchetypeCount);
        Assert.Equal(9, world.Read<Health>(entity).Value);
    }

    [Fact]
    public void BatchHooksRun()
    {
        var world = new World();
        var entity = world.CreateEntity();
        var calls = new List<string>();

        world.Hooks<Position>()
            .OnAdd((DeferredWorld worldArg, Entity entityArg, in Position component) => calls.Add($"Position:{component.X}"));
        world.Hooks<Velocity>()
            .OnAdd((DeferredWorld worldArg, Entity entityArg, in Velocity component) => calls.Add($"Velocity:{component.X}"));

        world.AddBundle(entity, new PhysicsBundle
        {
            Position = new Position { X = 10, Y = 20 },
            Velocity = new Velocity { X = 30, Y = 40 },
        });

        Assert.Equal(new[] { "Position:10", "Velocity:30" }, calls);
    }

    [Fact]
    public void BatchEnableDefault()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.AddBundle(entity, new PositionVisibilityBundle
        {
            Position = new Position { X = 1, Y = 2 },
            Visibility = new VisibilityState { Value = 7 },
        });

        Assert.True(world.Has<VisibilityState>(entity));
        Assert.True(world.IsEnabled<VisibilityState>(entity));
    }

    [Fact]
    public void SingleAddWorks()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.Add(entity, new Position { X = 5, Y = 6 });

        Assert.True(world.Has<Position>(entity));
        Assert.Equal(5f, world.Read<Position>(entity).X);
        Assert.Equal(2, world.ArchetypeCount);
    }
}
