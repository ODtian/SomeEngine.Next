using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpTextureViewShapeTests
{
    [Fact]
    public void Warp_lowers_explicit_resource_and_view_dimensions_without_debug_errors()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            EnableGpuValidation = false,
        });
        List<(TextureHandle Texture, TextureViewHandle[] Views)> values = [];

        TextureHandle oneDimensional = device.CreateTexture(new TextureDesc(
            8,
            1,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled | TextureUsage.Storage | TextureUsage.ColorAttachment,
            ArrayLayers: 2,
            Dimension: TextureDimension.Texture1D));
        values.Add((oneDimensional,
        [
            device.CreateTextureView(new TextureViewDesc(
                oneDimensional,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
                TextureViewUsage.ShaderResource,
                Dimension: TextureViewDimension.Texture1D)),
            device.CreateTextureView(new TextureViewDesc(
                oneDimensional,
                new TextureSubresourceRange(0, 1, 0, 2, TextureAspect.Color),
                TextureViewUsage.ShaderResource | TextureViewUsage.Storage | TextureViewUsage.ColorAttachment,
                Dimension: TextureViewDimension.Texture1DArray)),
        ]));

        TextureHandle twoDimensional = device.CreateTexture(new TextureDesc(
            8,
            4,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled | TextureUsage.Storage | TextureUsage.ColorAttachment,
            ArrayLayers: 2));
        values.Add((twoDimensional,
        [
            device.CreateTextureView(new TextureViewDesc(
                twoDimensional,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
                TextureViewUsage.ShaderResource | TextureViewUsage.Storage | TextureViewUsage.ColorAttachment,
                Dimension: TextureViewDimension.Texture2D)),
            device.CreateTextureView(new TextureViewDesc(
                twoDimensional,
                new TextureSubresourceRange(0, 1, 0, 2, TextureAspect.Color),
                TextureViewUsage.ShaderResource | TextureViewUsage.Storage | TextureViewUsage.ColorAttachment,
                Dimension: TextureViewDimension.Texture2DArray)),
        ]));

        TextureHandle multisampled = device.CreateTexture(new TextureDesc(
            8,
            4,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled | TextureUsage.ColorAttachment,
            ArrayLayers: 2,
            SampleCount: 4));
        values.Add((multisampled,
        [
            device.CreateTextureView(new TextureViewDesc(
                multisampled,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
                TextureViewUsage.ShaderResource | TextureViewUsage.ColorAttachment,
                Dimension: TextureViewDimension.Texture2DMS)),
            device.CreateTextureView(new TextureViewDesc(
                multisampled,
                new TextureSubresourceRange(0, 1, 0, 2, TextureAspect.Color),
                TextureViewUsage.ShaderResource | TextureViewUsage.ColorAttachment,
                Dimension: TextureViewDimension.Texture2DMSArray)),
        ]));

        TextureHandle cube = device.CreateTexture(new TextureDesc(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled,
            ArrayLayers: 12,
            CubeCompatible: true));
        values.Add((cube,
        [
            device.CreateTextureView(new TextureViewDesc(
                cube,
                new TextureSubresourceRange(0, 1, 0, 6, TextureAspect.Color),
                TextureViewUsage.ShaderResource,
                Dimension: TextureViewDimension.Cube)),
            device.CreateTextureView(new TextureViewDesc(
                cube,
                new TextureSubresourceRange(0, 1, 0, 12, TextureAspect.Color),
                TextureViewUsage.ShaderResource,
                Dimension: TextureViewDimension.CubeArray)),
        ]));

        TextureHandle volume = device.CreateTexture(new TextureDesc(
            8,
            4,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled | TextureUsage.Storage,
            Depth: 4,
            Dimension: TextureDimension.Texture3D));
        values.Add((volume,
        [
            device.CreateTextureView(new TextureViewDesc(
                volume,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
                TextureViewUsage.ShaderResource | TextureViewUsage.Storage,
                Dimension: TextureViewDimension.Texture3D)),
        ]));

        TextureHandle depthOneVolume = device.CreateTexture(new TextureDesc(
            4,
            4,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled,
            Depth: 1,
            Dimension: TextureDimension.Texture3D));
        values.Add((depthOneVolume,
        [
            device.CreateTextureView(new TextureViewDesc(
                depthOneVolume,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
                TextureViewUsage.ShaderResource,
                Dimension: TextureViewDimension.Texture3D)),
        ]));

        TextureHandle depth = device.CreateTexture(new TextureDesc(
            8,
            1,
            Format.D32Float,
            TextureUsage.Sampled | TextureUsage.DepthStencilAttachment,
            ArrayLayers: 2,
            Dimension: TextureDimension.Texture1D));
        values.Add((depth,
        [
            device.CreateTextureView(new TextureViewDesc(
                depth,
                new TextureSubresourceRange(0, 1, 0, 2, TextureAspect.Depth),
                TextureViewUsage.ShaderResource | TextureViewUsage.DepthStencilAttachment,
                Dimension: TextureViewDimension.Texture1DArray)),
        ]));

        foreach ((TextureHandle texture, TextureViewHandle[] views) in values)
        {
            foreach (TextureViewHandle view in views) device.DestroyTextureView(view);
            device.DestroyTexture(texture);
        }
        device.CollectGarbage();
        Assert.DoesNotContain(
            device.DrainDiagnostics(),
            static item => item.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
    }

    [Fact]
    public void Warp_creates_typed_linear_and_srgb_views_over_one_typeless_rgba8_resource()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            EnableGpuValidation = false,
        });
        TextureDesc desc = new(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled | TextureUsage.ColorAttachment,
            AllowedViewFormats:
            [
                Format.R8G8B8A8UNormSrgb,
                Format.R8G8B8A8UNorm,
            ]);
        TextureHandle texture = device.CreateTexture(desc);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspect.Color);
        TextureViewHandle linear = device.CreateTextureView(new TextureViewDesc(
            texture,
            range,
            TextureViewUsage.ShaderResource | TextureViewUsage.ColorAttachment,
            Format.R8G8B8A8UNorm,
            Dimension: TextureViewDimension.Texture2D));
        TextureViewHandle srgb = device.CreateTextureView(new TextureViewDesc(
            texture,
            range,
            TextureViewUsage.ShaderResource | TextureViewUsage.ColorAttachment,
            Format.R8G8B8A8UNormSrgb,
            Dimension: TextureViewDimension.Texture2D));

        device.DestroyTextureView(srgb);
        device.DestroyTextureView(linear);
        device.DestroyTexture(texture);
        device.CollectGarbage();
        Assert.DoesNotContain(
            device.DrainDiagnostics(),
            static item => item.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
    }
}
