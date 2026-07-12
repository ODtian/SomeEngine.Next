using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SomeEngine.RenderGraph;

/// <summary>Marks a partial value-only pass parameter shape for generated binding glue.</summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class PassParametersAttribute : Attribute;

/// <summary>
/// Marks a partial value-only shader parameter shape. The marker carries no shader path, entry
/// point, or shader identity; a cooked <see cref="ShaderDesc"/> is supplied only while pairing.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class ShaderParametersAttribute : Attribute;

/// <summary>An immutable runtime pairing between generated parameters and cooked shader truth.</summary>
public readonly record struct ShaderParameterBinding
{
    private readonly BindGroupLayoutHandle[]? _groupLayouts;

    public ShaderParameterBinding(in ShaderDesc shader, ulong layoutHash)
        : this(shader, layoutHash, default, ReadOnlyMemory<BindGroupLayoutHandle>.Empty)
    {
    }

    public ShaderParameterBinding(
        in ShaderDesc shader,
        PipelineLayoutHandle pipelineLayout,
        ReadOnlyMemory<BindGroupLayoutHandle> groupLayouts)
        : this(shader, shader.Interface.LayoutHash, pipelineLayout, groupLayouts)
    {
    }

    private ShaderParameterBinding(
        in ShaderDesc shader,
        ulong layoutHash,
        PipelineLayoutHandle pipelineLayout,
        ReadOnlyMemory<BindGroupLayoutHandle> groupLayouts)
    {
        if (!shader.Key.IsValid) throw new ArgumentException("A cooked shader artifact is required.", nameof(shader));
        if (layoutHash == 0 || shader.Interface.LayoutHash != layoutHash)
            throw new ArgumentException("The parameter binding layout hash must match cooked shader reflection.", nameof(layoutHash));
        Shader = shader;
        LayoutHash = layoutHash;
        PipelineLayout = pipelineLayout;
        _groupLayouts = groupLayouts.ToArray();
    }

    public ShaderDesc Shader { get; }
    public ulong LayoutHash { get; }
    public PipelineLayoutHandle PipelineLayout { get; }
    public ReadOnlyMemory<BindGroupLayoutHandle> GroupLayouts => _groupLayouts ?? [];
}

/// <summary>A logical buffer id and its complete view/access description as one atomic value.</summary>
public readonly record struct BufferParameter(
    BufferId Resource,
    BufferRange Range,
    BindingKind Kind,
    BufferUse Use,
    ResourceEffect Effect = ResourceEffect.Read,
    Format Format = Format.Unknown,
    uint Stride = 0,
    PriorContents PriorContents = PriorContents.Required,
    WriteCoverage Coverage = WriteCoverage.Partial,
    string? Name = null);

/// <summary>A descriptor-array value whose elements each retain logical id and complete view.</summary>
public readonly record struct BufferParameterArray(ReadOnlyMemory<BufferParameter> Elements);

/// <summary>A logical texture id and its complete view/access description as one atomic value.</summary>
public readonly record struct TextureParameter(
    TextureId Resource,
    TextureSubresourceRange Range,
    TextureViewUsage ViewUsage,
    TextureUse Use,
    ResourceEffect Effect = ResourceEffect.Read,
    Format Format = Format.Unknown,
    TextureViewDimension? Dimension = null,
    PriorContents PriorContents = PriorContents.Required,
    WriteCoverage Coverage = WriteCoverage.Partial,
    string? Name = null);

/// <summary>A descriptor-array value whose elements each retain logical id and complete view.</summary>
public readonly record struct TextureParameterArray(ReadOnlyMemory<TextureParameter> Elements);

/// <summary>One externally owned immutable sampler descriptor value.</summary>
public readonly record struct SamplerParameter(SamplerHandle Sampler);

/// <summary>One externally owned sampler descriptor array.</summary>
public readonly record struct SamplerParameterArray(ReadOnlyMemory<SamplerParameter> Elements);

/// <summary>
/// One explicitly placed unmanaged value in the cooked shader's push-constant address space.
/// Pairing validates offset, size, coverage, and visibility against <see cref="ShaderInterface"/>.
/// </summary>
public readonly record struct ConstantParameter<T>(T Value, uint Offset) where T : unmanaged;

