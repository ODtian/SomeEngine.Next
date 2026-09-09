using System.ComponentModel;
using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>
/// Strongly typed material business object. Concrete material types declare their own fixed
/// properties; this base owns only common asset identity, slot storage, and the polymorphic wire
/// boundary used by .material.asset files.
/// </summary>
[global::SomeEngine.Assets.Asset(".material.asset")]
public abstract partial class Material : IBinaryContract<Material>
{
    protected const uint MaterialPropertyCount = 0;

    private const uint WireVersion = 2;
    private const ulong WireFingerprint = 0x4D41544C76330002UL;

    private static readonly object TypeGate = new();
    private static readonly Dictionary<Guid, MaterialTypeRegistration> TypesById = [];
    private static readonly Dictionary<Type, MaterialTypeRegistration> TypesByClr = [];
    private static long _nextMaterialId;

    private readonly long _materialId = Interlocked.Increment(ref _nextMaterialId);
    private readonly object _valuesGate = new();
    private MaterialData? _capturedData;
    private ulong _capturedSourceRevision;
    private string? _name;
    private string? _shaderGuid;

    private MaterialSlotDefinition[]? _slotDefinitions;
    private object?[]? _slotValues;
    private ulong[]? _slotRevisions;
    private Dictionary<uint, AssetGuid>? _pendingTextureReferences;

    public string? AssetGuid { get; set; }

    public string? Name
    {
        get => _name;
        set
        {
            lock (_valuesGate)
            {
                if (_name == value) return;
                _name = value;
                Revision = checked(Revision + 1);
            }
            AssetUpdate.Notify(Changed, this);
        }
    }

