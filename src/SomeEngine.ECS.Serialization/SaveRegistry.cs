using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SomeEngine.ECS.Storage;
using SomeEngine.ECS.Entities;
using SomeEngine.Serialization;

namespace SomeEngine.ECS.Serialization;

internal enum SavedStorage : byte { Table, Tag, Buffer, Shared, Sparse, Resource, Parent, Endpoints, Children, Removed }

internal interface IStoredValueSerializer<T> where T : struct
{
    void WriteValue(ref SaveWriter writer, in T value);
    T ReadValue(ref SaveReader reader);
}

internal interface ISelectionSerializer<T> where T : struct
{
    bool Includes(in T value, IReadOnlySet<Entity> entities);
    void Write(ref SaveWriter writer, in T value, IReadOnlySet<Entity> entities);
}

internal abstract class SavedType(SavedTypeKey key, SavedStorage storage, int type, int width, bool raw,
    Type valueType, Type serializerType, SerializerTraits serializerTraits)
{
    internal readonly SavedTypeKey Key = key;
    internal readonly SavedStorage Storage = storage;
    internal readonly int Type = type, Width = width;
    internal readonly bool Raw = raw;
    internal readonly ValueEncoding ValueEncoding = serializerTraits.Kind;
    /// <summary>The fields its generated serializer writes, or null for a hand-written serializer.</summary>
    internal readonly SavedFieldList? Schema = serializerTraits.FieldList;
    internal virtual object? EntityPatcher => null;
    internal bool SupportsDurable = storage == SavedStorage.Tag || !serializerTraits.CopiesMemory;
    internal readonly SavedTypeKey CheckpointKey = key with
    {
        Name = string.Join('|', key.Name, TypeBuild(valueType), TypeBuild(serializerType), RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture, IntPtr.Size, BitConverter.IsLittleEndian),
        FieldHash = serializerTraits.CopiesMemory ? RawSchema(key, valueType, width) : key.FieldHash,
    };
    internal SavedTypeKey KeyFor(SavePurpose format) => format == SavePurpose.RawCheckpoint ? CheckpointKey : Key;
    /// <summary>Groups the World by this type when the type is a group component, before its saved tables are restored.</summary>
    internal void Activate(World world)
    {
        if (Storage != SavedStorage.Resource) ComponentTypes.Get(Type).GroupActivation?.GroupBy(world);
    }
    private static string TypeBuild(Type type) => type.AssemblyQualifiedName + "#" + type.Module.ModuleVersionId
        + (type.IsGenericType ? "[" + string.Join(';', type.GetGenericArguments().Select(TypeBuild)) + "]" : "");
    private static ulong RawSchema(SavedTypeKey key, Type valueType, int width)
    {
        ulong schema = key.FieldHash;
        string shape = string.Join('|', TypeBuild(valueType), width, IntPtr.Size, BitConverter.IsLittleEndian);
        foreach (byte value in Encoding.UTF8.GetBytes(shape)) schema = (schema ^ value) * 1099511628211ul;
        return schema == 0 ? 1 : schema;
    }
}

internal abstract class SavedColumn(SavedTypeKey key, SavedStorage storage, int type, int width, bool raw,
    Type valueType, Type serializerType, SerializerTraits serializerTraits) : SavedType(key, storage, type, width, raw, valueType, serializerType, serializerTraits)
{
    internal abstract void Write(ref SaveWriter writer, ref ColumnEntry column, int count);
    internal abstract void Read(ref SaveReader reader, ref ColumnEntry column, int count);
    internal virtual void PrepareRead(World world) { }
    internal abstract void WriteCell(ref SaveWriter writer, World world, ref ColumnEntry column, int row);
    internal virtual bool IncludesCell(ref ColumnEntry column, int row, IReadOnlySet<Entity> entities) => column.Has(row);
    internal virtual void WriteSelectedCell(ref SaveWriter writer, World world, ref ColumnEntry column, int row,
        IReadOnlySet<Entity> entities) => WriteCell(ref writer, world, ref column, row);
    internal virtual void WriteEntityRows(ref SaveWriter writer, ref ColumnEntry column, ReadOnlySpan<Entity> entities) => throw new InvalidOperationException();
    internal virtual void ReadCell(ref SaveReader reader, ref ColumnEntry column, int row) => throw new InvalidOperationException();
}

