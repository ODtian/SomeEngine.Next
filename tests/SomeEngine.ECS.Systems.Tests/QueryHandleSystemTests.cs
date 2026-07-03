using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using Xunit;

namespace SomeEngine.ECS.Systems.Tests;

public class QueryHandleSystemTests
{
    [Fact]
    public void SystemCanCacheAndRunMultipleQueryHandles()
    {
        var world = new World();
        var entity = world.CreateEntity(new SystemPosition { Value = 1 });
        world.Add(entity, new SystemVelocity { Value = 2 });
        world.Add(entity, new SystemDirty { Value = 1 });

        var system = new MultiQuerySystem();
        using var group = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        group.Add(system);

        group.Update();
        Assert.Equal(3, world.Read<SystemPosition>(entity).Value);
        Assert.Equal(1, system.LastDirtyCount);

        group.Update();
        Assert.Equal(5, world.Read<SystemPosition>(entity).Value);
        Assert.Equal(0, system.LastDirtyCount);
    }

    private sealed class MultiQuerySystem : ISystem<ImmediateSystemContext>
    {
        private QueryHandle _movement;
        private QueryHandle _dirty;

        public int LastDirtyCount { get; private set; }

        public void OnCreate(ref ImmediateSystemContext context)
        {
            _movement = context.World.Query(
                context.World.QueryDefinition()
                    .ReadWrite<SystemPosition>()
                    .Read<SystemVelocity>());

            _dirty = context.World.Query(
                context.World.QueryDefinition()
                    .Read<SystemDirty>()
                    .Changed<SystemDirty>());
        }

        public void OnUpdate(ref ImmediateSystemContext context)
        {
            foreach (var chunk in context.World
                         .RunQuery(_movement, context.LastSystemVersion, context.CurrentSystemVersion)
                         .Chunks)
            {
                var positions = chunk.ReadWrite<SystemPosition>();
                var velocities = chunk.Read<SystemVelocity>();
                for (int i = 0; i < chunk.Count; i++)
                    positions[i].Value += velocities[i].Value;
            }

            int dirtyCount = 0;
            foreach (var _ in context.World
                         .RunQuery(_dirty, context.LastSystemVersion, context.CurrentSystemVersion)
                         .Rows)
            {
                dirtyCount++;
            }

            LastDirtyCount = dirtyCount;
        }
    }
}

public struct SystemPosition : SomeEngine.ECS.Components.IComponent
{
    public int Value;
}

public struct SystemVelocity : SomeEngine.ECS.Components.IComponent
{
    public int Value;
}

public struct SystemDirty : SomeEngine.ECS.Components.IComponent
{
    public int Value;
}
