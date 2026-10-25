using System.Collections.Immutable;
using SlangShaderSharp;
using SomeEngine.Graphics;

namespace SomeEngine.Resources;

public partial class Shader
{
    private readonly Dictionary<(int Target, string FirstEntry, string? SecondEntry), ShaderParameterLayout>
        _slangResourceLayouts = [];

    /// <summary>Captures resource order and use for this shader's linked target and selected entries.</summary>
    /// <remarks>
    /// Use after linking and saved-usage restoration are complete. The cache lock serializes
    /// capture for this Shader; it does not authorize concurrent mutation of a shared Slang session.
    /// </remarks>
    public ShaderParameterLayout GetSlangResourceLayout(
        SlangCompileTarget target,
        string firstEntry,
        string? secondEntry = null)
    {
        int targetIndex = RequireSlangTarget(target);
        var key = (targetIndex, firstEntry, secondEntry);
        lock (_slangResourceLayouts)
        {
            if (_slangResourceLayouts.TryGetValue(key, out ShaderParameterLayout? layout))
                return layout;
            layout = ShaderParameterLayout.Create(this, targetIndex, firstEntry, secondEntry);
            _slangResourceLayouts.Add(key, layout);
            return layout;
        }
    }
}

/// <summary>Bounded resource order captured from the final linked Slang program.</summary>
public sealed class ShaderParameterLayout
{
    private readonly ShaderParameterResource[] _resources;
    // ParameterLayout markers borrow native reflection handles. Retain the Shader's
    // global/session/module/code owners for as long as this layout remains reachable.
    private readonly Shader? _owner;

    private ShaderParameterLayout(ShaderParameterResource[] resources, Shader? owner = null)
    {
        _resources = resources;
        _owner = owner;
    }

    /// <summary>Keep this layout alive while consuming its borrowed parameter-block markers.</summary>
    public ReadOnlySpan<ShaderParameterResource> Resources => _resources;

