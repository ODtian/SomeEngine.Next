using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Shared;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Serialization;

internal static class SavedValues<T, TSerializer> where TSerializer : struct, IValueSerializer<T>
{
    internal static void Write(ref SaveWriter writer, ReadOnlySpan<T> values, TSerializer serializer, bool raw)
    {
        if (raw)
        {
            ref byte first = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(values));
            writer.WriteRawBytes(MemoryMarshal.CreateReadOnlySpan(ref first, values.Length * Unsafe.SizeOf<T>()));
            return;
        }
        long start = writer.Position;
        foreach (ref readonly T value in values) { TSerializer item = serializer; item.Write(ref writer, value); }
        writer.WriteInt32(checked((int)(writer.Position - start)));
    }

    internal static void Read(ref SaveReader reader, Span<T> values, TSerializer serializer, bool raw, IReferencePatcher<T>? patcher = null)
    {
        if (raw)
        {
            int length = reader.Budget.PayloadLength(checked(values.Length * Unsafe.SizeOf<T>()));
            ref byte first = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(values));
            reader.ReadRawBytes(MemoryMarshal.CreateSpan(ref first, length));
            if (patcher is not null && reader.PatchesValues)
                foreach (ref T value in values) reader.Patch(ref value, patcher);
            return;
        }
        long start = reader.Position;
        if (patcher is null || !reader.PatchesValues)
            foreach (ref T value in values) { TSerializer item = serializer; item.Read(ref reader, out value); }
        else
            foreach (ref T value in values)
            { TSerializer item = serializer; item.Read(ref reader, out value); reader.Patch(ref value, patcher); }
        long consumed = reader.Position - start;
        if (consumed != reader.Budget.PayloadLength(reader.ReadInt32()))
            throw new InvalidDataException("Value serializer length differs from its segment footer.");
    }
}

internal abstract class SavedShared(SavedTypeKey key, int type, Type valueType, Type serializerType, SerializerTraits serializerTraits)
    : SavedColumn(key, SavedStorage.Shared, type, sizeof(int), false, valueType, serializerType, serializerTraits)
{
    internal override void Write(ref SaveWriter writer, ref ColumnEntry column, int count)
    {
        Span<int> values = ColumnEntry.Span<int>(ref column, count);
        if (column.Presence is null) writer.WriteRawBytes(MemoryMarshal.AsBytes(values));
        else for (int row = 0; row < count; row++) if (column.Has(row)) writer.WriteInt32(values[row]);
    }
    internal override void Read(ref SaveReader reader, ref ColumnEntry column, int count)
    {
        Span<int> values = ColumnEntry.Span<int>(ref column, count);
        if (column.Presence is null) reader.ReadRawBytes(MemoryMarshal.AsBytes(values));
        else for (int row = 0; row < count; row++) if (column.Has(row)) values[row] = reader.ReadInt32();
        if (reader.SharedMappings is { } mappings && mappings.TryGetValue(Type, out int[]? ids))
            for (int row = 0; row < count; row++) if (column.Has(row)) values[row] = ids[values[row]];
    }
    internal abstract void WriteValues(ref SaveWriter writer, SharedValues values);
    internal abstract void WriteSelectedValues(ref SaveWriter writer, SharedValues values, ReadOnlySpan<int> ids);
    internal abstract SharedValues ReadValues(ref SaveReader reader, World world);
}

