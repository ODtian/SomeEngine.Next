using System.Buffers.Binary;
using System.Collections.Immutable;
using SomeEngine.RenderGraph;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.Render.Materials;
using SlangShaderSharp;

namespace SomeEngine.Render.Cluster.Pipeline;

internal static class ClusterMaterialEntries
{
    internal const string Shade = "ClusterShade";
    internal const string SoftwareRasterImageAtomic64 = "ClusterRasterSoftwareImageAtomic64";
    internal const string SoftwareRasterBufferAtomic64 = "ClusterRasterSoftwareBufferAtomic64";
    internal const string HardwareVertex = "ClusterRasterVertex";
    internal const string HardwareMesh = "ClusterRasterMesh";
    internal const string HardwarePixelImageAtomic64 = "ClusterRasterPixelImageAtomic64";
    internal const string HardwarePixelBufferAtomic64 = "ClusterRasterPixelBufferAtomic64";
    internal const string HardwarePixelHardwareOnly = "ClusterRasterPixelHardwareOnly";
    internal const string TessellationSeed = "ClusterTessellationSeed";
    internal const string TessellationSplit = "ClusterTessellationSplit";
    internal const string TessellationDiceImageAtomic64 = "ClusterTessellationDiceImageAtomic64";
    internal const string TessellationDiceBufferAtomic64 = "ClusterTessellationDiceBufferAtomic64";
    internal const string TessellationHardwareMesh = "ClusterTessellationHardwareMesh";
    internal const string TessellationHardwareVertex = "ClusterTessellationHardwareVertex";
    internal const string TessellationHardwareMeshPixel = "ClusterTessellationHardwareMeshPixel";
    internal const string TessellationHardwarePixel = "ClusterTessellationHardwarePixel";

