using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SomeEngine.ECS.Storage;
using SomeEngine.ECS.Entities;
using SomeEngine.Serialization;

namespace SomeEngine.ECS.Serialization;

internal enum SavedStorage : byte { Table, Tag, Buffer, Shared, Sparse, Resource, Parent, Endpoints, Children, Removed }

internal interface IStoredValueCodec<T> where T : struct
{
    void WriteValue(ref DataWriter writer, in T value);
    T ReadValue(ref DataReader reader);
}

internal interface ISelectionCodec<T> where T : struct
{
    bool Includes(in T value, IReadOnlySet<Entity> entities);
    void Write(ref DataWriter writer, in T value, IReadOnlySet<Entity> entities);
}

internal abstract class SavedType(SerializationTypeKey key, SavedStorage storage, int type, int width, bool raw,
    Type valueType, Type codecType, CodecTraits codecTraits)
{
    internal readonly SerializationTypeKey Key = key;
    internal readonly SavedStorage Storage = storage;
    internal readonly int Type = type, Width = width;
    internal readonly bool Raw = raw;
    internal readonly ComponentCodecKind CodecKind = codecTraits.Kind;
    /// <summary>The fields its generated codec writes, or null for a hand-written codec.</summary>
    internal readonly ValueSchema? Schema = codecTraits.FieldList;
    internal virtual object? EntityPatcher => null;
    private readonly ISavedGroupBinding? _group = SavedGroups.Create(valueType);
    internal bool SupportsDurable = storage == SavedStorage.Tag || !codecTraits.CopiesMemory;
    internal readonly SerializationTypeKey CheckpointKey = key with
    {
        StableName = string.Join('|', key.StableName, TypeBuild(valueType), TypeBuild(codecType), RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture, IntPtr.Size, BitConverter.IsLittleEndian),
        SchemaFingerprint = codecTraits.CopiesMemory ? RawSchema(key, valueType, width) : key.SchemaFingerprint,
    };
    internal SerializationTypeKey KeyFor(SavePurpose format) => format == SavePurpose.RawCheckpoint ? CheckpointKey : Key;
    internal virtual void Activate(World world) => _group?.Activate(world);
    private static string TypeBuild(Type type) => type.AssemblyQualifiedName + "#" + type.Module.ModuleVersionId
        + (type.IsGenericType ? "[" + string.Join(';', type.GetGenericArguments().Select(TypeBuild)) + "]" : "");
    private static ulong RawSchema(SerializationTypeKey key, Type valueType, int width)
    {
        ulong schema = key.SchemaFingerprint;
        string shape = string.Join('|', TypeBuild(valueType), width, IntPtr.Size, BitConverter.IsLittleEndian);
        foreach (byte value in Encoding.UTF8.GetBytes(shape)) schema = (schema ^ value) * 1099511628211ul;
        return schema == 0 ? 1 : schema;
    }
}

internal abstract class SavedColumn(SerializationTypeKey key, SavedStorage storage, int type, int width, bool raw,
    Type valueType, Type codecType, CodecTraits codecTraits) : SavedType(key, storage, type, width, raw, valueType, codecType, codecTraits)
{
    internal abstract void Write(ref DataWriter writer, ref ColumnEntry column, int count);
    internal abstract void Read(ref DataReader reader, ref ColumnEntry column, int count);
    internal virtual void PrepareRead(World world) { }
    internal abstract void WriteCell(ref DataWriter writer, World world, ref ColumnEntry column, int row);
    internal virtual bool IncludesCell(ref ColumnEntry column, int row, IReadOnlySet<Entity> entities) => column.Has(row);
    internal virtual void WriteSelectedCell(ref DataWriter writer, World world, ref ColumnEntry column, int row,
        IReadOnlySet<Entity> entities) => WriteCell(ref writer, world, ref column, row);
    internal virtual void WriteEntityRows(ref DataWriter writer, ref ColumnEntry column, ReadOnlySpan<Entity> entities) => throw new InvalidOperationException();
    internal virtual void ReadCell(ref DataReader reader, ref ColumnEntry column, int row) => throw new InvalidOperationException();
}