internal sealed unsafe class SavedColumn<T, TSerializer>(SavedTypeKey key, SavedStorage storage, TSerializer serializer, bool raw,
    IReferencePatcher<T>? patcher = null, bool progress = false)
    : SavedColumn(key, storage, ComponentValues.Bind<T>().Id, raw && !ComponentType<T>.IsTagType ? Unsafe.SizeOf<T>() : 0,
        raw, typeof(T), typeof(TSerializer), SerializerTraits.Of<T, TSerializer>(serializer)), IStoredValueSerializer<T> where T : struct where TSerializer : struct, IValueSerializer<T>
{
    private readonly IReferencePatcher<T>? _patcher = patcher ?? ReferencePatchers.DefaultValue<T>();
    private readonly ISelectionSerializer<T>? _selection = serializer is ISelectionSerializer<T> selected ? selected : null;
    internal override object? EntityPatcher => _patcher;
    internal override bool IncludesCell(ref ColumnEntry column, int row, IReadOnlySet<Entity> entities)
        => column.Has(row) && (_selection is null || _selection.Includes(ColumnEntry.At<T>(ref column, row), entities));
    internal override void WriteSelectedCell(ref SaveWriter writer, World world, ref ColumnEntry column, int row,
        IReadOnlySet<Entity> entities)
    {
        if (_selection is { } selection) selection.Write(ref writer, ColumnEntry.At<T>(ref column, row), entities);
        else WriteCell(ref writer, world, ref column, row);
    }
    internal override void PrepareRead(World world)
    {
        if (serializer is IValueReadStorage storage) storage.PrepareRead(world);
    }
    internal override void WriteCell(ref SaveWriter writer, World world, ref ColumnEntry column, int row)
    {
        if (ComponentType<T>.IsTagType) return;
        TSerializer item = serializer;
        item.Write(ref writer, ColumnEntry.At<T>(ref column, row));
    }
    public void WriteValue(ref SaveWriter writer, in T value)
    {
        if (ComponentType<T>.IsTagType) return;
        TSerializer item = serializer;
        item.Write(ref writer, value);
    }

    public T ReadValue(ref SaveReader reader)
    {
        if (ComponentType<T>.IsTagType) return default;
        TSerializer item = serializer;
        item.Read(ref reader, out T value);
        return value;
    }
    internal override void WriteEntityRows(ref SaveWriter writer, ref ColumnEntry column, ReadOnlySpan<Entity> entities)
    {
        Span<T> values = ColumnEntry.Span<T>(ref column, entities.Length);
        if (column.Presence is null)
        {
            for (int row = 0; row < entities.Length; row++)
            { writer.WriteEntity(entities[row]); TSerializer item = serializer; item.Write(ref writer, values[row]); }
        }
        else
            for (int row = 0; row < entities.Length; row++)
                if (column.Has(row))
                { writer.WriteEntity(entities[row]); TSerializer item = serializer; item.Write(ref writer, values[row]); }
    }
    internal override void ReadCell(ref SaveReader reader, ref ColumnEntry column, int row)
    {
        if (ComponentType<T>.IsTagType) return;
        TSerializer item = serializer;
        ref T value = ref ColumnEntry.At<T>(ref column, row);
        item.Read(ref reader, out value);
        if (reader.PatchesValues) reader.Patch(ref value, _patcher, progress);
    }
    internal override void Write(ref SaveWriter writer, ref ColumnEntry column, int count)
    {
        if (ComponentType<T>.IsTagType) return;
        if (Raw)
        {
            if (column.Presence is null) writer.WriteRawBytes(new ReadOnlySpan<byte>((void*)column.Rows, count * Width));
            else for (int row = 0; row < count; row++)
                if (column.Has(row)) writer.WriteRawBytes(new ReadOnlySpan<byte>((void*)(column.Rows + (nint)row * Width), Width));
        }
        else
        {
            long start = writer.Position;
            Span<T> values = ColumnEntry.Span<T>(ref column, count);
            if (column.Presence is null)
            {
                foreach (ref readonly T value in values) { TSerializer itemSerializer = serializer; itemSerializer.Write(ref writer, value); }
            }
            else
                for (int row = 0; row < count; row++)
                    if (column.Has(row)) { TSerializer itemSerializer = serializer; itemSerializer.Write(ref writer, values[row]); }
            writer.WriteInt32(checked((int)(writer.Position - start)));
        }
    }

    internal override void Read(ref SaveReader reader, ref ColumnEntry column, int count)
    {
        if (ComponentType<T>.IsTagType) return;
        if (Raw)
        {
            if (column.Presence is null) reader.ReadRawBytes(new Span<byte>((void*)column.Rows, count * Width));
            else for (int row = 0; row < count; row++)
                if (column.Has(row)) reader.ReadRawBytes(new Span<byte>((void*)(column.Rows + (nint)row * Width), Width));
        }
        else
        {
            long start = reader.Position;
            Span<T> values = ColumnEntry.Span<T>(ref column, count);
            for (int row = 0; row < count; row++)
                if (column.Has(row)) { TSerializer itemSerializer = serializer; itemSerializer.Read(ref reader, out values[row]); }
            long consumed = reader.Position - start;
            int length = reader.ReadInt32();
            if (consumed != length) throw new InvalidDataException("Component serializer length differs from its column footer.");
            reader.Budget.PayloadLength(length);
        }
        if (_patcher is not null && reader.PatchesValues)
            for (int row = 0; row < count; row++)
                if (column.Has(row)) reader.Patch(ref ColumnEntry.At<T>(ref column, row), _patcher, progress);
    }
}

