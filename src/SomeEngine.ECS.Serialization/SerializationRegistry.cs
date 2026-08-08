using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using IComponent = global::SomeEngine.ECS.IComponent;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Registry;
using SomeEngine.Serialization;

namespace SomeEngine.ECS.Serialization;

public sealed partial class SerializationRegistry
{
    private readonly List<SerializationTypeRuntime> _runtimes = new();
    private readonly Dictionary<Guid, SerializationTypeRuntime> _byStableId = new();
    private readonly Dictionary<Type, SerializationTypeRuntime> _byType = new();
    private SerializationTypeEntry[] _publishedEntries = Array.Empty<SerializationTypeEntry>();

    public ReadOnlySpan<SerializationTypeEntry> Entries => _publishedEntries;

    public SerializationRegistry Register<T>()
        where T : struct, IComponent
    {
        ValidateOrdinaryComponent<T>();
        return Register(new ComponentSerializationRuntime<T>(
            CreateTypeKey<T>(ComponentMetadata<T>.Storage, SerializationValueKind.Component),
            CreateDefaultCodec<T>(),
            patcher: null,
            schemaSource: SerializationSchemaSource.RuntimeDerived));
    }

    public SerializationRegistry Register<T>(SerializationTypeKey typeKey)
        where T : struct, IComponent
    {
        ValidateOrdinaryComponent<T>();
        return Register(new ComponentSerializationRuntime<T>(typeKey, CreateDefaultCodec<T>(), patcher: null));
    }

    public SerializationRegistry Register<T, TCodec>()
        where T : struct, IComponent
        where TCodec : struct, IComponentCodec<T>
    {
        ValidateOrdinaryComponent<T>();
        return Register(new ComponentSerializationRuntime<T>(
            CreateTypeKey<T>(ComponentMetadata<T>.Storage, SerializationValueKind.Component),
            CreateExplicitCodec<T, TCodec>(),
            patcher: null,
            schemaSource: SerializationSchemaSource.RuntimeDerived));
    }

    public SerializationRegistry Register<T, TCodec>(SerializationTypeKey typeKey)
        where T : struct, IComponent
        where TCodec : struct, IComponentCodec<T>
    {
        ValidateOrdinaryComponent<T>();
        return Register(new ComponentSerializationRuntime<T>(typeKey, CreateExplicitCodec<T, TCodec>(), patcher: null));
    }

    public SerializationRegistry Register<T, TCodec, TPatcher>()
        where T : struct, IComponent
        where TCodec : struct, IComponentCodec<T>
        where TPatcher : struct, IReferencePatcher<T>
    {
        ValidateOrdinaryComponent<T>();
        return Register(new ComponentSerializationRuntime<T>(
            CreateTypeKey<T>(ComponentMetadata<T>.Storage, SerializationValueKind.Component),
            CreateExplicitCodec<T, TCodec>(),
            new CustomReferencePatcher<T, TPatcher>(),
            schemaSource: SerializationSchemaSource.RuntimeDerived));
    }

    public SerializationRegistry Register<T, TCodec, TPatcher>(SerializationTypeKey typeKey)
        where T : struct, IComponent
        where TCodec : struct, IComponentCodec<T>
        where TPatcher : struct, IReferencePatcher<T>
    {
        ValidateOrdinaryComponent<T>();
        return Register(new ComponentSerializationRuntime<T>(
            typeKey,
            CreateExplicitCodec<T, TCodec>(),
            new CustomReferencePatcher<T, TPatcher>()));
    }

    public SerializationRegistry RegisterTag<T>()
        where T : struct, ITag =>
        Register(new TagSerializationRuntime<T>(
            CreateTypeKey<T>(StoragePath.Tag, SerializationValueKind.Tag),
            schemaSource: SerializationSchemaSource.RuntimeDerived));

    public SerializationRegistry RegisterTag<T>(SerializationTypeKey typeKey)
        where T : struct, ITag =>
        Register(new TagSerializationRuntime<T>(typeKey));

    public SerializationRegistry RegisterShared<T>()
        where T : struct, ISharedComponent =>
        Register(new SharedSerializationRuntime<T>(
            CreateTypeKey<T>(StoragePath.Shared, SerializationValueKind.Shared),
            CreateDefaultCodec<T>(),
            patcher: null,
            schemaSource: SerializationSchemaSource.RuntimeDerived));

    public SerializationRegistry RegisterShared<T>(SerializationTypeKey typeKey)
        where T : struct, ISharedComponent =>
        Register(new SharedSerializationRuntime<T>(typeKey, CreateDefaultCodec<T>(), patcher: null));

    public SerializationRegistry RegisterShared<T, TCodec>()
        where T : struct, ISharedComponent
        where TCodec : struct, IComponentCodec<T> =>
        Register(new SharedSerializationRuntime<T>(
            CreateTypeKey<T>(StoragePath.Shared, SerializationValueKind.Shared),
            CreateExplicitCodec<T, TCodec>(),
            patcher: null,
            schemaSource: SerializationSchemaSource.RuntimeDerived));

    public SerializationRegistry RegisterShared<T, TCodec>(SerializationTypeKey typeKey)
        where T : struct, ISharedComponent
        where TCodec : struct, IComponentCodec<T> =>
        Register(new SharedSerializationRuntime<T>(typeKey, CreateExplicitCodec<T, TCodec>(), patcher: null));