/// <summary>An opaque declaration emitted by the RenderGraph parameter source generator.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct GeneratedParameterDeclaration
{
    internal GeneratedParameterDeclaration(BufferParameter[] buffers)
    {
        Kind = GeneratedParameterKind.Buffer;
        Buffers = buffers;
        Textures = null;
        Samplers = null;
        ConstantOffset = 0;
        ConstantSize = 0;
    }

    internal GeneratedParameterDeclaration(TextureParameter[] textures)
    {
        Kind = GeneratedParameterKind.Texture;
        Buffers = null;
        Textures = textures;
        Samplers = null;
        ConstantOffset = 0;
        ConstantSize = 0;
    }

    internal GeneratedParameterDeclaration(SamplerParameter[] samplers)
    {
        Kind = GeneratedParameterKind.Sampler;
        Buffers = null;
        Textures = null;
        Samplers = samplers;
        ConstantOffset = 0;
        ConstantSize = 0;
    }

    internal GeneratedParameterDeclaration(uint constantOffset, int constantSize)
    {
        Kind = GeneratedParameterKind.Constant;
        Buffers = null;
        Textures = null;
        Samplers = null;
        ConstantOffset = constantOffset;
        ConstantSize = constantSize;
    }

    internal GeneratedParameterKind Kind { get; }
    internal BufferParameter[]? Buffers { get; }
    internal TextureParameter[]? Textures { get; }
    internal SamplerParameter[]? Samplers { get; }
    internal uint ConstantOffset { get; }
    internal int ConstantSize { get; }
}

/// <summary>Opaque, pre-execute state created only by generated parameter pairing.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class GeneratedParameterSet
{
    private readonly GeneratedBindingEntry[] _entries;
    private readonly ShaderParameterBinding _pairing;
    private readonly PushConstantRange[] _pushConstants;
    private readonly HashSet<(uint Offset, int Size)> _constantDeclarations;
    private readonly HashSet<(uint Offset, int Size)> _packedConstants = [];
    private readonly byte[] _constantData;
    private bool _sealed;

    internal GeneratedParameterSet(
        GeneratedBindingEntry[] entries,
        in ShaderParameterBinding pairing,
        PushConstantRange[] pushConstants,
        HashSet<(uint Offset, int Size)> constantDeclarations,
        int constantByteCount)
    {
        _entries = entries;
        _pairing = pairing;
        _pushConstants = pushConstants;
        _constantDeclarations = constantDeclarations;
        _constantData = new byte[constantByteCount];
    }

    internal void Pack<T>(in ConstantParameter<T> parameter) where T : unmanaged
    {
        int size = GeneratedParameterBinding.SizeOf<T>();
        (uint Offset, int Size) key = (parameter.Offset, size);
        if (!_constantDeclarations.Contains(key))
            throw new InvalidOperationException("Generated constant packing does not match the paired declaration.");
        if (!_packedConstants.Add(key))
            throw new InvalidOperationException("A generated constant field was packed more than once.");
        T value = parameter.Value;
        MemoryMarshal.Write(_constantData.AsSpan(checked((int)parameter.Offset), size), in value);
    }

    internal void Seal()
    {
        if (_packedConstants.Count != _constantDeclarations.Count)
            throw new InvalidOperationException("Every generated constant declaration must be packed before execute.");
        _sealed = true;
    }

    internal void Bind(ICommandContext commands, in PassResources resources)
    {
        if (!_sealed) throw new InvalidOperationException("Generated parameters must be paired and sealed before execute.");
        int first = 0;
        while (first < _entries.Length)
        {
            uint group = _entries[first].Group;
            int last = first + 1;
            while (last < _entries.Length && _entries[last].Group == group) last++;
            BindingWrite[] writes = new BindingWrite[last - first];
            for (int index = first; index < last; index++)
            {
                GeneratedBindingEntry entry = _entries[index];
                writes[index - first] = entry.Kind switch
                {
                    GeneratedParameterKind.Buffer => BindingWrite.Buffer(entry.Binding, resources.Get(entry.Buffer), entry.Element),
                    GeneratedParameterKind.Texture => BindingWrite.Texture(entry.Binding, resources.Get(entry.Texture), entry.Element),
                    GeneratedParameterKind.Sampler => BindingWrite.SamplerValue(entry.Binding, entry.Sampler, entry.Element),
                    _ => throw new InvalidOperationException("Generated descriptor glue contains a non-descriptor entry."),
                };
            }
            commands.SetBindings(group, _pairing.GroupLayouts.Span[checked((int)group)], writes);
            first = last;
        }

        foreach (PushConstantRange range in _pushConstants)
        {
            commands.SetPushConstants(
                _pairing.PipelineLayout,
                range.Visibility,
                range.Offset,
                _constantData.AsSpan(checked((int)range.Offset), checked((int)range.Size)));
        }
    }
}

