using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Shared;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Serialization;

internal static class SavedValues<T, TCodec> where TCodec : struct, IValueCodec<T>
{
    internal static void Write(ref DataWriter writer, ReadOnlySpan<T> values, TCodec codec, bool raw)
    {
        if (raw)
        {
            ref byte first = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(values));
            writer.WriteRawBytes(MemoryMarshal.CreateReadOnlySpan(ref first, values.Length * Unsafe.SizeOf<T>()));
            return;
        }
        long start = writer.Position;
        foreach (ref readonly T value in values) { TCodec item = codec; item.Write(ref writer, value); }
        writer.WriteInt32(checked((int)(writer.Position - start)));
    }

    internal static void Read(ref DataReader reader, Span<T> values, TCodec codec, bool raw, IReferencePatcher<T>? patcher = null)
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
            foreach (ref T value in values) { TCodec item = codec; item.Read(ref reader, out value); }
        else
            foreach (ref T value in values)
            { TCodec item = codec; item.Read(ref reader, out value); reader.Patch(ref value, patcher); }
        long consumed = reader.Position - start;
        if (consumed != reader.Budget.PayloadLength(reader.ReadInt32()))
            throw new InvalidDataException("Value codec length differs from its segment footer.");
    }
}

internal abstract class SavedShared(SerializationTypeKey key, int type, Type valueType, Type codecType)
    : SavedColumn(key, SavedStorage.Shared, type, sizeof(int), false, valueType, codecType)
{
    internal override void Write(ref DataWriter writer, ref ColumnEntry column, int count)
    {
        Span<int> values = ColumnEntry.Span<int>(ref column, count);
        if (column.Presence is null) writer.WriteRawBytes(MemoryMarshal.AsBytes(values));
        else for (int row = 0; row < count; row++) if (column.Has(row)) writer.WriteInt32(values[row]);
    }
    internal override void Read(ref DataReader reader, ref ColumnEntry column, int count)
    {
        Span<int> values = ColumnEntry.Span<int>(ref column, count);
        if (column.Presence is null) reader.ReadRawBytes(MemoryMarshal.AsBytes(values));
        else for (int row = 0; row < count; row++) if (column.Has(row)) values[row] = reader.ReadInt32();
        if (reader.SharedMappings is { } mappings && mappings.TryGetValue(Type, out int[]? ids))
            for (int row = 0; row < count; row++) if (column.Has(row)) values[row] = ids[values[row]];
    }
    internal abstract void WriteValues(ref DataWriter writer, SharedValues values);
    internal abstract void WriteSelectedValues(ref DataWriter writer, SharedValues values, ReadOnlySpan<int> ids);
    internal abstract SharedValues ReadValues(ref DataReader reader, World world);
}

internal sealed class SavedShared<T, TCodec>(SerializationTypeKey key, TCodec codec, bool raw, IReferencePatcher<T>? patcher = null)
    : SavedShared(key, ComponentValues.Bind<T>().Id, typeof(T), typeof(TCodec))
    where T : struct where TCodec : struct, IValueCodec<T>
{
    private readonly IReferencePatcher<T>? _patcher = patcher ?? ReferencePatchers.DefaultValue<T>();
    internal override object? EntityPatcher => _patcher;
    internal override void WriteSelectedValues(ref DataWriter writer, SharedValues source, ReadOnlySpan<int> ids)
    {
        writer.WriteInt32(ids.Length);
        long start = writer.Position;
        var values = (SharedValues<T>)source;
        foreach (int id in ids) { TCodec item = codec; item.Write(ref writer, values.Values[id]); }
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
    internal override void WriteCell(ref DataWriter writer, World world, ref ColumnEntry column, int row)
    {
        var values = (SharedValues<T>)world.SharedTypes[Type]!;
        TCodec item = codec;
        item.Write(ref writer, values.Values[ColumnEntry.At<int>(ref column, row)]);
    }
    internal override void ReadCell(ref DataReader reader, ref ColumnEntry column, int row)
    {
        TCodec item = codec;
        item.Read(ref reader, out T value);
        reader.Patch(ref value, _patcher);
        World world = reader.ValuesWorld!;
        var values = (SharedValues<T>)world.SharedTypes[Type]!;
        int before = values.Count, id = values.Intern(value);
        ColumnEntry.At<int>(ref column, row) = id;
        if (id != before && ComponentType<T>.HasValueOperationsType) ComponentType<T>.ReleaseValue(world, ref value);
    }
    internal override void WriteValues(ref DataWriter writer, SharedValues source)
    {
        var values = (SharedValues<T>)source;
        writer.WriteInt32(values.Count);
        SavedValues<T, TCodec>.Write(ref writer, values.Values.AsSpan(0, values.Count), codec, raw);
    }
    internal override SharedValues ReadValues(ref DataReader reader, World world)
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
            SavedValues<T, TCodec>.Read(ref reader, values.Values.AsSpan(0, count), codec, raw, _patcher);
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
                else { TCodec item = codec; item.Read(ref reader, out value); }
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
            throw new InvalidDataException("Value codec length differs from its segment footer.");
        return values;
    }
}