    public SerializationRegistry RegisterShared<T, TCodec, TPatcher>()
        where T : struct, ISharedComponent
        where TCodec : struct, IComponentCodec<T>
        where TPatcher : struct, IReferencePatcher<T> =>
        Register(new SharedSerializationRuntime<T>(
            CreateTypeKey<T>(StoragePath.Shared, SerializationValueKind.Shared),
            new CustomValueCodec<T, TCodec>(),
            new CustomReferencePatcher<T, TPatcher>(),
            schemaSource: SerializationSchemaSource.RuntimeDerived));

    public SerializationRegistry RegisterShared<T, TCodec, TPatcher>(SerializationTypeKey typeKey)
        where T : struct, ISharedComponent
        where TCodec : struct, IComponentCodec<T>
        where TPatcher : struct, IReferencePatcher<T> =>
        Register(new SharedSerializationRuntime<T>(
            typeKey,
            new CustomValueCodec<T, TCodec>(),
            new CustomReferencePatcher<T, TPatcher>()));

    public SerializationRegistry RegisterBuffer<T>()
        where T : struct, IBufferElement =>
        Register(new BufferSerializationRuntime<T>(
            CreateTypeKey<T>(StoragePath.Table, SerializationValueKind.Buffer),
            CreateDefaultCodec<T>(),
            patcher: null,
            schemaSource: SerializationSchemaSource.RuntimeDerived));

    public SerializationRegistry RegisterBuffer<T>(SerializationTypeKey typeKey)
        where T : struct, IBufferElement =>
        Register(new BufferSerializationRuntime<T>(typeKey, CreateDefaultCodec<T>(), patcher: null));

    public SerializationRegistry RegisterBuffer<T, TCodec>()
        where T : struct, IBufferElement
        where TCodec : struct, IComponentCodec<T> =>
        Register(new BufferSerializationRuntime<T>(
            CreateTypeKey<T>(StoragePath.Table, SerializationValueKind.Buffer),
            CreateExplicitCodec<T, TCodec>(),
            patcher: null,
            schemaSource: SerializationSchemaSource.RuntimeDerived));

    public SerializationRegistry RegisterBuffer<T, TCodec>(SerializationTypeKey typeKey)
        where T : struct, IBufferElement
        where TCodec : struct, IComponentCodec<T> =>
        Register(new BufferSerializationRuntime<T>(typeKey, CreateExplicitCodec<T, TCodec>(), patcher: null));

    public SerializationRegistry RegisterBuffer<T, TCodec, TPatcher>()
        where T : struct, IBufferElement
        where TCodec : struct, IComponentCodec<T>
        where TPatcher : struct, IReferencePatcher<T> =>
        Register(new BufferSerializationRuntime<T>(
            CreateTypeKey<T>(StoragePath.Table, SerializationValueKind.Buffer),
            CreateExplicitCodec<T, TCodec>(),
            new CustomReferencePatcher<T, TPatcher>(),
            schemaSource: SerializationSchemaSource.RuntimeDerived));

    public SerializationRegistry RegisterBuffer<T, TCodec, TPatcher>(SerializationTypeKey typeKey)
        where T : struct, IBufferElement
        where TCodec : struct, IComponentCodec<T>
        where TPatcher : struct, IReferencePatcher<T> =>
        Register(new BufferSerializationRuntime<T>(
            typeKey,
            CreateExplicitCodec<T, TCodec>(),
            new CustomReferencePatcher<T, TPatcher>()));
}

public sealed partial class SerializationRegistry
{
    public SerializationRegistry RegisterSparse<T>()
        where T : struct, ISparseComponent =>
        Register(new SparseSerializationRuntime<T>(
            CreateTypeKey<T>(StoragePath.Sparse, SerializationValueKind.Sparse),
            CreateDefaultCodec<T>(),
            patcher: null,
            schemaSource: SerializationSchemaSource.RuntimeDerived));

    public SerializationRegistry RegisterSparse<T>(SerializationTypeKey typeKey)
        where T : struct, ISparseComponent =>
        Register(new SparseSerializationRuntime<T>(typeKey, CreateDefaultCodec<T>(), patcher: null));

    public SerializationRegistry RegisterSparse<T, TCodec>()
        where T : struct, ISparseComponent
        where TCodec : struct, IComponentCodec<T> =>
        Register(new SparseSerializationRuntime<T>(
            CreateTypeKey<T>(StoragePath.Sparse, SerializationValueKind.Sparse),
            CreateExplicitCodec<T, TCodec>(),
            patcher: null,
            schemaSource: SerializationSchemaSource.RuntimeDerived));

    public SerializationRegistry RegisterSparse<T, TCodec>(SerializationTypeKey typeKey)
        where T : struct, ISparseComponent
        where TCodec : struct, IComponentCodec<T> =>
        Register(new SparseSerializationRuntime<T>(typeKey, CreateExplicitCodec<T, TCodec>(), patcher: null));

    public SerializationRegistry RegisterSparse<T, TCodec, TPatcher>()
        where T : struct, ISparseComponent
        where TCodec : struct, IComponentCodec<T>
        where TPatcher : struct, IReferencePatcher<T> =>
        Register(new SparseSerializationRuntime<T>(
            CreateTypeKey<T>(StoragePath.Sparse, SerializationValueKind.Sparse),
            CreateExplicitCodec<T, TCodec>(),
            new CustomReferencePatcher<T, TPatcher>(),
            SerializationSchemaSource.RuntimeDerived));

    public SerializationRegistry RegisterSparse<T, TCodec, TPatcher>(SerializationTypeKey typeKey)
        where T : struct, ISparseComponent
        where TCodec : struct, IComponentCodec<T>
        where TPatcher : struct, IReferencePatcher<T> =>
        Register(new SparseSerializationRuntime<T>(
            typeKey,
            CreateExplicitCodec<T, TCodec>(),
            new CustomReferencePatcher<T, TPatcher>()));

}

