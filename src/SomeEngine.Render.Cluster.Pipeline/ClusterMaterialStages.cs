using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Materials;
using SlangShaderSharp;

namespace SomeEngine.Render.Cluster.Pipeline;

internal readonly record struct ClusterDeformCompatibilityKey(
    ShaderProgram Program,
    ShaderEntryPoint EntryPoint,
    string Target);

internal readonly record struct ClusterDeformGroupKey(
    MaterialDeformCompatibility Compatibility,
    string VertexEncoding);

internal readonly record struct ClusterRasterCompatibilityKey(
    ClusterComputePipeline Software,
    ClusterRasterPipeline Hardware,
    ClusterComputePipeline TessellationFactor,
    ClusterComputePipeline TessellationDice,
    uint StageAbiRevision);

internal readonly record struct ClusterShadeCompatibilityKey(
    ShaderProgram Program,
    ShaderEntryPoint EntryPoint,
    string Target);

internal static class ClusterMaterialBindingDefaults
{
    internal static readonly SamplerDesc Sampler = new(
        FilterType.Linear,
        FilterType.Linear,
        FilterType.Linear,
        AddressType.Repeat,
        AddressType.Repeat,
        AddressType.Repeat,
        Label: "Cluster default material sampler");
}

internal sealed class ClusterBinIndexMap<TKey>
    where TKey : notnull
{
    private readonly Dictionary<TKey, uint> _indices = [];
    private readonly SortedSet<uint> _free = [];
    private HashSet<TKey> _active = [];
    private uint _next;

    internal void Reconcile(HashSet<TKey> active)
    {
        ArgumentNullException.ThrowIfNull(active);
        foreach (TKey key in _active)
        {
            if (active.Contains(key))
                continue;
            uint index = _indices[key];
            _indices.Remove(key);
            _free.Add(index);
        }

        foreach (TKey key in active)
        {
            if (_indices.ContainsKey(key))
                continue;
            uint index;
            if (_free.Count != 0)
            {
                index = _free.Min;
                _free.Remove(index);
            }
            else
            {
                index = _next;
                _next = checked(_next + 1);
            }
            _indices.Add(key, index);
        }
        _active = new HashSet<TKey>(active);
    }

    internal uint Get(TKey key)
        => _indices.TryGetValue(key, out uint index)
            ? index
            : throw new KeyNotFoundException("The Cluster bin key is not registered.");

    internal uint Range
        => _indices.Count == 0 ? 0 : checked(_indices.Values.Max() + 1);
}

internal sealed class ClusterCommandBindingKey : IEquatable<ClusterCommandBindingKey>
{
    private readonly BindingValue[] _values;
    private readonly int _hash;

    private ClusterCommandBindingKey(BindingValue[] values)
    {
        _values = values;
        var hash = new HashCode();
        foreach (BindingValue value in values)
            hash.Add(value);
        _hash = hash.ToHashCode();
    }

    internal static ClusterCommandBindingKey Create(
        Material material,
        ClusterLinkedStageLayout resources,
        ClusterMaterialStageBinding[] bindings)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(bindings);
        ReadOnlySpan<ClusterLinkedStageResource> linked = resources.Resources;
        if (linked.Length != bindings.Length)
            throw new InvalidDataException("Cluster material binding metadata does not match the linked layout.");
        var values = new List<BindingValue>();
        for (int index = 0; index < bindings.Length; index++)
        {
            ClusterMaterialStageBinding binding = bindings[index];
            if (!binding.IsMaterialResource)
                continue;
            values.Add(new BindingValue(
                binding.Type,
                Normalize(material, binding, linked[index].Name)));
        }
        return new ClusterCommandBindingKey([.. values]);
    }

    internal static ClusterCommandBindingKey Combine(params ClusterCommandBindingKey[] keys)
    {
        int count = 0;
        foreach (ClusterCommandBindingKey key in keys)
            count = checked(count + key._values.Length);
        var values = new BindingValue[count];
        int offset = 0;
        foreach (ClusterCommandBindingKey key in keys)
        {
            key._values.CopyTo(values, offset);
            offset += key._values.Length;
        }
        return new ClusterCommandBindingKey(values);
    }

    public bool Equals(ClusterCommandBindingKey? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null || _hash != other._hash || _values.Length != other._values.Length)
            return false;
        return _values.AsSpan().SequenceEqual(other._values);
    }

    public override bool Equals(object? obj)
        => obj is ClusterCommandBindingKey other && Equals(other);

    public override int GetHashCode() => _hash;

    private static object? Normalize(
        Material material,
        in ClusterMaterialStageBinding binding,
        string name)
    {
        object? value = material.GetSlotValue(binding.MaterialSlot);
        return binding.Type switch
        {
            ResourceBindingType.TextureSrv => value switch
            {
                null => null,
                SomeEngine.Assets.Schema.Texture texture => new TextureValue(texture),
                _ => throw InvalidValue(material, name, "Texture"),
            },
            ResourceBindingType.TextureUav => value is SomeEngine.Assets.Schema.Texture texture
                ? new TextureValue(texture)
                : throw InvalidValue(material, name, "Texture"),
            ResourceBindingType.Sampler => value switch
            {
                null => ClusterMaterialBindingDefaults.Sampler,
                SamplerDesc sampler => sampler with { Label = null },
                _ => throw InvalidValue(material, name, nameof(SamplerDesc)),
            },
            ResourceBindingType.BufferSrv => value is BufferSrv srv
                ? BufferSrvValue.From(srv.Description)
                : throw InvalidValue(material, name, nameof(BufferSrv)),
            ResourceBindingType.BufferUav => value is BufferUav uav
                ? BufferUavValue.From(uav.Description)
                : throw InvalidValue(material, name, nameof(BufferUav)),
            ResourceBindingType.ConstantBuffer => value is BufferCbv cbv
                ? BufferCbvValue.From(cbv.Description)
                : throw InvalidValue(material, name, nameof(BufferCbv)),
            ResourceBindingType.AccelerationStructure => value is AccelerationStructureSrv acceleration
                ? new AccelerationValue(acceleration.Resource)
                : throw InvalidValue(material, name, nameof(AccelerationStructureSrv)),
            _ => throw new InvalidDataException(
                $"Cluster material resource '{name}' has unsupported binding type {binding.Type}."),
        };
    }

    private static InvalidDataException InvalidValue(Material material, string name, string expected)
        => new($"Material '{material.Name}' slot '{name}' is not a {expected}.");

    private readonly record struct BindingValue(ResourceBindingType Type, object? Value);
    private readonly record struct TextureValue(SomeEngine.Assets.Schema.Texture Texture);
    private readonly record struct AccelerationValue(AccelerationStructure Resource);
    private readonly record struct BufferSrvValue(
        SomeEngine.Graphics.Buffer Buffer,
        BufferRange Range,
        Format? Format,
        uint StructureStride)
    {
        internal static BufferSrvValue From(in BufferSrvDesc value)
            => new(value.Buffer, value.Range, value.Format, value.StructureStride);
    }
    private readonly record struct BufferUavValue(
        SomeEngine.Graphics.Buffer Buffer,
        BufferRange Range,
        Format? Format,
        uint StructureStride,
        SomeEngine.Graphics.Buffer? CounterBuffer,
        ulong CounterOffset)
    {
        internal static BufferUavValue From(in BufferUavDesc value)
            => new(
                value.Buffer,
                value.Range,
                value.Format,
                value.StructureStride,
                value.CounterBuffer,
                value.CounterOffset);
    }
    private readonly record struct BufferCbvValue(
        SomeEngine.Graphics.Buffer Buffer,
        BufferRange Range)
    {
        internal static BufferCbvValue From(in BufferCbvDesc value)
            => new(value.Buffer, value.Range);
    }
}

internal readonly record struct ClusterDeformBinKey(
    ClusterDeformCompatibilityKey Execution,
    string VertexEncoding,
    ClusterCommandBindingKey Bindings);

internal readonly record struct ClusterRasterBinKey(
    ClusterRasterCompatibilityKey Execution,
    string VertexEncoding,
    ClusterCommandBindingKey Bindings);

internal readonly record struct ClusterShadeBinKey(
    ClusterShadeCompatibilityKey Execution,
    string VertexEncoding,
    uint ScalarLayoutHash,
    uint ScalarPayloadBytes,
    ClusterCommandBindingKey Bindings);

/// <summary>Owns deform-only compatibility grouping and its material-slot mapping.</summary>
internal sealed class ClusterDeformStages : IDisposable
{
    internal const string Target = "cluster.deform.eval";

    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly IClusterStageResourceProvider? _featureResources;
    private readonly Dictionary<ClusterDeformCompatibilityKey, ClusterComputePipeline> _pipelines = [];
    private readonly ClusterBinIndexMap<ClusterDeformBinKey> _binIndices = new();
    private bool _disposed;

