using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Comparison;

internal static class ReadWorldChecks
{
    private sealed class Counter { internal int Patches, Disposals; }
    private readonly record struct Marker(int Value) : IAddedComponent;
    private readonly record struct OptionalValue(int Value) : IComponent;
    private readonly record struct EnabledValue(int Value) : IEnableableComponent;
    private readonly record struct ReferenceValue(Entity Link, int Value) : IRemovedComponent, IEntityReferenceValue<ReferenceValue>
    {
        static void IEntityReferenceValue<ReferenceValue>.Remap(ref ReferenceValue value, in EntityMap entities)
            => throw new InvalidOperationException("The explicitly bound patcher replaces generated reference handling.");
    }
    private readonly record struct AutomaticReference(Entity Link) : IRemovedComponent, IEntityReferenceValue<AutomaticReference>
    {
        static void IEntityReferenceValue<AutomaticReference>.Remap(ref AutomaticReference value, in EntityMap entities)
            => value = new(entities.Map(value.Link));
    }
    private readonly record struct SparseReference(Entity Link);
    private readonly record struct PreservedIndex(int Key) : IIndexedComponent<int>
    { public int GetKey() => Key; }
    private readonly record struct GroupedReference(Entity GroupEntity, int Kind) : ISharedComponent, IGroupComponent
    { public int GetGroup() => GroupEntity.Id; }
    private sealed class ResourceReference(Entity link, Counter counter) : IDisposable
    {
        internal Entity Link = link;
        public void Dispose() => counter.Disposals++;
    }
    private struct Tree : IHierarchyDomain;
    private struct Link : ITag;

    internal static void Run()
    {
        CompleteMapPatchesEveryStorageOnce();
        GeneratedRemovedReferencesUseTheCompleteMap();
        DefaultPreserveReturnsCompleteValuesToTheFactory();
        MissingReferencesFollowTheSelectedPolicy();
        HistoricalTopologySurvivesIdentityReuse();
        SharedMergeAndGroupsCompleteBeforeSystemsBind();
        FailedCandidateReleasesItsDecodedResource();
        Console.WriteLine("ReadWorld: complete identity mapping, bound patchers, retained topology, shared/group recovery, system binding and candidate disposal passed.");
    }

    private static void CompleteMapPatchesEveryStorageOnce()
    {
        using var source = new World();
        Entity hole = source.Create();
        Entity first = source.Create(new ReferenceValue(default, 10), new AutomaticReference(default));
        Entity second = source.Create(new Marker(20));
        Entity removed = source.Create(new ReferenceValue(second, 30));
        source.Set(first, new ReferenceValue(second, 10));
        source.Set(first, new AutomaticReference(second));
        source.Remove<ReferenceValue>(removed);
        source.AddBuffer<ReferenceValue>(first);
        using (var elements = source.GetBuffer<ReferenceValue>(first))
        { elements.Add(new(second, 40)); elements.Add(new(first, 50)); }
        source.Set(first, new SparseReference(second));
        source.SetResource(new ResourceReference(second, new()));
        source.Destroy(hole);
        var patches = new Counter();
        var registry = new SerializationRegistry()
            .RegisterCodec<ReferenceValue, ReferenceCodec, ReferencePatcher>(Key(1), new(), new(patches))
            .RegisterCodec<AutomaticReference, AutomaticCodec>(Key(2), new())
            .Register<Marker>(Key(3))
            .RegisterBufferCodec<ReferenceValue, ReferenceCodec, ReferencePatcher>(Key(4), new(), new(patches))
            .RegisterSparse<SparseReference, SparseCodec, SparsePatcher>(Key(5), new(), new(patches))
            .RegisterResource<ResourceReference, ResourceCodec, ResourcePatcher>(Key(6), new(new()), new(patches));
        using var bytes = new MemoryStream();
        new WorldSerializer(registry).Save(source, bytes, progress: CheckpointProgress.Preserve);
        bytes.Position = 0;
        using World target = WorldSerializer.ReadWorld(bytes, registry,
            new(EntityIdentityMode.Remap, RequiredPurpose: SavePurpose.RawCheckpoint));
        Entity targetFirst = new(1, first.Generation), targetSecond = new(2, second.Generation), targetRemoved = new(3, removed.Generation);
        Require(target.ReadValue<ReferenceValue>(targetFirst) == new ReferenceValue(targetSecond, 10)
            && target.ReadValue<AutomaticReference>(targetFirst).Link == targetSecond,
            "ordinary codecs retain source identities until the complete map patches forward references");
        using var buffer = target.ReadBuffer<ReferenceValue>(targetFirst);
        Require(buffer.Count == 2 && buffer[0] == new ReferenceValue(targetSecond, 40)
            && buffer[1] == new ReferenceValue(targetFirst, 50), "the bound element patcher maps each buffer value once");
        Require(target.Read<Removed<ReferenceValue>>(targetRemoved).Value.Link == targetSecond
            && target.Read<SparseReference>(targetFirst).Link == targetSecond
            && target.ReadResource<ResourceReference>().Link == targetSecond && patches.Patches == 6,
            "removed, sparse and class resource values share their registered patcher instances");
    }