public sealed partial class SerializationRegistry
{
    /// <summary>
    /// Registers a source-generated canonical component codec. When the generator proves that
    /// the packed CLR layout is identical to the canonical little-endian representation,
    /// <paramref name="rawCanonicalSize"/> and <paramref name="rawCanonicalLayoutFingerprint"/>
    /// enable a memcpy fast path on compatible hosts. Supplying a size without the generated
    /// layout proof always falls back to the canonical codec.
    /// </summary>
    public SerializationRegistry RegisterCanonical<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicFields |
            DynamicallyAccessedMemberTypes.NonPublicFields)] T,
        TCodec>(
        SerializationTypeKey typeKey,
        int rawCanonicalSize = -1,
        ulong rawCanonicalLayoutFingerprint = 0)
        where T : struct, IComponent
        where TCodec : struct, ICanonicalComponentCodec<T>
    {
        ValidateOrdinaryComponent<T>();
        return Register(new ComponentSerializationRuntime<T>(
            typeKey,
            CreateGeneratedCodec<T, TCodec>(rawCanonicalSize, rawCanonicalLayoutFingerprint),
            patcher: null));
    }

    public SerializationRegistry RegisterSharedCanonical<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicFields |
            DynamicallyAccessedMemberTypes.NonPublicFields)] T,
        TCodec>(
        SerializationTypeKey typeKey,
        int rawCanonicalSize = -1,
        ulong rawCanonicalLayoutFingerprint = 0)
        where T : struct, ISharedComponent
        where TCodec : struct, ICanonicalComponentCodec<T> =>
        Register(new SharedSerializationRuntime<T>(
            typeKey,
            CreateGeneratedCodec<T, TCodec>(rawCanonicalSize, rawCanonicalLayoutFingerprint),
            patcher: null));

    public SerializationRegistry RegisterBufferCanonical<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicFields |
            DynamicallyAccessedMemberTypes.NonPublicFields)] T,
        TCodec>(
        SerializationTypeKey typeKey,
        int rawCanonicalSize = -1,
        ulong rawCanonicalLayoutFingerprint = 0)
        where T : struct, IBufferElement
        where TCodec : struct, ICanonicalComponentCodec<T> =>
        Register(new BufferSerializationRuntime<T>(
            typeKey,
            CreateGeneratedCodec<T, TCodec>(rawCanonicalSize, rawCanonicalLayoutFingerprint),
            patcher: null));

    public SerializationRegistry RegisterSparseCanonical<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicFields |
            DynamicallyAccessedMemberTypes.NonPublicFields)] T,
        TCodec>(
        SerializationTypeKey typeKey,
        int rawCanonicalSize = -1,
        ulong rawCanonicalLayoutFingerprint = 0)
        where T : struct, ISparseComponent
        where TCodec : struct, ICanonicalComponentCodec<T> =>
        Register(new SparseSerializationRuntime<T>(
            typeKey,
            CreateGeneratedCodec<T, TCodec>(rawCanonicalSize, rawCanonicalLayoutFingerprint),
            patcher: null));

    internal ReadOnlySpan<SerializationTypeRuntime> RuntimeTypes =>
        CollectionsMarshal.AsSpan(_runtimes);
    internal SerializationTypeRuntime GetRegistered<T>()
        where T : struct
    {
        if (_byType.TryGetValue(typeof(T), out var runtime))
            return runtime;

        throw new InvalidOperationException($"Type {typeof(T).FullName} is not registered for serialization.");
    }

    internal SerializationTypeRuntime Resolve(SerializationTypeKey fileKey)
    {
        if (!_byStableId.TryGetValue(fileKey.StableId, out var runtime))
        {
            throw new InvalidDataException(
                $"Unknown serialized type '{fileKey.StableName}' ({fileKey.StableId}).");
        }

        if (fileKey.SchemaFingerprint == 0 ||
            runtime.Entry.TypeKey.SchemaFingerprint != fileKey.SchemaFingerprint)
        {
            throw new InvalidDataException(
                $"Schema mismatch for '{fileKey.StableName}': file=0x{fileKey.SchemaFingerprint:X16}, " +
                $"local=0x{runtime.Entry.TypeKey.SchemaFingerprint:X16}.");
        }
        if (!string.Equals(
                runtime.Entry.TypeKey.StableName,
                fileKey.StableName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Serialized type key name '{fileKey.StableName}' does not exactly match " +
                $"registered name '{runtime.Entry.TypeKey.StableName}'.");
        }

        return runtime;
    }

    internal SerializationTypeRuntime ResolveExact(SerializationTypeKey fileKey) => Resolve(fileKey);

    private SerializationRegistry Register(SerializationTypeRuntime runtime)
    {
        SerializationTypeKey typeKey = runtime.Entry.TypeKey;

        if (typeKey.SchemaFingerprint == 0)
        {
            throw new ArgumentException(
                $"Serialization type '{typeKey.StableName}' must declare a non-zero 64-bit schema fingerprint.",
                nameof(runtime));
        }

        if (_byStableId.ContainsKey(typeKey.StableId))
            throw new InvalidOperationException($"Serialization stable id {typeKey.StableId} is already registered.");

        if (_byType.ContainsKey(runtime.ValueType))
            throw new InvalidOperationException($"Type {runtime.ValueType.FullName} is already registered for serialization.");

        _runtimes.Add(runtime);
        _runtimes.Sort(static (left, right) => CompareTypeKeys(left.Entry.TypeKey, right.Entry.TypeKey));
        var entries = new SerializationTypeEntry[_runtimes.Count];
        for (int i = 0; i < _runtimes.Count; i++)
            entries[i] = _runtimes[i].Entry;
        _publishedEntries = entries;
        _byStableId.Add(typeKey.StableId, runtime);
        _byType.Add(runtime.ValueType, runtime);
        return this;
    }

    internal static int CompareTypeKeys(SerializationTypeKey left, SerializationTypeKey right)
    {
        int stableIdCompare = left.StableId.CompareTo(right.StableId);
        return stableIdCompare != 0
            ? stableIdCompare
            : string.CompareOrdinal(left.StableName, right.StableName);
    }

    private static void ValidateOrdinaryComponent<T>()
        where T : struct, IComponent
    {
        if (!ComponentMetadata<T>.IsRelationshipSource &&
            !ComponentMetadata<T>.IsRelationshipTarget)
        {
            return;
        }

        // Parent/endpoints are encoded by TopologySerializationRegistry in the canonical topology
        // section; derived Children/adjacency views are rebuilt on import. They must never enter
        // the ordinary per-entity value registry independently.
        throw new InvalidOperationException(
            $"Relationship topology component {typeof(T).FullName} cannot be registered as an " +
            "ordinary entity-row value. Register relation payload components with Register<T>(); " +
            "topology requires the canonical relationship/hierarchy serialization section.");
    }

    private static IValueCodec<T> CreateDefaultCodec<T>()
        where T : struct
    {
        return RuntimeHelpers.IsReferenceOrContainsReferences<T>()
            ? MissingValueCodec<T>.Instance
            : RawValueCodec<T>.Instance;
    }

    private static IValueCodec<T> CreateExplicitCodec<T, TCodec>()
        where T : struct
        where TCodec : struct, IComponentCodec<T>
    {
        return typeof(ICanonicalComponentCodec<T>).IsAssignableFrom(typeof(TCodec))
            ? new CanonicalValueCodec<T, TCodec>()
            : new CustomValueCodec<T, TCodec>();
    }

    private static IValueCodec<T> CreateGeneratedCodec<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicFields |
            DynamicallyAccessedMemberTypes.NonPublicFields)] T,
        TCodec>(
        int rawCanonicalSize,
        ulong rawCanonicalLayoutFingerprint)
        where T : struct
        where TCodec : struct, ICanonicalComponentCodec<T>
    {
        if (RawCanonicalLayout.IsVerified<T>(
                rawCanonicalSize,
                rawCanonicalLayoutFingerprint))
        {
            return RawCanonicalValueCodec<T>.Instance;
        }

        return new CanonicalValueCodec<T, TCodec>();
    }

    private static SerializationTypeKey CreateTypeKey<T>(StoragePath storage, SerializationValueKind kind)
        where T : struct
    {
        var type = typeof(T);
        var attr = (SerializableComponentAttribute?)Attribute.GetCustomAttribute(
            type,
            typeof(SerializableComponentAttribute));
        string stableName = type.FullName ?? type.Name;
        ulong schemaFingerprint = ComputeSchemaFingerprint<T>(stableName, storage, kind);
        Guid stableId = attr?.StableId ?? CreateDeterministicGuid(stableName);
        return new SerializationTypeKey(stableId, stableName, schemaFingerprint);
    }

    private static Guid CreateDeterministicGuid(string stableName)
    {
        return BinaryTypeId.FromLogicalName("SomeEngine.ECS.Serialization:" + stableName);
    }

    private static ulong ComputeSchemaFingerprint<T>(string stableName, StoragePath storage, SerializationValueKind kind)
        where T : struct
    {
        ulong hash = BinaryFieldKey.FromName(stableName);
        foreach (byte b in typeof(T).Module.ModuleVersionId.ToByteArray())
            hash = (hash ^ b) * 1099511628211ul;
        hash = (hash ^ (uint)Unsafe.SizeOf<T>()) * 1099511628211ul;
        hash = (hash ^ (uint)storage) * 1099511628211ul;
        hash = (hash ^ (uint)kind) * 1099511628211ul;
        hash = (hash ^ (RuntimeHelpers.IsReferenceOrContainsReferences<T>() ? 1u : 0u)) * 1099511628211ul;
        return hash == 0 ? 1ul : hash;
    }

}

