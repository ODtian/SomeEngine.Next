using SlangShaderSharp;
using SomeEngine.Graphics;

namespace SomeEngine.Render.Features;

/// <summary>
/// Captures the target-specific descriptor order once when a feature pipeline is created. Frame
/// recording uses the resulting compact keys and performs no reflection or string lookup.
/// </summary>
internal static class FeatureResourceLayout
{
    internal static TKey[] Resolve<TKey>(
        VariableLayoutReflection layout,
        Func<string, ResourceBindingType, TKey?> resolve)
        where TKey : struct
    {
        ArgumentNullException.ThrowIfNull(resolve);
        if (layout == VariableLayoutReflection.Null)
            throw new InvalidDataException("A feature shader has no linked parameter layout.");
        TypeLayoutReflection contents = layout.TypeLayout.UnwrapArray();
        if (contents.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            contents = contents.ElementTypeLayout.UnwrapArray();
        int rangeCount = Count(contents.BindingRangeCount, "binding ranges");
        int setCount = Count(contents.DescriptorSetCount, "descriptor sets");
        var result = new List<TKey>();
        for (int range = 0; range < rangeCount; range++)
        {
            int descriptorRangeCount = Count(
                contents.GetBindingRangeDescriptorRangeCount(range),
                $"descriptor ranges for binding range {range}");
            if (descriptorRangeCount == 0)
                continue;
            int set = Index(
                contents.GetBindingRangeDescriptorSetIndex(range),
                setCount,
                $"descriptor set for binding range {range}");
            int first = Count(
                contents.GetBindingRangeFirstDescriptorRangeIndex(range),
                $"first descriptor range for binding range {range}");
            int available = Count(
                contents.GetDescriptorSetDescriptorRangeCount(set),
                $"descriptor ranges for set {set}");
            if (first > available || descriptorRangeCount > available - first)
                throw new InvalidDataException("A feature shader descriptor range is out of bounds.");
            string name = contents.GetBindingRangeLeafVariable(range).Name;
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException("A feature shader resource has no field name.");
            int logicalCount = Count(
                contents.GetBindingRangeBindingCount(range),
                $"descriptor count for binding range {range}");
            for (int relative = 0; relative < descriptorRangeCount; relative++)
            {
                int descriptorRange = first + relative;
                int descriptorCount = Count(
                    contents.GetDescriptorSetDescriptorRangeDescriptorCount(set, descriptorRange),
                    $"descriptor count for set {set}, range {descriptorRange}");
                if (descriptorCount != logicalCount)
                    throw new InvalidDataException($"Feature shader resource '{name}' has inconsistent descriptor counts.");
                ResourceBindingType type = ConvertType(
                    contents.GetDescriptorSetDescriptorRangeType(set, descriptorRange),
                    contents.GetDescriptorSetDescriptorRangeCategory(set, descriptorRange));
                TKey key = resolve(name, type) ?? throw new InvalidDataException(
                    $"Feature shader resource '{name}' ({type}) has no installed binding.");
                for (int element = 0; element < descriptorCount; element++)
                    result.Add(key);
            }
        }
        return [.. result];
    }

    private static ResourceBindingType ConvertType(
        SlangBindingType reflected,
        SlangParameterCategory category)
    {
        SlangBindingType type = reflected & SlangBindingType.BaseMask;
        return type switch
        {
            SlangBindingType.Sampler => ResourceBindingType.Sampler,
            SlangBindingType.ConstantBuffer => ResourceBindingType.ConstantBuffer,
            SlangBindingType.Texture => (reflected & SlangBindingType.MutableFlag) != 0
                ? ResourceBindingType.TextureUav
                : ResourceBindingType.TextureSrv,
            SlangBindingType.TypedBuffer or SlangBindingType.RawBuffer =>
                (reflected & SlangBindingType.MutableFlag) != 0
                    ? ResourceBindingType.BufferUav
                    : ResourceBindingType.BufferSrv,
            SlangBindingType.RayTracingAccelerationStructure => ResourceBindingType.AccelerationStructure,
            SlangBindingType.InputRenderTarget => ResourceBindingType.TextureSrv,
            SlangBindingType.CombinedTextureSampler when category == SlangParameterCategory.ShaderResource =>
                ResourceBindingType.TextureSrv,
            SlangBindingType.CombinedTextureSampler when category == SlangParameterCategory.SamplerState =>
                ResourceBindingType.Sampler,
            _ => throw new InvalidDataException(
                $"Feature shader binding type {reflected} cannot be represented by the RHI."),
        };
    }

    private static int Index(nint value, int limit, string description)
    {
        int result = Count(value, description);
        return result < limit
            ? result
            : throw new InvalidDataException($"Feature shader {description} index {result} is out of range.");
    }

    private static int Count(nint value, string description)
    {
        nuint marker = unchecked((nuint)value);
        if (value < 0 || marker == Slang.UnknownSize || marker == Slang.UnboundedSize ||
            value > int.MaxValue)
        {
            throw new InvalidDataException($"Feature shader {description} are unresolved.");
        }
        return checked((int)value);
    }
}
