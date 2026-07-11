using System.Buffers.Binary;
using System.Reflection;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class RasterPipelineTests
{
    [Fact]
    public void Unsupported_dxil_shader_model_is_rejected_before_pso_creation()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
        });
        uint supported = GetHighestShaderModel(device);
        uint required = checked(supported + 1);
        byte[] bytecode = BuildSyntheticDxil(
            shaderKind: 1,
            major: required >> 4,
            minor: required & 0xF);
        ShaderDesc desc = Shader(
            new ShaderArtifactKey(11, 12, 13, 14),
            ShaderStage.Vertex,
            "VSMain",
            bytecode);

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() => device.CreateShader(desc));

        Assert.Contains($"requires Shader Model {required >> 4}.{required & 0xF}", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"supports up to {supported >> 4}.{supported & 0xF}", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            device.DrainDiagnostics(),
            static diagnostic => diagnostic.Severity >= GraphicsDiagnosticSeverity.Error);
    }

    [Fact]
    public void Sm62_zero_binding_triangle_pipeline_is_created_on_warp()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
        });
        ShaderHandle vertex = default;
        ShaderHandle pixel = default;
        PipelineLayoutHandle layout = default;
        PipelineHandle pipeline = default;
        try
        {
            vertex = device.CreateShader(Shader(
                new ShaderArtifactKey(21, 22, 23, 24),
                ShaderStage.Vertex,
                "VSMain",
                ReadFixture("triangle.vs.dxil")));
            pixel = device.CreateShader(Shader(
                new ShaderArtifactKey(31, 32, 33, 34),
                ShaderStage.Pixel,
                "PSMain",
                ReadFixture("triangle.ps.dxil")));
            layout = device.CreatePipelineLayout(new PipelineLayoutDesc(
                Array.Empty<BindGroupLayoutHandle>(),
                Array.Empty<PushConstantRange>(),
                "sm62-zero-binding-layout"));
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
                Name: "sm62-zero-binding-triangle"));

            Assert.DoesNotContain(
                device.DrainDiagnostics(),
                static diagnostic => diagnostic.Severity >= GraphicsDiagnosticSeverity.Error);
        }
        finally
        {
            if (pipeline.IsValid) device.DestroyPipeline(pipeline);
            if (layout.IsValid) device.DestroyPipelineLayout(layout);
            if (pixel.IsValid) device.DestroyShader(pixel);
            if (vertex.IsValid) device.DestroyShader(vertex);
            device.CollectGarbage();
        }
    }

    [Fact]
    public void Sm62_triangle_draws_with_a_native_d32_depth_pipeline_and_attachment()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
        });
        ShaderHandle vertex = default;
        ShaderHandle pixel = default;
        PipelineLayoutHandle layout = default;
        PipelineHandle pipeline = default;
        TextureHandle color = default;
        TextureHandle depth = default;
        TextureViewHandle colorView = default;
        TextureViewHandle depthView = default;
        try
        {
            vertex = device.CreateShader(Shader(
                new ShaderArtifactKey(41, 42, 43, 44),
                ShaderStage.Vertex,
                "VSMain",
                ReadFixture("triangle.vs.dxil")));
            pixel = device.CreateShader(Shader(
                new ShaderArtifactKey(51, 52, 53, 54),
                ShaderStage.Pixel,
                "PSMain",
                ReadFixture("triangle.ps.dxil")));
            layout = device.CreatePipelineLayout(new PipelineLayoutDesc(
                Array.Empty<BindGroupLayoutHandle>(),
                Array.Empty<PushConstantRange>()));
            pipeline = device.CreateRasterPipeline(new RasterPipelineDesc(
                layout,
                vertex,
                pixel,
                new[] { Format.R8G8B8A8UNorm },
                DepthStencilFormat: Format.D32Float,
                Topology: PrimitiveTopology.TriangleList,
                Rasterizer: new RasterizerDesc(FillMode.Solid, CullMode.None, FrontFace.CounterClockwise, DepthClip: true),
                DepthStencil: new DepthStencilDesc(true, true, CompareOp.Less),
                BlendAttachments: new[] { new BlendAttachmentDesc() },
                SampleCount: 1,
                Name: "sm62-d32-triangle"));

            color = device.CreateTexture(new TextureDesc(
                16,
                16,
                Format.R8G8B8A8UNorm,
                TextureUsage.ColorAttachment));
            depth = device.CreateTexture(new TextureDesc(
                16,
                16,
                Format.D32Float,
                TextureUsage.DepthStencilAttachment));
            colorView = device.CreateTextureView(new TextureViewDesc(
                color,
                TextureSubresourceRange.WholeColor,
                TextureViewUsage.ColorAttachment));
            depthView = device.CreateTextureView(new TextureViewDesc(
                depth,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Depth),
                TextureViewUsage.DepthStencilAttachment));

            using ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics);
            commands.Barriers([
                ResourceBarrier.Transition(color.Resource, ResourceState.Common, ResourceState.RenderTarget),
                ResourceBarrier.Transition(
                    depth.Resource,
                    ResourceState.Common,
                    ResourceState.DepthWrite,
                    new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Depth)),
            ]);
            commands.SetPipeline(pipeline);
            commands.SetViewport(new Viewport(0, 0, 16, 16));
            commands.SetScissor(new Rect(0, 0, 16, 16));
            InvalidOperationException missingDepth = Assert.Throws<InvalidOperationException>(() =>
                commands.BeginRendering(new RenderingInfo(
                    new[] { new ColorAttachment(colorView, LoadAction.Clear, StoreAction.Store) },
                    null,
                    16,
                    16)));
            Assert.Contains("requires a depth-stencil attachment", missingDepth.Message, StringComparison.Ordinal);
            commands.BeginRendering(new RenderingInfo(
                new[] { new ColorAttachment(colorView, LoadAction.Clear, StoreAction.Store) },
                new DepthStencilAttachment(
                    depthView,
                    new DepthAttachmentOperations(LoadAction.Clear, StoreAction.Store, ClearValue: 1f)),
                16,
                16));
            commands.Draw(3);
            commands.EndRendering();
            GpuCompletion completion = device.Submit(QueueType.Graphics, [commands.Finish()]);
            Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));
            Assert.DoesNotContain(
                device.DrainDiagnostics(),
                static diagnostic => diagnostic.Severity >= GraphicsDiagnosticSeverity.Error);
        }
        finally
        {
            if (depthView.IsValid) device.DestroyTextureView(depthView);
            if (colorView.IsValid) device.DestroyTextureView(colorView);
            if (depth.IsValid) device.DestroyTexture(depth);
            if (color.IsValid) device.DestroyTexture(color);
            if (pipeline.IsValid) device.DestroyPipeline(pipeline);
            if (layout.IsValid) device.DestroyPipelineLayout(layout);
            if (pixel.IsValid) device.DestroyShader(pixel);
            if (vertex.IsValid) device.DestroyShader(vertex);
            device.CollectGarbage();
        }
    }

    private static ShaderDesc Shader(
        ShaderArtifactKey key,
        ShaderStage stage,
        string entryPoint,
        byte[] bytecode) =>
        new(
            key,
            ShaderBinaryFormat.Dxil,
            stage,
            entryPoint,
            bytecode,
            new ShaderInterface(Array.Empty<ShaderBinding>(), Array.Empty<PushConstantRange>(), 1),
            $"test:{entryPoint}");

    private static byte[] ReadFixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static uint GetHighestShaderModel(Device device)
    {
        FieldInfo nativeField = typeof(Device).GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The D3D12 device no longer owns a native context field.");
        object native = nativeField.GetValue(device)
            ?? throw new InvalidOperationException("The D3D12 native context is unavailable.");
        PropertyInfo property = native.GetType().GetProperty("HighestShaderModel", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("The D3D12 native context does not expose its shader-model capability internally.");
        return Convert.ToUInt32(property.GetValue(native));
    }

    private static byte[] BuildSyntheticDxil(uint shaderKind, uint major, uint minor)
    {
        const int partOffset = 36;
        const int programOffset = partOffset + 8;
        const int programSize = 28;
        byte[] result = new byte[programOffset + programSize];
        "DXBC"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24, 4), checked((uint)result.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(32, 4), partOffset);
        "DXIL"u8.CopyTo(result.AsSpan(partOffset, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(partOffset + 4, 4), programSize);

        uint version = checked((shaderKind << 16) | (major << 4) | minor);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(programOffset, 4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(programOffset + 4, 4), programSize / 4);
        "DXIL"u8.CopyTo(result.AsSpan(programOffset + 8, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(programOffset + 12, 4), 0x100);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(programOffset + 16, 4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(programOffset + 20, 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(programOffset + 24, 4), 0xDEC0_4342);
        return result;
    }
}