internal interface IValueCodec<T>
    where T : struct
{
    ComponentCodecKind Kind { get; }
    void Write(ref DataWriter writer, in T value);
    T Read(ref DataReader reader);
}

internal interface IValuePatcher<T>
    where T : struct
{
    void Remap(ref T value, IReferenceRemapper remapper);
}

internal sealed class MissingValueCodec<T> : IValueCodec<T>
    where T : struct
{
    public static readonly MissingValueCodec<T> Instance = new();

    public ComponentCodecKind Kind => ComponentCodecKind.Missing;

    public void Write(ref DataWriter writer, in T value)
    {
        throw new InvalidOperationException(
            $"Type {typeof(T).FullName} contains references and requires a registered serialization codec.");
    }

    public T Read(ref DataReader reader)
    {
        throw new InvalidOperationException(
            $"Type {typeof(T).FullName} contains references and requires a registered serialization codec.");
    }
}

internal sealed class RawValueCodec<T> : IValueCodec<T>
    where T : struct
{
    public static readonly RawValueCodec<T> Instance = new();

    public ComponentCodecKind Kind => ComponentCodecKind.Raw;

    public void Write(ref DataWriter writer, in T value)
    {
        ref var mutable = ref Unsafe.AsRef(in value);
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref mutable, 1));
        writer.WriteRawBytes(bytes);
    }

    public T Read(ref DataReader reader)
    {
        T value = default;
        reader.ReadRawBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1)));
        return value;
    }
}

