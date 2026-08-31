using System.Numerics;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[global::SomeEngine.Assets.Asset(".material.asset")]
public partial class Material
{
    private MaterialSlotDefinition[]? _slotDefinitions;
    private object?[]? _slotValues;
    private ulong[]? _slotRevisions;
    private Dictionary<string, object?>? _weakValues;

    [BinaryIgnore]
    public ulong Revision { get; private set; } = 1;

    [BinaryIgnore]
    public IReadOnlyList<MaterialSlotDefinition> Slots => EnsureSlots();

    [BinaryIgnore]
    public int SlotCount => EnsureSlots().Length;

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
        EnsureSlot(slot);
        int index = checked((int)slot);
        object? current = _slotValues![index];
        if (typeof(T).IsValueType
            ? EqualityComparer<T>.Default.Equals(current is null ? default! : (T)current, value)
            : ReferenceEquals(current, value))
        {
            return;
        }

        MaterialSlotDefinition definition = _slotDefinitions![index];
        if (!definition.ValueType.IsAssignableFrom(typeof(T)) &&
            (value is not null && !definition.ValueType.IsInstanceOfType(value)))
        {
            throw new ArgumentException(
                $"Material slot {slot} expects '{definition.ValueType.FullName}'.",
                nameof(value));
        }
        _slotValues[index] = value;
        MarkSlotChanged(index);
    }

    protected virtual MaterialSlotDefinition[] CreateSlots()
    {
        Dictionary<string, object?> values = _weakValues ?? BuildWeakValues();
        var result = new MaterialSlotDefinition[values.Count];
        int slot = 0;
        foreach ((string name, object? value) in values.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            Type type = value?.GetType() ?? typeof(object);
            result[slot] = new MaterialSlotDefinition(checked((uint)slot), name, type);
            slot++;
        }
        return result;
    }

    internal void ResolveWeakSlots(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _weakValues = new Dictionary<string, object?>(values, StringComparer.Ordinal);
        _slotDefinitions = null;
        _slotValues = null;
        _slotRevisions = null;
        _ = EnsureSlots();
    }

    internal IReadOnlyList<AssetGuid> GetDependencies(string path)
    {
        var result = new List<AssetGuid>();
        if (Passes is not null)
        {
            for (int index = 0; index < Passes.Count; index++)
            {
                AssetGuid shader = ShaderRef.Require(
                    Passes[index]?.Shader,
                    $"Material asset '{path}'",
                    $"Passes[{index}].Shader");
                if (!result.Contains(shader))
                    result.Add(shader);
            }
        }
        if (Textures is not null)
        {
            for (int index = 0; index < Textures.Count; index++)
                AddRequired(result, Textures[index]?.TextureGuid, path, $"Textures[{index}].TextureGuid");
        }
        result.Sort(static (left, right) => left.Value.CompareTo(right.Value));
        return result;
    }

    internal static ValueTask ApplyReloadAsync(
        Material current,
        Material replacement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        cancellationToken.ThrowIfCancellationRequested();
        if (current.GetType() != replacement.GetType())
            throw new InvalidDataException("A material reload cannot change the runtime material type.");

        MaterialSlotDefinition[] replacementSlots = replacement.EnsureSlots();
        MaterialSlotDefinition[] currentSlots = current.EnsureSlots();
        if (!currentSlots.AsSpan().SequenceEqual(replacementSlots))
            throw new InvalidDataException("A material reload cannot change its generated slot contract.");

        current.AssetGuid = replacement.AssetGuid;
        current.Name = replacement.Name;
        current.Passes = replacement.Passes;
        current.Textures = replacement.Textures;
        current.Scalars = replacement.Scalars;
        current._weakValues = replacement._weakValues is null
            ? null
            : new Dictionary<string, object?>(replacement._weakValues, StringComparer.Ordinal);
        for (int index = 0; index < currentSlots.Length; index++)
        {
            object? next = replacement._slotValues![index];
            if (Equals(current._slotValues![index], next))
                continue;
            current._slotValues[index] = next;
            current.MarkSlotChanged(index);
        }
        current.Revision = checked(current.Revision + 1);
        return ValueTask.CompletedTask;
    }

    private MaterialSlotDefinition[] EnsureSlots()
    {
        if (_slotDefinitions is not null)
            return _slotDefinitions;
        MaterialSlotDefinition[] definitions = CreateSlots();
        for (int index = 0; index < definitions.Length; index++)
        {
            MaterialSlotDefinition definition = definitions[index];
            if (definition.Slot != checked((uint)index)
                || string.IsNullOrWhiteSpace(definition.Name)
                || definition.ValueType is null)
            {
                throw new InvalidOperationException("A material slot contract must be dense and fully typed.");
            }
        }
        _slotDefinitions = definitions;
        _slotValues = new object?[definitions.Length];
        _slotRevisions = new ulong[definitions.Length];

        Dictionary<string, object?>? weak = _weakValues;
        if (weak is not null)
        {
            for (int index = 0; index < definitions.Length; index++)
                if (weak.TryGetValue(definitions[index].Name, out object? value))
                    _slotValues[index] = value;
        }
        return definitions;
    }

    private Dictionary<string, object?> BuildWeakValues()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (TextureBinding binding in Textures ?? [])
        {
            if (string.IsNullOrWhiteSpace(binding.Name))
                throw new InvalidDataException("A weak material texture binding must have a name.");
            values.Add(binding.Name, null);
        }
        foreach (ScalarParam scalar in Scalars ?? [])
        {
            if (string.IsNullOrWhiteSpace(scalar.Name) || scalar.Value is not { } value)
                throw new InvalidDataException("A weak material scalar binding must have a name and value.");
            values.Add(scalar.Name, ConvertValue(value));
        }
        return values;
    }

    private void EnsureSlot(uint slot)
    {
        MaterialSlotDefinition[] definitions = EnsureSlots();
        if (slot >= definitions.Length)
            throw new ArgumentOutOfRangeException(nameof(slot));
    }

    private void MarkSlotChanged(int slot)
    {
        _slotRevisions![slot] = checked(_slotRevisions[slot] + 1);
        Revision = checked(Revision + 1);
    }

    private static object ConvertValue(ParamValue value)
        => value.Kind switch
        {
            ParamValue.ItemKind.FloatVal => value.FloatVal.V,
            ParamValue.ItemKind.IntVal => value.IntVal.V,
            ParamValue.ItemKind.BoolVal => value.BoolVal.V,
            ParamValue.ItemKind.Vec2Val => new Vector2(value.Vec2Val.X, value.Vec2Val.Y),
            ParamValue.ItemKind.Vec3Val => new Vector3(value.Vec3Val.X, value.Vec3Val.Y, value.Vec3Val.Z),
            ParamValue.ItemKind.Vec4Val => new Vector4(
                value.Vec4Val.X,
                value.Vec4Val.Y,
                value.Vec4Val.Z,
                value.Vec4Val.W),
            _ => throw new InvalidDataException("A weak material scalar has no supported value."),
        };

    private static void AddRequired(
        List<AssetGuid> result,
        string? value,
        string path,
        string field)
    {
        if (!global::SomeEngine.Assets.AssetGuid.TryParse(value, out AssetGuid guid) || guid.IsEmpty)
            throw new InvalidDataException($"Material asset '{path}' field '{field}' has an invalid asset GUID '{value}'.");
        if (!result.Contains(guid))
            result.Add(guid);
    }
}

public readonly record struct MaterialSlotDefinition(uint Slot, string Name, Type ValueType);