    internal ClusterDeformStages(
        IGraphicsBackend backend,
        Device device,
        IClusterStageResourceProvider? featureResources)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _featureResources = featureResources;
    }

    internal ClusterDeformStageSet Resolve(ClusterMaterialAssignmentSet assignments)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(assignments);
        ReadOnlySpan<Material> materialSlots = assignments.Slots.Span;
        ReadOnlySpan<string> vertexEncodings = assignments.VertexEncodings.Span;
        var keys = new ClusterDeformBinKey[materialSlots.Length];
        var invocationByKey = new Dictionary<ClusterDeformBinKey, ClusterDeformInvocation>();
        var slots = new uint[checked((int)assignments.SlotCapacity)];

        for (int slot = 0; slot < materialSlots.Length; slot++)
        {
            Material material = materialSlots[slot];
            string vertexEncoding = vertexEncodings[slot];
            MaterialDeformCompatibility compatibility = material.RequireDeformCompatibility();
            ClusterDeformInvocation invocation = CreateInvocation(material, compatibility, vertexEncoding);
            var key = new ClusterDeformBinKey(
                invocation.Key,
                vertexEncoding,
                invocation.CommandBindings);
            keys[slot] = key;
            invocationByKey.TryAdd(key, invocation);
        }

        _binIndices.Reconcile([.. invocationByKey.Keys]);
        var invocations = invocationByKey
            .Select(pair => pair.Value with { BinIndex = _binIndices.Get(pair.Key) })
            .OrderBy(static invocation => invocation.BinIndex)
            .ToArray();
        for (int slot = 0; slot < keys.Length; slot++)
            slots[slot] = _binIndices.Get(keys[slot]);

        return new ClusterDeformStageSet(
            slots,
            invocations,
            _binIndices.Range,
            materialSlots.ToArray(),
            materialSlots.ToArray().Select(static material => material.Revision).ToArray());
    }

    private ClusterDeformInvocation CreateInvocation(
        Material material,
        MaterialDeformCompatibility compatibility,
        string vertexEncoding)
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
        string subject = $"Material '{material.Name}' deform";
        ClusterStageTargets.RequireVertexEncoding(
            shader,
            compatibility.EntryPoint,
            vertexEncoding,
            subject);
        ClusterConservativeBounds bounds = ClusterGeometryContracts.RequireConservativeBounds(
            shader,
            compatibility.EntryPoint,
            subject);
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
        ClusterLinkedStageLayout resources = ClusterLinkedStageLayout.Create(pipeline.ParameterLayout);
        ClusterDeformCoreResource[] coreResources = ResolveDeformCoreResources(resources);
        Dictionary<string, byte> materialResources = ResolveMaterialResources(shader);
        resources.ValidateMaterialResources(materialResources);
        ClusterStageResourceBinder?[] featureResourceBinders = resources.ResolveFeatureResources(
            materialResources,
            _featureResources,
            ClusterDeformCoreResources.Contains);
        ClusterMaterialStageBinding[] materialBindings = ResolveMaterialBindings(
            material,
            resources,
            materialResources,
            featureResourceBinders);
        ClusterCommandBindingKey commandBindings = ClusterCommandBindingKey.Create(
            material,
            resources,
            materialBindings);
        return new ClusterDeformInvocation(
            0,
            material,
            vertexEncoding,
            compatibility,
            key,
            pipeline,
            bounds,
            resources,
            coreResources,
            materialBindings,
            featureResourceBinders,
            commandBindings);
    }

    private static ClusterDeformCoreResource[] ResolveDeformCoreResources(
        ClusterLinkedStageLayout resources)
    {
        ReadOnlySpan<ClusterLinkedStageResource> linked = resources.Resources;
        var result = new ClusterDeformCoreResource[linked.Length];
        for (int index = 0; index < linked.Length; index++)
        {
            ref readonly ClusterLinkedStageResource resource = ref linked[index];
            result[index] = ClusterDeformCoreResources.Resolve(resource.Name, resource.Type);
        }
        return result;
    }

    private static Dictionary<string, byte> ResolveMaterialResources(Shader shader)
    {
        var result = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (ShaderMaterialBinding binding in shader.Metadata?.MaterialBindings ?? [])
        {
            string name = binding.Name ?? throw new InvalidDataException(
                $"Shader '{shader.Name}' has an unnamed material resource.");
            if (!result.TryAdd(name, binding.ResourceType))
            {
                throw new InvalidDataException(
                    $"Shader '{shader.Name}' has duplicate material resource '{name}'.");
            }
        }
        return result;
    }

    private static ClusterMaterialStageBinding[] ResolveMaterialBindings(
        Material material,
        ClusterLinkedStageLayout resources,
        IReadOnlyDictionary<string, byte> materialResources,
        ClusterStageResourceBinder?[] featureResourceBinders)
    {
        ReadOnlySpan<ClusterLinkedStageResource> linked = resources.Resources;
        var result = new ClusterMaterialStageBinding[linked.Length];
        IReadOnlyList<MaterialSlotDefinition> slots = material.Slots;
        for (int resourceIndex = 0; resourceIndex < linked.Length; resourceIndex++)
        {
            if (featureResourceBinders[resourceIndex] is not null)
                continue;
            ref readonly ClusterLinkedStageResource resource = ref linked[resourceIndex];
            if (!materialResources.ContainsKey(resource.Name))
                continue;
            uint materialSlot = FindMaterialSlot(material, slots, resource.Name);
            result[resourceIndex] = new ClusterMaterialStageBinding(
                materialSlot,
                resource.Type,
                true);
        }
        return result;
    }

    private static uint FindMaterialSlot(
        Material material,
        IReadOnlyList<MaterialSlotDefinition> slots,
        string name)
    {
        for (uint slot = 0; slot < slots.Count; slot++)
        {
            if (string.Equals(
                    slots[checked((int)slot)].Name,
                    name,
                    StringComparison.Ordinal))
            {
                return slot;
            }
        }
        throw new InvalidDataException(
            $"Material '{material.Name}' does not expose linked resource '{name}'.");
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
    private readonly Material[] _materials;
    private readonly ulong[] _materialRevisions;

    internal ClusterDeformStageSet(
        uint[] slots,
        ClusterDeformInvocation[] invocations,
        uint binCount,
        Material[] materials,
        ulong[] materialRevisions)
    {
        Slots = slots;
        _invocations = invocations;
        BinCount = binCount;
        _materials = materials;
        _materialRevisions = materialRevisions;
    }

    internal ReadOnlyMemory<uint> Slots { get; }
    internal IReadOnlyList<ClusterDeformInvocation> Invocations => _invocations;
    internal uint BinCount { get; }
    internal bool HasFiniteBounds => _invocations.All(
        static invocation => invocation.Bounds == ClusterConservativeBounds.Finite);
    internal bool IsCurrent
    {
        get
        {
            for (int index = 0; index < _materials.Length; index++)
                if (_materials[index].Revision != _materialRevisions[index])
                    return false;
            return true;
        }
    }

    internal bool HasSameTopology(ClusterDeformStageSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (BinCount != other.BinCount ||
            !Slots.Span.SequenceEqual(other.Slots.Span) ||
            _invocations.Length != other._invocations.Length)
        {
            return false;
        }
        for (int index = 0; index < _invocations.Length; index++)
            if (_invocations[index].BinIndex != other._invocations[index].BinIndex ||
                _invocations[index].Key != other._invocations[index].Key ||
                !_invocations[index].CommandBindings.Equals(other._invocations[index].CommandBindings) ||
                !string.Equals(
                    _invocations[index].VertexEncoding,
                    other._invocations[index].VertexEncoding,
                    StringComparison.Ordinal))
                return false;
        return true;
    }
}

internal sealed record ClusterDeformInvocation(
    uint BinIndex,
    Material Material,
    string VertexEncoding,
    MaterialDeformCompatibility CookCompatibility,
    ClusterDeformCompatibilityKey Key,
    ClusterComputePipeline Pipeline,
    ClusterConservativeBounds Bounds,
    ClusterLinkedStageLayout Resources,
    ClusterDeformCoreResource[] CoreResources,
    ClusterMaterialStageBinding[] MaterialBindings,
    ClusterStageResourceBinder?[] FeatureResourceBinders,
    ClusterCommandBindingKey CommandBindings);

