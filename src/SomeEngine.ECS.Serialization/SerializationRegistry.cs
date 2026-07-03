using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Serialization;

public sealed partial class SerializationRegistry
{
    private readonly List<SerializationTypeRuntime> _runtimes = new();
    private readonly Dictionary<Guid, SerializationTypeRuntime> _byStableId = new();
    private readonly Dictionary<Type, SerializationTypeRuntime> _byType = new();
    private readonly Dictionary<MigrationKey, IMigrationStep> _migrations = new();

    public IReadOnlyList<SerializationTypeEntry> Entries =>
        _runtimes.Select(static runtime => runtime.Entry).ToArray();

    public SerializationRegistry Register<T>()
        where T : struct, IComponent =>
        Register<T>(CreateTypeKey<T>(ComponentMetadata<T>.Storage, SerializationValueKind.Component));

    public SerializationRegistry Register<T>(SerializationTypeKey typeKey)
        where T : struct, IComponent =>
        Register(new ComponentSerializationRuntime<T>(typeKey, CreateDefaultCodec<T>(), patcher: null));

    public SerializationRegistry Register<T, TCodec>()
        where T : struct, IComponent
        where TCodec : struct, IComponentCodec<T> =>
        Register<T, TCodec>(CreateTypeKey<T>(ComponentMetadata<T>.Storage, SerializationValueKind.Component));

    public SerializationRegistry Register<T, TCodec>(SerializationTypeKey typeKey)
        where T : struct, IComponent
        where TCodec : struct, IComponentCodec<T> =>
        Register(new ComponentSerializationRuntime<T>(typeKey, new CustomValueCodec<T, TCodec>(), patcher: null));

    public SerializationRegistry Register<T, TCodec, TPatcher>()
        where T : struct, IComponent
        where TCodec : struct, IComponentCodec<T>
        where TPatcher : struct, IReferencePatcher<T> =>
        Register<T, TCodec, TPatcher>(CreateTypeKey<T>(ComponentMetadata<T>.Storage, SerializationValueKind.Component));

    public SerializationRegistry Register<T, TCodec, TPatcher>(SerializationTypeKey typeKey)
        where T : struct, IComponent
        where TCodec : struct, IComponentCodec<T>
        where TPatcher : struct, IReferencePatcher<T> =>
        Register(new ComponentSerializationRuntime<T>(
            typeKey,
            new CustomValueCodec<T, TCodec>(),
            new CustomReferencePatcher<T, TPatcher>()));

    public SerializationRegistry RegisterTag<T>()
        where T : struct, ITag =>
        RegisterTag<T>(CreateTypeKey<T>(StoragePath.Tag, SerializationValueKind.Tag));

    public SerializationRegistry RegisterTag<T>(SerializationTypeKey typeKey)
        where T : struct, ITag =>
        Register(new TagSerializationRuntime<T>(typeKey));

    public SerializationRegistry RegisterShared<T>()
        where T : struct, ISharedComponent =>
        RegisterShared<T>(CreateTypeKey<T>(StoragePath.Shared, SerializationValueKind.Shared));

    public SerializationRegistry RegisterShared<T>(SerializationTypeKey typeKey)
        where T : struct, ISharedComponent =>
        Register(new SharedSerializationRuntime<T>(typeKey, CreateDefaultCodec<T>(), patcher: null));

    public SerializationRegistry RegisterShared<T, TCodec>()
        where T : struct, ISharedComponent
        where TCodec : struct, IComponentCodec<T> =>
        RegisterShared<T, TCodec>(CreateTypeKey<T>(StoragePath.Shared, SerializationValueKind.Shared));

    public SerializationRegistry RegisterShared<T, TCodec>(SerializationTypeKey typeKey)
        where T : struct, ISharedComponent
        where TCodec : struct, IComponentCodec<T> =>
        Register(new SharedSerializationRuntime<T>(typeKey, new CustomValueCodec<T, TCodec>(), patcher: null));

    public SerializationRegistry RegisterShared<T, TCodec, TPatcher>()
        where T : struct, ISharedComponent
        where TCodec : struct, IComponentCodec<T>
        where TPatcher : struct, IReferencePatcher<T> =>
        RegisterShared<T, TCodec, TPatcher>(CreateTypeKey<T>(StoragePath.Shared, SerializationValueKind.Shared));

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
        RegisterBuffer<T>(CreateTypeKey<T>(StoragePath.Table, SerializationValueKind.Buffer));

    public SerializationRegistry RegisterBuffer<T>(SerializationTypeKey typeKey)
        where T : struct, IBufferElement =>
        Register(new BufferSerializationRuntime<T>(typeKey, CreateDefaultCodec<T>(), patcher: null));

