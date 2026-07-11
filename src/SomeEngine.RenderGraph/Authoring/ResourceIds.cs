using System.Numerics;

namespace SomeEngine.RenderGraph;

internal sealed class GraphToken;

public readonly struct BufferId : IEquatable<BufferId>
{
    private readonly GraphToken? _owner;

    internal BufferId(GraphToken owner, int ordinal)
    {
        _owner = owner;
        Ordinal = ordinal;
    }

    internal GraphToken? Owner => _owner;
    internal int Ordinal { get; }
    public bool IsValid => _owner is not null && Ordinal >= 0;
    public bool Equals(BufferId other) => ReferenceEquals(_owner, other._owner) && Ordinal == other.Ordinal;
    public override bool Equals(object? obj) => obj is BufferId other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_owner, Ordinal);
    public static bool operator ==(BufferId left, BufferId right) => left.Equals(right);
    public static bool operator !=(BufferId left, BufferId right) => !left.Equals(right);
}

public readonly struct TextureId : IEquatable<TextureId>
{
    private readonly GraphToken? _owner;

    internal TextureId(GraphToken owner, int ordinal)
    {
        _owner = owner;
        Ordinal = ordinal;
    }

    internal GraphToken? Owner => _owner;
    internal int Ordinal { get; }
    public bool IsValid => _owner is not null && Ordinal >= 0;
    public bool Equals(TextureId other) => ReferenceEquals(_owner, other._owner) && Ordinal == other.Ordinal;
    public override bool Equals(object? obj) => obj is TextureId other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_owner, Ordinal);
    public static bool operator ==(TextureId left, TextureId right) => left.Equals(right);
    public static bool operator !=(TextureId left, TextureId right) => !left.Equals(right);
}

public readonly struct BufferViewId : IEquatable<BufferViewId>
{
    private readonly GraphToken? _owner;

    internal BufferViewId(GraphToken owner, int ordinal)
    {
        _owner = owner;
        Ordinal = ordinal;
    }

    internal GraphToken? Owner => _owner;
    internal int Ordinal { get; }
    public bool IsValid => _owner is not null && Ordinal >= 0;
    public bool Equals(BufferViewId other) => ReferenceEquals(_owner, other._owner) && Ordinal == other.Ordinal;
    public override bool Equals(object? obj) => obj is BufferViewId other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_owner, Ordinal);
    public static bool operator ==(BufferViewId left, BufferViewId right) => left.Equals(right);
    public static bool operator !=(BufferViewId left, BufferViewId right) => !left.Equals(right);
}

public readonly struct TextureViewId : IEquatable<TextureViewId>
{
    private readonly GraphToken? _owner;

    internal TextureViewId(GraphToken owner, int ordinal)
    {
        _owner = owner;
        Ordinal = ordinal;
    }

    internal GraphToken? Owner => _owner;
    internal int Ordinal { get; }
    public bool IsValid => _owner is not null && Ordinal >= 0;
    public bool Equals(TextureViewId other) => ReferenceEquals(_owner, other._owner) && Ordinal == other.Ordinal;
    public override bool Equals(object? obj) => obj is TextureViewId other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_owner, Ordinal);
    public static bool operator ==(TextureViewId left, TextureViewId right) => left.Equals(right);
    public static bool operator !=(TextureViewId left, TextureViewId right) => !left.Equals(right);
}

public enum ResourceEffect : byte
{
    Read,
    Write,
    ReadWrite,
}

public enum PriorContents : byte
{
    Discard,
    Required,
}

public enum WriteCoverage : byte
{
    Partial,
    Full,
}

public enum PassRecordingLane : byte
{
    Worker,
    Coordinator,
}

public enum BufferUse : byte
{
    CopySource,
    CopyDestination,
    ShaderRead,
    ShaderWrite,
    VertexOrConstant,
    Index,
    Indirect,
}

public enum TextureUse : byte
{
    CopySource,
    CopyDestination,
    ResolveSource,
    ResolveDestination,
    Sampled,
    Storage,
    ColorAttachment,
    DepthRead,
    DepthWrite,
}

public readonly record struct QueueSelection(QueueType First, QueueType? Second = null, QueueType? Third = null)
{
    public static QueueSelection Graphics => new(QueueType.Graphics);
    public static QueueSelection Compute => new(QueueType.Compute, QueueType.Graphics);
    public static QueueSelection Copy => new(QueueType.Copy, QueueType.Graphics);

    internal QueueType Select(DeviceCompilationSnapshot device)
    {
        if (device.Supports(First)) return First;
        if (Second is { } second && device.Supports(second)) return second;
        if (Third is { } third && device.Supports(third)) return third;
        throw new NotSupportedException("None of the pass's allowed queues are supported by the device.");
    }

    internal QueueType[] ToArray()
    {
        QueueType[] values = Third is not null ? new QueueType[3] : Second is not null ? new QueueType[2] : new QueueType[1];
        values[0] = First;
        if (Second is { } second) values[1] = second;
        if (Third is { } third) values[2] = third;
        if (values.Distinct().Count() != values.Length) throw new ArgumentException("Allowed queues must be unique.");
        return values;
    }

}