/// <summary>Owns raster-only compatibility grouping and its material-slot mapping.</summary>
internal sealed class ClusterRasterStages : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly string _defaultVertexEncoding;
    private readonly ClusterComputePipeline _defaultSoftware;
    private readonly ClusterRasterPipeline _defaultHardware;
    private readonly ClusterComputePipeline _defaultTessellationFactor;
    private readonly ClusterComputePipeline _defaultTessellationDice;
    private readonly IClusterStageResourceProvider? _featureResources;
    private readonly Dictionary<ClusterRasterSoftwareKey, ClusterComputePipeline> _softwarePipelines = [];
    private readonly Dictionary<ClusterRasterSoftwareKey, ClusterComputePipeline> _tessellationPipelines = [];
    private readonly Dictionary<ClusterRasterHardwareKey, ClusterRasterPipeline> _hardwarePipelines = [];
    private readonly ClusterBinIndexMap<ClusterRasterBinKey> _binIndices = new();
    private bool _disposed;

    internal ClusterRasterStages(
        IGraphicsBackend backend,
        Device device,
        string vertexEncoding,
        ClusterComputePipeline defaultSoftware,
        ClusterRasterPipeline defaultHardware,
        ClusterComputePipeline defaultTessellationFactor,
        ClusterComputePipeline defaultTessellationDice,
        IClusterStageResourceProvider? featureResources)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        ArgumentException.ThrowIfNullOrWhiteSpace(vertexEncoding);
        _defaultVertexEncoding = vertexEncoding;
        _defaultSoftware = defaultSoftware ?? throw new ArgumentNullException(nameof(defaultSoftware));
        _defaultHardware = defaultHardware ?? throw new ArgumentNullException(nameof(defaultHardware));
        _defaultTessellationFactor = defaultTessellationFactor ??
            throw new ArgumentNullException(nameof(defaultTessellationFactor));
        _defaultTessellationDice = defaultTessellationDice ??
            throw new ArgumentNullException(nameof(defaultTessellationDice));
        _featureResources = featureResources;
    }

    internal ClusterRasterStageSet Resolve(ClusterMaterialAssignmentSet assignments)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(assignments);
        ReadOnlySpan<Material> materialSlots = assignments.Slots.Span;
        ReadOnlySpan<string> vertexEncodings = assignments.VertexEncodings.Span;
        var keys = new ClusterRasterBinKey[materialSlots.Length];
        var invocationByKey = new Dictionary<ClusterRasterBinKey, ClusterRasterInvocation>();
        var slots = new uint[checked((int)assignments.SlotCapacity)];

        for (int slot = 0; slot < materialSlots.Length; slot++)
        {
            Material material = materialSlots[slot];
            string vertexEncoding = vertexEncodings[slot];
            ClusterRasterInvocation invocation = ResolveInvocation(material, vertexEncoding);
            var key = new ClusterRasterBinKey(
                invocation.Key,
                vertexEncoding,
                invocation.CommandBindings);
            keys[slot] = key;
            invocationByKey.TryAdd(key, invocation);
        }

        _binIndices.Reconcile([.. invocationByKey.Keys]);
        var invocations = invocationByKey
            .Select(pair => pair.Value with { BinIndex = _binIndices.Get(pair.Key) })
            .OrderBy(static invocation => invocation.BinIndex)
            .ToArray();
        for (int slot = 0; slot < keys.Length; slot++)
            slots[slot] = _binIndices.Get(keys[slot]);

        Material[] materials = materialSlots.ToArray();
        return new ClusterRasterStageSet(
            slots,
            invocations,
            _binIndices.Range,
            materials,
            materials.Select(static material => material.Revision).ToArray());
    }

    private ClusterRasterInvocation ResolveInvocation(Material material, string vertexEncoding)
    {
        (
            ShaderRef? softwareReference,
            ShaderRef? vertexReference,
            ShaderRef? pixelReference,
            ShaderRef? tessellationFactorReference,
            ShaderRef? tessellationDiceReference) = ResolveEntries(material);
        ClusterComputePipeline software;
        ClusterRasterPipeline hardware;
        ClusterComputePipeline tessellationFactor;
        ClusterComputePipeline tessellationDice;
        ClusterConservativeBounds bounds = ClusterConservativeBounds.Finite;

        bool hasRasterOverride = softwareReference is not null ||
            vertexReference is not null ||
            pixelReference is not null;
        if (!hasRasterOverride)
        {
            if (!string.Equals(vertexEncoding, _defaultVertexEncoding, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Material '{material.Name}' uses mesh vertex encoding '{vertexEncoding}', but the " +
                    $"default Cluster raster implementation accepts '{_defaultVertexEncoding}'. " +
                    "Provide a complete raster override for this encoding.");
            }
            software = _defaultSoftware;
            hardware = _defaultHardware;
        }
        else
        {
            if (softwareReference is null || vertexReference is null || pixelReference is null)
            {
                throw new InvalidDataException(
                    $"Material '{material.Name}' must override software, hardware vertex, and " +
                    "hardware pixel raster entries together.");
            }
            software = ResolveSoftwarePipeline(material, softwareReference, vertexEncoding);
            hardware = ResolveHardwarePipeline(material, vertexReference, pixelReference, vertexEncoding);
            bounds = ResolveBounds(material, softwareReference, vertexReference);
        }

        bool hasTessellationOverride = tessellationFactorReference is not null ||
            tessellationDiceReference is not null;
        if (!hasTessellationOverride)
        {
            if (!string.Equals(vertexEncoding, _defaultVertexEncoding, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Material '{material.Name}' uses mesh vertex encoding '{vertexEncoding}', but the " +
                    $"default Cluster tessellation implementation accepts '{_defaultVertexEncoding}'. " +
                    "Provide both tessellation factor and dicing overrides for this encoding.");
            }
            tessellationFactor = _defaultTessellationFactor;
            tessellationDice = _defaultTessellationDice;
        }
        else
        {
            if (tessellationFactorReference is null || tessellationDiceReference is null)
            {
                throw new InvalidDataException(
                    $"Material '{material.Name}' must override tessellation factor and dicing entries together.");
            }
            tessellationFactor = ResolveTessellationPipeline(
                material,
                tessellationFactorReference,
                vertexEncoding,
                "factor");
            tessellationDice = ResolveTessellationPipeline(
                material,
                tessellationDiceReference,
                vertexEncoding,
                "dicing");
            ClusterConservativeBounds tessellationBounds = ClusterGeometryContracts.RequireConservativeBounds(
                tessellationDiceReference.Asset ?? throw new InvalidDataException(
                    $"Material '{material.Name}' tessellation dicing entry has no resolved shader asset."),
                tessellationDiceReference.EntryPoint!,
                $"Material '{material.Name}' tessellation dicing");
            if (tessellationBounds != ClusterConservativeBounds.Finite)
                bounds = ClusterConservativeBounds.Unbounded;
        }

        return CreateInvocation(
            material,
            vertexEncoding,
            software,
            hardware,
            tessellationFactor,
            tessellationDice,
            bounds,
            softwareReference?.Asset,
            vertexReference?.Asset,
            tessellationFactorReference?.Asset,
            tessellationDiceReference?.Asset);
    }

    private ClusterRasterInvocation CreateInvocation(
        Material material,
        string vertexEncoding,
        ClusterComputePipeline software,
        ClusterRasterPipeline hardware,
        ClusterComputePipeline tessellationFactor,
        ClusterComputePipeline tessellationDice,
        ClusterConservativeBounds bounds,
        Shader? softwareShader,
        Shader? hardwareShader,
        Shader? tessellationFactorShader,
        Shader? tessellationDiceShader)
    {
        var key = new ClusterRasterCompatibilityKey(
            software,
            hardware,
            tessellationFactor,
            tessellationDice,
            StageAbiRevision: 1);
        ClusterRasterStageResources<ClusterSoftwareRasterCoreResource> softwareResources = ResolveRasterResources(
            material,
            software.ParameterLayout,
            softwareShader,
            ClusterSoftwareRasterCoreResources.Resolve,
            ClusterSoftwareRasterCoreResources.Contains);
        ClusterRasterStageResources<ClusterHardwareRasterCoreResource> hardwareResources = ResolveRasterResources(
            material,
            hardware.ParameterLayout,
            hardwareShader,
            ClusterHardwareRasterCoreResources.Resolve,
            ClusterHardwareRasterCoreResources.Contains);
        ClusterRasterStageResources<ClusterTessellationCoreResource> tessellationFactorResources =
            ResolveRasterResources(
                material,
                tessellationFactor.ParameterLayout,
                tessellationFactorShader,
                ClusterTessellationCoreResources.Resolve,
                ClusterTessellationCoreResources.Contains);
        ClusterRasterStageResources<ClusterTessellationCoreResource> tessellationDiceResources =
            ResolveRasterResources(
                material,
                tessellationDice.ParameterLayout,
                tessellationDiceShader,
                ClusterTessellationCoreResources.Resolve,
                ClusterTessellationCoreResources.Contains);
        ClusterCommandBindingKey commandBindings = ClusterCommandBindingKey.Combine(
            ClusterCommandBindingKey.Create(
                material,
                softwareResources.Resources,
                softwareResources.MaterialBindings),
            ClusterCommandBindingKey.Create(
                material,
                hardwareResources.Resources,
                hardwareResources.MaterialBindings),
            ClusterCommandBindingKey.Create(
                material,
                tessellationFactorResources.Resources,
                tessellationFactorResources.MaterialBindings),
            ClusterCommandBindingKey.Create(
                material,
                tessellationDiceResources.Resources,
                tessellationDiceResources.MaterialBindings));
        return new ClusterRasterInvocation(
            0,
            material,
            vertexEncoding,
            key,
            software,
            hardware,
            tessellationFactor,
            tessellationDice,
            bounds,
            softwareResources,
            hardwareResources,
            tessellationFactorResources,
            tessellationDiceResources,
            commandBindings);
    }

    private ClusterRasterStageResources<TCore> ResolveRasterResources<TCore>(
        Material material,
        ShaderVariableLayout parameterLayout,
        Shader? shader,
        Func<string, ResourceBindingType, TCore> resolveCore,
        Func<string, bool> isCore)
        where TCore : struct, Enum
    {
        ClusterLinkedStageLayout resources = ClusterLinkedStageLayout.Create(parameterLayout);
        Dictionary<string, byte> materialResources = shader is null
            ? new Dictionary<string, byte>(StringComparer.Ordinal)
            : ResolveMaterialResources(shader);
        resources.ValidateMaterialResources(materialResources);
        ClusterStageResourceBinder?[] featureBinders = resources.ResolveFeatureResources(
            materialResources,
            _featureResources,
            isCore);
        ClusterMaterialStageBinding[] materialBindings = ResolveMaterialBindings(
            material,
            resources,
            materialResources,
            featureBinders);
        var coreKinds = new TCore[resources.Resources.Length];
        for (int index = 0; index < coreKinds.Length; index++)
        {
            ref readonly ClusterLinkedStageResource resource = ref resources.Resources[index];
            coreKinds[index] = resolveCore(resource.Name, resource.Type);
        }
        return new ClusterRasterStageResources<TCore>(
            resources,
            coreKinds,
            materialBindings,
            featureBinders);
    }

    private static Dictionary<string, byte> ResolveMaterialResources(Shader shader)
    {
        var result = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (ShaderMaterialBinding binding in shader.Metadata?.MaterialBindings ?? [])
        {
            string name = binding.Name ?? throw new InvalidDataException(
                $"Shader '{shader.Name}' has an unnamed material resource.");
            if (!result.TryAdd(name, binding.ResourceType))
            {
                throw new InvalidDataException(
                    $"Shader '{shader.Name}' has duplicate material resource '{name}'.");
            }
        }
        return result;
    }

    private static ClusterMaterialStageBinding[] ResolveMaterialBindings(
        Material material,
        ClusterLinkedStageLayout resources,
        IReadOnlyDictionary<string, byte> materialResources,
        ClusterStageResourceBinder?[] featureBinders)
    {
        ReadOnlySpan<ClusterLinkedStageResource> linked = resources.Resources;
        var result = new ClusterMaterialStageBinding[linked.Length];
        IReadOnlyList<MaterialSlotDefinition> slots = material.Slots;
        for (int resourceIndex = 0; resourceIndex < linked.Length; resourceIndex++)
        {
            if (featureBinders[resourceIndex] is not null)
                continue;
            ref readonly ClusterLinkedStageResource resource = ref linked[resourceIndex];
            if (!materialResources.ContainsKey(resource.Name))
                continue;
            uint materialSlot = FindMaterialSlot(material, slots, resource.Name);
            result[resourceIndex] = new ClusterMaterialStageBinding(
                materialSlot,
                resource.Type,
                true);
        }
        return result;
    }

    private static uint FindMaterialSlot(
        Material material,
        IReadOnlyList<MaterialSlotDefinition> slots,
        string name)
    {
        for (uint slot = 0; slot < slots.Count; slot++)
        {
            if (string.Equals(slots[checked((int)slot)].Name, name, StringComparison.Ordinal))
                return slot;
        }
        throw new InvalidDataException(
            $"Material '{material.Name}' does not expose linked resource '{name}'.");
    }

    private ClusterComputePipeline ResolveSoftwarePipeline(
        Material material,
        ShaderRef reference,
        string vertexEncoding)
    {
        Shader shader = reference.Asset ?? throw new InvalidDataException(
            $"Material '{material.Name}' software raster entry has no resolved shader asset.");
        string entryPoint = reference.EntryPoint ?? throw new InvalidDataException(
            $"Material '{material.Name}' software raster entry has no entry-point name.");
        ClusterStageTargets.RequireVertexEncoding(
            shader,
            entryPoint,
            vertexEncoding,
            $"Material '{material.Name}' software raster");
        CookedShaderEntry cooked = shader.RequireEntry(
            _device.Capabilities.ShaderTarget,
            entryPoint,
            ShaderStage.Compute);
        var key = new ClusterRasterSoftwareKey(cooked.Program, cooked.EntryPoint);
        if (_softwarePipelines.TryGetValue(key, out ClusterComputePipeline? pipeline))
            return pipeline;
        pipeline = ClusterComputePipeline.Create(
            _backend,
            _device,
            cooked,
            $"Cluster material software raster {entryPoint}");
        _softwarePipelines.Add(key, pipeline);
        return pipeline;
    }

    private ClusterComputePipeline ResolveTessellationPipeline(
        Material material,
        ShaderRef reference,
        string vertexEncoding,
        string phase)
    {
        Shader shader = reference.Asset ?? throw new InvalidDataException(
            $"Material '{material.Name}' tessellation {phase} entry has no resolved shader asset.");
        string entryPoint = reference.EntryPoint ?? throw new InvalidDataException(
            $"Material '{material.Name}' tessellation {phase} entry has no entry-point name.");
        ClusterStageTargets.RequireVertexEncoding(
            shader,
            entryPoint,
            vertexEncoding,
            $"Material '{material.Name}' tessellation {phase}");
        CookedShaderEntry cooked = shader.RequireEntry(
            _device.Capabilities.ShaderTarget,
            entryPoint,
            ShaderStage.Compute);
        var key = new ClusterRasterSoftwareKey(cooked.Program, cooked.EntryPoint);
        if (_tessellationPipelines.TryGetValue(key, out ClusterComputePipeline? pipeline))
            return pipeline;
        pipeline = ClusterComputePipeline.Create(
            _backend,
            _device,
            cooked,
            $"Cluster material tessellation {phase} {entryPoint}");
        _tessellationPipelines.Add(key, pipeline);
        return pipeline;
    }

    private ClusterRasterPipeline ResolveHardwarePipeline(
        Material material,
        ShaderRef vertexReference,
        ShaderRef pixelReference,
        string vertexEncoding)
    {
        Shader vertexShader = vertexReference.Asset ?? throw new InvalidDataException(
            $"Material '{material.Name}' hardware raster vertex entry has no resolved shader asset.");
        Shader pixelShader = pixelReference.Asset ?? throw new InvalidDataException(
            $"Material '{material.Name}' hardware raster pixel entry has no resolved shader asset.");
        if (!ReferenceEquals(vertexShader, pixelShader))
        {
            throw new InvalidDataException(
                $"Material '{material.Name}' hardware raster vertex and pixel entries must share one shader asset.");
        }
        string vertexEntry = vertexReference.EntryPoint ?? throw new InvalidDataException(
            $"Material '{material.Name}' hardware raster vertex entry has no entry-point name.");
        string pixelEntry = pixelReference.EntryPoint ?? throw new InvalidDataException(
            $"Material '{material.Name}' hardware raster pixel entry has no entry-point name.");
        ClusterStageTargets.RequireVertexEncoding(
            vertexShader,
            vertexEntry,
            vertexEncoding,
            $"Material '{material.Name}' hardware raster");
        CookedShaderEntry vertex = vertexShader.RequireEntry(
            _device.Capabilities.ShaderTarget,
            vertexEntry,
            ShaderStage.Vertex);
        CookedShaderEntry pixel = pixelShader.RequireEntry(
            _device.Capabilities.ShaderTarget,
            pixelEntry,
            ShaderStage.Pixel);
        if (!ReferenceEquals(vertex.Program, pixel.Program))
            throw new InvalidDataException("Cluster material raster entries must share one cooked shader program.");
        var key = new ClusterRasterHardwareKey(vertex.Program, vertex.EntryPoint, pixel.EntryPoint);
        if (_hardwarePipelines.TryGetValue(key, out ClusterRasterPipeline? pipeline))
            return pipeline;
        pipeline = ClusterRasterPipeline.Create(
            _backend,
            _device,
            vertexShader,
            vertexEntry,
            pixelEntry,
            [Format.R32UInt],
            depthStencilFormat: Format.D32Float,
            rasterizer: new RasterizerState(Cull: CullType.Back),
            depthStencil: new DepthStencilState(
                DepthTest: true,
                DepthWrite: true,
                DepthComparison: SomeEngine.Graphics.CompareOperation.LessOrEqual),
            name: $"Cluster material hardware raster {vertexEntry}/{pixelEntry}");
        _hardwarePipelines.Add(key, pipeline);
        return pipeline;
    }

    private static ClusterConservativeBounds ResolveBounds(
        Material material,
        ShaderRef softwareReference,
        ShaderRef vertexReference)
    {
        Shader softwareShader = softwareReference.Asset ?? throw new InvalidDataException(
            $"Material '{material.Name}' software raster entry has no resolved shader asset.");
        Shader vertexShader = vertexReference.Asset ?? throw new InvalidDataException(
            $"Material '{material.Name}' hardware raster entry has no resolved shader asset.");
        ClusterConservativeBounds software = ClusterGeometryContracts.RequireConservativeBounds(
            softwareShader,
            softwareReference.EntryPoint!,
            $"Material '{material.Name}' software raster");
        ClusterConservativeBounds hardware = ClusterGeometryContracts.RequireConservativeBounds(
            vertexShader,
            vertexReference.EntryPoint!,
            $"Material '{material.Name}' hardware raster");
        return software == ClusterConservativeBounds.Finite &&
            hardware == ClusterConservativeBounds.Finite
            ? ClusterConservativeBounds.Finite
            : ClusterConservativeBounds.Unbounded;
    }

    private static (
        ShaderRef? Software,
        ShaderRef? Vertex,
        ShaderRef? Pixel,
        ShaderRef? TessellationFactor,
        ShaderRef? TessellationDice) ResolveEntries(
        Material material)
    {
        ShaderRef? software = null;
        ShaderRef? vertex = null;
        ShaderRef? pixel = null;
        ShaderRef? tessellationFactor = null;
        ShaderRef? tessellationDice = null;
        foreach (PassEntry pass in material.Passes ?? [])
        {
            ShaderRef shaderReference = pass.Shader ?? throw new InvalidDataException(
                $"Material '{material.Name}' has a pass without a final shader entry.");
            if (string.IsNullOrWhiteSpace(shaderReference.EntryPoint) ||
                shaderReference.Asset is not { } shader)
                continue;
            string entry = shaderReference.EntryPoint;
            if (shaderReference.Stage == ShaderStage.Compute &&
                ClusterStageTargets.HasTarget(shader, entry, "cluster.raster.sw.cached"))
            {
                if (software is not null)
                    throw new InvalidDataException($"Material '{material.Name}' has multiple software raster overrides.");
                software = shaderReference;
            }
            else if (shaderReference.Stage == ShaderStage.Vertex &&
                     ClusterStageTargets.HasTarget(shader, entry, "cluster.raster.vs.cached"))
            {
                if (vertex is not null)
                    throw new InvalidDataException($"Material '{material.Name}' has multiple hardware raster vertex overrides.");
                vertex = shaderReference;
            }
            else if (shaderReference.Stage == ShaderStage.Pixel &&
                     ClusterStageTargets.HasTarget(shader, entry, "cluster.raster.ps"))
            {
                if (pixel is not null)
                    throw new InvalidDataException($"Material '{material.Name}' has multiple hardware raster pixel overrides.");
                pixel = shaderReference;
            }
            else if (shaderReference.Stage == ShaderStage.Compute &&
                     ClusterStageTargets.HasTarget(shader, entry, "cluster.tess.factor"))
            {
                if (tessellationFactor is not null)
                {
                    throw new InvalidDataException(
                        $"Material '{material.Name}' has multiple tessellation factor overrides.");
                }
                tessellationFactor = shaderReference;
            }
            else if (shaderReference.Stage == ShaderStage.Compute &&
                     ClusterStageTargets.HasTarget(shader, entry, "cluster.tess.dice"))
            {
                if (tessellationDice is not null)
                {
                    throw new InvalidDataException(
                        $"Material '{material.Name}' has multiple tessellation dicing overrides.");
                }
                tessellationDice = shaderReference;
            }
        }
        return (software, vertex, pixel, tessellationFactor, tessellationDice);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        foreach (ClusterComputePipeline pipeline in _softwarePipelines.Values)
        {
            try { pipeline.Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        foreach (ClusterComputePipeline pipeline in _tessellationPipelines.Values)
        {
            try { pipeline.Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        foreach (ClusterRasterPipeline pipeline in _hardwarePipelines.Values)
        {
            try { pipeline.Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        _softwarePipelines.Clear();
        _tessellationPipelines.Clear();
        _hardwarePipelines.Clear();
        _disposed = true;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }
}

internal sealed class ClusterRasterStageSet
{
    private readonly ClusterRasterInvocation[] _invocations;
    private readonly Material[] _materials;
    private readonly ulong[] _materialRevisions;

    internal ClusterRasterStageSet(
        uint[] slots,
        ClusterRasterInvocation[] invocations,
        uint binCount,
        Material[] materials,
        ulong[] materialRevisions)
    {
        Slots = slots;
        _invocations = invocations;
        BinCount = binCount;
        _materials = materials;
        _materialRevisions = materialRevisions;
    }

    internal ReadOnlyMemory<uint> Slots { get; }
    internal IReadOnlyList<ClusterRasterInvocation> Invocations => _invocations;
    internal uint BinCount { get; }
    internal bool HasFiniteBounds => _invocations.All(
        static invocation => invocation.Bounds == ClusterConservativeBounds.Finite);
    internal bool IsCurrent
    {
        get
        {
            for (int index = 0; index < _materials.Length; index++)
                if (_materials[index].Revision != _materialRevisions[index])
                    return false;
            return true;
        }
    }

    internal bool HasSameTopology(ClusterRasterStageSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (BinCount != other.BinCount ||
            !Slots.Span.SequenceEqual(other.Slots.Span) ||
            _invocations.Length != other._invocations.Length)
        {
            return false;
        }
        for (int index = 0; index < _invocations.Length; index++)
            if (_invocations[index].BinIndex != other._invocations[index].BinIndex ||
                _invocations[index].Key != other._invocations[index].Key ||
                !_invocations[index].CommandBindings.Equals(other._invocations[index].CommandBindings) ||
                !string.Equals(
                    _invocations[index].VertexEncoding,
                    other._invocations[index].VertexEncoding,
                    StringComparison.Ordinal))
                return false;
        return true;
    }
}

internal sealed record ClusterRasterInvocation(
    uint BinIndex,
    Material Material,
    string VertexEncoding,
    ClusterRasterCompatibilityKey Key,
    ClusterComputePipeline Software,
    ClusterRasterPipeline Hardware,
    ClusterComputePipeline TessellationFactor,
    ClusterComputePipeline TessellationDice,
    ClusterConservativeBounds Bounds,
    ClusterRasterStageResources<ClusterSoftwareRasterCoreResource> SoftwareResources,
    ClusterRasterStageResources<ClusterHardwareRasterCoreResource> HardwareResources,
    ClusterRasterStageResources<ClusterTessellationCoreResource> TessellationFactorResources,
    ClusterRasterStageResources<ClusterTessellationCoreResource> TessellationDiceResources,
    ClusterCommandBindingKey CommandBindings);

internal sealed class ClusterRasterStageResources<TCore>
    where TCore : struct, Enum
{
    internal ClusterRasterStageResources(
        ClusterLinkedStageLayout resources,
        TCore[] coreResources,
        ClusterMaterialStageBinding[] materialBindings,
        ClusterStageResourceBinder?[] featureBinders)
    {
        Resources = resources;
        CoreResources = coreResources;
        MaterialBindings = materialBindings;
        FeatureBinders = featureBinders;
    }

    internal ClusterLinkedStageLayout Resources { get; }
    internal TCore[] CoreResources { get; }
    internal ClusterMaterialStageBinding[] MaterialBindings { get; }
    internal ClusterStageResourceBinder?[] FeatureBinders { get; }
}

internal readonly record struct ClusterRasterSoftwareKey(
    ShaderProgram Program,
    ShaderEntryPoint EntryPoint);

internal readonly record struct ClusterRasterHardwareKey(
    ShaderProgram Program,
    ShaderEntryPoint Vertex,
    ShaderEntryPoint Pixel);

/// <summary>
/// Resolves the final cooked shade entries and groups work by execution plus command binding
/// compatibility. Material identity and indexed scalar values never participate in the bin key.
/// </summary>
internal sealed class ClusterShadeStages : IDisposable
{
    internal const string Target = "cluster.shade.cached";

    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly IClusterStageResourceProvider? _featureResources;
    private readonly Dictionary<ClusterShadeCompatibilityKey, ClusterComputePipeline> _pipelines = [];
    private readonly ClusterBinIndexMap<ClusterShadeBinKey> _binIndices = new();
    private bool _disposed;

    internal ClusterShadeStages(
        IGraphicsBackend backend,
        Device device,
        IClusterStageResourceProvider? featureResources)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _featureResources = featureResources;
    }

    internal ClusterShadeStageSet Resolve(ClusterMaterialAssignmentSet assignments)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(assignments);
        ReadOnlySpan<Material> materialSlots = assignments.Slots.Span;
        ReadOnlySpan<string> vertexEncodings = assignments.VertexEncodings.Span;
        var keys = new ClusterShadeBinKey[materialSlots.Length];
        var invocationByKey = new Dictionary<ClusterShadeBinKey, ClusterShadeInvocation>();
        var scalarLayouts = new ScalarLayout.ScalarMaterialLayout?[checked((int)assignments.SlotCapacity)];
        var slots = new uint[checked((int)assignments.SlotCapacity)];

        for (int slot = 0; slot < materialSlots.Length; slot++)
        {
            Material material = materialSlots[slot];
            string vertexEncoding = vertexEncodings[slot];
            ClusterShadeInvocation invocation = CreateInvocation(material, vertexEncoding);
            var key = new ClusterShadeBinKey(
                invocation.Key,
                vertexEncoding,
                invocation.Scalars.LayoutHash,
                invocation.Scalars.PayloadByteSize,
                invocation.CommandBindings);
            keys[slot] = key;
            scalarLayouts[slot] = invocation.Scalars;
            invocationByKey.TryAdd(key, invocation);
        }

        _binIndices.Reconcile([.. invocationByKey.Keys]);
        var invocations = invocationByKey
            .Select(pair => pair.Value with { BinIndex = _binIndices.Get(pair.Key) })
            .OrderBy(static invocation => invocation.BinIndex)
            .ToArray();
        for (int slot = 0; slot < keys.Length; slot++)
            slots[slot] = _binIndices.Get(keys[slot]);

        Material[] materials = materialSlots.ToArray();
        return new ClusterShadeStageSet(
            slots,
            invocations,
            _binIndices.Range,
            materials,
            materials.Select(static material => material.Revision).ToArray(),
            scalarLayouts);
    }

    private ClusterShadeInvocation CreateInvocation(Material material, string vertexEncoding)
    {
        (Shader shader, string entryPoint) = ResolveEntry(material);
        ClusterStageTargets.RequireVertexEncoding(
            shader,
            entryPoint,
            vertexEncoding,
            $"Material '{material.Name}' shade");
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
        ScalarLayout.ScalarMaterialLayout scalarMaterialLayout = scalars.Resolve(material);
        ClusterLinkedStageLayout resources = ClusterLinkedStageLayout.Create(pipeline.ParameterLayout);
        ClusterShadeCoreResource[] coreResources = ResolveShadeCoreResources(resources);
        Dictionary<string, byte> materialResources = ResolveMaterialResources(shader);
        resources.ValidateMaterialResources(materialResources);
        ClusterStageResourceBinder?[] featureResourceBinders = resources.ResolveFeatureResources(
            materialResources,
            _featureResources,
            ClusterShadeCoreResources.Contains);
        ClusterMaterialStageBinding[] materialBindings = ResolveMaterialBindings(
            material,
            resources,
            materialResources,
            featureResourceBinders);
        ClusterCommandBindingKey commandBindings = ClusterCommandBindingKey.Create(
            material,
            resources,
            materialBindings);
        return new ClusterShadeInvocation(
            0,
            material,
            vertexEncoding,
            key,
            pipeline,
            resources,
            coreResources,
            scalarMaterialLayout,
            materialBindings,
            featureResourceBinders,
            commandBindings);
    }

    private static ClusterShadeCoreResource[] ResolveShadeCoreResources(
        ClusterLinkedStageLayout resources)
    {
        ReadOnlySpan<ClusterLinkedStageResource> linked = resources.Resources;
        var result = new ClusterShadeCoreResource[linked.Length];
        for (int index = 0; index < linked.Length; index++)
        {
            ref readonly ClusterLinkedStageResource resource = ref linked[index];
            result[index] = ClusterShadeCoreResources.Resolve(resource.Name, resource.Type);
        }
        return result;
    }

    private static ClusterMaterialStageBinding[] ResolveMaterialBindings(
        Material material,
        ClusterLinkedStageLayout resources,
        IReadOnlyDictionary<string, byte> materialResources,
        ClusterStageResourceBinder?[] featureResourceBinders)
    {
        ReadOnlySpan<ClusterLinkedStageResource> linked = resources.Resources;
        var result = new ClusterMaterialStageBinding[linked.Length];
        IReadOnlyList<MaterialSlotDefinition> slots = material.Slots;
        for (int resourceIndex = 0; resourceIndex < linked.Length; resourceIndex++)
        {
            if (featureResourceBinders[resourceIndex] is not null)
                continue;
            ref readonly ClusterLinkedStageResource resource = ref linked[resourceIndex];
            if (!materialResources.ContainsKey(resource.Name))
                continue;

            uint? materialSlot = null;
            for (uint slot = 0; slot < slots.Count; slot++)
            {
                if (!string.Equals(
                        slots[checked((int)slot)].Name,
                        resource.Name,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                materialSlot = slot;
                break;
            }
            if (materialSlot is null)
            {
                throw new InvalidDataException(
                    $"Material '{material.Name}' does not expose linked resource '{resource.Name}'.");
            }
            result[resourceIndex] = new ClusterMaterialStageBinding(
                materialSlot.Value,
                resource.Type,
                true);
        }
        return result;
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
    private readonly Material[] _materials;
    private readonly ulong[] _materialRevisions;
    private readonly ScalarLayout.ScalarMaterialLayout?[] _scalarLayouts;

    internal ClusterShadeStageSet(
        uint[] slots,
        ClusterShadeInvocation[] invocations,
        uint binCount,
        Material[] materials,
        ulong[] materialRevisions,
        ScalarLayout.ScalarMaterialLayout?[] scalarLayouts)
    {
        Slots = slots;
        _invocations = invocations;
        BinCount = binCount;
        _materials = materials;
        _materialRevisions = materialRevisions;
        _scalarLayouts = scalarLayouts;
    }

    internal ReadOnlyMemory<uint> Slots { get; }
    internal IReadOnlyList<ClusterShadeInvocation> Invocations => _invocations;
    internal uint BinCount { get; }

    internal bool IsCurrent
    {
        get
        {
            for (int index = 0; index < _materials.Length; index++)
                if (_materials[index].Revision != _materialRevisions[index])
                    return false;
            return true;
        }
    }

    internal (uint[] Offsets, byte[] Region) EncodeScalars()
    {
        int byteCount = 0;
        for (int index = 0; index < _materials.Length; index++)
        {
            ScalarLayout.ScalarMaterialLayout layout = _scalarLayouts[index]
                ?? throw new InvalidDataException("A Cluster material slot has no scalar layout.");
            byteCount = checked(byteCount + layout.ByteSize);
        }

        var offsets = new uint[Slots.Length];
        var region = new byte[Math.Max(byteCount, ScalarLayout.HeaderByteSize)];
        int byteOffset = 0;
        for (int index = 0; index < _materials.Length; index++)
        {
            ScalarLayout.ScalarMaterialLayout layout = _scalarLayouts[index]!;
            offsets[index] = checked((uint)byteOffset);
            byte[] packet = layout.Encode(_materials[index]);
            packet.CopyTo(region, byteOffset);
            byteOffset += packet.Length;
        }
        return (offsets, region);
    }

    internal bool HasSameTopology(ClusterShadeStageSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (BinCount != other.BinCount ||
            !Slots.Span.SequenceEqual(other.Slots.Span) ||
            _invocations.Length != other._invocations.Length)
        {
            return false;
        }
        for (int index = 0; index < _invocations.Length; index++)
        {
            if (_invocations[index].BinIndex != other._invocations[index].BinIndex ||
                _invocations[index].Key != other._invocations[index].Key ||
                _invocations[index].Scalars.LayoutHash != other._invocations[index].Scalars.LayoutHash ||
                _invocations[index].Scalars.PayloadByteSize != other._invocations[index].Scalars.PayloadByteSize ||
                !_invocations[index].CommandBindings.Equals(other._invocations[index].CommandBindings) ||
                !string.Equals(
                    _invocations[index].VertexEncoding,
                    other._invocations[index].VertexEncoding,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}

internal sealed record ClusterShadeInvocation(
    uint BinIndex,
    Material Material,
    string VertexEncoding,
    ClusterShadeCompatibilityKey Key,
    ClusterComputePipeline Pipeline,
    ClusterLinkedStageLayout Resources,
    ClusterShadeCoreResource[] CoreResources,
    ScalarLayout.ScalarMaterialLayout Scalars,
    ClusterMaterialStageBinding[] MaterialBindings,
    ClusterStageResourceBinder?[] FeatureResourceBinders,
    ClusterCommandBindingKey CommandBindings);

internal readonly record struct ClusterMaterialStageBinding(
    uint MaterialSlot,
    ResourceBindingType Type,
    bool IsMaterialResource);

internal enum ClusterDeformCoreResource : byte
{
    None,
    InstanceData,
    InstanceProperties,
    Uniforms,
    PageHeap,
    VisibleClusters,
    BinnedClusters,
    BinMetadata,
    DeformCache,
    CacheOffsets,
    CacheRequests,
}

internal enum ClusterSoftwareRasterCoreResource : byte
{
    None,
    InstanceData,
    InstanceProperties,
    Uniforms,
    Views,
    PageHeap,
    VisibleClusters,
    BinnedClusters,
    BinMetadata,
    Depth,
    Visibility,
    SoftwareDepth,
    DeformCache,
    CacheOffsets,
    Diagnostics,
}

internal enum ClusterHardwareRasterCoreResource : byte
{
    None,
    InstanceData,
    InstanceProperties,
    Uniforms,
    Views,
    DispatchUniforms,
    PageHeap,
    BinnedClusters,
    VisibleClusters,
    DrawMetadata,
    DeformCache,
    CacheOffsets,
}

internal enum ClusterTessellationCoreResource : byte
{
    None,
    InstanceData,
    InstanceProperties,
    Uniforms,
    Views,
    PageHeap,
    VisibleClusters,
    BinnedClusters,
    BinMetadata,
    DeformCache,
    CacheOffsets,
    Counters,
    SplitWork,
    DiceArguments,
    DicedTriangles,
    RasterArguments,
}

internal enum ClusterShadeCoreResource : byte
{
    None,
    InstanceData,
    InstanceProperties,
    Uniforms,
    Views,
    PixelCoordinates,
    BinOffsets,
    BinCounts,
    Visibility,
    Depth,
    VisibleClusters,
    PageHeap,
    OutputColor,
    DeformCache,
    CacheOffsets,
    MaterialScalars,
    MaterialScalarOffsets,
}

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
            bool found = false;
            foreach (ClusterLinkedStageResource resource in _resources)
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
                throw new InvalidDataException(
                    $"Material resource '{name}' is absent from the final linked entry layout.");
            }
        }
    }

    internal ClusterStageResourceBinder?[] ResolveFeatureResources(
        IReadOnlyDictionary<string, byte> materialResources,
        IClusterStageResourceProvider? provider,
        Func<string, bool> isCoreResource)
    {
        ArgumentNullException.ThrowIfNull(isCoreResource);
        var result = new ClusterStageResourceBinder?[_resources.Length];
        for (int index = 0; index < _resources.Length; index++)
        {
            ClusterLinkedStageResource resource = _resources[index];
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
                !provider.TryResolve(resource.Name, resource.Type, out ClusterStageResourceBinder? binder) ||
                binder is null)
            {
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

internal readonly record struct ClusterLinkedStageResource(
    string Name,
    ResourceBindingType Type,
    uint Element);

internal static class ClusterShadeCoreResources
{
    internal static bool Contains(string name) => ResolveName(name) != ClusterShadeCoreResource.None;

    internal static ClusterShadeCoreResource Resolve(string name, ResourceBindingType type)
    {
        ClusterShadeCoreResource result = ResolveName(name);
        if (result == ClusterShadeCoreResource.None)
            return result;
        ResourceBindingType expected = result switch
        {
            ClusterShadeCoreResource.InstanceData or
            ClusterShadeCoreResource.Views or
            ClusterShadeCoreResource.PixelCoordinates or
            ClusterShadeCoreResource.BinOffsets or
            ClusterShadeCoreResource.BinCounts or
            ClusterShadeCoreResource.VisibleClusters or
            ClusterShadeCoreResource.PageHeap or
            ClusterShadeCoreResource.DeformCache or
            ClusterShadeCoreResource.CacheOffsets or
            ClusterShadeCoreResource.MaterialScalars or
            ClusterShadeCoreResource.MaterialScalarOffsets => ResourceBindingType.BufferSrv,
            ClusterShadeCoreResource.InstanceProperties or
            ClusterShadeCoreResource.Uniforms => ResourceBindingType.ConstantBuffer,
            ClusterShadeCoreResource.Visibility or
            ClusterShadeCoreResource.Depth => ResourceBindingType.TextureSrv,
            ClusterShadeCoreResource.OutputColor => ResourceBindingType.TextureUav,
            _ => throw new InvalidOperationException($"Unknown Cluster shade core resource {result}."),
        };
        if (type != expected)
        {
            throw new InvalidDataException(
                $"Cluster shade core resource '{name}' must be {expected}, not {type}.");
        }
        return result;
    }

    private static ClusterShadeCoreResource ResolveName(string name) => name switch
    {
        "InstanceData" => ClusterShadeCoreResource.InstanceData,
        "InstanceProperties" => ClusterShadeCoreResource.InstanceProperties,
        "Uniforms" => ClusterShadeCoreResource.Uniforms,
        "Views" => ClusterShadeCoreResource.Views,
        "PixelCoordBuffer" => ClusterShadeCoreResource.PixelCoordinates,
        "BinOffsets" => ClusterShadeCoreResource.BinOffsets,
        "BinCounts" => ClusterShadeCoreResource.BinCounts,
        "VisBuffer" => ClusterShadeCoreResource.Visibility,
        "DepthBuffer" => ClusterShadeCoreResource.Depth,
        "VisibleClusters" => ClusterShadeCoreResource.VisibleClusters,
        "PageHeap" => ClusterShadeCoreResource.PageHeap,
        "OutputColor" => ClusterShadeCoreResource.OutputColor,
        "DeformCache" => ClusterShadeCoreResource.DeformCache,
        "CacheOffsets" => ClusterShadeCoreResource.CacheOffsets,
        "MaterialScalarRegion" => ClusterShadeCoreResource.MaterialScalars,
        "MaterialScalarOffsets" => ClusterShadeCoreResource.MaterialScalarOffsets,
        _ => ClusterShadeCoreResource.None,
    };
}

internal static class ClusterDeformCoreResources
{
    internal static bool Contains(string name) => ResolveName(name) != ClusterDeformCoreResource.None;

    internal static ClusterDeformCoreResource Resolve(string name, ResourceBindingType type)
    {
        ClusterDeformCoreResource result = ResolveName(name);
        if (result == ClusterDeformCoreResource.None)
            return result;
        ResourceBindingType expected = result switch
        {
            ClusterDeformCoreResource.InstanceData or
            ClusterDeformCoreResource.PageHeap or
            ClusterDeformCoreResource.VisibleClusters or
            ClusterDeformCoreResource.BinnedClusters or
            ClusterDeformCoreResource.BinMetadata => ResourceBindingType.BufferSrv,
            ClusterDeformCoreResource.InstanceProperties or
            ClusterDeformCoreResource.Uniforms => ResourceBindingType.ConstantBuffer,
            ClusterDeformCoreResource.DeformCache or
            ClusterDeformCoreResource.CacheOffsets or
            ClusterDeformCoreResource.CacheRequests => ResourceBindingType.BufferUav,
            _ => throw new InvalidOperationException($"Unknown Cluster deform core resource {result}."),
        };
        if (type != expected)
        {
            throw new InvalidDataException(
                $"Cluster deform core resource '{name}' must be {expected}, not {type}.");
        }
        return result;
    }

    private static ClusterDeformCoreResource ResolveName(string name) => name switch
    {
        "InstanceData" => ClusterDeformCoreResource.InstanceData,
        "InstanceProperties" => ClusterDeformCoreResource.InstanceProperties,
        "PushConstants" or "Uniforms" => ClusterDeformCoreResource.Uniforms,
        "PageHeap" => ClusterDeformCoreResource.PageHeap,
        "VisibleClusters" => ClusterDeformCoreResource.VisibleClusters,
        "BinnedClusterIndexBuffer" => ClusterDeformCoreResource.BinnedClusters,
        "DeformBinMeta" => ClusterDeformCoreResource.BinMetadata,
        "DeformCache" => ClusterDeformCoreResource.DeformCache,
        "CacheOffsetsWrite" => ClusterDeformCoreResource.CacheOffsets,
        "DeformCacheRequests" => ClusterDeformCoreResource.CacheRequests,
        _ => ClusterDeformCoreResource.None,
    };
}

internal static class ClusterSoftwareRasterCoreResources
{
    internal static bool Contains(string name) => ResolveName(name) != ClusterSoftwareRasterCoreResource.None;

    internal static ClusterSoftwareRasterCoreResource Resolve(string name, ResourceBindingType type)
    {
        ClusterSoftwareRasterCoreResource result = ResolveName(name);
        if (result == ClusterSoftwareRasterCoreResource.None)
            return result;
        ResourceBindingType expected = result switch
        {
            ClusterSoftwareRasterCoreResource.InstanceData or
            ClusterSoftwareRasterCoreResource.Views or
            ClusterSoftwareRasterCoreResource.PageHeap or
            ClusterSoftwareRasterCoreResource.VisibleClusters or
            ClusterSoftwareRasterCoreResource.BinnedClusters or
            ClusterSoftwareRasterCoreResource.BinMetadata or
            ClusterSoftwareRasterCoreResource.DeformCache or
            ClusterSoftwareRasterCoreResource.CacheOffsets => ResourceBindingType.BufferSrv,
            ClusterSoftwareRasterCoreResource.InstanceProperties or
            ClusterSoftwareRasterCoreResource.Uniforms => ResourceBindingType.ConstantBuffer,
            ClusterSoftwareRasterCoreResource.Depth => ResourceBindingType.TextureSrv,
            ClusterSoftwareRasterCoreResource.Visibility or
            ClusterSoftwareRasterCoreResource.SoftwareDepth => ResourceBindingType.TextureUav,
            ClusterSoftwareRasterCoreResource.Diagnostics => ResourceBindingType.BufferUav,
            _ => throw new InvalidOperationException($"Unknown Cluster software-raster core resource {result}."),
        };
        if (type != expected)
        {
            throw new InvalidDataException(
                $"Cluster software-raster core resource '{name}' must be {expected}, not {type}.");
        }
        return result;
    }

    private static ClusterSoftwareRasterCoreResource ResolveName(string name) => name switch
    {
        "InstanceData" => ClusterSoftwareRasterCoreResource.InstanceData,
        "InstanceProperties" => ClusterSoftwareRasterCoreResource.InstanceProperties,
        "PushConstants" or "Uniforms" => ClusterSoftwareRasterCoreResource.Uniforms,
        "Views" => ClusterSoftwareRasterCoreResource.Views,
        "PageHeap" => ClusterSoftwareRasterCoreResource.PageHeap,
        "VisibleClusters" => ClusterSoftwareRasterCoreResource.VisibleClusters,
        "BinnedClusterIndexBuffer" => ClusterSoftwareRasterCoreResource.BinnedClusters,
        "RasterBinMeta" => ClusterSoftwareRasterCoreResource.BinMetadata,
        "DepthTarget" => ClusterSoftwareRasterCoreResource.Depth,
        "VisBuffer" => ClusterSoftwareRasterCoreResource.Visibility,
        "DepthUAV" => ClusterSoftwareRasterCoreResource.SoftwareDepth,
        "DeformCache" => ClusterSoftwareRasterCoreResource.DeformCache,
        "CacheOffsets" => ClusterSoftwareRasterCoreResource.CacheOffsets,
        "DebugSWOutput" => ClusterSoftwareRasterCoreResource.Diagnostics,
        _ => ClusterSoftwareRasterCoreResource.None,
    };
}

internal static class ClusterHardwareRasterCoreResources
{
    internal static bool Contains(string name) => ResolveName(name) != ClusterHardwareRasterCoreResource.None;

    internal static ClusterHardwareRasterCoreResource Resolve(string name, ResourceBindingType type)
    {
        ClusterHardwareRasterCoreResource result = ResolveName(name);
        if (result == ClusterHardwareRasterCoreResource.None)
            return result;
        ResourceBindingType expected = result switch
        {
            ClusterHardwareRasterCoreResource.InstanceData or
            ClusterHardwareRasterCoreResource.Views or
            ClusterHardwareRasterCoreResource.PageHeap or
            ClusterHardwareRasterCoreResource.BinnedClusters or
            ClusterHardwareRasterCoreResource.VisibleClusters or
            ClusterHardwareRasterCoreResource.DrawMetadata or
            ClusterHardwareRasterCoreResource.DeformCache or
            ClusterHardwareRasterCoreResource.CacheOffsets => ResourceBindingType.BufferSrv,
            ClusterHardwareRasterCoreResource.InstanceProperties or
            ClusterHardwareRasterCoreResource.Uniforms or
            ClusterHardwareRasterCoreResource.DispatchUniforms => ResourceBindingType.ConstantBuffer,
            _ => throw new InvalidOperationException($"Unknown Cluster hardware-raster core resource {result}."),
        };
        if (type != expected)
        {
            throw new InvalidDataException(
                $"Cluster hardware-raster core resource '{name}' must be {expected}, not {type}.");
        }
        return result;
    }

    private static ClusterHardwareRasterCoreResource ResolveName(string name) => name switch
    {
        "InstanceData" => ClusterHardwareRasterCoreResource.InstanceData,
        "InstanceProperties" => ClusterHardwareRasterCoreResource.InstanceProperties,
        "Uniforms" => ClusterHardwareRasterCoreResource.Uniforms,
        "Views" => ClusterHardwareRasterCoreResource.Views,
        "DispatchUniforms" => ClusterHardwareRasterCoreResource.DispatchUniforms,
        "PageHeap" => ClusterHardwareRasterCoreResource.PageHeap,
        "BinnedClusterIndexBuffer" => ClusterHardwareRasterCoreResource.BinnedClusters,
        "VisibleClusters" => ClusterHardwareRasterCoreResource.VisibleClusters,
        "VisibleClusterMeta" => ClusterHardwareRasterCoreResource.DrawMetadata,
        "DeformCache" => ClusterHardwareRasterCoreResource.DeformCache,
        "CacheOffsets" => ClusterHardwareRasterCoreResource.CacheOffsets,
        _ => ClusterHardwareRasterCoreResource.None,
    };
}

internal static class ClusterTessellationCoreResources
{
    internal static bool Contains(string name) => ResolveName(name) != ClusterTessellationCoreResource.None;

    internal static ClusterTessellationCoreResource Resolve(string name, ResourceBindingType type)
    {
        ClusterTessellationCoreResource result = ResolveName(name);
        if (result == ClusterTessellationCoreResource.None)
            return result;
        ResourceBindingType expected = result switch
        {
            ClusterTessellationCoreResource.InstanceData or
            ClusterTessellationCoreResource.Views or
            ClusterTessellationCoreResource.PageHeap or
            ClusterTessellationCoreResource.VisibleClusters or
            ClusterTessellationCoreResource.BinMetadata or
            ClusterTessellationCoreResource.DeformCache or
            ClusterTessellationCoreResource.CacheOffsets => ResourceBindingType.BufferSrv,
            ClusterTessellationCoreResource.InstanceProperties or
            ClusterTessellationCoreResource.Uniforms => ResourceBindingType.ConstantBuffer,
            ClusterTessellationCoreResource.BinnedClusters or
            ClusterTessellationCoreResource.Counters or
            ClusterTessellationCoreResource.SplitWork or
            ClusterTessellationCoreResource.DiceArguments or
            ClusterTessellationCoreResource.DicedTriangles or
            ClusterTessellationCoreResource.RasterArguments => ResourceBindingType.BufferUav,
            _ => throw new InvalidOperationException(
                $"Unknown Cluster tessellation core resource {result}."),
        };
        if (type != expected)
        {
            throw new InvalidDataException(
                $"Cluster tessellation core resource '{name}' must be {expected}, not {type}.");
        }
        return result;
    }

    private static ClusterTessellationCoreResource ResolveName(string name) => name switch
    {
        "InstanceData" => ClusterTessellationCoreResource.InstanceData,
        "InstanceProperties" => ClusterTessellationCoreResource.InstanceProperties,
        "Uniforms" => ClusterTessellationCoreResource.Uniforms,
        "Views" => ClusterTessellationCoreResource.Views,
        "PageHeap" => ClusterTessellationCoreResource.PageHeap,
        "VisibleClusters" => ClusterTessellationCoreResource.VisibleClusters,
        "BinnedClusterIndexBuffer" => ClusterTessellationCoreResource.BinnedClusters,
        "RasterBinMeta" => ClusterTessellationCoreResource.BinMetadata,
        "DeformCache" => ClusterTessellationCoreResource.DeformCache,
        "CacheOffsets" => ClusterTessellationCoreResource.CacheOffsets,
        "TessellationCounters" => ClusterTessellationCoreResource.Counters,
        "SplitWorkItems" => ClusterTessellationCoreResource.SplitWork,
        "DiceDispatchArgs" => ClusterTessellationCoreResource.DiceArguments,
        "DicedTriangles" => ClusterTessellationCoreResource.DicedTriangles,
        "RasterDispatchArgs" => ClusterTessellationCoreResource.RasterArguments,
        _ => ClusterTessellationCoreResource.None,
    };
}

internal static class ClusterStageTargets
{
    internal static void RequireVertexEncoding(
        Shader shader,
        string entryPoint,
        string expected,
        string subject)
    {
        string actual = ClusterGeometryContracts.RequireVertexEncoding(
            shader,
            entryPoint,
            subject);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{subject} entry '{entryPoint}' accepts vertex encoding '{actual}', but the " +
                $"Cluster pipeline requires '{expected}'.");
        }
    }

    internal static bool HasTarget(Shader shader, string entryPoint, string target)
    {
        foreach (ShaderEntryPointAttribute attribute in shader.EntryPointAttributes ?? [])
        {
            if (!string.Equals(attribute.Name, "MaterialTarget", StringComparison.Ordinal) ||
                attribute.Args is not { Count: > 0 } args ||
                !args.Contains(target, StringComparer.Ordinal) ||
                !shader.TryEntry(attribute, entryPoint, out _))
            {
                continue;
            }
            return true;
        }
        return false;
    }
}
