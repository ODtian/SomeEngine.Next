using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class BundleAddTests
{
    [Fact]
    public void AddTwoComponents()
    {
        var world = new World();
        var entity = world.Create();

        world.AddBundle(entity, new PhysicsBundle
        {
            Position = new Position { X = 1, Y = 2 },
            Velocity = new Velocity { X = 3, Y = 4 },
        });

        Assert.True(world.Has<Position>(entity));
        Assert.True(world.Has<Velocity>(entity));
        Assert.Equal(1f, world.Read<Position>(entity).X);
        Assert.Equal(3f, world.Read<Velocity>(entity).X);
    }

    [Fact]
    public void AddDuplicateThrows()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1, Y = 2 });
        Assert.Throws<InvalidOperationException>(() => world.AddBundle(entity, new PhysicsBundle
        {
            Position = new Position { X = 3, Y = 4 },
            Velocity = new Velocity { X = 5, Y = 6 },
        }));
        Assert.Equal(1, world.Read<Position>(entity).X);
        Assert.False(world.Has<Velocity>(entity));
    }

    [Fact]
    public void AddThreeComponents()
    {
        var world = new World();
        var entity = world.Create();

        world.AddBundle(entity, new MotionHealthBundle
        {
            Position = new Position { X = 1, Y = 2 },
            Velocity = new Velocity { X = 3, Y = 4 },
            Health = new Health { Value = 9 },
        });

        Assert.Equal(9, world.Read<Health>(entity).Value);
    }

    [Fact]
    public void BatchEnableDefault()
    {
        var world = new World();
        var entity = world.Create();

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
        var entity = world.Create();

        world.Add(entity, new Position { X = 5, Y = 6 });

        Assert.True(world.Has<Position>(entity));
        Assert.Equal(5f, world.Read<Position>(entity).X);
    }
}
