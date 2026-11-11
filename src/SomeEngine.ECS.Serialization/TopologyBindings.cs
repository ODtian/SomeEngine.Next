using System.Buffers;
using System.Reflection;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Serialization;

internal interface ISavedGroupBinding
{
    void Activate(World world);
}

internal readonly struct SavedGroupBinding<T> : ISavedGroupBinding where T : struct, IGroupComponent
{
    public void Activate(World world) => world.GroupBy<T>();
}

internal static class SavedGroups
{
    internal static ISavedGroupBinding? Create(Type type) => typeof(IGroupComponent).IsAssignableFrom(type)
        ? (ISavedGroupBinding)Activator.CreateInstance(typeof(SavedGroupBinding<>).MakeGenericType(type))! : null;
}

internal readonly struct ParentCodec<T> : IComponentCodec<Parent<T>>, ISelectionCodec<Parent<T>> where T : struct, IHierarchyDomain
{
    public bool Includes(in Parent<T> value, IReadOnlySet<Entity> entities) => entities.Contains(value.Value);
    public void Write(ref DataWriter writer, in Parent<T> value, IReadOnlySet<Entity> entities) => Write(ref writer, value);
    public void Write(ref DataWriter writer, in Parent<T> value) => writer.WriteEntity(value.Value);
    public void Read(ref DataReader reader, out Parent<T> value) => value = new(reader.ReadEntity());
}
internal readonly struct ChildrenCodec<T> : IComponentCodec<Children<T>>, ISelectionCodec<Children<T>> where T : struct, IHierarchyDomain
{
    public bool Includes(in Children<T> value, IReadOnlySet<Entity> entities)
    {
        foreach (Entity child in value.Entities) if (entities.Contains(child)) return true;
        return false;
    }
    public void Write(ref DataWriter writer, in Children<T> value, IReadOnlySet<Entity> entities)
    {
        int count = 0;
        foreach (Entity child in value.Entities) if (entities.Contains(child)) count++;
        writer.WriteInt32(count); writer.WriteBoolean(value.IsOrdered);
        foreach (Entity child in value.Entities) if (entities.Contains(child)) writer.WriteEntity(child);
    }
    public void Write(ref DataWriter writer, in Children<T> value)
    { writer.WriteInt32(value.Count); writer.WriteBoolean(value.IsOrdered); writer.WriteEntities(value.Entities); }
    public void Read(ref DataReader reader, out Children<T> value)
    {
        int count = reader.Budget.TopologyEntryCount(reader.ReadInt32(), "child member");
        bool ordered = reader.ReadBoolean();
        Entity[]? items = count == 0 ? null : Children<T>.Rent(count);
        value = new(items, count, ordered);
        var children = new HashSet<Entity>();
        for (int i = 0; i < count; i++)
        {
            Entity child = items![i] = reader.ReadEntity();
            if (!children.Add(child)) throw new InvalidDataException("A child list contains a child twice.");
        }
    }
}
internal readonly struct EndpointsCodec<T> : IComponentCodec<RelationEndpoints<T>>, ISelectionCodec<RelationEndpoints<T>> where T : struct
{
    public bool Includes(in RelationEndpoints<T> value, IReadOnlySet<Entity> entities)
        => entities.Contains(value.Source) && entities.Contains(value.Target);
    public void Write(ref DataWriter writer, in RelationEndpoints<T> value, IReadOnlySet<Entity> entities) => Write(ref writer, value);
    public void Write(ref DataWriter writer, in RelationEndpoints<T> value)
    { writer.WriteEntity(value.Source); writer.WriteEntity(value.Target); }
    public void Read(ref DataReader reader, out RelationEndpoints<T> value) => value = new(reader.ReadEntity(), reader.ReadEntity());
}

internal abstract class SavedService(SavedColumn type)
{
    internal readonly SavedColumn Type = type;
    internal abstract IWorldDataService? Find(World world);
    internal abstract void Maintain(World world);
    internal abstract void Write(ref DataWriter writer, World world, bool preserve);
    internal abstract void WriteSelected(ref DataWriter writer, World world, IReadOnlySet<Entity> entities);
    internal abstract long CountSelectedRecords(World world, IReadOnlySet<Entity> entities);
    internal abstract bool Matches(WorldServiceData data);
    internal abstract void WriteData(ref DataWriter writer, WorldServiceData data);
    internal abstract void Read(ref DataReader reader, World world, bool preserve);
    internal virtual void ValidateMain(World world) { }
    internal virtual long CountRecords(World world, bool preserve) => 0;
    internal virtual long CountRecords(WorldCheckpoint checkpoint, WorldServiceData data) => 0;

