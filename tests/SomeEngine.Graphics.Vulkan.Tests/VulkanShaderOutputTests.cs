namespace SomeEngine.Graphics.Vulkan.Tests;

using System.Numerics;
using SlangShaderSharp;
using SomeEngine.Tests;
using Xunit;

[Xunit.Collection("Vulkan shader compilation")]
public sealed class VulkanShaderOutputTests(VulkanTestCompiler shaderCompiler)
{
    [WindowsFact]
    public void Layered_draw_routes_instances_to_their_array_layers()
    {
        const string source = """
            struct VertexOutput
            {
                float4 Position : SV_Position;
                nointerpolation float4 Color : COLOR0;
                uint Layer : SV_RenderTargetArrayIndex;
            };
            [shader("vertex")]
            VertexOutput vertexMain(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
            {
                const float2 positions[3] =
                {
                    float2(-1, -1),
                    float2(3, -1),
                    float2(-1, 3),
                };
                VertexOutput output;
                output.Position = float4(positions[vertex], 0, 1);
                output.Color = instance == 0 ? float4(1, 0, 0, 1) : float4(0, 1, 0, 1);
                output.Layer = instance;
                return output;
            }
            [shader("fragment")]
            float4 pixelMain(VertexOutput input) : SV_Target0 { return input.Color; }
            """;
        VulkanTestShader shader = shaderCompiler.Compile(
            source,
            ("vertexMain", SlangStage.Vertex),
            ("pixelMain", SlangStage.Fragment));
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create(
            new VulkanBackendOptions(EnableValidation: true, EnableDebugMessages: true));
        DeviceQueueDesc[] queues = [new(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        Format[] colorFormats = [Format.R8G8B8A8UNorm];
        BlendAttachmentState[] blend = [new()];
        using Pipeline pipeline = backend.CreateGraphicsPipeline(
            device,
            new GraphicsPipelineDesc(
                shader.SlangCode, shader.Reflection, shader.Entries[0], shader.GetEntryCode(0),
                shader.Entries[1], shader.GetEntryCode(1),
                [], [], PrimitiveTopology.TriangleList, StripCut.Disabled,
                new RasterizerState(Cull: CullType.None),
                new MultisampleState(), new DepthStencilState(), new BlendState(blend),
                new AttachmentFormatSignature(colorFormats, null),
                DynamicStates.Viewport | DynamicStates.Scissor));
        TextureDesc textureDesc = new(
            TextureDimension.Texture2D, 8, 8, 1, 1, 2, 1,
            Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment | TextureUsages.CopySource);
        using Texture texture = backend.CreateTexture(device, textureDesc);
        TextureSubresourceRange range = new(0, 1, 0, 2, TextureAspects.Color);
        using ColorAttachmentView attachment = backend.CreateColorAttachmentView(device,
            new ColorAttachmentViewDesc(texture, range, Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2DArray));
        BufferTextureCopy firstCopy = new(null!, 0, 0, 0, texture,
            0, 0, TextureAspects.Color, 0, 0, 0, 8, 8, 1);
        BufferTextureCopy secondCopy = firstCopy with { ArrayLayer = 1 };
        TextureCopyFootprint firstFootprint = backend.GetTextureCopyFootprint(
            device, textureDesc, firstCopy);
        TextureCopyFootprint secondFootprint = backend.GetTextureCopyFootprint(
            device, textureDesc, secondCopy,
            checked(firstFootprint.Offset + firstFootprint.TotalSize));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(checked(secondFootprint.Offset + secondFootprint.TotalSize),
                BufferUsages.CopyDestination), MemoryType.Readback);
        firstCopy = firstCopy with
        {
            Buffer = readback,
            BufferOffset = firstFootprint.Offset,
            BufferRowPitch = firstFootprint.RowPitch,
        };
        secondCopy = secondCopy with
        {
            Buffer = readback,
            BufferOffset = secondFootprint.Offset,
            BufferRowPitch = secondFootprint.RowPitch,
        };
        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(texture, range,
            PipelineSync.None, PipelineSync.RenderTarget,
            ResourceAccess.NoAccess, ResourceAccess.RenderTarget,
            TextureLayout.Undefined, TextureLayout.RenderTarget));
        ColorAttachmentDesc[] colors = [new(attachment, LoadType.Clear, StoreType.Store, Vector4.Zero)];
        // Vulkan derives the rendering layer count from this two-layer attachment view.
        backend.BeginRendering(context, new RenderingDesc(colors, null, 8, 8));
        backend.SetPipeline(context, pipeline);
        backend.SetViewports(context, [new Viewport(0, 0, 8, 8)]);
        backend.SetScissors(context, [new ScissorRect(0, 0, 8, 8)]);
        backend.Draw(context, new DrawArguments(3, 2, 0, 0));
        backend.EndRendering(context);
        backend.Barrier(context, new TextureBarrier(texture, range,
            PipelineSync.RenderTarget, PipelineSync.Copy,
            ResourceAccess.RenderTarget, ResourceAccess.CopySource,
            TextureLayout.RenderTarget, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, firstCopy);
        backend.CopyTextureToBuffer(context, secondCopy);
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Graphics),
            new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);

        byte[] expected = new byte[8 * 8 * 4 * 2];
        for (int pixel = 0; pixel < 8 * 8; pixel++)
        {
            expected[pixel * 4] = 255;
            expected[pixel * 4 + 3] = 255;
            expected[8 * 8 * 4 + pixel * 4 + 1] = 255;
            expected[8 * 8 * 4 + pixel * 4 + 3] = 255;
        }
        byte[] actual = new byte[expected.Length];
        for (int row = 0; row < 8; row++)
        {
            int firstOffset = checked((int)(firstFootprint.Offset + (ulong)row * firstFootprint.RowPitch));
            int secondOffset = checked((int)(secondFootprint.Offset + (ulong)row * secondFootprint.RowPitch));
            mapped.Bytes.Slice(firstOffset, 8 * 4).CopyTo(actual.AsSpan(row * 8 * 4, 8 * 4));
            mapped.Bytes.Slice(secondOffset, 8 * 4).CopyTo(actual.AsSpan(8 * 8 * 4 + row * 8 * 4, 8 * 4));
        }
        // The two array layers are shown vertically in comparison artifacts, without row padding.
        ImageArtifacts.AssertEqual(
            new(8, 16, Format.R8G8B8A8UNorm, expected),
            new(8, 16, Format.R8G8B8A8UNorm, actual));
    }
}
