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
    internal const string ShadeMode = "ClusterShadeMode";
    internal const string ForwardShade = "ClusterForwardShade";
    internal const string SoftwareRasterImageAtomic64 = "ClusterRasterSoftwareImageAtomic64";
    internal const string SoftwareRasterBufferAtomic64 = "ClusterRasterSoftwareBufferAtomic64";
    internal const string SoftwareRasterNonAtomic = "ClusterRasterSoftwareNonAtomic";
    internal const string SoftwareMeshletImageAtomic64 = "ClusterRasterSoftwareMeshletImageAtomic64";
    internal const string SoftwareMeshletBufferAtomic64 = "ClusterRasterSoftwareMeshletBufferAtomic64";
    internal const string SoftwareMeshletNonAtomic = "ClusterRasterSoftwareMeshletNonAtomic";
    internal const string SoftwareTarget = "ClusterRasterSoftwareTarget";
    internal const string SoftwareMeshletTarget = "ClusterRasterSoftwareMeshletTarget";
    internal const string HardwarePixelTarget = "ClusterRasterPixelTarget";
    internal const string HardwareMeshPixelTarget = "ClusterRasterMeshPixelTarget";
    internal const string HardwareVertex = "ClusterRasterVertex";
    internal const string HardwareMesh = "ClusterRasterMesh";
    internal const string HardwareMeshlet = "ClusterRasterMeshlet";
    internal const string HardwarePixelImageAtomic64 = "ClusterRasterPixelImageAtomic64";
    internal const string HardwarePixelBufferAtomic64 = "ClusterRasterPixelBufferAtomic64";
    internal const string HardwarePixelNonAtomic = "ClusterRasterPixelNonAtomic";
    internal const string HardwarePixelHardwareOnly = "ClusterRasterPixelHardwareOnly";
    internal const string TessellationSeed = "ClusterTessellationSeed";
    internal const string TessellationSplit = "ClusterTessellationSplit";
    internal const string TessellationDiceImageAtomic64 = "ClusterTessellationDiceImageAtomic64";
    internal const string TessellationDiceBufferAtomic64 = "ClusterTessellationDiceBufferAtomic64";
    internal const string TessellationDiceNonAtomic = "ClusterTessellationDiceNonAtomic";
    internal const string TessellationHardwareMesh = "ClusterTessellationHardwareMesh";
    internal const string TessellationHardwareVertex = "ClusterTessellationHardwareVertex";
    internal const string TessellationHardwareMeshPixel = "ClusterTessellationHardwareMeshPixel";
    internal const string TessellationHardwarePixel = "ClusterTessellationHardwarePixel";
    internal static string SoftwareRaster(ClusterVisibilityMode mode) => mode switch
    {
        ClusterVisibilityMode.ImageAtomic64 => SoftwareRasterImageAtomic64,
        ClusterVisibilityMode.BufferAtomic64 => SoftwareRasterBufferAtomic64,
        ClusterVisibilityMode.NonAtomic => SoftwareRasterNonAtomic,
        ClusterVisibilityMode.HardwareOnly => throw new InvalidOperationException("Hardware-only visibility has no software raster entry."),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
    internal static string HardwarePixel(ClusterVisibilityMode mode, bool mesh) => (mode, mesh) switch
    {
        (ClusterVisibilityMode.ImageAtomic64, false) => HardwarePixelImageAtomic64,
        (ClusterVisibilityMode.BufferAtomic64, false) => HardwarePixelBufferAtomic64,
        (ClusterVisibilityMode.NonAtomic, false) => HardwarePixelNonAtomic,
        (ClusterVisibilityMode.HardwareOnly, false) => HardwarePixelHardwareOnly,
        (ClusterVisibilityMode.ImageAtomic64, true) => "ClusterRasterMeshPixelImageAtomic64",
        (ClusterVisibilityMode.BufferAtomic64, true) => "ClusterRasterMeshPixelBufferAtomic64",
        (ClusterVisibilityMode.NonAtomic, true) => "ClusterRasterMeshPixelNonAtomic",
        (ClusterVisibilityMode.HardwareOnly, true) => "ClusterRasterMeshPixelHardwareOnly",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
    internal static string TessellationDice(ClusterVisibilityMode mode) => mode switch
    {
        ClusterVisibilityMode.ImageAtomic64 => TessellationDiceImageAtomic64,
        ClusterVisibilityMode.BufferAtomic64 => TessellationDiceBufferAtomic64,
        ClusterVisibilityMode.NonAtomic => TessellationDiceNonAtomic,
        ClusterVisibilityMode.HardwareOnly => throw new InvalidOperationException("Hardware-only tessellation is consumed by fixed-function rasterization."),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
    internal static void AddResourceProperties(HashSet<uint> properties, ReadOnlySpan<ClusterMaterialStageBinding> bindings)
    {
        foreach (ref readonly var binding in bindings)
            if (binding.IsMaterialResource)
                properties.Add(binding.MaterialProperty);
    }

    internal static Shader RequireShader(Material material) => material.Shader ?? throw new InvalidDataException($"Material '{material.Name}' has no loaded user shader.");
    internal static bool HasEntry(Shader shader, SlangCompileTarget target, string entryPoint, SlangStage stage)
    {
        return shader.TryGetSlangTarget(target, out int targetIndex) && shader.TryGetSlangEntry(targetIndex, entryPoint, stage, out _);
    }
}

internal readonly record struct ClusterTessellationCompatibilityKey(ClusterComputePipeline Seed, ClusterComputePipeline Split, ClusterComputePipeline? Dice, ClusterRasterPipeline? Hardware,
    ClusterComputePipeline? ShadowSplit = null, ClusterComputePipeline? LayerSplit = null,
    ClusterComputePipeline? ShadowDice = null, ClusterRasterPipeline? ShadowHardware = null,
    ClusterComputePipeline? LayerSeed = null, ClusterComputePipeline? LayerSpillSeed = null);
internal readonly record struct ClusterRasterCompatibilityKey(ClusterComputePipeline? Software, ClusterRasterPipeline Hardware, ClusterTessellationCompatibilityKey? Tessellation, uint StageLayoutVersion, ClusterComputePipeline? TargetSoftware = null, ClusterRasterPipeline? TargetHardware = null, ClusterComputePipeline? Deformation = null);
internal readonly record struct ClusterShadeCompatibilityKey(SlangShaderData SlangCode, EntryPointReflection EntryPoint);
internal static class ClusterMaterialBindingDefaults
{
    internal static readonly SamplerDesc Sampler = new(FilterType.Linear, FilterType.Linear, FilterType.Linear, AddressType.Repeat, AddressType.Repeat, AddressType.Repeat, Label: "Cluster default material sampler");
}

// This index is derived from material addresses. A candidate shares unchanged groups, and
// records affected bin identities directly instead of diffing a newly collected scene.
internal sealed class ClusterBinIndexMap<TKey, TStage>
    where TKey : notnull
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
        Changes = ImmutableHashSet<uint>.Empty;
    }
    internal void Set(int address, TKey key, TStage stage)
    {
        if (_keys.TryGetValue(address, out TKey? previous) && !EqualityComparer<TKey>.Default.Equals(previous, key))
            Remove(address);
        bool added = !_groups.TryGetValue(key, out Group? group);
        TStage? before = added ? default : group!.Members.First().Value;
        if (added)
        {
            uint index;
            if (!_free.IsEmpty)
            {
                index = _free.Min;
                _free = _free.Remove(index);
            }
            else
            {
                index = _next;
                _next = checked(_next + 1);
            }

            group = new Group(index, ImmutableSortedDictionary<int, TStage>.Empty);
        }

        group = group!with
        {
            Members = group.Members.SetItem(address, stage)
        };
        _groups = _groups.SetItem(key, group);
        _stages = _stages.SetItem(group.Index, group.Members.First().Value);
        _updated = _updated.Add(group.Index);
        _keys = _keys.SetItem(address, key);
        if (added || !_sameBindings(before!, group.Members.First().Value))
            Changes = Changes.Add(group.Index);
    }

    internal void Remove(int address)
    {
        if (!_keys.TryGetValue(address, out TKey? key))
            return;
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

    internal TStage GetStage(int address) => _groups[_keys[address]].Members[address];
    internal ImmutableSortedDictionary<uint, TStage> GetStages(Func<TStage, uint, TStage> withIndex)
    {
        foreach (uint index in _updated)
            if (_stages.TryGetValue(index, out TStage? stage))
                _stages = _stages.SetItem(index, withIndex(stage, index));
        _updated = _updated.Clear();
        return _stages;
    }

    internal ClusterBytes GetIndices(uint capacity, ClusterBytes previous, IEnumerable<int> addresses, Func<TStage, uint, uint>? encodeIndex = null)
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

    internal static ClusterCommandBindingKey Create(Material material, ShaderParameterResources resources, ClusterMaterialStageBinding[] bindings)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(bindings);
        ReadOnlySpan<ShaderParameterResource> linked = resources.Resources;
        if (linked.Length != bindings.Length)
            throw new InvalidDataException("Cluster material binding metadata does not match the linked layout.");
        var values = new List<BindingValue>();
        for (int index = 0; index < bindings.Length; index++)
        {
            ClusterMaterialStageBinding binding = bindings[index];
            if (!binding.IsMaterialResource || !linked[index].IsUsed)
                continue;
            values.Add(new BindingValue(binding.Type, Normalize(material, binding, linked[index].Name)));
        }

        return new ClusterCommandBindingKey([..values]);
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

    internal void CollectBuffers(HashSet<SomeEngine.Graphics.Buffer> buffers)
    {
        foreach (BindingValue binding in _values)
            switch (binding.Value)
            {
                case BufferCbvValue view:
                    buffers.Add(view.Buffer);
                    break;
                case BufferSrvValue view:
                    buffers.Add(view.Buffer);
                    break;
                case BufferUavValue view:
                    buffers.Add(view.Buffer);
                    if (view.CounterBuffer is { } counter)
                        buffers.Add(counter);
                    break;
                case AccelerationValue view:
                    buffers.Add(view.Resource.Info.Storage);
                    break;
            }
    }

    internal void CollectTextures(HashSet<SomeEngine.Resources.Texture> textures)
    {
        foreach (BindingValue binding in _values)
            if (binding.Value is TextureValue value)
                textures.Add(value.Texture);
    }

    public bool Equals(ClusterCommandBindingKey? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null || _hash != other._hash || _values.Length != other._values.Length)
            return false;
        return _values.AsSpan().SequenceEqual(other._values);
    }

    public override bool Equals(object? obj) => obj is ClusterCommandBindingKey other && Equals(other);
    public override int GetHashCode() => _hash;
    private static object? Normalize(Material material, in ClusterMaterialStageBinding binding, string name)
    {
        object? value = binding.Type switch
        {
            ResourceBindingType.TextureSrv or ResourceBindingType.TextureUav => material.GetTexture(binding.MaterialProperty),
            ResourceBindingType.Sampler => material.GetSampler(binding.MaterialProperty),
            _ => material.GetDeviceResource(binding.MaterialProperty),
        };
        return binding.Type switch
        {
            ResourceBindingType.TextureSrv => value switch
            {
                null => null,
                SomeEngine.Resources.Texture texture => new TextureValue(texture),
                _ => throw InvalidValue(material, name, "Texture"),
            },
            ResourceBindingType.TextureUav => value is SomeEngine.Resources.Texture texture ? new TextureValue(texture) : throw InvalidValue(material, name, "Texture"),
            ResourceBindingType.Sampler => value switch
            {
                null => ClusterMaterialBindingDefaults.Sampler,
                SamplerDesc sampler => sampler with
                {
                    Label = null
                },
                _ => throw InvalidValue(material, name, nameof(SamplerDesc)),
            },
            ResourceBindingType.BufferSrv => value is BufferSrv srv ? BufferSrvValue.From(srv.Description) : throw InvalidValue(material, name, nameof(BufferSrv)),
            ResourceBindingType.BufferUav => value is BufferUav uav ? BufferUavValue.From(uav.Description) : throw InvalidValue(material, name, nameof(BufferUav)),
            ResourceBindingType.ConstantBuffer => value is BufferCbv cbv ? BufferCbvValue.From(cbv.Description) : throw InvalidValue(material, name, nameof(BufferCbv)),
            ResourceBindingType.AccelerationStructure => value is AccelerationStructureSrv acceleration ? new AccelerationValue(acceleration.Resource) : throw InvalidValue(material, name, nameof(AccelerationStructureSrv)),
            _ => throw new InvalidDataException($"Cluster material resource '{name}' has unsupported binding type {binding.Type}."),
        };
    }

    private static InvalidDataException InvalidValue(Material material, string name, string expected) => new($"Material '{material.Name}' slot '{name}' is not a {expected}.");
    private readonly record struct BindingValue(ResourceBindingType Type, object? Value);
    private readonly record struct TextureValue(SomeEngine.Resources.Texture Texture);
    private readonly record struct AccelerationValue(AccelerationStructure Resource);
    private readonly record struct BufferSrvValue(SomeEngine.Graphics.Buffer Buffer, BufferRange Range, Format? Format, uint StructureStride)
    {
        internal static BufferSrvValue From(in BufferSrvDesc value) => new(value.Buffer, value.Range, value.Format, value.StructureStride);
    }

    private readonly record struct BufferUavValue(SomeEngine.Graphics.Buffer Buffer, BufferRange Range, Format? Format, uint StructureStride, SomeEngine.Graphics.Buffer? CounterBuffer, ulong CounterOffset)
    {
        internal static BufferUavValue From(in BufferUavDesc value) => new(value.Buffer, value.Range, value.Format, value.StructureStride, value.CounterBuffer, value.CounterOffset);
    }

    private readonly record struct BufferCbvValue(SomeEngine.Graphics.Buffer Buffer, BufferRange Range)
    {
        internal static BufferCbvValue From(in BufferCbvDesc value) => new(value.Buffer, value.Range);
    }
}