internal readonly struct MemoryCopySerializer<T> : IValueSerializer<T>, IEngineSerializer where T : struct
{
    public bool CopiesMemory => true;
    public SavedFieldList? FieldList => null;
    public void Write(ref SaveWriter writer, in T value)
        => writer.WriteRawBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in value), 1)));
    public void Read(ref SaveReader reader, out T value)
    {
        value = default;
        reader.ReadRawBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1)));
    }
}

/// <summary>Copies a value whose memory layout matches its generated serializer's encoding.</summary>
/// <remarks>A save whose fields differ from the current type is read by the generated serializer instead.</remarks>
internal readonly struct PackedValueSerializer<T, TSerializer> : IValueSerializer<T>, IEngineSerializer
    where T : struct where TSerializer : struct, IValueSerializer<T>
{
    public bool CopiesMemory => false;
    public bool IsPacked => true;
    public SavedFieldList? FieldList => SerializerTraits.FieldListOf(default(TSerializer));
    public void Write(ref SaveWriter writer, in T value) => new MemoryCopySerializer<T>().Write(ref writer, value);
    public void Read(ref SaveReader reader, out T value)
    {
        if (reader.HasFieldMappings) default(TSerializer).Read(ref reader, out value);
        else new MemoryCopySerializer<T>().Read(ref reader, out value);
    }
    public bool SupportsParallelWrite => true;
}

/// <summary>An engine serializer that reports how it stores values, including through the serializer it wraps.</summary>
internal interface IEngineSerializer
{
    /// <summary>Copies values as raw memory, which only the same build can read back.</summary>
    bool CopiesMemory { get; }
    /// <summary>Copies a memory layout that its generated serializer has proven matches that serializer's encoding.</summary>
    bool IsPacked => false;
    SavedFieldList? FieldList { get; }
}