internal sealed unsafe class SavedColumn<T, TCodec>(SerializationTypeKey key, SavedStorage storage, TCodec codec, bool raw,
    IReferencePatcher<T>? patcher = null, bool progress = false)
    : SavedColumn(key, storage, ComponentValues.Bind<T>().Id, raw && !ComponentType<T>.IsTagType ? Unsafe.SizeOf<T>() : 0,
        raw, typeof(T), typeof(TCodec), CodecTraits.Of<T, TCodec>(codec)), IStoredValueCodec<T> where T : struct where TCodec : struct, IValueCodec<T>
{
    private readonly IReferencePatcher<T>? _patcher = patcher ?? ReferencePatchers.DefaultValue<T>();
    private readonly ISelectionCodec<T>? _selection = codec is ISelectionCodec<T> selected ? selected : null;
    internal override object? EntityPatcher => _patcher;
    internal override bool IncludesCell(ref ColumnEntry column, int row, IReadOnlySet<Entity> entities)
        => column.Has(row) && (_selection is null || _selection.Includes(ColumnEntry.At<T>(ref column, row), entities));
    internal override void WriteSelectedCell(ref DataWriter writer, World world, ref ColumnEntry column, int row,
        IReadOnlySet<Entity> entities)
    {
        if (_selection is { } selection) selection.Write(ref writer, ColumnEntry.At<T>(ref column, row), entities);
        else WriteCell(ref writer, world, ref column, row);
    }
    internal override void PrepareRead(World world)
    {
        if (codec is IValueReadStorage storage) storage.PrepareRead(world);
    }
    internal override void WriteCell(ref DataWriter writer, World world, ref ColumnEntry column, int row)
    {
        if (ComponentType<T>.IsTagType) return;
        TCodec item = codec;
        item.Write(ref writer, ColumnEntry.At<T>(ref column, row));
    }
    public void WriteValue(ref DataWriter writer, in T value)
    {
        if (ComponentType<T>.IsTagType) return;
        TCodec item = codec;
        item.Write(ref writer, value);
    }

    public T ReadValue(ref DataReader reader)
    {
        if (ComponentType<T>.IsTagType) return default;
        TCodec item = codec;
        item.Read(ref reader, out T value);
        return value;
    }
    internal override void WriteEntityRows(ref DataWriter writer, ref ColumnEntry column, ReadOnlySpan<Entity> entities)
    {
        Span<T> values = ColumnEntry.Span<T>(ref column, entities.Length);
        if (column.Presence is null)
        {
            for (int row = 0; row < entities.Length; row++)
            { writer.WriteEntity(entities[row]); TCodec item = codec; item.Write(ref writer, values[row]); }
        }
        else
            for (int row = 0; row < entities.Length; row++)
                if (column.Has(row))
                { writer.WriteEntity(entities[row]); TCodec item = codec; item.Write(ref writer, values[row]); }
    }
    internal override void ReadCell(ref DataReader reader, ref ColumnEntry column, int row)
    {
        if (ComponentType<T>.IsTagType) return;
        TCodec item = codec;
        ref T value = ref ColumnEntry.At<T>(ref column, row);
        item.Read(ref reader, out value);
        if (reader.PatchesValues) reader.Patch(ref value, _patcher, progress);
    }
    internal override void Write(ref DataWriter writer, ref ColumnEntry column, int count)
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
                foreach (ref readonly T value in values) { TCodec itemCodec = codec; itemCodec.Write(ref writer, value); }
            }
            else
                for (int row = 0; row < count; row++)
                    if (column.Has(row)) { TCodec itemCodec = codec; itemCodec.Write(ref writer, values[row]); }
            writer.WriteInt32(checked((int)(writer.Position - start)));
        }
    }

    internal override void Read(ref DataReader reader, ref ColumnEntry column, int count)
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
                if (column.Has(row)) { TCodec itemCodec = codec; itemCodec.Read(ref reader, out values[row]); }
            long consumed = reader.Position - start;
            int length = reader.ReadInt32();
            if (consumed != length) throw new InvalidDataException("Component codec length differs from its column footer.");
            reader.Budget.PayloadLength(length);
        }
        if (_patcher is not null && reader.PatchesValues)
            for (int row = 0; row < count; row++)
                if (column.Has(row)) reader.Patch(ref ColumnEntry.At<T>(ref column, row), _patcher, progress);
    }
}

