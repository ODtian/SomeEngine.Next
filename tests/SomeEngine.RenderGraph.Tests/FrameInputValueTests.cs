using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Resources;

namespace SomeEngine.RenderGraph.Tests;

public sealed class FrameInputValueTests
{
    [Fact]
    public async Task TextureInputReadsTheValueProducedByItsCurrentFrame()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var shaders = new ShaderLibrary(SlangCompileTarget.Dxil);
        Shader producerShader = await shaders.CreateAsync("produce texture value", "input_texture_producer.slang", """
            StructuredBuffer<uint> Gate;
            RWTexture2D<uint> Produced;
            [shader("compute")] [numthreads(1,1,1)]
            void Main() { Produced[uint2(0, 0)] = Gate[0]; }
            """);
        Shader consumerShader = await shaders.CreateAsync("read texture input", "input_texture_consumer.slang", """
            Texture2D<uint> Selected;
            RWStructuredBuffer<uint> Result;
            [shader("compute")] [numthreads(1,1,1)]
            void Main() { Result[0] = Selected.Load(int3(0, 0, 0)); }
            """);
        EntryPointReflection producerEntry = producerShader.RequireSlangEntry(0, "Main", SlangStage.Compute);
        EntryPointReflection consumerEntry = consumerShader.RequireSlangEntry(0, "Main", SlangStage.Compute);
        using Pipeline producerPipeline = support.Backend.CreateComputePipeline(support.Device,
            new ComputePipelineDesc(producerShader.SlangCode, producerShader.GetSlangReflection(0), producerEntry,
                producerShader.GetSlangEntryBytes(0, producerEntry), CodeBlob: producerShader.GetSlangEntryBlob(0, producerEntry)));
        using Pipeline consumerPipeline = support.Backend.CreateComputePipeline(support.Device,
            new ComputePipelineDesc(consumerShader.SlangCode, consumerShader.GetSlangReflection(0), consumerEntry,
                consumerShader.GetSlangEntryBytes(0, consumerEntry), CodeBlob: consumerShader.GetSlangEntryBlob(0, consumerEntry)));
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue]);
        var result = new ReadbackValue();
        GraphTextureId texture;
        GraphTextureInput selected;
        GraphBufferId input;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            input = edit.CreatePersistentBuffer(new(4, BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.AddBufferUploadPass(input);
            GraphBufferId gate = edit.CreatePersistentBuffer(new(4, BufferUsages.CopyDestination | BufferUsages.ShaderRead));
            GraphBufferId output = edit.CreatePersistentBuffer(new(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
            texture = edit.CreateTexture(new(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
                Format.R32UInt, TextureUsages.Storage | TextureUsages.Sampled));
            selected = edit.CreateTextureInput();
            GraphBufferSrvId gateView = edit.CreateBufferSrv(gate, structureStride: 4);
            GraphTextureUavId producedView = edit.CreateTextureUav(texture,
                format: Format.R32UInt, dimension: TextureViewDimension.Texture2D);
            GraphTextureSrvId selectedView = edit.CreateTextureSrv(selected,
                format: Format.R32UInt, dimension: TextureViewDimension.Texture2D);
            GraphBufferUavId outputView = edit.CreateBufferUav(output, structureStride: 4);
            GraphParameterResourceBinding[] producerBindings =
            [
                GraphParameterResourceBinding.ReadOnlyBuffer(gateView, PipelineSync.ComputeShading),
                GraphParameterResourceBinding.StorageTexture(producedView, PipelineSync.ComputeShading,
                    GraphAccessMode.Write, WriteCoverage.Complete),
            ];
            GraphPassId producer = edit.AddComputeFramePass("produce current texture", PassQueueSelection.OnQueue(support.ComputeQueue), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull), producerPipeline,
                producerShader.GetSlangReflection(0).GetGlobalParamsVarLayout()!.Value, producerBindings,
                static (ref PassDefinition _, ref int _) => { },
                static (ref ComputePassCommandScope commands, in int _) => commands.Dispatch(new(1, 1, 1)));
            GraphParameterResourceBinding[] consumerBindings =
            [
                GraphParameterResourceBinding.SampledTexture(selectedView, PipelineSync.ComputeShading),
                GraphParameterResourceBinding.WritableBuffer(outputView, PipelineSync.ComputeShading,
                    GraphAccessMode.Write, WriteCoverage.Complete),
            ];
            edit.AddComputeFramePass("consume selected texture", PassQueueSelection.OnQueue(support.GraphicsQueue), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull), consumerPipeline,
                consumerShader.GetSlangReflection(0).GetGlobalParamsVarLayout()!.Value, consumerBindings,
                static (ref PassDefinition _, ref int _) => { },
                static (ref ComputePassCommandScope commands, in int _) => commands.Dispatch(new(1, 1, 1)));
            GraphPassId dependency = edit.AddCopyFramePass("produce texture dependency", PassQueueSelection.OnQueue(support.CopyQueue),
                (input, gate), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref (GraphBufferId Input, GraphBufferId Gate) data) =>
                {
                    pass.Read(data.Input, new(0, 4), PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Gate, new(0, 4), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Input, GraphBufferId Gate) data) =>
                    commands.CopyBuffer(new(commands.GetBuffer(data.Input), 0, commands.GetBuffer(data.Gate), 0, 4)));
            edit.OrderAfter(producer, dependency);
            edit.AddReadback(output, new(0, 4), result, "read texture input value");
            edit.Commit();
        }
        ulong identity;
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.WriteBuffer(input, 0, BitConverter.GetBytes(17u));
            frame.SetTextureInput(selected, texture);
            identity = frame.FrameIdentity;
            frame.Execute();
        }
        Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
        Assert.Equal(17u, Assert.Single(result.Values));
    }

    private sealed class ReadbackValue : IGraphReadbackReceiver
    {
        internal List<uint> Values { get; } = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Values.Add(BitConverter.ToUInt32(bytes));
    }
}
