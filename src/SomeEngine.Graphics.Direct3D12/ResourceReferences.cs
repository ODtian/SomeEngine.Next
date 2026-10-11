namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    private readonly record struct ResourceReferences(
        NativeLease? Lifetime,
        D3D12SparseState? SparseState,
        D3D12TextureResource? Texture,
        uint VisibleNodeMask)
    {
        internal static ResourceReferences FromBuffer(Buffer value)
        {
            D3D12Buffer buffer = RequireD3D12.Buffer(value);
            return new(buffer.NativeLifetime, buffer.SparseState, null, buffer.Info.VisibleNodeMask);
        }

        internal static ResourceReferences FromTexture(Texture value) =>
            FromTexture(RequireD3D12.Texture(value));

        internal static ResourceReferences FromTexture(D3D12TextureResource texture) =>
            new(texture.NativeLifetime, texture.SparseState, texture, texture.Info.VisibleNodeMask);
    }

    private static (ResourceReferences Primary, ResourceReferences Secondary)
        GetResourceReferences(GraphicsObject value)
    {
        // Construct the tuple once to avoid per-arm GC-containing temporaries in NativeAOT.
        ResourceReferences primary = default;
        ResourceReferences secondary = default;
        switch (value)
        {
            case Buffer buffer:
                primary = ResourceReferences.FromBuffer(buffer);
                break;
            case BufferCbv view:
                primary = ResourceReferences.FromBuffer(view.Resource);
                break;
            case BufferSrv view:
                primary = ResourceReferences.FromBuffer(view.Resource);
                break;
            case BufferUav view:
                primary = ResourceReferences.FromBuffer(view.Resource);
                if (view.Description.CounterBuffer is Buffer counter)
                    secondary = ResourceReferences.FromBuffer(counter);
                break;
            case D3D12SamplerFeedbackUav view:
                primary = ResourceReferences.FromTexture(view.FeedbackResource);
                secondary = ResourceReferences.FromTexture(view.SampledResource);
                break;
            case TextureSrv view:
                primary = ResourceReferences.FromTexture(view.Resource);
                break;
            case TextureUav view:
                primary = ResourceReferences.FromTexture(view.Resource);
                break;
            case ColorAttachmentView view:
                primary = ResourceReferences.FromTexture(view.Resource);
                break;
            case DepthStencilView view:
                primary = ResourceReferences.FromTexture(view.Resource);
                break;
            case AccelerationStructureSrv view:
                (primary, secondary) = GetAccelerationStructureReferences(view);
                break;
        }
        return (primary, secondary);
    }

    private static (ResourceReferences, ResourceReferences)
        GetAccelerationStructureReferences(AccelerationStructureSrv view)
    {
        var structure = RequireD3D12.AccelerationStructure(view.Resource);
        return (new(structure.NativeLifetime, null, null, structure.Storage.Info.VisibleNodeMask),
            ResourceReferences.FromBuffer(structure.Storage));
    }
}