    private static void MissingReferencesFollowTheSelectedPolicy()
    {
        using var source = new World();
        Entity expired = source.Create();
        Entity holder = source.Create(new AutomaticReference(expired));
        source.Destroy(expired);
        var registry = new SerializationRegistry().RegisterCodec<AutomaticReference, AutomaticCodec>(Key(1), new());
        using var bytes = new MemoryStream();
        new WorldSerializer(registry).Save(source, bytes, progress: CheckpointProgress.Preserve);
        bytes.Position = 0;
        using (World target = WorldSerializer.ReadWorld(bytes, registry, new(EntityIdentityMode.Remap, MissingReferenceMode.Clear)))
            Require(target.ReadValue<AutomaticReference>(new(1, holder.Generation)).Link == Entity.Null,
                "Clear maps an expired ordinary reference to the empty identity");
        bytes.Position = 0;
        using (World target = WorldSerializer.ReadWorld(bytes, registry, new(EntityIdentityMode.Remap, MissingReferenceMode.KeepOriginal)))
            Require(target.ReadValue<AutomaticReference>(new(1, holder.Generation)).Link == expired,
                "KeepOriginal retains the source token for an ordinary missing reference");
        bytes.Position = 0;
        bool failed = false;
        try { using World target = WorldSerializer.ReadWorld(bytes, registry, new(EntityIdentityMode.Remap)); }
        catch (InvalidOperationException) { failed = true; }
        Require(failed, "Throw reports the missing ordinary reference");
    }

    private static void GeneratedRemovedReferencesUseTheCompleteMap()
    {
        using var source = new World();
        Entity hole = source.Create();
        Entity holder = source.Create(new AutomaticReference(default));
        Entity referenced = source.Create(new Marker(30));
        source.Set(holder, new AutomaticReference(referenced));
        source.Remove<AutomaticReference>(holder);
        source.Destroy(hole);
        var registry = new SerializationRegistry()
            .RegisterCodec<AutomaticReference, AutomaticCodec>(Key(1), new()).Register<Marker>(Key(2));
        using var bytes = new MemoryStream();
        new WorldSerializer(registry).Save(source, bytes, progress: CheckpointProgress.Preserve);
        bytes.Position = 0;
        using World target = WorldSerializer.ReadWorld(bytes, registry, new(EntityIdentityMode.Remap));
        Entity targetHolder = new(1, holder.Generation), targetReference = new(2, referenced.Generation);
        Require(target.Read<Removed<AutomaticReference>>(targetHolder).Value.Link == targetReference
            && !target.Has<AutomaticReference>(targetHolder),
            "the default generated Removed patcher maps the saved value once and retains its absence");
    }

