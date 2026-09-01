using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Materials;
using SlangShaderSharp;

namespace SomeEngine.Render.Cluster.Pipeline;

internal readonly record struct ClusterDeformCompatibilityKey(
    ShaderProgram Program,
    ShaderEntryPoint EntryPoint,
    string Target);

internal readonly record struct ClusterRasterCompatibilityKey(
    ShaderProgram Program,
    uint StageAbiRevision);

internal readonly record struct ClusterShadeCompatibilityKey(
    ShaderProgram Program,
    ShaderEntryPoint EntryPoint,
    string Target);

/// <summary>Owns deform-only compatibility grouping and its material-slot mapping.</summary>
internal sealed class ClusterDeformStages : IDisposable
{
    internal const string Target = "cluster.deform.eval";

    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Dictionary<ClusterDeformCompatibilityKey, ClusterComputePipeline> _pipelines = [];
    private bool _disposed;

    internal ClusterDeformStages(IGraphicsBackend backend, Device device)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    internal ClusterDeformStageSet Resolve(ClusterMaterialAssignmentSet assignments)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(assignments);
        ReadOnlySpan<Material> materialSlots = assignments.Slots.Span;
        var invocations = new List<ClusterDeformInvocation>();
        var invocationByCompatibility = new Dictionary<MaterialDeformCompatibility, uint>();
        var slots = new uint[checked((int)assignments.SlotCapacity)];

        for (int slot = 0; slot < materialSlots.Length; slot++)
        {
            Material material = materialSlots[slot];
            MaterialDeformCompatibility compatibility = material.RequireDeformCompatibility();
            if (!invocationByCompatibility.TryGetValue(compatibility, out uint invocationIndex))
            {
                invocationIndex = checked((uint)invocations.Count);
                invocationByCompatibility.Add(compatibility, invocationIndex);
                invocations.Add(CreateInvocation(material, compatibility));
            }
            slots[slot] = invocationIndex;
        }