internal readonly struct RawCodec<T> : IComponentCodec<T>, IEngineCodec where T : struct
{
    public bool CopiesMemory => true;
    public ValueSchema? FieldList => null;
    public void Write(ref DataWriter writer, in T value)
        => writer.WriteRawBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in value), 1)));
    public void Read(ref DataReader reader, out T value)
    {
        value = default;
        reader.ReadRawBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1)));
    }
}

/// <summary>Copies a value whose memory layout matches its generated codec's encoding.</summary>
/// <remarks>A save whose fields differ from the current type is read by the generated codec instead.</remarks>
internal readonly struct PackedValueCodec<T, TCodec> : IDurableComponentCodec<T>, IEngineCodec
    where T : struct where TCodec : struct, IDurableComponentCodec<T>
{
    public bool CopiesMemory => false;
    public bool IsPacked => true;
    public ValueSchema? FieldList => CodecTraits.FieldListOf(default(TCodec));
    public void Write(ref DataWriter writer, in T value) => new RawCodec<T>().Write(ref writer, value);
    public void Read(ref DataReader reader, out T value)
    {
        if (reader.HasSavedFieldMaps) default(TCodec).Read(ref reader, out value);
        else new RawCodec<T>().Read(ref reader, out value);
    }
    public bool SupportsParallelWrite => true;
}

/// <summary>An engine codec that reports how it stores values, including through the codec it wraps.</summary>
internal interface IEngineCodec
{
    /// <summary>Copies values as raw memory, which only the same build can read back.</summary>
    bool CopiesMemory { get; }
    /// <summary>Copies a memory layout that its generated codec has proven matches that codec's encoding.</summary>
    bool IsPacked => false;
    ValueSchema? FieldList { get; }
}

/// <summary>What a binding needs to know about its codec, read from the codec value instead of by reflection.</summary>
internal readonly record struct CodecTraits(ComponentCodecKind Kind, bool CopiesMemory, ValueSchema? FieldList)
{
    internal static CodecTraits Of<T, TCodec>(TCodec codec) where TCodec : struct, IValueCodec<T>
    {
        bool copies = CopiesMemoryOf(codec);
        ComponentCodecKind kind = codec is IEngineCodec { IsPacked: true } ? ComponentCodecKind.RawDurable
            : copies ? ComponentCodecKind.Raw
            : codec is IDurableCodec ? ComponentCodecKind.Durable
            : ComponentCodecKind.Custom;
        return new(kind, copies, FieldListOf(codec));
    }

    internal static bool CopiesMemoryOf<TCodec>(TCodec codec) => codec is IEngineCodec { CopiesMemory: true };

    internal static ValueSchema? FieldListOf<TCodec>(TCodec codec)
        => codec is ISchemaValueCodec generated ? generated.FieldList : (codec as IEngineCodec)?.FieldList;
}

internal interface IValueReadStorage
{
    void PrepareRead(World world);
}

internal readonly struct RemovedCodec<T, TCodec>(TCodec codec) : IComponentCodec<Removed<T>>, IValueReadStorage, IEngineCodec
    where T : struct where TCodec : struct, IValueCodec<T>
{
    public bool CopiesMemory => CodecTraits.CopiesMemoryOf(codec);
    public ValueSchema? FieldList => CodecTraits.FieldListOf(codec);
    public void PrepareRead(World world)
    {
        if (codec is IValueReadStorage storage) storage.PrepareRead(world);
    }
    public void Write(ref DataWriter writer, in Removed<T> value)
    { TCodec item = codec; item.Write(ref writer, value.Value); writer.WriteUInt64(value.Version); }
    public void Read(ref DataReader reader, out Removed<T> value)
    { TCodec item = codec; item.Read(ref reader, out T previous); value = new(previous, reader.ReadUInt64()); }
}