    internal static string SoftwareRaster(ClusterVisibilityMode mode) => mode switch
    {
        ClusterVisibilityMode.ImageAtomic64 => SoftwareRasterImageAtomic64,
        ClusterVisibilityMode.BufferAtomic64 => SoftwareRasterBufferAtomic64,
        ClusterVisibilityMode.HardwareOnly => throw new InvalidOperationException(
            "Hardware-only visibility has no software raster entry."),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    internal static string HardwarePixel(ClusterVisibilityMode mode, bool mesh) => (mode, mesh) switch
    {
        (ClusterVisibilityMode.ImageAtomic64, false) => HardwarePixelImageAtomic64,
        (ClusterVisibilityMode.BufferAtomic64, false) => HardwarePixelBufferAtomic64,
        (ClusterVisibilityMode.HardwareOnly, false) => HardwarePixelHardwareOnly,
        (ClusterVisibilityMode.ImageAtomic64, true) => "ClusterRasterMeshPixelImageAtomic64",
        (ClusterVisibilityMode.BufferAtomic64, true) => "ClusterRasterMeshPixelBufferAtomic64",
        (ClusterVisibilityMode.HardwareOnly, true) => "ClusterRasterMeshPixelHardwareOnly",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    internal static string TessellationDice(ClusterVisibilityMode mode) => mode switch
    {
        ClusterVisibilityMode.ImageAtomic64 => TessellationDiceImageAtomic64,
        ClusterVisibilityMode.BufferAtomic64 => TessellationDiceBufferAtomic64,
        ClusterVisibilityMode.HardwareOnly => throw new InvalidOperationException(
            "Hardware-only tessellation is consumed by fixed-function rasterization."),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    internal static void AddResourceProperties(
        HashSet<uint> properties,
        ReadOnlySpan<ClusterMaterialStageBinding> bindings)
    {
        foreach (ref readonly var binding in bindings)
            if (binding.IsMaterialResource) properties.Add(binding.MaterialSlot);
    }

    internal static Shader RequireShader(MaterialData material)
        => material.ShaderAsset ?? throw new InvalidDataException(
            $"Material '{material.Name}' has no loaded user shader.");

    internal static bool HasEntry(
        Shader shader,
        ShaderTarget target,
        string entryPoint,
        ShaderStage stage)
    {
        string backend = target == ShaderTarget.Spirv ? "spirv" : "dxil";
        return shader.TryEntry(backend, entryPoint, stage, out _, out _);
    }
}

internal readonly record struct ClusterTessellationCompatibilityKey(
    ClusterComputePipeline Seed,
    ClusterComputePipeline Split,
    ClusterComputePipeline? Dice,
    ClusterRasterPipeline? Hardware);

internal readonly record struct ClusterRasterCompatibilityKey(
    ClusterComputePipeline? Software,
    ClusterRasterPipeline Hardware,
    ClusterTessellationCompatibilityKey? Tessellation,
    uint StageAbiRevision);

internal readonly record struct ClusterShadeCompatibilityKey(
    ShaderProgram Program,
    ShaderEntryPoint EntryPoint);

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

// This index is derived from material addresses. A candidate shares unchanged groups, and
// records affected bin identities directly instead of diffing a newly collected scene.
internal sealed class ClusterBinIndexMap<TKey, TStage> where TKey : notnull
{
    private sealed record Group(uint Index, ImmutableSortedDictionary<int, TStage> Members);
    private ImmutableDictionary<TKey, Group> _groups = ImmutableDictionary<TKey, Group>.Empty;
    private ImmutableDictionary<int, TKey> _keys = ImmutableDictionary<int, TKey>.Empty;
    private ImmutableSortedSet<uint> _free = ImmutableSortedSet<uint>.Empty;
    private uint _next;
    private ImmutableHashSet<uint> _updated = ImmutableHashSet<uint>.Empty;
    private ImmutableSortedDictionary<uint, TStage> _stages = ImmutableSortedDictionary<uint, TStage>.Empty;
    internal ImmutableHashSet<uint> Changes { get; private set; } = ImmutableHashSet<uint>.Empty;
    private readonly Func<TStage, TStage, bool> _sameBindings;
    internal ClusterBinIndexMap(Func<TStage, TStage, bool> sameBindings) => _sameBindings = sameBindings;
    internal ClusterBinIndexMap(ClusterBinIndexMap<TKey, TStage> source)
    {
        _sameBindings = source._sameBindings;
        _groups = source._groups;
        _keys = source._keys;
        _free = source._free;
        _next = source._next;
        _stages = source._stages;
        _updated = source._updated;
        Changes = source.Changes;
    }
    internal void AcceptChanges() => Changes = Changes.Clear();
    internal void Set(int address, TKey key, TStage stage)
    {
        if (_keys.TryGetValue(address, out TKey? previous) && !EqualityComparer<TKey>.Default.Equals(previous, key))
            Remove(address);
        bool added = !_groups.TryGetValue(key, out Group? group);
        TStage? before = added ? default : group!.Members.First().Value;
        if (added)
        {
            uint index;
            if (!_free.IsEmpty) { index = _free.Min; _free = _free.Remove(index); }
            else { index = _next; _next = checked(_next + 1); }
            group = new Group(index, ImmutableSortedDictionary<int, TStage>.Empty);
        }
        group = group! with { Members = group.Members.SetItem(address, stage) };
        _groups = _groups.SetItem(key, group);
        _stages = _stages.SetItem(group.Index, group.Members.First().Value);
        _updated = _updated.Add(group.Index);
        _keys = _keys.SetItem(address, key);
        if (added || !_sameBindings(before!, group.Members.First().Value)) Changes = Changes.Add(group.Index);
    }
    internal void Remove(int address)
    {
        if (!_keys.TryGetValue(address, out TKey? key)) return;
        Group group = _groups[key];
        _keys = _keys.Remove(address);
        var members = group.Members.Remove(address);
        if (members.IsEmpty)
        {
            _groups = _groups.Remove(key);
            _free = _free.Add(group.Index);
            _stages = _stages.Remove(group.Index);
        }
        else
        {
            _groups = _groups.SetItem(key, group with { Members = members });
            _stages = _stages.SetItem(group.Index, members.First().Value);
            _updated = _updated.Add(group.Index);
        }
        if (members.IsEmpty || !_sameBindings(group.Members.First().Value, members.First().Value))
            Changes = Changes.Add(group.Index);
    }
    internal uint Range => Math.Max(1u, _next);
    internal ImmutableSortedDictionary<uint, TStage> GetStages(Func<TStage, uint, TStage> withIndex)
    {
        foreach (uint index in _updated)
            if (_stages.TryGetValue(index, out TStage? stage))
                _stages = _stages.SetItem(index, withIndex(stage, index));
        _updated = _updated.Clear();
        return _stages;
    }
    internal ClusterUploadData GetIndices(
        uint capacity,
        ClusterUploadData previous,
        IEnumerable<int> addresses,
        Func<TStage, uint, uint>? encodeIndex = null)
    {
        var changes = new List<(int Offset, byte[] Bytes)>();
        foreach (int address in addresses)
        {
            uint index = 0;
            if (_keys.TryGetValue(address, out TKey? key))
            {
                Group group = _groups[key];
                index = group.Index;
                if (encodeIndex is not null)
                    index = encodeIndex(group.Members.First().Value, index);
            }
            byte[] bytes = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, index);
            changes.Add((checked(address * sizeof(uint)), bytes));
        }
        return previous.WithChanges(checked((int)capacity * sizeof(uint)), changes);
    }
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
        MaterialData material,
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
            if (!binding.IsMaterialResource || !linked[index].IsUsed)
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
        MaterialData material,
        in ClusterMaterialStageBinding binding,
        string name)
    {
        object? value = material.GetValue(binding.MaterialSlot);
        return binding.Type switch
        {
            ResourceBindingType.TextureSrv => value switch
            {
                null => null,
                SomeEngine.Resources.Texture texture => new TextureValue(texture),
                _ => throw InvalidValue(material, name, "Texture"),
            },
            ResourceBindingType.TextureUav => value is SomeEngine.Resources.Texture texture
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

    private static InvalidDataException InvalidValue(MaterialData material, string name, string expected)
        => new($"Material '{material.Name}' slot '{name}' is not a {expected}.");

    private readonly record struct BindingValue(ResourceBindingType Type, object? Value);
    private readonly record struct TextureValue(SomeEngine.Resources.Texture Texture);
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

/// <summary>Owns raster-only compatibility grouping and its material-slot mapping.</summary>
internal sealed class ClusterRasterStages : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly string _defaultVertexEncoding;
    private readonly ClusterVisibilityMode _visibilityMode;
    private readonly ClusterComputePipeline? _defaultSoftware;
    private readonly ClusterRasterPipeline _defaultHardware;
    private readonly IGraphResourceProvider? _featureResources;
    private readonly Dictionary<ClusterRasterSoftwareKey, ClusterComputePipeline> _softwarePipelines = [];
    private readonly Dictionary<ClusterRasterSoftwareKey, ClusterComputePipeline> _tessellationPipelines = [];
    private readonly Dictionary<ClusterRasterHardwareKey, ClusterRasterPipeline> _hardwarePipelines = [];
    private ClusterBinIndexMap<ClusterRasterBinKey, ClusterRasterInvocation> _binIndices =
        new(static (before, after) => before.HasSameBindings(after) && before.Bounds == after.Bounds);
    private ImmutableDictionary<long, HashSet<uint>> _resourceProperties = ImmutableDictionary<long, HashSet<uint>>.Empty;

    internal bool UsesResourceProperty(long materialId, uint property) =>
        _resourceProperties.TryGetValue(materialId, out var properties) && properties.Contains(property);

    private ClusterRasterStageSet? _resolved;

    internal void AcceptChanges() => _binIndices.AcceptChanges();
    private bool _disposed;

    internal ClusterRasterStages(
        IGraphicsBackend backend,
        Device device,
        ClusterVisibilityMode visibilityMode,
        string vertexEncoding,
        ClusterComputePipeline? defaultSoftware,
        ClusterRasterPipeline defaultHardware,
        IGraphResourceProvider? featureResources)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _visibilityMode = visibilityMode;
        ArgumentException.ThrowIfNullOrWhiteSpace(vertexEncoding);
        _defaultVertexEncoding = vertexEncoding;
        if (visibilityMode != ClusterVisibilityMode.HardwareOnly && defaultSoftware is null)
            throw new ArgumentNullException(nameof(defaultSoftware));
        _defaultSoftware = defaultSoftware;
        _defaultHardware = defaultHardware ?? throw new ArgumentNullException(nameof(defaultHardware));
        _featureResources = featureResources;
    }

    private ClusterMaterialAssignmentSet? _resolvedAssignments;

    internal ClusterRasterStageSet Resolve(ClusterMaterialAssignmentSet assignments)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(assignments, _resolvedAssignments)) return _resolved!;
        var addresses = _resolvedAssignments is null
            ? new HashSet<int>(assignments.ActiveOffsets)
            : new HashSet<int>(assignments.ChangedOffsets);
        foreach (long material in assignments.ChangedMaterialIds)
            addresses.UnionWith(assignments.GetMaterialOffsets(material));
        var bins = new ClusterBinIndexMap<ClusterRasterBinKey, ClusterRasterInvocation>(_binIndices);
        var resourceProperties = _resourceProperties;
        foreach (long material in assignments.ChangedMaterialIds)
            resourceProperties = resourceProperties.Remove(material);
        foreach (int address in addresses)
        {
            if (!assignments.TryGetEntry(address, out MaterialData material, out string encoding))
            {
                bins.Remove(address);
                continue;
            }
            ClusterRasterInvocation invocation = ResolveInvocation(material, encoding);
            HashSet<uint> properties = [];
            if (invocation.SoftwareResources is { } software)
                ClusterMaterialEntries.AddResourceProperties(properties, software.MaterialBindings);
            ClusterMaterialEntries.AddResourceProperties(properties, invocation.HardwareResources.MaterialBindings);
            if (invocation.Tessellation is { } tessellation)
            {
                ClusterMaterialEntries.AddResourceProperties(properties, tessellation.SeedResources.MaterialBindings);
                ClusterMaterialEntries.AddResourceProperties(properties, tessellation.SplitResources.MaterialBindings);
                if (tessellation.DiceResources is { } dice)
                    ClusterMaterialEntries.AddResourceProperties(properties, dice.MaterialBindings);
                if (tessellation.HardwareResources is { } hardware)
                    ClusterMaterialEntries.AddResourceProperties(properties, hardware.MaterialBindings);
            }
            var key = new ClusterRasterBinKey(invocation.Key, encoding, invocation.CommandBindings);
            if (resourceProperties.TryGetValue(material.MaterialId, out var existing)) properties.UnionWith(existing);
            resourceProperties = resourceProperties.SetItem(material.MaterialId, properties);
            bins.Set(address, key, invocation);
        }
        var invocations = bins.GetStages(static (stage, index) => stage with { BinIndex = index });
        var result = new ClusterRasterStageSet(bins.GetIndices(
            assignments.SlotCapacity,
            _resolved?.BinIndices ?? ClusterUploadData.Zero(0),
            addresses,
            static (stage, index) => stage.Software is null ? index : index | 0x80000000u),
            invocations, bins.Range, [.. bins.Changes], [.. addresses], _resolved);
        _binIndices = bins;
        _resourceProperties = resourceProperties;
        _resolvedAssignments = assignments;
        _resolved = result;
        return result;
    }

