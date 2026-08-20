using System.Numerics;
using System.Reflection;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;
using Xunit.Sdk;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class IntelCompatibilityTests
{
    [Theory]
    [IntelHardwareData]
    public void Sm60_persistent_draw_closes_and_executes_on_Intel_hardware(
        bool hardwareAvailable)
    {
        Assert.True(hardwareAvailable);
        const string source = """
            float4 Tint;

            struct VertexOutput
            {
                float4 Position : SV_Position;
                float4 Color : COLOR0;
            };

            [shader("vertex")]
            VertexOutput vertexMain(uint vertexId : SV_VertexID)
            {
                const float2 positions[3] =
                {
                    float2(0.0, 0.75),
                    float2(0.75, -0.75),
                    float2(-0.75, -0.75),
                };
                VertexOutput result;
                result.Position = float4(positions[vertexId], 0.0, 1.0);
                result.Color = float4(1.0, 1.0, 1.0, 1.0);
                return result;
            }

            [shader("fragment")]
            float4 pixelMain(VertexOutput input) : SV_Target0
            {
                return input.Color * Tint;
            }
            """;
        D3D12TestShaderEntry[] entries =
        [
            new("vertexMain", SlangStage.Vertex),
            new("pixelMain", SlangStage.Fragment),
        ];
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "intel_sm60_persistent_draw",
            source,
            entries,
            "sm_6_0");
        D3D12ValidationOptions validation = new(
            DisableGpuBasedValidation: true,
            DisableSynchronizedQueueValidation: true,
            DisableDred: true);
        using var backend = new ValidationLayer(
            new D3D12Backend(new D3D12BackendOptions(validation)));
        AdapterInfo adapter = SelectIntel(backend);
        DeviceQueueDesc[] queues = [new(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(adapter.Id, queues));
        Assert.Equal(
            DynamicStates.None,
            device.Capabilities.SupportedDynamicStates & DynamicStates.StripCut);
        Format[] formats = [Format.R8G8B8A8UNorm];
        BlendAttachmentState[] blendAttachments =
        [
            new(Enabled: false, WriteMask: ColorWriteMasks.All),
        ];
        Assert.Throws<InvalidOperationException>(() =>
            backend.CreateGraphicsPipeline(
                device,
                new GraphicsPipelineDesc(
                    shader.Program,
                    shader.GetEntryPoint(0),
                    shader.GetEntryPoint(1),
                    [],
                    [],
                    PrimitiveTopology.TriangleList,
                    StripCut.Disabled,
                    new RasterizerState(Cull: CullType.None),
                    new MultisampleState(SampleCount: 1),
                    new DepthStencilState(),
                    new BlendState(blendAttachments),
                    new AttachmentFormatSignature(formats, null),
                    DynamicStates.Viewport |
                    DynamicStates.Scissor |
                    DynamicStates.StripCut)));
        using Pipeline pipeline = backend.CreateGraphicsPipeline(
            device,
            new GraphicsPipelineDesc(
                shader.Program,
                shader.GetEntryPoint(0),
                shader.GetEntryPoint(1),
                [],
                [],
                PrimitiveTopology.TriangleList,
                StripCut.Disabled,
                new RasterizerState(Cull: CullType.None),
                new MultisampleState(SampleCount: 1),
                new DepthStencilState(),
                new BlendState(blendAttachments),
                new AttachmentFormatSignature(formats, null),
                DynamicStates.Viewport | DynamicStates.Scissor));
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        Assert.NotEqual(VariableLayoutReflection.Null, layout);
        byte[] ordinaryData = new byte[16];
        BitConverter.TryWriteBytes(ordinaryData.AsSpan(0, 4), 1f);
        BitConverter.TryWriteBytes(ordinaryData.AsSpan(4, 4), 1f);
        BitConverter.TryWriteBytes(ordinaryData.AsSpan(8, 4), 1f);
        BitConverter.TryWriteBytes(ordinaryData.AsSpan(12, 4), 1f);
        using PersistentParameterBindings persistent =
            backend.CreatePersistentParameterBindings(
                device,
                pipeline,
                new ParameterBlockBindings(layout, [], ordinaryData));
        using Texture target = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                64,
                64,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment | TextureUsages.CopySource));
        TextureSubresourceRange targetRange = new(
            0,
            1,
            0,
            1,
            TextureAspects.Color);
        using ColorAttachmentView targetView = backend.CreateColorAttachmentView(
            device,
            new ColorAttachmentViewDesc(
                target,
                targetRange,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context);
        Assert.Throws<InvalidOperationException>(() =>
            backend.SetStripCut(context, StripCut.Disabled));
        backend.Barrier(context, new TextureBarrier(
            target,
            targetRange,
            PipelineSync.None,
            PipelineSync.RenderTarget,
            ResourceAccess.NoAccess,
            ResourceAccess.RenderTarget,
            TextureLayout.Undefined,
            TextureLayout.RenderTarget));
        backend.SetPipeline(context, pipeline);
        backend.SetViewports(context, [new Viewport(0, 0, 64, 64)]);
        backend.SetScissors(context, [new ScissorRect(0, 0, 64, 64)]);
        backend.SetPersistentParameterBindings(context, persistent);
        ColorAttachmentDesc[] colors =
        [
            new(targetView, LoadType.Clear, StoreType.Store, Vector4.Zero),
        ];
        backend.BeginRendering(context, new RenderingDesc(colors, null, 64, 64));
        backend.Draw(context, new DrawArguments(3, 1, 0, 0));
        backend.EndRendering(context);
        backend.Barrier(context, new TextureBarrier(
            target,
            targetRange,
            PipelineSync.RenderTarget,
            PipelineSync.Copy,
            ResourceAccess.RenderTarget,
            ResourceAccess.CopySource,
            TextureLayout.RenderTarget,
            TextureLayout.CopySource));

        RecordedCommands commands;
        try
        {
            commands = backend.End(context);
        }
        catch (GraphicsException exception)
        {
            Assert.Fail(exception.Diagnostic ?? exception.ToString());
            throw;
        }
        using (commands)
        {
            QueueCompletion completion = backend.Submit(
                backend.GetQueue(device, QueueType.Graphics),
                new QueueSubmitDesc([], [], [commands], [], []));
            Assert.Equal(
                WaitStatus.Completed,
                backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        }
    }

    private static AdapterInfo SelectIntel(IGraphicsBackend backend)
    {
        AdapterEnumerationOptions options = new(
            AdapterPreference.HighPerformance,
            IncludeSoftware: false);
        _ = backend.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        Assert.True(backend.TryEnumerateAdapters(options, adapters, out int confirmed));
        Assert.Equal(count, confirmed);
        return adapters.First(static adapter =>
            adapter.HardwareAccelerated &&
            adapter.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase));
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class IntelHardwareDataAttribute : DataAttribute
{
    public override IEnumerable<object[]> GetData(MethodInfo testMethod)
    {
        try
        {
            using IGraphicsBackend backend = new D3D12Backend();
            AdapterEnumerationOptions options = new(
                AdapterPreference.HighPerformance,
                IncludeSoftware: false);
            _ = backend.TryEnumerateAdapters(options, [], out int count);
            var adapters = new AdapterInfo[count];
            if (!backend.TryEnumerateAdapters(options, adapters, out int confirmed) ||
                confirmed != adapters.Length ||
                !adapters.Any(static adapter =>
                    adapter.HardwareAccelerated &&
                    adapter.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase)))
            {
                Skip = "No Intel D3D12 hardware adapter is available.";
                return [];
            }
            return [[true]];
        }
        catch (Exception exception) when (
            exception is NotSupportedException or PlatformNotSupportedException)
        {
            Skip = exception.Message;
            return [];
        }
    }
}
