using System.Buffers.Binary;
using System.Collections.Immutable;
using SomeEngine.RenderGraph;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Assets;
using SlangShaderSharp;

namespace SomeEngine.Render.Cluster.Pipeline;

internal static class ClusterMaterialEntries
{
    internal const string Shade = "ClusterShade";
    internal const string ShadeMode = "ClusterShadeMode";
    internal const string ShadowRequests = "ClusterShadowRequests";
    internal const string ForwardShade = "ClusterForwardShade";
    internal const string SoftwareTarget = "ClusterRasterSoftwareTarget";
    internal const string SoftwareMeshletTarget = "ClusterRasterSoftwareMeshletTarget";
    internal const string HardwarePixelTarget = "ClusterRasterPixelTarget";
    internal const string HardwareMeshPixelTarget = "ClusterRasterMeshPixelTarget";
    internal const string HardwareVertex = "ClusterRasterVertex";
    internal const string HardwareMesh = "ClusterRasterMesh";
    internal const string HardwareMeshlet = "ClusterRasterMeshlet";
    internal const string TessellationSeed = "ClusterTessellationSeed";
    internal const string TessellationSplit = "ClusterTessellationSplit";
    internal const string TessellationHardwareMesh = "ClusterTessellationHardwareMesh";
    internal const string TessellationHardwareVertex = "ClusterTessellationHardwareVertex";
    internal const string TessellationHardwareMeshPixel = "ClusterTessellationHardwareMeshPixel";
    internal const string TessellationHardwarePixel = "ClusterTessellationHardwarePixel";

    internal static string SoftwareRaster(ClusterVisibilityMode mode) => mode switch
    {
        ClusterVisibilityMode.ImageAtomic64 => "ClusterRasterSoftwareImageAtomic64",
        ClusterVisibilityMode.BufferAtomic64 => "ClusterRasterSoftwareBufferAtomic64",
        ClusterVisibilityMode.HardwareOnly => throw new InvalidOperationException(
            "Hardware-only visibility has no software raster entry."),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    internal static string HardwarePixel(ClusterVisibilityMode mode, bool mesh) => (mode, mesh) switch
    {
        (ClusterVisibilityMode.ImageAtomic64, false) => "ClusterRasterPixelImageAtomic64",
        (ClusterVisibilityMode.BufferAtomic64, false) => "ClusterRasterPixelBufferAtomic64",
        (ClusterVisibilityMode.HardwareOnly, false) => "ClusterRasterPixelHardwareOnly",
        (ClusterVisibilityMode.ImageAtomic64, true) => "ClusterRasterMeshPixelImageAtomic64",
        (ClusterVisibilityMode.BufferAtomic64, true) => "ClusterRasterMeshPixelBufferAtomic64",
        (ClusterVisibilityMode.HardwareOnly, true) => "ClusterRasterMeshPixelHardwareOnly",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    internal static string TessellationDice(ClusterVisibilityMode mode) => mode switch
    {
        ClusterVisibilityMode.ImageAtomic64 => "ClusterTessellationDiceImageAtomic64",
        ClusterVisibilityMode.BufferAtomic64 => "ClusterTessellationDiceBufferAtomic64",
        ClusterVisibilityMode.HardwareOnly => throw new InvalidOperationException(
            "Hardware-only tessellation is consumed by fixed-function rasterization."),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    internal static void AddResourceProperties(
        HashSet<uint> properties,
        ReadOnlySpan<uint?> materialProperties)
    {
        foreach (uint? property in materialProperties)
            if (property is uint index) properties.Add(index);
    }

    internal static Shader RequireShader(Material material)
        => material.Shader ?? throw new InvalidDataException(
            $"Material '{material.Name}' has no loaded user shader.");

    internal static bool HasEntry(
        Shader shader,
        SlangCompileTarget target,
        string entryPoint,
        SlangStage stage)
    {
        return shader.TryGetSlangTarget(target, out int targetIndex) &&
            shader.TryGetSlangEntry(targetIndex, entryPoint, stage, out _);
    }
}

internal readonly record struct ClusterComputeKey(
    SlangShaderData SlangCode,
    EntryPointReflection EntryPoint);

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
    internal TStage GetStage(int address) => _groups[_keys[address]].Members[address];
    internal ImmutableSortedDictionary<uint, TStage> GetStages(Func<TStage, uint, TStage> withIndex)
    {
        foreach (uint index in _updated)
            if (_stages.TryGetValue(index, out TStage? stage))
                _stages = _stages.SetItem(index, withIndex(stage, index));
        _updated = _updated.Clear();
        return _stages;
    }
    internal ClusterBytes GetIndices(
        uint capacity,
        ClusterBytes previous,
        IEnumerable<int> addresses,
        Func<TStage, uint, uint>? encodeIndex = null)
    {
        var changes = new List<(int Offset, uint Value)>();
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
            changes.Add((checked(address * sizeof(uint)), index));
        }
        return previous.WithUInt32Changes(checked((int)capacity * sizeof(uint)), changes);
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
        Material material,
        ShaderParameterLayout resources,
        uint?[] bindings)
    {
        var values = new List<BindingValue>();
        Append(material, resources, bindings, values);
        return Create(values);
    }

    internal static void Append(
        Material material,
        ShaderParameterLayout resources,
        uint?[] bindings,
        List<BindingValue> values)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(bindings);
        ReadOnlySpan<ShaderParameterResource> linked = resources.Resources;
        if (linked.Length != bindings.Length)
            throw new InvalidDataException("Cluster material binding metadata does not match the linked layout.");
        for (int index = 0; index < bindings.Length; index++)
        {
            if (bindings[index] is not uint property || !linked[index].IsUsed)
                continue;
            values.Add(new BindingValue(
                linked[index].Type,
                Normalize(material, property, linked[index].Type, linked[index].Name)));
        }
    }

    internal static ClusterCommandBindingKey Create(List<BindingValue> values)
        => new([.. values]);

    internal void AppendTo(List<BindingValue> values)
        => values.AddRange(_values);

    internal void CollectBuffers(HashSet<SomeEngine.Graphics.Buffer> buffers)
    {
        foreach (BindingValue binding in _values)
            switch (binding.Value)
            {
                case BufferCbvDesc view: buffers.Add(view.Buffer); break;
                case BufferSrvDesc view: buffers.Add(view.Buffer); break;
                case BufferUavDesc view:
                    buffers.Add(view.Buffer);
                    if (view.CounterBuffer is { } counter) buffers.Add(counter);
                    break;
                case AccelerationStructure acceleration: buffers.Add(acceleration.Info.Storage); break;
            }
    }

    internal void CollectTextures(HashSet<SomeEngine.Resources.Texture> textures)
    {
        foreach (BindingValue binding in _values)
            if (binding.Value is SomeEngine.Resources.Texture texture) textures.Add(texture);
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
        uint property,
        ResourceBindingType type,
        string name)
    {
        if (type is ResourceBindingType.TextureSrv or ResourceBindingType.TextureUav)
        {
            SomeEngine.Resources.Texture? texture = material.GetTexture(property);
            if (texture is not null)
                return texture;
            if (type == ResourceBindingType.TextureUav)
                throw new InvalidDataException($"Material '{material.Name}' slot '{name}' is not a Texture.");
            return null;
        }
        if (type == ResourceBindingType.Sampler)
            return material.GetSampler(property) with { Label = null };

        GraphicsObject value = material.GetDeviceResourceBinding(property, type).Value!;
        return type switch
        {
            ResourceBindingType.BufferSrv => ((BufferSrv)value).Description with { Label = null },
            ResourceBindingType.BufferUav => ((BufferUav)value).Description with { Label = null },
            ResourceBindingType.ConstantBuffer => ((BufferCbv)value).Description with { Label = null },
            ResourceBindingType.AccelerationStructure => ((AccelerationStructureSrv)value).Resource,
            _ => throw new InvalidDataException(
                $"Cluster material resource '{name}' has unsupported binding type {type}."),
        };
    }

    internal readonly record struct BindingValue(ResourceBindingType Type, object? Value);

}

internal readonly record struct ClusterRasterBinKey(
    LinkedComputePipeline? Software,
    LinkedRasterPipeline Hardware,
    LinkedComputePipeline? TessellationSeed,
    LinkedComputePipeline? TessellationSplit,
    LinkedComputePipeline? TessellationDice,
    LinkedRasterPipeline? TessellationHardware,
    LinkedComputePipeline? TargetSoftware,
    LinkedRasterPipeline? TargetHardware,
    LinkedComputePipeline? Deformation,
    string VertexEncoding,
    ClusterCommandBindingKey Bindings,
    SurfaceMode Surface,
    bool Ordered);