internal sealed class RawCanonicalValueCodec<T> : IValueCodec<T>
    where T : struct
{
    public static readonly RawCanonicalValueCodec<T> Instance = new();

    public ComponentCodecKind Kind => ComponentCodecKind.RawCanonical;

    public void Write(ref DataWriter writer, in T value)
    {
        ref T mutable = ref Unsafe.AsRef(in value);
        writer.WriteRawBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref mutable, 1)));
    }

    public T Read(ref DataReader reader)
    {
        T value = default;
        reader.ReadRawBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1)));
        return value;
    }
}

internal sealed class CustomValueCodec<T, TCodec> : IValueCodec<T>
    where T : struct
    where TCodec : struct, IComponentCodec<T>
{
    public ComponentCodecKind Kind => ComponentCodecKind.Custom;

    public void Write(ref DataWriter writer, in T value)
    {
        var codec = default(TCodec);
        codec.Write(ref writer, in value);
    }

    public T Read(ref DataReader reader)
    {
        var codec = default(TCodec);
        codec.Read(ref reader, out var value);
        return value;
    }
}

internal sealed class CanonicalValueCodec<T, TCodec> : IValueCodec<T>
    where T : struct
    where TCodec : struct, IComponentCodec<T>
{
    public ComponentCodecKind Kind => ComponentCodecKind.Canonical;

    public void Write(ref DataWriter writer, in T value)
    {
        var codec = default(TCodec);
        codec.Write(ref writer, in value);
    }

    public T Read(ref DataReader reader)
    {
        var codec = default(TCodec);
        codec.Read(ref reader, out T value);
        return value;
    }
}

internal sealed class CustomReferencePatcher<T, TPatcher> : IValuePatcher<T>
    where T : struct
    where TPatcher : struct, IReferencePatcher<T>
{
    public void Remap(ref T value, IReferenceRemapper remapper)
    {
        var patcher = default(TPatcher);
        patcher.Remap(ref value, remapper);
    }
}

internal abstract class SerializationTypeRuntime
{
    protected SerializationTypeRuntime(SerializationTypeEntry entry, Type valueType)
    {
        Entry = entry;
        ValueType = valueType;
    }

    public SerializationTypeEntry Entry { get; }
    public Type ValueType { get; }

    public abstract bool IsPresent(AdmittedWorldWrite admitted, Entity entity);
    internal virtual void CollectSparsePresence(
        AdmittedWorldWrite admitted,
        SparseSerializationPresence destination)
    {
    }
    protected abstract void WriteItemAdmitted(
        ref DataWriter writer,
        AdmittedWorldWrite admitted,
        Entity entity);
    protected abstract void ApplyStream(
        ref DataReader reader,
        World world,
        Entity entity,
        IReferenceRemapper? remapper);

    internal void WriteAdmitted(
        ref DataWriter writer,
        AdmittedWorldWrite admitted,
        Entity entity) =>
        WriteItemAdmitted(ref writer, admitted, entity);

    internal void Apply(
        ref DataReader reader,
        World world,
        Entity entity,
        IReferenceRemapper? remapper) =>
        ApplyStream(ref reader, world, entity, remapper);
}

internal abstract class ValueSerializationRuntime<T> : SerializationTypeRuntime
    where T : struct
{
    protected readonly IValueCodec<T> Codec;
    private readonly IValuePatcher<T>? _patcher;

    protected ValueSerializationRuntime(
        SerializationTypeKey typeKey,
        int runtimeComponentId,
        StoragePath storage,
        SerializationValueKind kind,
        IValueCodec<T> codec,
        IValuePatcher<T>? patcher,
        SerializationSchemaSource schemaSource = SerializationSchemaSource.Explicit)
        : base(
            new SerializationTypeEntry(
                RawAbiTypeKey.Bind<T>(typeKey, storage, kind, codec.Kind),
                runtimeComponentId,
                storage,
                kind,
                codec.Kind,
                schemaSource,
                RuntimeHelpers.IsReferenceOrContainsReferences<T>(),
                containsEntityReferences: patcher is not null),
            typeof(T))
    {
        Codec = codec;
        _patcher = patcher;
    }

    protected void WriteValue(ref DataWriter writer, in T value) => Codec.Write(ref writer, in value);

    internal void WriteStandalone(ref DataWriter writer, in T value) =>
        Codec.Write(ref writer, in value);

    internal T ReadStandalone(ref DataReader reader) => Codec.Read(ref reader);

    protected T ReadValue(ref DataReader reader) => Codec.Read(ref reader);

    protected void RemapValue(ref T value, IReferenceRemapper remapper)
    {
        if (!Entry.ContainsEntityReferences)
            return;

        if (_patcher is null)
        {
            throw new InvalidOperationException(
                $"Type {ValueType.FullName} contains entity references but no reference patcher is registered.");
        }

        _patcher.Remap(ref value, remapper);
    }
}

/// <summary>
/// Independently verifies the source generator's claim that a CLR value layout is byte-for-byte
/// identical to its fixed-width little-endian canonical encoding. This work runs once at
/// registration; verified values retain the allocation-free memcpy codec on the hot path.
/// </summary>
internal static class RawCanonicalLayout
{
    private const string FingerprintDomain = "SomeEngine.ECS.RawCanonicalLayout.v1";

