namespace SomeEngine.RenderGraph;

internal readonly struct GraphIdentity : IEquatable<GraphIdentity>
{
    internal GraphIdentity(ulong owner, int slot, uint generation)
    {
        Owner = owner;
        Slot = slot;
        Generation = generation;
    }

    internal ulong Owner { get; }
    internal int Slot { get; }
    internal uint Generation { get; }
    internal bool IsValid => Owner != 0 && Slot >= 0 && Generation != 0;

    public bool Equals(GraphIdentity other) =>
        Owner == other.Owner && Slot == other.Slot && Generation == other.Generation;

    public override bool Equals(object? value) => value is GraphIdentity other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Owner, Slot, Generation);
    public static bool operator ==(GraphIdentity left, GraphIdentity right) => left.Equals(right);
    public static bool operator !=(GraphIdentity left, GraphIdentity right) => !left.Equals(right);
}

public readonly struct GraphPassId : IEquatable<GraphPassId>
{
    private readonly GraphIdentity _value;
    internal GraphPassId(in GraphIdentity value) => _value = value;
    internal GraphIdentity Value => _value;
    public bool Equals(GraphPassId other) => _value.Equals(other._value);
    public override bool Equals(object? value) => value is GraphPassId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(GraphPassId left, GraphPassId right) => left.Equals(right);
    public static bool operator !=(GraphPassId left, GraphPassId right) => !left.Equals(right);
}

public readonly struct GraphExtensionPointId : IEquatable<GraphExtensionPointId>
{
    private readonly GraphIdentity _value;
    internal GraphExtensionPointId(in GraphIdentity value) => _value = value;
    internal GraphIdentity Value => _value;
    public bool Equals(GraphExtensionPointId other) => _value.Equals(other._value);
    public override bool Equals(object? value) => value is GraphExtensionPointId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(GraphExtensionPointId left, GraphExtensionPointId right) => left.Equals(right);
    public static bool operator !=(GraphExtensionPointId left, GraphExtensionPointId right) => !left.Equals(right);
}

public readonly struct GraphBufferId : IEquatable<GraphBufferId>
{
    private readonly GraphIdentity _value;
    internal GraphBufferId(in GraphIdentity value) => _value = value;
    internal GraphIdentity Value => _value;
    public bool Equals(GraphBufferId other) => _value.Equals(other._value);
    public override bool Equals(object? value) => value is GraphBufferId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(GraphBufferId left, GraphBufferId right) => left.Equals(right);
    public static bool operator !=(GraphBufferId left, GraphBufferId right) => !left.Equals(right);
}

public readonly struct GraphTextureId : IEquatable<GraphTextureId>
{
    private readonly GraphIdentity _value;
    internal GraphTextureId(in GraphIdentity value) => _value = value;
    internal GraphIdentity Value => _value;
    public bool Equals(GraphTextureId other) => _value.Equals(other._value);
    public override bool Equals(object? value) => value is GraphTextureId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(GraphTextureId left, GraphTextureId right) => left.Equals(right);
    public static bool operator !=(GraphTextureId left, GraphTextureId right) => !left.Equals(right);
}

public readonly struct GraphQueryPoolId : IEquatable<GraphQueryPoolId>
{
    private readonly GraphIdentity _value;
    internal GraphQueryPoolId(in GraphIdentity value) => _value = value;
    internal GraphIdentity Value => _value;
    public bool Equals(GraphQueryPoolId other) => _value.Equals(other._value);
    public override bool Equals(object? value) => value is GraphQueryPoolId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(GraphQueryPoolId left, GraphQueryPoolId right) => left.Equals(right);
    public static bool operator !=(GraphQueryPoolId left, GraphQueryPoolId right) => !left.Equals(right);
}

public readonly struct GraphRayTracingShaderTableId : IEquatable<GraphRayTracingShaderTableId>
{
    private readonly GraphIdentity _value;
    internal GraphRayTracingShaderTableId(in GraphIdentity value) => _value = value;
    internal GraphIdentity Value => _value;
    public bool Equals(GraphRayTracingShaderTableId other) => _value.Equals(other._value);
    public override bool Equals(object? value) => value is GraphRayTracingShaderTableId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(GraphRayTracingShaderTableId left, GraphRayTracingShaderTableId right) => left.Equals(right);
    public static bool operator !=(GraphRayTracingShaderTableId left, GraphRayTracingShaderTableId right) => !left.Equals(right);
}

public readonly struct GraphPersistentParameterBindingsId : IEquatable<GraphPersistentParameterBindingsId>
{
    private readonly GraphIdentity _value;
    internal GraphPersistentParameterBindingsId(in GraphIdentity value) => _value = value;
    internal GraphIdentity Value => _value;
    public bool Equals(GraphPersistentParameterBindingsId other) => _value.Equals(other._value);
    public override bool Equals(object? value) => value is GraphPersistentParameterBindingsId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(GraphPersistentParameterBindingsId left, GraphPersistentParameterBindingsId right) => left.Equals(right);
    public static bool operator !=(GraphPersistentParameterBindingsId left, GraphPersistentParameterBindingsId right) => !left.Equals(right);
}

public readonly struct GraphBufferCbvId : IEquatable<GraphBufferCbvId>
{
    private readonly GraphIdentity _value;
    internal GraphBufferCbvId(in GraphIdentity value) => _value = value;
    internal GraphIdentity Value => _value;
    public bool Equals(GraphBufferCbvId other) => _value.Equals(other._value);
    public override bool Equals(object? value) => value is GraphBufferCbvId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(GraphBufferCbvId left, GraphBufferCbvId right) => left.Equals(right);
    public static bool operator !=(GraphBufferCbvId left, GraphBufferCbvId right) => !left.Equals(right);
}