    protected static long CountRows(World world, int type)
    {
        long count = 0;
        for (int i = 0; i < world.ArchetypeCount; i++)
            if (world.Archetypes[i].Types.Bits.Has(type))
                foreach (Chunk chunk in world.Archetypes[i].AllChunks)
                    count += CountRows(ref chunk.Columns[type], chunk.Count);
        return count;
    }

    protected static long CountRows(WorldCheckpoint checkpoint, int type)
    {
        long count = 0;
        foreach (ChunkData chunk in checkpoint.Chunks.AsSpan(0, checkpoint.ChunkCount))
        {
            int column = Array.BinarySearch(chunk.Types.Types, type);
            if (column >= 0) count += CountRows(ref chunk.Columns[column], chunk.Count);
        }
        return count;
    }

    private static int CountRows(ref ColumnEntry column, int rows)
    {
        if (column.Presence is null) return rows;
        int count = 0;
        for (int row = 0; row < rows; row++) if (column.Has(row)) count++;
        return count;
    }

    protected static Entity[] ReadEntities(ref DataReader reader)
    {
        int count = reader.Budget.TopologyEntryCount(reader.ReadInt32(), "topology entity");
        var values = new Entity[count];
        for (int i = 0; i < count; i++) values[i] = reader.MapEntity(reader.ReadEntity(), true);
        return values;
    }
    protected static void WriteEntities(ref DataWriter writer, scoped ReadOnlySpan<Entity> values)
    { writer.WriteInt32(values.Length); writer.WriteEntities(values); }

    protected static List<Entity> ReadMembers(ref DataReader reader)
    {
        int count = reader.Budget.TopologyEntryCount(reader.ReadInt32(), "topology entity");
        var values = new List<Entity>(count);
        for (int i = 0; i < count; i++) values.Add(reader.MapEntity(reader.ReadEntity(), true));
        return values;
    }
}

internal sealed class SavedHierarchy<T>(SavedColumn type) : SavedService(type) where T : struct, IHierarchyDomain
{
    internal override long CountSelectedRecords(World world, IReadOnlySet<Entity> entities)
    {
        long count = 0;
        foreach (Entity entity in entities)
        {
            if (world.Has<Parent<T>>(entity) && entities.Contains(world.Read<Parent<T>>(entity).Value)) count++;
            if (!world.Has<Children<T>>(entity)) continue;
            Children<T> children = world.Read<Children<T>>(entity);
            int members = 0;
            foreach (Entity child in children.Entities) if (entities.Contains(child)) members++;
            if (members != 0) count += 1L + members;
        }
        var data = ((HierarchyData<T>)Find(world)!).ReadData(false);
        foreach (Entity entity in data.EmptyOrdered) if (entities.Contains(entity)) count++;
        return count;
    }
    internal override void WriteSelected(ref DataWriter writer, World world, IReadOnlySet<Entity> entities)
    {
        var data = ((HierarchyData<T>)Find(world)!).ReadData(false);
        writer.WriteBoolean(data.Ordered); writer.WriteBoolean(false);
        writer.WriteUInt64(0); writer.WriteUInt64(0);
        writer.WriteInt32(0); writer.WriteInt32(0); writer.WriteInt32(0); writer.WriteInt32(0);
        int count = 0;
        foreach (Entity entity in data.EmptyOrdered) if (entities.Contains(entity)) count++;
        writer.WriteInt32(count);
        foreach (Entity entity in data.EmptyOrdered) if (entities.Contains(entity)) writer.WriteEntity(entity);
    }
    internal override void ValidateMain(World world)
    {
        var visited = new byte[world.Used + 1];
        int type = ComponentType<Parent<T>>.TypeId;
        for (int table = 0; table < world.ArchetypeCount; table++)
            foreach (Chunk chunk in world.Archetypes[table].AllChunks)
            {
                if ((uint)type >= (uint)chunk.Columns.Length || chunk.Columns[type].ColumnPlusOne == 0) continue;
                for (int row = 0; row < chunk.Count; row++)
                {
                    Entity start = chunk.Entities[row], current = start;
                    while (current != Entity.Null && visited[current.Id] == 0)
                    {
                        visited[current.Id] = 1;
                        current = ReadParent(world, current, type);
                    }
                    if (current != Entity.Null && visited[current.Id] == 1)
                        throw new InvalidDataException("Saved parent links form a cycle.");
                    current = start;
                    while (current != Entity.Null && visited[current.Id] == 1)
                    {
                        visited[current.Id] = 2;
                        current = ReadParent(world, current, type);
                    }
                }
            }
    }