internal sealed class SavedShared<T, TSerializer>(SavedTypeKey key, TSerializer serializer, bool raw, IReferencePatcher<T>? patcher = null)
    : SavedShared(key, ComponentValues.Bind<T>().Id, typeof(T), typeof(TSerializer), SerializerTraits.Of<T, TSerializer>(serializer))
    where T : struct where TSerializer : struct, IValueSerializer<T>
{
    private readonly IReferencePatcher<T>? _patcher = patcher ?? ReferencePatchers.DefaultValue<T>();
    internal override object? EntityPatcher => _patcher;
    internal override void WriteSelectedValues(ref SaveWriter writer, SharedValues source, ReadOnlySpan<int> ids)
    {
        writer.WriteInt32(ids.Length);
        long start = writer.Position;
        var values = (SharedValues<T>)source;
        foreach (int id in ids) { TSerializer item = serializer; item.Write(ref writer, values.Values[id]); }
        if (!raw) writer.WriteInt32(checked((int)(writer.Position - start)));
    }
    internal override void PrepareRead(World world)
    {
        if (Type >= world.SharedTypes.Length)
        {
            SharedValues?[] types = world.SharedTypes;
            Array.Resize(ref types, Math.Max(Type + 1, Math.Max(16, types.Length * 2)));
            world.SharedTypes = types;
        }
        world.SharedTypes[Type] ??= new SharedValues<T>(world.Records.Length);
    }
    internal override void WriteCell(ref SaveWriter writer, World world, ref ColumnEntry column, int row)
    {
        var values = (SharedValues<T>)world.SharedTypes[Type]!;
        TSerializer item = serializer;
        item.Write(ref writer, values.Values[ColumnEntry.At<int>(ref column, row)]);
    }
    internal override void ReadCell(ref SaveReader reader, ref ColumnEntry column, int row)
    {
        TSerializer item = serializer;
        item.Read(ref reader, out T value);
        reader.Patch(ref value, _patcher);
        World world = reader.ValuesWorld!;
        var values = (SharedValues<T>)world.SharedTypes[Type]!;
        int before = values.Count, id = values.Intern(value);
        ColumnEntry.At<int>(ref column, row) = id;
        if (id != before && ComponentType<T>.HasValueOperationsType) ComponentType<T>.ReleaseValue(world, ref value);
    }
    internal override void WriteValues(ref SaveWriter writer, SharedValues source)
    {
        var values = (SharedValues<T>)source;
        writer.WriteInt32(values.Count);
        SavedValues<T, TSerializer>.Write(ref writer, values.Values.AsSpan(0, values.Count), serializer, raw);
    }
    internal override SharedValues ReadValues(ref SaveReader reader, World world)
    {
        int count = reader.Budget.TopologyEntryCount(reader.ReadInt32(), "shared value");
        var values = new SharedValues<T>(world.Records.Length);
        if (!reader.NormalizeShared) { values.Values = new T[Math.Max(16, count)]; values.Count = count; }
        else values.ReserveLoadedValues(count);
        if (Type >= world.SharedTypes.Length)
        {
            SharedValues?[] types = world.SharedTypes;
            Array.Resize(ref types, Math.Max(Type + 1, Math.Max(16, types.Length * 2)));
            world.SharedTypes = types;
        }
        world.SharedTypes[Type] = values;
        if (!reader.NormalizeShared)
        {
            SavedValues<T, TSerializer>.Read(ref reader, values.Values.AsSpan(0, count), serializer, raw, _patcher);
            values.RebuildLoadedValues();
            return values;
        }
        long start = reader.Position;
        int[]? mapping = null;
        for (int id = 0; id < count; id++)
        {
            T value = default;
            bool stored = false;
            try
            {
                if (raw)
                {
                    ref byte first = ref Unsafe.As<T, byte>(ref value);
                    reader.ReadRawBytes(MemoryMarshal.CreateSpan(ref first, Unsafe.SizeOf<T>()));
                }
                else { TSerializer item = serializer; item.Read(ref reader, out value); }
                reader.Patch(ref value, _patcher);
                int before = values.Count, target = values.Intern(value);
                stored = target == before;
                if (mapping is null && target != id)
                {
                    mapping = new int[count];
                    for (int previous = 0; previous < id; previous++) mapping[previous] = previous;
                    (reader.SharedMappings ??= new())[Type] = mapping;
                }
                if (mapping is not null) mapping[id] = target;
            }
            finally
            {
                if (!stored && ComponentType<T>.HasValueOperationsType) ComponentType<T>.ReleaseValue(world, ref value);
            }
        }
        if (!raw && reader.Position - start != reader.Budget.PayloadLength(reader.ReadInt32()))
            throw new InvalidDataException("Value serializer length differs from its segment footer.");
        return values;
    }
}

internal abstract class SavedSparse(SavedTypeKey key, int type, Type valueType, Type serializerType, SerializerTraits serializerTraits)
    : SavedType(key, SavedStorage.Sparse, type, 0, false, valueType, serializerType, serializerTraits)
{
    internal abstract void Write(ref SaveWriter writer, SparseStore values);
    internal abstract void Read(ref SaveReader reader, World world, bool preserve);
    internal abstract void WriteCell(ref SaveWriter writer, SparseStore values, Entity entity);
    internal abstract void WriteSelected(ref SaveWriter writer, SparseStore values, ReadOnlySpan<Entity> entities, ulong version);
    internal abstract void AllocateAppend(World world, ReadOnlySpan<Entity> entities, ReadOnlySpan<int> members);
    internal abstract void ReadCell(ref SaveReader reader, World world, Entity entity);
}