    public SerializationRegistry RegisterBuffer<T, TCodec>()
        where T : struct, IBufferElement
        where TCodec : struct, IComponentCodec<T> =>
        RegisterBuffer<T, TCodec>(CreateTypeKey<T>(StoragePath.Table, SerializationValueKind.Buffer));

    public SerializationRegistry RegisterBuffer<T, TCodec>(SerializationTypeKey typeKey)
        where T : struct, IBufferElement
        where TCodec : struct, IComponentCodec<T> =>
        Register(new BufferSerializationRuntime<T>(typeKey, new CustomValueCodec<T, TCodec>(), patcher: null));

    public SerializationRegistry RegisterBuffer<T, TCodec, TPatcher>()
        where T : struct, IBufferElement
        where TCodec : struct, IComponentCodec<T>
        where TPatcher : struct, IReferencePatcher<T> =>
        RegisterBuffer<T, TCodec, TPatcher>(CreateTypeKey<T>(StoragePath.Table, SerializationValueKind.Buffer));

    public SerializationRegistry RegisterBuffer<T, TCodec, TPatcher>(SerializationTypeKey typeKey)
        where T : struct, IBufferElement
        where TCodec : struct, IComponentCodec<T>
        where TPatcher : struct, IReferencePatcher<T> =>
        Register(new BufferSerializationRuntime<T>(
            typeKey,
            new CustomValueCodec<T, TCodec>(),
            new CustomReferencePatcher<T, TPatcher>()));
}

public sealed partial class SerializationRegistry
{
    public SerializationRegistry RegisterSparse<T>()
        where T : struct, ISparseComponent =>
        RegisterSparse<T>(CreateTypeKey<T>(StoragePath.Sparse, SerializationValueKind.Sparse));

    public SerializationRegistry RegisterSparse<T>(SerializationTypeKey typeKey)
        where T : struct, ISparseComponent =>
        Register(new SparseSerializationRuntime<T>(typeKey, CreateDefaultCodec<T>(), patcher: null));

    public SerializationRegistry RegisterSparse<T, TCodec>()
        where T : struct, ISparseComponent
        where TCodec : struct, IComponentCodec<T> =>
        RegisterSparse<T, TCodec>(CreateTypeKey<T>(StoragePath.Sparse, SerializationValueKind.Sparse));

    public SerializationRegistry RegisterSparse<T, TCodec>(SerializationTypeKey typeKey)
        where T : struct, ISparseComponent
        where TCodec : struct, IComponentCodec<T> =>
        Register(new SparseSerializationRuntime<T>(typeKey, new CustomValueCodec<T, TCodec>(), patcher: null));

    public SerializationRegistry RegisterSparse<T, TCodec, TPatcher>()
        where T : struct, ISparseComponent
        where TCodec : struct, IComponentCodec<T>
        where TPatcher : struct, IReferencePatcher<T> =>
        RegisterSparse<T, TCodec, TPatcher>(CreateTypeKey<T>(StoragePath.Sparse, SerializationValueKind.Sparse));

    public SerializationRegistry RegisterSparse<T, TCodec, TPatcher>(SerializationTypeKey typeKey)
        where T : struct, ISparseComponent
        where TCodec : struct, IComponentCodec<T>
        where TPatcher : struct, IReferencePatcher<T> =>
        Register(new SparseSerializationRuntime<T>(
            typeKey,
            new CustomValueCodec<T, TCodec>(),
            new CustomReferencePatcher<T, TPatcher>()));

    public SerializationRegistry RegisterRelation<T>()
        where T : struct, IRelation =>
        RegisterRelation<T>(CreateTypeKey<T>(ComponentMetadata<T>.Storage, SerializationValueKind.Relation));

    public SerializationRegistry RegisterRelation<T>(SerializationTypeKey typeKey)
        where T : struct, IRelation =>
        Register(new RelationSerializationRuntime<T>(typeKey, CreateDefaultCodec<T>(), patcher: null));

    public SerializationRegistry RegisterRelation<T, TCodec>()
        where T : struct, IRelation
        where TCodec : struct, IComponentCodec<T> =>
        RegisterRelation<T, TCodec>(CreateTypeKey<T>(ComponentMetadata<T>.Storage, SerializationValueKind.Relation));

    public SerializationRegistry RegisterRelation<T, TCodec>(SerializationTypeKey typeKey)
        where T : struct, IRelation
        where TCodec : struct, IComponentCodec<T> =>
        Register(new RelationSerializationRuntime<T>(typeKey, new CustomValueCodec<T, TCodec>(), patcher: null));