    private static void DefaultPreserveReturnsCompleteValuesToTheFactory()
    {
        using var source = new World();
        Entity hole = source.Create();
        Entity first = source.Create(new PreservedIndex(7)), second = source.Create(new PreservedIndex(7));
        source.Index<PreservedIndex, int>();
        source.Destroy(hole);
        var registry = new SerializationRegistry().Register<PreservedIndex>(Key(1));
        using var bytes = new MemoryStream();
        new WorldSerializer(registry).Save(source, bytes, progress: CheckpointProgress.Preserve);
        bytes.Position = 0;
        using (World target = WorldSerializer.ReadWorld(bytes, registry))
            Require(target.IsAlive(first) && target.IsAlive(second) && target.ReadValue<PreservedIndex>(first).Key == 7,
                "the default ReadWorld entry preserves saved identities and values");
        bytes.Position = 0;
        var bound = new BindingResult();
        using World withSystems = WorldSerializer.ReadWorld(bytes, registry, default, new PreservedSystemsBinding(first, second, bound));
        Require(bound.Bound, "the Preserve system binding completes before ReadWorld returns");
    }

    private static void HistoricalTopologySurvivesIdentityReuse()
    {
        using var source = new World();
        Guid consumer = new("64cfecb6-b2d2-4734-8dfa-e81ef8298011");
        source.WatchDestroyed(consumer);
        Entity parent = source.Create();
        Entity child = source.Create(new Marker(10));
        Hierarchy<Tree>.SetParentDeferred(source, child, parent);
        Entity edge = source.CreateRelation(parent, child, new Link()).Entity;
        source.DestroyDeferred(parent);
        var registry = new SerializationRegistry().Register<Marker>(Key(1))
            .RegisterHierarchy<Tree>(Key(2)).RegisterRelation<Link, LinkCodec>(Key(3)).RegisterDestroyedReader(consumer);
        using var bytes = new MemoryStream();
        new WorldSerializer(registry).Save(source, bytes, progress: CheckpointProgress.Preserve);
        bytes.Position = 0;
        using World target = WorldSerializer.ReadWorld(bytes, registry, new(EntityIdentityMode.Remap));
        Entity targetChild = new(1, child.Generation), targetEdge = new(2, edge.Generation);
        Entity oldParent = new(3, parent.Generation);
        Require(Hierarchy<Tree>.GetParent(target, targetChild) == oldParent
            && target.ReadValue<RelationEndpoints<Link>>(targetEdge).Source == oldParent && !target.IsAlive(oldParent),
            "controlled values preserve the mapped historical identity before maintenance");
        using (var facts = target.WatchDestroyed(consumer).Open())
        {
            var entries = facts.GetEnumerator();
            Require(facts.Count == 1 && entries.MoveNext() && entries.Current.Entity == oldParent,
                "the named observer keeps the mapped historical destruction");
        }
        Entity reused = target.Create(new Marker(99));
        Require(reused.Id == oldParent.Id && reused.Generation > oldParent.Generation && !target.IsAlive(oldParent),
            "reusing the mapped free slot keeps its old generation inactive");
        Hierarchy<Tree>.Maintain(target);
        target.MaintainRelations<Link>();
        Require(Hierarchy<Tree>.GetParent(target, targetChild) == Entity.Null
            && target.ReadValue<Marker>(reused).Value == 99,
            "restored pending progress completes without changing the reused identity");
    }