    public bool Contains(string name)
    {
        foreach (ShaderParameterResource resource in _resources)
        {
            if (resource.IsUsed && string.Equals(resource.Name, name, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    internal static ShaderParameterLayout Create(
        Shader shader,
        int targetIndex,
        string firstEntry,
        string? secondEntry = null)
    {
        try
        {
            ShaderReflection reflection = shader.GetSlangReflection(targetIndex);
            string[] names = secondEntry is null ? [firstEntry] : [firstEntry, secondEntry];
            List<ShaderParameterResource> result = [];
            var entryLayouts = new List<VariableLayoutReflection>(names.Length);
            foreach (string name in names)
            {
                if (!shader.TryGetSlangEntry(targetIndex, name, out EntryPointReflection entry))
                    throw new InvalidDataException($"Shader '{shader.Name}' has no Slang entry point named '{name}'.");
                entryLayouts.Add(entry.VarLayout);
            }
            ImmutableHashSet<SlangDescriptorLocation>? firstUsedParameters = null;
            ImmutableHashSet<SlangDescriptorLocation>? secondUsedParameters = null;
            AddBlock(reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null, IsUsedByEntry);
            foreach (VariableLayoutReflection entryLayout in entryLayouts) AddBlock(entryLayout, null);
            return new ShaderParameterLayout([.. result], shader);

            bool IsUsedByEntry(SlangParameterCategory category, nuint space, nuint register)
            {
                firstUsedParameters ??= shader.GetSlangUsedParameters(targetIndex, firstEntry);
                var location = new SlangDescriptorLocation(category, checked((ulong)space), checked((ulong)register));
                if (firstUsedParameters.Contains(location))
                    return true;
                if (secondEntry is null)
                    return false;
                secondUsedParameters ??= shader.GetSlangUsedParameters(targetIndex, secondEntry);
                return secondUsedParameters.Contains(location);
            }

            void AddBlock(
                VariableLayoutReflection layout,
                Func<SlangParameterCategory, nuint, nuint, bool>? isUsed)
            {
                if (layout == VariableLayoutReflection.Null)
                    return;
                ShaderParameterLayout block = Create(layout, isUsed);
                if (block._resources.Length == 0)
                    return;
                result.Add(new ShaderParameterResource("", ResourceBindingType.None, 0)
                { ParameterLayout = layout, IsUsed = false });
                result.AddRange(block._resources);
            }
        }
        finally
        {
            GC.KeepAlive(shader);
        }
    }

    /// <summary>Reads a borrowed block now; the returned descriptors contain no block-layout markers.</summary>
    /// <remarks>The caller keeps the reflection's native owner alive through this call.</remarks>
    public static ShaderParameterLayout FromReflection(VariableLayoutReflection layout)
        => Create(layout, null);

    /// <summary>Reads a borrowed block while retaining a caller-supplied selected-entry use mask.</summary>
    /// <remarks>The caller keeps the reflection's native owner alive through this call.</remarks>
    public static ShaderParameterLayout FromReflection(
        VariableLayoutReflection layout,
        Func<SlangParameterCategory, nuint, nuint, bool>? isUsed)
        => Create(layout, isUsed);

    private static ShaderParameterLayout Create(
        VariableLayoutReflection layout,
        Func<SlangParameterCategory, nuint, nuint, bool>? isUsed)
    {
        if (layout == VariableLayoutReflection.Null)
            throw new InvalidDataException("A material stage has no linked parameter layout.");
        TypeLayoutReflection contents = layout.TypeLayout.UnwrapArray();
        if (contents.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            contents = contents.ElementTypeLayout.UnwrapArray();
        int rangeCount = ResolveCount(contents.BindingRangeCount, "binding ranges");
        int setCount = ResolveCount(contents.DescriptorSetCount, "descriptor sets");
        var resources = new List<ShaderParameterResource>();
        for (int range = 0; range < rangeCount; range++)
        {
            int descriptorRangeCount = ResolveCount(
                contents.GetBindingRangeDescriptorRangeCount(range),
                $"descriptor ranges for binding range {range}");
            if (descriptorRangeCount == 0)
                continue;
            int set = ResolveIndex(
                contents.GetBindingRangeDescriptorSetIndex(range),
                setCount,
                $"descriptor set for binding range {range}");
            int firstDescriptorRange = ResolveCount(
                contents.GetBindingRangeFirstDescriptorRangeIndex(range),
                $"first descriptor range for binding range {range}");
            int availableRanges = ResolveCount(
                contents.GetDescriptorSetDescriptorRangeCount(set),
                $"descriptor ranges for set {set}");
            if (firstDescriptorRange > availableRanges ||
                descriptorRangeCount > availableRanges - firstDescriptorRange)
            {
                throw new InvalidDataException(
                    $"Linked material binding range {range} references descriptors outside set {set}.");
            }

            string name = contents.GetBindingRangeLeafVariable(range).Name;
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException($"Linked material binding range {range} has no field name.");
            nint reflectedCount = contents.GetBindingRangeBindingCount(range);
            // Unbounded descriptor tables are bound by the RHI. Their actual
            // material texture reads are declared through indexed scalar fields.
            if (unchecked((nuint)reflectedCount) == Slang.UnboundedSize)
            {
                continue;
            }
            int logicalCount = ResolveCount(
                reflectedCount,
                $"descriptor count for binding range {range}");
            for (int relative = 0; relative < descriptorRangeCount; relative++)
            {
                int descriptorRange = firstDescriptorRange + relative;
                int descriptorCount = ResolveCount(
                    contents.GetDescriptorSetDescriptorRangeDescriptorCount(set, descriptorRange),
                    $"descriptor count for set {set}, range {descriptorRange}");
                if (descriptorCount != logicalCount)
                {
                    throw new InvalidDataException(
                        $"Linked material binding '{name}' has inconsistent logical and native descriptor counts.");
                }
                ResourceBindingType type = ExpectedType(
                    contents.GetDescriptorSetDescriptorRangeType(set, descriptorRange),
                    contents.GetDescriptorSetDescriptorRangeCategory(set, descriptorRange));
                for (int element = 0; element < descriptorCount; element++)
                {
                    bool used = true;
                    if (isUsed is not null)
                    {
                        var location = SlangDescriptorLocation.FromLayout(layout, contents, set, descriptorRange, element);
                        used = isUsed(
                            location.Category,
                            checked((nuint)location.Space),
                            checked((nuint)location.Register));
                    }
                    resources.Add(new ShaderParameterResource(name, type, checked((uint)element))
                    {
                        IsUsed = used,
                    });
                }
            }
        }
        return new ShaderParameterLayout([.. resources]);
    }

    private static ResourceBindingType ExpectedType(
        SlangBindingType reflected,
        SlangParameterCategory category)
    {
        SlangBindingType type = reflected & SlangBindingType.BaseMask;
        return type switch
        {
            SlangBindingType.Sampler => ResourceBindingType.Sampler,
            SlangBindingType.ConstantBuffer => ResourceBindingType.ConstantBuffer,
            SlangBindingType.Texture =>
                (reflected & SlangBindingType.MutableFlag) != 0
                    ? ResourceBindingType.TextureUav
                    : ResourceBindingType.TextureSrv,
            SlangBindingType.TypedBuffer or SlangBindingType.RawBuffer =>
                (reflected & SlangBindingType.MutableFlag) != 0
                    ? ResourceBindingType.BufferUav
                    : ResourceBindingType.BufferSrv,
            SlangBindingType.RayTracingAccelerationStructure =>
                ResourceBindingType.AccelerationStructure,
            SlangBindingType.InputRenderTarget => ResourceBindingType.TextureSrv,
            SlangBindingType.CombinedTextureSampler when
                category == SlangParameterCategory.ShaderResource =>
                ResourceBindingType.TextureSrv,
            SlangBindingType.CombinedTextureSampler when
                category == SlangParameterCategory.SamplerState =>
                ResourceBindingType.Sampler,
            _ => throw new InvalidDataException(
                $"Linked material binding type {reflected} cannot be represented by the RHI."),
        };
    }

    private static int ResolveIndex(nint value, int limit, string description)
    {
        int result = ResolveCount(value, description);
        return result < limit
            ? result
            : throw new InvalidDataException($"Linked {description} index {result} is out of range.");
    }

    private static int ResolveCount(nint value, string description)
    {
        nuint marker = unchecked((nuint)value);
        if (value < 0 || marker == Slang.UnknownSize || marker == Slang.UnboundedSize ||
            value > int.MaxValue)
            throw new InvalidDataException($"Linked material {description} are unresolved.");
        return checked((int)value);
    }
}

public readonly record struct ShaderParameterResource(
    string Name,
    ResourceBindingType Type,
    uint Element,
    bool IsUsed = true)
{
    public VariableLayoutReflection ParameterLayout { get; init; } = VariableLayoutReflection.Null;
}
