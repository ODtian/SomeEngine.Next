using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using SlangShaderSharp;
using SomeEngine.Resources;

namespace SomeEngine.Render.Materials;

public sealed class ScalarLayout
{
    public const int HeaderByteSize = 16;
    public const int PayloadAlignment = 16;

    public static readonly ScalarLayout Empty = new(string.Empty, [], 0, 0);

    private readonly IReadOnlyList<ScalarFieldLayout> _fields;

    private ScalarLayout(
        string name,
        ScalarFieldLayout[] fields,
        uint payloadByteSize,
        uint layoutHash)
    {
        Name = name;
        _fields = fields;

        PayloadByteSize = payloadByteSize;
        LayoutHash = layoutHash;
    }

    private ScalarLayout(
        string name,
        IReadOnlyList<ScalarFieldLayout> fields,
        uint payloadByteSize,
        uint layoutHash)
    {
        Name = name;
        _fields = fields;
        PayloadByteSize = payloadByteSize;
        LayoutHash = layoutHash;
    }

    public string Name { get; }

    public IReadOnlyList<ScalarFieldLayout> Fields => _fields;

    public uint PayloadByteSize { get; }

    public uint LayoutHash { get; }

    public int ByteSize => HeaderByteSize + AlignUp((int)PayloadByteSize, PayloadAlignment);