public readonly struct GraphBufferSrvId : IEquatable<GraphBufferSrvId>
{
    private readonly GraphIdentity _value;
    internal GraphBufferSrvId(in GraphIdentity value) => _value = value;
    internal GraphIdentity Value => _value;
    public bool Equals(GraphBufferSrvId other) => _value.Equals(other._value);
    public override bool Equals(object? value) => value is GraphBufferSrvId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(GraphBufferSrvId left, GraphBufferSrvId right) => left.Equals(right);
    public static bool operator !=(GraphBufferSrvId left, GraphBufferSrvId right) => !left.Equals(right);
}

public readonly struct GraphBufferUavId : IEquatable<GraphBufferUavId>
{
    private readonly GraphIdentity _value;
    internal GraphBufferUavId(in GraphIdentity value) => _value = value;
    internal GraphIdentity Value => _value;
    public bool Equals(GraphBufferUavId other) => _value.Equals(other._value);
    public override bool Equals(object? value) => value is GraphBufferUavId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(GraphBufferUavId left, GraphBufferUavId right) => left.Equals(right);
    public static bool operator !=(GraphBufferUavId left, GraphBufferUavId right) => !left.Equals(right);
}

public readonly struct GraphTextureSrvId : IEquatable<GraphTextureSrvId>
{
    private readonly GraphIdentity _value;
    internal GraphTextureSrvId(in GraphIdentity value) => _value = value;
    internal GraphIdentity Value => _value;
    public bool Equals(GraphTextureSrvId other) => _value.Equals(other._value);
    public override bool Equals(object? value) => value is GraphTextureSrvId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(GraphTextureSrvId left, GraphTextureSrvId right) => left.Equals(right);
    public static bool operator !=(GraphTextureSrvId left, GraphTextureSrvId right) => !left.Equals(right);
}

public readonly struct GraphTextureUavId : IEquatable<GraphTextureUavId>
{
    private readonly GraphIdentity _value;
    internal GraphTextureUavId(in GraphIdentity value) => _value = value;
    internal GraphIdentity Value => _value;
    public bool Equals(GraphTextureUavId other) => _value.Equals(other._value);
    public override bool Equals(object? value) => value is GraphTextureUavId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(GraphTextureUavId left, GraphTextureUavId right) => left.Equals(right);
    public static bool operator !=(GraphTextureUavId left, GraphTextureUavId right) => !left.Equals(right);
}

public readonly struct GraphColorAttachmentViewId : IEquatable<GraphColorAttachmentViewId>
{
    private readonly GraphIdentity _value;
    internal GraphColorAttachmentViewId(in GraphIdentity value) => _value = value;
    internal GraphIdentity Value => _value;
    public bool Equals(GraphColorAttachmentViewId other) => _value.Equals(other._value);
    public override bool Equals(object? value) => value is GraphColorAttachmentViewId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(GraphColorAttachmentViewId left, GraphColorAttachmentViewId right) => left.Equals(right);
    public static bool operator !=(GraphColorAttachmentViewId left, GraphColorAttachmentViewId right) => !left.Equals(right);
}

public readonly struct GraphDepthStencilViewId : IEquatable<GraphDepthStencilViewId>
{
    private readonly GraphIdentity _value;
    internal GraphDepthStencilViewId(in GraphIdentity value) => _value = value;
    internal GraphIdentity Value => _value;
    public bool Equals(GraphDepthStencilViewId other) => _value.Equals(other._value);
    public override bool Equals(object? value) => value is GraphDepthStencilViewId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(GraphDepthStencilViewId left, GraphDepthStencilViewId right) => left.Equals(right);
    public static bool operator !=(GraphDepthStencilViewId left, GraphDepthStencilViewId right) => !left.Equals(right);
}

public readonly struct GraphBufferAccessId : IEquatable<GraphBufferAccessId>
{
    private readonly GraphIdentity _value;
    internal GraphBufferAccessId(in GraphIdentity value) => _value = value;
    internal GraphIdentity Value => _value;
    public bool Equals(GraphBufferAccessId other) => _value.Equals(other._value);
    public override bool Equals(object? value) => value is GraphBufferAccessId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(GraphBufferAccessId left, GraphBufferAccessId right) => left.Equals(right);
    public static bool operator !=(GraphBufferAccessId left, GraphBufferAccessId right) => !left.Equals(right);
}

public readonly struct GraphTextureAccessId : IEquatable<GraphTextureAccessId>
{
    private readonly GraphIdentity _value;
    internal GraphTextureAccessId(in GraphIdentity value) => _value = value;
    internal GraphIdentity Value => _value;
    public bool Equals(GraphTextureAccessId other) => _value.Equals(other._value);
    public override bool Equals(object? value) => value is GraphTextureAccessId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(GraphTextureAccessId left, GraphTextureAccessId right) => left.Equals(right);
    public static bool operator !=(GraphTextureAccessId left, GraphTextureAccessId right) => !left.Equals(right);
}

public readonly struct PassRenderingRegionId : IEquatable<PassRenderingRegionId>
{
    private readonly GraphIdentity _value;
    internal PassRenderingRegionId(in GraphIdentity value) => _value = value;
    internal GraphIdentity Value => _value;
    public bool Equals(PassRenderingRegionId other) => _value.Equals(other._value);
    public override bool Equals(object? value) => value is PassRenderingRegionId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(PassRenderingRegionId left, PassRenderingRegionId right) => left.Equals(right);
    public static bool operator !=(PassRenderingRegionId left, PassRenderingRegionId right) => !left.Equals(right);
}

