using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Render.Assets;
using Xunit;
using D3DDevice = SomeEngine.Graphics.Direct3D12.Device;
using D3DOptions = SomeEngine.Graphics.Direct3D12.Options;

namespace SomeEngine.RenderGraph.Tests;

public sealed class AssetBackedRasterTests
{
    private const int Width = 64;
    private const int Height = 64;
    private const uint RowPitch = 256;

    [Fact]
    public void Cooked_shader_asset_renders_transient_rg_attachment_and_reads_back_on_warp()
    {
        if (!OperatingSystem.IsWindows()) return;

        using CookedShaderAssetFixture assets = new();
        ShaderAsset asset = assets.LoadHelloTriangle();
        ShaderDesc vertexDesc = ShaderAssetProjection.Dxil(asset, "VSMain", SomeEngine.Assets.Schema.ShaderStage.Vertex);
        ShaderDesc pixelDesc = ShaderAssetProjection.Dxil(asset, "PSMain", SomeEngine.Assets.Schema.ShaderStage.Pixel);
        using D3DDevice device = new(new D3DOptions { UseWarpAdapter = true, EnableDebugLayer = true });

        ShaderHandle vertex = default;
        ShaderHandle pixel = default;
        PipelineLayoutHandle layout = default;
        PipelineHandle pipeline = default;
        BufferHandle readback = default;
        try
        {
            vertex = device.CreateShader(vertexDesc);
            pixel = device.CreateShader(pixelDesc);
            layout = device.CreatePipelineLayout(new PipelineLayoutDesc(
                Array.Empty<BindGroupLayoutHandle>(),
                Array.Empty<PushConstantRange>(),
                "hello-triangle-layout"));
            pipeline = device.CreateRasterPipeline(new RasterPipelineDesc(
                layout,
                vertex,
                pixel,
                new[] { Format.R8G8B8A8UNorm },
                Topology: PrimitiveTopology.TriangleList,
                Rasterizer: new RasterizerDesc(FillMode.Solid, CullMode.None, FrontFace.CounterClockwise, DepthClip: true),
                DepthStencil: new DepthStencilDesc(false, false, CompareOp.Always),
                BlendAttachments: new[]
                {
                    new BlendAttachmentDesc(
                        false,
                        BlendFactor.One,
                        BlendFactor.Zero,
                        BlendOperation.Add,
                        BlendFactor.One,
                        BlendFactor.Zero,
                        BlendOperation.Add,
                        ColorWriteMask.All),
                },
                SampleCount: 1,
                Name: "hello-triangle-pipeline"));

            BufferDesc readbackDesc = new(checked((ulong)RowPitch * Height), BufferUsage.CopyDestination, "hello-triangle-readback");
            readback = device.CreateBuffer(readbackDesc, MemoryType.Readback);

            using (RenderGraph graph = new(device))
            {
                GraphBuilder builder = graph.Begin();
                TextureDesc colorDesc = new(
                    Width,
                    Height,
                    Format.R8G8B8A8UNorm,
                    TextureUsage.ColorAttachment | TextureUsage.CopySource,
                    Name: "hello-triangle-color");
                TextureId color = builder.CreateTexture(colorDesc);
                TextureViewId colorView = builder.CreateTextureView(
                    color,
                    new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
                    TextureViewUsage.ColorAttachment,
                    name: "hello-triangle-rtv");
                BufferId destination = builder.ImportBuffer(
                    readback,
                    BufferUse.CopyDestination,
                    BufferUse.CopyDestination,
                    contentsAvailable: false);

                PassBuilder raster = builder.AddPass("hello-triangle-raster", QueueSelection.Graphics);
                _ = raster.ColorAttachment(0, colorView, LoadAction.Clear, new Vector4(0, 0, 0, 1));
                raster.UsesShader(vertexDesc);
                raster.UsesShader(pixelDesc);
                raster.UsesPipeline(pipeline);
                raster.Execute((ICommandContext commands, in PassResources _) =>
                {
                    commands.SetViewport(new Viewport(0, 0, Width, Height));
                    commands.SetScissor(new Rect(0, 0, Width, Height));
                    commands.SetPipeline(pipeline);
                    commands.Draw(3);
                });

                PassBuilder copy = builder.AddPass("hello-triangle-readback", QueueSelection.Graphics);
                TextureAccess sourceAccess = copy.Read(
                    color,
                    TextureUse.CopySource,
                    new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color));
                BufferAccess destinationAccess = copy.Write(destination, BufferUse.CopyDestination);
                copy.Execute((ICommandContext commands, in PassResources resources) =>
                {
                    commands.CopyTextureToBuffer(new TextureBufferCopy(
                        resources.Get(sourceAccess),
                        new TextureCopyRegion(
                            MipLevel: 0,
                            ArrayLayer: 0,
                            Aspect: TextureAspect.Color,
                            X: 0,
                            Y: 0,
                            Z: 0,
                            Width,
                            Height,
                            Depth: 1),
                        resources.Get(destinationAccess),
                        new TextureBufferLayout(
                            Offset: 0,
                            BytesPerRow: RowPitch,
                            RowsPerImage: Height)));
                });

                GraphExecution execution = graph.Execute(ref builder);
                Assert.True(execution.Wait(TimeSpan.FromSeconds(10)));
            }

            byte[] pixels = new byte[checked((int)RowPitch * Height)];
            device.ReadBuffer(readback, 0, pixels);
            AssertBlack(pixels, 0, 0);
            Assert.True(ColorEnergy(pixels, Width / 2, Height / 2) > 30, "The triangle center remained at the clear color.");

            int coloredPixels = 0;
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                if (ColorEnergy(pixels, x, y) > 30) coloredPixels++;
            }
            Assert.True(coloredPixels > 300, $"Only {coloredPixels} pixels differed from the clear color.");

            device.CollectGarbage();
            GraphicsDiagnostic[] diagnostics = device.DrainDiagnostics();
            Assert.DoesNotContain(diagnostics, item => item.Severity >= GraphicsDiagnosticSeverity.Error);
        }
        finally
        {
            if (readback.IsValid) device.DestroyBuffer(readback);
            if (pipeline.IsValid) device.DestroyPipeline(pipeline);
            if (layout.IsValid) device.DestroyPipelineLayout(layout);
            if (pixel.IsValid) device.DestroyShader(pixel);
            if (vertex.IsValid) device.DestroyShader(vertex);
            device.CollectGarbage();
        }
    }

    private static int ColorEnergy(byte[] pixels, int x, int y)
    {
        int offset = checked(y * (int)RowPitch + x * 4);
        return pixels[offset] + pixels[offset + 1] + pixels[offset + 2];
    }

    private static void AssertBlack(byte[] pixels, int x, int y)
    {
        int offset = checked(y * (int)RowPitch + x * 4);
        Assert.InRange(pixels[offset], (byte)0, (byte)1);
        Assert.InRange(pixels[offset + 1], (byte)0, (byte)1);
        Assert.InRange(pixels[offset + 2], (byte)0, (byte)1);
        Assert.InRange(pixels[offset + 3], (byte)254, byte.MaxValue);
    }
}
