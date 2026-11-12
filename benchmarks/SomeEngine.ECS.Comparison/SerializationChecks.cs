using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using System.Buffers;

namespace SomeEngine.ECS.Comparison;

internal static class SerializationChecks
{
    private struct ArchiveValue(int value) : IAddedComponent, IRemovedComponent { internal int Value = value; }
    private readonly record struct ArchiveStep(int Value) : IEnableableComponent;
    private readonly record struct ArchiveShared(int Value) : ISharedComponent, IGroupComponent { public int GetGroup() => Value; }
    private readonly record struct ArchiveLink(Entity Entity, long Value) : IAddedComponent, IRemovedComponent, IEntityReferenceValue<ArchiveLink>
    {
        public static void Remap(ref ArchiveLink value, in EntityMap entities) => value = value with { Entity = entities.Map(value.Entity) };
    }
    private readonly struct ArchiveTree : IHierarchyDomain;
    [RelationSchema(RelationDirection.Directed, RelationCardinality.UniquePair)]
    private readonly record struct ArchiveEdge(long Value) : IComponent;
    private sealed class ArchiveResource(int value) : ICloneable, IDisposable
    {
        internal int[] Items = [value];
        internal bool Released;
        public object Clone() => new ArchiveResource(Items[0]);
        public void Dispose() => Released = true;
    }
    private readonly struct AdvanceArchive : IComponentAction<ArchiveValue, ArchiveStep>
    {
        public void Execute(Span<ArchiveValue> values, ReadOnlySpan<ArchiveStep> steps)
        { for (int i = 0; i < values.Length; i++) values[i].Value += steps[i].Value; }
    }

    internal static void CheckMemory() { CheckSnapshot(0); CheckRecaptureEncoding(); }

    private static void CheckRecaptureEncoding()
    {
        using var world = new World(16);
        Entity first = world.Create(new Position { X = 7 }, new Velocity { X = 2 });
        Entity second = world.Create(new Position { X = 9 }, new Velocity { X = 3 });
        using var checkpoint = new WorldCheckpoint();
        var serializer = new WorldSerializer(new SerializationRegistry().Register<Position>().Register<Velocity>().Register<Health>());
        var output = new ArrayBufferWriter<byte>();
        checkpoint.Capture(world, CheckpointProgress.Preserve);
        serializer.Save(checkpoint, output);
        world.Set(first, new Health { Value = 17 });
        Entity third = world.Create(new Position { X = 23 }, new Velocity { X = 4 }, new Health { Value = 29 });
        checkpoint.Capture(world, CheckpointProgress.Preserve);
        output.Clear(); serializer.Save(checkpoint, output);
        world.Clear(); serializer.Load(world, output.WrittenSpan);
        Require(world.EntityCount == 3 && world.ReadValue<Health>(first).Value == 17
            && world.ReadValue<Position>(second).X == 9 && world.ReadValue<Health>(third).Value == 29,
            "recapture updates checkpoint encoding after layout growth while keeping entity identities and values");
    }
    internal static void CheckBinary() { CheckSnapshot(1); CheckSnapshot(2); }
    internal static void CheckBackground()
    {
        try { CheckSnapshot(3); CheckSnapshot(4); }
        finally
        {
            File.Delete(Path.Combine("artifacts", "serialization-check.save"));
            File.Delete(Path.Combine("artifacts", "serialization-check.save.previous"));
            File.Delete(Path.Combine("artifacts", "serialization-check.save.lock"));
        }
    }