    private ClusterRasterInvocation ResolveInvocation(MaterialData material, string vertexEncoding)
    {
        Shader shader = ClusterMaterialEntries.RequireShader(material);
        ShaderTarget target = _device.Capabilities.ShaderTarget;
        ClusterComputePipeline? software;
        ClusterRasterPipeline hardware;
        ClusterConservativeBounds bounds = ClusterConservativeBounds.Finite;

        string? softwareEntry = _visibilityMode == ClusterVisibilityMode.HardwareOnly
            ? null
            : ClusterMaterialEntries.SoftwareRaster(_visibilityMode);
        string hardwarePixelEntry = ClusterMaterialEntries.HardwarePixel(_visibilityMode, ClusterRasterPipeline.SupportsMesh(_backend, _device));
        string? tessellationDiceEntry = _visibilityMode == ClusterVisibilityMode.HardwareOnly
            ? null
            : ClusterMaterialEntries.TessellationDice(_visibilityMode);
        bool hasSoftware = softwareEntry is not null && ClusterMaterialEntries.HasEntry(
            shader, target, softwareEntry, ShaderStage.Compute);
        bool usesMesh = ClusterRasterPipeline.SupportsMesh(_backend, _device);
        string hardwareEntry = usesMesh ? ClusterMaterialEntries.HardwareMesh : ClusterMaterialEntries.HardwareVertex;
        bool hasMesh = ClusterMaterialEntries.HasEntry(shader, target, ClusterMaterialEntries.HardwareMesh, ShaderStage.Mesh);
        bool hasVertex = ClusterMaterialEntries.HasEntry(
            shader, target, ClusterMaterialEntries.HardwareVertex, ShaderStage.Vertex);
        bool hasPixel = ClusterMaterialEntries.HasEntry(
            shader, target, hardwarePixelEntry, ShaderStage.Pixel);
        bool hasVisibilityRaster = hasSoftware || hasVertex || hasMesh || hasPixel;
        if (!hasVisibilityRaster)
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
            if ((_visibilityMode != ClusterVisibilityMode.HardwareOnly && !hasSoftware) ||
                !hasVertex || (usesMesh && !hasMesh) ||
                !hasPixel)
            {
                throw new InvalidDataException(
                    $"Material '{material.Name}' must provide all Cluster visibility raster entries together.");
            }
            software = softwareEntry is null
                ? null
                : ResolveSoftwarePipeline(
                    material,
                    shader,
                    softwareEntry,
                    vertexEncoding);
            hardware = ResolveHardwarePipeline(
                material,
                shader,
                hardwareEntry,
                hardwarePixelEntry,
                vertexEncoding);
            bounds = ResolveBounds(
                material,
                shader,
                softwareEntry ?? hardwareEntry,
                hardwareEntry);
        }

        bool hasSeed = ClusterMaterialEntries.HasEntry(
            shader, target, ClusterMaterialEntries.TessellationSeed, ShaderStage.Compute);
        bool hasSplit = ClusterMaterialEntries.HasEntry(
            shader, target, ClusterMaterialEntries.TessellationSplit, ShaderStage.Compute);
        bool hasDice = tessellationDiceEntry is not null && ClusterMaterialEntries.HasEntry(
            shader, target, tessellationDiceEntry, ShaderStage.Compute);
        bool hasTessellationHardwareVertex = ClusterMaterialEntries.HasEntry(
            shader, target, ClusterMaterialEntries.TessellationHardwareVertex, ShaderStage.Vertex);
        bool hasTessellationHardwarePixel = ClusterMaterialEntries.HasEntry(
            shader, target, ClusterMaterialEntries.TessellationHardwarePixel, ShaderStage.Pixel);
        bool hasTessellationTerminal = _visibilityMode == ClusterVisibilityMode.HardwareOnly
            ? hasTessellationHardwareVertex || hasTessellationHardwarePixel
            : hasDice;
        bool hasTessellationOverride = hasSeed || hasSplit || hasTessellationTerminal;
        if (hasTessellationOverride)
        {
            bool hasCompleteTerminal = _visibilityMode == ClusterVisibilityMode.HardwareOnly
                ? hasTessellationHardwareVertex && hasTessellationHardwarePixel
                : hasDice;
            if (!hasSeed || !hasSplit || !hasCompleteTerminal)
            {
                throw new InvalidDataException(
                    $"Material '{material.Name}' must provide all Cluster tessellation entries together.");
            }
            if (!ClusterMaterialEntries.HasEntry(
                    shader,
                    target,
                    ClusterMaterialEntries.Shade,
                    ShaderStage.Compute))
            {
                throw new InvalidDataException(
                    $"Material '{material.Name}' provides runtime tessellation, so its same shader must also " +
                    "provide ClusterShade. Forward domain evaluation and depth reconstruction cannot come " +
                    "from different shader compositions.");
            }
        }