internal readonly record struct ClusterShadeBinKey(
    LinkedComputePipeline Pipeline,
    string VertexEncoding,
    uint ScalarLayoutHash,
    uint ScalarPayloadBytes,
    ClusterCommandBindingKey Bindings,
    LinkedComputePipeline? Receiver,
    ClusterCommandBindingKey? ReceiverBindings,
    SurfaceMode Surface);

/// <summary>Owns raster-only compatibility grouping and its material-slot mapping.</summary>
internal sealed partial class ClusterRasterStages : IDisposable
{
    private static readonly SomeEngine.Diagnostics.TraceSite ResolveTrace =
        new("render", "Cluster.RasterStageResolve");
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly string _defaultVertexEncoding;
    private readonly ClusterVisibilityMode _visibilityMode;
    private readonly LinkedComputePipeline? _defaultSoftware;
    private readonly LinkedRasterPipeline _defaultHardware;
    private readonly IGraphResourceProvider? _featureResources;
    private readonly Dictionary<ClusterComputeKey, LinkedComputePipeline> _computePipelines = [];
    private readonly Dictionary<ClusterRasterHardwareKey, LinkedRasterPipeline> _hardwarePipelines = [];
    private ClusterBinIndexMap<ClusterRasterBinKey, ClusterRasterInvocation> _binIndices =
        new(static (before, after) => ReferenceEquals(before.Material, after.Material) &&
            before.HasSameBindings(after) && before.Bounds == after.Bounds);
    private ImmutableDictionary<long, HashSet<uint>> _resourceProperties = ImmutableDictionary<long, HashSet<uint>>.Empty;
    private ImmutableDictionary<long, HashSet<uint>?> _depthProperties = ImmutableDictionary<long, HashSet<uint>?>.Empty;

    internal void CollectResourceProperties(long material, HashSet<uint> destination)
    {
        if (_resourceProperties.TryGetValue(material, out var properties)) destination.UnionWith(properties);
    }

    internal bool TryGetMaterialDepthInputs(long materialId, out HashSet<uint> properties)
    {
        if (_depthProperties.TryGetValue(materialId, out var found) && found is not null)
        {
            properties = found;
            return true;
        }
        properties = null!;
        return false;
    }

    private ClusterRasterStageSet? _resolved;

    internal void AcceptChanges() => _binIndices.AcceptChanges();
    private bool _disposed;

