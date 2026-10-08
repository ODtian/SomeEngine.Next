using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using SomeEngine.RenderGraph;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class DisjointWriteFrameTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void DisjointBufferAndTextureWritesReachReadersAcrossFrames()
    {
        D3D12TestShader shader = shaderCompiler.Compile("disjoint_writes", """
            uint Pixel;
            uint Value;
            RWBuffer<uint> Values;
            RWTexture2DArray<uint> Image;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void writeMain()
            {
                Values[Pixel] = Value;
                Image[uint3(Pixel, 0, 0)] = Value;
            }
            """, [new D3D12TestEntry("writeMain", SlangStage.Compute)], "sm_6_2");
        using IGraphicsBackend backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0)));
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        using var graph = new SomeEngine.RenderGraph.RenderGraph(backend, device, [queue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer result = backend.CreateBuffer(device,
            new BufferDesc(1024, BufferUsages.CopyDestination), MemoryType.Readback);
        TextureSubresourceRange whole = new(0, 1, 0, 1, TextureAspects.Color);
        GraphPassId initialize, readback;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId values = edit.CreatePersistentBuffer(new BufferDesc(8,
                BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphTextureId image = edit.CreatePersistentTexture(new TextureDesc(TextureDimension.Texture2D,
                2, 1, 1, 1, 1, 1, Format.R32UInt,
                TextureUsages.ColorAttachment | TextureUsages.Storage | TextureUsages.CopySource));
            GraphBufferId output = edit.RegisterExternalBuffer(result,
                [new(BufferRange.Whole, result.InitialSync, result.InitialAccess, ResourceContentState.Undefined)]);
            initialize = edit.AddCopyFramePass("initialize output data", PassQueueSelection.Exact(queue),
                (Values: values, Image: image), new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition pass, ref (GraphBufferId Values, GraphTextureId Image) data) =>
                {
                    pass.Write(data.Values, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                    pass.Write(data.Image, whole, PipelineSync.RenderTarget, ResourceAccess.RenderTarget,
                        TextureLayout.RenderTarget, WriteCoverage.Complete);
                },
                (ref CopyPassCommandScope commands, in (GraphBufferId Values, GraphTextureId Image) data) =>
                {
                    commands.ClearBuffer(commands.GetBuffer(data.Values), BufferRange.Whole);
                    commands.ClearTexture(commands.GetTexture(data.Image), whole, Vector4.Zero);
                });
            GraphBufferUavId bufferView = edit.CreateBufferUav(values, format: Format.R32UInt);
            GraphTextureUavId textureView = edit.CreateTextureUav(image, format: Format.R32UInt,
                dimension: TextureViewDimension.Texture2DArray);
            for (uint pixel = 0; pixel < 2; pixel++)
            {
                uint[] data = [pixel, pixel == 0 ? 11u : 22u];
                byte[] ordinaryData = MemoryMarshal.AsBytes(data.AsSpan()).ToArray();
                edit.AddComputeFramePass("write one pixel", PassQueueSelection.Exact(queue), ordinaryData,
                    new PassOptions(RepeatCommands: true),
                    (ref PassDefinition pass, ref byte[] bytes) =>
                    {
                        pass.SetPipeline(pipeline);
                        pass.SetParameterBlock(layout, bytes);
                        pass.Bind(bufferView, PipelineSync.ComputeShading, GraphAccessMode.Write,
                            unorderedAccessHazards: UnorderedAccessHazardMode.DisjointWrites);
                        pass.Bind(textureView, PipelineSync.ComputeShading, GraphAccessMode.Write,
                            unorderedAccessHazards: UnorderedAccessHazardMode.DisjointWrites);
                    },
                    static (ref ComputePassCommandScope commands, in byte[] bytes) => commands.Dispatch(new(1, 1, 1)));
            }
            readback = edit.AddCopyFramePass("read all output pixels", PassQueueSelection.Exact(queue),
                (Values: values, Image: image, Output: output), new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition pass, ref (GraphBufferId Values, GraphTextureId Image, GraphBufferId Output) data) =>
                {
                    pass.Read(data.Values, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Read(data.Image, whole, PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
                    pass.Write(data.Output, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Partial);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Values, GraphTextureId Image, GraphBufferId Output) data) =>
                {
                    commands.CopyBuffer(new(commands.GetBuffer(data.Values), 0, commands.GetBuffer(data.Output), 0, 8));
                    commands.CopyTextureToBuffer(new(commands.GetBuffer(data.Output), 512, 256, 1,
                        commands.GetTexture(data.Image), 0, 0, TextureAspects.Color, 0, 0, 0, 2, 1, 1));
                });
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 6; frameIndex++)
        {
            using RenderGraphFrame frame = graph.BeginFrame();
            frame.SetPassEnabled(initialize, frameIndex == 0);
            frame.SetPassEnabled(readback, frameIndex == 5);
            frame.Execute(completions);
            foreach (QueueCompletion completion in completions)
                if (completion != default)
                    Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        }
        using MappedBuffer bytes = backend.Map(result, MapType.Read, BufferRange.Whole);
        bytes.Invalidate(bytes.Range);
        Assert.Equal(new uint[] { 11, 22 }, MemoryMarshal.Cast<byte, uint>(bytes.Bytes[..8]).ToArray());
        Assert.Equal(new uint[] { 11, 22 }, MemoryMarshal.Cast<byte, uint>(bytes.Bytes.Slice(512, 8)).ToArray());
    }
}
