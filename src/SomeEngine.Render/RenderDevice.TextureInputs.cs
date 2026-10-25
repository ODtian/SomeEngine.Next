using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using EngineTexture = SomeEngine.Resources.Texture;
using RhiTexture = SomeEngine.Graphics.Texture;

namespace SomeEngine.Render;

public sealed partial class RenderDevice
{
    public TextureSrv PrepareTextureSrv(ref RenderGraphFrame frame, GraphParameterInput input, EngineTexture texture) =>
        PrepareTextureForInputs(ref frame, [input], texture, out _).Srv!;

    public readonly record struct TextureViews(RhiTexture Texture, TextureSrv? Srv, TextureUav? Uav)
    {
        /// <summary>
        /// Supplies these prepared views to a group of texture inputs. Persistent inputs must
        /// refer to a registered backing. An unchanged persistent group preserves frame-local
        /// overrides; forceRebind explicitly replaces them after connection membership changes.
        /// </summary>
        public void BindInputs(ref RenderGraphFrame frame, scoped ReadOnlySpan<GraphParameterInput> inputs,
            bool persistent, bool forceRebind = false)
        {
            if (persistent && !forceRebind)
            {
                bool unchanged = true;
                foreach (GraphParameterInput input in inputs)
                {
                    DeviceResource? view = input.Type == ResourceBindingType.TextureSrv ? Srv : Uav;
                    if (!frame.TryGetPersistentParameterInput(input, out ResourceBinding binding) ||
                        binding.Type != input.Type || !ReferenceEquals(binding.Value, view))
                    {
                        unchanged = false;
                        break;
                    }
                }
                if (unchanged) return;
            }
            foreach (GraphParameterInput input in inputs)
            {
                ResourceBinding binding = input.Type == ResourceBindingType.TextureSrv
                    ? ResourceBinding.SampledTexture(Srv!) : ResourceBinding.StorageTexture(Uav!);
                if (persistent) frame.SetPersistentParameterInput(input, binding);
                else frame.SetParameterInput(input, binding);
            }
        }
    }

    public TextureViews PrepareTextureForInputs(ref RenderGraphFrame frame,
        scoped ReadOnlySpan<GraphParameterInput> inputs, EngineTexture asset) =>
        PrepareTextureForInputs(ref frame, inputs, asset, out _);

    public TextureViews PrepareTextureForInputs(ref RenderGraphFrame frame,
        scoped ReadOnlySpan<GraphParameterInput> inputs, EngineTexture asset, out bool hasFrameUploads)
    {
        RhiTexture texture = PrepareTexture(ref frame, ReadOnlySpan<GraphPassId>.Empty, asset);
        TextureRealization realization = GetOrCreateTexture(asset);
        OrderTextureInputs(ref frame, realization, inputs);
        hasFrameUploads = realization.HasUploadTransaction;
        TextureSrv? srv = null;
        TextureUav? uav = null;
        foreach (GraphParameterInput input in inputs)
            if (input.Type == ResourceBindingType.TextureSrv) srv ??= GetTextureSrv(asset);
            else if (input.Type == ResourceBindingType.TextureUav)
                uav ??= GetTextureUav(asset, realization.Shape.ResidentRange(realization.FirstResidentMip));
        return new(texture, srv, uav);
    }

    public TextureSrv PrepareMissingTextureForInputs(ref RenderGraphFrame frame,
        scoped ReadOnlySpan<GraphParameterInput> inputs) =>
        PrepareMissingTextureForInputs(ref frame, inputs, out _);

    public TextureSrv PrepareMissingTextureForInputs(ref RenderGraphFrame frame,
        scoped ReadOnlySpan<GraphParameterInput> inputs, out bool hasFrameUploads)
    {
        _ = PrepareMissingTexture(ref frame, ReadOnlySpan<GraphPassId>.Empty);
        TextureRealization texture = RequireMissingTexture();
        OrderTextureInputs(ref frame, texture, inputs);
        hasFrameUploads = texture.HasUploadTransaction;
        TextureSrv view = MissingTextureSrv;
        UpdateSampledDescriptor(texture, view);
        return view;
    }

    private static void OrderTextureInputs(ref RenderGraphFrame frame, TextureRealization texture,
        scoped ReadOnlySpan<GraphParameterInput> inputs)
    {
        if (texture.UploadPass == default) return;
        foreach (GraphParameterInput input in inputs)
        {
            frame.OrderAfter(input, texture.UploadPass);
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
