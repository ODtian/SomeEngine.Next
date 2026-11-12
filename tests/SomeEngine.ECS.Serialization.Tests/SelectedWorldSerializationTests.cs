using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class SelectedWorldSerializationTests
{
    [Theory]
    [InlineData(SelectionInput.Entity)]
    [InlineData(SelectionInput.Members)]
    [InlineData(SelectionInput.Query)]
    public void SelectedSaveEncodesRegisteredValuesWithoutCopyingOtherComponents(SelectionInput input)
    {
        var calls = new ResourceCalls();
        using var source = new World();
        Entity member = source.Create(new SerPosition { X = 11, Y = 17 }, new UnstoredResource(calls));
        source.Create(new SerVelocity { X = 23 });
        calls.Clones = calls.Releases = 0;
        calls.RejectClone = true;
        var registry = new SerializationRegistry().Register<SerPosition>();
        var serializer = new WorldSerializer(registry);
        using var output = new MemoryStream();

        switch (input)
        {
            case SelectionInput.Entity: serializer.Save(source, member, output); break;
            case SelectionInput.Members: serializer.Save(source, [member], output); break;
            case SelectionInput.Query: serializer.Save(source, source.Query<SerPosition>(), output); break;
        }

        Assert.Equal(0, calls.Clones);
        Assert.Equal(0, calls.Releases);
        output.Position = 0;
        using World restored = WorldSerializer.ReadWorld(output, registry);
        Assert.Equal(1, restored.EntityCount);
        Assert.Equal(11, restored.Read<SerPosition>(member).X);
        Assert.Equal(17, restored.Read<SerPosition>(member).Y);
        Assert.False(restored.Has<UnstoredResource>(member));
    }

    [Fact]
    public void SelectedWorldPayloadRestoresItsMembersStorageAndTopology()
    {
        var registry = new SerializationRegistry().Register<SerPosition>()
            .RegisterShared<SerScene>(SaveTypes.Key<SerScene>()).RegisterSparse<SerSparse>(SaveTypes.Key<SerSparse>())
            .RegisterBuffer<SerElement>(SaveTypes.Key<SerElement>()).Register<EntityGroupKey>()
            .RegisterHierarchy<EntityGroupDomain>(SaveTypes.Key<EntityGroupDomain>())
            .Register<EntityGroupLink>().RegisterRelation<EntityGroupLink>();
        using var source = new World();
        Entity parent = source.Create(new SerPosition { X = 1 });
        Entity child = source.Create(new SerPosition { X = 2 });
        Entity excluded = source.Create(new SerPosition { X = 3 });
        Hierarchy<EntityGroupDomain>.SetParent(source, child, parent);
        RelationEdge<EntityGroupLink> edge = Relations<EntityGroupLink>.CreateEdge(source, parent, child, new() { Value = 13 });
        source.SetShared(excluded, new SerScene { Value = 7 });
        source.SetShared(child, new SerScene { Value = 31 });
        source.SetSparse(excluded, new SerSparse { Value = 41 });
        source.SetSparse(child, new SerSparse { Value = 37 });
        source.Set(child, new EntityGroupKey { Value = 29 });
        source.AddBuffer<SerElement>(child);
        using (DynamicBuffer<SerElement> buffer = source.GetBuffer<SerElement>(child)) buffer.Add(new() { Value = 43 });
        using var output = new MemoryStream();

        new WorldSerializer(registry).Save(source, [edge.Entity, child, parent], output);

        output.Position = 0;
        using World restored = WorldSerializer.ReadWorld(output, registry);
        Assert.Equal(3, restored.EntityCount);
        Assert.False(restored.IsAlive(excluded));
        Assert.Equal(2, restored.Read<SerPosition>(child).X);
        Assert.Equal(31, restored.ReadShared<SerScene>(child).Value);
        Assert.Equal(37, restored.ReadSparse<SerSparse>(child).Value);
        using (ReadOnlyBuffer<SerElement> buffer = restored.ReadBuffer<SerElement>(child)) Assert.Equal(43, buffer[0].Value);
        using (IndexMembers members = restored.Index<EntityGroupKey, int>().Find(29)) Assert.Equal([child], members.Entities.ToArray());
        Assert.Equal(parent, Hierarchy<EntityGroupDomain>.GetParent(restored, child));
        using (ChildrenRead children = Hierarchy<EntityGroupDomain>.GetChildren(restored, parent)) Assert.Equal([child], children.Entities.ToArray());
        using (RelationRead outgoing = Relations<EntityGroupLink>.Outgoing(restored, parent))
            Assert.Equal([new RelationEntry(child, edge.Entity)], outgoing.Entries.ToArray());
        Assert.Equal(13, restored.Read<EntityGroupLink>(edge.Entity).Value);
    }

    public enum SelectionInput { Entity, Members, Query }
    private sealed class ResourceCalls { internal int Clones, Releases; internal bool RejectClone; }
    private readonly struct UnstoredResource(ResourceCalls? calls) : IComponent, IComponentOperations<UnstoredResource>
    {
        private readonly ResourceCalls? _calls = calls;
        public UnstoredResource Clone(World world, in UnstoredResource value)
        {
            value._calls!.Clones++;
            if (value._calls.RejectClone) throw new NotSupportedException();
            return value;
        }
        public void Release(World world, ref UnstoredResource value)
        {
            if (value._calls is { } calls) calls.Releases++;
        }
    }
}
