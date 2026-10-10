using System.Buffers;
using System.Numerics;
using SlangShaderSharp;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Validation;
using SomeEngine.Graphics.Vulkan;
using Shader = SomeEngine.Resources.Shader;
using Buffer = SomeEngine.Graphics.Buffer;
using Xunit;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class LayeredShaderRenderingTests
{
    private const string Source = """
        struct VertexOutput
        {
            float4 Position : SV_Position;
            uint Layer : SV_RenderTargetArrayIndex;
            float4 Color : COLOR0;
        };
        [shader("vertex")]
        VertexOutput vertexMain(uint id : SV_VertexID, uint instance : SV_InstanceID)
        {
            const float2 positions[3] =
            {
                float2(-1, -1), float2(3, -1), float2(-1, 3),
            };
            VertexOutput output;
            output.Position = float4(positions[id], 0, 1);
            output.Layer = instance;
            output.Color = instance == 0 ? float4(1, 0, 0, 1) : float4(0, 1, 0, 1);
            return output;
        }
        [shader("fragment")]
        float4 pixelMain(VertexOutput input) : SV_Target0 { return input.Color; }
        """;

    [Fact]
    public async Task Imported_vertex_shader_selects_distinct_render_target_layers()
    {
        Shader shader = await CompileImportedShaderAsync();
        EntryPointReflection vertex = shader.RequireSlangEntry(0, "vertexMain", SlangStage.Vertex);
        EntryPointReflection fragment = shader.RequireSlangEntry(0, "pixelMain", SlangStage.Fragment);
        ReadOnlySequence<byte> vertexCode = shader.GetSlangEntryBytes(0, vertex);
        ReadOnlySequence<byte> fragmentCode = shader.GetSlangEntryBytes(0, fragment);
        Assert.False(vertexCode.IsEmpty);
        Assert.False(fragmentCode.IsEmpty);
        using IGraphicsBackend backend = new ValidationLayer(
            VulkanGraphicsBackend.Create(), new ValidationOptions(NativeValidationDisabled: true));
        using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using var pipeline = backend.CreateGraphicsPipeline(device, new GraphicsPipelineDesc(
            shader.SlangCode, shader.GetSlangReflection(0), vertex, vertexCode,
            fragment, fragmentCode, [], [], PrimitiveTopology.TriangleList,
            StripCut.Disabled, new RasterizerState(Cull: CullType.None), new MultisampleState(),
            new DepthStencilState(), new BlendState([new BlendAttachmentState()]),
            new AttachmentFormatSignature([Format.R8G8B8A8UNorm], null),
            DynamicStates.Viewport | DynamicStates.Scissor));
        using Texture texture = backend.CreateTexture(device, new TextureDesc(
            TextureDimension.Texture2D, 16, 16, 1, 1, 2, 1, Format.R8G8B8A8UNorm,
            TextureUsages.ColorAttachment | TextureUsages.CopySource));
        TextureSubresourceRange range = new(0, 1, 0, 2, TextureAspects.Color);
        using ColorAttachmentView attachment = backend.CreateColorAttachmentView(device,
            new ColorAttachmentViewDesc(texture, range, Format.R8G8B8A8UNorm, TextureViewDimension.Texture2DArray));
        const int layerBytes = 16 * 16 * 4;
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(2 * layerBytes, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(texture, range,
            PipelineSync.None, PipelineSync.RenderTarget, ResourceAccess.NoAccess, ResourceAccess.RenderTarget,
            TextureLayout.Undefined, TextureLayout.RenderTarget));
        backend.BeginRendering(context, new RenderingDesc(
            [new ColorAttachmentDesc(attachment, LoadType.Clear, StoreType.Store, Vector4.Zero)], null, 16, 16));
        backend.SetPipeline(context, pipeline);
        backend.SetViewports(context, [new Viewport(0, 0, 16, 16)]);
        backend.SetScissors(context, [new ScissorRect(0, 0, 16, 16)]);
        backend.Draw(context, new DrawArguments(3, 2, 0, 0));
        backend.EndRendering(context);
        backend.Barrier(context, new TextureBarrier(texture, range,
            PipelineSync.RenderTarget, PipelineSync.Copy, ResourceAccess.RenderTarget, ResourceAccess.CopySource,
            TextureLayout.RenderTarget, TextureLayout.CopySource));
        for (uint layer = 0; layer < 2; layer++)
            backend.CopyTextureToBuffer(context, new BufferTextureCopy(readback, layer * layerBytes,
                16 * 4, 16, texture, 0, layer, TextureAspects.Color, 0, 0, 0, 16, 16, 1));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        using MappedBuffer pixels = backend.Map(readback, MapType.Read, BufferRange.Whole);
        pixels.Invalidate(pixels.Range);
        for (int layer = 0; layer < 2; layer++)
        {
            byte[] expected = new byte[layerBytes];
            for (int offset = 0; offset < layerBytes; offset += 4)
            {
                expected[offset + layer] = 255;
                expected[offset + 3] = 255;
            }
            ImageArtifacts.AssertEqual(new(16, 16, Format.R8G8B8A8UNorm, expected),
                new(16, 16, Format.R8G8B8A8UNorm, pixels.Bytes.Slice(layer * layerBytes, layerBytes).ToArray()),
                $"{nameof(Imported_vertex_shader_selects_distinct_render_target_layers)}-{layer}");
        }
        Assert.Equal(DeviceStatus.Active, device.Status);
    }

    private static async Task<Shader> CompileImportedShaderAsync()
    {
        string directory = Directory.CreateTempSubdirectory("SomeEngine-LayeredShader-").FullName;
        try
        {
            string sourcePath = Path.Combine(directory, "layered.slang");
            await File.WriteAllTextAsync(sourcePath, Source);
            using var definition = new ShaderAsset { Name = "Layered shader" };
            definition.GetBlock<ShaderSourceBlock>().SourceNames = ["layered.slang"];
            using ShaderAsset imported = SlangShaderImporter.CompileDefinition(
                definition, Path.Combine(directory, "layered.shader"));
            File.Delete(sourcePath);
            return await TestShaderLoading.CreateAsync(imported, SlangCompileTarget.Spirv);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
