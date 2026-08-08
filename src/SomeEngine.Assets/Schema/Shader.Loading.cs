using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

public partial class Shader
{
    internal static async ValueTask<Shader> LoadAssetAsync(
        AssetLoadContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BinaryDocument<Shader> document = await context
            .OpenAsync<Shader>()
            .ConfigureAwait(false);
        Shader asset = await MaterializeAsync(document, cancellationToken).ConfigureAwait(false);
        Validate(asset);
        return asset;
    }

    public bool TryVariant(
        string backend,
        string entryPoint,
        out ShaderBytecode variant)
        => TryVariant(backend, entryPoint, stage: null, out variant);

    public bool TryVariant(
        string backend,
        string entryPoint,
        ShaderStage stage,
        out ShaderBytecode variant)
        => TryVariant(backend, entryPoint, (ShaderStage?)stage, out variant);

    public bool TryReflection(
        string backend,
        string entryPoint,
        out ShaderEntryPointReflection reflection)
    {
        IList<ShaderEntryPointReflection>? reflections = EntryPointReflections;
        if (reflections is not null)
        {
            for (int index = 0; index < reflections.Count; index++)
            {
                ShaderEntryPointReflection candidate = reflections[index];
                if (string.Equals(candidate.Backend, backend, StringComparison.Ordinal)
                    && string.Equals(candidate.EntryPoint, entryPoint, StringComparison.Ordinal))
                {
                    reflection = candidate;
                    return true;
                }
            }
        }

        reflection = null!;
        return false;
    }

    public bool TryEntry(
        ShaderEntryPointAttribute attribute,
        string? preferred,
        out string entry)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        entry = string.Empty;
        IList<ShaderBytecode>? variants = Variants;
        if (variants is null
            || attribute.VariantIndex < 0
            || attribute.VariantIndex >= variants.Count)
        {
            return false;
        }

        entry = variants[attribute.VariantIndex].EntryPoint ?? string.Empty;
        return entry.Length != 0
            && (string.IsNullOrWhiteSpace(preferred)
                || string.Equals(entry, preferred, StringComparison.Ordinal));
    }

    private bool TryVariant(
        string backend,
        string entryPoint,
        ShaderStage? stage,
        out ShaderBytecode variant)
    {
        IList<ShaderBytecode>? variants = Variants;
        if (variants is not null)
        {
            for (int index = 0; index < variants.Count; index++)
            {
                ShaderBytecode candidate = variants[index];
                if (string.Equals(candidate.Backend, backend, StringComparison.Ordinal)
                    && string.Equals(candidate.EntryPoint, entryPoint, StringComparison.Ordinal)
                    && (!stage.HasValue || candidate.Stage == stage.Value))
                {
                    variant = candidate;
                    return true;
                }
            }
        }

        variant = null!;
        return false;
    }

    internal static void Validate(Shader asset)
    {
        if (asset.EntryPointReflections is not { Count: > 0 })
        {
            throw new InvalidDataException(
                $"Shader '{asset.Name}' has no serialized entry-point reflection. " +
                "Runtime loading never compiles or reflects source files; recook the shader.");
        }

        int variantCount = asset.Variants?.Count ?? 0;
        if (variantCount == 0)
            throw new InvalidDataException($"Shader '{asset.Name}' has no bytecode variant.");
        foreach (ShaderBytecode variant in asset.Variants!)
        {
            if (!variant.Data.HasValue)
                throw new InvalidDataException($"Shader '{asset.Name}' has an undecoded bytecode variant.");
        }

        if (asset.EntryPointAttributes is { Count: > 0 })
        {
            foreach (ShaderEntryPointAttribute attribute in asset.EntryPointAttributes)
            {
                if (attribute.VariantIndex < 0 || attribute.VariantIndex >= variantCount)
                    throw new InvalidDataException("Shader entry-point attribute references an invalid variant.");
            }
        }

        ValidateScalarLayouts(asset.Metadata?.MaterialScalarLayouts);
        ValidateMaterialInstanceProperties(asset.Metadata?.MaterialInstanceProperties);
    }

    private static void ValidateScalarLayouts(IList<ShaderMaterialScalarLayout>? layouts)
    {
        if (layouts is null)
            return;

        foreach (ShaderMaterialScalarLayout layout in layouts)
        {
            uint previousEnd = 0;
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (layout.Fields is null)
                continue;
            foreach (ShaderMaterialScalarField field in layout.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.Name) || !names.Add(field.Name))
                    throw new InvalidDataException($"Shader scalar layout '{layout.Name}' has a missing or duplicate field name.");
                if (field.Size == 0 || field.Offset < previousEnd || field.Offset + field.Size > layout.Size)
                    throw new InvalidDataException($"Shader scalar layout '{layout.Name}' is not canonical and ordered.");
                previousEnd = field.Offset + field.Size;
            }
        }
    }

    private static void ValidateMaterialInstanceProperties(
        IList<ShaderMaterialInstanceProperty>? properties)
    {
        if (properties is null)
            return;

        for (int index = 0; index < properties.Count; index++)
        {
            ShaderMaterialInstanceProperty property = properties[index];
            if (string.IsNullOrWhiteSpace(property.CanonicalId)
                || string.IsNullOrWhiteSpace(property.MaterialScalarLayoutName)
                || string.IsNullOrWhiteSpace(property.MaterialScalarName)
                || string.IsNullOrWhiteSpace(property.Accessor)
                || property.Size == 0
                || property.Alignment == 0
                || property.ScalarType == 0
                || property.DefaultValue is not { } value
                || value.Length != property.Size)
            {
                throw new InvalidDataException("Shader material-instance property metadata is not canonical.");
            }

            for (int previousIndex = 0; previousIndex < index; previousIndex++)
            {
                ShaderMaterialInstanceProperty previous = properties[previousIndex];
                if (string.Equals(previous.CanonicalId, property.CanonicalId, StringComparison.Ordinal)
                    && (string.Equals(
                            previous.MaterialScalarLayoutName,
                            property.MaterialScalarLayoutName,
                            StringComparison.Ordinal)
                        || !HasSameEncodingContract(previous, property)))
                {
                    throw new InvalidDataException(
                        $"Shader material-instance property '{property.CanonicalId}' is duplicated or changes its byte encoding.");
                }
            }
        }
    }

    private static bool HasSameEncodingContract(
        ShaderMaterialInstanceProperty left,
        ShaderMaterialInstanceProperty right)
        => left.Size == right.Size
            && left.Alignment == right.Alignment
            && left.RowCount == right.RowCount
            && left.ColumnCount == right.ColumnCount
            && left.ScalarType == right.ScalarType;
}
