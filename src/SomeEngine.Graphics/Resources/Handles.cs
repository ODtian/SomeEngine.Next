namespace SomeEngine.Graphics;

public readonly record struct HeapHandle(DeviceDomain Domain, uint Slot, uint Generation)
{
    public bool IsValid => Domain.IsValid && Slot != 0 && Generation != 0;
}

public readonly record struct BufferHandle(DeviceDomain Domain, uint Slot, uint Generation)
{
    public bool IsValid => Domain.IsValid && Slot != 0 && Generation != 0;
    public ResourceHandle Resource => new(Domain, ResourceKind.Buffer, Slot, Generation);
}

public readonly record struct TextureHandle(DeviceDomain Domain, uint Slot, uint Generation)
{
    public bool IsValid => Domain.IsValid && Slot != 0 && Generation != 0;
    public ResourceHandle Resource => new(Domain, ResourceKind.Texture, Slot, Generation);
}

public readonly record struct TextureViewHandle(DeviceDomain Domain, uint Slot, uint Generation)
{
    public bool IsValid => Domain.IsValid && Slot != 0 && Generation != 0;
}

public readonly record struct BufferViewHandle(DeviceDomain Domain, uint Slot, uint Generation)
{
    public bool IsValid => Domain.IsValid && Slot != 0 && Generation != 0;
}

public readonly record struct SamplerHandle(DeviceDomain Domain, uint Slot, uint Generation)
{
    public bool IsValid => Domain.IsValid && Slot != 0 && Generation != 0;
}

public readonly record struct BindGroupLayoutHandle(DeviceDomain Domain, uint Slot, uint Generation)
{
    public bool IsValid => Domain.IsValid && Slot != 0 && Generation != 0;
}

public readonly record struct BindGroupHandle(DeviceDomain Domain, uint Slot, uint Generation)
{
    public bool IsValid => Domain.IsValid && Slot != 0 && Generation != 0;
}

public readonly record struct ShaderHandle(DeviceDomain Domain, uint Slot, uint Generation)
{
    public bool IsValid => Domain.IsValid && Slot != 0 && Generation != 0;
}

public readonly record struct PipelineLayoutHandle(DeviceDomain Domain, uint Slot, uint Generation)
{
    public bool IsValid => Domain.IsValid && Slot != 0 && Generation != 0;
}

public readonly record struct PipelineHandle(DeviceDomain Domain, uint Slot, uint Generation)
{
    public bool IsValid => Domain.IsValid && Slot != 0 && Generation != 0;
}

public readonly record struct CommandListHandle(DeviceDomain Domain, uint Slot, uint Generation)
{
    public bool IsValid => Domain.IsValid && Slot != 0 && Generation != 0;
}

public readonly record struct QueryPoolHandle(DeviceDomain Domain, uint Slot, uint Generation)
{
    public bool IsValid => Domain.IsValid && Slot != 0 && Generation != 0;
}

public readonly record struct SwapchainHandle(DeviceDomain Domain, uint Slot, uint Generation)
{
    public bool IsValid => Domain.IsValid && Slot != 0 && Generation != 0;
}

public readonly record struct BindlessTableHandle(DeviceDomain Domain, uint Slot, uint Generation)
{
    public bool IsValid => Domain.IsValid && Slot != 0 && Generation != 0;
}

public enum ResourceKind : byte
{
    Buffer,
    Texture,
}

public readonly record struct ResourceHandle(DeviceDomain Domain, ResourceKind Kind, uint Slot, uint Generation)
{
    public bool IsValid => Domain.IsValid && Enum.IsDefined(Kind) && Slot != 0 && Generation != 0;
}
