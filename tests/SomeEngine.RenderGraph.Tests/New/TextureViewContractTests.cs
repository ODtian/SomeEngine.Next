using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Xunit;
using NullDevice = SomeEngine.Graphics.Null.Device;

namespace SomeEngine.RenderGraph.Tests;

public sealed class TextureViewContractTests
{
    [Fact]
    public void Frozen_view_preserves_explicit_dimension_and_allowed_reinterpretation_format()
    {
        using NullDevice device = new();
        GraphRecording recording = new();
        TextureId texture = recording.AddTexture(
            new TextureDesc(
                8,
                8,
                Format.R8G8B8A8UNorm,
                TextureUsage.Sampled,
                ArrayLayers: 2,
                AllowedViewFormats: [Format.R8G8B8A8UNormSrgb]),
            default);
        _ = recording.AddTextureView(
            texture,
            new TextureSubresourceRange(0, 1, 1, 1, TextureAspect.Color),
            TextureViewUsage.ShaderResource,
            Format.R8G8B8A8UNormSrgb,
            "alternate-array-view",
            TextureViewDimension.Texture2DArray);

        FrozenGraph frozen = recording.Freeze(device);
        FrozenTextureView view = Assert.Single(frozen.TextureViews);

        Assert.Equal(Format.R8G8B8A8UNormSrgb, view.Format);
        Assert.Equal(TextureViewDimension.Texture2DArray, view.Dimension);
        Assert.Equal(new TextureSubresourceRange(0, 1, 1, 1, TextureAspect.Color), view.Range);
    }

    [Fact]
    public void Canonical_identity_includes_view_dimension_and_resource_format_set()
    {
        using NullDevice device = new();
        FrozenGraph twoDimensional = FreezeView(
            device,
            TextureViewDimension.Texture2D,
            allowSrgb: false);
        FrozenGraph array = FreezeView(
            device,
            TextureViewDimension.Texture2DArray,
            allowSrgb: false);
        FrozenGraph castable = FreezeView(
            device,
            TextureViewDimension.Texture2D,
            allowSrgb: true);

        Assert.False(twoDimensional.Canonical.Equals(array.Canonical));
        Assert.False(twoDimensional.Canonical.Equals(castable.Canonical));
        Assert.NotEqual(twoDimensional.Canonical.Bytes, array.Canonical.Bytes);
        Assert.NotEqual(twoDimensional.Canonical.Bytes, castable.Canonical.Bytes);
    }

    [Fact]
    public void Authoring_default_is_frozen_to_the_exact_resource_shape()
    {
        using NullDevice device = new();

        Assert.Equal(
            TextureViewDimension.Texture2DMS,
            FreezeInferredView(device, new TextureDesc(
                8,
                8,
                Format.R8G8B8A8UNorm,
                TextureUsage.ColorAttachment,
                SampleCount: 4)));
        Assert.Equal(
            TextureViewDimension.Texture2DMSArray,
            FreezeInferredView(device, new TextureDesc(
                8,
                8,
                Format.R8G8B8A8UNorm,
                TextureUsage.ColorAttachment,
                ArrayLayers: 2,
                SampleCount: 4)));
        Assert.Equal(
            TextureViewDimension.Texture3D,
            FreezeInferredView(device, new TextureDesc(
                8,
                8,
                Format.R8G8B8A8UNorm,
                TextureUsage.Sampled,
                Depth: 4,
                Dimension: TextureDimension.Texture3D)));
    }

    [Fact]
    public void Authoring_rejects_a_view_format_absent_from_the_resource_set()
    {
        using NullDevice device = new();
        GraphRecording recording = new();
        TextureId texture = recording.AddTexture(
            new TextureDesc(8, 8, Format.R8G8B8A8UNorm, TextureUsage.Sampled),
            default);

        Assert.Throws<ArgumentException>(() => recording.AddTextureView(
            texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
            TextureViewUsage.ShaderResource,
            Format.R8G8B8A8UNormSrgb,
            null,
            TextureViewDimension.Texture2D));
    }

    private static FrozenGraph FreezeView(
        NullDevice device,
        TextureViewDimension dimension,
        bool allowSrgb)
    {
        GraphRecording recording = new();
        TextureDesc desc = allowSrgb
            ? new TextureDesc(
                8,
                8,
                Format.R8G8B8A8UNorm,
                TextureUsage.Sampled,
                ArrayLayers: 2,
                AllowedViewFormats: [Format.R8G8B8A8UNormSrgb])
            : new TextureDesc(
                8,
                8,
                Format.R8G8B8A8UNorm,
                TextureUsage.Sampled,
                ArrayLayers: 2);
        TextureId texture = recording.AddTexture(desc, default);
        _ = recording.AddTextureView(
            texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
            TextureViewUsage.ShaderResource,
            Format.R8G8B8A8UNorm,
            null,
            dimension);
        return recording.Freeze(device);
    }

    private static TextureViewDimension FreezeInferredView(NullDevice device, TextureDesc desc)
    {
        GraphRecording recording = new();
        TextureId texture = recording.AddTexture(desc, default);
        TextureViewUsage usage = (desc.Usage & TextureUsage.ColorAttachment) != 0
            ? TextureViewUsage.ColorAttachment
            : TextureViewUsage.ShaderResource;
        _ = recording.AddTextureView(
            texture,
            new TextureSubresourceRange(0, 1, 0, desc.ArrayLayers, TextureAspect.Color),
            usage,
            Format.Unknown,
            null);
        return Assert.Single(recording.Freeze(device).TextureViews).Dimension;
    }
}