    private static Entity ReadParent(World world, Entity entity, int type)
    {
        ref EntityRecord record = ref world.Records[entity.Id];
        if (record.Chunk is not { } chunk || (uint)type >= (uint)chunk.Columns.Length
            || !chunk.Columns[type].Has(record.Row)) return Entity.Null;
        return ColumnEntry.At<Parent<T>>(ref chunk.Columns[type], record.Row).Value;
    }

    private readonly IReferencePatcher<Children<T>>? _childrenPatcher = ReferencePatchers.DefaultValue<Children<T>>();
    internal override bool Matches(WorldServiceData data) => data is HierarchyData<T>.SavedData;
    internal override IWorldDataService? Find(World world) => world.FindLifecycle<HierarchyData<T>>();
    internal override void Maintain(World world) { if (Find(world) is not null) Hierarchy<T>.Maintain(world); }
    internal override long CountRecords(World world, bool preserve)
    {
        if (Find(world) is not HierarchyData<T> source) return 0;
        var data = source.ReadData(preserve);
        long count = CountRows(world, ComponentType<Parent<T>>.TypeId) + data.Applied.Length
            + (data.Retired?.Count ?? 0) + data.EmptyOrdered.Count;
        int children = ComponentType<Children<T>>.TypeId;
        for (int i = 0; i < world.ArchetypeCount; i++)
            if (world.Archetypes[i].Types.Bits.Has(children))
                foreach (Chunk chunk in world.Archetypes[i].AllChunks)
                    count += CountChildren(ref chunk.Columns[children], chunk.Count);
        if (data.RetiredChildren is { } retired)
            foreach (var pair in retired) count += 1L + pair.Value.Count;
        if (data.Future is { } future)
            foreach (var pair in future) count += 1L + pair.Value.Count;
        return count;
    }

    internal override long CountRecords(WorldCheckpoint checkpoint, WorldServiceData source)
    {
        var data = (HierarchyData<T>.SavedData)source;
        long count = CountRows(checkpoint, ComponentType<Parent<T>>.TypeId) + data.AppliedCount
            + data.RetiredCount + data.EmptyOrderedCount;
        int children = ComponentType<Children<T>>.TypeId;
        foreach (ChunkData chunk in checkpoint.Chunks.AsSpan(0, checkpoint.ChunkCount))
        {
            int column = Array.BinarySearch(chunk.Types.Types, children);
            if (column >= 0) count += CountChildren(ref chunk.Columns[column], chunk.Count);
        }
        foreach (var pair in data.RetiredChildren.AsSpan(0, data.RetiredChildCount)) count += 1L + pair.Value.Count;
        foreach (var pair in data.Future.AsSpan(0, data.FutureCount)) count += 1L + pair.Value.Count;
        return count;
    }