    public string? ShaderGuid
    {
        get => _shaderGuid;
        set
        {
            lock (_valuesGate)
            {
                if (_shaderGuid == value) return;
                _shaderGuid = value;
                ShaderAsset = null;
                Revision = checked(Revision + 1);
            }
            AssetUpdate.Notify(Changed, this);
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public Shader? ShaderAsset { get; private set; }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public ulong Revision { get; private set; } = 1;

    /// <summary>Raised after a slot edit or asset reload has updated this material.</summary>
    public event Action<Material>? Changed;

    [EditorBrowsable(EditorBrowsableState.Never)]
    public IReadOnlyList<MaterialSlotDefinition> Slots => EnsureSlots();

    [EditorBrowsable(EditorBrowsableState.Never)]
    public int SlotCount => EnsureSlots().Length;

    public Guid MaterialTypeId => Registration(GetType()).Id;

    protected Material()
    {
    }

    /// <summary>Captures values for transfer through the Game World to Render World boundary.</summary>
    public MaterialData CaptureData()
    {
        lock (_valuesGate)
        {
            MaterialSlotDefinition[] properties = EnsureSlots();
            MaterialData? previous = _capturedData;
            bool changed = previous is null || _capturedSourceRevision != Revision;
            object?[]? values = changed ? (object?[])_slotValues!.Clone() : null;
            for (int index = 0; index < properties.Length; index++)
            {
                if (_slotValues![index] is not Texture texture) continue;
                TextureData data = texture.CaptureData();
                if (!changed && ReferenceEquals(previous!.GetValue((uint)index), data)) continue;
                if (values is null) values = (object?[])_slotValues.Clone();
                changed = true;
                values[index] = data;
            }
            if (!changed) return previous!;
            // Once a texture changed, convert any earlier texture properties as well.
            for (int index = 0; index < values!.Length; index++)
                if (values[index] is Texture texture) values[index] = texture.CaptureData();
            ulong revision = checked((previous?.Revision ?? 0) + 1);
            var propertyRevisions = new ulong[properties.Length];
            for (int index = 0; index < properties.Length; index++)
                propertyRevisions[index] = previous is not null && Equals(previous.GetValue((uint)index), values[index])
                    ? previous.GetPropertyRevision((uint)index) : revision;
            _capturedData = new MaterialData(_materialId, revision, Name, ShaderGuid, ShaderAsset,
                [.. properties], values, propertyRevisions);
            _capturedSourceRevision = Revision;
            return _capturedData;
        }
    }

    internal Texture[] CaptureTextureReferences()
    {
        lock (_valuesGate)
        {
            _ = EnsureSlots();
            return _slotValues!.OfType<Texture>().Distinct().ToArray();
        }
    }

    public object? GetSlotValue(uint slot)
    {
        EnsureSlot(slot);
        return _slotValues![checked((int)slot)];
    }

    public ulong GetSlotRevision(uint slot)
    {
        EnsureSlot(slot);
        return _slotRevisions![checked((int)slot)];
    }

    protected T GetSlot<T>(uint slot)
    {
        EnsureSlot(slot);
        object? value = _slotValues![checked((int)slot)];
        return value is null ? default! : (T)value;
    }

    protected void SetSlot<T>(uint slot, T value)
    {
        lock (_valuesGate)
        {
            EnsureSlot(slot);
            int index = checked((int)slot);
            MaterialSlotDefinition definition = _slotDefinitions![index];
            if (!definition.ValueType.IsAssignableFrom(typeof(T)) &&
                (value is not null && !definition.ValueType.IsInstanceOfType(value)))
            {
                throw new ArgumentException(
                    $"Material slot {slot} expects '{definition.ValueType.FullName}'.",
                    nameof(value));
            }

            object? current = _slotValues![index];
            if (typeof(T).IsValueType
                ? EqualityComparer<T>.Default.Equals(current is null ? default! : (T)current, value)
                : ReferenceEquals(current, value))
            {
                return;
            }

            _slotValues[index] = value;
            _pendingTextureReferences?.Remove(slot);
            AdvancePropertyRevision(index);
        }
        AssetUpdate.Notify(Changed, this);
    }

    protected virtual MaterialSlotDefinition[] CreateSlots() => [];

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void RegisterGeneratedType<T>(string logicalName)
        where T : Material, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        Guid id = BinaryTypeId.FromLogicalName(logicalName);
        var registration = new MaterialTypeRegistration(
            id,
            logicalName,
            typeof(T),
            static () => new T());
        lock (TypeGate)
        {
            if (!TypesById.TryAdd(id, registration) ||
                !TypesByClr.TryAdd(typeof(T), registration))
            {
                throw new InvalidOperationException(
                    $"Material type '{logicalName}' was registered more than once.");
            }
        }
    }

    internal Material CloneForAuthoring(string name, global::SomeEngine.Assets.AssetGuid assetGuid)
    {
        Material result = Registration(GetType()).Create();
        result.AssetGuid = assetGuid.ToFlatString();
        result.Name = name;
        result.ShaderGuid = ShaderGuid;
        MaterialSlotDefinition[] sourceSlots = EnsureSlots();
        MaterialSlotDefinition[] destinationSlots = result.EnsureSlots();
        if (!sourceSlots.AsSpan().SequenceEqual(destinationSlots))
            throw new InvalidOperationException("A generated material type changed its slot contract while cloning.");

        for (uint slot = 0; slot < sourceSlots.Length; slot++)
        {
            if (sourceSlots[checked((int)slot)].ValueType == typeof(Texture))
            {
                AssetGuid reference = GetTextureReference(slot);
                if (!reference.IsEmpty)
                    result.SetTextureReference(slot, reference);
                continue;
            }
            result._slotValues![checked((int)slot)] = _slotValues![checked((int)slot)];
        }
        return result;
    }

    internal AssetGuid GetTextureReference(uint slot)
    {
        EnsureSlot(slot);
        if (_slotDefinitions![checked((int)slot)].ValueType != typeof(Texture))
            throw new ArgumentException("The material slot is not a Texture.", nameof(slot));
        if (_pendingTextureReferences is not null &&
            _pendingTextureReferences.TryGetValue(slot, out AssetGuid pending))
            return pending;
        if (_slotValues![checked((int)slot)] is not Texture texture)
            return global::SomeEngine.Assets.AssetGuid.Empty;
        if (global::SomeEngine.Assets.AssetGuid.TryParse(texture.AssetGuid, out AssetGuid guid) &&
            !guid.IsEmpty)
        {
            return guid;
        }
        throw new InvalidDataException(
            $"Material Texture slot '{_slotDefinitions[checked((int)slot)].Name}' references a Texture without a valid asset GUID.");
    }

    internal void SetTextureReference(uint slot, AssetGuid assetGuid)
    {
        EnsureSlot(slot);
        if (_slotDefinitions![checked((int)slot)].ValueType != typeof(Texture))
            throw new ArgumentException("The material slot is not a Texture.", nameof(slot));
        if (assetGuid.IsEmpty)
            throw new ArgumentException("A material Texture reference cannot be empty.", nameof(assetGuid));
        _slotValues![checked((int)slot)] = null;
        (_pendingTextureReferences ??= [])[slot] = assetGuid;
        MarkSlotChanged(checked((int)slot));
    }

    internal uint RequireSlot(string name, Type valueType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(valueType);
        MaterialSlotDefinition[] slots = EnsureSlots();
        for (uint slot = 0; slot < slots.Length; slot++)
        {
            MaterialSlotDefinition definition = slots[checked((int)slot)];
            if (!string.Equals(definition.Name, name, StringComparison.Ordinal))
                continue;
            if (definition.ValueType != valueType)
            {
                throw new InvalidDataException(
                    $"Material slot '{name}' is '{definition.ValueType.FullName}', not '{valueType.FullName}'.");
            }
            return slot;
        }
        throw new InvalidDataException(
            $"Material type '{Registration(GetType()).LogicalName}' has no slot '{name}'.");
    }

    internal IReadOnlyList<global::SomeEngine.Assets.AssetGuid> GetDependencies(string path)
    {
        MaterialSlotDefinition[] slots = EnsureSlots();
        var result = new List<global::SomeEngine.Assets.AssetGuid>();
        if (!string.IsNullOrWhiteSpace(ShaderGuid))
        {
            if (!global::SomeEngine.Assets.AssetGuid.TryParse(ShaderGuid, out AssetGuid shader) ||
                shader.IsEmpty)
            {
                throw new InvalidDataException(
                    $"Material asset '{path}' has invalid shader GUID '{ShaderGuid}'.");
            }
            result.Add(shader);
        }
        for (uint slot = 0; slot < slots.Length; slot++)
        {
            if (slots[checked((int)slot)].ValueType != typeof(Texture))
                continue;
            AssetGuid dependency = GetTextureReference(slot);
            if (!dependency.IsEmpty && !result.Contains(dependency))
                result.Add(dependency);
        }
        result.Sort(static (left, right) => left.Value.CompareTo(right.Value));
        return result;
    }

    internal static async ValueTask<Material> LoadAssetAsync(
        global::SomeEngine.Assets.AssetLoadContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SomeEngine.Serialization.Containers.BinaryDocument<Material> document = await context
            .OpenAsync<Material>()
            .ConfigureAwait(false);
        Material material = document.Root;
        await document.DisposeAsync().ConfigureAwait(false);
        await material.ResolveShaderAsync(context, cancellationToken).ConfigureAwait(false);
        await material.ResolveTextureReferencesAsync(context, cancellationToken).ConfigureAwait(false);
        return material;
    }

    internal static ValueTask ApplyReloadAsync(
        Material current,
        Material replacement,
        AssetUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        cancellationToken.ThrowIfCancellationRequested();
        if (current.MaterialTypeId != replacement.MaterialTypeId)
            throw new InvalidDataException("A material reload cannot change its concrete material type.");

        lock (current._valuesGate)
        {
            MaterialSlotDefinition[] currentSlots = current.EnsureSlots();
            MaterialSlotDefinition[] replacementSlots = replacement.EnsureSlots();
            if (!currentSlots.AsSpan().SequenceEqual(replacementSlots))
                throw new InvalidDataException("A material reload cannot change its generated slot contract.");

            ulong revision = checked(current.Revision + 1);
            object?[] values = (object?[])replacement._slotValues!.Clone();
            ulong[] revisions = (ulong[])current._slotRevisions!.Clone();
            for (int index = 0; index < currentSlots.Length; index++)
                if (!Equals(current._slotValues![index], values[index]))
                    revisions[index] = checked(revisions[index] + 1);
            current.AssetGuid = replacement.AssetGuid;
            current._name = replacement.Name;
            current._shaderGuid = replacement.ShaderGuid;
            current.ShaderAsset = replacement.ShaderAsset;
            current._slotValues = values;
            current._slotRevisions = revisions;
            current._pendingTextureReferences = null;
            current.Revision = revision;
        }
        update.Accept();
        AssetUpdate.Notify(current.Changed, current);
        return ValueTask.CompletedTask;
    }

    public static Guid TypeId => BinaryTypeId.FromLogicalName("SomeEngine.Assets.Material");

    public static ulong SchemaFingerprint => WireFingerprint;

    public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;

    public static uint SchemaEpoch => 1;

    public static void Write(ref BinaryDataWriter writer, Material value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteUInt32(WireVersion);
        writer.WriteString(value.AssetGuid);
        writer.WriteString(value.Name);
        writer.WriteString(value.ShaderGuid);
        writer.WriteGuid(value.MaterialTypeId);
        MaterialSlotDefinition[] slots = value.EnsureSlots();
        writer.WriteInt32(slots.Length);
        for (uint slot = 0; slot < slots.Length; slot++)
        {
            MaterialSlotDefinition definition = slots[checked((int)slot)];
            writer.WriteString(definition.Name);
            MaterialWireKind kind = WireKind(definition.ValueType);
            writer.WriteByte((byte)kind);
            WriteSlot(ref writer, value, slot, kind);
        }
    }

    public static Material Read(ref BinaryDataReader reader)
    {
        uint version = reader.ReadUInt32();
        if (version != WireVersion)
            throw new InvalidDataException($"Unsupported material wire version {version}.");
        string? assetGuid = reader.ReadString();
        string? name = reader.ReadString();
        string? shaderGuid = reader.ReadString();
        Guid materialType = reader.ReadGuid();
        Material result = Registration(materialType).Create();
        result.AssetGuid = assetGuid;
        result.Name = name;
        result.ShaderGuid = shaderGuid;
        MaterialSlotDefinition[] slots = result.EnsureSlots();
        int count = reader.ReadCollectionCount(
            "material slot",
            elementAllocationBytes: 32,
            fixedAllocationBytes: 0);
        if (count != slots.Length)
        {
            throw new InvalidDataException(
                $"Material type '{Registration(materialType).LogicalName}' expects {slots.Length} slots, document contains {count}.");
        }

        for (uint slot = 0; slot < slots.Length; slot++)
        {
            MaterialSlotDefinition definition = slots[checked((int)slot)];
            string? encodedName = reader.ReadString();
            if (!string.Equals(encodedName, definition.Name, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Material slot {slot} is '{encodedName}', expected '{definition.Name}'.");
            }
            MaterialWireKind kind = (MaterialWireKind)reader.ReadByte();
            MaterialWireKind expected = WireKind(definition.ValueType);
            if (kind != expected)
                throw new InvalidDataException($"Material slot '{definition.Name}' has wire kind {kind}, expected {expected}.");
            ReadSlot(ref reader, result, slot, kind);
        }
        return result;
    }

    private async ValueTask ResolveShaderAsync(
        global::SomeEngine.Assets.AssetLoadContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ShaderGuid))
        {
            ShaderAsset = null;
            return;
        }
        if (!global::SomeEngine.Assets.AssetGuid.TryParse(ShaderGuid, out AssetGuid guid) || guid.IsEmpty)
            throw new InvalidDataException($"Material '{Name}' has invalid shader GUID '{ShaderGuid}'.");
        cancellationToken.ThrowIfCancellationRequested();
        ShaderAsset = await context
            .LoadDependencyAsync(new global::SomeEngine.Assets.AssetId<Shader>(guid))
            .ConfigureAwait(false);
    }