    internal static bool IsVerified<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicFields |
            DynamicallyAccessedMemberTypes.NonPublicFields)] T>(
        int expectedSize,
        ulong expectedFingerprint)
        where T : struct
    {
        if (expectedSize < 0 ||
            expectedFingerprint == 0 ||
            !BitConverter.IsLittleEndian ||
            RuntimeHelpers.IsReferenceOrContainsReferences<T>() ||
            Unsafe.SizeOf<T>() != expectedSize)
        {
            return false;
        }

        Type type = typeof(T);
        StructLayoutAttribute? layout = type.StructLayoutAttribute;
        if (layout is null ||
            layout.Value != LayoutKind.Sequential ||
            layout.Pack != 1)
        {
            return false;
        }

        FieldInfo[] fields = type.GetFields(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly);
        var fieldLayouts = new List<FieldLayout>(fields.Length);
        int nextOffset = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            FieldInfo field = fields[i];
            if (!TryGetFixedWidthPrimitive(field.FieldType, out uint kind, out int size))
                return false;

            // Modern reflection returns declared fields in metadata order. Sequential Pack=1
            // layout uses that same order and cannot introduce alignment padding, so the actual
            // offset is the accumulated fixed-width field size. Avoid Marshal.OffsetOf here:
            // NativeAOT intentionally omits structure-marshalling data for types that are not
            // used by an interop signature, even though their managed layout is fully known.
            if (nextOffset > int.MaxValue - size)
                return false;
            fieldLayouts.Add(new FieldLayout(nextOffset, kind, size));
            nextOffset += size;
        }

        return nextOffset == expectedSize &&
               ComputeFingerprint(
                   expectedSize,
                   CollectionsMarshal.AsSpan(fieldLayouts)) == expectedFingerprint;
    }

    private static bool TryGetFixedWidthPrimitive(Type type, out uint kind, out int size)
    {
        if (type == typeof(byte))
            return Shape(1, 1, out kind, out size);
        if (type == typeof(sbyte))
            return Shape(2, 1, out kind, out size);
        if (type == typeof(short))
            return Shape(3, 2, out kind, out size);
        if (type == typeof(ushort))
            return Shape(4, 2, out kind, out size);
        if (type == typeof(char))
            return Shape(5, 2, out kind, out size);
        if (type == typeof(int))
            return Shape(6, 4, out kind, out size);
        if (type == typeof(uint))
            return Shape(7, 4, out kind, out size);
        if (type == typeof(float))
            return Shape(8, 4, out kind, out size);
        if (type == typeof(long))
            return Shape(9, 8, out kind, out size);
        if (type == typeof(ulong))
            return Shape(10, 8, out kind, out size);
        if (type == typeof(double))
            return Shape(11, 8, out kind, out size);

        kind = 0;
        size = 0;
        return false;
    }

    private static bool Shape(uint valueKind, int valueSize, out uint kind, out int size)
    {
        kind = valueKind;
        size = valueSize;
        return true;
    }

    private static ulong ComputeFingerprint(
        int size,
        ReadOnlySpan<FieldLayout> fields)
    {
        ulong hash = BinaryFieldKey.FromName(FingerprintDomain);
        AddUInt32(ref hash, unchecked((uint)size));
        AddUInt32(ref hash, unchecked((uint)fields.Length));
        for (int i = 0; i < fields.Length; i++)
        {
            FieldLayout field = fields[i];
            AddUInt32(ref hash, field.Kind);
            AddUInt32(ref hash, unchecked((uint)field.Offset));
            AddUInt32(ref hash, unchecked((uint)field.Size));
        }

        return hash == 0 ? 1ul : hash;
    }

    private static void AddUInt32(ref ulong hash, uint value)
    {
        for (int shift = 0; shift < 32; shift += 8)
            hash = (hash ^ (byte)(value >> shift)) * 1099511628211ul;
    }

    private static void AddBytes(ref ulong hash, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            hash = (hash ^ bytes[i]) * 1099511628211ul;
    }

    private readonly struct FieldLayout
    {
        internal FieldLayout(int offset, uint kind, int size)
        {
            Offset = offset;
            Kind = kind;
            Size = size;
        }

        internal int Offset { get; }

        internal uint Kind { get; }

        internal int Size { get; }
    }
}