    private static long CountChildren(ref ColumnEntry column, int rows)
    {
        long count = 0;
        for (int row = 0; row < rows; row++)
            if (column.Has(row)) count += 1L + ColumnEntry.At<Children<T>>(ref column, row).Count;
        return count;
    }
    internal override void Write(ref DataWriter writer, World world, bool preserve)
    {
        var data = ((HierarchyData<T>)Find(world)!).ReadData(preserve);
        writer.WriteBoolean(data.Ordered); writer.WriteBoolean(data.Deferred);
        writer.WriteUInt64(data.Version); writer.WriteUInt64(data.ReaderPosition);
        writer.WriteInt32(data.Applied.Length);
        WriteApplied(ref writer, data.Applied);
        writer.WriteInt32(data.Retired?.Count ?? 0);
        if (data.Retired is { } retired)
            foreach (var pair in retired) { writer.WriteEntity(pair.Key); WriteApplied(ref writer, pair.Value); }
        writer.WriteInt32(data.RetiredChildren?.Count ?? 0);
        if (data.RetiredChildren is { } children)
            foreach (var pair in children) { writer.WriteEntity(pair.Key); default(ChildrenCodec<T>).Write(ref writer, pair.Value); }
        writer.WriteInt32(data.Future?.Count ?? 0);
        if (data.Future is { } future)
            foreach (var pair in future) { writer.WriteEntity(pair.Key); WriteEntities(ref writer, CollectionsMarshal.AsSpan(pair.Value)); }
        writer.WriteInt32(data.EmptyOrdered.Count);
        foreach (Entity entity in data.EmptyOrdered) writer.WriteEntity(entity);
    }
    internal override void WriteData(ref DataWriter writer, WorldServiceData source)
    {
        var data = (HierarchyData<T>.SavedData)source;
        writer.WriteBoolean(data.Ordered); writer.WriteBoolean(data.Deferred);
        writer.WriteUInt64(data.Version); writer.WriteUInt64(data.ReaderPosition);
        writer.WriteInt32(data.AppliedCount);
        WriteApplied(ref writer, data.Applied.AsSpan(0, data.AppliedCount));
        writer.WriteInt32(data.RetiredCount);
        foreach (var pair in data.Retired.AsSpan(0, data.RetiredCount)) { writer.WriteEntity(pair.Key); WriteApplied(ref writer, pair.Value); }
        writer.WriteInt32(data.RetiredChildCount);
        foreach (var pair in data.RetiredChildren.AsSpan(0, data.RetiredChildCount))
        { writer.WriteEntity(pair.Key); default(ChildrenCodec<T>).Write(ref writer, pair.Value); }
        writer.WriteInt32(data.FutureCount);
        foreach (var pair in data.Future.AsSpan(0, data.FutureCount)) { writer.WriteEntity(pair.Key); WriteEntities(ref writer, CollectionsMarshal.AsSpan(pair.Value)); }
        WriteEntities(ref writer, data.EmptyOrdered.AsSpan(0, data.EmptyOrderedCount));
    }
    internal override void Read(ref DataReader reader, World world, bool preserve)
    {
        using var data = new HierarchyData<T>.SavedData
        {
            Preserve = preserve, Ordered = reader.ReadBoolean(), Deferred = reader.ReadBoolean(),
            Version = reader.ReadUInt64(), ReaderPosition = reader.ReadUInt64(),
        };
        int count = reader.Budget.TopologyEntryCount(reader.ReadInt32(), "applied parent");
        data.Applied = count == 0 ? [] : new AppliedParent[Math.Max(count, world.Records.Length)];
        data.AppliedCount = data.Applied.Length;
        for (int i = 0; i < count; i++)
        {
            AppliedParent value = ReadApplied(ref reader);
            if (value.Generation != 0) data.Applied[reader.MapId(i)] = value;
        }
        count = reader.Budget.TopologyEntryCount(reader.ReadInt32(), "retired parent");
        data.Retired = new KeyValuePair<Entity, AppliedParent>[count];
        data.RetiredCount = count;
        for (int i = 0; i < count; i++) data.Retired[i] = new(reader.MapEntity(reader.ReadEntity(), true), ReadApplied(ref reader));
        count = reader.Budget.TopologyEntryCount(reader.ReadInt32(), "retired child list");
        data.RetiredChildren = new KeyValuePair<Entity, Children<T>>[count];
        data.RetiredChildCount = count;
        for (int i = 0; i < count; i++)
        {
            Entity entity = reader.MapEntity(reader.ReadEntity(), true);
            Children<T> value = default;
            try
            {
                default(ChildrenCodec<T>).Read(ref reader, out value);
                reader.Patch(ref value, _childrenPatcher, true);
                data.RetiredChildren[i] = new(entity, value);
                value = default;
            }
            finally { ComponentType<Children<T>>.ReleaseValue(world, ref value); }
        }
        count = reader.Budget.TopologyEntryCount(reader.ReadInt32(), "projected child list");
        data.Future = new KeyValuePair<Entity, List<Entity>>[count];
        for (int i = 0; i < count; i++) data.Future[i] = new(reader.MapEntity(reader.ReadEntity(), true), ReadMembers(ref reader));
        data.FutureCount = count;
        data.EmptyOrdered = ReadEntities(ref reader);
        data.EmptyOrderedCount = data.EmptyOrdered.Length;
        data.Restore(world, true);
    }
    private static void WriteApplied(ref DataWriter writer, scoped ReadOnlySpan<AppliedParent> values)
    {
        if (BitConverter.IsLittleEndian) writer.WriteRawBytes(MemoryMarshal.AsBytes(values));
        else foreach (AppliedParent value in values) WriteApplied(ref writer, value);
    }
    private static void WriteApplied(ref DataWriter writer, in AppliedParent value)
    { writer.WriteEntity(value.Parent); writer.WriteInt32(value.Place); writer.WriteInt32(value.Generation); }
    private static AppliedParent ReadApplied(ref DataReader reader)
        => new() { Parent = reader.MapEntity(reader.ReadEntity(), true), Place = reader.ReadInt32(), Generation = reader.ReadInt32() };
}