/// <summary>What a binding needs to know about its serializer, read from the serializer value instead of by reflection.</summary>
internal readonly record struct SerializerTraits(ValueEncoding Kind, bool CopiesMemory, SavedFieldList? FieldList)
{
    internal static SerializerTraits Of<T, TSerializer>(TSerializer serializer) where TSerializer : struct, IValueSerializer<T>
    {
        bool copies = CopiesMemoryOf(serializer);
        ValueEncoding kind = serializer is IEngineSerializer { IsPacked: true } ? ValueEncoding.PackedMemoryCopy
            : copies ? ValueEncoding.MemoryCopy
            : ValueEncoding.Serializer;
        return new(kind, copies, FieldListOf(serializer));
    }

    internal static bool CopiesMemoryOf<TSerializer>(TSerializer serializer) => serializer is IEngineSerializer { CopiesMemory: true };

    internal static SavedFieldList? FieldListOf<TSerializer>(TSerializer serializer)
        => serializer is IFieldSerializer generated ? generated.FieldList : (serializer as IEngineSerializer)?.FieldList;
}

internal interface IValueReadStorage
{
    void PrepareRead(World world);
}

internal readonly struct RemovedSerializer<T, TSerializer>(TSerializer serializer) : IValueSerializer<Removed<T>>, IValueReadStorage, IEngineSerializer
    where T : struct where TSerializer : struct, IValueSerializer<T>
{
    public bool CopiesMemory => SerializerTraits.CopiesMemoryOf(serializer);
    public SavedFieldList? FieldList => SerializerTraits.FieldListOf(serializer);
    public void PrepareRead(World world)
    {
        if (serializer is IValueReadStorage storage) storage.PrepareRead(world);
    }
    public void Write(ref SaveWriter writer, in Removed<T> value)
    { TSerializer item = serializer; item.Write(ref writer, value.Value); writer.WriteUInt64(value.Version); }
    public void Read(ref SaveReader reader, out Removed<T> value)
    { TSerializer item = serializer; item.Read(ref reader, out T previous); value = new(previous, reader.ReadUInt64()); }
}

internal readonly struct BufferSerializer<T, TSerializer>(TSerializer serializer) : IValueSerializer<Buffer<T>>, IValueReadStorage, IEngineSerializer
    where T : struct where TSerializer : struct, IValueSerializer<T>
{
    public bool CopiesMemory => SerializerTraits.CopiesMemoryOf(serializer);
    public SavedFieldList? FieldList => SerializerTraits.FieldListOf(serializer);
    public void PrepareRead(World world) => world.BufferPool<T>();
    public void Write(ref SaveWriter writer, in Buffer<T> value)
    {
        int count = value.Count;
        writer.WriteInt32(count);
        for (int i = 0; i < count; i++) { TSerializer item = serializer; item.Write(ref writer, value.Items![i]); }
    }
    public void Read(ref SaveReader reader, out Buffer<T> value)
    {
        int count = reader.Budget.BufferElementCount<T>(reader.ReadInt32());
        value = reader.ValuesWorld is { } world ? world.BufferPool<T>().Rent() : new() { Items = [] };
        value.Reserve(count);
        for (int i = 0; i < count; i++) { TSerializer item = serializer; item.Read(ref reader, out value.Items![i]); value.Count = i + 1; }
    }
}

public sealed partial class SaveRegistry
{
    public SaveRegistry()
        => RegisterColumn<Destroyed, MemoryCopySerializer<Destroyed>>(
            new(new Guid("d2a68e47-3bef-4b89-a213-d8151bf19872"), "SomeEngine.ECS.Destroyed", 1),
            default, true, SavedStorage.Tag, describe: false);
    private readonly List<SerializationTypeEntry> _entries = [];
    public ReadOnlySpan<SerializationTypeEntry> Entries => CollectionsMarshal.AsSpan(_entries);
    internal readonly List<SavedType> Types = [];
    internal readonly Dictionary<int, SavedColumn> Columns = [];
    internal readonly Dictionary<Guid, SavedType> ById = [];
    private readonly Dictionary<(SavedStorage Storage, int Type), int> _orders = [];
    internal int GetOrder(SavedType type) => _orders[(type.Storage, type.Type)];
    /// <summary>Types that older saves contain but the game no longer has; loading skips their data.</summary>
    internal readonly HashSet<Guid> RetiredTypes = [];