        return new ClusterDeformStageSet(slots, [.. invocations]);
    }

    private ClusterDeformInvocation CreateInvocation(
        Material material,
        MaterialDeformCompatibility compatibility)
    {
        ShaderRef shaderReference = ResolveEntry(material);
        Shader shader = shaderReference.Asset ?? throw new InvalidDataException(
            $"Material '{material.Name}' has no resolved deform shader asset.");
        if (!ClusterStageTargets.HasTarget(shader, compatibility.EntryPoint, Target))
        {
            throw new InvalidDataException(
                $"Material '{material.Name}' deform entry '{compatibility.EntryPoint}' " +
                $"does not implement '{Target}'.");
        }
        CookedShaderEntry cooked = shader.RequireEntry(
            _device.Capabilities.ShaderTarget,
            compatibility.EntryPoint,
            ShaderStage.Compute);
        var key = new ClusterDeformCompatibilityKey(cooked.Program, cooked.EntryPoint, Target);
        if (!_pipelines.TryGetValue(key, out ClusterComputePipeline? pipeline))
        {
            pipeline = ClusterComputePipeline.Create(
                _backend,
                _device,
                cooked,
                $"Cluster deform {compatibility.EntryPoint}");
            _pipelines.Add(key, pipeline);
        }
        return new ClusterDeformInvocation(compatibility, key, pipeline);
    }

    private static ShaderRef ResolveEntry(Material material)
    {
        ShaderRef? selected = null;
        foreach (PassEntry pass in material.Passes ?? [])
        {
            if (pass.Tags?.Any(static tag =>
                    string.Equals(tag.Name, Material.DeformPassTag, StringComparison.Ordinal)) != true)
            {
                continue;
            }
            if (selected is not null)
                throw new InvalidDataException($"Material '{material.Name}' has multiple deform passes.");
            selected = pass.Shader;
        }
        return selected ?? throw new InvalidDataException(
            $"Material '{material.Name}' has no deform pass.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        foreach (ClusterComputePipeline pipeline in _pipelines.Values)
        {
            try { pipeline.Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        _pipelines.Clear();
        _disposed = true;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }
}

internal sealed class ClusterDeformStageSet
{
    private readonly ClusterDeformInvocation[] _invocations;

    internal ClusterDeformStageSet(uint[] slots, ClusterDeformInvocation[] invocations)
    {
        Slots = slots;
        _invocations = invocations;
    }

    internal ReadOnlyMemory<uint> Slots { get; }
    internal IReadOnlyList<ClusterDeformInvocation> Invocations => _invocations;
    internal uint BinCount => checked((uint)_invocations.Length);

    internal bool HasSameTopology(ClusterDeformStageSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!Slots.Span.SequenceEqual(other.Slots.Span) ||
            _invocations.Length != other._invocations.Length)
        {
            return false;
        }
        for (int index = 0; index < _invocations.Length; index++)
            if (_invocations[index].Key != other._invocations[index].Key)
                return false;
        return true;
    }
}

internal sealed record ClusterDeformInvocation(
    MaterialDeformCompatibility CookCompatibility,
    ClusterDeformCompatibilityKey Key,
    ClusterComputePipeline Pipeline);

/// <summary>Owns raster-only compatibility grouping and its material-slot mapping.</summary>
internal sealed class ClusterRasterStages
{
    private readonly ClusterRasterCompatibilityKey _key;

    internal ClusterRasterStages(ClusterRasterPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        _key = new ClusterRasterCompatibilityKey(pipeline.Program, StageAbiRevision: 1);
    }

    internal ClusterRasterStageSet Resolve(ClusterMaterialAssignmentSet assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        return new ClusterRasterStageSet(
            _key,
            new uint[checked((int)assignments.SlotCapacity)]);
    }
}

internal sealed class ClusterRasterStageSet
{
    internal ClusterRasterStageSet(
        ClusterRasterCompatibilityKey key,
        uint[] slots)
    {
        Key = key;
        Slots = slots;
    }

    internal ClusterRasterCompatibilityKey Key { get; }
    internal ReadOnlyMemory<uint> Slots { get; }
    internal uint BinCount => 1;
}

/// <summary>
/// Resolves the final cooked shade entries, shares pipelines by execution compatibility, and keeps
/// one invocation per direct Material so ordinary descriptor values never participate in the key.
/// </summary>
internal sealed class ClusterShadeStages : IDisposable
{
    internal const string Target = "cluster.shade.cached";

    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Dictionary<ClusterShadeCompatibilityKey, ClusterComputePipeline> _pipelines = [];
    private bool _disposed;

    internal ClusterShadeStages(IGraphicsBackend backend, Device device)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    internal ClusterShadeStageSet Resolve(ClusterMaterialAssignmentSet assignments)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(assignments);
        ReadOnlySpan<Material> materialSlots = assignments.Slots.Span;
        var invocations = new List<ClusterShadeInvocation>();
        var invocationByMaterial = new Dictionary<Material, uint>(ReferenceEqualityComparer.Instance);
        var slots = new uint[checked((int)assignments.SlotCapacity)];

        for (int slot = 0; slot < materialSlots.Length; slot++)
        {
            Material material = materialSlots[slot];
            if (!invocationByMaterial.TryGetValue(material, out uint invocationIndex))
            {
                invocationIndex = checked((uint)invocations.Count);
                invocationByMaterial.Add(material, invocationIndex);
                invocations.Add(CreateInvocation(material));
            }
            slots[slot] = invocationIndex;
        }

        return new ClusterShadeStageSet(slots, [.. invocations]);
    }

    private ClusterShadeInvocation CreateInvocation(Material material)
    {
        (Shader shader, string entryPoint) = ResolveEntry(material);
        CookedShaderEntry cooked = shader.RequireEntry(
            _device.Capabilities.ShaderTarget,
            entryPoint,
            ShaderStage.Compute);
        var key = new ClusterShadeCompatibilityKey(cooked.Program, cooked.EntryPoint, Target);
        if (!_pipelines.TryGetValue(key, out ClusterComputePipeline? pipeline))
        {
            pipeline = ClusterComputePipeline.Create(
                _backend,
                _device,
                cooked,
                $"Cluster shade {entryPoint}");
            _pipelines.Add(key, pipeline);
        }

        ScalarLayout scalars = ResolveScalarLayout(shader);
        ClusterLinkedStageLayout resources = ClusterLinkedStageLayout.Create(pipeline.ParameterLayout);
        Dictionary<string, byte> materialResources = ResolveMaterialResources(shader);
        resources.ValidateMaterialResources(materialResources);
        return new ClusterShadeInvocation(
            material,
            material.Revision,
            key,
            pipeline,
            resources,
            scalars,
            materialResources);
    }

    private static (Shader Shader, string EntryPoint) ResolveEntry(Material material)
    {
        Shader? selectedShader = null;
        string? selectedEntry = null;
        foreach (PassEntry pass in material.Passes ?? [])
        {
            ShaderRef shaderReference = pass.Shader ?? throw new InvalidDataException(
                $"Material '{material.Name}' has a pass without a final shader entry.");
            if (shaderReference.Stage != ShaderStage.Compute ||
                string.IsNullOrWhiteSpace(shaderReference.EntryPoint) ||
                shaderReference.Asset is not { } shader ||
                !ClusterStageTargets.HasTarget(shader, shaderReference.EntryPoint, Target))
            {
                continue;
            }
            if (selectedShader is not null)
            {
                throw new InvalidDataException(
                    $"Material '{material.Name}' has more than one '{Target}' implementation.");
            }
            selectedShader = shader;
            selectedEntry = shaderReference.EntryPoint;
        }

        return selectedShader is not null && selectedEntry is not null
            ? (selectedShader, selectedEntry)
            : throw new InvalidDataException(
                $"Material '{material.Name}' has no final cooked '{Target}' implementation.");
    }

    private static ScalarLayout ResolveScalarLayout(Shader shader)
    {
        IList<ShaderMaterialScalarLayout>? layouts = shader.Metadata?.MaterialScalarLayouts;
        return layouts switch
        {
            null or { Count: 0 } => ScalarLayout.Empty,
            { Count: 1 } => ScalarLayout.FromData(layouts[0]),
            _ => throw new InvalidDataException(
                $"Shader '{shader.Name}' exposes multiple material scalar layouts without an entry-local selection."),
        };
    }

    private static Dictionary<string, byte> ResolveMaterialResources(Shader shader)
    {
        var result = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (ShaderMaterialBinding binding in shader.Metadata?.MaterialBindings ?? [])
        {
            string name = binding.Name ?? throw new InvalidDataException(
                $"Shader '{shader.Name}' has an unnamed material resource.");
            if (!result.TryAdd(name, binding.ResourceType))
                throw new InvalidDataException(
                    $"Shader '{shader.Name}' has duplicate material resource '{name}'.");
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        foreach (ClusterComputePipeline pipeline in _pipelines.Values)
        {
            try { pipeline.Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        _pipelines.Clear();
        _disposed = true;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }
}

internal sealed class ClusterShadeStageSet
{
    private readonly ClusterShadeInvocation[] _invocations;

    internal ClusterShadeStageSet(uint[] slots, ClusterShadeInvocation[] invocations)
    {
        Slots = slots;
        _invocations = invocations;
    }

    internal ReadOnlyMemory<uint> Slots { get; }
    internal IReadOnlyList<ClusterShadeInvocation> Invocations => _invocations;
    internal uint BinCount => checked((uint)_invocations.Length);

    internal bool IsCurrent
    {
        get
        {
            for (int index = 0; index < _invocations.Length; index++)
                if (_invocations[index].Material.Revision != _invocations[index].MaterialRevision)
                    return false;
            return true;
        }
    }

    internal bool HasSameTopology(ClusterShadeStageSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!Slots.Span.SequenceEqual(other.Slots.Span) ||
            _invocations.Length != other._invocations.Length)
        {
            return false;
        }
        for (int index = 0; index < _invocations.Length; index++)
        {
            if (!ReferenceEquals(_invocations[index].Material, other._invocations[index].Material) ||
                _invocations[index].Key != other._invocations[index].Key)
            {
                return false;
            }
        }
        return true;
    }
}

internal sealed record ClusterShadeInvocation(
    Material Material,
    ulong MaterialRevision,
    ClusterShadeCompatibilityKey Key,
    ClusterComputePipeline Pipeline,
    ClusterLinkedStageLayout Resources,
    ScalarLayout Scalars,
    IReadOnlyDictionary<string, byte> MaterialResources);

/// <summary>Bounded resource order captured from the final linked Slang program.</summary>
internal sealed class ClusterLinkedStageLayout
{
    private readonly ClusterLinkedStageResource[] _resources;

    private ClusterLinkedStageLayout(ClusterLinkedStageResource[] resources)
        => _resources = resources;

    internal ReadOnlySpan<ClusterLinkedStageResource> Resources => _resources;

    internal static ClusterLinkedStageLayout Create(ShaderVariableLayout layout)
    {
        if (layout == ShaderVariableLayout.Null)
            throw new InvalidDataException("A material stage has no linked parameter layout.");
        ShaderTypeLayout contents = layout.TypeLayout.UnwrapArray();
        if (contents.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            contents = contents.ElementTypeLayout.UnwrapArray();
        int rangeCount = ResolveCount(contents.BindingRangeCount, "binding ranges");
        int setCount = ResolveCount(contents.DescriptorSetCount, "descriptor sets");
        var resources = new List<ClusterLinkedStageResource>();
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
            int logicalCount = ResolveCount(
                contents.GetBindingRangeBindingCount(range),
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
                    resources.Add(new ClusterLinkedStageResource(name, type, checked((uint)element)));
            }
        }
        return new ClusterLinkedStageLayout([.. resources]);
    }

    internal void ValidateMaterialResources(IReadOnlyDictionary<string, byte> materialResources)
    {
        foreach ((string name, byte kind) in materialResources)
        {
            ResourceBindingType expected = kind switch
            {
                0 => ResourceBindingType.TextureSrv,
                1 => ResourceBindingType.Sampler,
                2 => ResourceBindingType.BufferSrv,
                _ => throw new InvalidDataException(
                    $"Material resource '{name}' has unsupported cooked kind {kind}."),
            };
            bool found = false;
            foreach (ClusterLinkedStageResource resource in _resources)
            {
                if (!string.Equals(resource.Name, name, StringComparison.Ordinal))
                    continue;
                if (resource.Type != expected)
                {
                    throw new InvalidDataException(
                        $"Material resource '{name}' is {expected} in metadata but {resource.Type} in linked reflection.");
                }
                found = true;
            }
            if (!found)
            {
                throw new InvalidDataException(
                    $"Material resource '{name}' is absent from the final linked entry layout.");
            }
        }
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

internal readonly record struct ClusterLinkedStageResource(
    string Name,
    ResourceBindingType Type,
    uint Element);

internal static class ClusterStageTargets
{
    internal static bool HasTarget(Shader shader, string entryPoint, string target)
    {
        foreach (ShaderEntryPointAttribute attribute in shader.EntryPointAttributes ?? [])
        {
            if (!string.Equals(attribute.Name, "MaterialTarget", StringComparison.Ordinal) ||
                attribute.Args is not { Count: > 0 } args ||
                !args.Contains(target, StringComparer.Ordinal) ||
                !shader.TryEntry(attribute, entryPoint, out ShaderEntryPoint reflected) ||
                reflected.Stage != SlangStage.Compute)
            {
                continue;
            }
            return true;
        }
        return false;
    }
}