        return CreateInvocation(
            material,
            vertexEncoding,
            software,
            hardware,
            bounds,
            hasVisibilityRaster ? shader : null,
            hasTessellationOverride ? shader : null);
    }

    private ClusterRasterInvocation CreateInvocation(
        MaterialData material,
        string vertexEncoding,
        ClusterComputePipeline? software,
        ClusterRasterPipeline hardware,
        ClusterConservativeBounds bounds,
        Shader? visibilityShader,
        Shader? tessellationShader)
    {
        ClusterRasterStageResources<ClusterSoftwareRasterCoreResource>? softwareResources = software is null
            ? null
            : ResolveRasterResources(
                material,
                software.ParameterLayout,
                visibilityShader,
                visibilityShader is null ? null : ClusterMaterialEntries.SoftwareRaster(_visibilityMode),
                null,
                ClusterSoftwareRasterCoreResources.Resolve,
                ClusterSoftwareRasterCoreResources.Contains);
        ClusterRasterStageResources<ClusterHardwareRasterCoreResource> hardwareResources = ResolveRasterResources(
            material,
            hardware.ParameterLayout,
            visibilityShader,
            visibilityShader is null ? null : hardware.UsesMesh
                ? ClusterMaterialEntries.HardwareMesh : ClusterMaterialEntries.HardwareVertex,
            visibilityShader is null ? null : ClusterMaterialEntries.HardwarePixel(_visibilityMode, hardware.UsesMesh),
            ClusterHardwareRasterCoreResources.Resolve,
            ClusterHardwareRasterCoreResources.Contains);
        var bindingKeys = new List<ClusterCommandBindingKey>();
        if (softwareResources is not null)
        {
            bindingKeys.Add(ClusterCommandBindingKey.Create(
                material,
                softwareResources.Resources,
                softwareResources.MaterialBindings));
        }
        bindingKeys.Add(ClusterCommandBindingKey.Create(
                material,
                hardwareResources.Resources,
                hardwareResources.MaterialBindings));
        ClusterTessellationInvocation? tessellation = null;
        if (tessellationShader is not null)
        {
            ClusterComputePipeline seed = ResolveTessellationPipeline(
                material,
                tessellationShader,
                ClusterMaterialEntries.TessellationSeed,
                vertexEncoding,
                "seed");
            ClusterComputePipeline split = ResolveTessellationPipeline(
                material,
                tessellationShader,
                ClusterMaterialEntries.TessellationSplit,
                vertexEncoding,
                "split");
            ClusterComputePipeline? dice = _visibilityMode == ClusterVisibilityMode.HardwareOnly
                ? null
                : ResolveTessellationPipeline(
                    material,
                    tessellationShader,
                    ClusterMaterialEntries.TessellationDice(_visibilityMode),
                    vertexEncoding,
                    "dicing");
            ClusterRasterPipeline? tessellationHardware =
                _visibilityMode == ClusterVisibilityMode.HardwareOnly
                    ? ResolveHardwarePipeline(
                        material,
                        tessellationShader,
                        ClusterRasterPipeline.SupportsMesh(_backend, _device)
                            ? ClusterMaterialEntries.TessellationHardwareMesh : ClusterMaterialEntries.TessellationHardwareVertex,
                        ClusterRasterPipeline.SupportsMesh(_backend, _device)
                            ? ClusterMaterialEntries.TessellationHardwareMeshPixel : ClusterMaterialEntries.TessellationHardwarePixel,
                        vertexEncoding)
                    : null;
            ClusterRasterStageResources<ClusterTessellationCoreResource> seedResources =
                ResolveRasterResources(
                    material,
                    seed.ParameterLayout,
                    tessellationShader,
                    ClusterMaterialEntries.TessellationSeed,
                    null,
                    ClusterTessellationCoreResources.Resolve,
                    ClusterTessellationCoreResources.Contains);
            ClusterRasterStageResources<ClusterTessellationCoreResource> splitResources =
                ResolveRasterResources(
                    material,
                    split.ParameterLayout,
                    tessellationShader,
                    ClusterMaterialEntries.TessellationSplit,
                    null,
                    ClusterTessellationCoreResources.Resolve,
                    ClusterTessellationCoreResources.Contains);
            ClusterRasterStageResources<ClusterTessellationCoreResource>? diceResources = dice is null
                ? null
                : ResolveRasterResources(
                    material,
                    dice.ParameterLayout,
                    tessellationShader,
                    ClusterMaterialEntries.TessellationDice(_visibilityMode),
                    null,
                    ClusterTessellationCoreResources.Resolve,
                    ClusterTessellationCoreResources.Contains);
            ClusterRasterStageResources<ClusterTessellationCoreResource>? tessellationHardwareResources =
                tessellationHardware is null
                    ? null
                    : ResolveRasterResources(
                        material,
                        tessellationHardware.ParameterLayout,
                        tessellationShader,
                        ClusterRasterPipeline.SupportsMesh(_backend, _device)
                            ? ClusterMaterialEntries.TessellationHardwareMesh : ClusterMaterialEntries.TessellationHardwareVertex,
                        ClusterRasterPipeline.SupportsMesh(_backend, _device)
                            ? ClusterMaterialEntries.TessellationHardwareMeshPixel : ClusterMaterialEntries.TessellationHardwarePixel,
                    ClusterTessellationCoreResources.Resolve,
                    ClusterTessellationCoreResources.Contains);
            ClusterCommandBindingKey splitBindings = ClusterCommandBindingKey.Create(
                material, splitResources.Resources, splitResources.MaterialBindings);
            bindingKeys.Add(ClusterCommandBindingKey.Create(
                material,
                seedResources.Resources,
                seedResources.MaterialBindings));
            bindingKeys.Add(splitBindings);
            if (diceResources is not null)
            {
                bindingKeys.Add(ClusterCommandBindingKey.Create(
                    material,
                    diceResources.Resources,
                    diceResources.MaterialBindings));
            }
            if (tessellationHardwareResources is not null)
            {
                bindingKeys.Add(ClusterCommandBindingKey.Create(
                    material,
                    tessellationHardwareResources.Resources,
                    tessellationHardwareResources.MaterialBindings));
            }
            tessellation = new ClusterTessellationInvocation(
                new ClusterTessellationCompatibilityKey(seed, split, dice, tessellationHardware),
                seed,
                split,
                dice,
                tessellationHardware,
                seedResources,
                splitResources,
                diceResources,
                tessellationHardwareResources,
                splitBindings);
            ClusterConservativeBounds tessellationBounds =
                ClusterGeometryContracts.RequireConservativeBounds(
                    tessellationShader,
                    ClusterMaterialEntries.TessellationSplit,
                    $"Material '{material.Name}' tessellation split");
            if (tessellationBounds != ClusterConservativeBounds.Finite)
                bounds = ClusterConservativeBounds.Unbounded;
        }
        ClusterCommandBindingKey commandBindings = ClusterCommandBindingKey.Combine([.. bindingKeys]);
        var key = new ClusterRasterCompatibilityKey(
            software,
            hardware,
            tessellation?.Key,
            StageAbiRevision: 4);
        return new ClusterRasterInvocation(
            0,
            material,
            vertexEncoding,
            key,
            software,
            hardware,
            tessellation,
            bounds,
            softwareResources,
            hardwareResources,
            commandBindings);
    }

    private ClusterRasterStageResources<TCore> ResolveRasterResources<TCore>(
        MaterialData material,
        ShaderVariableLayout parameterLayout,
        Shader? shader,
        string? firstEntry,
        string? secondEntry,
        Func<string, ResourceBindingType, TCore> resolveCore,
        Func<string, bool> isCore)
        where TCore : struct, Enum
    {
        ClusterLinkedStageLayout resources = shader is null
            ? ClusterLinkedStageLayout.Create(parameterLayout)
            : ClusterLinkedStageLayout.Create(shader, _device.Capabilities.ShaderTarget,
                firstEntry ?? throw new InvalidDataException("A custom raster stage has no entry name."), secondEntry);
        Dictionary<string, byte> materialResources = shader is null
            ? new Dictionary<string, byte>(StringComparer.Ordinal)
            : ResolveMaterialResources(shader, resources);
        resources.ValidateMaterialResources(materialResources);
        GraphResourceBinder?[] featureBinders = resources.ResolveFeatureResources(
            materialResources,
            _featureResources,
            isCore,
            allowNeutralRasterTarget: true);
        ClusterMaterialStageBinding[] materialBindings = ResolveMaterialBindings(
            material,
            resources,
            materialResources,
            featureBinders);
        var coreKinds = new TCore[resources.Resources.Length];
        for (int index = 0; index < coreKinds.Length; index++)
        {
            ref readonly ClusterLinkedStageResource resource = ref resources.Resources[index];
            if (resource.IsUsed)
                coreKinds[index] = resolveCore(resource.Name, resource.Type);
        }
        return new ClusterRasterStageResources<TCore>(
            resources,
            coreKinds,
            materialBindings,
            featureBinders);
    }

    private static Dictionary<string, byte> ResolveMaterialResources(
        Shader shader,
        ClusterLinkedStageLayout resources)
    {
        var result = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (ShaderMaterialBinding binding in shader.Metadata?.MaterialBindings ?? [])
        {
            string name = binding.Name ?? throw new InvalidDataException(
                $"Shader '{shader.Name}' has an unnamed material resource.");
            if (!resources.Contains(name))
                continue;
            if (!result.TryAdd(name, binding.ResourceType))
            {
                throw new InvalidDataException(
                    $"Shader '{shader.Name}' has duplicate material resource '{name}'.");
            }
        }
        return result;
    }

    private static ClusterMaterialStageBinding[] ResolveMaterialBindings(
        MaterialData material,
        ClusterLinkedStageLayout resources,
        IReadOnlyDictionary<string, byte> materialResources,
        GraphResourceBinder?[] featureBinders)
    {
        ReadOnlySpan<ClusterLinkedStageResource> linked = resources.Resources;
        var result = new ClusterMaterialStageBinding[linked.Length];
        IReadOnlyList<MaterialPropertyDefinition> slots = material.Properties;
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
        MaterialData material,
        IReadOnlyList<MaterialPropertyDefinition> slots,
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
        MaterialData material,
        Shader shader,
        string entryPoint,
        string vertexEncoding)
    {
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
        MaterialData material,
        Shader shader,
        string entryPoint,
        string vertexEncoding,
        string phase)
    {
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
        MaterialData material,
        Shader shader,
        string vertexEntry,
        string pixelEntry,
        string vertexEncoding)
    {
        ClusterStageTargets.RequireVertexEncoding(
            shader,
            vertexEntry,
            vertexEncoding,
            $"Material '{material.Name}' hardware raster");
        ShaderStage geometryStage = vertexEntry is ClusterMaterialEntries.HardwareMesh or ClusterMaterialEntries.TessellationHardwareMesh
            ? ShaderStage.Mesh : ShaderStage.Vertex;
        CookedShaderEntry vertex = shader.RequireEntry(
            _device.Capabilities.ShaderTarget,
            vertexEntry,
            geometryStage);
        CookedShaderEntry pixel = shader.RequireEntry(
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
            shader,
            vertexEntry,
            pixelEntry,
            _visibilityMode == ClusterVisibilityMode.HardwareOnly
                ? [Format.R32UInt]
                : [],
            depthStencilFormat: _visibilityMode == ClusterVisibilityMode.HardwareOnly
                ? Format.D32Float
                : null,
            rasterizer: new RasterizerState(Cull: CullType.Back),
            depthStencil: _visibilityMode == ClusterVisibilityMode.HardwareOnly
                ? new DepthStencilState(
                    DepthTest: true,
                    DepthWrite: true,
                    DepthComparison: SomeEngine.Graphics.CompareOperation.LessOrEqual)
                : default,
            name: $"Cluster material hardware raster {vertexEntry}/{pixelEntry}",
            vertexBuffers: vertexEntry == ClusterMaterialEntries.TessellationHardwareVertex
                ? [new VertexBufferLayout(0, sizeof(uint), PerInstance: true)] : [],
            vertexAttributes: vertexEntry == ClusterMaterialEntries.TessellationHardwareVertex
                ? [new VertexAttribute(0, 0, Format.R32UInt, 0)] : [],
            geometryStage: geometryStage);
        _hardwarePipelines.Add(key, pipeline);
        return pipeline;
    }

    private static ClusterConservativeBounds ResolveBounds(
        MaterialData material,
        Shader shader,
        string softwareEntry,
        string vertexEntry)
    {
        ClusterConservativeBounds software = ClusterGeometryContracts.RequireConservativeBounds(
            shader,
            softwareEntry,
            $"Material '{material.Name}' software raster");
        ClusterConservativeBounds hardware = ClusterGeometryContracts.RequireConservativeBounds(
            shader,
            vertexEntry,
            $"Material '{material.Name}' hardware raster");
        return software == ClusterConservativeBounds.Finite &&
            hardware == ClusterConservativeBounds.Finite
            ? ClusterConservativeBounds.Finite
            : ClusterConservativeBounds.Unbounded;
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
    private readonly ImmutableSortedDictionary<uint, ClusterRasterInvocation> _bins;
    private readonly int _unboundedBinCount;

    internal ClusterRasterStageSet(
        ClusterUploadData binIndices,
        ImmutableSortedDictionary<uint, ClusterRasterInvocation> invocations,
        uint binCount,
        uint[]? changedBins = null,
        int[]? changedOffsets = null,
        ClusterRasterStageSet? previous = null)
    {
        BinIndices = binIndices;
        _bins = invocations;
        BinCount = binCount;
        ChangedBins = changedBins ?? [];
        ChangedOffsets = changedOffsets ?? [];
        _unboundedBinCount = previous?._unboundedBinCount ?? 0;
        if (previous is null)
            _unboundedBinCount = invocations.Values.Count(static stage => stage.Bounds != ClusterConservativeBounds.Finite);
        else
            foreach (uint bin in ChangedBins)
            {
                if (previous._bins.TryGetValue(bin, out var before) && before.Bounds != ClusterConservativeBounds.Finite)
                    _unboundedBinCount--;
                if (_bins.TryGetValue(bin, out var after) && after.Bounds != ClusterConservativeBounds.Finite)
                    _unboundedBinCount++;
            }
    }

    internal ClusterUploadData BinIndices { get; }
    internal IReadOnlyList<int> ChangedOffsets { get; }
    internal IEnumerable<ClusterRasterInvocation> Invocations => _bins.Values;
    internal uint BinCount { get; }
    internal IReadOnlyList<uint> ChangedBins { get; }
    internal bool TryGetBin(uint bin, out ClusterRasterInvocation stage) => _bins.TryGetValue(bin, out stage!);
    internal ClusterRasterInvocation GetBin(uint bin) => _bins[bin];
    internal bool HasFiniteBounds => _unboundedBinCount == 0;


}