    /// <summary>
    /// Declares a type that older saves contain but the game no longer has, so loading skips its data instead of
    /// failing. A saved type that is neither registered nor retired still fails the load, which catches a
    /// forgotten registration before the next save would drop that type's data for good.
    /// </summary>
    public SaveRegistry SkipSavedType(Guid id)
    {
        if (ById.ContainsKey(id)) throw new ArgumentException("A registered type cannot also be retired.", nameof(id));
        RetiredTypes.Add(id);
        // Its enable flags, children, removed values and relation endpoints are saved under derived ids.
        foreach (byte role in (ReadOnlySpan<byte>)[8, 16, 32, 64])
            RetiredTypes.Add(Derived(new SavedTypeKey(id, string.Empty, 1), role, string.Empty).Id);
        return this;
    }
    internal int Version;

    private TBinding Add<TBinding>(TBinding type) where TBinding : SavedType
    {
        if (type.Key.Id == Guid.Empty || type.Key.FieldHash == 0)
            throw new ArgumentException("Serialization types require a stable identity and a nonzero schema.");
        _orders.Add((type.Storage, type.Type), Types.Count);
        Types.Add(type);
        ById.Add(type.Key.Id, type);
        if (type is SavedColumn column) Columns.Add(type.Type, column);
        Version++;
        return type;
    }
    internal static SavedTypeKey Derived(SavedTypeKey key, byte role, string name)
    {
        Span<byte> bytes = stackalloc byte[16];
        key.Id.TryWriteBytes(bytes);
        bytes[15] ^= role;
        return new(new Guid(bytes), key.Name + "." + name, key.FieldHash);
    }
    internal static SavedTypeKey RawKey<T>()
    {
        Span<byte> bytes = stackalloc byte[16];
        typeof(T).Module.ModuleVersionId.TryWriteBytes(bytes);
        ulong schema = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return new(typeof(T).GUID, typeof(T).FullName!, schema == 0 ? 1 : schema);
    }

    public SaveRegistry Register<T>() where T : struct => Register<T>(RawKey<T>());
    public SaveRegistry Register<T>(SavedTypeKey key) where T : struct
        => RegisterComponent<T, MemoryCopySerializer<T>>(key, default, true, null);
    public SaveRegistry Register<T, TSerializer>(SavedTypeKey key, TSerializer serializer = default)
        where T : struct where TSerializer : struct, IValueSerializer<T>
        => RegisterComponent<T, TSerializer>(key, serializer, false, null);
    public SaveRegistry Register<T, TSerializer, TPatcher>(SavedTypeKey key, TSerializer serializer, TPatcher patcher)
        where T : struct where TSerializer : struct, IValueSerializer<T> where TPatcher : struct, IReferencePatcher<T>
        => RegisterComponent<T, TSerializer>(key, serializer, false, ReferencePatchers.Bind<T, TPatcher>(patcher));

    /// <summary>The component type decides where its values are saved: shared values, sparse values or table columns.</summary>
    private SaveRegistry RegisterComponent<T, TSerializer>(SavedTypeKey key, TSerializer serializer, bool raw,
        IReferencePatcher<T>? patcher)
        where T : struct where TSerializer : struct, IValueSerializer<T>
        => ComponentType<T>.IsSharedType ? RegisterShared(key, serializer, raw, patcher)
            : ComponentType<T>.IsSparseType ? RegisterSparse(key, serializer, raw, patcher)
            : RegisterColumn(key, serializer, raw, ComponentType<T>.IsTagType ? SavedStorage.Tag : SavedStorage.Table, patcher);