    private static void SharedMergeAndGroupsCompleteBeforeSystemsBind()
    {
        using var source = new World();
        Entity firstOwner = source.Create(), secondOwner = source.Create();
        ulong baseline = source.Version;
        Entity first = source.Create(new Marker(10), new EnabledValue(5), new OptionalValue(1));
        Entity second = source.Create(new Marker(20), new EnabledValue(6), new OptionalValue(2));
        source.SetShared(first, new GroupedReference(firstOwner, 7));
        source.SetShared(second, new GroupedReference(secondOwner, 7));
        source.GroupBy<GroupedReference>();
        source.Remove<OptionalValue>(second);
        source.Disable<EnabledValue>(second);
        source.Destroy(firstOwner); source.Destroy(secondOwner);
        var registry = new SerializationRegistry().Register<Marker>(Key(1)).Register<EnabledValue>(Key(2))
            .Register<OptionalValue>(Key(3))
            .RegisterShared<GroupedReference, GroupedCodec, GroupedPatcher>(Key(4), new(), new());
        var savedVersions = new Versions { Last = 7, Current = 11 };
        using var bytes = new MemoryStream();
        new WorldSerializer(registry).Save(source, bytes, SavePurpose.RawCheckpoint, CheckpointProgress.Preserve, savedVersions);
        bytes.Position = 0;
        var restoredVersions = new Versions();
        var bound = new BindingResult();
        using World target = WorldSerializer.ReadWorld(bytes, registry,
            new(EntityIdentityMode.Remap, MissingReferenceMode.Clear), new GroupedSystemsBinding(restoredVersions, bound));
        Require(bound.Bound && restoredVersions.Last == 7 && restoredVersions.Current == 11 && target.Version == source.Version,
            "system progress is restored after the complete candidate is bound");
        Entity targetSecond = default;
        using (var markers = target.Query<Marker>().OpenRead())
            foreach (var chunk in markers)
            {
                var values = chunk.Read<Marker>();
                for (int row = 0; row < chunk.Count; row++) if (values[row].Value == 20) targetSecond = chunk.Entities[row];
            }
        Require(!target.Has<OptionalValue>(targetSecond) && !target.IsEnabled<EnabledValue>(targetSecond)
            && target.ReadValue<EnabledValue>(targetSecond).Value == 6
            && target.Query(new QueryDescription().WithChanged<Marker>(baseline)).Count == 2,
            "restored group movement preserves presence, disabled values and read versions");
    }

    private static void FailedCandidateReleasesItsDecodedResource()
    {
        using var source = new World();
        source.SetResource(new ResourceReference(new Entity(100, 1), new()));
        var lifetime = new Counter();
        var registry = new SerializationRegistry()
            .RegisterResource<ResourceReference, ResourceCodec, ResourcePatcher>(Key(1), new(lifetime), new(new()));
        using var bytes = new MemoryStream();
        new WorldSerializer(registry).Save(source, bytes, progress: CheckpointProgress.Preserve);
        bytes.Position = 0;
        bool failed = false;
        try { using World target = WorldSerializer.ReadWorld(bytes, registry, new(EntityIdentityMode.Remap)); }
        catch (InvalidOperationException) { failed = true; }
        Require(failed && lifetime.Disposals == 1, "candidate failure releases its decoded class resource");
    }