    public static ScalarLayout FromFields(
        string name,
        IEnumerable<ScalarFieldLayout> fields,
        uint payloadByteSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(fields);

        var materialFields = new List<ScalarFieldLayout>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScalarFieldLayout field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name) || !seen.Add(field.Name))
            {
                continue;
            }

            ulong fieldEnd = (ulong)field.Offset + field.Size;
            if (field.Size == 0 || fieldEnd > payloadByteSize)
            {
                continue;
            }

            materialFields.Add(field);
        }

        materialFields.Sort(static (left, right) =>
        {
            int offset = left.Offset.CompareTo(right.Offset);
            return offset != 0 ? offset : string.CompareOrdinal(left.Name, right.Name);
        });

        return new ScalarLayout(
            name,
            [.. materialFields],
            payloadByteSize,
            ComputeHash(name, materialFields, payloadByteSize));
    }

    internal static ScalarLayout FromSlangByteAddress(
        string name,
        TypeLayoutReflection typeLayout)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A material scalar type requires a name.", nameof(name));
        if (typeLayout == TypeLayoutReflection.Null || typeLayout.Kind != SlangTypeKind.Struct)
            throw new ArgumentException("Material scalars require a reflected struct type.", nameof(typeLayout));

        // MaterialScalarRegion.Load<T> uses Slang natural byte-address layout, not
        // the target-specific StructuredBuffer layout. For the material property
        // types supported here, every scalar (including bool) is 32 bits and
        // vectors inherit that 4-byte alignment, without float3 tail padding.
        // Keep declaration order from Slang; never infer fields from a C# struct.
        uint byteOffset = 0;
        var fields = new List<ScalarFieldLayout>(checked((int)typeLayout.FieldCount));
        for (uint fieldIndex = 0; fieldIndex < typeLayout.FieldCount; fieldIndex++)
        {
            VariableLayoutReflection field = typeLayout.GetFieldByIndex(fieldIndex);
            if (field == VariableLayoutReflection.Null || string.IsNullOrWhiteSpace(field.Name))
                throw new InvalidDataException($"Material scalar type '{name}' has an unnamed field.");
            TypeLayoutReflection fieldType = field.TypeLayout;
            bool scalar = fieldType.Kind == SlangTypeKind.Scalar &&
                fieldType.ScalarType is SlangScalarType.Bool or SlangScalarType.Int32 or
                    SlangScalarType.UInt32 or SlangScalarType.Float32;
            bool vector = fieldType.Kind == SlangTypeKind.Vector &&
                fieldType.ScalarType == SlangScalarType.Float32 &&
                fieldType.ColumnCount is >= 1 and <= 4;
            if (!scalar && !vector)
                throw new InvalidDataException(
                    $"Material scalar field '{name}.{field.Name}' must be a 32-bit scalar or float vector.");
            uint components = scalar ? 1u : fieldType.ColumnCount;
            uint size = checked(components * sizeof(uint));
            fields.Add(new ScalarFieldLayout(field.Name, byteOffset, size,
                fieldType.RowCount, fieldType.ColumnCount, checked((byte)fieldType.ScalarType)));
            byteOffset = checked(byteOffset + size);
        }
        return FromFields(name, fields, byteOffset);
    }

    internal bool HasSameContract(ScalarLayout other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!string.Equals(Name, other.Name, StringComparison.Ordinal)
            || PayloadByteSize != other.PayloadByteSize
            || LayoutHash != other.LayoutHash
            || _fields.Count != other._fields.Count)
        {
            return false;
        }

        for (int index = 0; index < _fields.Count; index++)
        {
            if (_fields[index] != other._fields[index])
                return false;
        }

        return true;
    }

    internal void WriteHeader(Span<byte> destination)
    {
        if (destination.Length < ByteSize)
        {
            throw new ArgumentException("Destination span is smaller than the material scalar region.", nameof(destination));
        }

        destination[..ByteSize].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination, PayloadByteSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(sizeof(uint)), LayoutHash);
    }

    internal void Write(string name, float value, Span<byte> payload)
    {
        if (TryField(name, payload, out ScalarFieldLayout field, out Span<byte> fieldBytes))
            WriteFloatField(fieldBytes, field.ComponentCount, new Vector4(value, 0, 0, 0));
    }

    internal void Write(string name, int value, Span<byte> payload)
    {
        if (TryField(name, payload, out ScalarFieldLayout field, out Span<byte> fieldBytes))
            WriteIntegerField(fieldBytes, field, value);
    }

    internal void Write(string name, bool value, Span<byte> payload)
    {
        if (TryField(name, payload, out ScalarFieldLayout field, out Span<byte> fieldBytes))
            WriteIntegerField(fieldBytes, field, value ? 1 : 0);
    }

    internal void Write(string name, Vector2 value, Span<byte> payload)
    {
        if (TryField(name, payload, out ScalarFieldLayout field, out Span<byte> fieldBytes))
            WriteFloatField(fieldBytes, field.ComponentCount, new Vector4(value, 0, 0));
    }

    internal void Write(string name, Vector3 value, Span<byte> payload)
    {
        if (TryField(name, payload, out ScalarFieldLayout field, out Span<byte> fieldBytes))
            WriteFloatField(fieldBytes, field.ComponentCount, new Vector4(value, 0));
    }

    internal void Write(string name, Vector4 value, Span<byte> payload)
    {
        if (TryField(name, payload, out ScalarFieldLayout field, out Span<byte> fieldBytes))
            WriteFloatField(fieldBytes, field.ComponentCount, value);
    }

    internal ScalarMaterialLayout Resolve(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);
        IReadOnlyList<MaterialPropertyDefinition> definitions = material.Properties;
        var bindings = new List<ScalarMaterialBinding>(_fields.Count);
        for (int fieldIndex = 0; fieldIndex < _fields.Count; fieldIndex++)
        {
            ScalarFieldLayout field = _fields[fieldIndex];
            uint? materialSlot = null;
            for (uint slot = 0; slot < definitions.Count; slot++)
            {
                if (!string.Equals(
                        definitions[checked((int)slot)].Name,
                        field.Name,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                materialSlot = slot;
                break;
            }
            if (materialSlot is { } resolved)
                bindings.Add(new ScalarMaterialBinding(resolved, field, definitions[checked((int)resolved)].ValueType));
        }
        return new ScalarMaterialLayout(this, [.. bindings]);
    }

    private byte[] Encode(Material material, ReadOnlySpan<ScalarMaterialBinding> bindings,
        Func<SomeEngine.Resources.Texture?, uint>? textureIndex)
    {
        var result = new byte[ByteSize];
        WriteHeader(result);
        Span<byte> payload = result.AsSpan(HeaderByteSize, checked((int)PayloadByteSize));
        foreach (ref readonly ScalarMaterialBinding binding in bindings)
        {
            ScalarFieldLayout field = binding.Field;
            Span<byte> fieldBytes = payload.Slice((int)field.Offset, (int)field.Size);
            Type type = binding.PropertyType;
            if (type == typeof(float) || type == typeof(Vector2) ||
                type == typeof(Vector3) || type == typeof(Vector4))
                WriteFloatField(fieldBytes, field.ComponentCount, material.GetFloatComponents(binding.MaterialSlot));
            else if (type == typeof(int) || type == typeof(uint) || type == typeof(bool))
                WriteIntegerField(fieldBytes, field, unchecked((int)material.GetInteger(binding.MaterialSlot)));
            else if (typeof(SomeEngine.Resources.Texture).IsAssignableFrom(type))
            {
                if (textureIndex is null || field.Size != sizeof(uint) ||
                    field.ScalarType != (byte)SlangScalarType.UInt32)
                    throw new InvalidDataException($"Indexed texture '{field.Name}' requires a uint scalar field.");
                uint index = textureIndex(material.GetTexture(binding.MaterialSlot));
                BinaryPrimitives.WriteUInt32LittleEndian(fieldBytes, index);
            }
            else
                throw new InvalidDataException(
                    $"Material slot {binding.MaterialSlot} cannot be encoded as scalar data.");
        }
        return result;
    }

    internal sealed class ScalarMaterialLayout
    {
        private readonly ScalarLayout _layout;
        private readonly ScalarMaterialBinding[] _bindings;

        internal ScalarMaterialLayout(ScalarLayout layout, ScalarMaterialBinding[] bindings)
        {
            _layout = layout;
            _bindings = bindings;
        }

        internal int ByteSize => _layout.ByteSize;
        internal uint LayoutHash => _layout.LayoutHash;
        internal uint PayloadByteSize => _layout.PayloadByteSize;

        internal byte[] Encode(Material material, Func<SomeEngine.Resources.Texture?, uint>? textureIndex = null)
            => _layout.Encode(material, _bindings, textureIndex);

        internal IEnumerable<uint> TextureProperties => _bindings
            .Where(static binding => typeof(SomeEngine.Resources.Texture).IsAssignableFrom(binding.PropertyType))
            .Select(static binding => binding.MaterialSlot);
    }

    internal readonly record struct ScalarMaterialBinding(
        uint MaterialSlot,
        ScalarFieldLayout Field,
        Type PropertyType);

    private bool TryField(
        string name,
        Span<byte> payload,
        out ScalarFieldLayout field,
        out Span<byte> fieldBytes)
    {
        for (int i = 0; i < _fields.Count; i++)
        {
            field = _fields[i];
            if (string.Equals(field.Name, name, StringComparison.Ordinal))
            {
                fieldBytes = payload.Slice((int)field.Offset, (int)field.Size);
                return true;
            }
        }

        field = default;
        fieldBytes = default;
        return false;
    }

    private static void WriteIntegerField(Span<byte> fieldBytes, ScalarFieldLayout field, int raw)
    {
        switch (field.ScalarType)
        {
            case ScalarBool:
            case ScalarInt32:
            case ScalarUInt32:
                if (fieldBytes.Length >= sizeof(uint))
                    BinaryPrimitives.WriteUInt32LittleEndian(fieldBytes, unchecked((uint)raw));
                break;
            case ScalarFloat32:
                WriteFloatField(fieldBytes, field.ComponentCount, new Vector4(raw, 0, 0, 0));
                break;
        }
    }

    private static void WriteFloatField(Span<byte> fieldBytes, uint componentCount, Vector4 vector)
    {
        int writableComponents = Math.Min((int)componentCount, fieldBytes.Length / sizeof(uint));
        for (int i = 0; i < writableComponents; i++)
        {
            WriteFloat(fieldBytes, i * sizeof(uint), GetComponent(vector, i));
        }
    }

    private static float GetComponent(Vector4 value, int index)
        => index switch
        {
            0 => value.X,
            1 => value.Y,
            2 => value.Z,
            3 => value.W,
            _ => 0,
        };

    private static void WriteFloat(Span<byte> destination, int byteOffset, float value)
        => BinaryPrimitives.WriteUInt32LittleEndian(
            destination.Slice(byteOffset, sizeof(uint)),
            BitConverter.SingleToUInt32Bits(value));

    private static uint ComputeHash(
        string name,
        IReadOnlyList<ScalarFieldLayout> fields,
        uint payloadByteSize)
    {
        const uint offsetBasis = 2166136261u;
        const uint prime = 16777619u;

        uint hash = offsetBasis;
        foreach (char character in name)
            HashUInt(ref hash, character);
        HashUInt(ref hash, 0);
        HashUInt(ref hash, payloadByteSize);
        foreach (ScalarFieldLayout field in fields)
        {
            foreach (char c in field.Name)
            {
                HashUInt(ref hash, c);
            }

            HashUInt(ref hash, 0);
            HashUInt(ref hash, field.Offset);
            HashUInt(ref hash, field.Size);
            HashUInt(ref hash, field.RowCount);
            HashUInt(ref hash, field.ColumnCount);
            HashUInt(ref hash, field.ScalarType);
        }

        return hash;

        static void HashUInt(ref uint hash, uint value)
        {
            hash ^= value;
            hash *= prime;
        }
    }

    private static int AlignUp(int value, int alignment)
        => ((value + alignment - 1) / alignment) * alignment;

    // Values are persisted from the cooked shader contract's Slang scalar-type numbering.
    private const byte ScalarBool = 2;
    private const byte ScalarInt32 = 3;
    private const byte ScalarUInt32 = 4;
    private const byte ScalarFloat32 = 8;
}

public readonly record struct ScalarFieldLayout(
    string Name,
    uint Offset,
    uint Size,
    uint RowCount,
    uint ColumnCount,
    byte ScalarType)
{
    public uint ComponentCount
    {
        get
        {
            uint rows = RowCount == 0 ? 1 : RowCount;
            uint columns = ColumnCount == 0 ? 1 : ColumnCount;
            return Math.Max(1u, rows * columns);
        }
    }
}