internal readonly record struct ClusterRasterBinKey(ClusterRasterCompatibilityKey Execution, string VertexEncoding, ClusterCommandBindingKey Bindings, bool Layer);
internal readonly record struct ClusterShadeBinKey(ClusterShadeCompatibilityKey Execution, long MaterialId, string VertexEncoding, uint ScalarLayoutHash, uint ScalarPayloadBytes, ClusterCommandBindingKey Bindings);
internal readonly record struct ClusterLinkedStageKey(SlangShaderData SlangCode, SlangCompileTarget Target, string FirstEntry, string? SecondEntry);
/// <summary>Owns raster-only compatibility grouping and its material-slot mapping.</summary>
internal sealed class ClusterRasterStages : IDisposable
{
    private static readonly SomeEngine.Diagnostics.TraceSite ResolveTrace = new("render", "Cluster.RasterStageResolve");
    private readonly RenderPipelines _renderPipelines;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly ClusterVisibilityMode _visibilityMode;
    private readonly IGraphResourceProvider? _featureResources;
    private readonly Dictionary<ClusterRasterSoftwareKey, ClusterComputePipeline> _softwarePipelines = [];
    private readonly Dictionary<ClusterRasterSoftwareKey, ClusterComputePipeline> _tessellationPipelines = [];
    private readonly Dictionary<ClusterRasterHardwareKey, ClusterRasterPipeline> _hardwarePipelines = [];
    private readonly Dictionary<ClusterLinkedStageKey, ShaderParameterResources> _linkedLayouts = [];
    private ClusterBinIndexMap<ClusterRasterBinKey, ClusterRasterDispatch> _binIndices = new(static (before, after) => ReferenceEquals(before.Material, after.Material) && before.HasSameBindings(after) && before.Bounds == after.Bounds);
    private ImmutableDictionary<long, HashSet<uint>> _resourceProperties = ImmutableDictionary<long, HashSet<uint>>.Empty;
    private ImmutableDictionary<long, HashSet<uint>?> _depthProperties = ImmutableDictionary<long, HashSet<uint>?>.Empty;
    internal bool ResourcePropertiesMatch(Material material, ReadOnlySpan<ulong> revisions)
    {
        if (_resourceProperties.TryGetValue(material.MaterialId, out var properties))
            foreach (uint property in properties)
                if (material.GetPropertyRevision(property) != revisions[(int)property])
                    return false;
        return true;
    }

    internal bool TryGetMaterialDepthInputs(long materialId, out HashSet<uint> properties)
    {
        if (_depthProperties.TryGetValue(materialId, out var found) && found is not null)
        {
            properties = found;
            return true;
        }

        properties = null !;
        return false;
    }

    private ClusterRasterStageSet? _resolved;
    private bool _disposed;
    internal ClusterRasterStages(IGraphicsBackend backend, Device device, RenderPipelines pipelines, ClusterVisibilityMode visibilityMode, IGraphResourceProvider? featureResources)
    {
        _renderPipelines = pipelines;
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _visibilityMode = visibilityMode;
        _featureResources = featureResources;
    }