internal sealed class SavedRelation<T>(SavedColumn type) : SavedService(type) where T : struct
{
    internal override long CountSelectedRecords(World world, IReadOnlySet<Entity> entities)
    {
        long count = 0;
        foreach (Entity entity in entities)
            if (world.Has<RelationEndpoints<T>>(entity))
            {
                RelationEndpoints<T> endpoints = world.Read<RelationEndpoints<T>>(entity);
                if (entities.Contains(endpoints.Source) && entities.Contains(endpoints.Target)) count++;
            }
        var data = ((RelationData<T>)Find(world)!).ReadData(false);
        return count + CountSelectedHeads(data.Outgoing, entities) + CountSelectedHeads(data.Incoming, entities);
    }

    private static long CountSelectedHeads(ReadOnlySpan<AdjacencyHead> heads, IReadOnlySet<Entity> entities)
    {
        long count = 0;
        for (int id = 0; id < heads.Length; id++)
        {
            AdjacencyHead head = heads[id];
            if (head.Generation == 0 || !entities.Contains(new Entity(id, head.Generation))) continue;
            count++;
            foreach (RelationEntry entry in head.Entries)
                if (entities.Contains(entry.Edge) && entities.Contains(entry.Other)) count++;
        }
        return count;
    }
    internal override void WriteSelected(ref DataWriter writer, World world, IReadOnlySet<Entity> entities)
    {
        RelationSchema schema = RelationSchema.For<T>();
        writer.WriteByte((byte)schema.Direction); writer.WriteByte((byte)schema.Cardinality); writer.WriteBoolean(schema.AllowSelfEdge);
        var data = ((RelationData<T>)Find(world)!).ReadData(false);
        writer.WriteBoolean(data.Ordered); writer.WriteBoolean(false);
        writer.WriteUInt64(0); writer.WriteUInt64(0);
        WriteSelectedHeads(ref writer, data.Outgoing, entities);
        WriteSelectedHeads(ref writer, data.Incoming, entities);
        writer.WriteInt32(0); writer.WriteInt32(0); writer.WriteInt32(0); writer.WriteInt32(0);
    }

    private static void WriteSelectedHeads(ref DataWriter writer, ReadOnlySpan<AdjacencyHead> heads, IReadOnlySet<Entity> entities)
    {
        writer.WriteInt32(heads.Length);
        for (int id = 0; id < heads.Length; id++)
        {
            AdjacencyHead head = heads[id];
            if (head.Generation == 0 || !entities.Contains(new Entity(id, head.Generation)))
            { writer.WriteInt32(0); writer.WriteInt32(0); writer.WriteBoolean(false); continue; }
            int count = 0;
            foreach (RelationEntry entry in head.Entries)
                if (entities.Contains(entry.Edge) && entities.Contains(entry.Other)) count++;
            writer.WriteInt32(head.Generation); writer.WriteInt32(count); writer.WriteBoolean(head.IsOrdered);
            foreach (RelationEntry entry in head.Entries)
                if (entities.Contains(entry.Edge) && entities.Contains(entry.Other))
                { writer.WriteEntity(entry.Other); writer.WriteEntity(entry.Edge); }
        }
    }
    internal override bool Matches(WorldServiceData data) => data is RelationData<T>.SavedData;
    internal override IWorldDataService? Find(World world) => world.FindLifecycle<RelationData<T>>();
    internal override void Maintain(World world) { if (Find(world) is not null) world.MaintainRelations<T>(); }
    internal override long CountRecords(World world, bool preserve)
    {
        if (Find(world) is not RelationData<T> source) return 0;
        var data = source.ReadData(preserve);
        long count = CountRows(world, ComponentType<RelationEndpoints<T>>.TypeId) + data.Applied.Length
            + (data.Retired?.Count ?? 0) + CountHeads(data.Outgoing) + CountHeads(data.Incoming);
        if (data.RetiredHeads is { } retired)
            foreach (var pair in retired) count += 1L + pair.Value.Count;
        if (data.Future is { } future)
            foreach (var pair in future) count += 1L + pair.Value.Count;
        return count;
    }

    internal override long CountRecords(WorldCheckpoint checkpoint, WorldServiceData source)
    {
        var data = (RelationData<T>.SavedData)source;
        long count = CountRows(checkpoint, ComponentType<RelationEndpoints<T>>.TypeId) + data.AppliedCount
            + data.RetiredCount + CountHeads(data.Outgoing.AsSpan(0, data.OutgoingCount)) + CountHeads(data.Incoming.AsSpan(0, data.IncomingCount));
        foreach (var pair in data.RetiredHeads.AsSpan(0, data.RetiredHeadCount)) count += 1L + pair.Value.Count;
        foreach (var pair in data.Future.AsSpan(0, data.FutureCount)) count += 1L + pair.Value.Count;
        return count;
    }