internal readonly struct BufferCodec<T, TCodec>(TCodec codec) : IComponentCodec<Buffer<T>>, IValueReadStorage, IEngineCodec
    where T : struct where TCodec : struct, IValueCodec<T>
{
    public bool CopiesMemory => CodecTraits.CopiesMemoryOf(codec);
    public ValueSchema? FieldList => CodecTraits.FieldListOf(codec);
    public void PrepareRead(World world) => world.BufferPool<T>();
    public void Write(ref DataWriter writer, in Buffer<T> value)
    {
        int count = value.Count;
        writer.WriteInt32(count);
        for (int i = 0; i < count; i++) { TCodec item = codec; item.Write(ref writer, value.Items![i]); }
    }
    public void Read(ref DataReader reader, out Buffer<T> value)
    {
        int count = reader.Budget.BufferElementCount<T>(reader.ReadInt32());
        value = reader.ValuesWorld is { } world ? world.BufferPool<T>().Rent() : new() { Items = [] };
        value.Reserve(count);
        for (int i = 0; i < count; i++) { TCodec item = codec; item.Read(ref reader, out value.Items![i]); value.Count = i + 1; }
    }
}

public sealed partial class SerializationRegistry
{
    public SerializationRegistry()
        => RegisterColumn<Destroyed, RawCodec<Destroyed>>(
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
    public SerializationRegistry RetireType(Guid id)
    {
        if (ById.ContainsKey(id)) throw new ArgumentException("A registered type cannot also be retired.", nameof(id));
        RetiredTypes.Add(id);
        // Its enable flags, children, removed values and relation endpoints are saved under derived ids.
        foreach (byte role in (ReadOnlySpan<byte>)[8, 16, 32, 64])
            RetiredTypes.Add(Derived(new SerializationTypeKey(id, string.Empty, 1), role, string.Empty).StableId);
        return this;
    }
    internal int Version;

    private TBinding Add<TBinding>(TBinding type) where TBinding : SavedType
    {
        if (type.Key.StableId == Guid.Empty || type.Key.SchemaFingerprint == 0)
            throw new ArgumentException("Serialization types require a stable identity and a nonzero schema.");
        _orders.Add((type.Storage, type.Type), Types.Count);
        Types.Add(type);
        ById.Add(type.Key.StableId, type);
        if (type is SavedColumn column) Columns.Add(type.Type, column);
        Version++;
        return type;
    }
    internal static SerializationTypeKey Derived(SerializationTypeKey key, byte role, string name)
    {
        Span<byte> bytes = stackalloc byte[16];
        key.StableId.TryWriteBytes(bytes);
        bytes[15] ^= role;
        return new(new Guid(bytes), key.StableName + "." + name, key.SchemaFingerprint);
    }
    internal static SerializationTypeKey RawKey<T>()
    {
        Span<byte> bytes = stackalloc byte[16];
        typeof(T).Module.ModuleVersionId.TryWriteBytes(bytes);
        ulong schema = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return new(typeof(T).GUID, typeof(T).FullName!, schema == 0 ? 1 : schema);
    }

    public SerializationRegistry Register<T>() where T : struct => Register<T>(RawKey<T>());
    public SerializationRegistry Register<T>(SerializationTypeKey key) where T : struct
        => RegisterComponent<T, RawCodec<T>>(key, default, true, null);
    public SerializationRegistry Register<T, TCodec>(SerializationTypeKey key, TCodec codec = default)
        where T : struct where TCodec : struct, IComponentCodec<T>
        => RegisterComponent<T, TCodec>(key, codec, false, null);
    public SerializationRegistry Register<T, TCodec, TPatcher>(SerializationTypeKey key, TCodec codec, TPatcher patcher)
        where T : struct where TCodec : struct, IComponentCodec<T> where TPatcher : struct, IReferencePatcher<T>
        => RegisterComponent<T, TCodec>(key, codec, false, ReferencePatchers.Bind<T, TPatcher>(patcher));

    /// <summary>The component type decides where its values are saved: shared values, sparse values or table columns.</summary>
    private SerializationRegistry RegisterComponent<T, TCodec>(SerializationTypeKey key, TCodec codec, bool raw,
        IReferencePatcher<T>? patcher)
        where T : struct where TCodec : struct, IComponentCodec<T>
        => ComponentType<T>.IsSharedType ? RegisterShared(key, codec, raw, patcher)
            : ComponentType<T>.IsSparseType ? RegisterSparse(key, codec, raw, patcher)
            : RegisterColumn(key, codec, raw, ComponentType<T>.IsTagType ? SavedStorage.Tag : SavedStorage.Table, patcher);

    public SerializationRegistry RegisterDurable<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicFields)] T, TCodec>(
        SerializationTypeKey key, int encodedSize = -1, ulong layoutProof = 0)
        where T : struct where TCodec : struct, IDurableComponentCodec<T>
        => PackedValueLayout.IsVerified<T>(encodedSize, layoutProof)
            ? Register<T, PackedValueCodec<T, TCodec>>(key)
            : Register<T, TCodec>(key);

    /// <summary>Registers a codec under the type's build identity; its values stay out of durable saves.</summary>
    public SerializationRegistry RegisterCodec<T, TCodec>(TCodec codec)
        where T : struct where TCodec : struct, IComponentCodec<T>
    {
        SerializationTypeKey key = RawKey<T>();
        Register<T, TCodec>(key, codec);
        ById[key.StableId].SupportsDurable = false;
        return this;
    }

    private SerializationRegistry RegisterColumn<T, TCodec>(SerializationTypeKey key, TCodec codec, bool raw, SavedStorage kind,
        IReferencePatcher<T>? patcher = null, bool progress = false, bool describe = true)
        where T : struct where TCodec : struct, IValueCodec<T>
    {
        ComponentValues.Bind<T>();
        if ((kind is SavedStorage.Table or SavedStorage.Tag) && typeof(IControlledComponent).IsAssignableFrom(typeof(T)))
            throw new InvalidOperationException("Controlled topology values require their hierarchy or relation registration.");
        if (raw && RuntimeHelpers.IsReferenceOrContainsReferences<T>() && kind != SavedStorage.Tag)
            throw new ArgumentException("Register a value codec for managed component fields.");
        patcher ??= ReferencePatchers.DefaultValue<T>();
        var binding = Add(new SavedColumn<T, TCodec>(key, kind, codec, raw, patcher, progress));
        if (describe) _entries.Add(binding.Describe());
        if (ComponentType<T>.IsEnableableType)
            Add(new SavedColumn<World.EnableGroup<T>, RawCodec<World.EnableGroup<T>>>(Derived(key, 8, "EnableGroup"), SavedStorage.Tag, default, true));
        if (ComponentType<T>.KeepsRemovedType && !Columns.ContainsKey(ComponentType<Removed<T>>.TypeId))
            Add(new SavedColumn<Removed<T>, RemovedCodec<T, TCodec>>(Derived(key, 32, "Removed"), SavedStorage.Removed,
                new(codec), raw, ReferencePatchers.Removed(patcher), progress));
        return this;
    }

    public SerializationRegistry RegisterBuffer<T>(SerializationTypeKey key) where T : struct
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) throw new ArgumentException("Register a buffer element codec for managed fields.");
        return RegisterBuffer<T, RawCodec<T>>(key);
    }
    public SerializationRegistry RegisterBuffer<T, TCodec>(SerializationTypeKey key, TCodec codec = default)
        where T : struct where TCodec : struct, IComponentCodec<T>
        => RegisterColumn<Buffer<T>, BufferCodec<T, TCodec>>(key, new(codec), false, SavedStorage.Buffer);

    public SerializationRegistry RegisterBuffer<T, TCodec, TPatcher>(SerializationTypeKey key, TCodec codec, TPatcher patcher)
        where T : struct where TCodec : struct, IComponentCodec<T> where TPatcher : struct, IReferencePatcher<T>
        => RegisterColumn<Buffer<T>, BufferCodec<T, TCodec>>(key, new(codec), false, SavedStorage.Buffer,
            new BufferReferencePatcher<T>(ReferencePatchers.Bind<T, TPatcher>(patcher)));

    /// <summary>Registers a buffer element codec under the buffer's build identity; its values stay out of durable saves.</summary>
    public SerializationRegistry RegisterBufferCodec<T, TCodec>(TCodec codec)
        where T : struct where TCodec : struct, IComponentCodec<T>
    {
        SerializationTypeKey key = RawKey<Buffer<T>>();
        RegisterBuffer<T, TCodec>(key, codec);
        ById[key.StableId].SupportsDurable = false;
        return this;
    }
}