    private ClusterMaterialAssignmentSet? _resolvedAssignments;
    internal ClusterRasterStageSet Resolve(ClusterMaterialAssignmentSet assignments)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(ResolveTrace);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(assignments, _resolvedAssignments))
            return _resolved!;
        var addresses = _resolvedAssignments is null ? new HashSet<int>(assignments.ActiveOffsets) : new HashSet<int>(assignments.ChangedOffsets);
        foreach (long material in assignments.ChangedMaterialIds)
            addresses.UnionWith(assignments.GetMaterialOffsets(material));
        var bins = new ClusterBinIndexMap<ClusterRasterBinKey, ClusterRasterDispatch>(_binIndices);
        var resourceProperties = _resourceProperties;
        var depthProperties = _depthProperties;
        foreach (long material in assignments.ChangedMaterialIds)
        {
            resourceProperties = resourceProperties.Remove(material);
            depthProperties = depthProperties.Remove(material);
        }

        foreach (int address in addresses)
        {
            if (!assignments.TryGetEntry(address, out Material material, out string encoding))
            {
                bins.Remove(address);
                continue;
            }

            ClusterRasterDispatch dispatchData = ResolveDispatch(material, encoding, out uint[]? materialDepthInputs);
            HashSet<uint> properties = [];
            if (dispatchData.SoftwareResources is { } software)
                ClusterMaterialEntries.AddResourceProperties(properties, software.MaterialBindings);
            ClusterMaterialEntries.AddResourceProperties(properties, dispatchData.HardwareResources.MaterialBindings);
            if (dispatchData.Deformation is { } deformation)
                foreach (ClusterDeformStage stage in deformation.Stages)
                    ClusterMaterialEntries.AddResourceProperties(properties, stage.Resources.MaterialBindings);
            if (dispatchData.TargetStages?.SoftwareResources is { } targetSoftware)
                ClusterMaterialEntries.AddResourceProperties(properties, targetSoftware.MaterialBindings);
            if (dispatchData.TargetStages?.HardwareResources is { } targetHardware)
                ClusterMaterialEntries.AddResourceProperties(properties, targetHardware.MaterialBindings);
            if (dispatchData.Tessellation is { } tessellation)
            {
                ClusterMaterialEntries.AddResourceProperties(properties, tessellation.SeedResources.MaterialBindings);
                ClusterMaterialEntries.AddResourceProperties(properties, tessellation.SplitResources.MaterialBindings);
                if (tessellation.DiceResources is { } dice)
                    ClusterMaterialEntries.AddResourceProperties(properties, dice.MaterialBindings);
                if (tessellation.HardwareResources is { } hardware)
                    ClusterMaterialEntries.AddResourceProperties(properties, hardware.MaterialBindings);
            }

            var key = new ClusterRasterBinKey(dispatchData.Key, encoding, dispatchData.CommandBindings, material.Layer.HasValue);
            if (resourceProperties.TryGetValue(material.MaterialId, out var existing))
                properties.UnionWith(existing);
            resourceProperties = resourceProperties.SetItem(material.MaterialId, properties);
            HashSet<uint>? depthInputs = materialDepthInputs is { } inputs ? [..inputs] : null;
            if (depthProperties.TryGetValue(material.MaterialId, out var previousInputs))
            {
                if (depthInputs is not null && previousInputs is not null)
                    depthInputs.UnionWith(previousInputs);
                else
                    depthInputs = null;
            }

            depthProperties = depthProperties.SetItem(material.MaterialId, depthInputs);
            bins.Set(address, key, dispatchData);
        }

        var dispatches = bins.GetStages(static (stage, index) => stage with { BinIndex = index });
        var boundsChanges = new List<(int Offset, byte[] Bytes)>();
        foreach ((int offset, int count)in assignments.MaterialRanges)
        {
            bool changed = false;
            bool finite = true;
            for (int index = 0; index < count; index++)
            {
                changed |= addresses.Contains(offset + index);
                finite &= bins.GetStage(offset + index).Bounds == ClusterConservativeBounds.Finite;
            }

            if (!changed)
                continue;
            byte[] bytes = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, finite ? 1u : 0u);
            boundsChanges.Add((checked(offset * sizeof(uint)), bytes));
        }

        ClusterBytes materialBounds = (_resolved?.MaterialBounds ?? ClusterBytes.Zero(0)).WithChanges(checked((int)assignments.MaterialAddressCapacity * sizeof(uint)), boundsChanges);
        var result = new ClusterRasterStageSet(bins.GetIndices(assignments.MaterialAddressCapacity, _resolved?.BinIndices ?? ClusterBytes.Zero(0), addresses, static (stage, index) => index | (stage.Software is null ? 0u : 0x80000000u) | (stage.Hardware.UsesMaterialRanges && stage.Tessellation is null ? 0x40000000u : 0u) | (stage.SoftwareUsesMaterialRanges && stage.Tessellation is null ? 0x20000000u : 0u) | (stage.PixelVisibility ? 0x10000000u : 0u) | (stage.Material.Layer.HasValue ? 0x08000000u : 0u)), dispatches, bins.Range, materialBounds, [..boundsChanges.Select(change => change.Offset / sizeof(uint))], [..bins.Changes], [..addresses], _resolved);
        _binIndices = bins;
        _resourceProperties = resourceProperties;
        _depthProperties = depthProperties;
        _resolvedAssignments = assignments;
        _resolved = result;
        return result;
    }

    private ClusterRasterDispatch ResolveDispatch(Material material, string vertexEncoding, out uint[]? materialDepthInputs)
    {
        Shader shader = ClusterMaterialEntries.RequireShader(material);
        SlangCompileTarget target = _device.Capabilities.CompileTarget;
        ClusterComputePipeline? software;
        ClusterRasterPipeline hardware;
        ClusterConservativeBounds bounds = ClusterConservativeBounds.Finite;
        string? softwareEntry = _visibilityMode == ClusterVisibilityMode.HardwareOnly ? null : ClusterMaterialEntries.SoftwareRaster(_visibilityMode);
        string meshletSoftwareEntry = _visibilityMode switch
        {
            ClusterVisibilityMode.ImageAtomic64 => ClusterMaterialEntries.SoftwareMeshletImageAtomic64,
            ClusterVisibilityMode.NonAtomic => ClusterMaterialEntries.SoftwareMeshletNonAtomic,
            _ => ClusterMaterialEntries.SoftwareMeshletBufferAtomic64,
        };
        bool softwareUsesMaterialRanges = softwareEntry is not null && ClusterMaterialEntries.HasEntry(shader, target, meshletSoftwareEntry, SlangStage.Compute);
        if (softwareUsesMaterialRanges)
            softwareEntry = meshletSoftwareEntry;
        string hardwarePixelEntry = ClusterMaterialEntries.HardwarePixel(_visibilityMode, ClusterRasterPipeline.SupportsMesh(_backend, _device));
        string? tessellationDiceEntry = _visibilityMode == ClusterVisibilityMode.HardwareOnly ? null : ClusterMaterialEntries.TessellationDice(_visibilityMode);
        bool hasSoftware = softwareEntry is not null && ClusterMaterialEntries.HasEntry(shader, target, softwareEntry, SlangStage.Compute);
        bool usesMesh = ClusterRasterPipeline.SupportsMesh(_backend, _device);
        bool hasMesh = ClusterMaterialEntries.HasEntry(shader, target, ClusterMaterialEntries.HardwareMesh, SlangStage.Mesh);
        bool hasMeshlet = ClusterMaterialEntries.HasEntry(shader, target, ClusterMaterialEntries.HardwareMeshlet, SlangStage.Mesh);
        string hardwareEntry = usesMesh ? ClusterMaterialEntries.HardwareMesh : ClusterMaterialEntries.HardwareVertex;
        if (usesMesh && hasMeshlet && ClusterRasterPipeline.SupportsMeshlets(_backend, _device))
            hardwareEntry = ClusterMaterialEntries.HardwareMeshlet;
        bool selectedMeshAvailable = hardwareEntry == ClusterMaterialEntries.HardwareMeshlet ? hasMeshlet : hasMesh;
        bool hasVertex = ClusterMaterialEntries.HasEntry(shader, target, ClusterMaterialEntries.HardwareVertex, SlangStage.Vertex);
        bool hasPixel = ClusterMaterialEntries.HasEntry(shader, target, hardwarePixelEntry, SlangStage.Fragment);
        if ((_visibilityMode != ClusterVisibilityMode.HardwareOnly && !hasSoftware) || !hasVertex || (usesMesh && !selectedMeshAvailable) || !hasPixel)
        {
            throw new InvalidDataException($"Material '{material.Name}' must provide all Cluster visibility raster entries together.");
        }

        software = softwareEntry is null ? null : ResolveSoftwarePipeline(material, shader, softwareEntry, vertexEncoding);
        hardware = ResolveHardwarePipeline(material, shader, hardwareEntry, hardwarePixelEntry, vertexEncoding);
        bounds = ResolveBounds(material, shader, softwareEntry ?? hardwareEntry, hardwareEntry);
        bool hasSeed = ClusterMaterialEntries.HasEntry(shader, target, ClusterMaterialEntries.TessellationSeed, SlangStage.Compute);
        bool hasSplit = ClusterMaterialEntries.HasEntry(shader, target, ClusterMaterialEntries.TessellationSplit, SlangStage.Compute);
        bool hasDice = tessellationDiceEntry is not null && ClusterMaterialEntries.HasEntry(shader, target, tessellationDiceEntry, SlangStage.Compute);
        bool hasTessellationHardwareVertex = ClusterMaterialEntries.HasEntry(shader, target, ClusterMaterialEntries.TessellationHardwareVertex, SlangStage.Vertex);
        bool hasTessellationHardwarePixel = ClusterMaterialEntries.HasEntry(shader, target, ClusterMaterialEntries.TessellationHardwarePixel, SlangStage.Fragment);
        bool hasTessellationTerminal = _visibilityMode == ClusterVisibilityMode.HardwareOnly ? hasTessellationHardwareVertex || hasTessellationHardwarePixel : hasDice;
        bool hasTessellationOverride = hasSeed || hasSplit || hasTessellationTerminal;
        if (hasTessellationOverride)
        {
            bool hasCompleteTerminal = _visibilityMode == ClusterVisibilityMode.HardwareOnly ? hasTessellationHardwareVertex && hasTessellationHardwarePixel : hasDice;
            if (!hasSeed || !hasSplit || !hasCompleteTerminal)
            {
                throw new InvalidDataException($"Material '{material.Name}' must provide all Cluster tessellation entries together.");
            }

            if (!ClusterMaterialEntries.HasEntry(shader, target, ClusterMaterialEntries.Shade, SlangStage.Compute))
            {
                throw new InvalidDataException($"Material '{material.Name}' provides runtime tessellation, so its same shader must also " + "provide ClusterShade. Forward domain evaluation and depth reconstruction cannot come " + "from different shader compositions.");
            }
        }

        return CreateDispatch(material, vertexEncoding, software, hardware, bounds, shader, hasTessellationOverride ? shader : null, softwareEntry, softwareUsesMaterialRanges, out materialDepthInputs);
    }

    private ClusterRasterDispatch CreateDispatch(Material material, string vertexEncoding, ClusterComputePipeline? software, ClusterRasterPipeline hardware, ClusterConservativeBounds bounds, Shader? visibilityShader, Shader? tessellationShader, string? softwareEntry, bool softwareUsesMaterialRanges, out uint[]? materialDepthInputs)
    {
        ClusterRasterStageResources<ClusterSoftwareRasterCoreResource>? softwareResources = software is null ? null : ResolveRasterResources(material, software.ParameterLayout, visibilityShader, visibilityShader is null ? null : softwareEntry, null, ClusterSoftwareRasterCoreResources.Resolve, ClusterSoftwareRasterCoreResources.Contains);
        ClusterRasterStageResources<ClusterHardwareRasterCoreResource> hardwareResources = ResolveRasterResources(material, hardware.ParameterLayout, visibilityShader, visibilityShader is null ? null : hardware.GeometryEntry, visibilityShader is null ? null : ClusterMaterialEntries.HardwarePixel(_visibilityMode, hardware.UsesMesh), ClusterHardwareRasterCoreResources.Resolve, ClusterHardwareRasterCoreResources.Contains);
        var bindingKeys = new List<ClusterCommandBindingKey>();
        if (softwareResources is not null)
        {
            bindingKeys.Add(ClusterCommandBindingKey.Create(material, softwareResources.Resources, softwareResources.MaterialBindings));
        }

        bindingKeys.Add(ClusterCommandBindingKey.Create(material, hardwareResources.Resources, hardwareResources.MaterialBindings));
        ClusterRasterTargetStages? targetStages = visibilityShader is null ? null : CreateTargetStages(material, visibilityShader, vertexEncoding, hardware, softwareUsesMaterialRanges);
        if (targetStages?.Software is not null && ClusterGeometryRequirements.RequireConservativeBounds(visibilityShader!, softwareUsesMaterialRanges ? ClusterMaterialEntries.SoftwareMeshletTarget : ClusterMaterialEntries.SoftwareTarget, $"Material '{material.Name}' target software raster") != ClusterConservativeBounds.Finite)
            bounds = ClusterConservativeBounds.Unbounded;
        if (targetStages?.SoftwareResources is { } targetSoftware)
            bindingKeys.Add(ClusterCommandBindingKey.Create(material, targetSoftware.Resources, targetSoftware.MaterialBindings));
        if (targetStages?.HardwareResources is { } targetHardware)
            bindingKeys.Add(ClusterCommandBindingKey.Create(material, targetHardware.Resources, targetHardware.MaterialBindings));
        ClusterRasterTargetStages spillStages = CreateSpillStages(material, visibilityShader!, vertexEncoding, false);
        ClusterRasterTargetStages? spillTargetStages = targetStages is null ? null : CreateSpillStages(material, visibilityShader!, vertexEncoding, true);
        foreach (ClusterRasterTargetStages? spill in new[] { spillStages, spillTargetStages })
        {
            if (spill?.SoftwareResources is { } inputs) bindingKeys.Add(ClusterCommandBindingKey.Create(material, inputs.Resources, inputs.MaterialBindings));
            if (spill?.HardwareResources is { } geometry) bindingKeys.Add(ClusterCommandBindingKey.Create(material, geometry.Resources, geometry.MaterialBindings));
        }
        ClusterTessellationDispatch? tessellation = null;
        if (tessellationShader is not null)
        {
            ClusterComputePipeline seed = ResolveTessellationPipeline(material, tessellationShader, ClusterMaterialEntries.TessellationSeed, vertexEncoding, "seed");
            ClusterComputePipeline spillSeed = ResolveTessellationPipeline(material, tessellationShader, "ClusterTessellationSeedSpilled", vertexEncoding, "spilled seed");
            ClusterComputePipeline layerSeed = ResolveTessellationPipeline(material, tessellationShader, "ClusterTessellationSeedLayer", vertexEncoding, "layer seed");
            ClusterComputePipeline layerSpillSeed = ResolveTessellationPipeline(material, tessellationShader, "ClusterTessellationSeedSpilledLayer", vertexEncoding, "layer spilled seed");
            ClusterRasterStageResources<ClusterTessellationCoreResource> spillSeedResources = ResolveRasterResources(material, spillSeed.ParameterLayout, tessellationShader, "ClusterTessellationSeedSpilled", null, ClusterTessellationCoreResources.Resolve, ClusterTessellationCoreResources.Contains);
            ClusterRasterStageResources<ClusterTessellationCoreResource> layerSeedResources = ResolveRasterResources(material, layerSeed.ParameterLayout, tessellationShader, "ClusterTessellationSeedLayer", null, ClusterTessellationCoreResources.Resolve, ClusterTessellationCoreResources.Contains);
            ClusterRasterStageResources<ClusterTessellationCoreResource> layerSpillSeedResources = ResolveRasterResources(material, layerSpillSeed.ParameterLayout, tessellationShader, "ClusterTessellationSeedSpilledLayer", null, ClusterTessellationCoreResources.Resolve, ClusterTessellationCoreResources.Contains);
            bindingKeys.Add(ClusterCommandBindingKey.Create(material, spillSeedResources.Resources, spillSeedResources.MaterialBindings));
            ClusterComputePipeline split = ResolveTessellationPipeline(material, tessellationShader, ClusterMaterialEntries.TessellationSplit, vertexEncoding, "split");
            ClusterComputePipeline shadowSplit = ResolveTessellationPipeline(material, tessellationShader, "ClusterTessellationSplitTarget", vertexEncoding, "shadow split");
            ClusterComputePipeline layerSplit = ResolveTessellationPipeline(material, tessellationShader, "ClusterTessellationSplitLayer", vertexEncoding, "layer split");
            ClusterComputePipeline? dice = _visibilityMode == ClusterVisibilityMode.HardwareOnly ? null : ResolveTessellationPipeline(material, tessellationShader, ClusterMaterialEntries.TessellationDice(_visibilityMode), vertexEncoding, "dicing");
            ClusterComputePipeline? shadowDice = _visibilityMode == ClusterVisibilityMode.HardwareOnly ? null : ResolveTessellationPipeline(material, tessellationShader, "ClusterTessellationDiceTarget", vertexEncoding, "shadow dicing");
            ClusterRasterPipeline? tessellationHardware = _visibilityMode == ClusterVisibilityMode.HardwareOnly ? ResolveHardwarePipeline(material, tessellationShader, ClusterRasterPipeline.SupportsMesh(_backend, _device) ? ClusterMaterialEntries.TessellationHardwareMesh : ClusterMaterialEntries.TessellationHardwareVertex, ClusterRasterPipeline.SupportsMesh(_backend, _device) ? ClusterMaterialEntries.TessellationHardwareMeshPixel : ClusterMaterialEntries.TessellationHardwarePixel, vertexEncoding) : null;
            string shadowVertex = tessellationHardware?.UsesMesh == true ? "ClusterTessellationHardwareMeshTarget" : "ClusterTessellationHardwareVertexTarget";
            string shadowPixel = tessellationHardware?.UsesMesh == true ? "ClusterTessellationHardwareMeshPixelTarget" : "ClusterTessellationHardwarePixelTarget";
            ClusterRasterPipeline? shadowHardware = tessellationHardware is null ? null : ResolveHardwarePipeline(material,
                tessellationShader, shadowVertex, shadowPixel, vertexEncoding, targetOutput: true);
            ClusterRasterStageResources<ClusterTessellationCoreResource> seedResources = ResolveRasterResources(material, seed.ParameterLayout, tessellationShader, ClusterMaterialEntries.TessellationSeed, null, ClusterTessellationCoreResources.Resolve, ClusterTessellationCoreResources.Contains);
            ClusterRasterStageResources<ClusterTessellationCoreResource> splitResources = ResolveRasterResources(material, split.ParameterLayout, tessellationShader, ClusterMaterialEntries.TessellationSplit, null, ClusterTessellationCoreResources.Resolve, ClusterTessellationCoreResources.Contains);
            ClusterRasterStageResources<ClusterTessellationCoreResource> shadowSplitResources = ResolveRasterResources(material, shadowSplit.ParameterLayout, tessellationShader, "ClusterTessellationSplitTarget", null, ClusterTessellationCoreResources.Resolve, ClusterTessellationCoreResources.Contains);
            ClusterRasterStageResources<ClusterTessellationCoreResource> layerSplitResources = ResolveRasterResources(material, layerSplit.ParameterLayout, tessellationShader, "ClusterTessellationSplitLayer", null, ClusterTessellationCoreResources.Resolve, ClusterTessellationCoreResources.Contains);
            ClusterRasterStageResources<ClusterTessellationCoreResource>? diceResources = dice is null ? null : ResolveRasterResources(material, dice.ParameterLayout, tessellationShader, ClusterMaterialEntries.TessellationDice(_visibilityMode), null, ClusterTessellationCoreResources.Resolve, ClusterTessellationCoreResources.Contains);
            ClusterRasterStageResources<ClusterTessellationCoreResource>? shadowDiceResources = shadowDice is null ? null : ResolveRasterResources(material, shadowDice.ParameterLayout, tessellationShader, "ClusterTessellationDiceTarget", null, ClusterTessellationCoreResources.Resolve, ClusterTessellationCoreResources.Contains);
            ClusterRasterStageResources<ClusterTessellationCoreResource>? shadowHardwareResources = shadowHardware is null ? null : ResolveRasterResources(material, shadowHardware.ParameterLayout, tessellationShader, shadowVertex, shadowPixel, ClusterTessellationCoreResources.Resolve, ClusterTessellationCoreResources.Contains);
            ClusterRasterStageResources<ClusterTessellationCoreResource>? tessellationHardwareResources = tessellationHardware is null ? null : ResolveRasterResources(material, tessellationHardware.ParameterLayout, tessellationShader, ClusterRasterPipeline.SupportsMesh(_backend, _device) ? ClusterMaterialEntries.TessellationHardwareMesh : ClusterMaterialEntries.TessellationHardwareVertex, ClusterRasterPipeline.SupportsMesh(_backend, _device) ? ClusterMaterialEntries.TessellationHardwareMeshPixel : ClusterMaterialEntries.TessellationHardwarePixel, ClusterTessellationCoreResources.Resolve, ClusterTessellationCoreResources.Contains);
            ClusterCommandBindingKey splitBindings = ClusterCommandBindingKey.Create(material, splitResources.Resources, splitResources.MaterialBindings);
            bindingKeys.Add(ClusterCommandBindingKey.Create(material, seedResources.Resources, seedResources.MaterialBindings));
            bindingKeys.Add(splitBindings);
            if (diceResources is not null)
            {
                bindingKeys.Add(ClusterCommandBindingKey.Create(material, diceResources.Resources, diceResources.MaterialBindings));
            }

            if (tessellationHardwareResources is not null)
            {
                bindingKeys.Add(ClusterCommandBindingKey.Create(material, tessellationHardwareResources.Resources, tessellationHardwareResources.MaterialBindings));
            }

            foreach (ClusterRasterStageResources<ClusterTessellationCoreResource>? resources in new[] { layerSeedResources, layerSpillSeedResources, shadowSplitResources, layerSplitResources, shadowDiceResources, shadowHardwareResources })
                if (resources is not null) bindingKeys.Add(ClusterCommandBindingKey.Create(material, resources.Resources, resources.MaterialBindings));
            tessellation = new ClusterTessellationDispatch(new ClusterTessellationCompatibilityKey(seed, split, dice, tessellationHardware, shadowSplit, layerSplit, shadowDice, shadowHardware, layerSeed, layerSpillSeed), seed, split, dice, tessellationHardware, seedResources, splitResources, diceResources, tessellationHardwareResources, splitBindings)
            {
                SpillSeed = spillSeed, SpillSeedResources = spillSeedResources,
                LayerSeed = layerSeed, LayerSeedResources = layerSeedResources,
                LayerSpillSeed = layerSpillSeed, LayerSpillSeedResources = layerSpillSeedResources,
                ShadowSplit = shadowSplit, ShadowSplitResources = shadowSplitResources,
                LayerSplit = layerSplit, LayerSplitResources = layerSplitResources,
                ShadowDice = shadowDice, ShadowDiceResources = shadowDiceResources,
                ShadowHardware = shadowHardware, ShadowHardwareResources = shadowHardwareResources,
            };
            ClusterConservativeBounds tessellationBounds = ClusterGeometryRequirements.RequireConservativeBounds(tessellationShader, ClusterMaterialEntries.TessellationSplit, $"Material '{material.Name}' tessellation split");
            if (tessellationBounds != ClusterConservativeBounds.Finite)
                bounds = ClusterConservativeBounds.Unbounded;
        }

        ClusterDeformation? deformation = ResolveDeformation(material, vertexEncoding, bindingKeys);
        ClusterCommandBindingKey commandBindings = ClusterCommandBindingKey.Combine([..bindingKeys]);
        var key = new ClusterRasterCompatibilityKey(software, hardware, tessellation?.Key, StageLayoutVersion: 4, TargetSoftware: targetStages?.Software, TargetHardware: targetStages?.Hardware, Deformation: deformation?.Stages[0].Pipeline);
        materialDepthInputs = visibilityShader is not null && tessellation is null ? ClusterGeometryRequirements.ReadMaterialDepthInputs(material, visibilityShader, visibilityShader.RequireSlangTarget(_device.Capabilities.CompileTarget), software is null ? null : softwareEntry, hardware.GeometryEntry, ClusterMaterialEntries.HardwarePixel(_visibilityMode, hardware.UsesMesh), targetStages?.Software is null ? null : softwareUsesMaterialRanges ? ClusterMaterialEntries.SoftwareMeshletTarget : ClusterMaterialEntries.SoftwareTarget, targetStages?.Hardware?.GeometryEntry, targetStages?.Hardware is null ? null : targetStages.Hardware.UsesMesh ? ClusterMaterialEntries.HardwareMeshPixelTarget : ClusterMaterialEntries.HardwarePixelTarget) : null;
        return new ClusterRasterDispatch(0, material, vertexEncoding, key, software, hardware, tessellation, bounds, softwareResources, hardwareResources, commandBindings)
        {
            SoftwareUsesMaterialRanges = softwareUsesMaterialRanges,
            TargetStages = targetStages,
            SpillStages = spillStages,
            SpillTargetStages = spillTargetStages,
            Deformation = deformation,
            VisibilityDepth = visibilityShader is null ? ClusterVisibilityDepth.Opaque : ClusterGeometryRequirements.RequireVisibilityDepth(visibilityShader)
        };
    }

    private ClusterRasterTargetStages CreateSpillStages(Material material, Shader shader, string vertexEncoding, bool target)
    {
        string entry = target ? "ClusterRasterSoftwareSpilledTarget" : _visibilityMode switch
        {
            ClusterVisibilityMode.ImageAtomic64 => "ClusterRasterSoftwareSpilledImageAtomic64",
            ClusterVisibilityMode.NonAtomic => "ClusterRasterSoftwareSpilledNonAtomic",
            _ => "ClusterRasterSoftwareSpilledBufferAtomic64",
        };
        ClusterComputePipeline? software = _visibilityMode == ClusterVisibilityMode.HardwareOnly ? null
            : ResolveSoftwarePipeline(material, shader, entry, vertexEncoding);
        string pixel = target ? ClusterMaterialEntries.HardwarePixelTarget : ClusterMaterialEntries.HardwarePixel(_visibilityMode, false);
        string vertex = target ? "ClusterRasterSpilledVertexTarget" : "ClusterRasterSpilledVertex";
        ClusterRasterPipeline hardware = ResolveHardwarePipeline(material, shader, vertex, pixel, vertexEncoding, target);
        var softwareResources = software is null ? null : ResolveRasterResources(material, software.ParameterLayout, shader,
            entry, null, ClusterSoftwareRasterCoreResources.Resolve, ClusterSoftwareRasterCoreResources.Contains);
        var hardwareResources = ResolveRasterResources(material, hardware.ParameterLayout, shader,
            vertex, pixel, ClusterHardwareRasterCoreResources.Resolve, ClusterHardwareRasterCoreResources.Contains);
        return new(software, hardware, softwareResources, hardwareResources);
    }

    private ClusterRasterTargetStages? CreateTargetStages(Material material, Shader shader, string vertexEncoding, ClusterRasterPipeline primaryHardware, bool softwareUsesMaterialRanges)
    {
        string softwareEntry = softwareUsesMaterialRanges ? ClusterMaterialEntries.SoftwareMeshletTarget : ClusterMaterialEntries.SoftwareTarget;
        string pixelEntry = primaryHardware.UsesMesh ? ClusterMaterialEntries.HardwareMeshPixelTarget : ClusterMaterialEntries.HardwarePixelTarget;
        SlangCompileTarget target = _device.Capabilities.CompileTarget;
        ClusterComputePipeline? software = _visibilityMode != ClusterVisibilityMode.HardwareOnly && ClusterMaterialEntries.HasEntry(shader, target, softwareEntry, SlangStage.Compute) ? ResolveSoftwarePipeline(material, shader, softwareEntry, vertexEncoding) : null;
        string vertexEntry = primaryHardware.GeometryEntry + "Target";
        ClusterRasterPipeline? hardware = ClusterMaterialEntries.HasEntry(shader, target, pixelEntry, SlangStage.Fragment) ? ResolveHardwarePipeline(material, shader, vertexEntry, pixelEntry, vertexEncoding, targetOutput: true) : null;
        if (software is null && hardware is null)
            return null;
        var softwareResources = software is null ? null : ResolveRasterResources(material, software.ParameterLayout, shader, softwareEntry, null, ClusterSoftwareRasterCoreResources.Resolve, ClusterSoftwareRasterCoreResources.Contains);
        var hardwareResources = hardware is null ? null : ResolveRasterResources(material, hardware.ParameterLayout, shader, hardware.GeometryEntry, pixelEntry, ClusterHardwareRasterCoreResources.Resolve, ClusterHardwareRasterCoreResources.Contains);
        return new ClusterRasterTargetStages(software, hardware, softwareResources, hardwareResources);
    }

    private ClusterRasterStageResources<TCore> ResolveRasterResources<TCore>(Material material, VariableLayoutReflection parameterLayout, Shader? shader, string? firstEntry, string? secondEntry, Func<string, ResourceBindingType, TCore> resolveCore, Func<string, bool> isCore)
        where TCore : struct, Enum
    {
        ShaderParameterResources resources = shader is null ? ShaderParameterResources.Create(parameterLayout) : ResolveLinkedLayout(shader, firstEntry ?? throw new InvalidDataException("A custom raster stage has no entry name."), secondEntry);
        Dictionary<string, byte> materialResources = shader is null ? new Dictionary<string, byte>(StringComparer.Ordinal) : ResolveMaterialResources(material, resources);
        resources.ValidateMaterialResources(materialResources);
        GraphResourceBinder? [] featureBinders = resources.ResolveFeatureResources(materialResources, _featureResources,
            name => isCore(name) || ClusterRasterInputResources.Contains(name) || ClusterDeformResourceNames.IsRead(name),
            allowNeutralRasterTarget: true);
        ClusterMaterialStageBinding[] materialBindings = ResolveMaterialBindings(material, resources, materialResources, featureBinders);
        var coreKinds = new TCore[resources.Resources.Length];
        for (int index = 0; index < coreKinds.Length; index++)
        {
            ref readonly ShaderParameterResource resource = ref resources.Resources[index];
            if (resource.IsUsed)
                coreKinds[index] = resolveCore(resource.Name, resource.Type);
        }

        return new ClusterRasterStageResources<TCore>(resources, coreKinds, materialBindings, featureBinders);
    }

    private ShaderParameterResources ResolveLinkedLayout(Shader shader, string firstEntry, string? secondEntry)
    {
        SlangCompileTarget target = _device.Capabilities.CompileTarget;
        var key = new ClusterLinkedStageKey(shader.SlangCode, target, firstEntry, secondEntry);
        if (_linkedLayouts.TryGetValue(key, out ShaderParameterResources? resources))
            return resources;
        resources = ShaderParameterResources.Create(shader, target, firstEntry, secondEntry);
        _linkedLayouts.Add(key, resources);
        return resources;
    }

    private static Dictionary<string, byte> ResolveMaterialResources(Material material, ShaderParameterResources resources)
    {
        var result = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (ref readonly ShaderParameterResource resource in resources.Resources)
        {
            string resourceName = resource.Name;
            if (!resource.IsUsed || resource.Type == ResourceBindingType.None || !material.Properties.Any(property => string.Equals(property.Name, resourceName, StringComparison.Ordinal)))
            {
                continue;
            }

            result.TryAdd(resource.Name, MaterialResourceKind(resource.Type));
        }

        return result;
    }

    private static byte MaterialResourceKind(ResourceBindingType type) => type switch
    {
        ResourceBindingType.TextureSrv or ResourceBindingType.TextureUav => 0,
        ResourceBindingType.Sampler => 1,
        ResourceBindingType.BufferSrv or ResourceBindingType.BufferUav or ResourceBindingType.ConstantBuffer => 2,
        ResourceBindingType.AccelerationStructure => 3,
        _ => throw new InvalidDataException($"Resource type {type} cannot be a material resource."),
    };
    private static ClusterMaterialStageBinding[] ResolveMaterialBindings(Material material, ShaderParameterResources resources, IReadOnlyDictionary<string, byte> materialResources, GraphResourceBinder? [] featureBinders)
    {
        ReadOnlySpan<ShaderParameterResource> linked = resources.Resources;
        var result = new ClusterMaterialStageBinding[linked.Length];
        IReadOnlyList<MaterialPropertyDefinition> propertyDefinitions = material.Properties;
        for (int resourceIndex = 0; resourceIndex < linked.Length; resourceIndex++)
        {
            if (featureBinders[resourceIndex] is not null)
                continue;
            ref readonly ShaderParameterResource resource = ref linked[resourceIndex];
            if (!materialResources.ContainsKey(resource.Name))
                continue;
            uint materialProperty = FindMaterialProperty(material, propertyDefinitions, resource.Name);
            result[resourceIndex] = new ClusterMaterialStageBinding(materialProperty, resource.Type, true);
        }

        return result;
    }

    private static uint FindMaterialProperty(Material material, IReadOnlyList<MaterialPropertyDefinition> propertyDefinitions, string name)
    {
        for (uint propertyIndex = 0; propertyIndex < propertyDefinitions.Count; propertyIndex++)
        {
            if (string.Equals(propertyDefinitions[checked((int)propertyIndex)].Name, name, StringComparison.Ordinal))
                return propertyIndex;
        }

        throw new InvalidDataException($"Material '{material.Name}' does not expose linked resource '{name}'.");
    }

    private ClusterComputePipeline ResolveSoftwarePipeline(Material material, Shader shader, string entryPoint, string vertexEncoding)
    {
        ClusterStageTargets.RequireVertexEncoding(shader, entryPoint, vertexEncoding, $"Material '{material.Name}' software raster");
        int targetIndex = shader.RequireSlangTarget(_device.Capabilities.CompileTarget);
        EntryPointReflection entry = shader.RequireSlangEntry(targetIndex, entryPoint, SlangStage.Compute);
        var key = new ClusterRasterSoftwareKey(shader.SlangCode, entry);
        if (_softwarePipelines.TryGetValue(key, out ClusterComputePipeline? pipeline))
            return pipeline;
        pipeline = ClusterComputePipeline.Create(_renderPipelines, shader, entry, $"Cluster material software raster {entryPoint}");
        _softwarePipelines.Add(key, pipeline);
        return pipeline;
    }

    private ClusterComputePipeline ResolveTessellationPipeline(Material material, Shader shader, string entryPoint, string vertexEncoding, string phase)
    {
        ClusterStageTargets.RequireVertexEncoding(shader, entryPoint, vertexEncoding, $"Material '{material.Name}' tessellation {phase}");
        int targetIndex = shader.RequireSlangTarget(_device.Capabilities.CompileTarget);
        EntryPointReflection entry = shader.RequireSlangEntry(targetIndex, entryPoint, SlangStage.Compute);
        var key = new ClusterRasterSoftwareKey(shader.SlangCode, entry);
        if (_tessellationPipelines.TryGetValue(key, out ClusterComputePipeline? pipeline))
            return pipeline;
        pipeline = ClusterComputePipeline.Create(_renderPipelines, shader, entry, $"Cluster material tessellation {phase} {entryPoint}");
        _tessellationPipelines.Add(key, pipeline);
        return pipeline;
    }

    private ClusterRasterPipeline ResolveHardwarePipeline(Material material, Shader shader, string vertexEntry, string pixelEntry, string vertexEncoding, bool targetOutput = false)
    {
        ClusterStageTargets.RequireVertexEncoding(shader, vertexEntry, vertexEncoding, $"Material '{material.Name}' hardware raster");
        SlangStage geometryStage = vertexEntry is ClusterMaterialEntries.HardwareMesh or ClusterMaterialEntries.HardwareMeshlet or ClusterMaterialEntries.TessellationHardwareMesh or "ClusterRasterMeshTarget" or "ClusterRasterMeshletTarget" or "ClusterTessellationHardwareMeshTarget" ? SlangStage.Mesh : SlangStage.Vertex;
        int targetIndex = shader.RequireSlangTarget(_device.Capabilities.CompileTarget);
        EntryPointReflection vertex = shader.RequireSlangEntry(targetIndex, vertexEntry, geometryStage);
        EntryPointReflection pixel = shader.RequireSlangEntry(targetIndex, pixelEntry, SlangStage.Fragment);
        var key = new ClusterRasterHardwareKey(shader.SlangCode, vertex, pixel, targetOutput);
        if (_hardwarePipelines.TryGetValue(key, out ClusterRasterPipeline? pipeline))
            return pipeline;
        pipeline = ClusterRasterPipeline.Create(_renderPipelines, shader, vertexEntry, pixelEntry, !targetOutput && _visibilityMode == ClusterVisibilityMode.HardwareOnly ? [Format.R32UInt] : [], depthStencilFormat: !targetOutput && _visibilityMode == ClusterVisibilityMode.HardwareOnly ? Format.D32Float : null, rasterizer: new RasterizerState(Cull: ClusterGeometryRequirements.UsesBothRasterFaces(pixel, targetOutput) ? CullType.None : CullType.Back), depthStencil: !targetOutput && _visibilityMode == ClusterVisibilityMode.HardwareOnly ? new DepthStencilState(DepthTest: true, DepthWrite: true, DepthComparison: SomeEngine.Graphics.CompareOperation.LessOrEqual) : default, name: $"Cluster material hardware raster {vertexEntry}/{pixelEntry}", vertexBuffers: (vertexEntry is ClusterMaterialEntries.TessellationHardwareVertex or "ClusterTessellationHardwareVertexTarget") ? [new VertexBufferLayout(0, sizeof(uint), PerInstance: true)] : [], vertexAttributes: (vertexEntry is ClusterMaterialEntries.TessellationHardwareVertex or "ClusterTessellationHardwareVertexTarget") ? [new VertexAttribute(0, 0, Format.R32UInt, 0)] : [], geometryStage: geometryStage);
        _hardwarePipelines.Add(key, pipeline);
        return pipeline;
    }

    private static ClusterConservativeBounds ResolveBounds(Material material, Shader shader, string softwareEntry, string vertexEntry)
    {
        ClusterConservativeBounds software = ClusterGeometryRequirements.RequireConservativeBounds(shader, softwareEntry, $"Material '{material.Name}' software raster");
        ClusterConservativeBounds hardware = ClusterGeometryRequirements.RequireConservativeBounds(shader, vertexEntry, $"Material '{material.Name}' hardware raster");
        return software == ClusterConservativeBounds.Finite && hardware == ClusterConservativeBounds.Finite ? ClusterConservativeBounds.Finite : ClusterConservativeBounds.Unbounded;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        foreach (ClusterComputePipeline pipeline in _softwarePipelines.Values)
        {
            try
            {
                pipeline.Dispose();
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }

        foreach (ClusterComputePipeline pipeline in _tessellationPipelines.Values)
        {
            try
            {
                pipeline.Dispose();
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }

        foreach (ClusterRasterPipeline pipeline in _hardwarePipelines.Values)
        {
            try
            {
                pipeline.Dispose();
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }

        _softwarePipelines.Clear();
        _tessellationPipelines.Clear();
        _hardwarePipelines.Clear();
        _disposed = true;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private ClusterDeformation? ResolveDeformation(Material material, string vertexEncoding, List<ClusterCommandBindingKey> bindings)
    {
        Shader shader = ClusterMaterialEntries.RequireShader(material);
        if (!ClusterMaterialEntries.HasEntry(shader, _device.Capabilities.CompileTarget, "ClusterDeformSize", SlangStage.Compute))
            return null;
        var stages = new ClusterDeformStage[ClusterDeformation.Entries.Length];
        for (int index = 0; index < stages.Length; index++)
        {
            string entry = ClusterDeformation.Entries[index];
            ClusterComputePipeline pipeline = ResolveSoftwarePipeline(material, shader, entry, vertexEncoding);
            ClusterRasterStageResources<ClusterDeformResource> resources = ResolveRasterResources(material, pipeline.ParameterLayout, shader, entry, null, ClusterDeformResourceNames.Resolve, ClusterDeformResourceNames.Contains);
            bindings.Add(ClusterCommandBindingKey.Create(material, resources.Resources, resources.MaterialBindings));
            stages[index] = new(pipeline, resources);
        }

        return new(stages);
    }
}

internal sealed class ClusterRasterStageSet
{
    private readonly ImmutableSortedDictionary<uint, ClusterRasterDispatch> _bins;
    private readonly int _unboundedBinCount;
    internal ClusterRasterStageSet(ClusterBytes binIndices, ImmutableSortedDictionary<uint, ClusterRasterDispatch> dispatches, uint binCount, ClusterBytes materialBounds, int[] changedBounds, uint[]? changedBins = null, int[]? changedOffsets = null, ClusterRasterStageSet? previous = null)
    {
        BinIndices = binIndices;
        _bins = dispatches;
        BinCount = binCount;
        MaterialBounds = materialBounds;
        ChangedBounds = changedBounds;
        ChangedBins = changedBins ?? [];
        ChangedOffsets = changedOffsets ?? [];
        _unboundedBinCount = previous?._unboundedBinCount ?? 0;
        if (previous is null)
            _unboundedBinCount = dispatches.Values.Count(static stage => stage.Bounds != ClusterConservativeBounds.Finite);
        else
            foreach (uint bin in ChangedBins)
            {
                if (previous._bins.TryGetValue(bin, out var before) && before.Bounds != ClusterConservativeBounds.Finite)
                    _unboundedBinCount--;
                if (_bins.TryGetValue(bin, out var after) && after.Bounds != ClusterConservativeBounds.Finite)
                    _unboundedBinCount++;
            }
    }

    internal ClusterBytes BinIndices { get; }
    internal ClusterBytes MaterialBounds { get; }
    internal IReadOnlyList<int> ChangedBounds { get; }
    internal IReadOnlyList<int> ChangedOffsets { get; }
    internal IEnumerable<ClusterRasterDispatch> Dispatches => _bins.Values;
    internal uint BinCount { get; }
    internal IReadOnlyList<uint> ChangedBins { get; }

    internal bool TryGetBin(uint bin, out ClusterRasterDispatch stage) => _bins.TryGetValue(bin, out stage!);
    internal ClusterRasterDispatch GetBin(uint bin) => _bins[bin];
    internal bool HasFiniteBounds => _unboundedBinCount == 0;
}

internal sealed record ClusterRasterDispatch(uint BinIndex, Material Material, string VertexEncoding, ClusterRasterCompatibilityKey Key, ClusterComputePipeline? Software, ClusterRasterPipeline Hardware, ClusterTessellationDispatch? Tessellation, ClusterConservativeBounds Bounds, ClusterRasterStageResources<ClusterSoftwareRasterCoreResource>? SoftwareResources, ClusterRasterStageResources<ClusterHardwareRasterCoreResource> HardwareResources, ClusterCommandBindingKey CommandBindings)
{
    internal bool SoftwareUsesMaterialRanges { get; init; }
    internal ClusterVisibilityDepth VisibilityDepth { get; init; }
    internal bool PixelVisibility => VisibilityDepth != ClusterVisibilityDepth.Opaque;
    internal bool UsesEarlyDepth => VisibilityDepth != ClusterVisibilityDepth.Offset;
    internal ClusterRasterTargetStages? TargetStages { get; init; }
    internal ClusterRasterTargetStages SpillStages { get; init; } = null!;
    internal ClusterRasterTargetStages? SpillTargetStages { get; init; }
    internal ClusterDeformation? Deformation { get; init; }
    internal bool ReadsMaterialScalars =>
        ReadsScalars(SoftwareResources) || ReadsScalars(HardwareResources) ||
        ReadsScalars(TargetStages?.SoftwareResources) || ReadsScalars(TargetStages?.HardwareResources) ||
        ReadsScalars(Tessellation?.SeedResources) || ReadsScalars(Tessellation?.SplitResources) ||
        ReadsScalars(Tessellation?.DiceResources) || ReadsScalars(Tessellation?.HardwareResources) ||
        ReadsScalars(SpillStages.SoftwareResources) || ReadsScalars(SpillStages.HardwareResources) ||
        ReadsScalars(SpillTargetStages?.SoftwareResources) || ReadsScalars(SpillTargetStages?.HardwareResources) ||
        ReadsScalars(Tessellation?.SpillSeedResources);

    private static bool ReadsScalars<TCore>(ClusterRasterStageResources<TCore>? stage)
        where TCore : struct, Enum => stage?.Resources.Contains("MaterialScalarRegion") == true;
    internal bool HasSameBindings(ClusterRasterDispatch other) =>
        ClusterDeformation.SameBindings(Deformation, other.Deformation) &&
        SameBindings(SoftwareResources, other.SoftwareResources) &&
        SameBindings(HardwareResources, other.HardwareResources) &&
        SameBindings(TargetStages?.SoftwareResources, other.TargetStages?.SoftwareResources) &&
        SameBindings(TargetStages?.HardwareResources, other.TargetStages?.HardwareResources) &&
        SameBindings(Tessellation?.SeedResources, other.Tessellation?.SeedResources) &&
        SameBindings(Tessellation?.SplitResources, other.Tessellation?.SplitResources) &&
        SameBindings(Tessellation?.DiceResources, other.Tessellation?.DiceResources) &&
        SameBindings(Tessellation?.HardwareResources, other.Tessellation?.HardwareResources) &&
        SameBindings(SpillStages.SoftwareResources, other.SpillStages.SoftwareResources) &&
        SameBindings(SpillStages.HardwareResources, other.SpillStages.HardwareResources) &&
        SameBindings(SpillTargetStages?.SoftwareResources, other.SpillTargetStages?.SoftwareResources) &&
        SameBindings(SpillTargetStages?.HardwareResources, other.SpillTargetStages?.HardwareResources) &&
        SameBindings(Tessellation?.SpillSeedResources, other.Tessellation?.SpillSeedResources);
    private static bool SameBindings<TCore>(ClusterRasterStageResources<TCore>? left, ClusterRasterStageResources<TCore>? right)
        where TCore : struct, Enum => ReferenceEquals(left, right) || left is not null && right is not null && left.MaterialBindings.AsSpan().SequenceEqual(right.MaterialBindings);
}

internal sealed record ClusterRasterTargetStages(ClusterComputePipeline? Software, ClusterRasterPipeline? Hardware, ClusterRasterStageResources<ClusterSoftwareRasterCoreResource>? SoftwareResources, ClusterRasterStageResources<ClusterHardwareRasterCoreResource>? HardwareResources);
internal sealed record ClusterTessellationDispatch(ClusterTessellationCompatibilityKey Key, ClusterComputePipeline Seed, ClusterComputePipeline Split, ClusterComputePipeline? Dice, ClusterRasterPipeline? Hardware, ClusterRasterStageResources<ClusterTessellationCoreResource> SeedResources, ClusterRasterStageResources<ClusterTessellationCoreResource> SplitResources, ClusterRasterStageResources<ClusterTessellationCoreResource>? DiceResources, ClusterRasterStageResources<ClusterTessellationCoreResource>? HardwareResources, ClusterCommandBindingKey SplitBindings)
{
    internal ClusterComputePipeline LayerSeed { get; init; } = null!;
    internal ClusterComputePipeline LayerSpillSeed { get; init; } = null!;
    internal ClusterRasterStageResources<ClusterTessellationCoreResource> LayerSeedResources { get; init; } = null!;
    internal ClusterRasterStageResources<ClusterTessellationCoreResource> LayerSpillSeedResources { get; init; } = null!;
    internal ClusterComputePipeline ShadowSplit { get; init; } = null!;
    internal ClusterComputePipeline LayerSplit { get; init; } = null!;
    internal ClusterComputePipeline? ShadowDice { get; init; }
    internal ClusterRasterPipeline? ShadowHardware { get; init; }
    internal ClusterRasterStageResources<ClusterTessellationCoreResource> ShadowSplitResources { get; init; } = null!;
    internal ClusterRasterStageResources<ClusterTessellationCoreResource> LayerSplitResources { get; init; } = null!;
    internal ClusterRasterStageResources<ClusterTessellationCoreResource>? ShadowDiceResources { get; init; }
    internal ClusterRasterStageResources<ClusterTessellationCoreResource>? ShadowHardwareResources { get; init; }
    internal ClusterComputePipeline SpillSeed { get; init; } = null!;
    internal ClusterRasterStageResources<ClusterTessellationCoreResource> SpillSeedResources { get; init; } = null!;
}
internal sealed class ClusterRasterStageResources<TCore>
    where TCore : struct, Enum
{
    internal ClusterRasterStageResources(ShaderParameterResources resources, TCore[] coreResources, ClusterMaterialStageBinding[] materialBindings, GraphResourceBinder? [] featureBinders)
    {
        Resources = resources;
        CoreResources = coreResources;
        MaterialBindings = materialBindings;
        FeatureBinders = featureBinders;
    }

    internal ShaderParameterResources Resources { get; }
    internal TCore[] CoreResources { get; }
    internal ClusterMaterialStageBinding[] MaterialBindings { get; }
    internal GraphResourceBinder? [] FeatureBinders { get; }
}

internal readonly record struct ClusterRasterSoftwareKey(SlangShaderData SlangCode, EntryPointReflection EntryPoint);
internal readonly record struct ClusterRasterHardwareKey(SlangShaderData SlangCode, EntryPointReflection Vertex, EntryPointReflection Pixel, bool TargetOutput);
/// <summary>
/// Resolves the final cooked shade entries and groups work by execution plus command binding
/// compatibility. Material identity and indexed scalar values never participate in the bin key.
/// </summary>
internal sealed class ClusterShadeStages : IDisposable
{
    private static readonly SomeEngine.Diagnostics.TraceSite ResolveTrace = new("render", "Cluster.ShadeStageResolve");
    private readonly RenderPipelines _renderPipelines;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly IGraphResourceProvider? _featureResources;
    private readonly Dictionary<ClusterShadeCompatibilityKey, ClusterComputePipeline> _pipelines = [];
    private readonly Dictionary<ClusterShadeCompatibilityKey, ShaderParameterResources> _linkedLayouts = [];
    private readonly Dictionary<ClusterShadeCompatibilityKey, ScalarLayout> _scalarLayouts = [];
    private readonly Dictionary<ClusterShadeCompatibilityKey, ClusterShadeCoreResource[]> _coreResourceLayouts = [];
    private ClusterBinIndexMap<ClusterShadeBinKey, ClusterShadeDispatch> _binIndices = new(static (before, after) => ReferenceEquals(before.Material, after.Material) && before.MaterialBindings.AsSpan().SequenceEqual(after.MaterialBindings));
    private ImmutableDictionary<long, HashSet<uint>> _resourceProperties = ImmutableDictionary<long, HashSet<uint>>.Empty;
    internal bool ResourcePropertiesMatch(Material material, ReadOnlySpan<ulong> revisions)
    {
        if (_resourceProperties.TryGetValue(material.MaterialId, out var properties))
            foreach (uint property in properties)
                if (material.GetPropertyRevision(property) != revisions[(int)property])
                    return false;
        return true;
    }

    private ClusterShadeStageSet _resolved = ClusterShadeStageSet.Empty;
    private bool _disposed;
    internal ClusterShadeStages(IGraphicsBackend backend, Device device, RenderPipelines pipelines, IGraphResourceProvider? featureResources)
    {
        _renderPipelines = pipelines;
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _featureResources = featureResources;
    }

    private ClusterMaterialAssignmentSet? _resolvedAssignments;
    internal ClusterShadeStageSet Resolve(ClusterMaterialAssignmentSet assignments, ClusterShadingPath shadingPath, uint debugMode)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(ResolveTrace);
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool shadingChanged = _resolved.ShadingPath != shadingPath || _resolved.DebugMode != debugMode;
        if (ReferenceEquals(assignments, _resolvedAssignments) && !shadingChanged)
            return _resolved;
        var addresses = _resolvedAssignments is null || shadingChanged ? new HashSet<int>(assignments.ActiveOffsets) : new HashSet<int>(assignments.ChangedOffsets);
        if (shadingChanged && _resolvedAssignments is not null)
            addresses.UnionWith(_resolvedAssignments.ActiveOffsets);
        foreach (long material in assignments.ChangedMaterialIds)
            addresses.UnionWith(assignments.GetMaterialOffsets(material));
        var bins = new ClusterBinIndexMap<ClusterShadeBinKey, ClusterShadeDispatch>(_binIndices);
        var resourceProperties = shadingChanged ? ImmutableDictionary<long, HashSet<uint>>.Empty : _resourceProperties;
        foreach (long material in assignments.ChangedMaterialIds)
            resourceProperties = resourceProperties.Remove(material);
        var scalarLayouts = _resolved.ScalarLayouts;
        foreach (int address in addresses)
        {
            if (!assignments.TryGetEntry(address, out Material material, out string encoding))
            {
                bins.Remove(address);
                scalarLayouts = scalarLayouts.Remove(address);
                continue;
            }

            bool forward = shadingPath == ClusterShadingPath.Forward;
            ClusterShadeDispatch dispatchData = CreateDispatch(material, encoding, forward ? ClusterMaterialEntries.ForwardShade : ClusterMaterialEntries.Shade, debugMode);
            HashSet<uint> properties = [];
            ClusterMaterialEntries.AddResourceProperties(properties, dispatchData.MaterialBindings);
            scalarLayouts = scalarLayouts.SetItem(address, dispatchData.Scalars);
            properties.UnionWith(scalarLayouts[address].TextureProperties);
            var key = new ClusterShadeBinKey(dispatchData.Key, material.MaterialId, encoding, dispatchData.Scalars.LayoutHash, dispatchData.Scalars.PayloadByteSize, dispatchData.CommandBindings);
            if (resourceProperties.TryGetValue(material.MaterialId, out var existing))
                properties.UnionWith(existing);
            resourceProperties = resourceProperties.SetItem(material.MaterialId, properties);
            bins.Set(address, key, dispatchData);
        }

        var dispatches = bins.GetStages(static (stage, index) => stage with { BinIndex = index });
        var result = new ClusterShadeStageSet(bins.GetIndices(assignments.MaterialAddressCapacity, _resolved.BinIndices, addresses), dispatches, bins.Range, scalarLayouts, [..bins.Changes], [..addresses], shadingPath, debugMode);
        _binIndices = bins;
        _resourceProperties = resourceProperties;
        _resolvedAssignments = assignments;
        _resolved = result;
        return result;
    }

    private ClusterShadeDispatch CreateDispatch(Material material, string vertexEncoding, string entryPoint, uint debugMode)
    {
        Shader shader = ClusterMaterialEntries.RequireShader(material);
        ClusterVisibilityDepth visibilityDepth = ClusterGeometryRequirements.RequireVisibilityDepth(shader);
        bool forward = entryPoint == ClusterMaterialEntries.ForwardShade;
        string mode = debugMode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string depth = ((uint)visibilityDepth).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string selectedEntry = entryPoint switch
        {
            ClusterMaterialEntries.Shade => ClusterMaterialEntries.ShadeMode,
            _ => entryPoint,
        };
        string[] arguments = selectedEntry switch
        {
            ClusterMaterialEntries.ShadeMode => [mode],
            ClusterMaterialEntries.ForwardShade => [depth, mode],
            _ => [depth, mode],
        };
        if (shader.TryGetSpecialization(selectedEntry, arguments, out Shader specialized))
        {
            shader = specialized;
            entryPoint = selectedEntry;
        }
        else if (forward || debugMode != 0u)
        {
            throw new InvalidDataException($"Material '{material.Name}' has no cooked {selectedEntry}<{string.Join(", ", arguments)}>. " + "Provide the matching material capability and saved specialization, or select normal Deferred shading.");
        }

        ClusterStageTargets.RequireVertexEncoding(shader, entryPoint, vertexEncoding, $"Material '{material.Name}' shade");
        int targetIndex = shader.RequireSlangTarget(_device.Capabilities.CompileTarget);
        EntryPointReflection entry = shader.RequireSlangEntry(targetIndex, entryPoint, SlangStage.Compute);
        var key = new ClusterShadeCompatibilityKey(shader.SlangCode, entry);
        if (!_pipelines.TryGetValue(key, out ClusterComputePipeline? pipeline))
        {
            pipeline = ClusterComputePipeline.Create(_renderPipelines, shader, entry, $"Cluster shade {entryPoint}");
            _pipelines.Add(key, pipeline);
        }

        if (!_linkedLayouts.TryGetValue(key, out ShaderParameterResources? resources))
        {
            resources = ShaderParameterResources.Create(shader, _device.Capabilities.CompileTarget, entryPoint);
            _linkedLayouts.Add(key, resources);
            _scalarLayouts.Add(key, resources.Contains("MaterialScalarRegion") ? ResolveScalarLayout(shader, targetIndex) : ScalarLayout.Empty);
            _coreResourceLayouts.Add(key, ResolveShadeCoreResources(resources));
        }

        ScalarLayout scalars = _scalarLayouts[key];
        ScalarLayout.ScalarMaterialLayout scalarMaterialLayout = scalars.Resolve(material);
        ClusterShadeCoreResource[] coreResources = _coreResourceLayouts[key];
        Dictionary<string, byte> materialResources = ResolveMaterialResources(material, resources);
        resources.ValidateMaterialResources(materialResources);
        GraphResourceBinder? [] featureResourceBinders = resources.ResolveFeatureResources(materialResources, _featureResources, name => ClusterShadeCoreResources.Contains(name) || ClusterDeformResourceNames.IsRead(name));
        ClusterMaterialStageBinding[] materialBindings = ResolveMaterialBindings(material, resources, materialResources, featureResourceBinders);
        ClusterCommandBindingKey commandBindings = ClusterCommandBindingKey.Create(material, resources, materialBindings);
        return new ClusterShadeDispatch(0, material, vertexEncoding, key, pipeline, resources, coreResources, scalarMaterialLayout, materialBindings, featureResourceBinders, commandBindings)
        {
            VisibilityDepth = visibilityDepth
        };
    }

    private static ClusterShadeCoreResource[] ResolveShadeCoreResources(ShaderParameterResources resources)
    {
        ReadOnlySpan<ShaderParameterResource> linked = resources.Resources;
        var result = new ClusterShadeCoreResource[linked.Length];
        for (int index = 0; index < linked.Length; index++)
        {
            ref readonly ShaderParameterResource resource = ref linked[index];
            if (resource.IsUsed)
                result[index] = ClusterShadeCoreResources.Resolve(resource.Name, resource.Type);
        }

        return result;
    }

    private static ClusterMaterialStageBinding[] ResolveMaterialBindings(Material material, ShaderParameterResources resources, IReadOnlyDictionary<string, byte> materialResources, GraphResourceBinder? [] featureResourceBinders)
    {
        ReadOnlySpan<ShaderParameterResource> linked = resources.Resources;
        var result = new ClusterMaterialStageBinding[linked.Length];
        IReadOnlyList<MaterialPropertyDefinition> propertyDefinitions = material.Properties;
        for (int resourceIndex = 0; resourceIndex < linked.Length; resourceIndex++)
        {
            if (featureResourceBinders[resourceIndex] is not null)
                continue;
            ref readonly ShaderParameterResource resource = ref linked[resourceIndex];
            if (!materialResources.ContainsKey(resource.Name))
                continue;
            uint? materialProperty = null;
            for (uint propertyIndex = 0; propertyIndex < propertyDefinitions.Count; propertyIndex++)
            {
                if (!string.Equals(propertyDefinitions[checked((int)propertyIndex)].Name, resource.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                materialProperty = propertyIndex;
                break;
            }

            if (materialProperty is null)
            {
                throw new InvalidDataException($"Material '{material.Name}' does not expose linked resource '{resource.Name}'.");
            }

            result[resourceIndex] = new ClusterMaterialStageBinding(materialProperty.Value, resource.Type, true);
        }

        return result;
    }

    private static ScalarLayout ResolveScalarLayout(Shader shader, int targetIndex)
    {
        TypeLayoutReflection layout = ResolveMaterialTypeLayout(shader, targetIndex, "MaterialScalars");
        return ScalarLayout.FromSlangByteAddress(layout.Type.Name, layout);
    }

    internal static TypeLayoutReflection ResolveMaterialTypeLayout(Shader shader, int targetIndex, string attributeName)
    {
        var materialTypeNames = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (DeclReflection declaration in shader.GetSlangDeclarations())
            Visit(declaration);
        if (materialTypeNames.Count == 0)
            throw new InvalidDataException($"Shader '{shader.Name}' exposes no {attributeName} type after IR restoration.");
        if (materialTypeNames.Count != 1)
        {
            throw new InvalidDataException($"Shader '{shader.Name}' exposes multiple {attributeName} types.");
        }

        string typeName = materialTypeNames[0];
        ShaderReflection reflection = shader.GetSlangReflection(targetIndex);
        TypeReflection type = reflection.FindTypeByName(typeName) ?? throw new InvalidDataException($"Shader '{shader.Name}' has no linked {attributeName} type '{typeName}'.");
        // Surface storage uses the target buffer layout. Material scalar encoding
        // derives its raw-buffer contract separately from the reflected field types.
        TypeLayoutReflection layout = reflection.GetTypeLayout(type, LayoutRules.DefaultStructuredBuffer) ?? throw new InvalidDataException($"Shader '{shader.Name}' has no layout for {attributeName} type '{type.Name}'.");
        return layout;
        void Visit(DeclReflection declaration)
        {
            if (declaration == DeclReflection.Null)
                return;
            if (declaration.Kind == DeclReflectionKind.Struct)
            {
                TypeReflection candidate = declaration.Type;
                if (candidate != TypeReflection.Null && HasAttribute(candidate, attributeName) && !string.IsNullOrWhiteSpace(candidate.Name) && seen.Add(candidate.Name))
                {
                    materialTypeNames.Add(candidate.Name);
                }
            }

            for (int index = 0; index < declaration.ChildrenCount; index++)
                Visit(declaration.GetChild(checked((uint)index)));
        }

        static bool HasAttribute(TypeReflection type, string name)
        {
            for (uint index = 0; index < type.AttributeCount; index++)
            {
                AttributeReflection attribute = type.GetAttribute(index);
                if (attribute != AttributeReflection.Null && string.Equals(attribute.Name, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private static Dictionary<string, byte> ResolveMaterialResources(Material material, ShaderParameterResources resources)
    {
        var result = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (ref readonly ShaderParameterResource resource in resources.Resources)
        {
            string resourceName = resource.Name;
            if (!resource.IsUsed || resource.Type == ResourceBindingType.None || !material.Properties.Any(property => string.Equals(property.Name, resourceName, StringComparison.Ordinal)))
            {
                continue;
            }

            result.TryAdd(resource.Name, resource.Type switch
            {
                ResourceBindingType.TextureSrv or ResourceBindingType.TextureUav => (byte)0,
                ResourceBindingType.Sampler => 1,
                ResourceBindingType.BufferSrv or ResourceBindingType.BufferUav or ResourceBindingType.ConstantBuffer => 2,
                ResourceBindingType.AccelerationStructure => 3,
                _ => throw new InvalidDataException($"Resource type {resource.Type} cannot be a material resource."),
            });
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
            try
            {
                pipeline.Dispose();
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }

        _pipelines.Clear();
        _disposed = true;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }
}

internal sealed class ClusterShadeStageSet
{
    internal static ClusterShadeStageSet Empty { get; } = new(ClusterBytes.Zero(0), ImmutableSortedDictionary<uint, ClusterShadeDispatch>.Empty, 0u, ImmutableDictionary<int, ScalarLayout.ScalarMaterialLayout>.Empty);

    private readonly ImmutableSortedDictionary<uint, ClusterShadeDispatch> _bins;
    internal ImmutableDictionary<int, ScalarLayout.ScalarMaterialLayout> ScalarLayouts { get; }

    internal ClusterShadeStageSet(ClusterBytes binIndices, ImmutableSortedDictionary<uint, ClusterShadeDispatch> dispatches, uint binCount, ImmutableDictionary<int, ScalarLayout.ScalarMaterialLayout> scalarLayouts, uint[]? changedBins = null, int[]? changedOffsets = null, ClusterShadingPath shadingPath = ClusterShadingPath.Deferred, uint debugMode = 0)
    {
        ShadingPath = shadingPath;
        DebugMode = debugMode;
        BinIndices = binIndices;
        _bins = dispatches;
        BinCount = binCount;
        ScalarLayouts = scalarLayouts;
        ChangedBins = changedBins ?? [];
        ChangedOffsets = changedOffsets ?? [];
    }

    internal ClusterBytes BinIndices { get; }
    internal IReadOnlyList<int> ChangedOffsets { get; }
    internal IEnumerable<ClusterShadeDispatch> Dispatches => _bins.Values;
    internal ClusterShadingPath ShadingPath { get; }
    internal uint DebugMode { get; }
    internal uint BinCount { get; }

    internal ClusterShadeDispatch GetBin(uint bin) => _bins[bin];
    internal IReadOnlyList<uint> ChangedBins { get; }

    internal bool TryGetBin(uint bin, out ClusterShadeDispatch stage) => _bins.TryGetValue(bin, out stage!);
}

internal sealed record ClusterShadeDispatch(uint BinIndex, Material Material, string VertexEncoding, ClusterShadeCompatibilityKey Key, ClusterComputePipeline Pipeline, ShaderParameterResources Resources, ClusterShadeCoreResource[] CoreResources, ScalarLayout.ScalarMaterialLayout Scalars, ClusterMaterialStageBinding[] MaterialBindings, GraphResourceBinder? [] FeatureResourceBinders, ClusterCommandBindingKey CommandBindings)
{
    internal ClusterVisibilityDepth VisibilityDepth { get; init; }
}

internal readonly record struct ClusterMaterialStageBinding(uint MaterialProperty, ResourceBindingType Type, bool IsMaterialResource);
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
    DrawMetadata,
    WinnerImage,
    WinnerBuffer,
}

internal enum ClusterTessellationCoreResource : byte
{
    None,
    LayerWindows,
    LayerParameters,
    InstanceData,
    InstanceProperties,
    Uniforms,
    Views,
    PolicyViews,
    PageHeap,
    VisibleClusters,
    BinnedClusters,
    BinMetadata,
    RasterBinIndices,
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
    WorkStatus,
}

internal enum ClusterShadeCoreResource : byte
{
    None,
    BinIndices,
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
            ClusterShadeCoreResource.BinIndices or ClusterShadeCoreResource.InstanceData or ClusterShadeCoreResource.Views or ClusterShadeCoreResource.PixelCoordinates or ClusterShadeCoreResource.BinOffsets or ClusterShadeCoreResource.BinCounts or ClusterShadeCoreResource.VisibleClusters or ClusterShadeCoreResource.PageHeap or ClusterShadeCoreResource.MaterialScalars or ClusterShadeCoreResource.MaterialScalarOffsets => ResourceBindingType.BufferSrv,
            ClusterShadeCoreResource.InstanceProperties or ClusterShadeCoreResource.Uniforms => ResourceBindingType.ConstantBuffer,
            ClusterShadeCoreResource.Visibility or ClusterShadeCoreResource.Depth => ResourceBindingType.TextureSrv,
            ClusterShadeCoreResource.OutputColor or ClusterShadeCoreResource.OutputMotionVectors => ResourceBindingType.TextureUav,
            _ => throw new InvalidOperationException($"Unknown Cluster shade core resource {result}."),
        };
        if (type != expected)
        {
            throw new InvalidDataException($"Cluster shade core resource '{name}' must be {expected}, not {type}.");
        }

        return result;
    }

    private static ClusterShadeCoreResource ResolveName(string name) => name switch
    {
        "ShadeSlots" => ClusterShadeCoreResource.BinIndices,
        "InstanceData" or "MaterialInstanceData" => ClusterShadeCoreResource.InstanceData,
        "InstanceProperties" or "MaterialInstanceProperties" => ClusterShadeCoreResource.InstanceProperties,
        "Uniforms" => ClusterShadeCoreResource.Uniforms,
        "Views" or "MaterialViews" => ClusterShadeCoreResource.Views,
        "PixelCoordBuffer" => ClusterShadeCoreResource.PixelCoordinates,
        "BinOffsets" => ClusterShadeCoreResource.BinOffsets,
        "BinCounts" => ClusterShadeCoreResource.BinCounts,
        "VisBuffer" => ClusterShadeCoreResource.Visibility,
        "DepthBuffer" or "MaterialDepth" => ClusterShadeCoreResource.Depth,
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
            ClusterSoftwareRasterCoreResource.InstanceData or ClusterSoftwareRasterCoreResource.Views or ClusterSoftwareRasterCoreResource.PolicyViews or ClusterSoftwareRasterCoreResource.PageHeap or ClusterSoftwareRasterCoreResource.VisibleClusters or ClusterSoftwareRasterCoreResource.BinnedClusters or ClusterSoftwareRasterCoreResource.BinMetadata => ResourceBindingType.BufferSrv,
            ClusterSoftwareRasterCoreResource.InstanceProperties or ClusterSoftwareRasterCoreResource.Uniforms => ResourceBindingType.ConstantBuffer,
            ClusterSoftwareRasterCoreResource.Depth => ResourceBindingType.TextureSrv,
            ClusterSoftwareRasterCoreResource.Visibility or ClusterSoftwareRasterCoreResource.WinnerImage => ResourceBindingType.TextureUav,
            ClusterSoftwareRasterCoreResource.WinnerBuffer => ResourceBindingType.BufferUav,
            ClusterSoftwareRasterCoreResource.Diagnostics => ResourceBindingType.BufferUav,
            _ => throw new InvalidOperationException($"Unknown Cluster software-raster core resource {result}."),
        };
        if (type != expected)
        {
            throw new InvalidDataException($"Cluster software-raster core resource '{name}' must be {expected}, not {type}.");
        }

        return result;
    }

    private static ClusterSoftwareRasterCoreResource ResolveName(string name) => name switch
    {
        "InstanceData" or "RasterOutputInstanceData" => ClusterSoftwareRasterCoreResource.InstanceData,
        "InstanceProperties" or "RasterOutputInstanceProperties" => ClusterSoftwareRasterCoreResource.InstanceProperties,
        "PushConstants" or "Uniforms" or "ClusterSoftwareRasterPushConstants" or "ClusterSoftwareRasterUniformData" => ClusterSoftwareRasterCoreResource.Uniforms,
        "Views" or "SoftwareRasterViews" => ClusterSoftwareRasterCoreResource.Views,
        "PolicyClusterViews" or "SoftwareRasterPolicyViews" => ClusterSoftwareRasterCoreResource.PolicyViews,
        "PageHeap" => ClusterSoftwareRasterCoreResource.PageHeap,
        "VisibleClusters" or "SoftwareRasterVisibleClusters" or "RasterOutputVisibleClusters" => ClusterSoftwareRasterCoreResource.VisibleClusters,
        "BinnedClusterIndexBuffer" or "SoftwareRasterBinnedClusterIndexBuffer" => ClusterSoftwareRasterCoreResource.BinnedClusters,
        "RasterBinMeta" or "SoftwareRasterBinMeta" => ClusterSoftwareRasterCoreResource.BinMetadata,
        "DepthTarget" or "SoftwareVisibilityDepthTarget" => ClusterSoftwareRasterCoreResource.Depth,
        "VisBuffer" or "SoftwareVisibilityBuffer" => ClusterSoftwareRasterCoreResource.Visibility,
        "DepthUAV" or "VisibilityWinnerImage" => ClusterSoftwareRasterCoreResource.WinnerImage,
        "VisibilityWinnerBuffer" or "VisibilityNonAtomicBuffer" => ClusterSoftwareRasterCoreResource.WinnerBuffer,
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
            ClusterHardwareRasterCoreResource.InstanceData or ClusterHardwareRasterCoreResource.Views or ClusterHardwareRasterCoreResource.PolicyViews or ClusterHardwareRasterCoreResource.PageHeap or ClusterHardwareRasterCoreResource.BinnedClusters or ClusterHardwareRasterCoreResource.VisibleClusters or ClusterHardwareRasterCoreResource.DrawMetadata => ResourceBindingType.BufferSrv,
            ClusterHardwareRasterCoreResource.InstanceProperties or ClusterHardwareRasterCoreResource.Uniforms or ClusterHardwareRasterCoreResource.DispatchUniforms => ResourceBindingType.ConstantBuffer,
            ClusterHardwareRasterCoreResource.WinnerImage => ResourceBindingType.TextureUav,
            ClusterHardwareRasterCoreResource.WinnerBuffer => ResourceBindingType.BufferUav,
            _ => throw new InvalidOperationException($"Unknown Cluster hardware-raster core resource {result}."),
        };
        if (type != expected)
        {
            throw new InvalidDataException($"Cluster hardware-raster core resource '{name}' must be {expected}, not {type}.");
        }

        return result;
    }

    private static ClusterHardwareRasterCoreResource ResolveName(string name) => name switch
    {
        "InstanceData" or "RasterOutputInstanceData" => ClusterHardwareRasterCoreResource.InstanceData,
        "InstanceProperties" or "RasterOutputInstanceProperties" => ClusterHardwareRasterCoreResource.InstanceProperties,
        "Uniforms" or "HardwareRasterUniformData" => ClusterHardwareRasterCoreResource.Uniforms,
        "Views" or "HardwareRasterViews" => ClusterHardwareRasterCoreResource.Views,
        "PolicyClusterViews" or "HardwareRasterPolicyViews" => ClusterHardwareRasterCoreResource.PolicyViews,
        "DispatchUniforms" or "HardwareRasterDispatchUniformData" => ClusterHardwareRasterCoreResource.DispatchUniforms,
        "PageHeap" => ClusterHardwareRasterCoreResource.PageHeap,
        "BinnedClusterIndexBuffer" or "HardwareRasterBinnedClusterIndexBuffer" => ClusterHardwareRasterCoreResource.BinnedClusters,
        "VisibleClusters" or "HardwareRasterVisibleClusters" or "RasterOutputVisibleClusters" => ClusterHardwareRasterCoreResource.VisibleClusters,
        "VisibleClusterMeta" or "HardwareRasterVisibleClusterMeta" => ClusterHardwareRasterCoreResource.DrawMetadata,
        "VisibilityWinnerImage" => ClusterHardwareRasterCoreResource.WinnerImage,
        "VisibilityWinnerBuffer" or "VisibilityNonAtomicBuffer" => ClusterHardwareRasterCoreResource.WinnerBuffer,
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
            ClusterTessellationCoreResource.LayerWindows or ClusterTessellationCoreResource.InstanceData or ClusterTessellationCoreResource.Views or ClusterTessellationCoreResource.PolicyViews or ClusterTessellationCoreResource.PageHeap or ClusterTessellationCoreResource.VisibleClusters or ClusterTessellationCoreResource.BinMetadata or ClusterTessellationCoreResource.RasterBinIndices or ClusterTessellationCoreResource.GroupedPatchIndices or ClusterTessellationCoreResource.RasterPatches or ClusterTessellationCoreResource.BinCounts or ClusterTessellationCoreResource.BinOffsets => ResourceBindingType.BufferSrv,
            ClusterTessellationCoreResource.HiZ or ClusterTessellationCoreResource.Depth => ResourceBindingType.TextureSrv,
            ClusterTessellationCoreResource.LayerParameters or ClusterTessellationCoreResource.InstanceProperties or ClusterTessellationCoreResource.Uniforms => ResourceBindingType.ConstantBuffer,
            ClusterTessellationCoreResource.Visibility or ClusterTessellationCoreResource.WinnerImage => ResourceBindingType.TextureUav,
            ClusterTessellationCoreResource.WinnerBuffer => ResourceBindingType.BufferUav,
            ClusterTessellationCoreResource.BinnedClusters or ClusterTessellationCoreResource.Counters or ClusterTessellationCoreResource.WorkStatus or ClusterTessellationCoreResource.WorkPatches0 or ClusterTessellationCoreResource.WorkPatches1 or ClusterTessellationCoreResource.LevelCounts or ClusterTessellationCoreResource.NextLevelArgs or ClusterTessellationCoreResource.FinalPatches => ResourceBindingType.BufferUav,
            _ => throw new InvalidOperationException($"Unknown Cluster tessellation core resource {result}."),
        };
        if (type != expected)
        {
            throw new InvalidDataException($"Cluster tessellation core resource '{name}' must be {expected}, not {type}.");
        }

        return result;
    }

    private static ClusterTessellationCoreResource ResolveName(string name) => name switch
    {
        "LayerClusterTileWindows" => ClusterTessellationCoreResource.LayerWindows,
        "LayerCullParameters" => ClusterTessellationCoreResource.LayerParameters,
        "InstanceData" or "RasterOutputInstanceData" => ClusterTessellationCoreResource.InstanceData,
        "InstanceProperties" or "RasterOutputInstanceProperties" => ClusterTessellationCoreResource.InstanceProperties,
        "Uniforms" or "TessellationUniformData" => ClusterTessellationCoreResource.Uniforms,
        "Views" or "TessellationViews" => ClusterTessellationCoreResource.Views,
        "PolicyClusterViews" or "TessellationPolicyViews" => ClusterTessellationCoreResource.PolicyViews,
        "PageHeap" => ClusterTessellationCoreResource.PageHeap,
        "VisibleClusters" or "TessellationVisibleClusters" or "RasterOutputVisibleClusters" => ClusterTessellationCoreResource.VisibleClusters,
        "BinnedClusterIndexBuffer" or "TessellationBinnedClusterIndexBuffer" => ClusterTessellationCoreResource.BinnedClusters,
        "RasterBinMeta" or "TessellationRasterBinMeta" => ClusterTessellationCoreResource.BinMetadata,
        "TessellationRasterSlots" => ClusterTessellationCoreResource.RasterBinIndices,
        "HiZTexture" or "TessellationHiZTexture" => ClusterTessellationCoreResource.HiZ,
        "DepthTarget" or "SoftwareVisibilityDepthTarget" => ClusterTessellationCoreResource.Depth,
        "VisBuffer" or "SoftwareVisibilityBuffer" => ClusterTessellationCoreResource.Visibility,
        "DepthUAV" or "VisibilityWinnerImage" => ClusterTessellationCoreResource.WinnerImage,
        "VisibilityWinnerBuffer" or "VisibilityNonAtomicBuffer" => ClusterTessellationCoreResource.WinnerBuffer,
        "TessellationCounters" => ClusterTessellationCoreResource.Counters,
        "WorkStatus" => ClusterTessellationCoreResource.WorkStatus,
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
    internal static void RequireVertexEncoding(Shader shader, string entryPoint, string expected, string subject)
    {
        string actual = ClusterGeometryRequirements.RequireVertexEncoding(shader, entryPoint, subject);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{subject} entry '{entryPoint}' accepts vertex encoding '{actual}', but the " + $"Cluster pipeline requires '{expected}'.");
        }
    }
}