    public SerializationRegistry RegisterRelation<T, TCodec, TPatcher>()
        where T : struct, IRelation
        where TCodec : struct, IComponentCodec<T>
        where TPatcher : struct, IReferencePatcher<T> =>
        RegisterRelation<T, TCodec, TPatcher>(CreateTypeKey<T>(ComponentMetadata<T>.Storage, SerializationValueKind.Relation));

    public SerializationRegistry RegisterRelation<T, TCodec, TPatcher>(SerializationTypeKey typeKey)
        where T : struct, IRelation
        where TCodec : struct, IComponentCodec<T>
        where TPatcher : struct, IReferencePatcher<T> =>
        Register(new RelationSerializationRuntime<T>(
            typeKey,
            new CustomValueCodec<T, TCodec>(),
            new CustomReferencePatcher<T, TPatcher>()));
}

public sealed partial class SerializationRegistry
{
    public SerializationRegistry RegisterMigration(IMigrationStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var key = new MigrationKey(step.From.StableId, step.From.SchemaHash, step.To.SchemaHash);
        if (!_migrations.TryAdd(key, step))
            throw new InvalidOperationException($"Migration from 0x{step.From.SchemaHash:X8} to 0x{step.To.SchemaHash:X8} for {step.From.StableId} is already registered.");

        return this;
    }

    internal IReadOnlyList<SerializationTypeRuntime> RuntimeTypes => _runtimes;

    internal SerializationTypeRuntime GetRegistered<T>()
        where T : struct
    {
        if (_byType.TryGetValue(typeof(T), out var runtime))
            return runtime;

        throw new InvalidOperationException($"Type {typeof(T).FullName} is not registered for serialization.");
    }

    internal SerializationTypeRuntime? Resolve(
        SerializationTypeKey fileKey,
        UnknownTypeMode unknownTypeMode,
        SchemaMismatchMode schemaMismatchMode,
        out IMigrationStep? migration)
    {
        migration = null;
        if (!_byStableId.TryGetValue(fileKey.StableId, out var runtime))
        {
            if (unknownTypeMode == UnknownTypeMode.Skip)
                return null;

            throw new InvalidDataException(
                $"Unknown serialized type '{fileKey.StableName}' ({fileKey.StableId}).");
        }

        if (runtime.Entry.TypeKey.SchemaHash != fileKey.SchemaHash)
        {
            if (schemaMismatchMode == SchemaMismatchMode.UseRegisteredMigration)
            {
                var localKey = runtime.Entry.TypeKey;
                var migrationKey = new MigrationKey(fileKey.StableId, fileKey.SchemaHash, localKey.SchemaHash);
                if (_migrations.TryGetValue(migrationKey, out migration) &&
                    migration.To.StableId == localKey.StableId &&
                    migration.To.SchemaHash == localKey.SchemaHash)
                {
                    return runtime;
                }

                throw new InvalidDataException(
                    $"No registered migration for '{fileKey.StableName}': file=0x{fileKey.SchemaHash:X8}, local=0x{localKey.SchemaHash:X8}.");
            }

            if (schemaMismatchMode == SchemaMismatchMode.BestEffortAdditive)
                throw new NotSupportedException("Best-effort additive schema migration requires generated field defaults and is not enabled without a registered migration.");

            throw new InvalidDataException(
                $"Schema mismatch for '{fileKey.StableName}': file=0x{fileKey.SchemaHash:X8}, local=0x{runtime.Entry.TypeKey.SchemaHash:X8}.");
        }

        return runtime;
    }

    internal SerializationTypeRuntime ResolveExact(SerializationTypeKey fileKey)
    {
        return Resolve(fileKey, UnknownTypeMode.Throw, SchemaMismatchMode.Throw, out _)!;
    }

