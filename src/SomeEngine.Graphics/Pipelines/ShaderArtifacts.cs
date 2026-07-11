namespace SomeEngine.Graphics;

[Flags]
public enum ShaderStage : byte
{
    Vertex = 1 << 0,
    Pixel = 1 << 1,
    Compute = 1 << 2,
}

public enum BindingKind : byte
{
    ConstantBuffer,
    SampledTexture,
    StorageTexture,
    ReadOnlyBuffer,
    StorageBuffer,
    Sampler,
}

/// <summary>Access facts preserved from shader reflection.</summary>
public enum ReflectedAccess : byte
{
    Unknown,
    ReadOnly,
    WriteOnly,
    ReadWrite,
}

/// <summary>User-authored effect metadata, kept independently from reflected access.</summary>
public enum DeclaredEffect : byte
{
    Unspecified,
    Read,
    Write,
    ReadWrite,
}

/// <summary>User-authored operation qualifiers, kept independently from the base effect.</summary>
[Flags]
public enum DeclaredOperations : byte
{
    None = 0,
    Atomic = 1 << 0,
    Append = 1 << 1,
    Consume = 1 << 2,
    RasterOrdered = 1 << 3,
    Feedback = 1 << 4,
}

/// <summary>Operation qualifiers reported by shader reflection, parallel to authored qualifiers.</summary>
[Flags]
public enum ReflectedOperations : byte
{
    None = 0,
    Atomic = 1 << 0,
    Append = 1 << 1,
    Consume = 1 << 2,
    RasterOrdered = 1 << 3,
    Feedback = 1 << 4,
}

/// <summary>Texture ABI shape preserved by shader reflection. Unknown means the producer had no fact.</summary>
public enum ShaderTextureDimension : byte
{
    Unknown,
    Texture1D,
    Texture1DArray,
    Texture2D,
    Texture2DArray,
    Texture2DMS,
    Texture2DMSArray,
    Cube,
    CubeArray,
    Texture3D,
}

/// <summary>Scalar category returned by a texture operation. Normalized formats return Float.</summary>
public enum TextureSampleType : byte
{
    Unknown,
    Float,
    UInt,
    SInt,
    Depth,
}

public readonly record struct ShaderBinding(
    uint Group,
    uint Binding,
    BindingKind Kind,
    uint Count,
    ShaderStage Visibility,
    ReflectedAccess ReflectedAccess,
    DeclaredEffect DeclaredEffect,
    ShaderTextureDimension TextureDimension = ShaderTextureDimension.Unknown,
    TextureSampleType TextureSampleType = TextureSampleType.Unknown,
    Format StorageFormat = Format.Unknown,
    DeclaredOperations DeclaredOperations = DeclaredOperations.None,
    ReflectedOperations ReflectedOperations = ReflectedOperations.None);

/// <summary>
/// One logical byte range backed by a shader constant-buffer register. D3D12 lowers the range to
/// root constants; other backends retain the same register/space identity in their pipeline ABI.
/// </summary>
public readonly record struct PushConstantRange(
    uint Offset,
    uint Size,
    ShaderStage Visibility,
    uint Register = 0,
    uint Space = 0);

public readonly record struct ShaderInterface(
    ReadOnlyMemory<ShaderBinding> Bindings,
    ReadOnlyMemory<PushConstantRange> PushConstants,
    ulong LayoutHash);

public enum ShaderBinaryFormat : byte
{
    Dxil,
    SpirV,
}

/// <summary>A full-width canonical identity for bytecode, interface, effects, stage, and entry point.</summary>
public readonly record struct ShaderArtifactKey(ulong Word0, ulong Word1, ulong Word2, ulong Word3)
{
    public bool IsValid => (Word0 | Word1 | Word2 | Word3) != 0;
}

public readonly record struct ShaderDesc(
    ShaderArtifactKey Key,
    ShaderBinaryFormat Format,
    ShaderStage Stage,
    string EntryPoint,
    ReadOnlyMemory<byte> Bytecode,
    ShaderInterface Interface,
    string? Name = null);

public readonly record struct BindingDesc(
    uint Binding,
    BindingKind Kind,
    uint Count,
    ShaderStage Visibility);

public readonly record struct PipelineLayoutDesc(
    ReadOnlyMemory<BindGroupLayoutHandle> Groups,
    ReadOnlyMemory<PushConstantRange> PushConstants,
    string? Name = null);

public enum BindingValueKind : byte
{
    TextureView,
    BufferView,
    Sampler,
}

public readonly record struct BindingWrite(
    uint Binding,
    uint Element,
    BindingValueKind ValueKind,
    TextureViewHandle TextureView,
    BufferViewHandle BufferView,
    SamplerHandle Sampler)
{
    public static BindingWrite Texture(uint binding, TextureViewHandle view, uint element = 0) =>
        new(binding, element, BindingValueKind.TextureView, view, default, default);

    public static BindingWrite Buffer(uint binding, BufferViewHandle view, uint element = 0) =>
        new(binding, element, BindingValueKind.BufferView, default, view, default);

    public static BindingWrite SamplerValue(uint binding, SamplerHandle sampler, uint element = 0) =>
        new(binding, element, BindingValueKind.Sampler, default, default, sampler);
}

public enum PipelineType : byte
{
    Raster,
    Compute,
}

public readonly record struct PipelineShaderIdentity(ShaderArtifactKey Key, ShaderStage Stage);

/// <summary>Immutable shader identity metadata for one exact live pipeline.</summary>
public sealed class PipelineMetadata
{
    private readonly PipelineShaderIdentity[] _shaders;
    private readonly System.Collections.ObjectModel.ReadOnlyCollection<PipelineShaderIdentity> _view;

    public PipelineMetadata(PipelineType type, ReadOnlySpan<PipelineShaderIdentity> shaders)
    {
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        if (shaders.IsEmpty) throw new ArgumentException("A pipeline must identify at least one shader.", nameof(shaders));
        _shaders = shaders.ToArray();
        foreach (PipelineShaderIdentity shader in _shaders)
        {
            if (!shader.Key.IsValid || shader.Stage is not (ShaderStage.Vertex or ShaderStage.Pixel or ShaderStage.Compute))
                throw new ArgumentException("Pipeline shader identities must be valid.", nameof(shaders));
        }
        Type = type;
        _view = Array.AsReadOnly(_shaders);
    }

    public PipelineType Type { get; }
    public IReadOnlyList<PipelineShaderIdentity> Shaders => _view;
}
