namespace SomeEngine.RenderGraph;

[Flags]
public enum GraphAccess : byte
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    ReadWrite = Read | Write,
    Discard = 1 << 2,
    WriteAll = Write | Discard,
}

public enum GraphResourceUsage : byte
{
    Common,
    Undefined,
    Present,
    VertexOrConstantBuffer,
    IndexBuffer,
    RenderTarget,
    UnorderedAccess,
    DepthRead,
    DepthWrite,
    DepthReadShaderResource,
    ShaderResource,
    IndirectArgument,
    CopySource,
    CopyDestination,
    ResolveSource,
    ResolveDestination,
    AccelerationStructure,
    ShadingRateSource,
}

public enum GraphBindingType : byte
{
    ConstantBuffer,
    ReadOnlyBuffer,
    StorageBuffer,
    SampledTexture,
    StorageTexture,
    Sampler,
    AccelerationStructure,
}

[Flags]
public enum GraphTextureViewUsage : byte
{
    None = 0,
    ShaderResource = 1 << 0,
    Storage = 1 << 1,
    ColorAttachment = 1 << 2,
    DepthStencilAttachment = 1 << 3,
    ResolveDestination = 1 << 4,
}

internal enum GraphTextureAspect : byte
{
    Color,
    Depth,
    Stencil,
}

internal enum GraphBarrierKind : byte
{
    Resource,
    QueueRelease,
    QueueAcquire,
}

public readonly struct BufferHandle : IEquatable<BufferHandle>
{
    private readonly long _graph;
    private readonly int _ordinal;

    internal BufferHandle(long graph, int ordinal)
    {
        if (graph == 0) throw new ArgumentOutOfRangeException(nameof(graph));
        if (ordinal < 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
        _graph = graph;
        _ordinal = ordinal;
    }

    internal long Graph => _graph;
    internal int Ordinal => _ordinal;
    public bool IsValid => _graph != 0;
    public bool Equals(BufferHandle other) =>
        _graph == other._graph && _ordinal == other._ordinal;
    public override bool Equals(object? obj) => obj is BufferHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_graph, _ordinal);
    public static bool operator ==(BufferHandle left, BufferHandle right) => left.Equals(right);
    public static bool operator !=(BufferHandle left, BufferHandle right) => !left.Equals(right);
}

public readonly struct TextureHandle : IEquatable<TextureHandle>
{
    private readonly long _graph;
    private readonly int _ordinal;

    internal TextureHandle(long graph, int ordinal)
    {
        if (graph == 0) throw new ArgumentOutOfRangeException(nameof(graph));
        if (ordinal < 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
        _graph = graph;
        _ordinal = ordinal;
    }

    internal long Graph => _graph;
    internal int Ordinal => _ordinal;
    public bool IsValid => _graph != 0;
    public bool Equals(TextureHandle other) =>
        _graph == other._graph && _ordinal == other._ordinal;
    public override bool Equals(object? obj) => obj is TextureHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_graph, _ordinal);
    public static bool operator ==(TextureHandle left, TextureHandle right) => left.Equals(right);
    public static bool operator !=(TextureHandle left, TextureHandle right) => !left.Equals(right);
}

public readonly struct BufferViewHandle : IEquatable<BufferViewHandle>
{
    private readonly long _graph;
    private readonly int _ordinal;

    internal BufferViewHandle(long graph, int ordinal)
    {
        if (graph == 0) throw new ArgumentOutOfRangeException(nameof(graph));
        if (ordinal < 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
        _graph = graph;
        _ordinal = ordinal;
    }

    internal long Graph => _graph;
    internal int Ordinal => _ordinal;
    public bool IsValid => _graph != 0;
    public bool Equals(BufferViewHandle other) =>
        _graph == other._graph && _ordinal == other._ordinal;
    public override bool Equals(object? obj) => obj is BufferViewHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_graph, _ordinal);
    public static bool operator ==(BufferViewHandle left, BufferViewHandle right) => left.Equals(right);
    public static bool operator !=(BufferViewHandle left, BufferViewHandle right) => !left.Equals(right);
}