internal abstract class SavedSparse(SerializationTypeKey key, int type, Type valueType, Type codecType)
    : SavedType(key, SavedStorage.Sparse, type, 0, false, valueType, codecType)
{
    internal abstract void Write(ref DataWriter writer, SparseStore values);
    internal abstract void Read(ref DataReader reader, World world, bool preserve);
    internal abstract void WriteCell(ref DataWriter writer, SparseStore values, Entity entity);
    internal abstract void WriteSelected(ref DataWriter writer, SparseStore values, ReadOnlySpan<Entity> entities, ulong version);
    internal abstract void AllocateAppend(World world, ReadOnlySpan<Entity> entities, ReadOnlySpan<int> members);
    internal abstract void ReadCell(ref DataReader reader, World world, Entity entity);
}

internal sealed class SavedSparse<T, TCodec>(SerializationTypeKey key, TCodec codec, bool raw, IReferencePatcher<T>? patcher = null)
    : SavedSparse(key, ComponentValues.Bind<T>().Id, typeof(T), typeof(TCodec)), IStoredValueCodec<T>
    where T : struct where TCodec : struct, IValueCodec<T>
{
    private readonly IReferencePatcher<T>? _patcher = patcher ?? ReferencePatchers.DefaultValue<T>();
    internal override object? EntityPatcher => _patcher;
    internal override void WriteSelected(ref DataWriter writer, SparseStore source, ReadOnlySpan<Entity> entities, ulong version)
    {
        writer.WriteInt32(entities.Length);
        writer.WriteEntities(entities);
        long start = writer.Position;
        foreach (Entity entity in entities) WriteCell(ref writer, source, entity);
        if (!raw) writer.WriteInt32(checked((int)(writer.Position - start)));
        SavedChanges.WriteBaseline(ref writer, entities.Length, ComponentType<T>.TracksWritesType,
            ComponentType<T>.TracksAddedType, version);
    }
    internal override void WriteCell(ref DataWriter writer, SparseStore values, Entity entity)
    {
        TCodec item = codec;
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
    internal override void ReadCell(ref DataReader reader, World world, Entity entity)
    {
        var values = (SparseStore<T>)world.SparseTypes!.Types[Type]!;
        ref T value = ref values.Get(entity);
        TCodec item = codec;
        item.Read(ref reader, out value);
        reader.Patch(ref value, _patcher);
    }
    public void WriteValue(ref DataWriter writer, in T value)
    {
        TCodec item = codec;
        item.Write(ref writer, value);
    }
    public T ReadValue(ref DataReader reader)
    {
        TCodec item = codec;
        item.Read(ref reader, out T value);
        return value;
    }
    internal override void Write(ref DataWriter writer, SparseStore source)
    {
        var values = (SparseStore<T>)source;
        writer.WriteInt32(values.Count);
        writer.WriteEntities(values.Entities.AsSpan(0, values.Count));
        SavedValues<T, TCodec>.Write(ref writer, values.Values.AsSpan(0, values.Count), codec, raw);
        SavedChanges.Write(ref writer, values.Changes is { } changes ? changes[0] : default,
            values.Count, ComponentType<T>.TracksWritesType, ComponentType<T>.TracksAddedType);
    }
    internal override void Read(ref DataReader reader, World world, bool preserve)
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
        SavedValues<T, TCodec>.Read(ref reader, values.Values.AsSpan(0, count), codec, raw, _patcher);
        if (values.Changes is { } tracked)
        {
            if (tracked[0].Rows.Length < capacity) tracked[0].Grow(capacity, ComponentType<T>.TracksAddedType);
            SavedChanges.Read(ref reader, ref tracked[0], count, true, ComponentType<T>.TracksAddedType, preserve, world.Version);
        }
        else { ComponentChanges unused = default; SavedChanges.Read(ref reader, ref unused, count, false, false, preserve, world.Version); }
    }
}

internal abstract class SavedResource(SerializationTypeKey key, int type, Type valueType, Type codecType)
    : SavedType(key, SavedStorage.Resource, type, 0, false, valueType, codecType)
{
    internal abstract void Write(ref DataWriter writer, ResourceValue value);
    internal abstract void Read(ref DataReader reader, World world);
}

internal sealed class SavedResource<T, TCodec>(SerializationTypeKey key, TCodec codec, IReferencePatcher<T>? patcher = null)
    : SavedResource(key, ResourceType<T>.Id, typeof(T), typeof(TCodec)) where TCodec : struct, IValueCodec<T>
{
    private readonly IReferencePatcher<T>? _patcher = patcher ?? ReferencePatchers.Default<T>();
    internal override void Write(ref DataWriter writer, ResourceValue value)
    {
        long start = writer.Position;
        TCodec item = codec;
        item.Write(ref writer, ((ResourceValue<T>)value).Value);
        writer.WriteInt32(checked((int)(writer.Position - start)));
    }
    internal override void Read(ref DataReader reader, World world)
    {
        var value = new ResourceValue<T>(default!);
        world.ResourceValues.Set(Type, value);
        long start = reader.Position;
        TCodec item = codec;
        item.Read(ref reader, out value.Value);
        reader.Patch(ref value.Value, _patcher);
        if (reader.Position - start != reader.Budget.PayloadLength(reader.ReadInt32()))
            throw new InvalidDataException("Resource codec length differs from its segment footer.");
    }
}