internal sealed record ClusterRasterInvocation(
    uint BinIndex,
    MaterialData Material,
    string VertexEncoding,
    ClusterRasterCompatibilityKey Key,
    ClusterComputePipeline? Software,
    ClusterRasterPipeline Hardware,
    ClusterTessellationInvocation? Tessellation,
    ClusterConservativeBounds Bounds,
    ClusterRasterStageResources<ClusterSoftwareRasterCoreResource>? SoftwareResources,
    ClusterRasterStageResources<ClusterHardwareRasterCoreResource> HardwareResources,
    ClusterCommandBindingKey CommandBindings)
{
    internal bool HasSameBindings(ClusterRasterInvocation other) =>
        SameBindings(SoftwareResources, other.SoftwareResources) &&
        SameBindings(HardwareResources, other.HardwareResources) &&
        SameBindings(Tessellation?.SeedResources, other.Tessellation?.SeedResources) &&
        SameBindings(Tessellation?.SplitResources, other.Tessellation?.SplitResources) &&
        SameBindings(Tessellation?.DiceResources, other.Tessellation?.DiceResources) &&
        SameBindings(Tessellation?.HardwareResources, other.Tessellation?.HardwareResources);

    private static bool SameBindings<TCore>(ClusterRasterStageResources<TCore>? left,
        ClusterRasterStageResources<TCore>? right) where TCore : struct, Enum =>
        ReferenceEquals(left, right) || left is not null && right is not null &&
        left.MaterialBindings.AsSpan().SequenceEqual(right.MaterialBindings);
}

internal sealed record ClusterTessellationInvocation(
    ClusterTessellationCompatibilityKey Key,
    ClusterComputePipeline Seed,
    ClusterComputePipeline Split,
    ClusterComputePipeline? Dice,
    ClusterRasterPipeline? Hardware,
    ClusterRasterStageResources<ClusterTessellationCoreResource> SeedResources,
    ClusterRasterStageResources<ClusterTessellationCoreResource> SplitResources,
    ClusterRasterStageResources<ClusterTessellationCoreResource>? DiceResources,
    ClusterRasterStageResources<ClusterTessellationCoreResource>? HardwareResources,
    ClusterCommandBindingKey SplitBindings);

