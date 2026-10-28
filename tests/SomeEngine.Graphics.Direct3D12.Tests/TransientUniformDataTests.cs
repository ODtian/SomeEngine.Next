using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class TransientUniformDataTests(D3D12TestCompiler shaderCompiler)
{
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    public void Many_uniform_updates_preserve_each_dispatch_result(int vectorCount)
    {
        const int count = 260;
        string source = $$"""
            RWStructuredBuffer<uint> outputValues;
            uint4 values[{{vectorCount}}];
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain()
            {
                outputValues[values[0].x] = values[0].y +
                    values[{{vectorCount - 1}}].z + values[{{vectorCount - 1}}].w;
            }
            """;
        D3D12TestShader shader = shaderCompiler.Compile(
            $"transient_uniform_dispatch_{vectorCount}", source,
            [new("computeMain", SlangStage.Compute)], "sm_6_2");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        TypeLayoutReflection reflected = layout.TypeLayout.UnwrapArray();
        if (reflected.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            reflected = reflected.ElementTypeLayout.UnwrapArray();
        Assert.Equal((nuint)(vectorCount * 16), reflected.GetSize(SlangParameterCategory.Uniform));

        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0)));
        using Buffer output = backend.CreateBuffer(device,
            new(count * sizeof(uint), BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav view = backend.CreateBufferUav(device, new(output, BufferRange.Whole, StructureStride: sizeof(uint)));
        using Buffer readback = backend.CreateBuffer(device,
            new(count * sizeof(uint), BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Compute);
        ResourceBinding[] resources = [ResourceBinding.WritableBuffer(view)];
        byte[] data = new byte[vectorCount * 16];

        for (uint recording = 0; recording < 2; recording++)
        {
            backend.Begin(context, new CommandRecordingDesc(4, 0, 4));
            backend.Barrier(context, new BufferBarrier(output,
                recording == 0 ? PipelineSync.None : PipelineSync.Copy, PipelineSync.ComputeShading,
                recording == 0 ? ResourceAccess.NoAccess : ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
            backend.SetPipeline(context, pipeline);
            for (uint index = 0; index < count; index++)
            {
                Span<uint> values = MemoryMarshal.Cast<byte, uint>(data.AsSpan());
                values[0] = index;
                values[1] = recording * 1000 + index + 1;
                values[^2] = 17;
                values[^1] = 29;
                backend.SetTransientParameterBindings(context, new(layout, resources, data));
                backend.Dispatch(context, new(1, 1, 1));
                backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.ComputeShading,
                    ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
            }
            backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            backend.CopyBuffer(context, new(output, 0, readback, 0, count * sizeof(uint)));
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = backend.Submit(queue, new([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
            mapped.Invalidate(mapped.Range);
            ReadOnlySpan<uint> results = MemoryMarshal.Cast<byte, uint>(mapped.Bytes);
            for (int index = 0; index < count; index++)
                Assert.Equal(recording * 1000 + (uint)index + 47, results[index]);
            backend.CollectCompleted(device);
        }
    }

    [Fact]
    public void Resource_free_uniform_updates_preserve_each_draw_result()
    {
        const int count = 260;
        const uint rowPitch = 1280;
        const string source = """
            uint4 values;
            [shader("vertex")]
            float4 vertexMain(uint id : SV_VertexID) : SV_Position
            {
                float2 position = float2((id << 1) & 2, id & 2) * 2.0 - 1.0;
                return float4(position, 0.0, 1.0);
            }
            [shader("fragment")]
            uint fragmentMain() : SV_Target
            {
                return values.x + values.y + values.z + values.w;
            }
            """;
        D3D12TestShader shader = shaderCompiler.Compile(
            "transient_uniform_draw", source,
            [new("vertexMain", SlangStage.Vertex), new("fragmentMain", SlangStage.Fragment)], "sm_6_2");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        TypeLayoutReflection reflected = layout.TypeLayout.UnwrapArray();
        if (reflected.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            reflected = reflected.ElementTypeLayout.UnwrapArray();
        Assert.Equal((nuint)16, reflected.GetSize(SlangParameterCategory.Uniform));
        Assert.Equal((nint)0, reflected.BindingRangeCount);

        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateGraphicsPipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0),
                shader.GetEntryPoint(1), shader.GetEntryCode(1), [], [], PrimitiveTopology.TriangleList,
                StripCut.Disabled, new RasterizerState(Cull: CullType.None), new MultisampleState(SampleCount: 1),
                new DepthStencilState(), new BlendState([new(Enabled: false, WriteMask: ColorWriteMasks.All)]),
                new AttachmentFormatSignature([Format.R32UInt], null), DynamicStates.Viewport | DynamicStates.Scissor));
        using Texture target = backend.CreateTexture(device,
            new(TextureDimension.Texture2D, count, 1, 1, 1, 1, 1, Format.R32UInt,
                TextureUsages.ColorAttachment | TextureUsages.CopySource));
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using ColorAttachmentView view = backend.CreateColorAttachmentView(device,
            new(target, range, Format.R32UInt, TextureViewDimension.Texture2D));
        using Buffer readback = backend.CreateBuffer(device,
            new(rowPitch, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        byte[] data = new byte[16];

        for (uint recording = 0; recording < 2; recording++)
        {
            backend.Begin(context);
            backend.Barrier(context, new TextureBarrier(target, range,
                recording == 0 ? PipelineSync.None : PipelineSync.Copy, PipelineSync.RenderTarget,
                recording == 0 ? ResourceAccess.NoAccess : ResourceAccess.CopySource, ResourceAccess.RenderTarget,
                recording == 0 ? TextureLayout.Undefined : TextureLayout.CopySource, TextureLayout.RenderTarget));
            backend.SetPipeline(context, pipeline);
            backend.SetViewports(context, [new Viewport(0, 0, count, 1)]);
            backend.BeginRendering(context, new RenderingDesc(
                [new ColorAttachmentDesc(view, LoadType.Clear, StoreType.Store, Vector4.Zero)], null, count, 1));
            for (int index = 0; index < count; index++)
            {
                Span<uint> values = MemoryMarshal.Cast<byte, uint>(data.AsSpan());
                values[0] = recording * 1000 + (uint)index + 1;
                values[1] = 2;
                values[2] = 3;
                values[3] = 4;
                backend.SetScissors(context, [new ScissorRect(index, 0, 1, 1)]);
                backend.SetTransientParameterBindings(context, new(layout, [], data));
                backend.Draw(context, new(3, 1, 0, 0));
            }
            backend.EndRendering(context);
            backend.Barrier(context, new TextureBarrier(target, range, PipelineSync.RenderTarget, PipelineSync.Copy,
                ResourceAccess.RenderTarget, ResourceAccess.CopySource, TextureLayout.RenderTarget, TextureLayout.CopySource));
            backend.CopyTextureToBuffer(context, new(readback, 0, rowPitch, 1, target, 0, 0,
                TextureAspects.Color, 0, 0, 0, count, 1, 1));
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = backend.Submit(queue, new([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            using MappedBuffer mapped = backend.Map(readback, MapType.Read, new(0, count * sizeof(uint)));
            mapped.Invalidate(mapped.Range);
            ReadOnlySpan<uint> results = MemoryMarshal.Cast<byte, uint>(mapped.Bytes);
            for (int index = 0; index < count; index++)
                Assert.Equal(recording * 1000 + (uint)index + 10, results[index]);
            backend.CollectCompleted(device);
        }
    }
}
