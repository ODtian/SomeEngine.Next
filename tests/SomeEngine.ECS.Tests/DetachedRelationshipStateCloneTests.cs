using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class DetachedRelationshipStateCloneTests
{
    [Fact]
    public void FailedRelationBatchPreservesEndpointsAndAllowsRetry()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity target = world.CreateEntity();
        Entity replacement = world.CreateEntity();
        RelationEdge<Link> edge = world.CreateRelation(source, target, new Link(7));
        using (var commands = new CommandBuffer(world))
        {
            var relations = commands.Relations<Link>();
            relations.Retarget(edge, source, replacement, RelationMaintenanceTiming.Immediate);
            relations.Create(source, source, new Link(8), RelationMaintenanceTiming.Immediate);
            Assert.Throws<InvalidOperationException>(commands.Playback);
        }
        Assert.Equal(target, world.GetDirectedRelationEndpoints(edge).Target);
        Assert.Equal(7, world.Read<Link>(edge.Entity).Value);
        Assert.Empty(world.GetIncomingRelations<Link>(replacement).Entries.ToArray());
        using (var commands = new CommandBuffer(world))
        {
            commands.Relations<Link>().Retarget(edge, source, replacement, RelationMaintenanceTiming.Immediate);
            commands.Playback();
        }
        Assert.Equal(replacement, world.GetDirectedRelationEndpoints(edge).Target);
        Assert.Empty(world.GetIncomingRelations<Link>(target).Entries.ToArray());
    }
    [RelationSchema(RelationDirection.Directed, RelationCardinality.UniquePair, AllowSelfEdge = false)]
    private readonly record struct Link(int Value) : IComponent;
}