internal sealed class SavedSparse<T, TSerializer>(SavedTypeKey key, TSerializer serializer, bool raw, IReferencePatcher<T>? patcher = null)
    : SavedSparse(key, ComponentValues.Bind<T>().Id, typeof(T), typeof(TSerializer), SerializerTraits.Of<T, TSerializer>(serializer)), IStoredValueSerializer<T>
    where T : struct where TSerializer : struct, IValueSerializer<T>
{
    private readonly IReferencePatcher<T>? _patcher = patcher ?? ReferencePatchers.DefaultValue<T>();
    internal override object? EntityPatcher => _patcher;
    internal override void WriteSelected(ref SaveWriter writer, SparseStore source, ReadOnlySpan<Entity> entities, ulong version)
    {
        writer.WriteInt32(entities.Length);
        writer.WriteEntities(entities);
        long start = writer.Position;
        foreach (Entity entity in entities) WriteCell(ref writer, source, entity);
        if (!raw) writer.WriteInt32(checked((int)(writer.Position - start)));
        SavedChanges.WriteBaseline(ref writer, entities.Length, ComponentType<T>.TracksWritesType,
            ComponentType<T>.TracksAddedType, version);
    }
    internal override void WriteCell(ref SaveWriter writer, SparseStore values, Entity entity)
    {
        TSerializer item = serializer;
        item.Write(ref writer, ((SparseStore<T>)values).Get(entity));
    }
    internal override void AllocateAppend(World world, ReadOnlySpan<Entity> entities, ReadOnlySpan<int> members)
    {
        SparseStores stores = world.SparseTypes ??= new(world, world.Records.Length);
        SparseStore<T> values = stores.GetOrCreate<T>();
        int needed = values.Count + members.Length;
        if (values.Values.Length < needed)
        {
            Array.Resize(ref values.Values, needed);
            Array.Resize(ref values.Entities, needed);
            if (values.Changes is { } tracked) tracked[0].Grow(needed, ComponentType<T>.TracksAddedType);
        }
        foreach (int member in members)
        {
            Entity entity = entities[member];
            int row = values.Count++;
            values.Entities[row] = entity;
            values.EnsureCell(entity.Id) = new() { RowPlusOne = row + 1, AttachmentPlusOne = stores.Attach(entity.Id, Type) };
            if (values.Changes is { } tracked) tracked[0].Stamp(row, world.NextVersion(), true, ComponentType<T>.TracksAddedType);
        }
    }
    internal override void ReadCell(ref SaveReader reader, World world, Entity entity)
    {
        var values = (SparseStore<T>)world.SparseTypes!.Types[Type]!;
        ref T value = ref values.Get(entity);
        TSerializer item = serializer;
        item.Read(ref reader, out value);
        reader.Patch(ref value, _patcher);
    }
    public void WriteValue(ref SaveWriter writer, in T value)
    {
        TSerializer item = serializer;
        item.Write(ref writer, value);
    }
    public T ReadValue(ref SaveReader reader)
    {
        TSerializer item = serializer;
        item.Read(ref reader, out T value);
        return value;
    }
    internal override void Write(ref SaveWriter writer, SparseStore source)
    {
        var values = (SparseStore<T>)source;
        writer.WriteInt32(values.Count);
        writer.WriteEntities(values.Entities.AsSpan(0, values.Count));
        SavedValues<T, TSerializer>.Write(ref writer, values.Values.AsSpan(0, values.Count), serializer, raw);
        SavedChanges.Write(ref writer, values.Changes is { } changes ? changes[0] : default,
            values.Count, ComponentType<T>.TracksWritesType, ComponentType<T>.TracksAddedType);
    }
    internal override void Read(ref SaveReader reader, World world, bool preserve)
    {
        int count = reader.Budget.TopologyEntryCount(reader.ReadInt32(), "sparse value");
        SparseStores stores = world.SparseTypes ??= new(world, world.Records.Length);
        SparseStore<T> values = stores.GetOrCreate<T>();
        int capacity = Math.Max(16, count);
        if (values.Values.Length < capacity)
        { values.Values = new T[capacity]; values.Entities = new Entity[capacity]; }
        values.Count = count;
        for (int row = 0; row < count; row++)
        {
            Entity entity = values.Entities[row] = reader.MapEntity(reader.ReadEntity());
            values.EnsureCell(entity.Id) = new() { RowPlusOne = row + 1, AttachmentPlusOne = stores.Attach(entity.Id, Type) };
        }
        SavedValues<T, TSerializer>.Read(ref reader, values.Values.AsSpan(0, count), serializer, raw, _patcher);
        if (values.Changes is { } tracked)
        {
            if (tracked[0].Rows.Length < capacity) tracked[0].Grow(capacity, ComponentType<T>.TracksAddedType);
            SavedChanges.Read(ref reader, ref tracked[0], count, true, ComponentType<T>.TracksAddedType, preserve, world.Version);
        }
        else { ComponentChanges unused = default; SavedChanges.Read(ref reader, ref unused, count, false, false, preserve, world.Version); }
    }
}

