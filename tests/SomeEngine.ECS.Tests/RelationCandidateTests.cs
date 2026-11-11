using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Tests;

public sealed class RelationCandidateTests
{
    [Fact]
    public void FailedCandidateRelationBatchPreservesEndpointsAndAllowsRetry()
    {
        using var world = new World();
        Entity source = world.Create();
        Entity target = world.Create();
        Entity replacement = world.Create();
        RelationEdge<Link> edge = world.CreateRelation(source, target, new Link(7));
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        using (var commands = new CommandBuffer(candidate))
        {
            commands.EditEndpoints<Link>([new(edge.Entity, source, replacement)]);
            var created = new DeferredEntity[1];
            commands.EditEndpoints<Link>([EndpointCommandEdit.Create(source, source)], created);
            Assert.Throws<ArgumentException>(commands.Playback);
        }
        Assert.Equal(target, ReadEndpoints(world, edge).Target);
        Assert.Equal(7, world.Read<Link>(edge.Entity).Value);
        Assert.Empty(ReadIncoming<Link>(world, replacement));
        using (var commands = new CommandBuffer(world))
        {
            commands.EditEndpoints<Link>([new(edge.Entity, source, replacement)]);
            commands.Playback();
        }
        Assert.Equal(replacement, ReadEndpoints(world, edge).Target);
        Assert.Empty(ReadIncoming<Link>(world, target));
    }

    [Fact]
    public void CapturedEndpointEditsRejectReplacedQueryMembers()
    {
        using var world = new World();
        Entity source = world.Create();
        Entity target = world.Create();
        Entity other = world.Create();
        Entity replacement = world.Create();
        RelationEdge<Link> first = world.CreateRelation(source, target, new Link(1));
        RelationEdge<Link> second = world.CreateRelation(source, other, new Link(2));
        world.Add(first.Entity, new CapturedMarker());
        Query query = world.Query(new QueryDescription().WithStored<RelationEndpoints<Link>>().WithStored<CapturedMarker>());
        ulong membershipVersion = query.MembershipVersion;
        using var commands = new CommandBuffer(world);
        commands.EditEndpoints<Link>(query,
            [new CapturedEndpointEdit(first.Entity, source, target, source, replacement)], [],
            world.DataGeneration, membershipVersion);

        world.Remove<CapturedMarker>(first.Entity);
        world.Add(second.Entity, new CapturedMarker());

        Assert.Equal(1, query.Count);
        Assert.Throws<InvalidOperationException>(commands.Playback);
        Assert.Equal(target, ReadEndpoints(world, first).Target);
        Assert.Empty(ReadIncoming<Link>(world, replacement));
    }

    private readonly struct CapturedMarker : ITag;
    [RelationSchema(RelationDirection.Directed, RelationCardinality.UniquePair, AllowSelfEdge = false)]
    private readonly record struct Link(int Value) : IAddedComponent;
}