    private SerializationRegistry Register(SerializationTypeRuntime runtime)
    {
        SerializationTypeKey typeKey = runtime.Entry.TypeKey;

        if (_byStableId.ContainsKey(typeKey.StableId))
            throw new InvalidOperationException($"Serialization stable id {typeKey.StableId} is already registered.");

        if (_byType.ContainsKey(runtime.ValueType))
            throw new InvalidOperationException($"Type {runtime.ValueType.FullName} is already registered for serialization.");

        _runtimes.Add(runtime);
        _runtimes.Sort(static (left, right) => CompareTypeKeys(left.Entry.TypeKey, right.Entry.TypeKey));
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

    private static IValueCodec<T> CreateDefaultCodec<T>()
        where T : struct
    {
        return RuntimeHelpers.IsReferenceOrContainsReferences<T>()
            ? MissingValueCodec<T>.Instance
            : RawValueCodec<T>.Instance;
    }

    private static SerializationTypeKey CreateTypeKey<T>(StoragePath storage, SerializationValueKind kind)
        where T : struct
    {
        var type = typeof(T);
        var attr = (SerializableComponentAttribute?)Attribute.GetCustomAttribute(
            type,
            typeof(SerializableComponentAttribute));
        string stableName = type.FullName ?? type.Name;
        uint schemaHash = attr is { SchemaHash: not 0 }
            ? attr.SchemaHash
            : ComputeSchemaHash<T>(stableName, storage, kind);
        Guid stableId = attr?.StableId ?? CreateDeterministicGuid(stableName);
        return new SerializationTypeKey(stableId, stableName, schemaHash);
    }

    private static Guid CreateDeterministicGuid(string stableName)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes("SomeEngine.ECS.Serialization:" + stableName), hash);
        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);
        return new Guid(guidBytes);
    }

    private static uint ComputeSchemaHash<T>(string stableName, StoragePath storage, SerializationValueKind kind)
        where T : struct
    {
        uint hash = 2166136261u;
        foreach (byte b in Encoding.UTF8.GetBytes(stableName))
            hash = (hash ^ b) * 16777619u;
        hash = (hash ^ (uint)Unsafe.SizeOf<T>()) * 16777619u;
        hash = (hash ^ (uint)storage) * 16777619u;
        hash = (hash ^ (uint)kind) * 16777619u;
        hash = (hash ^ (RuntimeHelpers.IsReferenceOrContainsReferences<T>() ? 1u : 0u)) * 16777619u;
        return hash;
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

internal readonly record struct MigrationKey(Guid StableId, uint FromSchemaHash, uint ToSchemaHash);

internal readonly record struct ComponentItemValue<T>(T Value, bool Enabled)
    where T : struct;

internal abstract class SerializationTypeRuntime
{
    protected SerializationTypeRuntime(SerializationTypeEntry entry, Type valueType)
    {
        Entry = entry;
        ValueType = valueType;
        Item = new ItemPort(this);
        Bundle = new BundlePort(this);
        Component = new ComponentPort(this);
    }

    public SerializationTypeEntry Entry { get; }
    public Type ValueType { get; }
    public ItemPort Item { get; }
    public BundlePort Bundle { get; }
    public ComponentPort Component { get; }

    public abstract bool IsPresent(World world, Entity entity);
    protected abstract object Capture(World world, Entity entity);
    protected abstract void Write(ref DataWriter writer, object value);
    protected abstract object Read(ref DataReader reader);
    protected abstract void AddIds(List<int> componentIds);
    protected abstract void Spawn(ref DataReader reader, World world, BundleWriter writer);
    protected abstract void ApplyStream(
        ref DataReader reader,
        World world,
        Entity entity,
        EntityApplyMode mode,
        IReferenceRemapper? remapper);
    protected abstract object Remap(object value, IReferenceRemapper remapper);
    protected abstract void Apply(object value, World world, Entity entity, EntityApplyMode mode);
    public abstract void Remove(World world, Entity entity);
    protected abstract void WriteComponent(ref DataWriter writer, object value);
    protected abstract object ReadComponent(ref DataReader reader);

    protected virtual SharedValueSlot ReadShared(ref DataReader reader, World world) =>
        throw new InvalidOperationException($"Type {ValueType.FullName} is not a shared component.");

    public sealed class ItemPort
    {
        private readonly SerializationTypeRuntime _runtime;

        internal ItemPort(SerializationTypeRuntime runtime) => _runtime = runtime;

        public object Capture(World world, Entity entity) => _runtime.Capture(world, entity);

        public void Write(ref DataWriter writer, object value) => _runtime.Write(ref writer, value);

        public object Read(ref DataReader reader) => _runtime.Read(ref reader);

        public object Remap(object value, IReferenceRemapper remapper) => _runtime.Remap(value, remapper);

        public void Apply(object value, World world, Entity entity, EntityApplyMode mode) =>
            _runtime.Apply(value, world, entity, mode);

        public void ApplyStream(
            ref DataReader reader,
            World world,
            Entity entity,
            EntityApplyMode mode,
            IReferenceRemapper? remapper) =>
            _runtime.ApplyStream(ref reader, world, entity, mode, remapper);
    }

    public sealed class BundlePort
    {
        private readonly SerializationTypeRuntime _runtime;

        internal BundlePort(SerializationTypeRuntime runtime) => _runtime = runtime;

        public void AddIds(List<int> componentIds) => _runtime.AddIds(componentIds);

        public void Spawn(ref DataReader reader, World world, BundleWriter writer) =>
            _runtime.Spawn(ref reader, world, writer);

        public SharedValueSlot ReadShared(ref DataReader reader, World world) =>
            _runtime.ReadShared(ref reader, world);
    }

    public sealed class ComponentPort
    {
        private readonly SerializationTypeRuntime _runtime;

        internal ComponentPort(SerializationTypeRuntime runtime) => _runtime = runtime;

        public void Write(ref DataWriter writer, object value) => _runtime.WriteComponent(ref writer, value);

        public object Read(ref DataReader reader) => _runtime.ReadComponent(ref reader);
    }
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
        IValuePatcher<T>? patcher)
        : base(
            new SerializationTypeEntry(
                typeKey,
                runtimeComponentId,
                storage,
                kind,
                codec.Kind,
                RuntimeHelpers.IsReferenceOrContainsReferences<T>(),
                containsEntityReferences: patcher is not null),
            typeof(T))
    {
        Codec = codec;
        _patcher = patcher;
    }

    protected override void WriteComponent(ref DataWriter writer, object value)
    {
        if (value is not T typed)
            throw new ArgumentException($"Expected value of type {typeof(T).FullName}.", nameof(value));

        Codec.Write(ref writer, in typed);
    }

    protected override object ReadComponent(ref DataReader reader) => Codec.Read(ref reader);

    protected void WriteValue(ref DataWriter writer, in T value) => Codec.Write(ref writer, in value);

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

internal sealed class ComponentSerializationRuntime<T> : ValueSerializationRuntime<T>
    where T : struct, IComponent
{
    public ComponentSerializationRuntime(
        SerializationTypeKey typeKey,
        IValueCodec<T> codec,
        IValuePatcher<T>? patcher)
        : base(typeKey, ComponentMetadata<T>.Id, StoragePath.Table, SerializationValueKind.Component, codec, patcher)
    {
    }

    public override bool IsPresent(World world, Entity entity) => world.Has<T>(entity);

    protected override object Capture(World world, Entity entity)
    {
        bool enabled = ComponentMetadata<T>.IsEnableable &&
                       world.IsEnabledId(entity, ComponentMetadata<T>.Id);
        return new ComponentItemValue<T>(world.Read<T>(entity), enabled);
    }

    protected override void Write(ref DataWriter writer, object value)
    {
        var item = (ComponentItemValue<T>)value;
        if (ComponentMetadata<T>.IsEnableable)
            writer.WriteBoolean(item.Enabled);

        T component = item.Value;
        WriteValue(ref writer, in component);
    }

    protected override object Read(ref DataReader reader)
    {
        bool isEnableable = ComponentMetadata<T>.IsEnableable;
        bool enabled = isEnableable && reader.ReadBoolean();
        return new ComponentItemValue<T>(ReadValue(ref reader), enabled);
    }

    protected override void AddIds(List<int> componentIds) => componentIds.Add(ComponentMetadata<T>.Id);

    protected override void Spawn(ref DataReader reader, World world, BundleWriter writer)
    {
        bool isEnableable = ComponentMetadata<T>.IsEnableable;
        bool enabled = isEnableable && reader.ReadBoolean();
        T component = ReadValue(ref reader);
        writer.Write(component);

        if (isEnableable)
            world.WriteEnabledId(writer.Entity, ComponentMetadata<T>.Id, enabled);
    }

    protected override void ApplyStream(
        ref DataReader reader,
        World world,
        Entity entity,
        EntityApplyMode mode,
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

    protected override object Remap(object value, IReferenceRemapper remapper)
    {
        var item = (ComponentItemValue<T>)value;
        T component = item.Value;
        RemapValue(ref component, remapper);
        return new ComponentItemValue<T>(component, item.Enabled);
    }

    protected override void Apply(object value, World world, Entity entity, EntityApplyMode mode)
    {
        var item = (ComponentItemValue<T>)value;
        T component = item.Value;
        if (world.Has<T>(entity))
            world.Replace(entity, in component);
        else
            world.Add(entity, in component);

        if (ComponentMetadata<T>.IsEnableable)
            world.WriteEnabledId(entity, ComponentMetadata<T>.Id, item.Enabled);
    }

    public override void Remove(World world, Entity entity)
    {
        if (world.Has<T>(entity))
            world.Remove<T>(entity);
    }
}

internal sealed class TagSerializationRuntime<T> : SerializationTypeRuntime
    where T : struct, ITag
{
    public TagSerializationRuntime(SerializationTypeKey typeKey)
        : base(
            new SerializationTypeEntry(
                typeKey,
                ComponentMetadata<T>.Id,
                StoragePath.Tag,
                SerializationValueKind.Tag,
                ComponentCodecKind.Missing,
                containsReferences: false,
                containsEntityReferences: false),
            typeof(T))
    {
    }

    public override bool IsPresent(World world, Entity entity) => world.Has<T>(entity);
    protected override object Capture(World world, Entity entity) => default(T);
    protected override void Write(ref DataWriter writer, object value) { }

    protected override object Read(ref DataReader reader) => default(T);

    protected override void AddIds(List<int> componentIds) => componentIds.Add(ComponentMetadata<T>.Id);

    protected override void Spawn(ref DataReader reader, World world, BundleWriter writer)
    {
    }

    protected override void ApplyStream(
        ref DataReader reader,
        World world,
        Entity entity,
        EntityApplyMode mode,
        IReferenceRemapper? remapper)
    {
        if (!world.Has<T>(entity))
            world.AddTag<T>(entity);
    }

    protected override object Remap(object value, IReferenceRemapper remapper) => value;

    protected override void Apply(object value, World world, Entity entity, EntityApplyMode mode)
    {
        if (!world.Has<T>(entity))
            world.AddTag<T>(entity);
    }

    public override void Remove(World world, Entity entity)
    {
        if (world.Has<T>(entity))
            world.RemoveTag<T>(entity);
    }

    protected override void WriteComponent(ref DataWriter writer, object value) =>
        throw new InvalidOperationException("Tags do not have standalone values.");

    protected override object ReadComponent(ref DataReader reader) => default(T);
}

internal sealed class SharedSerializationRuntime<T> : ValueSerializationRuntime<T>
    where T : struct, ISharedComponent
{
    public SharedSerializationRuntime(
        SerializationTypeKey typeKey,
        IValueCodec<T> codec,
        IValuePatcher<T>? patcher)
        : base(typeKey, ComponentMetadata<T>.Id, StoragePath.Shared, SerializationValueKind.Shared, codec, patcher)
    {
    }

    public override bool IsPresent(World world, Entity entity) => world.HasShared<T>(entity);

    protected override object Capture(World world, Entity entity) => world.GetShared<T>(entity);

    protected override void Write(ref DataWriter writer, object value)
    {
        var shared = (T)value;
        WriteValue(ref writer, in shared);
    }

    protected override object Read(ref DataReader reader) => ReadValue(ref reader);

    protected override void AddIds(List<int> componentIds) => componentIds.Add(ComponentMetadata<T>.Id);

    protected override void Spawn(ref DataReader reader, World world, BundleWriter writer)
    {
    }

    protected override SharedValueSlot ReadShared(ref DataReader reader, World world)
    {
        T shared = ReadValue(ref reader);
        int componentId = ComponentMetadata<T>.Id;
        int sharedIndex = world.Shared.AddIndex(componentId, shared);
        return new SharedValueSlot(componentId, sharedIndex);
    }

    protected override void ApplyStream(
        ref DataReader reader,
        World world,
        Entity entity,
        EntityApplyMode mode,
        IReferenceRemapper? remapper)
    {
        T shared = ReadValue(ref reader);
        if (remapper is not null)
            RemapValue(ref shared, remapper);

        world.MergeShared(entity, in shared);
    }

    protected override object Remap(object value, IReferenceRemapper remapper)
    {
        var shared = (T)value;
        RemapValue(ref shared, remapper);
        return shared;
    }

    protected override void Apply(object value, World world, Entity entity, EntityApplyMode mode)
    {
        var shared = (T)value;
        world.MergeShared(entity, in shared);
    }

    public override void Remove(World world, Entity entity)
    {
        if (world.HasShared<T>(entity))
            world.RemoveShared<T>(entity);
    }
}

internal sealed class SparseSerializationRuntime<T> : ValueSerializationRuntime<T>
    where T : struct, ISparseComponent
{
    public SparseSerializationRuntime(
        SerializationTypeKey typeKey,
        IValueCodec<T> codec,
        IValuePatcher<T>? patcher)
        : base(typeKey, ComponentMetadata<T>.Id, StoragePath.Sparse, SerializationValueKind.Sparse, codec, patcher)
    {
    }

    public override bool IsPresent(World world, Entity entity) => world.HasSparse<T>(entity);

    protected override object Capture(World world, Entity entity) => world.GetSparse<T>(entity);

    protected override void Write(ref DataWriter writer, object value)
    {
        var sparse = (T)value;
        WriteValue(ref writer, in sparse);
    }

    protected override object Read(ref DataReader reader) => ReadValue(ref reader);

    protected override void AddIds(List<int> componentIds)
    {
    }

    protected override void Spawn(ref DataReader reader, World world, BundleWriter writer)
    {
        T sparse = ReadValue(ref reader);
        writer.WriteSparse(sparse);
    }

    protected override void ApplyStream(
        ref DataReader reader,
        World world,
        Entity entity,
        EntityApplyMode mode,
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

    protected override object Remap(object value, IReferenceRemapper remapper)
    {
        var sparse = (T)value;
        RemapValue(ref sparse, remapper);
        return sparse;
    }

    protected override void Apply(object value, World world, Entity entity, EntityApplyMode mode)
    {
        var sparse = (T)value;
        if (world.HasSparse<T>(entity))
            world.ReplaceSparse(entity, in sparse);
        else
            world.AddSparse(entity, in sparse);
    }

    public override void Remove(World world, Entity entity)
    {
        if (world.HasSparse<T>(entity))
            world.RemoveSparse<T>(entity);
    }
}

internal sealed class BufferSerializationRuntime<T> : ValueSerializationRuntime<T>
    where T : struct, IBufferElement
{
    public BufferSerializationRuntime(
        SerializationTypeKey typeKey,
        IValueCodec<T> codec,
        IValuePatcher<T>? patcher)
        : base(typeKey, BufferComponents.Header<T>(), StoragePath.Table, SerializationValueKind.Buffer, codec, patcher)
    {
    }

    public override bool IsPresent(World world, Entity entity) => world.HasBuffer<T>(entity);

    protected override object Capture(World world, Entity entity)
    {
        var span = world.GetBuffer<T>(entity).AsSpan();
        return span.ToArray();
    }

    protected override void Write(ref DataWriter writer, object value)
    {
        var values = (T[])value;
        writer.WriteInt32(values.Length);
        for (int i = 0; i < values.Length; i++)
            WriteValue(ref writer, in values[i]);
    }

    protected override object Read(ref DataReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0)
            throw new InvalidDataException("Negative buffer element count.");

        T[] values = new T[count];
        for (int i = 0; i < values.Length; i++)
            values[i] = ReadValue(ref reader);

        return values;
    }

    protected override void AddIds(List<int> componentIds)
    {
        componentIds.Add(BufferComponents.Header<T>());
        componentIds.Add(BufferComponents.Inline<T>());
    }

    protected override void Spawn(ref DataReader reader, World world, BundleWriter writer)
    {
        int count = reader.ReadInt32();
        if (count < 0)
            throw new InvalidDataException("Negative buffer element count.");

        writer.Write(DynamicBufferHeader<T>.Create());
        writer.Write(default(DynamicBufferInline<T>));

        DynamicBuffer<T> buffer = world.GetBuffer<T>(writer.Entity);
        var values = buffer.LoadUninitialized(count);
        for (int i = 0; i < values.Length; i++)
            values[i] = ReadValue(ref reader);
    }

    protected override void ApplyStream(
        ref DataReader reader,
        World world,
        Entity entity,
        EntityApplyMode mode,
        IReferenceRemapper? remapper)
    {
        int count = reader.ReadInt32();
        if (count < 0)
            throw new InvalidDataException("Negative buffer element count.");

        bool hadBuffer = world.HasBuffer<T>(entity);
        if (!hadBuffer)
            world.AddBuffer<T>(entity);

        DynamicBuffer<T> buffer = world.GetBuffer<T>(entity);
        var values = hadBuffer
            ? buffer.ReplaceWithUninitialized(count, SerializationChangeKind.BufferChanged)
            : buffer.LoadUninitialized(count);
        for (int i = 0; i < values.Length; i++)
        {
            T value = ReadValue(ref reader);
            if (remapper is not null)
                RemapValue(ref value, remapper);

            values[i] = value;
        }
    }

    protected override object Remap(object value, IReferenceRemapper remapper)
    {
        var values = (T[])value;
        if (!Entry.ContainsEntityReferences)
            return values;

        T[] remapped = new T[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            remapped[i] = values[i];
            RemapValue(ref remapped[i], remapper);
        }

        return remapped;
    }

    protected override void Apply(object value, World world, Entity entity, EntityApplyMode mode)
    {
        var source = (T[])value;
        bool hadBuffer = world.HasBuffer<T>(entity);
        if (!hadBuffer)
            world.AddBuffer<T>(entity);

        DynamicBuffer<T> buffer = world.GetBuffer<T>(entity);
        var values = hadBuffer
            ? buffer.ReplaceWithUninitialized(source.Length, SerializationChangeKind.BufferChanged)
            : buffer.LoadUninitialized(source.Length);
        source.AsSpan().CopyTo(values);
    }

    public override void Remove(World world, Entity entity)
    {
        if (world.HasBuffer<T>(entity))
            world.RemoveBuffer<T>(entity);
    }
}

internal sealed class RelationSerializationRuntime<T> : ValueSerializationRuntime<T>
    where T : struct, IRelation
{
    private static readonly bool s_isExclusive = default(T) is IExclusiveRelation;

    public RelationSerializationRuntime(
        SerializationTypeKey typeKey,
        IValueCodec<T> codec,
        IValuePatcher<T>? patcher)
        : base(typeKey, ComponentMetadata<T>.Id, ComponentMetadata<T>.Storage, SerializationValueKind.Relation, codec, patcher)
    {
    }

    public override bool IsPresent(World world, Entity entity) => world.GetRelations<T>(entity).Length > 0;

    protected override object Capture(World world, Entity entity) => world.GetRelations<T>(entity).ToArray();

    protected override void Write(ref DataWriter writer, object value)
    {
        var relations = (RelationEntry<T>[])value;
        writer.WriteInt32(relations.Length);
        for (int i = 0; i < relations.Length; i++)
        {
            writer.WriteEntity(relations[i].Target);
            T relationValue = relations[i].Value;
            WriteValue(ref writer, in relationValue);
        }
    }

    protected override object Read(ref DataReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0)
            throw new InvalidDataException("Negative relation count.");

        RelationEntry<T>[] relations = new RelationEntry<T>[count];
        for (int i = 0; i < relations.Length; i++)
        {
            Entity target = reader.ReadEntity();
            T value = ReadValue(ref reader);
            relations[i] = new RelationEntry<T>(target, in value);
        }

        return relations;
    }

    protected override void AddIds(List<int> componentIds) =>
        componentIds.Add(ComponentMetadata<RelationTag<T>>.Id);

    protected override void Spawn(ref DataReader reader, World world, BundleWriter writer)
    {
        ApplyStream(
            ref reader,
            world,
            writer.Entity,
            EntityApplyMode.MergeIncluded,
            remapper: null);
    }

    protected override void ApplyStream(
        ref DataReader reader,
        World world,
        Entity entity,
        EntityApplyMode mode,
        IReferenceRemapper? remapper)
    {
        int count = reader.ReadInt32();
        if (count < 0)
            throw new InvalidDataException("Negative relation count.");

        if (mode != EntityApplyMode.MergeIncluded)
            world.RemoveAllRelations<T>(entity);

        for (int i = 0; i < count; i++)
        {
            Entity target = reader.ReadEntity();
            T relationValue = ReadValue(ref reader);
            if (remapper is not null)
            {
                target = RemapEntity(target, remapper);
                RemapValue(ref relationValue, remapper);
            }

            MergeRelation(world, entity, target, in relationValue);
        }
    }

    protected override object Remap(object value, IReferenceRemapper remapper)
    {
        var relations = (RelationEntry<T>[])value;
        RelationEntry<T>[] remapped = new RelationEntry<T>[relations.Length];
        for (int i = 0; i < relations.Length; i++)
        {
            Entity target = RemapEntity(relations[i].Target, remapper);
            T relationValue = relations[i].Value;
            RemapValue(ref relationValue, remapper);
            remapped[i] = new RelationEntry<T>(target, in relationValue);
        }

        return remapped;
    }

    protected override void Apply(object value, World world, Entity entity, EntityApplyMode mode)
    {
        var relations = (RelationEntry<T>[])value;
        if (mode != EntityApplyMode.MergeIncluded)
            world.RemoveAllRelations<T>(entity);

        for (int i = 0; i < relations.Length; i++)
        {
            T relationValue = relations[i].Value;
            MergeRelation(world, entity, relations[i].Target, in relationValue);
        }
    }

    private static void MergeRelation(World world, Entity entity, Entity target, in T relationValue)
    {
        if (world.HasRelation<T>(entity, target) ||
            (s_isExclusive && world.GetRelations<T>(entity).Length != 0))
        {
            world.ReplaceRelation(entity, target, in relationValue);
            return;
        }

        world.AddRelation(entity, target, in relationValue);
    }

    public override void Remove(World world, Entity entity)
    {
        if (world.IsAlive(entity))
            world.RemoveAllRelations<T>(entity);
    }

    private static Entity RemapEntity(Entity entity, IReferenceRemapper remapper)
    {
        if (entity == Entity.Null)
            return Entity.Null;

        if (!remapper.TryMap(entity, out var mapped))
            throw new InvalidOperationException($"Missing entity remap for relation target {entity}.");

        return mapped;
    }
}