public sealed partial class SerializationRegistry
{
    internal readonly Dictionary<int, SavedSparse> Sparse = [];
    internal readonly Dictionary<int, SavedResource> Resources = [];

    public SerializationRegistry RegisterResource<T>(SerializationTypeKey key) where T : struct
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) throw new ArgumentException("Register a resource value codec for managed fields.");
        return RegisterResource<T, RawCodec<T>>(key);
    }

    public SerializationRegistry RegisterShared<T>(SerializationTypeKey key) where T : struct
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) throw new ArgumentException("Register a shared value codec for managed fields.");
        return RegisterShared<T, RawCodec<T>>(key, default, true);
    }
    public SerializationRegistry RegisterShared<T, TCodec>(SerializationTypeKey key, TCodec codec = default)
        where T : struct where TCodec : struct, IComponentCodec<T> => RegisterShared<T, TCodec>(key, codec, false);
    private SerializationRegistry RegisterShared<T, TCodec>(SerializationTypeKey key, TCodec codec, bool raw,
        IReferencePatcher<T>? patcher = null)
        where T : struct where TCodec : struct, IComponentCodec<T>
    {
        patcher ??= ReferencePatchers.DefaultValue<T>();
        _entries.Add(Add(new SavedShared<T, TCodec>(key, codec, raw, patcher)).Describe());
        if (ComponentType<T>.KeepsRemovedType && !Columns.ContainsKey(ComponentType<Removed<T>>.TypeId))
            Add(new SavedColumn<Removed<T>, RemovedCodec<T, TCodec>>(Derived(key, 32, "Removed"), SavedStorage.Removed,
                new(codec), raw, ReferencePatchers.Removed(patcher)));
        return this;
    }
    public SerializationRegistry RegisterSparse<T>(SerializationTypeKey key) where T : struct
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) throw new ArgumentException("Register a sparse value codec for managed fields.");
        return RegisterSparse<T, RawCodec<T>>(key, default, true);
    }
    public SerializationRegistry RegisterSparse<T, TCodec>(SerializationTypeKey key, TCodec codec = default)
        where T : struct where TCodec : struct, IComponentCodec<T> => RegisterSparse<T, TCodec>(key, codec, false);
    private SerializationRegistry RegisterSparse<T, TCodec>(SerializationTypeKey key, TCodec codec, bool raw,
        IReferencePatcher<T>? patcher = null)
        where T : struct where TCodec : struct, IComponentCodec<T>
    {
        patcher ??= ReferencePatchers.DefaultValue<T>();
        var binding = Add(new SavedSparse<T, TCodec>(key, codec, raw, patcher));
        _entries.Add(binding.Describe());
        Sparse.Add(binding.Type, binding);
        if (ComponentType<T>.KeepsRemovedType && !Columns.ContainsKey(ComponentType<Removed<T>>.TypeId))
            Add(new SavedColumn<Removed<T>, RemovedCodec<T, TCodec>>(Derived(key, 32, "Removed"), SavedStorage.Removed,
                new(codec), raw, ReferencePatchers.Removed(patcher)));
        return this;
    }
    public SerializationRegistry RegisterResource<T, TCodec>(SerializationTypeKey key, TCodec codec = default)
        where TCodec : struct, IValueCodec<T>
    {
        var binding = Add(new SavedResource<T, TCodec>(key, codec));
        _entries.Add(binding.Describe());
        Resources.Add(binding.Type, binding);
        return this;
    }

    public SerializationRegistry RegisterShared<T, TCodec, TPatcher>(SerializationTypeKey key, TCodec codec, TPatcher patcher)
        where T : struct where TCodec : struct, IComponentCodec<T> where TPatcher : struct, IReferencePatcher<T>
        => RegisterShared<T, TCodec>(key, codec, false, ReferencePatchers.Bind<T, TPatcher>(patcher));

    public SerializationRegistry RegisterSparse<T, TCodec, TPatcher>(SerializationTypeKey key, TCodec codec, TPatcher patcher)
        where T : struct where TCodec : struct, IComponentCodec<T> where TPatcher : struct, IReferencePatcher<T>
        => RegisterSparse<T, TCodec>(key, codec, false, ReferencePatchers.Bind<T, TPatcher>(patcher));

    public SerializationRegistry RegisterResource<T, TCodec, TPatcher>(SerializationTypeKey key, TCodec codec, TPatcher patcher)
        where TCodec : struct, IValueCodec<T> where TPatcher : struct, IReferencePatcher<T>
    {
        var binding = Add(new SavedResource<T, TCodec>(key, codec, ReferencePatchers.Bind<T, TPatcher>(patcher)));
        _entries.Add(binding.Describe());
        Resources.Add(binding.Type, binding);
        return this;
    }
}
