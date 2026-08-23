namespace SomeEngine.Graphics.Vulkan.Tests;

using System.Numerics;
using SlangShaderSharp;
using Xunit;

public sealed class VulkanRenderingTests
{
    [Fact]
    public void Dynamic_rendering_and_persistent_parameters_draw_expected_color()
    {
        const string source = """
            float4 Tint;
            [shader("vertex")]
            float4 vertexMain(uint id : SV_VertexID) : SV_Position
            {
                const float2 positions[3] =
                {
                    float2(0.0, 0.75),
                    float2(0.75, -0.75),
                    float2(-0.75, -0.75),
                };
                return float4(positions[id], 0, 1);
            }
            [shader("fragment")]
            float4 pixelMain() : SV_Target0 { return Tint; }
            """;
        using VulkanTestShaderProgram shader = VulkanTestShaderProgram.Compile(
            source,
            ("vertexMain", SlangStage.Vertex),
            ("pixelMain", SlangStage.Fragment));
        using var backend = new VulkanBackend();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        Format[] colorFormats = [Format.R8G8B8A8UNorm];
        BlendAttachmentState[] blend = [new()];
        using Pipeline pipeline = backend.CreateGraphicsPipeline(
            device,
            new GraphicsPipelineDesc(
                shader.Program,
                shader.Entries[0],
                shader.Entries[1],
                [],
                [],
                PrimitiveTopology.TriangleList,
                StripCut.Disabled,
                new RasterizerState(Cull: CullType.None),
                new MultisampleState(),
                new DepthStencilState(),
                new BlendState(blend),
                new AttachmentFormatSignature(colorFormats, null),
                DynamicStates.Viewport | DynamicStates.Scissor));
        VariableLayoutReflection global = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        byte[] tint =
        [
            0, 0, 0x80, 0x3F,
            0, 0, 0, 0,
            0, 0, 0, 0,
            0, 0, 0x80, 0x3F,
        ];
        using PersistentParameterBindings parameters = backend.CreatePersistentParameterBindings(
            device,
            pipeline,
            new ParameterBlockBindings(global, [], tint));

        TextureDesc textureDesc = new(
            TextureDimension.Texture2D,
            16,
            16,
            1,
            1,
            1,
            1,
            Format.R8G8B8A8UNorm,
            TextureUsages.ColorAttachment | TextureUsages.CopySource);
        using Texture texture = backend.CreateTexture(device, textureDesc);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using ColorAttachmentView attachment = backend.CreateColorAttachmentView(
            device,
            new ColorAttachmentViewDesc(
                texture,
                range,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(16 * 16 * 4, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            texture,
            range,
            PipelineSync.None,
            PipelineSync.RenderTarget,
            ResourceAccess.NoAccess,
            ResourceAccess.RenderTarget,
            TextureLayout.Undefined,
            TextureLayout.RenderTarget));
        ColorAttachmentDesc[] colors =
        [
            new(attachment, LoadType.Clear, StoreType.Store, Vector4.Zero),
        ];
        backend.BeginRendering(context, new RenderingDesc(colors, null, 16, 16));
        backend.SetPipeline(context, pipeline);
        backend.SetPersistentParameterBindings(context, parameters);
        backend.SetViewports(context, [new Viewport(0, 0, 16, 16)]);
        backend.SetScissors(context, [new ScissorRect(0, 0, 16, 16)]);
        backend.Draw(context, new DrawArguments(3, 1, 0, 0));
        backend.EndRendering(context);
        backend.Barrier(context, new TextureBarrier(
            texture,
            range,
            PipelineSync.RenderTarget,
            PipelineSync.Copy,
            ResourceAccess.RenderTarget,
            ResourceAccess.CopySource,
            TextureLayout.RenderTarget,
            TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new BufferTextureCopy(
            readback,
            0,
            16 * 4,
            16,
            texture,
            0,
            0,
            TextureAspects.Color,
            0,
            0,
            0,
            16,
            16,
            1));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(2)));

        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        int center = 8 * 16 * 4 + 8 * 4;
        Assert.True(mapped.Bytes[center] > 200);
        Assert.True(mapped.Bytes[center + 3] > 200);
    }
}