/// <summary>
/// Binds native-layout codecs to the actual CLR type ABI in addition to the caller's logical
/// serialization key. This prevents an explicit stable key from accidentally making a raw
/// checkpoint portable across rebuilds of the assembly that owns <typeparamref name="T"/>.
/// Canonical and custom codecs retain their caller-supplied durable schema keys unchanged.
/// </summary>
internal static class RawAbiTypeKey
{
    internal static SerializationTypeKey Bind<T>(
        SerializationTypeKey logicalKey,
        StoragePath storage,
        SerializationValueKind kind,
        ComponentCodecKind codecKind)
        where T : struct
    {
        if (codecKind != ComponentCodecKind.Raw)
            return logicalKey;

        Type type = typeof(T);
        StructLayoutAttribute? layout = type.StructLayoutAttribute;
        ulong hash = BinaryFieldKey.FromName("SomeEngine.ECS.RawAbi.v1");
        Span<byte> stableIdBytes = stackalloc byte[16];
        BinaryPrimitiveEncoding.WriteGuid(stableIdBytes, logicalKey.StableId);
        AddBytes(ref hash, stableIdBytes);
        AddString(ref hash, logicalKey.StableName);
        AddUInt64(ref hash, logicalKey.SchemaFingerprint);
        AddString(ref hash, type.FullName ?? type.Name);
        AddBytes(ref hash, type.Module.ModuleVersionId.ToByteArray());
        AddUInt32(ref hash, unchecked((uint)type.MetadataToken));
        AddUInt32(ref hash, unchecked((uint)Unsafe.SizeOf<T>()));
        AddUInt32(ref hash, (uint)storage);
        AddUInt32(ref hash, (uint)kind);
        AddUInt32(ref hash, RuntimeHelpers.IsReferenceOrContainsReferences<T>() ? 1u : 0u);
        AddUInt32(ref hash, unchecked((uint)IntPtr.Size));
        AddUInt32(ref hash, BitConverter.IsLittleEndian ? 1u : 0u);
        AddUInt32(ref hash, layout is null ? uint.MaxValue : (uint)layout.Value);
        AddUInt32(ref hash, layout is null ? 0u : unchecked((uint)layout.Pack));
        AddUInt32(ref hash, layout is null ? 0u : unchecked((uint)layout.Size));

        if (hash == 0)
            hash = 1;
        return new SerializationTypeKey(
            logicalKey.StableId,
            logicalKey.StableName,
            hash);
    }

    private static void AddString(ref ulong hash, string value) =>
        AddBytes(ref hash, SerializationBinary.StrictUtf8.GetBytes(value));

    private static void AddBytes(ref ulong hash, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            hash = (hash ^ bytes[i]) * 1099511628211ul;
    }

    private static void AddUInt32(ref ulong hash, uint value) => AddUInt64(ref hash, value);

    private static void AddUInt64(ref ulong hash, ulong value)
    {
        for (int shift = 0; shift < 64; shift += 8)
            hash = (hash ^ (byte)(value >> shift)) * 1099511628211ul;
    }
}

internal sealed class ComponentSerializationRuntime<T> : ValueSerializationRuntime<T>
    where T : struct, IComponent
{
    public ComponentSerializationRuntime(
        SerializationTypeKey typeKey,
        IValueCodec<T> codec,
        IValuePatcher<T>? patcher,
        SerializationSchemaSource schemaSource = SerializationSchemaSource.Explicit)
        : base(
            typeKey,
            ComponentMetadata<T>.Id,
            StoragePath.Table,
            SerializationValueKind.Component,
            codec,
            patcher,
            schemaSource)
    {
    }

    public override bool IsPresent(AdmittedWorldWrite admitted, Entity entity) =>
        admitted.HasValue<T>(entity);

    protected override void WriteItemAdmitted(
        ref DataWriter writer,
        AdmittedWorldWrite admitted,
        Entity entity)
    {
        if (ComponentMetadata<T>.IsEnableable)
        {
            writer.WriteBoolean(
                admitted.IsValueEnabled(entity, ComponentMetadata<T>.Id));
        }

        ref readonly T component = ref admitted.ReadValue<T>(entity);
        WriteValue(ref writer, in component);
    }

    protected override void ApplyStream(
        ref DataReader reader,
        World world,
        Entity entity,
        IReferenceRemapper? remapper)
    {
        bool isEnableable = ComponentMetadata<T>.IsEnableable;
        bool enabled = isEnableable && reader.ReadBoolean();
        T component = ReadValue(ref reader);
        if (remapper is not null)
            RemapValue(ref component, remapper);

        if (world.Has<T>(entity))
            world.Replace(entity, in component);
        else
            world.Add(entity, in component);

        if (ComponentMetadata<T>.IsEnableable)
            world.WriteEnabledId(entity, ComponentMetadata<T>.Id, enabled);
    }

}

internal sealed class TagSerializationRuntime<T> : SerializationTypeRuntime
    where T : struct, ITag
{
    public TagSerializationRuntime(
        SerializationTypeKey typeKey,
        SerializationSchemaSource schemaSource = SerializationSchemaSource.Explicit)
        : base(
            new SerializationTypeEntry(
                typeKey,
                ComponentMetadata<T>.Id,
                StoragePath.Tag,
                SerializationValueKind.Tag,
                ComponentCodecKind.Missing,
                schemaSource,
                containsReferences: false,
                containsEntityReferences: false),
            typeof(T))
    {
    }

    public override bool IsPresent(AdmittedWorldWrite admitted, Entity entity) =>
        admitted.HasValue<T>(entity);
    protected override void WriteItemAdmitted(
        ref DataWriter writer,
        AdmittedWorldWrite admitted,
        Entity entity)
    {
    }

    protected override void ApplyStream(
        ref DataReader reader,
        World world,
        Entity entity,
        IReferenceRemapper? remapper)
    {
        if (!world.Has<T>(entity))
            world.AddTag<T>(entity);
    }

}

internal sealed class SharedSerializationRuntime<T> : ValueSerializationRuntime<T>
    where T : struct, ISharedComponent
{
    public SharedSerializationRuntime(
        SerializationTypeKey typeKey,
        IValueCodec<T> codec,
        IValuePatcher<T>? patcher,
        SerializationSchemaSource schemaSource = SerializationSchemaSource.Explicit)
        : base(
            typeKey,
            ComponentMetadata<T>.Id,
            StoragePath.Shared,
            SerializationValueKind.Shared,
            codec,
            patcher,
            schemaSource)
    {
    }

    public override bool IsPresent(AdmittedWorldWrite admitted, Entity entity) =>
        admitted.HasShared<T>(entity);

    protected override void WriteItemAdmitted(
        ref DataWriter writer,
        AdmittedWorldWrite admitted,
        Entity entity)
    {
        ref readonly T shared = ref admitted.ReadShared<T>(entity);
        WriteValue(ref writer, in shared);
    }

    protected override void ApplyStream(
        ref DataReader reader,
        World world,
        Entity entity,
        IReferenceRemapper? remapper)
    {
        T shared = ReadValue(ref reader);
        if (remapper is not null)
            RemapValue(ref shared, remapper);

        world.MergeShared(entity, in shared);
    }

}