    private static long CountHeads(ReadOnlySpan<AdjacencyHead> heads)
    {
        long count = 0;
        foreach (ref readonly AdjacencyHead head in heads)
            if (head.Generation != 0) count += 1L + head.Count;
        return count;
    }
    internal override void Write(ref DataWriter writer, World world, bool preserve)
    {
        RelationSchema schema = RelationSchema.For<T>();
        writer.WriteByte((byte)schema.Direction); writer.WriteByte((byte)schema.Cardinality); writer.WriteBoolean(schema.AllowSelfEdge);
        var data = ((RelationData<T>)Find(world)!).ReadData(preserve);
        writer.WriteBoolean(data.Ordered); writer.WriteBoolean(data.Deferred);
        writer.WriteUInt64(data.Version); writer.WriteUInt64(data.ReaderPosition);
        WriteHeads(ref writer, data.Outgoing); WriteHeads(ref writer, data.Incoming);
        writer.WriteInt32(data.Applied.Length);
        WriteApplied(ref writer, data.Applied);
        writer.WriteInt32(data.Retired?.Count ?? 0);
        if (data.Retired is { } retired)
            foreach (var pair in retired) { writer.WriteEntity(pair.Key); WriteApplied(ref writer, pair.Value); }
        writer.WriteInt32(data.RetiredHeads?.Count ?? 0);
        if (data.RetiredHeads is { } heads)
            foreach (var pair in heads) { WriteKey(ref writer, pair.Key); WriteHead(ref writer, pair.Value); }
        writer.WriteInt32(data.Future?.Count ?? 0);
        if (data.Future is { } future)
            foreach (var pair in future) { WriteKey(ref writer, pair.Key); WriteEntities(ref writer, CollectionsMarshal.AsSpan(pair.Value)); }
    }
    internal override void WriteData(ref DataWriter writer, WorldServiceData source)
    {
        RelationSchema schema = RelationSchema.For<T>();
        writer.WriteByte((byte)schema.Direction); writer.WriteByte((byte)schema.Cardinality); writer.WriteBoolean(schema.AllowSelfEdge);
        var data = (RelationData<T>.SavedData)source;
        writer.WriteBoolean(data.Ordered); writer.WriteBoolean(data.Deferred);
        writer.WriteUInt64(data.Version); writer.WriteUInt64(data.ReaderPosition);
        WriteHeads(ref writer, data.Outgoing.AsSpan(0, data.OutgoingCount)); WriteHeads(ref writer, data.Incoming.AsSpan(0, data.IncomingCount));
        writer.WriteInt32(data.AppliedCount);
        WriteApplied(ref writer, data.Applied.AsSpan(0, data.AppliedCount));
        writer.WriteInt32(data.RetiredCount);
        foreach (var pair in data.Retired.AsSpan(0, data.RetiredCount)) { writer.WriteEntity(pair.Key); WriteApplied(ref writer, pair.Value); }
        writer.WriteInt32(data.RetiredHeadCount);
        foreach (var pair in data.RetiredHeads.AsSpan(0, data.RetiredHeadCount)) { WriteKey(ref writer, pair.Key); WriteHead(ref writer, pair.Value); }
        writer.WriteInt32(data.FutureCount);
        foreach (var pair in data.Future.AsSpan(0, data.FutureCount)) { WriteKey(ref writer, pair.Key); WriteEntities(ref writer, CollectionsMarshal.AsSpan(pair.Value)); }
    }
    internal override void Read(ref DataReader reader, World world, bool preserve)
    {
        var schema = new RelationSchema((RelationDirection)reader.ReadByte(), (RelationCardinality)reader.ReadByte(), reader.ReadBoolean());
        if (schema != RelationSchema.For<T>()) throw new InvalidDataException("Relation declaration differs from its registered schema.");
        using var data = new RelationData<T>.SavedData
        {
            Preserve = preserve, Ordered = reader.ReadBoolean(), Deferred = reader.ReadBoolean(),
            Version = reader.ReadUInt64(), ReaderPosition = reader.ReadUInt64(),
        };
        data.Outgoing = ReadHeads(ref reader, world.Records.Length);
        data.Incoming = ReadHeads(ref reader, world.Records.Length);
        data.OutgoingCount = data.Outgoing.Length; data.IncomingCount = data.Incoming.Length;
        int count = reader.Budget.TopologyEntryCount(reader.ReadInt32(), "applied relation");
        data.Applied = count == 0 ? [] : new AppliedEdge[Math.Max(count, world.Records.Length)];
        data.AppliedCount = data.Applied.Length;
        for (int i = 0; i < count; i++)
        {
            AppliedEdge value = ReadApplied(ref reader);
            if (value.Generation != 0) data.Applied[reader.MapId(i)] = value;
        }
        ValidateHeads(world, data.Outgoing, data.Applied, schema.Direction, true, preserve);
        ValidateHeads(world, data.Incoming, data.Applied, schema.Direction, false, preserve);
        count = reader.Budget.TopologyEntryCount(reader.ReadInt32(), "retired relation");
        data.Retired = new KeyValuePair<Entity, AppliedEdge>[count];
        data.RetiredCount = count;
        for (int i = 0; i < count; i++) data.Retired[i] = new(reader.MapEntity(reader.ReadEntity(), true), ReadApplied(ref reader));
        count = reader.Budget.TopologyEntryCount(reader.ReadInt32(), "retired adjacency");
        data.RetiredHeads = new KeyValuePair<AdjacencyKey, AdjacencyHead>[count];
        data.RetiredHeadCount = count;
        for (int i = 0; i < count; i++) data.RetiredHeads[i] = new(ReadKey(ref reader), ReadHead(ref reader));
        count = reader.Budget.TopologyEntryCount(reader.ReadInt32(), "projected adjacency");
        data.Future = new KeyValuePair<AdjacencyKey, List<Entity>>[count];
        for (int i = 0; i < count; i++) data.Future[i] = new(ReadKey(ref reader), ReadMembers(ref reader));
        data.FutureCount = count;
        data.Restore(world, true);
    }
    private static void ValidateHeads(World world, AdjacencyHead[] heads, AppliedEdge[] applied, RelationDirection direction, bool outgoing, bool preserve)
    {
        for (int id = 0; id < heads.Length; id++)
        {
            ref AdjacencyHead head = ref heads[id];
            if (head.Count == 0) continue;
            var endpoint = new Entity(id, head.Generation);
            var edges = new HashSet<Entity>();
            foreach (RelationEntry entry in head.Entries)
            {
                if (!edges.Add(entry.Edge)) throw new InvalidDataException("An adjacency head contains an edge twice.");
                AppliedEdge edge;
                if (preserve)
                {
                    if ((uint)entry.Edge.Id >= (uint)applied.Length)
                        throw new InvalidDataException("An adjacency edge requires its applied endpoints.");
                    edge = applied[entry.Edge.Id];
                }
                else
                {
                    if (!world.IsAlive(entry.Edge) || !world.Has<RelationEndpoints<T>>(entry.Edge))
                        throw new InvalidDataException("An adjacency edge requires its saved endpoints.");
                    RelationEndpoints<T> endpoints = world.Read<RelationEndpoints<T>>(entry.Edge);
                    edge = new() { Source = endpoints.Source, Target = endpoints.Target, Generation = entry.Edge.Generation };
                }
                bool matches = direction == RelationDirection.Undirected
                    ? edge.Source == endpoint && edge.Target == entry.Other || edge.Target == endpoint && edge.Source == entry.Other
                    : outgoing ? edge.Source == endpoint && edge.Target == entry.Other : edge.Target == endpoint && edge.Source == entry.Other;
                if (edge.Generation != entry.Edge.Generation || !matches)
                    throw new InvalidDataException("An adjacency member differs from its endpoint role.");
            }
        }
    }
    private static void WriteApplied(ref DataWriter writer, scoped ReadOnlySpan<AppliedEdge> values)
    {
        if (BitConverter.IsLittleEndian) writer.WriteRawBytes(MemoryMarshal.AsBytes(values));
        else foreach (AppliedEdge value in values) WriteApplied(ref writer, value);
    }
    private static void WriteApplied(ref DataWriter writer, in AppliedEdge value)
    {
        writer.WriteEntity(value.Source); writer.WriteEntity(value.Target);
        writer.WriteInt32(value.SourcePlace); writer.WriteInt32(value.TargetPlace); writer.WriteInt32(value.Generation);
    }
    private static AppliedEdge ReadApplied(ref DataReader reader) => new()
    { Source = reader.MapEntity(reader.ReadEntity(), true), Target = reader.MapEntity(reader.ReadEntity(), true),
        SourcePlace = reader.ReadInt32(), TargetPlace = reader.ReadInt32(), Generation = reader.ReadInt32() };
    private static void WriteKey(ref DataWriter writer, in AdjacencyKey value)
    { writer.WriteEntity(value.Entity); writer.WriteByte((byte)value.Role); }
    private static AdjacencyKey ReadKey(ref DataReader reader) => new(reader.MapEntity(reader.ReadEntity(), true), (RelationAdjacencyRole)reader.ReadByte());
    private static void WriteHeads(ref DataWriter writer, scoped ReadOnlySpan<AdjacencyHead> heads)
    { writer.WriteInt32(heads.Length); foreach (AdjacencyHead head in heads) WriteHead(ref writer, head); }
    private static void WriteHead(ref DataWriter writer, in AdjacencyHead head)
    {
        writer.WriteInt32(head.Generation); writer.WriteInt32(head.Count); writer.WriteBoolean(head.IsOrdered);
        if (head.Count == 0) return;
        if (BitConverter.IsLittleEndian) writer.WriteRawBytes(MemoryMarshal.AsBytes(head.Entries));
        else foreach (RelationEntry entry in head.Entries) { writer.WriteEntity(entry.Other); writer.WriteEntity(entry.Edge); }
    }
    private static AdjacencyHead[] ReadHeads(ref DataReader reader, int capacity)
    {
        int count = reader.Budget.TopologyEntryCount(reader.ReadInt32(), "adjacency head");
        if (count == 0) return [];
        var heads = new AdjacencyHead[Math.Max(count, capacity)];
        for (int i = 0; i < count; i++)
        {
            AdjacencyHead head = ReadHead(ref reader);
            if (head.Generation != 0) heads[reader.MapId(i)] = head;
        }
        return heads;
    }
    private static AdjacencyHead ReadHead(ref DataReader reader)
    {
        int generation = reader.ReadInt32();
        int count = reader.Budget.TopologyEntryCount(reader.ReadInt32(), "adjacency member");
        bool ordered = reader.ReadBoolean();
        var head = new AdjacencyHead
        { Generation = generation, CountFlags = count | (ordered ? int.MinValue : 0), Items = count == 0 ? null : AdjacencyHead.Rent(count) };
        for (int i = 0; i < count; i++)
            head.Items![i] = new(reader.MapEntity(reader.ReadEntity(), true), reader.MapEntity(reader.ReadEntity(), true));
        return head;
    }
}