    internal ClusterRasterStages(
        IGraphicsBackend backend,
        Device device,
        ClusterVisibilityMode visibilityMode,
        string vertexEncoding,
        LinkedComputePipeline? defaultSoftware,
        LinkedRasterPipeline defaultHardware,
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
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(ResolveTrace);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(assignments, _resolvedAssignments)) return _resolved!;
        var addresses = _resolvedAssignments is null
            ? new HashSet<int>(assignments.ActiveOffsets)
            : new HashSet<int>(assignments.ChangedOffsets);
        foreach (long material in assignments.ChangedMaterialIds)
            addresses.UnionWith(assignments.GetMaterialOffsets(material));
        var bins = new ClusterBinIndexMap<ClusterRasterBinKey, ClusterRasterInvocation>(_binIndices);
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
            ClusterRasterInvocation invocation = ResolveInvocation(material, encoding, out HashSet<uint>? depthInputs);
            HashSet<uint> properties = [];
            if (invocation.SoftwareResources is { } software)
                ClusterMaterialEntries.AddResourceProperties(properties, software.MaterialProperties);
            ClusterMaterialEntries.AddResourceProperties(properties, invocation.HardwareResources.MaterialProperties);
            if (invocation.Deformation is { } deformation)
                foreach (ClusterDeformStage stage in deformation.Stages)
                    ClusterMaterialEntries.AddResourceProperties(properties, stage.Resources.MaterialProperties);
            if (invocation.TargetStages?.SoftwareResources is { } targetSoftware)
                ClusterMaterialEntries.AddResourceProperties(properties, targetSoftware.MaterialProperties);
            if (invocation.TargetStages?.HardwareResources is { } targetHardware)
                ClusterMaterialEntries.AddResourceProperties(properties, targetHardware.MaterialProperties);
            if (invocation.Tessellation is { } tessellation)
            {
                ClusterMaterialEntries.AddResourceProperties(properties, tessellation.SeedResources.MaterialProperties);
                ClusterMaterialEntries.AddResourceProperties(properties, tessellation.SplitResources.MaterialProperties);
                if (tessellation.DiceResources is { } dice)
                    ClusterMaterialEntries.AddResourceProperties(properties, dice.MaterialProperties);
                if (tessellation.HardwareResources is { } hardware)
                    ClusterMaterialEntries.AddResourceProperties(properties, hardware.MaterialProperties);
            }
            var key = new ClusterRasterBinKey(
                invocation.Software, invocation.Hardware,
                invocation.Tessellation?.Seed, invocation.Tessellation?.Split,
                invocation.Tessellation?.Dice, invocation.Tessellation?.Hardware,
                invocation.TargetStages?.Software, invocation.TargetStages?.Hardware,
                invocation.Deformation?.Stages[0].Pipeline, encoding, invocation.CommandBindings,
                material.State.Surface, material.State.Ordered);
            if (resourceProperties.TryGetValue(material.MaterialId, out var existing)) properties.UnionWith(existing);
            resourceProperties = resourceProperties.SetItem(material.MaterialId, properties);
            depthProperties = depthProperties.SetItem(material.MaterialId, depthInputs);
            bins.Set(address, key, invocation);
        }
        var invocations = bins.GetStages(static (stage, index) => stage with { BinIndex = index });
        var boundsChanges = new List<(int Offset, byte[] Bytes)>();
        foreach ((int offset, int count) in assignments.MaterialRanges)
        {
            bool changed = false;
            bool finite = true;
            for (int index = 0; index < count; index++)
            {
                changed |= addresses.Contains(offset + index);
                finite &= bins.GetStage(offset + index).Bounds == ClusterConservativeBounds.Finite;
            }
            if (!changed) continue;
            byte[] bytes = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, finite ? 1u : 0u);
            boundsChanges.Add((checked(offset * sizeof(uint)), bytes));
        }
        ClusterBytes materialBounds = (_resolved?.MaterialBounds ?? ClusterBytes.Zero(0)).WithChanges(
            checked((int)assignments.SlotCapacity * sizeof(uint)), boundsChanges);
        var result = new ClusterRasterStageSet(bins.GetIndices(
            assignments.SlotCapacity,
            _resolved?.BinIndices ?? ClusterBytes.Zero(0),
            addresses,
            static (stage, index) => index | (stage.Software is null ? 0u : 0x80000000u) |
                (ClusterRasterPolicy.UsesMaterialRanges(stage.Hardware) && stage.Tessellation is null ? 0x40000000u : 0u) |
                (stage.SoftwareUsesMaterialRanges && stage.Tessellation is null ? 0x20000000u : 0u) |
                (stage.PixelVisibility ? 0x10000000u : 0u) |
                (stage.Material.State.Surface == SurfaceMode.Translucent || stage.Material.State.Ordered ? 0x08000000u : 0u)),
            invocations, bins.Range, materialBounds,
            [.. boundsChanges.Select(change => change.Offset / sizeof(uint))],
            [.. bins.Changes], [.. addresses], _resolved);
        _binIndices = bins;
        _resourceProperties = resourceProperties;
        _depthProperties = depthProperties;
        _resolvedAssignments = assignments;
        _resolved = result;
        return result;
    }

    private ClusterRasterInvocation ResolveInvocation(Material material, string vertexEncoding,
        out HashSet<uint>? materialDepthInputs)
    {
        Shader shader = ClusterMaterialEntries.RequireShader(material);
        SlangCompileTarget target = _device.Capabilities.CompileTarget;
        LinkedComputePipeline? software;
        LinkedRasterPipeline hardware;
        ClusterConservativeBounds bounds = ClusterConservativeBounds.Finite;

        string? softwareEntry = _visibilityMode == ClusterVisibilityMode.HardwareOnly
            ? null
            : ClusterMaterialEntries.SoftwareRaster(_visibilityMode);
        string meshletSoftwareEntry = _visibilityMode == ClusterVisibilityMode.ImageAtomic64
            ? "ClusterRasterSoftwareMeshletImageAtomic64"
            : "ClusterRasterSoftwareMeshletBufferAtomic64";
        bool softwareUsesMaterialRanges = softwareEntry is not null &&
            ClusterMaterialEntries.HasEntry(shader, target, meshletSoftwareEntry, SlangStage.Compute);
        if (softwareUsesMaterialRanges)
            softwareEntry = meshletSoftwareEntry;
        string hardwarePixelEntry = ClusterMaterialEntries.HardwarePixel(_visibilityMode, ClusterRasterPolicy.SupportsMesh(_backend, _device));
        string? tessellationDiceEntry = _visibilityMode == ClusterVisibilityMode.HardwareOnly
            ? null
            : ClusterMaterialEntries.TessellationDice(_visibilityMode);
        bool hasSoftware = softwareEntry is not null && ClusterMaterialEntries.HasEntry(
            shader, target, softwareEntry, SlangStage.Compute);
        bool usesMesh = ClusterRasterPolicy.SupportsMesh(_backend, _device);
        bool hasMesh = ClusterMaterialEntries.HasEntry(shader, target, ClusterMaterialEntries.HardwareMesh, SlangStage.Mesh);
        bool hasMeshlet = ClusterMaterialEntries.HasEntry(shader, target, ClusterMaterialEntries.HardwareMeshlet, SlangStage.Mesh);
        string hardwareEntry = usesMesh ? ClusterMaterialEntries.HardwareMesh : ClusterMaterialEntries.HardwareVertex;
        if (usesMesh && hasMeshlet && ClusterRasterPolicy.SupportsMeshlets(_backend, _device))
            hardwareEntry = ClusterMaterialEntries.HardwareMeshlet;
        bool selectedMeshAvailable = hardwareEntry == ClusterMaterialEntries.HardwareMeshlet ? hasMeshlet : hasMesh;
        bool hasVertex = ClusterMaterialEntries.HasEntry(
            shader, target, ClusterMaterialEntries.HardwareVertex, SlangStage.Vertex);
        bool hasPixel = ClusterMaterialEntries.HasEntry(
            shader, target, hardwarePixelEntry, SlangStage.Fragment);
        bool hasVisibilityRaster = hasSoftware || hasVertex || hasMesh || hasMeshlet || hasPixel;
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
                !hasVertex || (usesMesh && !selectedMeshAvailable) ||
                !hasPixel)
            {
                throw new InvalidDataException(
                    $"Material '{material.Name}' must provide all Cluster visibility raster entries together.");
            }
            software = softwareEntry is null
                ? null
                : ResolveComputePipeline(
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
            shader, target, ClusterMaterialEntries.TessellationSeed, SlangStage.Compute);
        bool hasSplit = ClusterMaterialEntries.HasEntry(
            shader, target, ClusterMaterialEntries.TessellationSplit, SlangStage.Compute);
        bool hasDice = tessellationDiceEntry is not null && ClusterMaterialEntries.HasEntry(
            shader, target, tessellationDiceEntry, SlangStage.Compute);
        bool hasTessellationHardwareVertex = ClusterMaterialEntries.HasEntry(
            shader, target, ClusterMaterialEntries.TessellationHardwareVertex, SlangStage.Vertex);
        bool hasTessellationHardwarePixel = ClusterMaterialEntries.HasEntry(
            shader, target, ClusterMaterialEntries.TessellationHardwarePixel, SlangStage.Fragment);
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
                    SlangStage.Compute))
            {
                throw new InvalidDataException(
                    $"Material '{material.Name}' provides runtime tessellation, so its same shader must also " +
                    "provide ClusterShade. Forward domain evaluation and depth reconstruction cannot come " +
                    "from different shader compositions.");
            }
        }

        Shader? visibilityShader = hasVisibilityRaster ? shader : null;
        Shader? tessellationShader = hasTessellationOverride ? shader : null;
        ClusterRasterStageResources<ClusterSoftwareRasterCoreResource>? softwareResources = software is null
            ? null
            : ResolveRasterResources(
                material,
                visibilityShader is null ? software.GlobalResources : null,
                visibilityShader,
                visibilityShader is null ? null : softwareEntry,
                null,
                ClusterSoftwareRasterCoreResources.Resolve);
        ClusterRasterStageResources<ClusterHardwareRasterCoreResource> hardwareResources = ResolveRasterResources(
            material,
            visibilityShader is null ? hardware.GlobalResources : null,
            visibilityShader,
            visibilityShader is null ? null : hardware.GeometryEntry,
            visibilityShader is null ? null : ClusterMaterialEntries.HardwarePixel(_visibilityMode, hardware.UsesMesh),
            ClusterHardwareRasterCoreResources.Resolve);
        var bindingValues = new List<ClusterCommandBindingKey.BindingValue>();
        if (softwareResources is not null)
        {
            ClusterCommandBindingKey.Append(
                material,
                softwareResources.Resources,
                softwareResources.MaterialProperties,
                bindingValues);
        }
        ClusterCommandBindingKey.Append(
            material,
            hardwareResources.Resources,
            hardwareResources.MaterialProperties,
            bindingValues);
        ClusterRasterTargetStages? targetStages = visibilityShader is null
            ? null
            : CreateTargetStages(material, visibilityShader, vertexEncoding, hardware, softwareUsesMaterialRanges);
        if (targetStages?.Software is not null &&
            ClusterGeometryRequirements.RequireConservativeBounds(
                visibilityShader!, softwareUsesMaterialRanges
                    ? ClusterMaterialEntries.SoftwareMeshletTarget : ClusterMaterialEntries.SoftwareTarget,
                $"Material '{material.Name}' target software raster") != ClusterConservativeBounds.Finite)
            bounds = ClusterConservativeBounds.Unbounded;
        if (targetStages?.SoftwareResources is { } targetSoftware)
            ClusterCommandBindingKey.Append(material, targetSoftware.Resources, targetSoftware.MaterialProperties, bindingValues);
        if (targetStages?.HardwareResources is { } targetHardware)
            ClusterCommandBindingKey.Append(material, targetHardware.Resources, targetHardware.MaterialProperties, bindingValues);
        ClusterTessellationInvocation? tessellation = null;
        if (tessellationShader is not null)
        {
            LinkedComputePipeline seed = ResolveComputePipeline(
                material,
                tessellationShader,
                ClusterMaterialEntries.TessellationSeed,
                vertexEncoding);
            LinkedComputePipeline split = ResolveComputePipeline(
                material,
                tessellationShader,
                ClusterMaterialEntries.TessellationSplit,
                vertexEncoding);
            LinkedComputePipeline? dice = _visibilityMode == ClusterVisibilityMode.HardwareOnly
                ? null
                : ResolveComputePipeline(
                    material,
                    tessellationShader,
                    ClusterMaterialEntries.TessellationDice(_visibilityMode),
                    vertexEncoding);
            LinkedRasterPipeline? tessellationHardware =
                _visibilityMode == ClusterVisibilityMode.HardwareOnly
                    ? ResolveHardwarePipeline(
                        material,
                        tessellationShader,
                        ClusterRasterPolicy.SupportsMesh(_backend, _device)
                            ? ClusterMaterialEntries.TessellationHardwareMesh : ClusterMaterialEntries.TessellationHardwareVertex,
                        ClusterRasterPolicy.SupportsMesh(_backend, _device)
                            ? ClusterMaterialEntries.TessellationHardwareMeshPixel : ClusterMaterialEntries.TessellationHardwarePixel,
                        vertexEncoding)
                    : null;
            ClusterRasterStageResources<ClusterTessellationCoreResource> seedResources =
                ResolveRasterResources(
                    material,
                    null,
                    tessellationShader,
                    ClusterMaterialEntries.TessellationSeed,
                    null,
                    ClusterTessellationCoreResources.Resolve);
            ClusterRasterStageResources<ClusterTessellationCoreResource> splitResources =
                ResolveRasterResources(
                    material,
                    null,
                    tessellationShader,
                    ClusterMaterialEntries.TessellationSplit,
                    null,
                    ClusterTessellationCoreResources.Resolve);
            ClusterRasterStageResources<ClusterTessellationCoreResource>? diceResources = dice is null
                ? null
                : ResolveRasterResources(
                    material,
                    null,
                    tessellationShader,
                    ClusterMaterialEntries.TessellationDice(_visibilityMode),
                    null,
                    ClusterTessellationCoreResources.Resolve);
            ClusterRasterStageResources<ClusterTessellationCoreResource>? tessellationHardwareResources =
                tessellationHardware is null
                    ? null
                    : ResolveRasterResources(
                        material,
                        null,
                        tessellationShader,
                        ClusterRasterPolicy.SupportsMesh(_backend, _device)
                            ? ClusterMaterialEntries.TessellationHardwareMesh : ClusterMaterialEntries.TessellationHardwareVertex,
                        ClusterRasterPolicy.SupportsMesh(_backend, _device)
                            ? ClusterMaterialEntries.TessellationHardwareMeshPixel : ClusterMaterialEntries.TessellationHardwarePixel,
                    ClusterTessellationCoreResources.Resolve);
            ClusterCommandBindingKey splitBindings = ClusterCommandBindingKey.Create(
                material, splitResources.Resources, splitResources.MaterialProperties);
            ClusterCommandBindingKey.Append(
                material,
                seedResources.Resources,
                seedResources.MaterialProperties,
                bindingValues);
            splitBindings.AppendTo(bindingValues);
            if (diceResources is not null)
            {
                ClusterCommandBindingKey.Append(
                    material,
                    diceResources.Resources,
                    diceResources.MaterialProperties,
                    bindingValues);
            }
            if (tessellationHardwareResources is not null)
            {
                ClusterCommandBindingKey.Append(
                    material,
                    tessellationHardwareResources.Resources,
                    tessellationHardwareResources.MaterialProperties,
                    bindingValues);
            }
            tessellation = new ClusterTessellationInvocation(
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
                ClusterGeometryRequirements.RequireConservativeBounds(
                    tessellationShader,
                    ClusterMaterialEntries.TessellationSplit,
                    $"Material '{material.Name}' tessellation split");
            if (tessellationBounds != ClusterConservativeBounds.Finite)
                bounds = ClusterConservativeBounds.Unbounded;
        }
        ClusterDeformation? deformation = ResolveDeformation(material, vertexEncoding, bindingValues);
        ClusterCommandBindingKey commandBindings = ClusterCommandBindingKey.Create(bindingValues);
        materialDepthInputs = visibilityShader is not null && tessellation is null
            ? ClusterGeometryRequirements.ReadMaterialDepthInputs(material, visibilityShader,
                visibilityShader.RequireSlangTarget(_device.Capabilities.CompileTarget),
                software is null ? null : softwareEntry,
                hardware.GeometryEntry, ClusterMaterialEntries.HardwarePixel(_visibilityMode, hardware.UsesMesh),
                targetStages?.Software is null ? null : softwareUsesMaterialRanges
                    ? ClusterMaterialEntries.SoftwareMeshletTarget : ClusterMaterialEntries.SoftwareTarget,
                targetStages?.Hardware?.GeometryEntry,
                targetStages?.Hardware is null ? null : targetStages.Hardware.UsesMesh
                    ? ClusterMaterialEntries.HardwareMeshPixelTarget : ClusterMaterialEntries.HardwarePixelTarget)
            : null;
        return new ClusterRasterInvocation(
            0,
            material,
            software,
            hardware,
            tessellation,
            bounds,
            softwareResources,
            hardwareResources,
            commandBindings)
        {
            SoftwareUsesMaterialRanges = softwareUsesMaterialRanges,
            TargetStages = targetStages,
            Deformation = deformation,
            VisibilityDepth = visibilityShader is null ? ClusterVisibilityDepth.Opaque :
                ClusterGeometryRequirements.RequireVisibilityDepth(visibilityShader),
        };
    }

    private ClusterRasterTargetStages? CreateTargetStages(
        Material material,
        Shader shader,
        string vertexEncoding,
        LinkedRasterPipeline primaryHardware,
        bool softwareUsesMaterialRanges)
    {
        string softwareEntry = softwareUsesMaterialRanges
            ? ClusterMaterialEntries.SoftwareMeshletTarget
            : ClusterMaterialEntries.SoftwareTarget;
        string pixelEntry = primaryHardware.UsesMesh
            ? ClusterMaterialEntries.HardwareMeshPixelTarget
            : ClusterMaterialEntries.HardwarePixelTarget;
        SlangCompileTarget target = _device.Capabilities.CompileTarget;
        LinkedComputePipeline? software = _visibilityMode != ClusterVisibilityMode.HardwareOnly &&
            ClusterMaterialEntries.HasEntry(shader, target, softwareEntry, SlangStage.Compute)
                ? ResolveComputePipeline(material, shader, softwareEntry, vertexEncoding)
                : null;
        LinkedRasterPipeline? hardware = ClusterMaterialEntries.HasEntry(shader, target, pixelEntry, SlangStage.Fragment)
            ? ResolveHardwarePipeline(material, shader, primaryHardware.GeometryEntry,
                pixelEntry, vertexEncoding)
            : null;
        if (software is null && hardware is null)
            return null;
        var softwareResources = software is null ? null : ResolveRasterResources(
            material, null, shader, softwareEntry, null,
            ClusterSoftwareRasterCoreResources.Resolve);
        var hardwareResources = hardware is null ? null : ResolveRasterResources(
            material, null, shader, hardware.GeometryEntry, pixelEntry,
            ClusterHardwareRasterCoreResources.Resolve);
        return new ClusterRasterTargetStages(software, hardware, softwareResources, hardwareResources);
    }

    private ClusterRasterStageResources<TCore> ResolveRasterResources<TCore>(
        Material material,
        ShaderParameterLayout? defaultResources,
        Shader? shader,
        string? firstEntry,
        string? secondEntry,
        Func<string, ResourceBindingType, TCore> resolveCore)
        where TCore : struct, Enum
    {
        ShaderParameterLayout resources = shader is null
            ? defaultResources!
            : shader.GetSlangResourceLayout(
                _device.Capabilities.CompileTarget,
                firstEntry ?? throw new InvalidDataException("A custom raster stage has no entry name."),
                secondEntry);
        uint?[] materialProperties = shader is null
            ? new uint?[resources.Resources.Length]
            : material.ResolveResourceProperties(resources.Resources);
        var coreKinds = new TCore[resources.Resources.Length];
        for (int index = 0; index < coreKinds.Length; index++)
        {
            ref readonly ShaderParameterResource resource = ref resources.Resources[index];
            if (resource.IsUsed)
                coreKinds[index] = resolveCore(resource.Name, resource.Type);
        }
        GraphResourceBinder?[] featureBinders = ClusterStageResourceBindings.ResolveFeatureResources(
            resources.Resources,
            materialProperties,
            _featureResources,
            coreKinds,
            allowNeutralRasterTarget: true);
        return new ClusterRasterStageResources<TCore>(
            resources,
            coreKinds,
            materialProperties,
            featureBinders);
    }

    private LinkedComputePipeline ResolveComputePipeline(
        Material material,
        Shader shader,
        string entryPoint,
        string vertexEncoding)
    {
        ClusterStageTargets.RequireVertexEncoding(
            shader,
            entryPoint,
            vertexEncoding,
            $"Material '{material.Name}' compute raster");
        int targetIndex = shader.RequireSlangTarget(_device.Capabilities.CompileTarget);
        EntryPointReflection entry = shader.RequireSlangEntry(
            targetIndex,
            entryPoint,
            SlangStage.Compute);
        var key = new ClusterComputeKey(shader.SlangCode, entry);
        if (_computePipelines.TryGetValue(key, out LinkedComputePipeline? pipeline))
            return pipeline;
        pipeline = LinkedComputePipeline.Create(
            _backend,
            _device,
            shader,
            entry,
            $"Cluster material compute raster {entryPoint}");
        _computePipelines.Add(key, pipeline);
        return pipeline;
    }

    private LinkedRasterPipeline ResolveHardwarePipeline(
        Material material,
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
        SlangStage geometryStage = vertexEntry is ClusterMaterialEntries.HardwareMesh or ClusterMaterialEntries.HardwareMeshlet or ClusterMaterialEntries.TessellationHardwareMesh
            ? SlangStage.Mesh : SlangStage.Vertex;
        int targetIndex = shader.RequireSlangTarget(_device.Capabilities.CompileTarget);
        EntryPointReflection vertex = shader.RequireSlangEntry(
            targetIndex,
            vertexEntry,
            geometryStage);
        EntryPointReflection pixel = shader.RequireSlangEntry(
            targetIndex,
            pixelEntry,
            SlangStage.Fragment);
        var key = new ClusterRasterHardwareKey(shader.SlangCode, vertex, pixel);
        if (_hardwarePipelines.TryGetValue(key, out LinkedRasterPipeline? pipeline))
            return pipeline;
        bool targetOutput = pixelEntry is ClusterMaterialEntries.HardwarePixelTarget or
            ClusterMaterialEntries.HardwareMeshPixelTarget;
        pipeline = LinkedRasterPipeline.Create(
            _backend,
            _device,
            shader,
            geometryStage,
            vertexEntry,
            pixelEntry,
            !targetOutput && _visibilityMode == ClusterVisibilityMode.HardwareOnly
                ? [Format.R32UInt]
                : [],
            depthStencilFormat: !targetOutput && _visibilityMode == ClusterVisibilityMode.HardwareOnly
                ? Format.D32Float
                : null,
            rasterizer: new RasterizerState(Cull: ClusterGeometryRequirements.UsesBothRasterFaces(pixel, targetOutput)
                ? CullType.None : CullType.Back),
            depthStencil: !targetOutput && _visibilityMode == ClusterVisibilityMode.HardwareOnly
                ? new DepthStencilState(
                    DepthTest: true,
                    DepthWrite: true,
                    DepthComparison: SomeEngine.Graphics.CompareOperation.LessOrEqual)
                : default,
            label: $"Cluster material hardware raster {vertexEntry}/{pixelEntry}",
            vertexBuffers: vertexEntry == ClusterMaterialEntries.TessellationHardwareVertex
                ? [new VertexBufferLayout(0, sizeof(uint), PerInstance: true)] : [],
            vertexAttributes: vertexEntry == ClusterMaterialEntries.TessellationHardwareVertex
                ? [new VertexAttribute(0, 0, Format.R32UInt, 0)] : []);
        _hardwarePipelines.Add(key, pipeline);
        return pipeline;
    }

    private static ClusterConservativeBounds ResolveBounds(
        Material material,
        Shader shader,
        string softwareEntry,
        string vertexEntry)
    {
        ClusterConservativeBounds software = ClusterGeometryRequirements.RequireConservativeBounds(
            shader,
            softwareEntry,
            $"Material '{material.Name}' software raster");
        ClusterConservativeBounds hardware = ClusterGeometryRequirements.RequireConservativeBounds(
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
        foreach (LinkedComputePipeline pipeline in _computePipelines.Values)
        {
            try { pipeline.Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        foreach (LinkedRasterPipeline pipeline in _hardwarePipelines.Values)
        {
            try { pipeline.Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        _computePipelines.Clear();
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
        ClusterBytes binIndices,
        ImmutableSortedDictionary<uint, ClusterRasterInvocation> invocations,
        uint binCount,
        ClusterBytes materialBounds,
        int[] changedBounds,
        uint[]? changedBins = null,
        int[]? changedOffsets = null,
        ClusterRasterStageSet? previous = null)
    {
        BinIndices = binIndices;
        _bins = invocations;
        BinCount = binCount;
        MaterialBounds = materialBounds;
        ChangedBounds = changedBounds;
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

    internal ClusterBytes BinIndices { get; }
    internal ClusterBytes MaterialBounds { get; }
    internal IReadOnlyList<int> ChangedBounds { get; }
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
    Material Material,
    LinkedComputePipeline? Software,
    LinkedRasterPipeline Hardware,
    ClusterTessellationInvocation? Tessellation,
    ClusterConservativeBounds Bounds,
    ClusterRasterStageResources<ClusterSoftwareRasterCoreResource>? SoftwareResources,
    ClusterRasterStageResources<ClusterHardwareRasterCoreResource> HardwareResources,
    ClusterCommandBindingKey CommandBindings)
{
    internal bool SoftwareUsesMaterialRanges { get; init; }
    internal ClusterVisibilityDepth VisibilityDepth { get; init; }
    internal bool PixelVisibility => VisibilityDepth != ClusterVisibilityDepth.Opaque;
    internal bool UsesEarlyDepth => VisibilityDepth != ClusterVisibilityDepth.Offset;
    internal ClusterRasterTargetStages? TargetStages { get; init; }
    internal ClusterDeformation? Deformation { get; init; }

    internal bool ReadsMaterialScalars =>
        ReadsScalars(SoftwareResources) || ReadsScalars(HardwareResources) ||
        ReadsScalars(TargetStages?.SoftwareResources) || ReadsScalars(TargetStages?.HardwareResources) ||
        ReadsScalars(Tessellation?.SeedResources) || ReadsScalars(Tessellation?.SplitResources) ||
        ReadsScalars(Tessellation?.DiceResources) || ReadsScalars(Tessellation?.HardwareResources) ||
        Deformation?.Stages.Any(static stage => ReadsScalars(stage.Resources)) == true;

    private static bool ReadsScalars<TCore>(ClusterRasterStageResources<TCore>? stage) where TCore : struct, Enum =>
        stage?.Resources.Contains("MaterialScalarRegion") == true;

    internal bool HasSameBindings(ClusterRasterInvocation other) =>
        ClusterDeformation.SameBindings(Deformation, other.Deformation) &&
        SameBindings(SoftwareResources, other.SoftwareResources) &&
        SameBindings(HardwareResources, other.HardwareResources) &&
        SameBindings(TargetStages?.SoftwareResources, other.TargetStages?.SoftwareResources) &&
        SameBindings(TargetStages?.HardwareResources, other.TargetStages?.HardwareResources) &&
        SameBindings(Tessellation?.SeedResources, other.Tessellation?.SeedResources) &&
        SameBindings(Tessellation?.SplitResources, other.Tessellation?.SplitResources) &&
        SameBindings(Tessellation?.DiceResources, other.Tessellation?.DiceResources) &&
        SameBindings(Tessellation?.HardwareResources, other.Tessellation?.HardwareResources);

    private static bool SameBindings<TCore>(ClusterRasterStageResources<TCore>? left,
        ClusterRasterStageResources<TCore>? right) where TCore : struct, Enum =>
        ReferenceEquals(left, right) || left is not null && right is not null &&
        left.MaterialProperties.AsSpan().SequenceEqual(right.MaterialProperties);
}

internal sealed record ClusterRasterTargetStages(
    LinkedComputePipeline? Software,
    LinkedRasterPipeline? Hardware,
    ClusterRasterStageResources<ClusterSoftwareRasterCoreResource>? SoftwareResources,
    ClusterRasterStageResources<ClusterHardwareRasterCoreResource>? HardwareResources);

internal sealed record ClusterTessellationInvocation(
    LinkedComputePipeline Seed,
    LinkedComputePipeline Split,
    LinkedComputePipeline? Dice,
    LinkedRasterPipeline? Hardware,
    ClusterRasterStageResources<ClusterTessellationCoreResource> SeedResources,
    ClusterRasterStageResources<ClusterTessellationCoreResource> SplitResources,
    ClusterRasterStageResources<ClusterTessellationCoreResource>? DiceResources,
    ClusterRasterStageResources<ClusterTessellationCoreResource>? HardwareResources,
    ClusterCommandBindingKey SplitBindings);

internal sealed class ClusterRasterStageResources<TCore>
    where TCore : struct, Enum
{
    internal ClusterRasterStageResources(
        ShaderParameterLayout resources,
        TCore[] coreResources,
        uint?[] materialProperties,
        GraphResourceBinder?[] featureBinders)
    {
        Resources = resources;
        CoreResources = coreResources;
        MaterialProperties = materialProperties;
        FeatureBinders = featureBinders;
    }

    internal ShaderParameterLayout Resources { get; }
    internal TCore[] CoreResources { get; }
    internal uint?[] MaterialProperties { get; }
    internal GraphResourceBinder?[] FeatureBinders { get; }
}

internal readonly record struct ClusterRasterHardwareKey(
    SlangShaderData SlangCode,
    EntryPointReflection Vertex,
    EntryPointReflection Pixel);

/// <summary>
/// Resolves the final cooked shade entries and groups work by execution plus command binding
/// compatibility. Material identity and indexed scalar values never participate in the bin key.
/// </summary>
internal sealed class ClusterShadeStages : IDisposable
{
    private static readonly SomeEngine.Diagnostics.TraceSite ResolveTrace =
        new("render", "Cluster.ShadeStageResolve");
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly IGraphResourceProvider? _featureResources;
    private readonly Dictionary<ClusterComputeKey, LinkedComputePipeline> _pipelines = [];
    private readonly Dictionary<ShaderParameterLayout,
        (ScalarLayout Scalars, ClusterShadeCoreResource[] CoreResources)> _stageLayouts =
        new(ReferenceEqualityComparer.Instance);
    private ClusterBinIndexMap<ClusterShadeBinKey, ClusterShadeInvocation> _binIndices =
        new(static (before, after) => ReferenceEquals(before.Material, after.Material) &&
            before.MaterialProperties.AsSpan().SequenceEqual(after.MaterialProperties));
    private ImmutableDictionary<long, HashSet<uint>> _resourceProperties = ImmutableDictionary<long, HashSet<uint>>.Empty;

    internal void CollectResourceProperties(long material, HashSet<uint> destination)
    {
        if (_resourceProperties.TryGetValue(material, out var properties)) destination.UnionWith(properties);
    }

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

    internal ClusterShadeStageSet Resolve(ClusterMaterialAssignmentSet assignments,
        ClusterShadingPath shadingPath, uint debugMode, bool requestShadowPages)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(ResolveTrace);
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool shadingChanged = _resolved.ShadingPath != shadingPath || _resolved.DebugMode != debugMode ||
            _resolved.HasShadowRequests != requestShadowPages;
        if (ReferenceEquals(assignments, _resolvedAssignments) && !shadingChanged) return _resolved;
        var addresses = _resolvedAssignments is null || shadingChanged
            ? new HashSet<int>(assignments.ActiveOffsets)
            : new HashSet<int>(assignments.ChangedOffsets);
        if (shadingChanged && _resolvedAssignments is not null)
            addresses.UnionWith(_resolvedAssignments.ActiveOffsets);
        foreach (long material in assignments.ChangedMaterialIds)
            addresses.UnionWith(assignments.GetMaterialOffsets(material));
        var bins = new ClusterBinIndexMap<ClusterShadeBinKey, ClusterShadeInvocation>(_binIndices);
        var resourceProperties = shadingChanged
            ? ImmutableDictionary<long, HashSet<uint>>.Empty : _resourceProperties;
        foreach (long material in assignments.ChangedMaterialIds)
            resourceProperties = resourceProperties.Remove(material);
        var scalarLayouts = _resolved.ScalarLayouts;
        ClusterShadeInvocation? materialStage = null;
        foreach (int address in addresses)
        {
            if (!assignments.TryGetEntry(address, out Material material, out string encoding))
            {
                bins.Remove(address);
                scalarLayouts = scalarLayouts.Remove(address);
                continue;
            }
            bool forward = shadingPath == ClusterShadingPath.Forward;
            ClusterShadeInvocation invocation = CreateInvocation(material, encoding,
                forward ? ClusterMaterialEntries.ForwardShade : ClusterMaterialEntries.Shade, debugMode, materialStage);
            invocation = invocation with
            {
                RequestShader = requestShadowPages && material.Shader!.HasSpecializations(ClusterMaterialEntries.ShadowRequests)
                    ? CreateInvocation(material, encoding,
                    ClusterMaterialEntries.ShadowRequests, debugMode, materialStage?.RequestShader) : null,
            };
            HashSet<uint> properties = [];
            ClusterMaterialEntries.AddResourceProperties(properties, invocation.MaterialProperties);
            if (invocation.RequestShader is { } receiver) ClusterMaterialEntries.AddResourceProperties(properties, receiver.MaterialProperties);
            ScalarLayout.ScalarMaterialLayout scalarLayout = invocation.Scalars.PayloadByteSize != 0u
                ? invocation.Scalars : invocation.RequestShader?.Scalars ?? invocation.Scalars;
            scalarLayouts = scalarLayouts.SetItem(address, scalarLayout);
            properties.UnionWith(scalarLayout.TextureProperties);
            var key = new ClusterShadeBinKey(invocation.Pipeline, encoding,
                invocation.Scalars.LayoutHash, invocation.Scalars.PayloadByteSize, invocation.CommandBindings,
                invocation.RequestShader?.Pipeline, invocation.RequestShader?.CommandBindings, material.State.Surface);
            if (resourceProperties.TryGetValue(material.MaterialId, out var existing)) properties.UnionWith(existing);
            resourceProperties = resourceProperties.SetItem(material.MaterialId, properties);
            bins.Set(address, key, invocation);
            materialStage = invocation;
        }
        var invocations = bins.GetStages(static (stage, index) => stage with
            { BinIndex = index, RequestShader = stage.RequestShader is { } receiver ? receiver with { BinIndex = index } : null });
        var result = new ClusterShadeStageSet(bins.GetIndices(
            assignments.SlotCapacity, _resolved.BinIndices, addresses),
            invocations, bins.Range, scalarLayouts, [.. bins.Changes], [.. addresses], shadingPath, debugMode, requestShadowPages);
        _binIndices = bins;
        _resourceProperties = resourceProperties;
        _resolvedAssignments = assignments;
        _resolved = result;
        return result;
    }

    private ClusterShadeInvocation CreateInvocation(Material material, string vertexEncoding, string entryPoint,
        uint debugMode, ClusterShadeInvocation? materialStage)
    {
        Shader shader = ClusterMaterialEntries.RequireShader(material);
        ClusterVisibilityDepth visibilityDepth = ClusterGeometryRequirements.RequireVisibilityDepth(shader);
        bool forward = entryPoint is ClusterMaterialEntries.ForwardShade or ClusterMaterialEntries.ShadowRequests;
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
            throw new InvalidDataException(
                $"Material '{material.Name}' has no cooked {selectedEntry}<{string.Join(", ", arguments)}>. " +
                "Provide the matching material capability and saved specialization, or select normal Deferred shading.");
        }
        ClusterStageTargets.RequireVertexEncoding(
            shader,
            entryPoint,
            vertexEncoding,
            $"Material '{material.Name}' shade");
        int targetIndex = shader.RequireSlangTarget(_device.Capabilities.CompileTarget);
        EntryPointReflection entry = shader.RequireSlangEntry(
            targetIndex,
            entryPoint,
            SlangStage.Compute);
        var key = new ClusterComputeKey(shader.SlangCode, entry);
        if (!_pipelines.TryGetValue(key, out LinkedComputePipeline? pipeline))
        {
            pipeline = LinkedComputePipeline.Create(
                _backend,
                _device,
                shader,
                entry,
                $"Cluster shade {entryPoint}");
            _pipelines.Add(key, pipeline);
        }

        ShaderParameterLayout resources = shader.GetSlangResourceLayout(
            _device.Capabilities.CompileTarget, entryPoint);
        if (!_stageLayouts.TryGetValue(resources, out var stageLayout))
        {
            if (forward && (resources.Contains("VirtualShadowProjectedDirectionalVisibility") ||
                resources.Contains("VirtualShadowProjectedLocalVisibility") ||
                resources.Contains("VirtualShadowAdditionalProjectedDirectionalVisibility") ||
                resources.Contains("VirtualShadowAdditionalProjectedLocalVisibility")))
                throw new NotSupportedException(
                    $"Material '{material.Name}' forward entry consumes projected shadow results. " +
                    "Use inline shadow sampling or select Deferred shading.");
            ScalarLayout scalarLayout = resources.Contains("MaterialScalarRegion")
                ? ResolveScalarLayout(shader, targetIndex)
                : ScalarLayout.Empty;
            ClusterShadeCoreResource[] coreLayout = ResolveShadeCoreResources(resources);
            stageLayout = (scalarLayout, coreLayout);
            _stageLayouts.Add(resources, stageLayout);
        }
        bool sameMaterialLayout = materialStage is not null &&
            ReferenceEquals(materialStage.Material, material) &&
            ReferenceEquals(materialStage.Resources, resources);
        ScalarLayout.ScalarMaterialLayout scalarMaterialLayout = sameMaterialLayout
            ? materialStage!.Scalars : stageLayout.Scalars.Resolve(material);
        ClusterShadeCoreResource[] coreResources = stageLayout.CoreResources;
        uint?[] materialProperties = sameMaterialLayout
            ? materialStage!.MaterialProperties : material.ResolveResourceProperties(resources.Resources);
        GraphResourceBinder?[] featureResourceBinders = ClusterStageResourceBindings.ResolveFeatureResources(
            resources.Resources,
            materialProperties,
            _featureResources,
            coreResources);
        ClusterCommandBindingKey commandBindings = ClusterCommandBindingKey.Create(
            material,
            resources,
            materialProperties);
        return new ClusterShadeInvocation(
            0,
            material,
            pipeline,
            resources,
            coreResources,
            scalarMaterialLayout,
            materialProperties,
            featureResourceBinders,
            commandBindings) { VisibilityDepth = visibilityDepth };
    }

    private static ClusterShadeCoreResource[] ResolveShadeCoreResources(
        ShaderParameterLayout resources)
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

    private static ScalarLayout ResolveScalarLayout(Shader shader, int targetIndex)
    {
        TypeLayoutReflection layout = shader.GetSlangAttributedTypeLayout(
            targetIndex, "MaterialScalars", LayoutRules.DefaultStructuredBuffer);
        return ScalarLayout.FromSlangByteAddress(layout);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        foreach (LinkedComputePipeline pipeline in _pipelines.Values)
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
        new(ClusterBytes.Zero(0), ImmutableSortedDictionary<uint, ClusterShadeInvocation>.Empty, 0u,
            ImmutableDictionary<int, ScalarLayout.ScalarMaterialLayout>.Empty,
            [], [], ClusterShadingPath.Deferred, 0u, false);

    private readonly ImmutableSortedDictionary<uint, ClusterShadeInvocation> _bins;
    internal ImmutableDictionary<int, ScalarLayout.ScalarMaterialLayout> ScalarLayouts { get; }

    internal ClusterShadeStageSet(
        ClusterBytes binIndices,
        ImmutableSortedDictionary<uint, ClusterShadeInvocation> invocations,
        uint binCount,
        ImmutableDictionary<int, ScalarLayout.ScalarMaterialLayout> scalarLayouts,
        uint[] changedBins,
        int[] changedOffsets,
        ClusterShadingPath shadingPath,
        uint debugMode,
        bool requestShadowPages)
    {
        ShadingPath = shadingPath;
        DebugMode = debugMode;
        HasShadowRequests = requestShadowPages;
        BinIndices = binIndices;
        _bins = invocations;
        BinCount = binCount;
        ScalarLayouts = scalarLayouts;
        ChangedBins = changedBins;
        ChangedOffsets = changedOffsets;
    }

    internal ClusterBytes BinIndices { get; }
    internal IReadOnlyList<int> ChangedOffsets { get; }
    internal IEnumerable<ClusterShadeInvocation> Invocations => _bins.Values;
    internal ClusterShadingPath ShadingPath { get; }
    internal uint DebugMode { get; }
    internal bool HasShadowRequests { get; }
    internal uint BinCount { get; }
    internal ClusterShadeInvocation GetBin(uint bin) => _bins[bin];
    internal IReadOnlyList<uint> ChangedBins { get; }
    internal bool TryGetBin(uint bin, out ClusterShadeInvocation stage) => _bins.TryGetValue(bin, out stage!);

}

internal sealed record ClusterShadeInvocation(
    uint BinIndex,
    Material Material,
    LinkedComputePipeline Pipeline,
    ShaderParameterLayout Resources,
    ClusterShadeCoreResource[] CoreResources,
    ScalarLayout.ScalarMaterialLayout Scalars,
    uint?[] MaterialProperties,
    GraphResourceBinder?[] FeatureResourceBinders,
    ClusterCommandBindingKey CommandBindings)
{
    internal ClusterShadeInvocation? RequestShader { get; init; }
    internal ClusterVisibilityDepth VisibilityDepth { get; init; }
}

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
    InstanceData,
    InstanceProperties,
    Uniforms,
    Views,
    PolicyViews,
    PageHeap,
    VisibleClusters,
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
    WorkStatus,
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

internal static class ClusterStageResourceBindings
{
    internal static GraphResourceBinder?[] ResolveFeatureResources<TCore>(
        ReadOnlySpan<ShaderParameterResource> resources,
        ReadOnlySpan<uint?> materialProperties,
        IGraphResourceProvider? provider,
        TCore[] coreResources,
        bool allowNeutralRasterTarget = false)
        where TCore : struct, Enum
    {
        var result = new GraphResourceBinder?[resources.Length];
        for (int index = 0; index < resources.Length; index++)
        {
            ShaderParameterResource resource = resources[index];
            if (!resource.IsUsed)
                continue;
            if (!EqualityComparer<TCore>.Default.Equals(coreResources[index], default) ||
                materialProperties[index].HasValue || ClusterDeformResourceNames.IsRead(resource.Name))
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

}

internal static class ClusterShadeCoreResources
{
    internal static ClusterShadeCoreResource Resolve(string name, ResourceBindingType type)
    {
        (ClusterShadeCoreResource result, ResourceBindingType expected) = name switch
        {
            "InstanceData" or "MaterialInstanceData" => (ClusterShadeCoreResource.InstanceData, ResourceBindingType.BufferSrv),
            "InstanceProperties" or "MaterialInstanceProperties" => (ClusterShadeCoreResource.InstanceProperties, ResourceBindingType.ConstantBuffer),
            "Uniforms" => (ClusterShadeCoreResource.Uniforms, ResourceBindingType.ConstantBuffer),
            "Views" or "MaterialViews" => (ClusterShadeCoreResource.Views, ResourceBindingType.BufferSrv),
            "PixelCoordBuffer" => (ClusterShadeCoreResource.PixelCoordinates, ResourceBindingType.BufferSrv),
            "BinOffsets" => (ClusterShadeCoreResource.BinOffsets, ResourceBindingType.BufferSrv),
            "BinCounts" => (ClusterShadeCoreResource.BinCounts, ResourceBindingType.BufferSrv),
            "VisBuffer" => (ClusterShadeCoreResource.Visibility, ResourceBindingType.TextureSrv),
            "DepthBuffer" or "MaterialDepth" => (ClusterShadeCoreResource.Depth, ResourceBindingType.TextureSrv),
            "VisibleClusters" => (ClusterShadeCoreResource.VisibleClusters, ResourceBindingType.BufferSrv),
            "PageHeap" => (ClusterShadeCoreResource.PageHeap, ResourceBindingType.BufferSrv),
            "OutputColor" => (ClusterShadeCoreResource.OutputColor, ResourceBindingType.TextureUav),
            "OutputMotionVectors" => (ClusterShadeCoreResource.OutputMotionVectors, ResourceBindingType.TextureUav),
            "MaterialScalarRegion" => (ClusterShadeCoreResource.MaterialScalars, ResourceBindingType.BufferSrv),
            "MaterialScalarOffsets" => (ClusterShadeCoreResource.MaterialScalarOffsets, ResourceBindingType.BufferSrv),
            _ => (ClusterShadeCoreResource.None, default),
        };
        if (result != ClusterShadeCoreResource.None && type != expected)
        {
            throw new InvalidDataException(
                $"Cluster shade core resource '{name}' must be {expected}, not {type}.");
        }
        return result;
    }
}

internal static class ClusterSoftwareRasterCoreResources
{
    internal static ClusterSoftwareRasterCoreResource Resolve(string name, ResourceBindingType type)
    {
        (ClusterSoftwareRasterCoreResource result, ResourceBindingType expected) = name switch
        {
            "InstanceData" or "RasterOutputInstanceData" =>
                (ClusterSoftwareRasterCoreResource.InstanceData, ResourceBindingType.BufferSrv),
            "InstanceProperties" or "RasterOutputInstanceProperties" =>
                (ClusterSoftwareRasterCoreResource.InstanceProperties, ResourceBindingType.ConstantBuffer),
            "PushConstants" or "Uniforms" or "ClusterSoftwareRasterPushConstants" or
                "ClusterSoftwareRasterUniformData" =>
                (ClusterSoftwareRasterCoreResource.Uniforms, ResourceBindingType.ConstantBuffer),
            "Views" or "SoftwareRasterViews" => (ClusterSoftwareRasterCoreResource.Views, ResourceBindingType.BufferSrv),
            "PolicyClusterViews" or "SoftwareRasterPolicyViews" =>
                (ClusterSoftwareRasterCoreResource.PolicyViews, ResourceBindingType.BufferSrv),
            "PageHeap" => (ClusterSoftwareRasterCoreResource.PageHeap, ResourceBindingType.BufferSrv),
            "VisibleClusters" or "SoftwareRasterVisibleClusters" or "RasterOutputVisibleClusters" =>
                (ClusterSoftwareRasterCoreResource.VisibleClusters, ResourceBindingType.BufferSrv),
            "BinnedClusterIndexBuffer" or "SoftwareRasterBinnedClusterIndexBuffer" =>
                (ClusterSoftwareRasterCoreResource.BinnedClusters, ResourceBindingType.BufferSrv),
            "RasterBinMeta" or "SoftwareRasterBinMeta" =>
                (ClusterSoftwareRasterCoreResource.BinMetadata, ResourceBindingType.BufferSrv),
            "DepthTarget" or "SoftwareVisibilityDepthTarget" =>
                (ClusterSoftwareRasterCoreResource.Depth, ResourceBindingType.TextureSrv),
            "VisBuffer" or "SoftwareVisibilityBuffer" =>
                (ClusterSoftwareRasterCoreResource.Visibility, ResourceBindingType.TextureUav),
            "DepthUAV" or "VisibilityWinnerImage" =>
                (ClusterSoftwareRasterCoreResource.WinnerImage, ResourceBindingType.TextureUav),
            "VisibilityWinnerBuffer" => (ClusterSoftwareRasterCoreResource.WinnerBuffer, ResourceBindingType.BufferUav),
            "DebugSWOutput" or "SoftwareRasterDebugOutput" =>
                (ClusterSoftwareRasterCoreResource.Diagnostics, ResourceBindingType.BufferUav),
            _ => (ClusterSoftwareRasterCoreResource.None, default),
        };
        if (result != ClusterSoftwareRasterCoreResource.None && type != expected)
        {
            throw new InvalidDataException(
                $"Cluster software-raster core resource '{name}' must be {expected}, not {type}.");
        }
        return result;
    }
}

internal static class ClusterHardwareRasterCoreResources
{
    internal static ClusterHardwareRasterCoreResource Resolve(string name, ResourceBindingType type)
    {
        (ClusterHardwareRasterCoreResource result, ResourceBindingType expected) = name switch
        {
            "InstanceData" or "RasterOutputInstanceData" =>
                (ClusterHardwareRasterCoreResource.InstanceData, ResourceBindingType.BufferSrv),
            "InstanceProperties" or "RasterOutputInstanceProperties" =>
                (ClusterHardwareRasterCoreResource.InstanceProperties, ResourceBindingType.ConstantBuffer),
            "Uniforms" or "HardwareRasterUniformData" =>
                (ClusterHardwareRasterCoreResource.Uniforms, ResourceBindingType.ConstantBuffer),
            "Views" or "HardwareRasterViews" => (ClusterHardwareRasterCoreResource.Views, ResourceBindingType.BufferSrv),
            "PolicyClusterViews" or "HardwareRasterPolicyViews" =>
                (ClusterHardwareRasterCoreResource.PolicyViews, ResourceBindingType.BufferSrv),
            "DispatchUniforms" or "HardwareRasterDispatchUniformData" =>
                (ClusterHardwareRasterCoreResource.DispatchUniforms, ResourceBindingType.ConstantBuffer),
            "PageHeap" => (ClusterHardwareRasterCoreResource.PageHeap, ResourceBindingType.BufferSrv),
            "BinnedClusterIndexBuffer" or "HardwareRasterBinnedClusterIndexBuffer" =>
                (ClusterHardwareRasterCoreResource.BinnedClusters, ResourceBindingType.BufferSrv),
            "VisibleClusters" or "HardwareRasterVisibleClusters" or "RasterOutputVisibleClusters" =>
                (ClusterHardwareRasterCoreResource.VisibleClusters, ResourceBindingType.BufferSrv),
            "VisibleClusterMeta" or "HardwareRasterVisibleClusterMeta" =>
                (ClusterHardwareRasterCoreResource.DrawMetadata, ResourceBindingType.BufferSrv),
            "VisibilityWinnerImage" => (ClusterHardwareRasterCoreResource.WinnerImage, ResourceBindingType.TextureUav),
            "VisibilityWinnerBuffer" => (ClusterHardwareRasterCoreResource.WinnerBuffer, ResourceBindingType.BufferUav),
            _ => (ClusterHardwareRasterCoreResource.None, default),
        };
        if (result != ClusterHardwareRasterCoreResource.None && type != expected)
        {
            throw new InvalidDataException(
                $"Cluster hardware-raster core resource '{name}' must be {expected}, not {type}.");
        }
        return result;
    }
}

internal static class ClusterTessellationCoreResources
{
    internal static ClusterTessellationCoreResource Resolve(string name, ResourceBindingType type)
    {
        (ClusterTessellationCoreResource result, ResourceBindingType expected) = name switch
        {
            "InstanceData" or "RasterOutputInstanceData" =>
                (ClusterTessellationCoreResource.InstanceData, ResourceBindingType.BufferSrv),
            "InstanceProperties" or "RasterOutputInstanceProperties" =>
                (ClusterTessellationCoreResource.InstanceProperties, ResourceBindingType.ConstantBuffer),
            "Uniforms" or "TessellationUniformData" =>
                (ClusterTessellationCoreResource.Uniforms, ResourceBindingType.ConstantBuffer),
            "Views" or "TessellationViews" => (ClusterTessellationCoreResource.Views, ResourceBindingType.BufferSrv),
            "PolicyClusterViews" or "TessellationPolicyViews" =>
                (ClusterTessellationCoreResource.PolicyViews, ResourceBindingType.BufferSrv),
            "PageHeap" => (ClusterTessellationCoreResource.PageHeap, ResourceBindingType.BufferSrv),
            "VisibleClusters" or "TessellationVisibleClusters" or "RasterOutputVisibleClusters" =>
                (ClusterTessellationCoreResource.VisibleClusters, ResourceBindingType.BufferSrv),
            "BinnedClusterIndexBuffer" or "TessellationBinnedClusterIndexBuffer" =>
                (ClusterTessellationCoreResource.BinnedClusters, ResourceBindingType.BufferUav),
            "RasterBinMeta" or "TessellationRasterBinMeta" =>
                (ClusterTessellationCoreResource.BinMetadata, ResourceBindingType.BufferSrv),
            "TessellationRasterSlots" => (ClusterTessellationCoreResource.RasterSlots, ResourceBindingType.BufferSrv),
            "HiZTexture" or "TessellationHiZTexture" => (ClusterTessellationCoreResource.HiZ, ResourceBindingType.TextureSrv),
            "DepthTarget" or "SoftwareVisibilityDepthTarget" =>
                (ClusterTessellationCoreResource.Depth, ResourceBindingType.TextureSrv),
            "VisBuffer" or "SoftwareVisibilityBuffer" =>
                (ClusterTessellationCoreResource.Visibility, ResourceBindingType.TextureUav),
            "DepthUAV" or "VisibilityWinnerImage" =>
                (ClusterTessellationCoreResource.WinnerImage, ResourceBindingType.TextureUav),
            "VisibilityWinnerBuffer" => (ClusterTessellationCoreResource.WinnerBuffer, ResourceBindingType.BufferUav),
            "TessellationCounters" => (ClusterTessellationCoreResource.Counters, ResourceBindingType.BufferUav),
            "WorkStatus" => (ClusterTessellationCoreResource.WorkStatus, ResourceBindingType.BufferUav),
            "TessellationWorkPatches0" => (ClusterTessellationCoreResource.WorkPatches0, ResourceBindingType.BufferUav),
            "TessellationWorkPatches1" => (ClusterTessellationCoreResource.WorkPatches1, ResourceBindingType.BufferUav),
            "TessellationLevelCounts" => (ClusterTessellationCoreResource.LevelCounts, ResourceBindingType.BufferUav),
            "TessellationNextLevelArgs" => (ClusterTessellationCoreResource.NextLevelArgs, ResourceBindingType.BufferUav),
            "TessellationFinalPatches" => (ClusterTessellationCoreResource.FinalPatches, ResourceBindingType.BufferUav),
            "GroupedPatchIndices" => (ClusterTessellationCoreResource.GroupedPatchIndices, ResourceBindingType.BufferSrv),
            "TessellationRasterPatches" => (ClusterTessellationCoreResource.RasterPatches, ResourceBindingType.BufferSrv),
            "TessellationBinCounts" => (ClusterTessellationCoreResource.BinCounts, ResourceBindingType.BufferSrv),
            "TessellationBinOffsets" => (ClusterTessellationCoreResource.BinOffsets, ResourceBindingType.BufferSrv),
            _ => (ClusterTessellationCoreResource.None, default),
        };
        if (result != ClusterTessellationCoreResource.None && type != expected)
        {
            throw new InvalidDataException(
                $"Cluster tessellation core resource '{name}' must be {expected}, not {type}.");
        }
        return result;
    }
}

internal static class ClusterStageTargets
{
    internal static void RequireVertexEncoding(
        Shader shader,
        string entryPoint,
        string expected,
        string subject)
    {
        string actual = shader.RequireSlangEntryStringAttribute(
            0, entryPoint, "VertexEncoding", subject);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{subject} entry '{entryPoint}' accepts vertex encoding '{actual}', but the " +
                $"Cluster pipeline requires '{expected}'.");
        }
    }

}
