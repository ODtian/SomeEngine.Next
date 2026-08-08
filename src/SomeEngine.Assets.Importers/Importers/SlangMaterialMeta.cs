using System.Text;
using System.Text.RegularExpressions;
using SlangShaderSharp;
using Schema = global::SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

internal static partial class SlangMaterialMeta
{
    public const byte Texture = 0;
    public const byte Sampler = 1;
    public const byte Buffer = 2;
    public const byte Unknown = 255;

    public static void BindEntry(
        VariableLayoutReflection parameter,
        Schema.ShaderMetadata metadata,
        HashSet<string>? resourceNames = null)
    {
        if (parameter == VariableLayoutReflection.Null)
        {
            return;
        }

        TypeLayoutReflection typeLayout = parameter.TypeLayout.UnwrapArray();
        if (typeLayout.Kind != SlangTypeKind.Struct)
        {
            return;
        }

        BindType(typeLayout, metadata, resourceNames);
    }

    public static void AddBinding(
        Schema.ShaderMetadata metadata,
        string? name,
        byte resourceType)
    {
        if (resourceType == Unknown || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        metadata.MaterialBindings ??= [];
        foreach (Schema.ShaderMaterialBinding existing in metadata.MaterialBindings)
        {
            if (!string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (existing.ResourceType != resourceType)
            {
                throw new InvalidOperationException(
                    $"Shader material binding '{name}' was reflected with conflicting resource types {existing.ResourceType} and {resourceType}.");
            }

            return;
        }

        metadata.MaterialBindings.Add(
            new Schema.ShaderMaterialBinding
            {
                Name = name,
                ResourceType = resourceType,
            });
    }

    public static List<string> ScalarTypes(
        DeclReflection root,
        string source,
        IReadOnlyList<DependencyEntryData> dependencies,
        string projectRoot)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddType(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
            {
                result.Add(name);
            }
        }

        void Visit(DeclReflection decl)
        {
            if (decl == DeclReflection.Null)
            {
                return;
            }

            if (decl.Kind == DeclReflectionKind.Struct)
            {
                TypeReflection type = decl.Type;
                if (type != TypeReflection.Null && HasAttribute(type, "MaterialScalars"))
                {
                    AddType(type.Name);
                }
            }

            for (int i = 0; i < decl.ChildrenCount; i++)
            {
                Visit(decl.GetChild((uint)i));
            }
        }

        Visit(root);
        ReadTypes(source, AddType);
        foreach (DependencyEntryData dependency in dependencies)
        {
            string path = DependencyPath(projectRoot, dependency.RelativePath);
            if (File.Exists(path))
            {
                ReadTypes(File.ReadAllText(path), AddType);
            }
        }

        return result;
    }

    public static void ScalarLayouts(
        ShaderReflection reflection,
        IReadOnlyList<string> materialScalarTypes,
        Schema.ShaderMetadata metadata)
    {
        if (reflection == ShaderReflection.Null || materialScalarTypes.Count == 0)
        {
            return;
        }

        var existing = new HashSet<string>(
            metadata.MaterialScalarLayouts?.Select(static layout => layout.Name ?? string.Empty)
                ?? [],
            StringComparer.Ordinal);

        metadata.MaterialScalarLayouts ??= [];

        foreach (string typeName in materialScalarTypes)
        {
            if (string.IsNullOrWhiteSpace(typeName) || !existing.Add(typeName))
            {
                continue;
            }

            TypeReflection? maybeLayoutType = reflection.FindTypeByName(typeName);
            if (maybeLayoutType == null)
            {
                continue;
            }

            TypeLayoutReflection? maybeLayout = reflection.GetTypeLayout(
                maybeLayoutType.Value,
                LayoutRules.Default);
            if (maybeLayout == null)
            {
                continue;
            }

            AddScalarLayout(metadata, typeName, maybeLayout.Value);
        }
    }

    public static void SortInstanceProperties(Schema.ShaderMetadata metadata)
    {
        if (metadata.MaterialInstanceProperties is not { Count: > 1 } properties)
        {
            return;
        }

        metadata.MaterialInstanceProperties = properties
            .OrderBy(static property => property.MaterialScalarLayoutName, StringComparer.Ordinal)
            .ThenBy(static property => property.CanonicalId, StringComparer.Ordinal)
            .ToList();
    }

    public static byte ResourceType(TypeReflection type)
    {
        if (type == TypeReflection.Null)
        {
            return Unknown;
        }

        type = type.UnwrapArray();
        SlangResourceShape shape = type.ResourceShape & SlangResourceShape.BaseShapeMask;

        return type.Kind switch
        {
            SlangTypeKind.SamplerState => Sampler,
            SlangTypeKind.ConstantBuffer => Buffer,
            SlangTypeKind.Resource
            or SlangTypeKind.TextureBuffer
            or SlangTypeKind.ShaderStorageBuffer =>
                shape is SlangResourceShape.StructuredBuffer
                    or SlangResourceShape.ByteAddressBuffer
                    or SlangResourceShape.TextureBuffer
                    ? Buffer
                    : Texture,
            _ => Unknown,
        };
    }

    private static void BindType(
        TypeLayoutReflection typeLayout,
        Schema.ShaderMetadata metadata,
        HashSet<string>? resourceNames)
    {
        if (typeLayout == TypeLayoutReflection.Null)
        {
            return;
        }

        for (uint fieldIndex = 0; fieldIndex < typeLayout.FieldCount; fieldIndex++)
        {
            VariableLayoutReflection field = typeLayout.GetFieldByIndex(fieldIndex);
            if (field == VariableLayoutReflection.Null || string.IsNullOrWhiteSpace(field.Name))
            {
                continue;
            }

            byte resourceType = ResourceType(field.Type);
            if (resourceType != Unknown)
            {
                AddBinding(metadata, field.Name, resourceType);
                resourceNames?.Add(field.Name);
                continue;
            }

            BindType(field.TypeLayout.UnwrapArray(), metadata, resourceNames);
        }
    }

    private static void AddScalarLayout(
        Schema.ShaderMetadata metadata,
        string typeName,
        TypeLayoutReflection typeLayout)
    {
        Schema.ShaderMaterialScalarLayout layout = CreateScalarLayout(metadata, typeName, typeLayout);
        metadata.MaterialScalarLayouts ??= [];
        metadata.MaterialScalarLayouts.Add(layout);
    }

    private static Schema.ShaderMaterialScalarLayout CreateScalarLayout(
        Schema.ShaderMetadata metadata,
        string typeName,
        TypeLayoutReflection typeLayout)
    {
        uint payloadSize = checked((uint)typeLayout.GetSize(SlangParameterCategory.Uniform));
        var fields = new List<Schema.ShaderMaterialScalarField>((int)typeLayout.FieldCount);
        ScalarOffsetRange offsetRange = AddScalarFields(metadata, typeName, typeLayout, fields);
        payloadSize = NormalizeScalarPayload(payloadSize, offsetRange, fields);
        return new Schema.ShaderMaterialScalarLayout
        {
            Name = typeName,
            Size = payloadSize,
            Fields = fields,
        };
    }

    private static ScalarOffsetRange AddScalarFields(
        Schema.ShaderMetadata metadata,
        string materialScalarLayoutName,
        TypeLayoutReflection typeLayout,
        List<Schema.ShaderMaterialScalarField> fields)
    {
        uint baseOffset = uint.MaxValue;
        uint maxFieldEnd = 0;
        for (uint fieldIndex = 0; fieldIndex < typeLayout.FieldCount; fieldIndex++)
        {
            VariableLayoutReflection fieldLayout = typeLayout.GetFieldByIndex(fieldIndex);
            if (fieldLayout == VariableLayoutReflection.Null || string.IsNullOrWhiteSpace(fieldLayout.Name))
            {
                continue;
            }

            TypeLayoutReflection fieldTypeLayout = fieldLayout.TypeLayout;
            uint fieldOffset = checked((uint)fieldLayout.GetOffset(SlangParameterCategory.Uniform));
            uint fieldSize = checked((uint)fieldTypeLayout.GetSize(SlangParameterCategory.Uniform));
            if (fieldSize == 0)
            {
                fieldSize = checked((uint)fieldTypeLayout.GetStride(SlangParameterCategory.Uniform));
            }

            uint fieldEnd = fieldOffset + fieldSize;
            baseOffset = Math.Min(baseOffset, fieldOffset);
            maxFieldEnd = Math.Max(maxFieldEnd, fieldEnd);
            fields.Add(CreateScalarField(fieldLayout, fieldTypeLayout, fieldOffset, fieldSize));
            AddInstanceProperty(
                metadata,
                materialScalarLayoutName,
                fieldLayout,
                fieldTypeLayout,
                fieldSize);
        }

        return new ScalarOffsetRange(baseOffset, maxFieldEnd);
    }

    private static Schema.ShaderMaterialScalarField CreateScalarField(
        VariableLayoutReflection fieldLayout,
        TypeLayoutReflection fieldTypeLayout,
        uint fieldOffset,
        uint fieldSize)
    {
        return new Schema.ShaderMaterialScalarField
        {
            Name = fieldLayout.Name,
            Offset = fieldOffset,
            Size = fieldSize,
            RowCount = fieldTypeLayout.RowCount,
            ColumnCount = fieldTypeLayout.ColumnCount,
            ScalarType = checked((byte)fieldTypeLayout.ScalarType),
        };
    }

    private static void AddInstanceProperty(
        Schema.ShaderMetadata metadata,
        string materialScalarLayoutName,
        VariableLayoutReflection fieldLayout,
        TypeLayoutReflection fieldTypeLayout,
        uint fieldSize)
    {
        VariableReflection variable = fieldLayout.Variable;
        AttributeReflection attribute = FindAttribute(variable, "InstanceProperty");
        if (attribute == AttributeReflection.Null)
        {
            return;
        }

        if (attribute.ArgumentCount != 2)
        {
            throw new InvalidOperationException(
                $"Material scalar field '{fieldLayout.Name}' must declare InstanceProperty with exactly canonical-id and accessor arguments.");
        }

        string canonicalId = attribute.GetArgumentValueString(0);
        string accessor = attribute.GetArgumentValueString(1);
        ValidateInstancePropertyIdentity(fieldLayout.Name, canonicalId, accessor);

        byte scalarType = checked((byte)fieldTypeLayout.ScalarType);
        if (scalarType == 0 || fieldSize == 0)
        {
            throw new NotSupportedException(
                $"Material instance property '{canonicalId}' must be a fixed-size scalar, vector, or matrix value.");
        }

        int reflectedAlignment = fieldTypeLayout.GetAlignment(SlangParameterCategory.Uniform);
        if (reflectedAlignment <= 0)
        {
            throw new InvalidOperationException(
                $"Material instance property '{canonicalId}' has invalid reflected alignment {reflectedAlignment}.");
        }

        var property = new Schema.ShaderMaterialInstanceProperty
        {
            CanonicalId = canonicalId,
            MaterialScalarLayoutName = materialScalarLayoutName,
            MaterialScalarName = fieldLayout.Name,
            Accessor = accessor,
            Size = fieldSize,
            Alignment = checked((uint)reflectedAlignment),
            RowCount = fieldTypeLayout.RowCount,
            ColumnCount = fieldTypeLayout.ColumnCount,
            ScalarType = scalarType,
            DefaultValue = new byte[checked((int)fieldSize)],
        };

        metadata.MaterialInstanceProperties ??= [];
        foreach (Schema.ShaderMaterialInstanceProperty existing in metadata.MaterialInstanceProperties)
        {
            bool sameLayout = string.Equals(
                existing.MaterialScalarLayoutName,
                materialScalarLayoutName,
                StringComparison.Ordinal);

            if (string.Equals(existing.CanonicalId, canonicalId, StringComparison.Ordinal))
            {
                if (sameLayout)
                {
                    if (!SameInstanceProperty(existing, property))
                    {
                        throw new InvalidOperationException(
                            $"Material instance property '{canonicalId}' was reflected with conflicting " +
                            $"contracts in scalar layout '{materialScalarLayoutName}'.");
                    }

                    return;
                }

                if (!SameInstanceEncoding(existing, property))
                {
                    throw new InvalidOperationException(
                        $"Material instance property '{canonicalId}' changes its byte encoding " +
                        "across material scalar layouts.");
                }

                continue;
            }
        }

        metadata.MaterialInstanceProperties.Add(property);
    }

    private static AttributeReflection FindAttribute(VariableReflection variable, string name)
    {
        if (variable == VariableReflection.Null)
        {
            return AttributeReflection.Null;
        }

        for (uint index = 0; index < variable.AttributeCount; index++)
        {
            AttributeReflection attribute = variable.GetAttribute(index);
            if (attribute != AttributeReflection.Null
                && string.Equals(attribute.Name, name, StringComparison.Ordinal))
            {
                return attribute;
            }
        }

        return AttributeReflection.Null;
    }

    private static void ValidateInstancePropertyIdentity(
        string fieldName,
        string canonicalId,
        string accessor)
    {
        if (!IsCanonicalId(canonicalId))
        {
            throw new InvalidOperationException(
                $"Material scalar field '{fieldName}' declares an invalid InstanceProperty canonical id.");
        }

        if (!IsIdentifier(accessor))
        {
            throw new InvalidOperationException(
                $"Material instance property '{canonicalId}' declares invalid accessor '{accessor}'.");
        }
    }

    private static bool IsCanonicalId(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        bool segmentStart = true;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character == '.')
            {
                if (segmentStart)
                {
                    return false;
                }

                segmentStart = true;
                continue;
            }

            if (segmentStart)
            {
                if (character is < 'a' or > 'z')
                {
                    return false;
                }

                segmentStart = false;
                continue;
            }

            if (character is not (>= 'a' and <= 'z')
                && character is not (>= '0' and <= '9')
                && character != '_')
            {
                return false;
            }
        }

