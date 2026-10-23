using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Instances;

namespace SomeEngine.Render.Features.Skinning;

/// <summary>Offsets of one mesh's source joints and inverse bind matrices in this render frame.</summary>
public readonly record struct RenderSkinBinding(uint JointOffset, uint InverseBindOffset) : IComponent;

/// <summary>Contributes the skin binding to the same instance storage as geometry and materials.</summary>
public readonly struct SkinInstanceProducer : IRenderInstanceProducer
{
    public static RenderInstancePropertyKey BindingKey { get; } = new("someengine.skin.binding");
    public static RenderInstancePropertyLayout Layout { get; } = BuildLayout();
    private static readonly ResolvedRenderInstanceProperty<RenderSkinBinding> Binding = Layout.Resolve<RenderSkinBinding>(BindingKey);

    public RenderInstancePropertyLayout Properties => Layout;

    public RenderInstanceChanges GetChanges(ReadOnlyQueryPacket packet, uint lastSystemVersion) =>
        packet.Has<RenderSkinBinding>() && packet.ChangedSince<RenderSkinBinding>(lastSystemVersion)
            ? RenderInstanceChanges.Values : RenderInstanceChanges.None;

    public void Bind(RenderInstanceWriteSlice destination) => destination.BindPerInstance(Binding);

    public void Write(RenderInstanceWriteSlice destination, ReadOnlyQueryPacket packet)
    {
        if (packet.TryRead<RenderSkinBinding>(out var bindings)) destination.Write(Binding, bindings);
        else
            for (int row = 0; row < packet.Count; row++) destination.Write(Binding, row, default(RenderSkinBinding));
    }

    private static RenderInstancePropertyLayout BuildLayout()
    {
        var builder = new RenderInstancePropertyLayoutBuilder();
        builder.Register<RenderSkinBinding>("SomeEngine.Render.Features.Skinning", BindingKey,
            new("someengine.skin.binding.uint2.v1", valueSize: 8, storageAlignment: 4, storageStride: 8, metadataWordCount: 1));
        return builder.Freeze();
    }
}