public sealed partial class SerializationRegistry
{
    internal readonly List<SavedService> Services = [];
    internal readonly Dictionary<int, SavedService> ServicesByType = [];
    public SerializationRegistry RegisterHierarchy<T>(SerializationTypeKey key) where T : struct, IHierarchyDomain
    {
        RegisterColumn<Parent<T>, ParentCodec<T>>(key, default, false, SavedStorage.Parent, progress: true);
        RegisterColumn<Children<T>, ChildrenCodec<T>>(Derived(key, 16, "Children"), default, false, SavedStorage.Children, progress: true);
        var service = new SavedHierarchy<T>(Columns[SomeEngine.ECS.Storage.ComponentType<Parent<T>>.TypeId]);
        Services.Add(service); ServicesByType.Add(service.Type.Type, service);
        return this;
    }
    public SerializationRegistry RegisterRelation<T, TCodec>(SerializationTypeKey key, TCodec codec = default)
        where T : struct where TCodec : struct, IComponentCodec<T>
    {
        RegisterColumn<T, TCodec>(key, codec, false, SavedStorage.Table);
        return RegisterRelation<T>(key);
    }

    public SerializationRegistry RegisterRelation<T>(SerializationTypeKey? topologyKey = null) where T : struct
    {
        if (!Columns.TryGetValue(SomeEngine.ECS.Storage.ComponentType<T>.TypeId, out SavedColumn? payload)
            || payload.Storage != SavedStorage.Table)
            throw new InvalidOperationException("Relation endpoints require an ordinary table payload codec.");
        SerializationTypeKey key = topologyKey ?? payload.Key;
        return RegisterRelationTopology<T>(key);
    }

    internal SerializationRegistry RegisterRelationTopology<T>(SerializationTypeKey key) where T : struct
    {
        RegisterColumn<RelationEndpoints<T>, EndpointsCodec<T>>(Derived(key, 64, "Endpoints"), default, false, SavedStorage.Endpoints,
            progress: true);
        var service = new SavedRelation<T>(Columns[SomeEngine.ECS.Storage.ComponentType<RelationEndpoints<T>>.TypeId]);
        Services.Add(service); ServicesByType.Add(service.Type.Type, service);
        return this;
    }
}