/// <summary>
/// Runtime primitives called by generated code. Pairing consumes cooked reflection before graph
/// compilation; binding uses only opaque access tokens and immutable descriptor/constant data.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class GeneratedParameterBinding
{
    public static GeneratedParameterDeclaration Describe(in BufferParameter parameter) =>
        new([parameter]);

    public static GeneratedParameterDeclaration Describe(in BufferParameterArray parameters) =>
        new(parameters.Elements.ToArray());

    public static GeneratedParameterDeclaration Describe(in TextureParameter parameter) =>
        new([parameter]);

    public static GeneratedParameterDeclaration Describe(in TextureParameterArray parameters) =>
        new(parameters.Elements.ToArray());

    public static GeneratedParameterDeclaration Describe(in SamplerParameter parameter) =>
        new([parameter]);

    public static GeneratedParameterDeclaration Describe(in SamplerParameterArray parameters) =>
        new(parameters.Elements.ToArray());

    public static GeneratedParameterDeclaration Describe<T>(in ConstantParameter<T> parameter) where T : unmanaged =>
        new(parameter.Offset, SizeOf<T>());

    public static GeneratedParameterSet Pair(
        ref GraphBuilder graph,
        ref PassBuilder pass,
        in ShaderParameterBinding pairing,
        ReadOnlySpan<GeneratedParameterDeclaration> declarations)
    {
        if (pairing.LayoutHash == 0 || pairing.LayoutHash != pairing.Shader.Interface.LayoutHash)
            throw new InvalidOperationException("Generated parameter pairing is stale relative to cooked shader reflection.");
        ValidateRuntimeLayouts(pairing);

        ReadOnlySpan<ShaderBinding> shaderBindings = pairing.Shader.Interface.Bindings.Span;
        List<ShaderBindingAccess> mappings = [];
        List<GeneratedBindingEntry> entries = [];
        int bindingOrdinal = 0;
        foreach (ref readonly GeneratedParameterDeclaration declaration in declarations)
        {
            if (declaration.Kind == GeneratedParameterKind.Constant) continue;
            if ((uint)bindingOrdinal >= (uint)shaderBindings.Length)
                throw new InvalidOperationException("Generated parameters declare more descriptors than cooked shader reflection.");
            ShaderBinding shaderBinding = shaderBindings[bindingOrdinal++];
            switch (declaration.Kind)
            {
                case GeneratedParameterKind.Buffer:
                    PairBuffers(ref graph, ref pass, shaderBinding, declaration.Buffers!, mappings, entries);
                    break;
                case GeneratedParameterKind.Texture:
                    PairTextures(ref graph, ref pass, shaderBinding, declaration.Textures!, mappings, entries);
                    break;
                case GeneratedParameterKind.Sampler:
                    PairSamplers(ref pass, shaderBinding, declaration.Samplers!, pairing, mappings, entries);
                    break;
                default:
                    throw new InvalidOperationException("Generated parameter declaration kind is invalid.");
            }
        }
        if (bindingOrdinal != shaderBindings.Length)
            throw new InvalidOperationException("Generated parameters do not cover every cooked shader descriptor binding.");

        pass.UsesShader(pairing.Shader, mappings.ToArray());
        PushConstantRange[] pushConstants = pairing.Shader.Interface.PushConstants.ToArray();
        HashSet<(uint Offset, int Size)> constantDeclarations = ValidateConstants(declarations, pushConstants, out int constantByteCount);
        GeneratedBindingEntry[] ordered = entries
            .OrderBy(static entry => entry.Group)
            .ThenBy(static entry => entry.Binding)
            .ThenBy(static entry => entry.Element)
            .ToArray();
        return new GeneratedParameterSet(ordered, pairing, pushConstants, constantDeclarations, constantByteCount);
    }

    public static void Pack<T>(GeneratedParameterSet parameters, in ConstantParameter<T> constant) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Pack(constant);
    }

    public static void Seal(GeneratedParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Seal();
    }

    public static void Bind(GeneratedParameterSet parameters, ICommandContext commands, in PassResources resources)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(commands);
        parameters.Bind(commands, resources);
    }

    internal static int SizeOf<T>() where T : unmanaged
    {
        T value = default;
        return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)).Length;
    }

    private static void PairBuffers(
        ref GraphBuilder graph,
        ref PassBuilder pass,
        in ShaderBinding shaderBinding,
        BufferParameter[] parameters,
        List<ShaderBindingAccess> mappings,
        List<GeneratedBindingEntry> entries)
    {
        if (parameters.Length == 0 || shaderBinding.Count != (uint)parameters.Length)
            throw new InvalidOperationException("Generated buffer descriptor count does not match cooked shader reflection.");
        for (int element = 0; element < parameters.Length; element++)
        {
            BufferParameter parameter = parameters[element];
            if (parameter.Kind != shaderBinding.Kind)
                throw new InvalidOperationException("Generated buffer descriptor kind does not match cooked shader reflection.");
            BufferViewAccess access = Declare(ref graph, ref pass, parameter);
            ShaderBindingAccess mapping = pass.MapShaderBinding(shaderBinding.Group, shaderBinding.Binding, access, checked((uint)element));
            mappings.Add(mapping);
            entries.Add(GeneratedBindingEntry.ForBuffer(shaderBinding, checked((uint)element), access));
        }
    }

    private static void PairTextures(
        ref GraphBuilder graph,
        ref PassBuilder pass,
        in ShaderBinding shaderBinding,
        TextureParameter[] parameters,
        List<ShaderBindingAccess> mappings,
        List<GeneratedBindingEntry> entries)
    {
        if (shaderBinding.Kind is not (BindingKind.SampledTexture or BindingKind.StorageTexture))
            throw new InvalidOperationException("Generated texture descriptor kind does not match cooked shader reflection.");
        if (parameters.Length == 0 || shaderBinding.Count != (uint)parameters.Length)
            throw new InvalidOperationException("Generated texture descriptor count does not match cooked shader reflection.");
        for (int element = 0; element < parameters.Length; element++)
        {
            TextureViewAccess access = Declare(ref graph, ref pass, parameters[element]);
            BindingKind actual = access.Use == TextureUse.Sampled ? BindingKind.SampledTexture : BindingKind.StorageTexture;
            if (actual != shaderBinding.Kind)
                throw new InvalidOperationException("Generated texture descriptor kind does not match cooked shader reflection.");
            ShaderBindingAccess mapping = pass.MapShaderBinding(shaderBinding.Group, shaderBinding.Binding, access, checked((uint)element));
            mappings.Add(mapping);
            entries.Add(GeneratedBindingEntry.ForTexture(shaderBinding, checked((uint)element), access));
        }
    }

    private static void PairSamplers(
        ref PassBuilder pass,
        in ShaderBinding shaderBinding,
        SamplerParameter[] parameters,
        in ShaderParameterBinding pairing,
        List<ShaderBindingAccess> mappings,
        List<GeneratedBindingEntry> entries)
    {
        if (shaderBinding.Kind != BindingKind.Sampler)
            throw new InvalidOperationException("Generated sampler descriptor kind does not match cooked shader reflection.");
        if (parameters.Length == 0 || shaderBinding.Count != (uint)parameters.Length)
            throw new InvalidOperationException("Generated sampler descriptor count does not match cooked shader reflection.");
        DeviceDomain domain = pairing.GroupLayouts.Span[checked((int)shaderBinding.Group)].Domain;
        for (int element = 0; element < parameters.Length; element++)
        {
            SamplerHandle sampler = parameters[element].Sampler;
            if (!sampler.IsValid || sampler.Domain != domain)
                throw new InvalidOperationException("Generated sampler descriptors must be valid handles from the paired layout device.");
            ShaderBindingAccess mapping = pass.MapExternallyManagedShaderBinding(
                shaderBinding.Group,
                shaderBinding.Binding,
                checked((uint)element));
            mappings.Add(mapping);
            entries.Add(GeneratedBindingEntry.ForSampler(shaderBinding, checked((uint)element), sampler));
        }
    }

    private static BufferViewAccess Declare(
        ref GraphBuilder graph,
        ref PassBuilder pass,
        in BufferParameter parameter)
    {
        BufferUse expectedUse = parameter.Kind switch
        {
            BindingKind.ConstantBuffer => BufferUse.VertexOrConstant,
            BindingKind.ReadOnlyBuffer => BufferUse.ShaderRead,
            BindingKind.StorageBuffer => BufferUse.ShaderWrite,
            _ => throw new InvalidOperationException("Generated buffer parameter requires a buffer descriptor kind."),
        };
        if (parameter.Use != expectedUse)
            throw new InvalidOperationException("Generated buffer use does not match its complete view descriptor.");
        BufferViewId view = graph.CreateBufferView(
            parameter.Resource,
            parameter.Range,
            parameter.Kind,
            parameter.Format,
            parameter.Stride,
            parameter.Name);
        return parameter.Effect switch
        {
            ResourceEffect.Read => pass.Read(view),
            ResourceEffect.Write => pass.Write(view, parameter.PriorContents, parameter.Coverage),
            ResourceEffect.ReadWrite => pass.ReadWrite(view, parameter.Coverage),
            _ => throw new ArgumentOutOfRangeException(nameof(parameter)),
        };
    }

    private static TextureViewAccess Declare(
        ref GraphBuilder graph,
        ref PassBuilder pass,
        in TextureParameter parameter)
    {
        TextureViewId view = graph.CreateTextureView(
            parameter.Resource,
            parameter.Range,
            parameter.ViewUsage,
            parameter.Format,
            parameter.Name,
            parameter.Dimension);
        TextureViewAccess access = parameter.Effect switch
        {
            ResourceEffect.Read => pass.Read(view),
            ResourceEffect.Write => pass.Write(view, parameter.PriorContents, parameter.Coverage),
            ResourceEffect.ReadWrite => pass.ReadWrite(view, parameter.Coverage),
            _ => throw new ArgumentOutOfRangeException(nameof(parameter)),
        };
        if (access.Use != parameter.Use)
            throw new InvalidOperationException("Generated texture use does not match its complete view descriptor.");
        return access;
    }

    private static void ValidateRuntimeLayouts(in ShaderParameterBinding pairing)
    {
        ReadOnlySpan<ShaderBinding> bindings = pairing.Shader.Interface.Bindings.Span;
        ReadOnlySpan<BindGroupLayoutHandle> layouts = pairing.GroupLayouts.Span;
        DeviceDomain domain = default;
        bool hasDomain = false;
        foreach (ref readonly ShaderBinding binding in bindings)
        {
            if (binding.Group >= (uint)layouts.Length || !layouts[checked((int)binding.Group)].IsValid)
                throw new InvalidOperationException("Generated descriptor binding requires a paired runtime group layout.");
            DeviceDomain current = layouts[checked((int)binding.Group)].Domain;
            if (hasDomain && current != domain)
                throw new InvalidOperationException("Generated runtime layouts must belong to one device domain.");
            domain = current;
            hasDomain = true;
        }
        if (pairing.Shader.Interface.PushConstants.Length != 0)
        {
            if (!pairing.PipelineLayout.IsValid)
                throw new InvalidOperationException("Generated push constants require a paired runtime pipeline layout.");
            if (hasDomain && pairing.PipelineLayout.Domain != domain)
                throw new InvalidOperationException("Generated pipeline and group layouts must belong to one device domain.");
        }
    }

    private static HashSet<(uint Offset, int Size)> ValidateConstants(
        ReadOnlySpan<GeneratedParameterDeclaration> declarations,
        PushConstantRange[] pushConstants,
        out int constantByteCount)
    {
        List<(uint Offset, int Size, ulong End)> constants = CollectConstants(declarations);
        ValidateConstantOverlap(constants);
        ValidateConstantRanges(constants, pushConstants);

        ulong byteCount = pushConstants.Length == 0
            ? 0
            : pushConstants.Max(static range => checked((ulong)range.Offset + range.Size));
        constantByteCount = checked((int)byteCount);
        return constants.Select(static value => (value.Offset, value.Size)).ToHashSet();
    }

    private static List<(uint Offset, int Size, ulong End)> CollectConstants(
        ReadOnlySpan<GeneratedParameterDeclaration> declarations)
    {
        List<(uint Offset, int Size, ulong End)> constants = [];
        foreach (ref readonly GeneratedParameterDeclaration declaration in declarations)
        {
            if (declaration.Kind != GeneratedParameterKind.Constant) continue;
            if (declaration.ConstantSize <= 0 || (declaration.ConstantOffset & 3) != 0 || (declaration.ConstantSize & 3) != 0)
                throw new InvalidOperationException("Generated constants must have non-empty four-byte-aligned offset and size.");
            ulong end = checked((ulong)declaration.ConstantOffset + (uint)declaration.ConstantSize);
            constants.Add((declaration.ConstantOffset, declaration.ConstantSize, end));
        }
        constants.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        return constants;
    }

    private static void ValidateConstantOverlap(List<(uint Offset, int Size, ulong End)> constants)
    {
        for (int index = 1; index < constants.Count; index++)
            if (constants[index].Offset < constants[index - 1].End)
                throw new InvalidOperationException("Generated constant fields may not overlap.");
    }

    private static void ValidateConstantRanges(
        List<(uint Offset, int Size, ulong End)> constants,
        PushConstantRange[] pushConstants)
    {
        foreach ((uint offset, int _, ulong end) in constants)
        {
            if (!pushConstants.Any(range => offset >= range.Offset && end <= checked((ulong)range.Offset + range.Size)))
                throw new InvalidOperationException("Generated constant packing lies outside cooked push-constant reflection.");
        }
        foreach (PushConstantRange range in pushConstants)
            ValidateConstantCoverage(constants, range);
        if (pushConstants.Length == 0 && constants.Count != 0)
            throw new InvalidOperationException("Generated constants require cooked push-constant reflection.");
    }

    private static void ValidateConstantCoverage(
        List<(uint Offset, int Size, ulong End)> constants,
        in PushConstantRange range)
    {
        ulong cursor = range.Offset;
        ulong end = checked((ulong)range.Offset + range.Size);
        foreach ((uint offset, int _, ulong constantEnd) in constants)
        {
            if (constantEnd <= cursor || offset >= end) continue;
            if (offset > cursor)
                throw new InvalidOperationException("Generated constants do not fully cover cooked push-constant reflection.");
            cursor = Math.Max(cursor, constantEnd);
            if (cursor >= end) break;
        }
        if (cursor < end)
            throw new InvalidOperationException("Generated constants do not fully cover cooked push-constant reflection.");
    }
}

internal enum GeneratedParameterKind : byte
{
    Buffer,
    Texture,
    Sampler,
    Constant,
}

internal readonly record struct GeneratedBindingEntry(
    GeneratedParameterKind Kind,
    uint Group,
    uint Binding,
    uint Element,
    BufferViewAccess Buffer,
    TextureViewAccess Texture,
    SamplerHandle Sampler)
{
    public static GeneratedBindingEntry ForBuffer(in ShaderBinding binding, uint element, BufferViewAccess access) =>
        new(GeneratedParameterKind.Buffer, binding.Group, binding.Binding, element, access, default, default);

    public static GeneratedBindingEntry ForTexture(in ShaderBinding binding, uint element, TextureViewAccess access) =>
        new(GeneratedParameterKind.Texture, binding.Group, binding.Binding, element, default, access, default);

    public static GeneratedBindingEntry ForSampler(in ShaderBinding binding, uint element, SamplerHandle sampler) =>
        new(GeneratedParameterKind.Sampler, binding.Group, binding.Binding, element, default, default, sampler);
}