public readonly struct BufferAccess
{
    private readonly GraphToken? _owner;

    internal BufferAccess(GraphToken owner, int pass, int access, int resource, ResourceEffect effect, BufferUse use, BufferRange range)
    {
        _owner = owner;
        Pass = pass;
        Access = access;
        Resource = resource;
        Effect = effect;
        Use = use;
        Range = range;
    }

    internal GraphToken? Owner => _owner;
    internal int Pass { get; }
    internal int Access { get; }
    internal int Resource { get; }
    public ResourceEffect Effect { get; }
    public BufferUse Use { get; }
    public BufferRange Range { get; }
    public bool IsValid => _owner is not null && Pass >= 0 && Access >= 0 && Resource >= 0;
}

public readonly struct TextureAccess
{
    private readonly GraphToken? _owner;

    internal TextureAccess(GraphToken owner, int pass, int access, int resource, ResourceEffect effect, TextureUse use, TextureSubresourceRange range)
    {
        _owner = owner;
        Pass = pass;
        Access = access;
        Resource = resource;
        Effect = effect;
        Use = use;
        Range = range;
    }

    internal GraphToken? Owner => _owner;
    internal int Pass { get; }
    internal int Access { get; }
    internal int Resource { get; }
    public ResourceEffect Effect { get; }
    public TextureUse Use { get; }
    public TextureSubresourceRange Range { get; }
    public bool IsValid => _owner is not null && Pass >= 0 && Access >= 0 && Resource >= 0;
}

public readonly struct BufferViewAccess
{
    internal BufferViewAccess(BufferAccess resourceAccess, int view)
    {
        ResourceAccess = resourceAccess;
        View = view;
    }

    internal BufferAccess ResourceAccess { get; }
    internal int View { get; }
    public ResourceEffect Effect => ResourceAccess.Effect;
    public BufferUse Use => ResourceAccess.Use;
    public bool IsValid => ResourceAccess.IsValid && View >= 0;
}

public readonly struct TextureViewAccess
{
    internal TextureViewAccess(TextureAccess resourceAccess, int view)
    {
        ResourceAccess = resourceAccess;
        View = view;
    }

    internal TextureAccess ResourceAccess { get; }
    internal int View { get; }
    public ResourceEffect Effect => ResourceAccess.Effect;
    public TextureUse Use => ResourceAccess.Use;
    public bool IsValid => ResourceAccess.IsValid && View >= 0;
}

internal enum ShaderBindingAccessKind : byte
{
    BufferView,
    TextureView,
    ExternallyManaged,
}

/// <summary>
/// An opaque mapping from one shader descriptor-array element to an exact pass-local access token.
/// Instances can only be created by the <see cref="PassBuilder"/> that owns the access.
/// </summary>
public readonly struct ShaderBindingAccess
{
    private readonly GraphToken? _owner;

    internal ShaderBindingAccess(
        GraphToken owner,
        int pass,
        uint group,
        uint binding,
        uint element,
        ShaderBindingAccessKind kind,
        int access,
        int view)
    {
        _owner = owner;
        Pass = pass;
        Group = group;
        Binding = binding;
        Element = element;
        Kind = kind;
        Access = access;
        View = view;
    }

    internal GraphToken? Owner => _owner;
    internal int Pass { get; }
    internal ShaderBindingAccessKind Kind { get; }
    internal int Access { get; }
    internal int View { get; }
    public uint Group { get; }
    public uint Binding { get; }
    public uint Element { get; }
    public bool IsExternallyManaged => Kind == ShaderBindingAccessKind.ExternallyManaged;
    public bool IsValid => _owner is not null && Pass >= 0 &&
                           (Kind == ShaderBindingAccessKind.ExternallyManaged || (Access >= 0 && View >= 0));
}

public readonly struct ColorAttachmentAccess
{
    internal ColorAttachmentAccess(TextureViewAccess viewAccess, int slot, LoadAction load, Vector4 clearColor)
    {
        ViewAccess = viewAccess;
        Slot = slot;
        Load = load;
        ClearColor = clearColor;
    }

    internal TextureViewAccess ViewAccess { get; }
    public int Slot { get; }
    public LoadAction Load { get; }
    public Vector4 ClearColor { get; }
    public bool IsValid => ViewAccess.IsValid && Slot >= 0;
}

/// <summary>Per-invocation depth-plane operations. Store is compiler-owned.</summary>
public readonly record struct DepthAttachmentOps(
    LoadAction Load,
    bool ReadOnly = false,
    float ClearValue = 1f);

/// <summary>Per-invocation stencil-plane operations. Store is compiler-owned.</summary>
public readonly record struct StencilAttachmentOps(
    LoadAction Load,
    bool ReadOnly = false,
    byte ClearValue = 0);

public readonly struct DepthStencilAttachmentAccess
{
    internal DepthStencilAttachmentAccess(
        TextureViewId view,
        TextureAccess depthAccess,
        TextureAccess stencilAccess,
        bool hasDepth,
        bool hasStencil)
    {
        View = view;
        DepthAccess = depthAccess;
        StencilAccess = stencilAccess;
        HasDepth = hasDepth;
        HasStencil = hasStencil;
    }

    public TextureViewId View { get; }
    public TextureAccess DepthAccess { get; }
    public TextureAccess StencilAccess { get; }
    public bool HasDepth { get; }
    public bool HasStencil { get; }
    public bool IsValid => View.IsValid && (HasDepth || HasStencil) &&
                           (!HasDepth || DepthAccess.IsValid) &&
                           (!HasStencil || StencilAccess.IsValid);
}