public readonly struct TextureViewHandle : IEquatable<TextureViewHandle>
{
    private readonly long _graph;
    private readonly int _ordinal;

    internal TextureViewHandle(long graph, int ordinal)
    {
        if (graph == 0) throw new ArgumentOutOfRangeException(nameof(graph));
        if (ordinal < 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
        _graph = graph;
        _ordinal = ordinal;
    }

    internal long Graph => _graph;
    internal int Ordinal => _ordinal;
    public bool IsValid => _graph != 0;
    public bool Equals(TextureViewHandle other) =>
        _graph == other._graph && _ordinal == other._ordinal;
    public override bool Equals(object? obj) => obj is TextureViewHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_graph, _ordinal);
    public static bool operator ==(TextureViewHandle left, TextureViewHandle right) => left.Equals(right);
    public static bool operator !=(TextureViewHandle left, TextureViewHandle right) => !left.Equals(right);
}

public readonly struct AccelerationStructureHandle : IEquatable<AccelerationStructureHandle>
{
    private readonly long _graph;
    private readonly int _ordinal;

    internal AccelerationStructureHandle(long graph, int ordinal)
    {
        if (graph == 0) throw new ArgumentOutOfRangeException(nameof(graph));
        if (ordinal < 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
        _graph = graph;
        _ordinal = ordinal;
    }

    internal long Graph => _graph;
    internal int Ordinal => _ordinal;
    public bool IsValid => _graph != 0;
    public bool Equals(AccelerationStructureHandle other) =>
        _graph == other._graph && _ordinal == other._ordinal;
    public override bool Equals(object? obj) => obj is AccelerationStructureHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_graph, _ordinal);
    public static bool operator ==(AccelerationStructureHandle left, AccelerationStructureHandle right) => left.Equals(right);
    public static bool operator !=(AccelerationStructureHandle left, AccelerationStructureHandle right) => !left.Equals(right);
}

public readonly struct SamplerHandle : IEquatable<SamplerHandle>
{
    private readonly long _graph;
    private readonly int _ordinal;

    internal SamplerHandle(long graph, int ordinal)
    {
        if (graph == 0) throw new ArgumentOutOfRangeException(nameof(graph));
        if (ordinal < 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
        _graph = graph;
        _ordinal = ordinal;
    }

    internal long Graph => _graph;
    internal int Ordinal => _ordinal;
    public bool IsValid => _graph != 0;
    public bool Equals(SamplerHandle other) =>
        _graph == other._graph && _ordinal == other._ordinal;
    public override bool Equals(object? obj) => obj is SamplerHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_graph, _ordinal);
    public static bool operator ==(SamplerHandle left, SamplerHandle right) => left.Equals(right);
    public static bool operator !=(SamplerHandle left, SamplerHandle right) => !left.Equals(right);
}

public readonly struct DescriptorTableHandle : IEquatable<DescriptorTableHandle>
{
    private readonly long _graph;
    private readonly int _ordinal;

    internal DescriptorTableHandle(long graph, int ordinal)
    {
        if (graph == 0) throw new ArgumentOutOfRangeException(nameof(graph));
        if (ordinal < 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
        _graph = graph;
        _ordinal = ordinal;
    }

    internal long Graph => _graph;
    internal int Ordinal => _ordinal;
    public bool IsValid => _graph != 0;
    public bool Equals(DescriptorTableHandle other) =>
        _graph == other._graph && _ordinal == other._ordinal;
    public override bool Equals(object? obj) => obj is DescriptorTableHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_graph, _ordinal);
    public static bool operator ==(DescriptorTableHandle left, DescriptorTableHandle right) => left.Equals(right);
    public static bool operator !=(DescriptorTableHandle left, DescriptorTableHandle right) => !left.Equals(right);
}

public readonly struct QueryPoolHandle : IEquatable<QueryPoolHandle>
{
    private readonly long _graph;
    private readonly int _ordinal;

    internal QueryPoolHandle(long graph, int ordinal)
    {
        if (graph == 0) throw new ArgumentOutOfRangeException(nameof(graph));
        if (ordinal < 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
        _graph = graph;
        _ordinal = ordinal;
    }

    internal long Graph => _graph;
    internal int Ordinal => _ordinal;
    public bool IsValid => _graph != 0;
    public bool Equals(QueryPoolHandle other) =>
        _graph == other._graph && _ordinal == other._ordinal;
    public override bool Equals(object? obj) => obj is QueryPoolHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_graph, _ordinal);
    public static bool operator ==(QueryPoolHandle left, QueryPoolHandle right) => left.Equals(right);
    public static bool operator !=(QueryPoolHandle left, QueryPoolHandle right) => !left.Equals(right);
}

[Flags]
public enum PassFlags : byte
{
    None = 0,
    NeverCull = 1 << 0,
    NeverParallel = 1 << 1,
    NeverMerge = 1 << 2,
}
