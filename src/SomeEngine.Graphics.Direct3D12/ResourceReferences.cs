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
        GetResourceReferences(GraphicsObject value) => value switch
        {
            Buffer buffer => (ResourceReferences.FromBuffer(buffer), default),
            BufferCbv view => (ResourceReferences.FromBuffer(view.Resource), default),
            BufferSrv view => (ResourceReferences.FromBuffer(view.Resource), default),
            BufferUav view => (
                ResourceReferences.FromBuffer(view.Resource),
                view.Description.CounterBuffer is Buffer counter
                    ? ResourceReferences.FromBuffer(counter) : default),
            D3D12SamplerFeedbackUav view => (
                ResourceReferences.FromTexture(view.FeedbackResource),
                ResourceReferences.FromTexture(view.SampledResource)),
            TextureSrv view => (ResourceReferences.FromTexture(view.Resource), default),
            TextureUav view => (ResourceReferences.FromTexture(view.Resource), default),
            ColorAttachmentView view => (ResourceReferences.FromTexture(view.Resource), default),
            DepthStencilView view => (ResourceReferences.FromTexture(view.Resource), default),
            AccelerationStructureSrv view => GetAccelerationStructureReferences(view),
            _ => default,
        };

    private static (ResourceReferences, ResourceReferences)
        GetAccelerationStructureReferences(AccelerationStructureSrv view)
    {
        var structure = RequireD3D12.AccelerationStructure(view.Resource);
        return (new(structure.NativeLifetime, null, null, structure.Storage.Info.VisibleNodeMask),
            ResourceReferences.FromBuffer(structure.Storage));
    }
}
