using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Tests;

public sealed class CommandBufferAtomicityTests
{
    [Fact]
    public void FailedCandidatePlaybackPreservesTheSourceEntitiesAndFreeIdentityOrder()
    {
        using var world = new World(capacity: 4);
        Entity first = world.Create();
        Entity survivor = world.Create();
        Entity third = world.Create();
        world.Destroy(first);
        world.Destroy(third);
        int count = world.EntityCount;
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        using (var commands = new CommandBuffer(candidate))
        {
            DeferredEntity head = commands.Create(new AtomicValue { Value = 31 });
            DeferredEntity tail = commands.Create(new AtomicValue { Value = 32 });
            commands.Add(Entity.Null, new AtomicValue());
            Assert.Throws<ArgumentException>(commands.Playback);
            Assert.True(candidate.IsAlive(head.Resolve()));
            Assert.True(candidate.IsAlive(tail.Resolve()));
        }
        Assert.Equal(count, world.EntityCount);
        Assert.True(world.IsAlive(survivor));
        Assert.False(world.IsAlive(first));
        Assert.False(world.IsAlive(third));
        Entity reusedThird = world.Create();
        Entity reusedFirst = world.Create();
        Entity frontier = world.Create();
        Assert.True(world.IsAlive(reusedThird));
        Assert.NotEqual(third, reusedThird);
        Assert.True(world.IsAlive(reusedFirst));
        Assert.NotEqual(first, reusedFirst);
        Assert.True(world.IsAlive(frontier));
        Assert.NotEqual(reusedFirst, frontier);
        Assert.NotEqual(reusedThird, frontier);
    }

    [Fact]
    public void DeferredHandlesResolveAfterCompletedEntityAndRelationCommands()
    {
        using var world = new World();
        using var commands = new CommandBuffer(world);
        DeferredEntity source = commands.Create();
        DeferredEntity target = commands.Create();
        Span<DeferredEntity> edges = new DeferredEntity[1];
        commands.EditEndpoints<AtomicLink>([EndpointCommandEdit.Create(source, target)], edges);
        DeferredEntity edge = edges[0];
        commands.Set(edge, new AtomicLink { Value = 44 });
        Assert.False(source.TryResolve(out _));
        Assert.False(target.TryResolve(out _));
        Assert.False(edge.TryResolve(out _));
        Assert.Throws<InvalidOperationException>(() => edge.Resolve());

        commands.Playback();

        Assert.True(world.IsAlive(source.Resolve()));
        Assert.True(world.IsAlive(target.Resolve()));
        Assert.True(world.IsAlive(edge.Resolve()));
        RelationEndpoints<AtomicLink> endpoints = world.Read<RelationEndpoints<AtomicLink>>(edge.Resolve());
        Assert.Equal(source.Resolve(), endpoints.Source);
        Assert.Equal(target.Resolve(), endpoints.Target);
        Assert.Equal(44, world.Read<AtomicLink>(edge.Resolve()).Value);
    }

    [Fact]
    public void FailedCommandSuffixRemainsUnresolvedAcrossLaterBatches()
    {
        using var world = new World();
        DeferredEntity suffix;
        using (var failed = new CommandBuffer(world))
        {
            DeferredEntity completed = failed.Create(new AtomicValue { Value = 3 });
            failed.Add(Entity.Null, new AtomicValue());
            suffix = failed.Create();
            Assert.Throws<ArgumentException>(failed.Playback);
            Assert.True(completed.TryResolve(out Entity entity));
            Assert.Equal(3, world.Read<AtomicValue>(entity).Value);
            Assert.False(suffix.TryResolve(out _));
        }
        using (var successful = new CommandBuffer(world))
        {
            DeferredEntity later = successful.Create();
            successful.Playback();
            Assert.True(later.TryResolve(out Entity entity));
            Assert.True(world.IsAlive(entity));
        }
        Assert.False(suffix.TryResolve(out _));
        Assert.Throws<InvalidOperationException>(() => suffix.Resolve());
    }

    [Fact]
    public void TopologyMaintenancePreservesCompletedDeferredEntityAndRelationIdentities()
    {
        using var world = new World();
        Entity target = world.Create();
        using var commands = new CommandBuffer(world);
        DeferredEntity source = commands.Create();
        Span<DeferredEntity> edges = new DeferredEntity[1];
        commands.EditEndpoints<AtomicLink>([EndpointCommandEdit.Create(source, target)], edges, deferred: true);
        DeferredEntity edge = edges[0];
        commands.Set(edge, new AtomicLink { Value = 7 });
        commands.Playback();
        Entity sourceBefore = source.Resolve();
        Entity edgeBefore = edge.Resolve();

        world.Flush();

        Assert.Equal(sourceBefore, source.Resolve());
        Assert.Equal(edgeBefore, edge.Resolve());
        Assert.True(world.IsAlive(sourceBefore));
        Assert.True(world.IsAlive(edgeBefore));
        using RelationRead incoming = Relations<AtomicLink>.Incoming(world, target);
        Assert.Equal([new RelationEntry(sourceBefore, edgeBefore)], incoming.Entries.ToArray());
    }

    private struct AtomicValue : IAddedComponent { public int Value; }
    [RelationSchema(RelationDirection.Directed, RelationCardinality.UniqueTarget, AllowSelfEdge = false)]
    private struct AtomicLink : IAddedComponent { public int Value; }
}
