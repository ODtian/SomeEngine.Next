namespace SomeEngine.RenderGraph;

internal sealed class MaterializedBufferView : IDisposable
{
    internal MaterializedBufferView(GraphBindingType type, DeviceResource view)
    {
        Type = type;
        View = view ?? throw new ArgumentNullException(nameof(view));
    }

    internal GraphBindingType Type { get; }
    internal DeviceResource View { get; }

    internal BufferCbv ConstantBuffer => View as BufferCbv ??
        throw new InvalidOperationException("The graph view is not a constant-buffer view.");

    internal BufferSrv ReadOnlyBuffer => View as BufferSrv ??
        throw new InvalidOperationException("The graph view is not a buffer SRV.");

    internal BufferUav StorageBuffer => View as BufferUav ??
        throw new InvalidOperationException("The graph view is not a buffer UAV.");

    internal ResourceBinding ToBinding() => Type switch
    {
        GraphBindingType.ConstantBuffer => ResourceBinding.ConstantBuffer(ConstantBuffer),
        GraphBindingType.ReadOnlyBuffer => ResourceBinding.ReadOnlyBuffer(ReadOnlyBuffer),
        GraphBindingType.StorageBuffer => ResourceBinding.WritableBuffer(StorageBuffer),
        _ => throw new InvalidOperationException($"Graph binding type {Type} is not a buffer view."),
    };

    public void Dispose() => View.Dispose();
}

internal sealed class MaterializedTextureView : IDisposable
{
    internal TextureSrv? ShaderResource { get; init; }
    internal TextureUav? Storage { get; init; }
    internal ColorAttachmentView? ColorAttachment { get; init; }
    internal DepthStencilView? ReadOnlyDepthStencilAttachment { get; init; }
    internal DepthStencilView? WritableDepthStencilAttachment { get; init; }

    internal ResourceBinding ToBinding(GraphBindingType type) => type switch
    {
        GraphBindingType.SampledTexture => ResourceBinding.SampledTexture(
            ShaderResource ?? throw new InvalidOperationException("The graph view has no texture SRV.")),
        GraphBindingType.StorageTexture => ResourceBinding.StorageTexture(
            Storage ?? throw new InvalidOperationException("The graph view has no texture UAV.")),
        _ => throw new InvalidOperationException($"Graph binding type {type} is not a texture view."),
    };

    public void Dispose()
    {
        ShaderResource?.Dispose();
        Storage?.Dispose();
        ColorAttachment?.Dispose();
        ReadOnlyDepthStencilAttachment?.Dispose();
        WritableDepthStencilAttachment?.Dispose();
    }
}