    private static void CheckSnapshot(int format)
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 3, EnableCounters = false });
        using var world = new World(300, chunkCapacity: 128, execution: new WorldJobs(scheduler));
        Entity[] entities = new Entity[257];
        for (int i = 0; i < entities.Length; i++) entities[i] = world.Create(new ArchiveValue(i), new ArchiveStep(2), new Position { X = i });
        world.Disable<ArchiveStep>(entities[7]);
        world.Index<IndexedNumber, int>();
        world.Set(entities[3], new IndexedNumber(7, 3));
        world.GroupBy<ArchiveShared>();
        world.SetShared(entities[2], new ArchiveShared(7));
        world.SetSparse(entities[1], new ArchiveLink(entities[0], 11));
        world.AddBuffer<ArchiveLink>(entities[0]);
        using (var values = world.GetBuffer<ArchiveLink>(entities[0])) values.Add(new(entities[1], 21));
        world.AddBuffer<ArchiveLink>(entities[6]);
        using (var values = world.GetBuffer<ArchiveLink>(entities[6])) values.Add(new(entities[0], 41));
        world.RemoveBuffer<ArchiveLink>(entities[6]);
        var initialResource = new ArchiveResource(17);
        world.SetResource(initialResource);
        world.SetResource(37);
        world.RemoveResource<int>();
        Entity firstParent = world.Create(), secondParent = world.Create();
        Hierarchy<ArchiveTree>.SetParents(world, [new(entities[0], firstParent), new(entities[1], firstParent)]);
        Hierarchy<ArchiveTree>.SetOrdered(world, firstParent, true, [entities[1], entities[0]]);
        var firstEdge = world.CreateRelation(firstParent, entities[0], new ArchiveEdge(31));
        var secondEdge = world.CreateRelation(firstParent, entities[1], new ArchiveEdge(32));
        Relations<ArchiveEdge>.SetOrdered(world, firstParent, RelationAdjacencyRole.Outgoing, true, [secondEdge.Entity, firstEdge.Entity]);
        var group = new SystemGroup(world, scheduler, 128);
        SystemState state = group.Add<ArchiveValue, ArchiveStep, AdvanceArchive>(world.Query<ArchiveValue, ArchiveStep>(), new AdvanceArchive());
        ulong baseline = world.Version;
        group.Execute();
        world.Remove<ArchiveValue>(entities[5]);
        Hierarchy<ArchiveTree>.SetParentDeferred(world, entities[0], firstParent, 0);
        Hierarchy<ArchiveTree>.SetParentDeferred(world, entities[1], secondParent);
        world.RetargetRelationDeferred(secondEdge, secondParent, entities[1]);
        ulong capturedVersion = world.Version, capturedSystem = state.LastSystemVersion;
        int capturedCount = world.EntityCount;
        using var checkpoint = new WorldCheckpoint();
        var serializer = new WorldSerializer(CreateRegistry(format >= 2));
        byte[] snapshot = [];
        Task? pending = null;
        MemoryStream? pendingOutput = null;
        bool checkedFresh = false;
        string savedFile = Path.Combine("artifacts", "serialization-check.save");
        void Capture(CheckpointProgress progress)
        {
            if (format == 0) checkpoint.Capture(world, progress, group);
            else if (format == 1)
            {
                var output = new ArrayBufferWriter<byte>();
                serializer.Save(world, output, SavePurpose.RawCheckpoint, progress, group);
                snapshot = output.WrittenSpan.ToArray();
            }
            else if (format == 2)
            {
                using var output = new MemoryStream();
                serializer.Save(world, output, SavePurpose.DurableSave, progress, group);
                snapshot = output.ToArray();
            }
            else if (format == 3)
            {
                pendingOutput = new MemoryStream();
                pending = serializer.SaveAsync(world, pendingOutput, SavePurpose.DurableSave, progress, group);
            }
            else pending = serializer.SaveFileAsync(world, savedFile, SavePurpose.DurableSave, progress, group);
        }
        void Restore()
        {
            pending?.GetAwaiter().GetResult();
            if (pendingOutput is not null)
            { snapshot = pendingOutput.ToArray(); pendingOutput.Dispose(); pendingOutput = null; }
            if (format == 0) checkpoint.Restore(world, group);
            else if (format == 1) serializer.Load(world, snapshot, null, group);
            else if (format == 4) serializer.LoadFile(world, savedFile, null, group);
            else
            {
                using var input = new SequentialStream(new MemoryStream(snapshot));
                serializer.Load(world, input, null, group);
            }
            if (format == 3 && !checkedFresh)
            {
                using var fresh = new World();
                var versions = new SavedSystemVersions(group.VersionCount);
                serializer.Load(fresh, snapshot, null, versions);
                Require(fresh.EntityCount == capturedCount && versions.Values[0] == capturedSystem
                    && fresh.Query(new QueryDescription().WithDisabled<ArchiveStep>()).Count == 1
                    && fresh.Query(new QueryDescription().InGroup<ArchiveShared>(7)).Count == 1,
                    "fresh world loads complete identities, system progress, enabled filters and group declarations");
                using (var found = fresh.Index<IndexedNumber, int>().Find(7)) Require(found.Count == 1, "fresh world builds its value index from loaded main values");
                using (var children = Hierarchy<ArchiveTree>.GetChildren(fresh, firstParent))
                    Require(children.Entities.SequenceEqual([entities[1], entities[0]]), "fresh world returns the captured applied hierarchy order");
                using (var edges = fresh.GetOutgoingRelations<ArchiveEdge>(firstParent))
                    Require(edges.Count == 2 && edges.Entries[0].Edge == secondEdge.Entity, "fresh world returns the captured applied adjacency order");
                checkedFresh = true;
            }
        }
        Capture(CheckpointProgress.Preserve);

        world.Set(entities[0], new ArchiveValue(999));
        world.Enable<ArchiveStep>(entities[7]);
        using (var values = world.GetBuffer<ArchiveLink>(entities[0])) values[0] = new(entities[0], 999);
        world.SetSparse(entities[1], new ArchiveLink(entities[1], 999));
        world.Set(entities[3], new IndexedNumber(9, 3));
        using (var resource = world.OpenResourceWrite<ArchiveResource>()) resource.Value.Items[0] = 999;
        world.ClearRemoved<Buffer<ArchiveLink>>();
        Hierarchy<ArchiveTree>.Maintain(world);
        world.MaintainRelations<ArchiveEdge>();
        group.Execute();
        Entity later = world.Create(new Position { X = 999 });
        Restore();

        Require(world.Version == capturedVersion && world.EntityCount == capturedCount && state.LastSystemVersion == capturedSystem,
            "checkpoint restore publishes captured world and system progress");
        Require(!world.IsAlive(later) && !world.IsEnabled<ArchiveStep>(entities[7]), "restore retains captured identities and enabled states");
        for (int i = 0; i < entities.Length; i++)
            if (i == 5) Require(!world.Has<ArchiveValue>(entities[i]) && world.ReadValue<Removed<ArchiveValue>>(entities[i]).Value.Value == i + 2,
                "retained old values restore with their captured membership");
            else Require(world.ReadValue<ArchiveValue>(entities[i]).Value == i + (i == 7 ? 0 : 2), "all saved rows return to their captured values");
        Require(world.Query(new QueryDescription().WithChanged<ArchiveValue>(baseline)).Count == 255, "restored row versions rebuild exact change summaries");
        using (var buffer = world.ReadBuffer<ArchiveLink>(entities[0])) Require(buffer[0] == new ArchiveLink(entities[1], 21), "buffer copies own independent elements");
        using (var buffer = world.ReadRemovedBuffer<ArchiveLink>(entities[6])) Require(buffer[0] == new ArchiveLink(entities[0], 41), "removed buffer facts retain independent contents");
        using (var sparse = world.OpenSparseRead<ArchiveLink>()) Require(sparse.Read(entities[1]) == new ArchiveLink(entities[0], 11), "sparse copies rebuild their entity map");
        using (var found = world.Index<IndexedNumber, int>().Find(7)) Require(found.Count == 1 && found.Entities[0] == entities[3], "restore completes the active value index");
        Require(world.Query(new QueryDescription().InGroup<ArchiveShared>(7)).Count == 1, "restore retains grouped shared membership");
        using (var resource = world.OpenResourceRead<ArchiveResource>()) Require(resource.Value.Items[0] == 17 && !resource.Value.Released && initialResource.Released,
            "restored resources own their values and release the replaced instance");
        Require(!world.HasResource<int>(), "removed resources stay absent through capture and restore");
        using (var children = Hierarchy<ArchiveTree>.GetChildren(world, firstParent)) Require(children.Entities.SequenceEqual([entities[1], entities[0]]), "preserved progress restores the applied child order");
        using (var edges = world.GetOutgoingRelations<ArchiveEdge>(firstParent)) Require(edges.Entries[0].Edge == secondEdge.Entity && edges.Entries[1].Edge == firstEdge.Entity,
            "preserved progress restores applied relation order");
        Hierarchy<ArchiveTree>.Maintain(world);
        world.MaintainRelations<ArchiveEdge>();
        using (var children = Hierarchy<ArchiveTree>.GetChildren(world, secondParent)) Require(children.Count == 1 && children.Entities[0] == entities[1], "restored hierarchy progress consumes the captured deferred edit");
        using (var edges = world.GetOutgoingRelations<ArchiveEdge>(secondParent)) Require(edges.Count == 1 && edges.Entries[0].Edge == secondEdge.Entity,
            "restored relation progress consumes the captured deferred edit");
        Capture(CheckpointProgress.Rebuild);
        world.Clear();
        Restore();
        Require(world.EntityCount == capturedCount && state.LastSystemVersion == world.Version, "rebuild mode initializes consumers at the restored observation point");
        using (var children = Hierarchy<ArchiveTree>.GetChildren(world, secondParent)) Require(children.Count == 1, "rebuild mode returns completed hierarchy views");
        using (var edges = world.GetOutgoingRelations<ArchiveEdge>(secondParent)) Require(edges.Count == 1, "rebuild mode returns completed relation views");
        world.Create(new ArchiveValue(17), new ArchiveStep(2));
        if (format == 4) Require(File.Exists(savedFile + ".previous"), "file commit retains the previous completed generation");
        Console.WriteLine($"Snapshot format {format}: complete rows, identities, buffers and old values, sparse/shared/indexed storage, resources, ordered deferred topology and system progress passed.");
    }

    private static SerializationRegistry CreateRegistry(bool durable)
    {
        var registry = new SerializationRegistry();
        if (durable) registry.Register<Position, ArchivePositionCodec>(Key(1));
        else registry.Register<Position>(Key(1));
        return registry.Register<ArchiveValue, ArchiveValueCodec>(Key(2))
            .Register<ArchiveStep, ArchiveStepCodec>(Key(3))
            .Register<IndexedNumber, ArchiveIndexCodec>(Key(4))
            .RegisterShared<ArchiveShared, ArchiveSharedCodec>(Key(5))
            .RegisterSparse<ArchiveLink, ArchiveLinkCodec>(Key(6))
            .RegisterBuffer<ArchiveLink, ArchiveLinkCodec>(Key(7))
            .RegisterResource<ArchiveResource, ArchiveResourceCodec>(Key(8))
            .RegisterHierarchy<ArchiveTree>(Key(9))
            .RegisterRelation<ArchiveEdge, ArchiveEdgeCodec>(Key(10));
    }
    private sealed class SavedSystemVersions(int count) : ISystemVersions
    {
        internal readonly ulong[] Values = new ulong[count * 2];
        public int VersionCount => count;
        public void CopyVersions(Span<ulong> target) => Values.CopyTo(target);
        public void RestoreVersions(ReadOnlySpan<ulong> values) => values.CopyTo(Values);
    }
    private static SerializationTypeKey Key(int value) => new(new Guid(value, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), "Archive." + value, 1);
    private readonly struct ArchiveValueCodec : IComponentCodec<ArchiveValue>
    {
        public void Write(ref DataWriter writer, in ArchiveValue value) => writer.WriteInt32(value.Value);
        public void Read(ref DataReader reader, out ArchiveValue value) => value = new(reader.ReadInt32());
    }
    private readonly struct ArchiveStepCodec : IComponentCodec<ArchiveStep>
    {
        public void Write(ref DataWriter writer, in ArchiveStep value) => writer.WriteInt32(value.Value);
        public void Read(ref DataReader reader, out ArchiveStep value) => value = new(reader.ReadInt32());
    }
    private readonly struct ArchiveSharedCodec : IComponentCodec<ArchiveShared>
    {
        public void Write(ref DataWriter writer, in ArchiveShared value) => writer.WriteInt32(value.Value);
        public void Read(ref DataReader reader, out ArchiveShared value) => value = new(reader.ReadInt32());
    }
    private readonly struct ArchivePositionCodec : IComponentCodec<Position>
    {
        public void Write(ref DataWriter writer, in Position value)
        { writer.WriteSingle(value.X); writer.WriteSingle(value.Y); }
        public void Read(ref DataReader reader, out Position value) => value = new() { X = reader.ReadSingle(), Y = reader.ReadSingle() };
    }
    private readonly struct ArchiveIndexCodec : IComponentCodec<IndexedNumber>
    {
        public void Write(ref DataWriter writer, in IndexedNumber value) { writer.WriteInt32(value.Value); writer.WriteInt32(value.Ordinal); }
        public void Read(ref DataReader reader, out IndexedNumber value) => value = new(reader.ReadInt32(), reader.ReadInt32());
    }
    private readonly struct ArchiveLinkCodec : IComponentCodec<ArchiveLink>
    {
        public void Write(ref DataWriter writer, in ArchiveLink value) { writer.WriteEntity(value.Entity); writer.WriteInt64(value.Value); }
        public void Read(ref DataReader reader, out ArchiveLink value) => value = new(reader.ReadEntity(), reader.ReadInt64());
    }
    private readonly struct ArchiveEdgeCodec : IComponentCodec<ArchiveEdge>
    {
        public void Write(ref DataWriter writer, in ArchiveEdge value) => writer.WriteInt64(value.Value);
        public void Read(ref DataReader reader, out ArchiveEdge value) => value = new(reader.ReadInt64());
    }
    private readonly struct ArchiveResourceCodec : IValueCodec<ArchiveResource>
    {
        public void Write(ref DataWriter writer, in ArchiveResource value) => writer.WriteInt32(value.Items[0]);
        public void Read(ref DataReader reader, out ArchiveResource value) => value = new(reader.ReadInt32());
    }

    private static void Require(bool condition, string behavior)
    { if (!condition) throw new InvalidOperationException(behavior); }

    internal static void CheckIo()
    {
        var output = new ArrayBufferWriter<byte>();
        string text = string.Concat(Enumerable.Repeat("上海科技大学 ECS 存档 🚀，同一份数据。", 500));
        Guid id = Guid.NewGuid();
        var writer = new DataWriter(output);
        WriteValues(ref writer, id, text);
        writer.Flush();
        var reader = new DataReader(output.WrittenSpan);
        ReadValues(ref reader, id, text);
        Require(reader.Position == output.WrittenCount, "span decoding consumes the complete declared values");
        using var storage = new MemoryStream();
        using var stream = new SequentialStream(storage);
        writer = new DataWriter(stream);
        WriteValues(ref writer, id, text);
        writer.Flush();
        storage.Position = 0;
        reader = new DataReader(stream);
        try { ReadValues(ref reader, id, text); Require(reader.Position == storage.Length, "stream decoding consumes the complete declared values"); }
        finally { reader.Dispose(); }
        Console.WriteLine("Serialization IO: fixed-endian fields, entity identities, nullable Unicode strings, final string storage and sequential streams passed.");
    }

    internal static void WriteValues(ref DataWriter writer, Guid id, string text)
    {
        writer.WriteInt32(0x01020304); writer.WriteUInt64(0x0102030405060708);
        writer.WriteSingle(1.25f); writer.WriteDouble(7.5); writer.WriteGuid(id);
        writer.WriteEntity(new(17, 3)); writer.WriteString(text); writer.WriteString(null);
    }
    private static void ReadValues(ref DataReader reader, Guid id, string text)
    {
        Require(reader.ReadInt32() == 0x01020304 && reader.ReadUInt64() == 0x0102030405060708
            && reader.ReadSingle() == 1.25f && reader.ReadDouble() == 7.5 && reader.ReadGuid() == id
            && reader.ReadEntity() == new Entity(17, 3) && reader.ReadString() == text && reader.ReadString() is null,
            "paired codec operations retain all field values");
    }

    private sealed class SequentialStream(Stream stream) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => stream.Read(buffer);
        public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => stream.Write(buffer);
        public override void Flush() => stream.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