        return !segmentStart;
    }

    private static bool IsIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value) || !(value[0] == '_' || char.IsLetter(value[0])))
        {
            return false;
        }

        for (int index = 1; index < value.Length; index++)
        {
            if (value[index] != '_' && !char.IsLetterOrDigit(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameInstanceProperty(
        Schema.ShaderMaterialInstanceProperty left,
        Schema.ShaderMaterialInstanceProperty right)
    {
        if (!string.Equals(left.CanonicalId, right.CanonicalId, StringComparison.Ordinal)
            || !string.Equals(
                left.MaterialScalarLayoutName,
                right.MaterialScalarLayoutName,
                StringComparison.Ordinal)
            || !string.Equals(left.MaterialScalarName, right.MaterialScalarName, StringComparison.Ordinal)
            || !string.Equals(left.Accessor, right.Accessor, StringComparison.Ordinal)
            || left.Size != right.Size
            || left.Alignment != right.Alignment
            || left.RowCount != right.RowCount
            || left.ColumnCount != right.ColumnCount
            || left.ScalarType != right.ScalarType
            || left.DefaultValue is not { } leftDefault
            || right.DefaultValue is not { } rightDefault)
        {
            return false;
        }

        return leftDefault.Span.SequenceEqual(rightDefault.Span);
    }

    private static bool SameInstanceEncoding(
        Schema.ShaderMaterialInstanceProperty left,
        Schema.ShaderMaterialInstanceProperty right)
    {
        if (!string.Equals(left.CanonicalId, right.CanonicalId, StringComparison.Ordinal)
            || left.Size != right.Size
            || left.Alignment != right.Alignment
            || left.RowCount != right.RowCount
            || left.ColumnCount != right.ColumnCount
            || left.ScalarType != right.ScalarType)
        {
            return false;
        }

        return true;
    }

    private static uint NormalizeScalarPayload(
        uint payloadSize,
        ScalarOffsetRange offsetRange,
        List<Schema.ShaderMaterialScalarField> fields)
    {
        if (payloadSize == 0)
        {
            payloadSize = offsetRange.MaxFieldEnd;
        }

        if (offsetRange.BaseOffset != uint.MaxValue && offsetRange.BaseOffset > 0)
        {
            if (payloadSize >= offsetRange.MaxFieldEnd)
            {
                payloadSize -= offsetRange.BaseOffset;
            }

            payloadSize = Math.Max(payloadSize, offsetRange.MaxFieldEnd - offsetRange.BaseOffset);
            NormalizeScalarFieldOffsets(fields, offsetRange.BaseOffset);
        }

        return payloadSize;
    }

    private static void NormalizeScalarFieldOffsets(
        List<Schema.ShaderMaterialScalarField> fields,
        uint baseOffset)
    {
        for (int fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
        {
            Schema.ShaderMaterialScalarField field = fields[fieldIndex];
            field.Offset -= baseOffset;
            fields[fieldIndex] = field;
        }
    }

    private readonly record struct ScalarOffsetRange(uint BaseOffset, uint MaxFieldEnd);

    private static void ReadTypes(
        string source,
        Action<string> addType)
    {
        foreach (Match match in MaterialScalarsRegex().Matches(source))
        {
            if (match.Groups.Count > 1)
            {
                addType(match.Groups[1].Value);
            }
        }
    }

    private static bool HasAttribute(TypeReflection type, string name)
    {
        for (uint i = 0; i < type.AttributeCount; i++)
        {
            AttributeReflection attribute = type.GetAttribute(i);
            if (attribute != AttributeReflection.Null
                && string.Equals(attribute.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string DependencyPath(string projectRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            return Path.GetFullPath(relativePath);
        }

        return Path.GetFullPath(Path.Combine(projectRoot, relativePath));
    }

    [GeneratedRegex(
        @"\[\s*MaterialScalars(?:\s*\([^\)]*\))?\s*\]\s*struct\s+([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex MaterialScalarsRegex();
}