    private readonly struct ReferenceCodec : IComponentCodec<ReferenceValue>
    {
        public void Write(ref DataWriter writer, in ReferenceValue value) { writer.WriteEntity(value.Link); writer.WriteInt32(value.Value); }
        public void Read(ref DataReader reader, out ReferenceValue value) => value = new(reader.ReadEntity(), reader.ReadInt32());
    }
    private readonly struct ReferencePatcher(Counter counter) : IReferencePatcher<ReferenceValue>
    {
        public void Remap(ref ReferenceValue value, IReferenceRemapper remapper)
        { remapper.TryMap(value.Link, out Entity link); value = new(link, value.Value); counter.Patches++; }
    }
    private readonly struct AutomaticCodec : IComponentCodec<AutomaticReference>
    {
        public void Write(ref DataWriter writer, in AutomaticReference value) => writer.WriteEntity(value.Link);
        public void Read(ref DataReader reader, out AutomaticReference value) => value = new(reader.ReadEntity());
    }
    private readonly struct SparseCodec : IComponentCodec<SparseReference>
    {
        public void Write(ref DataWriter writer, in SparseReference value) => writer.WriteEntity(value.Link);
        public void Read(ref DataReader reader, out SparseReference value) => value = new(reader.ReadEntity());
    }
    private readonly struct SparsePatcher(Counter counter) : IReferencePatcher<SparseReference>
    {
        public void Remap(ref SparseReference value, IReferenceRemapper remapper)
        { remapper.TryMap(value.Link, out Entity link); value = new(link); counter.Patches++; }
    }
    private readonly struct ResourceCodec(Counter counter) : IValueCodec<ResourceReference>
    {
        public void Write(ref DataWriter writer, in ResourceReference value) => writer.WriteEntity(value.Link);
        public void Read(ref DataReader reader, out ResourceReference value) => value = new(reader.ReadEntity(), counter);
    }
    private readonly struct ResourcePatcher(Counter counter) : IReferencePatcher<ResourceReference>
    {
        public void Remap(ref ResourceReference value, IReferenceRemapper remapper)
        { remapper.TryMap(value.Link, out Entity link); value.Link = link; counter.Patches++; }
    }
    private readonly struct GroupedCodec : IComponentCodec<GroupedReference>
    {
        public void Write(ref DataWriter writer, in GroupedReference value) { writer.WriteEntity(value.GroupEntity); writer.WriteInt32(value.Kind); }
        public void Read(ref DataReader reader, out GroupedReference value) => value = new(reader.ReadEntity(), reader.ReadInt32());
    }
    private readonly struct GroupedPatcher : IReferencePatcher<GroupedReference>
    {
        public void Remap(ref GroupedReference value, IReferenceRemapper remapper)
        { remapper.TryMap(value.GroupEntity, out Entity groupEntity); value = new(groupEntity, value.Kind); }
    }
    private readonly struct LinkCodec : IComponentCodec<Link>
    {
        public void Write(ref DataWriter writer, in Link value) { }
        public void Read(ref DataReader reader, out Link value) => value = default;
    }
    private sealed class Versions : ISystemVersions
    {
        internal ulong Last, Current;
        public int VersionCount => 1;
        public void CopyVersions(Span<ulong> values) { values[0] = Last; values[1] = Current; }
        public void RestoreVersions(ReadOnlySpan<ulong> values) { Last = values[0]; Current = values[1]; }
    }
    private static SerializationTypeKey Key(int value)
        => new(new Guid(value, 0x56e2, 0x42a5, 0x98, 0x63, 0x16, 0x2a, 0x34, 0x61, 0x90, 0x21), "ReadWorld." + value, 1);
    private static void Require(bool condition, string behavior)
    { if (!condition) throw new InvalidOperationException(behavior); }
    private sealed class BindingResult { internal bool Bound; }
    private readonly struct PreservedSystemsBinding(Entity first, Entity second, BindingResult result) : IWorldSystemBinding
    {
        public ISystemVersions[] Create(World world)
        {
            using var members = world.Index<PreservedIndex, int>().Find(7);
            Require(members.Count == 2 && world.IsAlive(first) && world.IsAlive(second),
                "the system binding observes complete values through its registered index");
            result.Bound = true;
            return [];
        }
    }
    private readonly struct GroupedSystemsBinding(Versions versions, BindingResult result) : IWorldSystemBinding
    {
        public ISystemVersions[] Create(World world)
        {
            SharedId<GroupedReference> id = world.InternShared(new GroupedReference(Entity.Null, 7));
            using var members = world.FindShared(id);
            Require(members.Count == 2, "system binding observes complete shared membership");
            using var rows = world.Query<GroupedReference>().OpenRead();
            int count = 0;
            foreach (var row in rows) { Require(row.Group<GroupedReference>() == 0, "group keys use the patched shared value"); count += row.Count; }
            Require(count == 2, "source tables merge into the complete final group");
            result.Bound = true;
            return [versions];
        }
    }

}
