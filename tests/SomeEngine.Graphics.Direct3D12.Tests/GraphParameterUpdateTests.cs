using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Job;
using SomeEngine.RenderGraph;
using Xunit;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class GraphParameterUpdateTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void IndependentParameterBlocksPreservePixelsAcrossResourceAndOrdinaryDataChanges()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        D3D12TestShader shader = shaderCompiler.Compile("graph_parameter_updates", """
            struct InputValue { uint Value; };
            ConstantBuffer<InputValue> source;
            RWTexture2D<uint> target;
            uint bias;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain() { target[uint2(0, 0)] = source.Value + bias; }
            """, [new D3D12TestEntry("computeMain", SlangStage.Compute)], "sm_6_2");
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0)));
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        using Buffer first = backend.CreateBuffer(device, new BufferDesc(256, BufferUsages.Constant), MemoryType.Upload);
        using Buffer second = backend.CreateBuffer(device, new BufferDesc(256, BufferUsages.Constant), MemoryType.Upload);
        using BufferCbv firstView = backend.CreateBufferCbv(device, new BufferCbvDesc(first, BufferRange.Whole));
        using BufferCbv secondView = backend.CreateBufferCbv(device, new BufferCbvDesc(second, BufferRange.Whole));
        using Buffer output = backend.CreateBuffer(device, new BufferDesc(2_048, BufferUsages.CopyDestination), MemoryType.Readback);
        foreach ((Buffer buffer, uint value) in new[] { (first, 3u), (second, 7u) })
        {
            using MappedBuffer mapped = backend.Map(buffer, MapType.Write, BufferRange.Whole);
            mapped.Bytes.Clear();
            MemoryMarshal.Cast<byte, uint>(mapped.Bytes)[0] = value;
            mapped.Flush(mapped.Range);
        }
        Queue queue = backend.GetQueue(device, QueueType.Compute);
        using var graph = new SomeEngine.RenderGraph.RenderGraph(backend, device, [queue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        GraphParameterInput input;
        var passes = new GraphPassId[4];
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.RegisterExternalBuffer(first, [new(BufferRange.Whole, first.InitialSync, first.InitialAccess, ResourceContentState.Defined)]);
            edit.RegisterExternalBuffer(second, [new(BufferRange.Whole, second.InitialSync, second.InitialAccess, ResourceContentState.Defined)]);
            GraphBufferId destination = edit.RegisterExternalBuffer(output,
                [new(BufferRange.Whole, output.InitialSync, output.InitialAccess, ResourceContentState.Undefined)]);
            input = edit.CreateParameterInput(ResourceBindingType.ConstantBuffer);
            for (int index = 0; index < passes.Length; index++)
            {
                GraphTextureId texture = edit.CreateTexture(new TextureDesc(TextureDimension.Texture2D,
                    1, 1, 1, 1, 1, 1, Format.R32UInt, TextureUsages.Storage | TextureUsages.CopySource));
                GraphTextureUavId target = edit.CreateTextureUav(texture, Format.R32UInt, TextureViewDimension.Texture2D);
                passes[index] = edit.AddComputeFramePass("parameter pixel", PassQueueSelection.Exact(queue),
                    new PixelParameters(input, target),
                    new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread, RepeatCommands: true),
                    pipeline, layout, [],
                    static (ref PassDefinition pass, ref PixelParameters data) =>
                    {
                        pass.Bind(data.Input, PipelineSync.ComputeShading);
                        pass.Bind(data.Target, PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete);
                    },
                    static (ref ComputePassCommandScope commands, in PixelParameters _) => commands.Dispatch(new DispatchArguments(1, 1, 1)));
                edit.AddCopyFramePass("parameter pixel readback", PassQueueSelection.Exact(queue),
                    new PixelReadback(texture, destination, (ulong)index * 512),
                    new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
                    static (ref PassDefinition pass, ref PixelReadback data) =>
                    {
                        pass.Read(data.Texture, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                            PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
                        pass.Write(data.Buffer, new BufferRange(data.Offset, 256), PipelineSync.Copy,
                            ResourceAccess.CopyDestination, WriteCoverage.Partial);
                    },
                    static (ref CopyPassCommandScope commands, in PixelReadback data) =>
                        commands.CopyTextureToBuffer(new BufferTextureCopy(commands.GetBuffer(data.Buffer), data.Offset,
                            256, 1, commands.GetTexture(data.Texture), 0, 0, TextureAspects.Color, 0, 0, 0, 1, 1, 1)));
            }
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 6; frameIndex++)
        {
            bool alternate = frameIndex is 2 or 3;
            uint bias = alternate ? 20u : 10u;
            using (RenderGraphEdit edit = graph.BeginEdit())
            {
                for (int index = 0; index < passes.Length; index++)
                {
                    uint[] data = [bias + (uint)index];
                    PassDefinition pass = edit.GetPass(passes[index]);
                    pass.SetParameterBlock(layout, MemoryMarshal.AsBytes(data.AsSpan()));
                }
                edit.Commit();
            }
            using (RenderGraphFrame frame = graph.BeginFrame(new RenderGraphFrameOptions(FrameSubmissionMode.RecordAllThenSubmit)))
            {
                frame.SetParameterInput(input, ResourceBinding.ConstantBuffer(alternate ? secondView : firstView));
                int count = frame.Execute(completions);
                foreach (QueueCompletion completion in completions.AsSpan(0, count))
                    Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            }
            for (int index = 0; index < passes.Length; index++)
            {
                using MappedBuffer mapped = backend.Map(output, MapType.Read, new BufferRange((ulong)index * 512, 4));
                mapped.Invalidate(mapped.Range);
                Assert.Equal((alternate ? 27u : 13u) + (uint)index, MemoryMarshal.Read<uint>(mapped.Bytes));
            }
        }
    }

    private readonly record struct PixelParameters(GraphParameterInput Input, GraphTextureUavId Target);
    private readonly record struct PixelReadback(GraphTextureId Texture, GraphBufferId Buffer, ulong Offset);
}
