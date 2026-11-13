using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render;

public sealed class ShaderParameterResources
{
    private readonly ShaderParameterResource[] _resources;

    private ShaderParameterResources(ShaderParameterResource[] resources)
        => _resources = resources;

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

    public static ShaderParameterResources Create(
        Shader shader,
        SlangCompileTarget target,
        string firstEntry,
        string? secondEntry = null)
    {
        int targetIndex = shader.RequireSlangTarget(target);
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
        AddBlock(reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null, IsUsedByEntry);
        foreach (VariableLayoutReflection entryLayout in entryLayouts) AddBlock(entryLayout, null);
        return new ShaderParameterResources([.. result]);

        bool IsUsedByEntry(SlangParameterCategory category, nuint space, nuint register) =>
            names.Any(name => shader.IsSlangParameterLocationUsed(targetIndex, name, category, space, register));

        void AddBlock(
            VariableLayoutReflection layout,
            Func<SlangParameterCategory, nuint, nuint, bool>? isUsed)
        {
            if (layout == VariableLayoutReflection.Null)
                return;
            ShaderParameterResources block = Create(layout, isUsed);
            if (block._resources.Length == 0)
                return;
            result.Add(new ShaderParameterResource("", ResourceBindingType.None, 0)
            { ParameterLayout = layout, IsUsed = false });
            result.AddRange(block._resources);
        }
    }

    public static ShaderParameterResources Create(VariableLayoutReflection layout)
        => Create(layout, null);

    public static ShaderParameterResources Create(
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
                SlangParameterCategory category =
                    contents.GetDescriptorSetDescriptorRangeCategory(set, descriptorRange);
                for (int element = 0; element < descriptorCount; element++)
                {
                    var location = SlangDescriptorLocation.FromLayout(layout, contents, set, descriptorRange, element);
                    resources.Add(new ShaderParameterResource(name, type, checked((uint)element))
                    {
                        IsUsed = isUsed?.Invoke(
                            location.Category,
                            checked((nuint)location.Space),
                            checked((nuint)location.Register)) ?? true,
                    });
                }
            }
        }
        return new ShaderParameterResources([.. resources]);
    }

    public void ValidateMaterialResources(IReadOnlyDictionary<string, byte> materialResources)
    {
        foreach ((string name, byte kind) in materialResources)
        {
            bool found = false;
            foreach (ShaderParameterResource resource in _resources)
            {
                if (!string.Equals(resource.Name, name, StringComparison.Ordinal))
                    continue;
                bool compatible = kind switch
                {
                    0 => resource.Type is ResourceBindingType.TextureSrv or ResourceBindingType.TextureUav,
                    1 => resource.Type == ResourceBindingType.Sampler,
                    2 => resource.Type is ResourceBindingType.BufferSrv or ResourceBindingType.BufferUav or
                        ResourceBindingType.ConstantBuffer,
                    3 => resource.Type == ResourceBindingType.AccelerationStructure,
                    _ => throw new InvalidDataException(
                        $"Material resource '{name}' has unsupported cooked kind {kind}."),
                };
                if (!compatible)
                {
                    throw new InvalidDataException(
                        $"Material resource '{name}' cooked kind {kind} is incompatible with " +
                        $"{resource.Type} in linked reflection.");
                }
                found = true;
            }
            if (!found)
            {
                string linkedNames = string.Join(", ", _resources.Select(static resource => resource.Name));
                throw new InvalidDataException(
                    $"Material resource '{name}' is absent from the final linked entry layout. " +
                    $"Linked resources: [{linkedNames}].");
            }
        }
    }

    public GraphResourceBinder?[] ResolveFeatureResources(
        IReadOnlyDictionary<string, byte> materialResources,
        IGraphResourceProvider? provider,
        Func<string, bool> isCoreResource,
        bool allowNeutralRasterTarget = false)
    {
        ArgumentNullException.ThrowIfNull(isCoreResource);
        var result = new GraphResourceBinder?[_resources.Length];
        for (int index = 0; index < _resources.Length; index++)
        {
            ShaderParameterResource resource = _resources[index];
            if (!resource.IsUsed)
                continue;
            if (isCoreResource(resource.Name) ||
                materialResources.ContainsKey(resource.Name))
            {
                continue;
            }
            if (resource.Element != 0)
            {
                throw new NotSupportedException(
                    $"Feature stage resource arrays are not supported: '{resource.Name}[{resource.Element}]'.");
            }
            if (provider is null ||
                !provider.TryResolve(resource.Name, resource.Type, out GraphResourceBinder? binder) ||
                binder is null)
            {
                if (allowNeutralRasterTarget &&
                    (resource.Name, resource.Type) is
                    ("RasterTargetViews", ResourceBindingType.BufferSrv) or
                    ("RasterTargetPageHierarchy", ResourceBindingType.BufferSrv) or
                    ("RasterTargetDepthCacheClasses", ResourceBindingType.BufferSrv) or
                    ("RasterTargetPageTable", ResourceBindingType.BufferSrv) or
                    ("RasterTargetReceiverMasks", ResourceBindingType.TextureSrv) or
                    ("RasterTargetPageMetadata", ResourceBindingType.BufferSrv) or
                    ("RasterTargetOutput", ResourceBindingType.TextureUav) or
                    ("RasterTargetStaticOutput", ResourceBindingType.TextureUav))
                    continue;
                throw new InvalidDataException(
                    $"Final stage entry requires feature resource '{resource.Name}' ({resource.Type}), " +
                    "but no installed feature owns it.");
            }
            result[index] = binder;
        }
        return result;
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
    uint Element)
{
    public bool IsUsed { get; init; } = true;
    public VariableLayoutReflection ParameterLayout { get; init; } = VariableLayoutReflection.Null;
}