internal abstract class SavedResource(SavedTypeKey key, int type, Type valueType, Type serializerType, SerializerTraits serializerTraits)
    : SavedType(key, SavedStorage.Resource, type, 0, false, valueType, serializerType, serializerTraits)
{
    internal abstract void Write(ref SaveWriter writer, ResourceValue value);
    internal abstract void Read(ref SaveReader reader, World world);
}

internal sealed class SavedResource<T, TSerializer>(SavedTypeKey key, TSerializer serializer, IReferencePatcher<T>? patcher = null)
    : SavedResource(key, ResourceType<T>.Id, typeof(T), typeof(TSerializer), SerializerTraits.Of<T, TSerializer>(serializer))
    where TSerializer : struct, IValueSerializer<T>
{
    // Resources may be classes, so their entity patcher is never inferred: register one when a resource holds entities.
    private readonly IReferencePatcher<T>? _patcher = patcher;
    internal override void Write(ref SaveWriter writer, ResourceValue value)
    {
        long start = writer.Position;
        TSerializer item = serializer;
        item.Write(ref writer, ((ResourceValue<T>)value).Value);
        writer.WriteInt32(checked((int)(writer.Position - start)));
    }
    internal override void Read(ref SaveReader reader, World world)
    {
        var value = new ResourceValue<T>(default!);
        world.ResourceValues.Set(Type, value);
        long start = reader.Position;
        TSerializer item = serializer;
        item.Read(ref reader, out value.Value);
        reader.Patch(ref value.Value, _patcher);
        if (reader.Position - start != reader.Budget.PayloadLength(reader.ReadInt32()))
            throw new InvalidDataException("Resource serializer length differs from its segment footer.");
    }
}

public sealed partial class SaveRegistry
{
    internal readonly Dictionary<int, SavedSparse> Sparse = [];
    internal readonly Dictionary<int, SavedResource> Resources = [];

    public SaveRegistry RegisterResource<T>(SavedTypeKey key) where T : struct
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) throw new ArgumentException("Register a resource value serializer for managed fields.");
        return RegisterResource<T, MemoryCopySerializer<T>>(key);
    }

    private SaveRegistry RegisterShared<T, TSerializer>(SavedTypeKey key, TSerializer serializer, bool raw,
        IReferencePatcher<T>? patcher)
        where T : struct where TSerializer : struct, IValueSerializer<T>
    {
        if (raw && RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            throw new ArgumentException("Register a shared value serializer for managed fields.");
        patcher ??= ReferencePatchers.DefaultValue<T>();
        _entries.Add(Add(new SavedShared<T, TSerializer>(key, serializer, raw, patcher)).Describe());
        if (ComponentType<T>.KeepsRemovedType && !Columns.ContainsKey(ComponentType<Removed<T>>.TypeId))
            Add(new SavedColumn<Removed<T>, RemovedSerializer<T, TSerializer>>(Derived(key, 32, "Removed"), SavedStorage.Removed,
                new(serializer), raw, ReferencePatchers.Removed(patcher)));
        return this;
    }
    private SaveRegistry RegisterSparse<T, TSerializer>(SavedTypeKey key, TSerializer serializer, bool raw,
        IReferencePatcher<T>? patcher)
        where T : struct where TSerializer : struct, IValueSerializer<T>
    {
        if (raw && RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            throw new ArgumentException("Register a sparse value serializer for managed fields.");
        patcher ??= ReferencePatchers.DefaultValue<T>();
        var binding = Add(new SavedSparse<T, TSerializer>(key, serializer, raw, patcher));
        _entries.Add(binding.Describe());
        Sparse.Add(binding.Type, binding);
        if (ComponentType<T>.KeepsRemovedType && !Columns.ContainsKey(ComponentType<Removed<T>>.TypeId))
            Add(new SavedColumn<Removed<T>, RemovedSerializer<T, TSerializer>>(Derived(key, 32, "Removed"), SavedStorage.Removed,
                new(serializer), raw, ReferencePatchers.Removed(patcher)));
        return this;
    }
    public SaveRegistry RegisterResource<T, TSerializer>(SavedTypeKey key, TSerializer serializer = default)
        where TSerializer : struct, IValueSerializer<T>
    {
        var binding = Add(new SavedResource<T, TSerializer>(key, serializer));
        _entries.Add(binding.Describe());
        Resources.Add(binding.Type, binding);
        return this;
    }

    public SaveRegistry RegisterResource<T, TSerializer, TPatcher>(SavedTypeKey key, TSerializer serializer, TPatcher patcher)
        where TSerializer : struct, IValueSerializer<T> where TPatcher : struct, IReferencePatcher<T>
    {
        var binding = Add(new SavedResource<T, TSerializer>(key, serializer, ReferencePatchers.Bind<T, TPatcher>(patcher)));
        _entries.Add(binding.Describe());
        Resources.Add(binding.Type, binding);
        return this;
    }
}
