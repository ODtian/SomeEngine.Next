using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Systems;
using Xunit;

namespace SomeEngine.ECS.Systems.Tests;

public class QueryHandleSystemTests
{
    [Fact]
    public void SystemCanCacheAndRunMultipleQueryHandles()
    {
        var world = new World();
        var entity = world.Create(new SystemPosition { Value = 1 });
        world.Add(entity, new SystemVelocity { Value = 2 });
        world.Add(entity, new SystemDirty { Value = 1 });

        var system = new MultiQuerySystem();
        using var group = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        group.Add(system);

        group.Update();
        Assert.Equal(3, world.ReadComponentData<SystemPosition>(entity).Value);
        Assert.Equal(1, system.LastDirtyCount);

        group.Update();
        Assert.Equal(5, world.ReadComponentData<SystemPosition>(entity).Value);
        Assert.Equal(0, system.LastDirtyCount);
    }

    private sealed class MultiQuerySystem : ISystem<ImmediateSystemContext>
    {
        private Query _movement;
        private Query _dirty;

        public int LastDirtyCount { get; private set; }

        public void OnCreate(ref ImmediateSystemContext context)
        {
            _movement = context.World.Query(
                new QueryDescription()
                    .WithStored<SystemPosition>()
                    .WithStored<SystemVelocity>());

            _dirty = context.World.Query(
                new QueryDescription()
                    .WithStored<SystemDirty>()
                    .WithChanged<SystemDirty>(0));
        }

        public void OnUpdate(ref ImmediateSystemContext context)
        {
            using (QueryScope movement = _movement.Since(context.LastSystemVersion).Open())
                foreach (QueryChunk chunk in movement)
                {
                    Span<SystemPosition> positions = chunk.Write<SystemPosition>();
                    ReadOnlySpan<SystemVelocity> velocities = chunk.Read<SystemVelocity>();
                    for (int index = 0; index < chunk.Count; index++) positions[index].Value += velocities[index].Value;
                }
            int dirtyCount = _dirty.Since(context.LastSystemVersion).Count;

            LastDirtyCount = dirtyCount;
        }
    }
}

public struct SystemPosition : SomeEngine.ECS.IComponent
{
    public int Value;
}

public struct SystemVelocity : SomeEngine.ECS.IComponent
{
    public int Value;
}

public struct SystemDirty : SomeEngine.ECS.IComponent, ITrackedComponent
{
    public int Value;
}