internal sealed class ClusterRasterStageResources<TCore>
    where TCore : struct, Enum
{
    internal ClusterRasterStageResources(
        ClusterLinkedStageLayout resources,
        TCore[] coreResources,
        ClusterMaterialStageBinding[] materialBindings,
        GraphResourceBinder?[] featureBinders)
    {
        Resources = resources;
        CoreResources = coreResources;
        MaterialBindings = materialBindings;
        FeatureBinders = featureBinders;
    }

    internal ClusterLinkedStageLayout Resources { get; }
    internal TCore[] CoreResources { get; }
    internal ClusterMaterialStageBinding[] MaterialBindings { get; }
    internal GraphResourceBinder?[] FeatureBinders { get; }
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
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly IGraphResourceProvider? _featureResources;
    private readonly Dictionary<ClusterShadeCompatibilityKey, ClusterComputePipeline> _pipelines = [];
    private ClusterBinIndexMap<ClusterShadeBinKey, ClusterShadeInvocation> _binIndices =
        new(static (before, after) => before.MaterialBindings.AsSpan().SequenceEqual(after.MaterialBindings));
    private ImmutableDictionary<long, HashSet<uint>> _resourceProperties = ImmutableDictionary<long, HashSet<uint>>.Empty;

    internal bool UsesResourceProperty(long materialId, uint property) =>
        _resourceProperties.TryGetValue(materialId, out var properties) && properties.Contains(property);

    private ClusterShadeStageSet _resolved = ClusterShadeStageSet.Empty;
    private bool _disposed;

    internal void AcceptChanges() => _binIndices.AcceptChanges();

    internal ClusterShadeStages(
        IGraphicsBackend backend,
        Device device,
        IGraphResourceProvider? featureResources)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _featureResources = featureResources;
    }

    private ClusterMaterialAssignmentSet? _resolvedAssignments;

    internal ClusterShadeStageSet Resolve(ClusterMaterialAssignmentSet assignments)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(assignments, _resolvedAssignments)) return _resolved!;
        var addresses = _resolvedAssignments is null
            ? new HashSet<int>(assignments.ActiveOffsets)
            : new HashSet<int>(assignments.ChangedOffsets);
        foreach (long material in assignments.ChangedMaterialIds)
            addresses.UnionWith(assignments.GetMaterialOffsets(material));
        var bins = new ClusterBinIndexMap<ClusterShadeBinKey, ClusterShadeInvocation>(_binIndices);
        var resourceProperties = _resourceProperties;
        foreach (long material in assignments.ChangedMaterialIds)
            resourceProperties = resourceProperties.Remove(material);
        var scalarLayouts = _resolved.ScalarLayouts;
        foreach (int address in addresses)
        {
            if (!assignments.TryGetEntry(address, out MaterialData material, out string encoding))
            {
                bins.Remove(address);
                scalarLayouts = scalarLayouts.Remove(address);
                continue;
            }
            ClusterShadeInvocation invocation = CreateInvocation(material, encoding);
            HashSet<uint> properties = [];
            ClusterMaterialEntries.AddResourceProperties(properties, invocation.MaterialBindings);
            scalarLayouts = scalarLayouts.SetItem(address, invocation.Scalars);
            var key = new ClusterShadeBinKey(invocation.Key, encoding,
                invocation.Scalars.LayoutHash, invocation.Scalars.PayloadByteSize, invocation.CommandBindings);
            if (resourceProperties.TryGetValue(material.MaterialId, out var existing)) properties.UnionWith(existing);
            resourceProperties = resourceProperties.SetItem(material.MaterialId, properties);
            bins.Set(address, key, invocation);
        }
        var invocations = bins.GetStages(static (stage, index) => stage with { BinIndex = index });
        var result = new ClusterShadeStageSet(bins.GetIndices(
            assignments.SlotCapacity, _resolved.BinIndices, addresses),
            invocations, bins.Range, scalarLayouts, [.. bins.Changes], [.. addresses]);
        _binIndices = bins;
        _resourceProperties = resourceProperties;
        _resolvedAssignments = assignments;
        _resolved = result;
        return result;
    }

    private ClusterShadeInvocation CreateInvocation(MaterialData material, string vertexEncoding)
    {
        Shader shader = ClusterMaterialEntries.RequireShader(material);
        string entryPoint = ClusterMaterialEntries.Shade;
        ClusterStageTargets.RequireVertexEncoding(
            shader,
            entryPoint,
            vertexEncoding,
            $"Material '{material.Name}' shade");
        CookedShaderEntry cooked = shader.RequireEntry(
            _device.Capabilities.ShaderTarget,
            entryPoint,
            ShaderStage.Compute);
        var key = new ClusterShadeCompatibilityKey(cooked.Program, cooked.EntryPoint);
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
        ClusterLinkedStageLayout resources = ClusterLinkedStageLayout.Create(
            shader,
            _device.Capabilities.ShaderTarget,
            entryPoint);
        ClusterShadeCoreResource[] coreResources = ResolveShadeCoreResources(resources);
        Dictionary<string, byte> materialResources = ResolveMaterialResources(shader, resources);
        resources.ValidateMaterialResources(materialResources);
        GraphResourceBinder?[] featureResourceBinders = resources.ResolveFeatureResources(
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
            if (resource.IsUsed)
                result[index] = ClusterShadeCoreResources.Resolve(resource.Name, resource.Type);
        }
        return result;
    }

    private static ClusterMaterialStageBinding[] ResolveMaterialBindings(
        MaterialData material,
        ClusterLinkedStageLayout resources,
        IReadOnlyDictionary<string, byte> materialResources,
        GraphResourceBinder?[] featureResourceBinders)
    {
        ReadOnlySpan<ClusterLinkedStageResource> linked = resources.Resources;
        var result = new ClusterMaterialStageBinding[linked.Length];
        IReadOnlyList<MaterialPropertyDefinition> slots = material.Properties;
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

    private static Dictionary<string, byte> ResolveMaterialResources(
        Shader shader,
        ClusterLinkedStageLayout resources)
    {
        var result = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (ShaderMaterialBinding binding in shader.Metadata?.MaterialBindings ?? [])
        {
            string name = binding.Name ?? throw new InvalidDataException(
                $"Shader '{shader.Name}' has an unnamed material resource.");
            if (!resources.Contains(name))
                continue;
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
    internal static ClusterShadeStageSet Empty { get; } =
        new(ClusterUploadData.Zero(0), ImmutableSortedDictionary<uint, ClusterShadeInvocation>.Empty, 0u,
            ImmutableDictionary<int, ScalarLayout.ScalarMaterialLayout>.Empty);

    private readonly ImmutableSortedDictionary<uint, ClusterShadeInvocation> _bins;
    internal ImmutableDictionary<int, ScalarLayout.ScalarMaterialLayout> ScalarLayouts { get; }

    internal ClusterShadeStageSet(
        ClusterUploadData binIndices,
        ImmutableSortedDictionary<uint, ClusterShadeInvocation> invocations,
        uint binCount,
        ImmutableDictionary<int, ScalarLayout.ScalarMaterialLayout> scalarLayouts,
        uint[]? changedBins = null,
        int[]? changedOffsets = null)
    {
        BinIndices = binIndices;
        _bins = invocations;
        BinCount = binCount;
        ScalarLayouts = scalarLayouts;
        ChangedBins = changedBins ?? [];
        ChangedOffsets = changedOffsets ?? [];
    }

    internal ClusterUploadData BinIndices { get; }
    internal IReadOnlyList<int> ChangedOffsets { get; }
    internal IEnumerable<ClusterShadeInvocation> Invocations => _bins.Values;
    internal uint BinCount { get; }
    internal ClusterShadeInvocation GetBin(uint bin) => _bins[bin];
    internal IReadOnlyList<uint> ChangedBins { get; }
    internal bool TryGetBin(uint bin, out ClusterShadeInvocation stage) => _bins.TryGetValue(bin, out stage!);

}

internal sealed record ClusterShadeInvocation(
    uint BinIndex,
    MaterialData Material,
    string VertexEncoding,
    ClusterShadeCompatibilityKey Key,
    ClusterComputePipeline Pipeline,
    ClusterLinkedStageLayout Resources,
    ClusterShadeCoreResource[] CoreResources,
    ScalarLayout.ScalarMaterialLayout Scalars,
    ClusterMaterialStageBinding[] MaterialBindings,
    GraphResourceBinder?[] FeatureResourceBinders,
    ClusterCommandBindingKey CommandBindings);

internal readonly record struct ClusterMaterialStageBinding(
    uint MaterialSlot,
    ResourceBindingType Type,
    bool IsMaterialResource);

internal enum ClusterSoftwareRasterCoreResource : byte
{
    None,
    InstanceData,
    InstanceProperties,
    Uniforms,
    Views,
    PolicyViews,
    PageHeap,
    VisibleClusters,
    RasterWindows,
    BinnedClusters,
    BinMetadata,
    Depth,
    Visibility,
    WinnerImage,
    WinnerBuffer,
    Diagnostics,
}

internal enum ClusterHardwareRasterCoreResource : byte
{
    None,
    InstanceData,
    InstanceProperties,
    Uniforms,
    Views,
    PolicyViews,
    DispatchUniforms,
    PageHeap,
    BinnedClusters,
    VisibleClusters,
    RasterWindows,
    DrawMetadata,
    WinnerImage,
    WinnerBuffer,
}

internal enum ClusterTessellationCoreResource : byte
{
    None,
    InstanceData,
    InstanceProperties,
    Uniforms,
    Views,
    PolicyViews,
    PageHeap,
    VisibleClusters,
    RasterWindows,
    BinnedClusters,
    BinMetadata,
    RasterSlots,
    HiZ,
    Depth,
    Visibility,
    WinnerImage,
    WinnerBuffer,
    Counters,
    WorkPatches0,
    WorkPatches1,
    LevelCounts,
    NextLevelArgs,
    FinalPatches,
    GroupedPatchIndices,
    RasterPatches,
    BinCounts,
    BinOffsets,
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
    OutputMotionVectors,
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

    internal bool Contains(string name)
    {
        foreach (ClusterLinkedStageResource resource in _resources)
        {
            if (resource.IsUsed && string.Equals(resource.Name, name, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    internal static ClusterLinkedStageLayout Create(
        Shader shader, ShaderTarget target, string firstEntry, string? secondEntry = null)
    {
        string backend = target == ShaderTarget.Spirv ? "spirv" : "dxil";
        if (!shader.TryProgram(backend, out ShaderProgram program))
            throw new InvalidDataException($"Shader '{shader.Name}' has no {backend} shader data.");
        string[] names = secondEntry is null ? [firstEntry] : [firstEntry, secondEntry];
        HashSet<string> used = new(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (!shader.TryReflection(backend, name, out ShaderEntryPointReflection reflection) ||
                reflection.Reflection?.Resources is not { } resources)
                throw new InvalidDataException($"Shader '{shader.Name}' has no resource usage for '{name}'.");
            foreach (ShaderResourceReflection resource in resources)
                used.Add(resource.Name ?? throw new InvalidDataException("A reflected resource has no name."));
        }
        List<ClusterLinkedStageResource> result = [];
        AddBlock(program.Layout.GetGlobalParamsVarLayout());
        foreach (string name in names)
        {
            ShaderEntryPoint entry = program.EntryPoints.First(value => value.Name == name);
            AddBlock(entry.VarLayout);
        }
        return new ClusterLinkedStageLayout([.. result]);

        void AddBlock(ShaderVariableLayout layout)
        {
            if (layout == ShaderVariableLayout.Null)
                return;
            ClusterLinkedStageLayout block = Create(layout);
            if (block._resources.Length == 0)
                return;
            result.Add(new ClusterLinkedStageResource("", ResourceBindingType.None, 0)
                { ParameterLayout = layout, IsUsed = false });
            foreach (ClusterLinkedStageResource resource in block._resources)
                result.Add(resource with { IsUsed = used.Contains(resource.Name) });
        }
    }

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
                string linkedNames = string.Join(", ", _resources.Select(static resource => resource.Name));
                throw new InvalidDataException(
                    $"Material resource '{name}' is absent from the final linked entry layout. " +
                    $"Linked resources: [{linkedNames}].");
            }
        }
    }

    internal GraphResourceBinder?[] ResolveFeatureResources(
        IReadOnlyDictionary<string, byte> materialResources,
        IGraphResourceProvider? provider,
        Func<string, bool> isCoreResource,
        bool allowNeutralRasterTarget = false)
    {
        ArgumentNullException.ThrowIfNull(isCoreResource);
        var result = new GraphResourceBinder?[_resources.Length];
        for (int index = 0; index < _resources.Length; index++)
        {
            ClusterLinkedStageResource resource = _resources[index];
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
                    ("RasterTargetPageTable", ResourceBindingType.BufferSrv) or
                    ("RasterTargetReceiverMasks", ResourceBindingType.BufferSrv) or
                    ("RasterTargetOutput", ResourceBindingType.TextureUav))
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

    private static ResourceBindingType ExpectedType(DescriptorType type) => type switch
    {
        DescriptorType.ConstantBuffer => ResourceBindingType.ConstantBuffer,
        DescriptorType.SampledTexture => ResourceBindingType.TextureSrv,
        DescriptorType.StorageTexture => ResourceBindingType.TextureUav,
        DescriptorType.ReadOnlyBuffer => ResourceBindingType.BufferSrv,
        DescriptorType.StorageBuffer => ResourceBindingType.BufferUav,
        DescriptorType.Sampler => ResourceBindingType.Sampler,
        DescriptorType.AccelerationStructure => ResourceBindingType.AccelerationStructure,
        _ => throw new InvalidDataException(
            $"Cooked shader resource type {type} cannot be represented by the RHI."),
    };

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
    uint Element)
{
    internal bool IsUsed { get; init; } = true;
    internal ShaderVariableLayout ParameterLayout { get; init; } = ShaderVariableLayout.Null;
}

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
            ClusterShadeCoreResource.MaterialScalars or
            ClusterShadeCoreResource.MaterialScalarOffsets => ResourceBindingType.BufferSrv,
            ClusterShadeCoreResource.InstanceProperties or
            ClusterShadeCoreResource.Uniforms => ResourceBindingType.ConstantBuffer,
            ClusterShadeCoreResource.Visibility or
            ClusterShadeCoreResource.Depth => ResourceBindingType.TextureSrv,
            ClusterShadeCoreResource.OutputColor or
            ClusterShadeCoreResource.OutputMotionVectors => ResourceBindingType.TextureUav,
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
        "OutputMotionVectors" => ClusterShadeCoreResource.OutputMotionVectors,
        "MaterialScalarRegion" => ClusterShadeCoreResource.MaterialScalars,
        "MaterialScalarOffsets" => ClusterShadeCoreResource.MaterialScalarOffsets,
        _ => ClusterShadeCoreResource.None,
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
            ClusterSoftwareRasterCoreResource.PolicyViews or
            ClusterSoftwareRasterCoreResource.PageHeap or
            ClusterSoftwareRasterCoreResource.VisibleClusters or
            ClusterSoftwareRasterCoreResource.RasterWindows or
            ClusterSoftwareRasterCoreResource.BinnedClusters or
            ClusterSoftwareRasterCoreResource.BinMetadata => ResourceBindingType.BufferSrv,
            ClusterSoftwareRasterCoreResource.InstanceProperties or
            ClusterSoftwareRasterCoreResource.Uniforms => ResourceBindingType.ConstantBuffer,
            ClusterSoftwareRasterCoreResource.Depth => ResourceBindingType.TextureSrv,
            ClusterSoftwareRasterCoreResource.Visibility or
            ClusterSoftwareRasterCoreResource.WinnerImage => ResourceBindingType.TextureUav,
            ClusterSoftwareRasterCoreResource.WinnerBuffer => ResourceBindingType.BufferUav,
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
        "PushConstants" or "Uniforms" or
        "ClusterSoftwareRasterPushConstants" or
        "ClusterSoftwareRasterUniformData" => ClusterSoftwareRasterCoreResource.Uniforms,
        "Views" or "SoftwareRasterViews" => ClusterSoftwareRasterCoreResource.Views,
        "PolicyClusterViews" or
        "SoftwareRasterPolicyViews" => ClusterSoftwareRasterCoreResource.PolicyViews,
        "PageHeap" => ClusterSoftwareRasterCoreResource.PageHeap,
        "VisibleClusters" or "SoftwareRasterVisibleClusters" => ClusterSoftwareRasterCoreResource.VisibleClusters,
        "VisibleClusterRasterWindows" => ClusterSoftwareRasterCoreResource.RasterWindows,
        "BinnedClusterIndexBuffer" or
        "SoftwareRasterBinnedClusterIndexBuffer" => ClusterSoftwareRasterCoreResource.BinnedClusters,
        "RasterBinMeta" or "SoftwareRasterBinMeta" => ClusterSoftwareRasterCoreResource.BinMetadata,
        "DepthTarget" or
        "SoftwareVisibilityDepthTarget" => ClusterSoftwareRasterCoreResource.Depth,
        "VisBuffer" or
        "SoftwareVisibilityBuffer" => ClusterSoftwareRasterCoreResource.Visibility,
        "DepthUAV" or
        "VisibilityWinnerImage" => ClusterSoftwareRasterCoreResource.WinnerImage,
        "VisibilityWinnerBuffer" => ClusterSoftwareRasterCoreResource.WinnerBuffer,
        "DebugSWOutput" or "SoftwareRasterDebugOutput" => ClusterSoftwareRasterCoreResource.Diagnostics,
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
            ClusterHardwareRasterCoreResource.PolicyViews or
            ClusterHardwareRasterCoreResource.PageHeap or
            ClusterHardwareRasterCoreResource.BinnedClusters or
            ClusterHardwareRasterCoreResource.VisibleClusters or
            ClusterHardwareRasterCoreResource.RasterWindows or
            ClusterHardwareRasterCoreResource.DrawMetadata => ResourceBindingType.BufferSrv,
            ClusterHardwareRasterCoreResource.InstanceProperties or
            ClusterHardwareRasterCoreResource.Uniforms or
            ClusterHardwareRasterCoreResource.DispatchUniforms => ResourceBindingType.ConstantBuffer,
            ClusterHardwareRasterCoreResource.WinnerImage => ResourceBindingType.TextureUav,
            ClusterHardwareRasterCoreResource.WinnerBuffer => ResourceBindingType.BufferUav,
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
        "Uniforms" or "HardwareRasterUniformData" => ClusterHardwareRasterCoreResource.Uniforms,
        "Views" or "HardwareRasterViews" => ClusterHardwareRasterCoreResource.Views,
        "PolicyClusterViews" or
        "HardwareRasterPolicyViews" => ClusterHardwareRasterCoreResource.PolicyViews,
        "DispatchUniforms" or
        "HardwareRasterDispatchUniformData" => ClusterHardwareRasterCoreResource.DispatchUniforms,
        "PageHeap" => ClusterHardwareRasterCoreResource.PageHeap,
        "BinnedClusterIndexBuffer" or
        "HardwareRasterBinnedClusterIndexBuffer" => ClusterHardwareRasterCoreResource.BinnedClusters,
        "VisibleClusters" or "HardwareRasterVisibleClusters" => ClusterHardwareRasterCoreResource.VisibleClusters,
        "VisibleClusterRasterWindows" => ClusterHardwareRasterCoreResource.RasterWindows,
        "VisibleClusterMeta" or "HardwareRasterVisibleClusterMeta" => ClusterHardwareRasterCoreResource.DrawMetadata,
        "VisibilityWinnerImage" => ClusterHardwareRasterCoreResource.WinnerImage,
        "VisibilityWinnerBuffer" => ClusterHardwareRasterCoreResource.WinnerBuffer,
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
            ClusterTessellationCoreResource.PolicyViews or
            ClusterTessellationCoreResource.PageHeap or
            ClusterTessellationCoreResource.VisibleClusters or
            ClusterTessellationCoreResource.RasterWindows or
            ClusterTessellationCoreResource.BinMetadata or
            ClusterTessellationCoreResource.RasterSlots or
            ClusterTessellationCoreResource.GroupedPatchIndices or
            ClusterTessellationCoreResource.RasterPatches or
            ClusterTessellationCoreResource.BinCounts or
            ClusterTessellationCoreResource.BinOffsets => ResourceBindingType.BufferSrv,
            ClusterTessellationCoreResource.HiZ or
            ClusterTessellationCoreResource.Depth => ResourceBindingType.TextureSrv,
            ClusterTessellationCoreResource.InstanceProperties or
            ClusterTessellationCoreResource.Uniforms => ResourceBindingType.ConstantBuffer,
            ClusterTessellationCoreResource.Visibility or
            ClusterTessellationCoreResource.WinnerImage => ResourceBindingType.TextureUav,
            ClusterTessellationCoreResource.WinnerBuffer => ResourceBindingType.BufferUav,
            ClusterTessellationCoreResource.BinnedClusters or
            ClusterTessellationCoreResource.Counters or
            ClusterTessellationCoreResource.WorkPatches0 or
            ClusterTessellationCoreResource.WorkPatches1 or
            ClusterTessellationCoreResource.LevelCounts or
            ClusterTessellationCoreResource.NextLevelArgs or
            ClusterTessellationCoreResource.FinalPatches => ResourceBindingType.BufferUav,
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
        "Uniforms" or "TessellationUniformData" => ClusterTessellationCoreResource.Uniforms,
        "Views" or "TessellationViews" => ClusterTessellationCoreResource.Views,
        "PolicyClusterViews" or
        "TessellationPolicyViews" => ClusterTessellationCoreResource.PolicyViews,
        "PageHeap" => ClusterTessellationCoreResource.PageHeap,
        "VisibleClusters" or
        "TessellationVisibleClusters" => ClusterTessellationCoreResource.VisibleClusters,
        "VisibleClusterRasterWindows" => ClusterTessellationCoreResource.RasterWindows,
        "BinnedClusterIndexBuffer" or
        "TessellationBinnedClusterIndexBuffer" => ClusterTessellationCoreResource.BinnedClusters,
        "RasterBinMeta" or
        "TessellationRasterBinMeta" => ClusterTessellationCoreResource.BinMetadata,
        "TessellationRasterSlots" => ClusterTessellationCoreResource.RasterSlots,
        "HiZTexture" or
        "TessellationHiZTexture" => ClusterTessellationCoreResource.HiZ,
        "DepthTarget" or
        "SoftwareVisibilityDepthTarget" => ClusterTessellationCoreResource.Depth,
        "VisBuffer" or
        "SoftwareVisibilityBuffer" => ClusterTessellationCoreResource.Visibility,
        "DepthUAV" or
        "VisibilityWinnerImage" => ClusterTessellationCoreResource.WinnerImage,
        "VisibilityWinnerBuffer" => ClusterTessellationCoreResource.WinnerBuffer,
        "TessellationCounters" => ClusterTessellationCoreResource.Counters,
        "TessellationWorkPatches0" => ClusterTessellationCoreResource.WorkPatches0,
        "TessellationWorkPatches1" => ClusterTessellationCoreResource.WorkPatches1,
        "TessellationLevelCounts" => ClusterTessellationCoreResource.LevelCounts,
        "TessellationNextLevelArgs" => ClusterTessellationCoreResource.NextLevelArgs,
        "TessellationFinalPatches" => ClusterTessellationCoreResource.FinalPatches,
        "GroupedPatchIndices" => ClusterTessellationCoreResource.GroupedPatchIndices,
        "TessellationRasterPatches" => ClusterTessellationCoreResource.RasterPatches,
        "TessellationBinCounts" => ClusterTessellationCoreResource.BinCounts,
        "TessellationBinOffsets" => ClusterTessellationCoreResource.BinOffsets,
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

}