internal sealed class SparseSerializationRuntime<T> : ValueSerializationRuntime<T>
    where T : struct, ISparseComponent
{
    public SparseSerializationRuntime(
        SerializationTypeKey typeKey,
        IValueCodec<T> codec,
        IValuePatcher<T>? patcher,
        SerializationSchemaSource schemaSource = SerializationSchemaSource.Explicit)
        : base(
            typeKey,
            ComponentMetadata<T>.Id,
            StoragePath.Sparse,
            SerializationValueKind.Sparse,
            codec,
            patcher,
            schemaSource)
    {
    }

    public override bool IsPresent(AdmittedWorldWrite admitted, Entity entity) =>
        admitted.HasSparse<T>(entity);

    internal override void CollectSparsePresence(
        AdmittedWorldWrite admitted,
        SparseSerializationPresence destination)
    {
        if (admitted.TrySparseSet<T>(out SomeEngine.ECS.Sparse.SparseSet<T>? sparseSet))
            destination.Add(this, sparseSet.DenseEntities);
    }

    protected override void WriteItemAdmitted(
        ref DataWriter writer,
        AdmittedWorldWrite admitted,
        Entity entity)
    {
        ref readonly T sparse = ref admitted.ReadSparse<T>(entity);
        WriteValue(ref writer, in sparse);
    }

    protected override void ApplyStream(
        ref DataReader reader,
        World world,
        Entity entity,
        IReferenceRemapper? remapper)
    {
        T sparse = ReadValue(ref reader);
        if (remapper is not null)
            RemapValue(ref sparse, remapper);

        if (world.HasSparse<T>(entity))
            world.ReplaceSparse(entity, in sparse);
        else
            world.AddSparse(entity, in sparse);
    }

}

internal sealed class BufferSerializationRuntime<T> : ValueSerializationRuntime<T>
    where T : struct, IBufferElement
{
    public BufferSerializationRuntime(
        SerializationTypeKey typeKey,
        IValueCodec<T> codec,
        IValuePatcher<T>? patcher,
        SerializationSchemaSource schemaSource = SerializationSchemaSource.Explicit)
        : base(
            typeKey,
            BufferComponents.Header<T>(),
            StoragePath.Table,
            SerializationValueKind.Buffer,
            codec,
            patcher,
            schemaSource)
    {
    }

    public override bool IsPresent(AdmittedWorldWrite admitted, Entity entity) =>
        admitted.HasBuffer<T>(entity);

    protected override void WriteItemAdmitted(
        ref DataWriter writer,
        AdmittedWorldWrite admitted,
        Entity entity)
    {
        BufferView<T> buffer = admitted.BorrowBuffer<T>(entity);
        ReadOnlySpan<T> values = buffer.AsSpan();
        writer.WriteInt32(values.Length);
        for (int i = 0; i < values.Length; i++)
            WriteValue(ref writer, in values[i]);
    }

    protected override void ApplyStream(
        ref DataReader reader,
        World world,
        Entity entity,
        IReferenceRemapper? remapper)
    {
        int count = reader.ReadBufferElementCount<T>();

        bool hadBuffer = world.HasBuffer<T>(entity);
        if (!hadBuffer)
            world.AddBuffer<T>(entity);

        var state = new BufferApplyState(
            this,
            reader.Reader,
            reader.Budget,
            count,
            remapper,
            preserveAddVersion: !hadBuffer);
        world.ExecuteBufferWrite<T, BufferApplyState>(
            entity,
            ref state,
            static (DynamicBuffer<T> buffer, ref BufferApplyState apply) =>
            {
                Span<T> destination = apply.PreserveAddVersion
                    ? buffer.LoadUninitialized(apply.Count)
                    : buffer.ReplaceWithUninitialized(apply.Count);
                var itemReader = new DataReader(apply.Reader, apply.Budget);
                apply.Runtime.ReadValuesDirect(
                    ref itemReader,
                    destination,
                    apply.Remapper);
            });
    }

    private void ReadValuesDirect(
        ref DataReader reader,
        Span<T> destination,
        IReferenceRemapper? remapper)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            T value = ReadValue(ref reader);
            if (remapper is not null)
                RemapValue(ref value, remapper);
            destination[i] = value;
        }
    }

    private readonly struct BufferApplyState
    {
        internal BufferApplyState(
            BufferSerializationRuntime<T> runtime,
            BinaryReader reader,
            SerializationReadBudget? budget,
            int count,
            IReferenceRemapper? remapper,
            bool preserveAddVersion)
        {
            Runtime = runtime;
            Reader = reader;
            Budget = budget;
            Count = count;
            Remapper = remapper;
            PreserveAddVersion = preserveAddVersion;
        }

        internal BufferSerializationRuntime<T> Runtime { get; }

        internal BinaryReader Reader { get; }

        internal SerializationReadBudget? Budget { get; }

        internal int Count { get; }

        internal IReferenceRemapper? Remapper { get; }

        internal bool PreserveAddVersion { get; }
    }
}