    public SaveRegistry RegisterDurable<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicFields)] T, TSerializer>(
        SavedTypeKey key, int encodedSize = -1, ulong layoutProof = 0)
        where T : struct where TSerializer : struct, IValueSerializer<T>
        => PackedValueLayout.IsVerified<T>(encodedSize, layoutProof)
            ? Register<T, PackedValueSerializer<T, TSerializer>>(key)
            : Register<T, TSerializer>(key);

    /// <summary>Registers a serializer under the type's build identity; its values stay out of durable saves.</summary>
    public SaveRegistry RegisterSerializer<T, TSerializer>(TSerializer serializer)
        where T : struct where TSerializer : struct, IValueSerializer<T>
    {
        SavedTypeKey key = RawKey<T>();
        Register<T, TSerializer>(key, serializer);
        ById[key.Id].SupportsDurable = false;
        return this;
    }

    private SaveRegistry RegisterColumn<T, TSerializer>(SavedTypeKey key, TSerializer serializer, bool raw, SavedStorage kind,
        IReferencePatcher<T>? patcher = null, bool progress = false, bool describe = true)
        where T : struct where TSerializer : struct, IValueSerializer<T>
    {
        ComponentValues.Bind<T>();
        if ((kind is SavedStorage.Table or SavedStorage.Tag) && typeof(IControlledComponent).IsAssignableFrom(typeof(T)))
            throw new InvalidOperationException("Controlled topology values require their hierarchy or relation registration.");
        if (raw && RuntimeHelpers.IsReferenceOrContainsReferences<T>() && kind != SavedStorage.Tag)
            throw new ArgumentException("Register a value serializer for managed component fields.");
        patcher ??= ReferencePatchers.DefaultValue<T>();
        var binding = Add(new SavedColumn<T, TSerializer>(key, kind, serializer, raw, patcher, progress));
        if (describe) _entries.Add(binding.Describe());
        if (ComponentType<T>.IsEnableableType)
            Add(new SavedColumn<World.EnableGroup<T>, MemoryCopySerializer<World.EnableGroup<T>>>(Derived(key, 8, "EnableGroup"), SavedStorage.Tag, default, true));
        if (ComponentType<T>.KeepsRemovedType && !Columns.ContainsKey(ComponentType<Removed<T>>.TypeId))
            Add(new SavedColumn<Removed<T>, RemovedSerializer<T, TSerializer>>(Derived(key, 32, "Removed"), SavedStorage.Removed,
                new(serializer), raw, ReferencePatchers.Removed(patcher), progress));
        return this;
    }

    public SaveRegistry RegisterBuffer<T>(SavedTypeKey key) where T : struct
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) throw new ArgumentException("Register a buffer element serializer for managed fields.");
        return RegisterBuffer<T, MemoryCopySerializer<T>>(key);
    }
    public SaveRegistry RegisterBuffer<T, TSerializer>(SavedTypeKey key, TSerializer serializer = default)
        where T : struct where TSerializer : struct, IValueSerializer<T>
        => RegisterColumn<Buffer<T>, BufferSerializer<T, TSerializer>>(key, new(serializer), false, SavedStorage.Buffer);

    public SaveRegistry RegisterBuffer<T, TSerializer, TPatcher>(SavedTypeKey key, TSerializer serializer, TPatcher patcher)
        where T : struct where TSerializer : struct, IValueSerializer<T> where TPatcher : struct, IReferencePatcher<T>
        => RegisterColumn<Buffer<T>, BufferSerializer<T, TSerializer>>(key, new(serializer), false, SavedStorage.Buffer,
            new BufferReferencePatcher<T>(ReferencePatchers.Bind<T, TPatcher>(patcher)));

    /// <summary>Registers a buffer element serializer under the buffer's build identity; its values stay out of durable saves.</summary>
    public SaveRegistry RegisterBufferSerializer<T, TSerializer>(TSerializer serializer)
        where T : struct where TSerializer : struct, IValueSerializer<T>
    {
        SavedTypeKey key = RawKey<Buffer<T>>();
        RegisterBuffer<T, TSerializer>(key, serializer);
        ById[key.Id].SupportsDurable = false;
        return this;
    }
}
