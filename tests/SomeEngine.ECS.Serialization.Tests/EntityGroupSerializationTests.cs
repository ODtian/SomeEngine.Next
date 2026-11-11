using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class EntityGroupSerializationTests
{
    [Fact]
    public void AppendingTheSameGroupTwicePreservesBusinessEntitiesAndCreatesIndependentTopology()
    {
        using var source = new World();
        Entity parent = source.Create(new DescribedPosition { Value = 2 });
        Entity child = source.Create(new DescribedPosition { Value = 7, Target = parent });
        Hierarchy<EntityGroupDomain>.SetParent(source, child, parent);
        RelationEdge<EntityGroupLink> edge = Relations<EntityGroupLink>.CreateEdge(source, parent, child, new() { Value = 13 });
        source.SetShared(child, new DescribedGroup { Value = 4 });
        source.SetSparse(child, new DescribedLabel { Value = "member" });
        source.Set(child, new EntityGroupKey { Value = 27 });
        source.AddBuffer<DescribedElement>(child);
        using (DynamicBuffer<DescribedElement> elements = source.GetBuffer<DescribedElement>(child))
            elements.Add(new() { Value = 9, Target = parent });
        using var output = new MemoryStream();
        WorldSerializer.WriteEntities(output, source, [parent, child, edge.Entity], new(Purpose: SavePurpose.DurableSave));
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using var target = new World(execution: new WorldJobs(scheduler));
        Entity business = target.Create(new DescribedPosition { Value = 91 });
        target.SetShared(business, new DescribedGroup { Value = 8 });
        ValueIndex<EntityGroupKey, int> index = target.Index<EntityGroupKey, int>();

        Entity[] first = WorldSerializer.ReadEntities(output.ToArray().AsMemory(), target);
        output.Position = 0;
        Entity[] second = WorldSerializer.ReadEntities(output, target);

        Assert.Equal(7, target.EntityCount);
        Assert.Equal(91, target.ReadComponentData<DescribedPosition>(business).Value);
        Assert.Equal(8, target.ReadComponentData<DescribedGroup>(business).Value);
        Assert.Equal(first[0], target.ReadComponentData<DescribedPosition>(first[1]).Target);
        Assert.Equal(second[0], target.ReadComponentData<DescribedPosition>(second[1]).Target);
        Assert.NotEqual(first[0], second[0]);
        Assert.Equal(first[0], Hierarchy<EntityGroupDomain>.GetParent(target, first[1]));
        Assert.Equal(second[0], Hierarchy<EntityGroupDomain>.GetParent(target, second[1]));
        using (ChildrenRead children = Hierarchy<EntityGroupDomain>.GetChildren(target, first[0]))
            Assert.Equal([first[1]], children.Entities.ToArray());
        using (RelationRead outgoing = Relations<EntityGroupLink>.Outgoing(target, first[0]))
            Assert.Equal([new RelationEntry(first[1], first[2])], outgoing.Entries.ToArray());
        Assert.Equal(13, target.ReadComponentData<EntityGroupLink>(first[2]).Value);
        Assert.Equal(4, target.ReadComponentData<DescribedGroup>(first[1]).Value);
        Assert.Equal("member", target.ReadComponentData<DescribedLabel>(first[1]).Value);
        using (ReadOnlyBuffer<DescribedElement> elements = target.ReadBuffer<DescribedElement>(first[1]))
            Assert.Equal(new DescribedElement { Value = 9, Target = first[0] }, elements[0]);
        using (IndexMembers found = index.Find(27))
            Assert.Equal(new HashSet<Entity> { first[1], second[1] }, found.Entities.ToArray().ToHashSet());

        target.Destroy(first.AsSpan());

        Assert.True(target.IsAlive(business));
        Assert.Equal(4, target.Query(new QueryDescription().Without<Destroyed>()).Count);
        Assert.Equal(second[0], Hierarchy<EntityGroupDomain>.GetParent(target, second[1]));
        using (RelationRead outgoing = Relations<EntityGroupLink>.Outgoing(target, second[0]))
            Assert.Equal([new RelationEntry(second[1], second[2])], outgoing.Entries.ToArray());
    }

    [Theory]
    [InlineData(MissingReferenceMode.KeepOriginal)]
    [InlineData(MissingReferenceMode.Clear)]
    public void ReferencesOutsideTheGroupFollowTheSelectedReadMode(MissingReferenceMode mode)
    {
        using var source = new World();
        Entity external = source.Create();
        Entity member = source.Create(new DescribedPosition { Value = 8, Target = external });
        using var output = new MemoryStream();
        WorldSerializer.WriteEntities(output, source, [member]);
        using var target = new World();

        Entity[] restored = WorldSerializer.ReadEntities(output.ToArray().AsMemory(), target, new(MissingReferenceMode: mode));

        Assert.Single(restored);
        Assert.Equal(8, target.Read<DescribedPosition>(restored[0]).Value);
        Assert.Equal(mode == MissingReferenceMode.Clear ? Entity.Null : external,
            target.Read<DescribedPosition>(restored[0]).Target);
    }

    [Fact]
    public void AnUnmappedExternalReferenceFailsWithThrowReadMode()
    {
        using var source = new World();
        Entity external = source.Create();
        Entity member = source.Create(new DescribedPosition { Target = external });
        using var output = new MemoryStream();
        WorldSerializer.WriteEntities(output, source, [member]);
        using var target = new World();

        Assert.Throws<InvalidOperationException>(() => WorldSerializer.ReadEntities(output.ToArray().AsMemory(), target,
            new(MissingReferenceMode: MissingReferenceMode.Throw)));
    }
}

public readonly struct EntityGroupDomain : IHierarchyDomain;

[SerializableComponent("5b1ee9ee-d48d-492b-afed-e62969403d93")]
[RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
public struct EntityGroupLink : IComponent
{
    public int Value;
}

[SerializableComponent("c579c031-b89f-4b6c-aa61-c132d1d86c63")]
public struct EntityGroupKey : IIndexedComponent<int>
{
    public int Value;
    public readonly int GetKey() => Value;
}
