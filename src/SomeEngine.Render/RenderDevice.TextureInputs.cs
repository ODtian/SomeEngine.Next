using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using EngineTexture = SomeEngine.Resources.Texture;
using RhiTexture = SomeEngine.Graphics.Texture;

namespace SomeEngine.Render;

public sealed partial class RenderDevice
{
    public TextureSrv PrepareTextureSrv(ref RenderGraphFrame frame, GraphParameterInput input, EngineTexture texture) =>
        PrepareTextureForInputs(ref frame, [input], texture, out _).Srv!;

    public readonly record struct TextureViews(RhiTexture Texture, TextureSrv? Srv, TextureUav? Uav);

    public TextureViews PrepareTextureForInputs(ref RenderGraphFrame frame,
        scoped ReadOnlySpan<GraphParameterInput> inputs, EngineTexture asset, out bool hasFrameUploads)
    {
        RhiTexture texture = PrepareTexture(ref frame, ReadOnlySpan<GraphPassId>.Empty, asset);
        TextureRealization realization = GetOrCreateTexture(asset);
        OrderTextureInputs(ref frame, realization, inputs);
        hasFrameUploads = realization.UploadPasses.Count != 0;
        TextureSrv? srv = null;
        TextureUav? uav = null;
        foreach (GraphParameterInput input in inputs)
            if (input.Type == ResourceBindingType.TextureSrv) srv ??= GetTextureSrv(asset);
            else if (input.Type == ResourceBindingType.TextureUav)
                uav ??= GetTextureUav(asset, realization.Shape.ResidentRange(realization.FirstResidentMip));
        return new(texture, srv, uav);
    }

    public TextureSrv PrepareMissingTextureForInputs(ref RenderGraphFrame frame,
        scoped ReadOnlySpan<GraphParameterInput> inputs, out bool hasFrameUploads)
    {
        _ = PrepareMissingTexture(ref frame, ReadOnlySpan<GraphPassId>.Empty);
        TextureRealization texture = RequireMissingTexture();
        OrderTextureInputs(ref frame, texture, inputs);
        hasFrameUploads = texture.UploadPasses.Count != 0;
        TextureSrv view = MissingTextureSrv;
        UpdateSampledDescriptor(texture, view);
        return view;
    }

    private static void OrderTextureInputs(ref RenderGraphFrame frame, TextureRealization texture,
        scoped ReadOnlySpan<GraphParameterInput> inputs)
    {
        if (texture.UploadPasses.Count == 0) return;
        foreach (GraphParameterInput input in inputs)
        {
            foreach (GraphPassId upload in texture.UploadPasses) frame.OrderAfter(input, upload);
            frame.OrderAfter(texture.FinalizationPass, input);
        }
    }

    public uint GetTextureDescriptorIndex(EngineTexture? asset)
    {
        ThrowIfDisposed();
        TextureRealization texture = asset is null ? RequireMissingTexture() : GetOrCreateTexture(asset);
        if (texture.SampledDescriptor is null)
        {
            TextureShape shape = texture.Shape;
            texture.SampledDescriptor = _backend.CreateDescriptorTable(_device,
                [new DescriptorSlotDesc(ResourceBindingType.TextureSrv, shape.SampledFormat,
                    TextureDimension: shape.SampledDimension)], "Material sampled texture");
            texture.SampledIndex = _backend.GetDescriptorIndex(texture.SampledDescriptor, 0).Value;
            if (texture.FirstResidentMip < shape.MipLevelCount)
                UpdateSampledDescriptor(texture, asset is null ? MissingTextureSrv : GetTextureSrv(asset));
        }
        return texture.SampledIndex;
    }

    private void UpdateSampledDescriptor(TextureRealization texture, TextureSrv view)
    {
        if (texture.SampledDescriptor is null || ReferenceEquals(texture.SampledView, view)) return;
        _backend.WriteDescriptor(texture.SampledDescriptor, 0, ResourceBinding.SampledTexture(view));
        texture.SampledView = view;
    }
}