    private async ValueTask ResolveTextureReferencesAsync(
        global::SomeEngine.Assets.AssetLoadContext context,
        CancellationToken cancellationToken)
    {
        if (_pendingTextureReferences is null || _pendingTextureReferences.Count == 0)
            return;
        foreach ((uint slot, AssetGuid guid) in _pendingTextureReferences.OrderBy(static pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Texture texture = await context
                .LoadDependencyAsync(new global::SomeEngine.Assets.AssetId<Texture>(guid))
                .ConfigureAwait(false);
            _slotValues![checked((int)slot)] = texture;
        }
        _pendingTextureReferences = null;
    }

    private MaterialSlotDefinition[] EnsureSlots()
    {
        if (_slotDefinitions is not null)
            return _slotDefinitions;
        MaterialSlotDefinition[] definitions = CreateSlots();
        for (int index = 0; index < definitions.Length; index++)
        {
            MaterialSlotDefinition definition = definitions[index];
            if (definition.Slot != checked((uint)index) ||
                string.IsNullOrWhiteSpace(definition.Name) ||
                definition.ValueType is null)
            {
                throw new InvalidOperationException("A material slot contract must be dense and fully typed.");
            }
            _ = WireKind(definition.ValueType);
        }
        _slotDefinitions = definitions;
        _slotValues = new object?[definitions.Length];
        _slotRevisions = new ulong[definitions.Length];
        return definitions;
    }

    private void EnsureSlot(uint slot)
    {
        if (slot >= EnsureSlots().Length)
            throw new ArgumentOutOfRangeException(nameof(slot));
    }

    private void MarkSlotChanged(int slot)
    {
        AdvancePropertyRevision(slot);
        AssetUpdate.Notify(Changed, this);
    }

    private void AdvancePropertyRevision(int property)
    {
        _slotRevisions![property] = checked(_slotRevisions[property] + 1);
        Revision = checked(Revision + 1);
    }

    private static MaterialTypeRegistration Registration(Guid id)
    {
        lock (TypeGate)
            return TypesById.TryGetValue(id, out MaterialTypeRegistration registration)
                ? registration
                : throw new InvalidDataException($"Material document references unknown material type '{id}'.");
    }

    private static MaterialTypeRegistration Registration(Type type)
    {
        lock (TypeGate)
            return TypesByClr.TryGetValue(type, out MaterialTypeRegistration registration)
                ? registration
                : throw new InvalidOperationException(
                    $"Concrete material type '{type.FullName}' is not generated/registered.");
    }

    private static MaterialWireKind WireKind(Type type)
    {
        if (type == typeof(Texture)) return MaterialWireKind.Texture;
        if (type == typeof(SamplerDesc)) return MaterialWireKind.Sampler;
        if (type == typeof(float)) return MaterialWireKind.Float;
        if (type == typeof(int)) return MaterialWireKind.Int;
        if (type == typeof(uint)) return MaterialWireKind.UInt;
        if (type == typeof(bool)) return MaterialWireKind.Bool;
        if (type == typeof(Vector2)) return MaterialWireKind.Vector2;
        if (type == typeof(Vector3)) return MaterialWireKind.Vector3;
        if (type == typeof(Vector4)) return MaterialWireKind.Vector4;
        if (type == typeof(Matrix4x4)) return MaterialWireKind.Matrix4x4;
        throw new NotSupportedException(
            $"Material slot type '{type.FullName}' has no authored wire encoding.");
    }

    private static void WriteSlot(
        ref BinaryDataWriter writer,
        Material material,
        uint slot,
        MaterialWireKind kind)
    {
        object? value = material._slotValues![checked((int)slot)];
        switch (kind)
        {
            case MaterialWireKind.Texture:
            {
                AssetGuid guid = material.GetTextureReference(slot);
                writer.WriteString(guid.IsEmpty ? null : guid.ToFlatString());
                break;
            }
            case MaterialWireKind.Sampler:
                WriteSampler(ref writer, value is SamplerDesc sampler ? sampler : default);
                break;
            case MaterialWireKind.Float:
                writer.WriteSingle(value is float scalar ? scalar : 0f);
                break;
            case MaterialWireKind.Int:
                writer.WriteInt32(value is int integer ? integer : 0);
                break;
            case MaterialWireKind.UInt:
                writer.WriteUInt32(value is uint unsigned ? unsigned : 0u);
                break;
            case MaterialWireKind.Bool:
                writer.WriteBoolean(value is bool boolean && boolean);
                break;
            case MaterialWireKind.Vector2:
                Write(ref writer, value is Vector2 vector2 ? vector2 : default);
                break;
            case MaterialWireKind.Vector3:
                Write(ref writer, value is Vector3 vector3 ? vector3 : default);
                break;
            case MaterialWireKind.Vector4:
                Write(ref writer, value is Vector4 vector4 ? vector4 : default);
                break;
            case MaterialWireKind.Matrix4x4:
                Write(ref writer, value is Matrix4x4 matrix ? matrix : default);
                break;
            default:
                throw new InvalidDataException($"Unknown material wire kind {kind}.");
        }
    }

    private static void ReadSlot(
        ref BinaryDataReader reader,
        Material material,
        uint slot,
        MaterialWireKind kind)
    {
        switch (kind)
        {
            case MaterialWireKind.Texture:
            {
                string? encoded = reader.ReadString();
                if (encoded is null)
                    break;
                if (!global::SomeEngine.Assets.AssetGuid.TryParse(encoded, out AssetGuid guid) || guid.IsEmpty)
                    throw new InvalidDataException($"Material Texture slot {slot} contains invalid asset GUID '{encoded}'.");
                (material._pendingTextureReferences ??= [])[slot] = guid;
                break;
            }
            case MaterialWireKind.Sampler:
                material._slotValues![checked((int)slot)] = ReadSampler(ref reader);
                break;
            case MaterialWireKind.Float:
                material._slotValues![checked((int)slot)] = reader.ReadSingle();
                break;
            case MaterialWireKind.Int:
                material._slotValues![checked((int)slot)] = reader.ReadInt32();
                break;
            case MaterialWireKind.UInt:
                material._slotValues![checked((int)slot)] = reader.ReadUInt32();
                break;
            case MaterialWireKind.Bool:
                material._slotValues![checked((int)slot)] = reader.ReadBoolean();
                break;
            case MaterialWireKind.Vector2:
                material._slotValues![checked((int)slot)] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                break;
            case MaterialWireKind.Vector3:
                material._slotValues![checked((int)slot)] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                break;
            case MaterialWireKind.Vector4:
                material._slotValues![checked((int)slot)] = new Vector4(
                    reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                break;
            case MaterialWireKind.Matrix4x4:
                material._slotValues![checked((int)slot)] = ReadMatrix(ref reader);
                break;
            default:
                throw new InvalidDataException($"Unknown material wire kind {kind}.");
        }
    }

    private static void WriteSampler(ref BinaryDataWriter writer, in SamplerDesc value)
    {
        writer.WriteInt32((int)value.MinFilter);
        writer.WriteInt32((int)value.MagFilter);
        writer.WriteInt32((int)value.MipFilter);
        writer.WriteInt32((int)value.AddressU);
        writer.WriteInt32((int)value.AddressV);
        writer.WriteInt32((int)value.AddressW);
        writer.WriteSingle(value.MipLodBias);
        writer.WriteUInt32(value.MaximumAnisotropy);
        writer.WriteBoolean(value.Comparison.HasValue);
        if (value.Comparison.HasValue)
            writer.WriteInt32((int)value.Comparison.Value);
        Write(ref writer, value.BorderColor);
        writer.WriteSingle(value.MinimumLod);
        writer.WriteSingle(value.MaximumLod);
        writer.WriteString(value.Label);
    }

    private static SamplerDesc ReadSampler(ref BinaryDataReader reader)
    {
        var min = (FilterType)reader.ReadInt32();
        var mag = (FilterType)reader.ReadInt32();
        var mip = (FilterType)reader.ReadInt32();
        var u = (AddressType)reader.ReadInt32();
        var v = (AddressType)reader.ReadInt32();
        var w = (AddressType)reader.ReadInt32();
        float bias = reader.ReadSingle();
        uint anisotropy = reader.ReadUInt32();
        CompareOperation? comparison = reader.ReadBoolean()
            ? (CompareOperation)reader.ReadInt32()
            : null;
        Vector4 border = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        float minimumLod = reader.ReadSingle();
        float maximumLod = reader.ReadSingle();
        string? label = reader.ReadString();
        if (!Enum.IsDefined(min) || !Enum.IsDefined(mag) || !Enum.IsDefined(mip) ||
            !Enum.IsDefined(u) || !Enum.IsDefined(v) || !Enum.IsDefined(w) ||
            (comparison.HasValue && !Enum.IsDefined(comparison.Value)) || anisotropy == 0)
            throw new InvalidDataException("Material sampler contains an invalid enum or anisotropy value.");
        return new SamplerDesc(
            min, mag, mip, u, v, w, bias, anisotropy, comparison, border, minimumLod, maximumLod, label);
    }

    private static void Write(ref BinaryDataWriter writer, Vector2 value)
    {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
    }

    private static void Write(ref BinaryDataWriter writer, Vector3 value)
    {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
        writer.WriteSingle(value.Z);
    }

    private static void Write(ref BinaryDataWriter writer, Vector4 value)
    {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
        writer.WriteSingle(value.Z);
        writer.WriteSingle(value.W);
    }

    private static void Write(ref BinaryDataWriter writer, Matrix4x4 value)
    {
        writer.WriteSingle(value.M11); writer.WriteSingle(value.M12); writer.WriteSingle(value.M13); writer.WriteSingle(value.M14);
        writer.WriteSingle(value.M21); writer.WriteSingle(value.M22); writer.WriteSingle(value.M23); writer.WriteSingle(value.M24);
        writer.WriteSingle(value.M31); writer.WriteSingle(value.M32); writer.WriteSingle(value.M33); writer.WriteSingle(value.M34);
        writer.WriteSingle(value.M41); writer.WriteSingle(value.M42); writer.WriteSingle(value.M43); writer.WriteSingle(value.M44);
    }

    private static Matrix4x4 ReadMatrix(ref BinaryDataReader reader) => new(
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private readonly record struct MaterialTypeRegistration(
        Guid Id,
        string LogicalName,
        Type ClrType,
        Func<Material> Create);

    private enum MaterialWireKind : byte
    {
        Texture = 1,
        Sampler = 2,
        Float = 3,
        Int = 4,
        UInt = 5,
        Bool = 6,
        Vector2 = 7,
        Vector3 = 8,
        Vector4 = 9,
        Matrix4x4 = 10,
    }
}

public readonly record struct MaterialSlotDefinition(uint Slot, string Name, Type ValueType);
