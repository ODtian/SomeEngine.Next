using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using SomeEngine.RenderGraph;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class ParameterInputRepeatTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public unsafe void ReimportedBindingsRepeatAndObserveTextureViewRangeAndSamplerChanges()
    {
        using var data = new InputResources(shaderCompiler);
        IGraphicsBackend backend = data.NativeBackend;
        Queue queue = backend.GetQueue(data.Device, QueueType.Compute);
        using var graph = new SomeEngine.RenderGraph.RenderGraph(backend, data.Device, [queue],
            new RenderGraphDesc(MaximumFramesInFlight: 2));
        using Buffer result = backend.CreateBuffer(data.Device,
            new BufferDesc(12, BufferUsages.CopyDestination), MemoryType.Readback);
        using ExternalTimeline gate = data.Validation.CreateExternalTimeline(data.Device, 0);
        var nativeGate = data.Validation.GetNativeTimeline(gate);
        GraphParameterInput textureInput, samplerInput, constantsInput, bufferInput;
        int records = 0;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            textureInput = edit.CreateParameterInput(ResourceBindingType.TextureSrv);
            samplerInput = edit.CreateParameterInput(ResourceBindingType.Sampler);
            constantsInput = edit.CreateParameterInput(ResourceBindingType.ConstantBuffer);
            bufferInput = edit.CreateParameterInput(ResourceBindingType.BufferSrv);
            GraphBufferId values = edit.CreatePersistentBuffer(new BufferDesc(12,
                BufferUsages.ShaderWrite | BufferUsages.CopySource));
            GraphBufferUavId valuesView = edit.CreateBufferUav(values, structureStride: 4);
            GraphBufferId destination = edit.RegisterExternalBuffer(result,
                [new(BufferRange.Whole, result.InitialSync, result.InitialAccess, ResourceContentState.Undefined)]);
            edit.AddComputeFramePass("read imported shader inputs", PassQueueSelection.Exact(queue),
                (byte)0, new PassOptions(RepeatCommands: true),
                (ref PassDefinition pass, ref byte _) =>
                {
                    pass.SetPipeline(data.Pipeline);
                    pass.SetParameterBlock(data.Layout, []);
                    pass.Bind(textureInput, PipelineSync.ComputeShading);
                    pass.Bind(samplerInput, PipelineSync.ComputeShading);
                    pass.Bind(constantsInput, PipelineSync.ComputeShading);
                    pass.Bind(bufferInput, PipelineSync.ComputeShading);
                    pass.Bind(valuesView, PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete);
                },
                (ref ComputePassCommandScope commands, in byte _) =>
                {
                    records++;
                    commands.Dispatch(new(1, 1, 1));
                });
            edit.AddCopyFramePass("read shader results", PassQueueSelection.Exact(queue),
                (values, destination), new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                static (ref PassDefinition pass, ref (GraphBufferId Source, GraphBufferId Destination) pair) =>
                {
                    pass.Read(pair.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(pair.Destination, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Source, GraphBufferId Destination) pair) =>
                    commands.CopyBuffer(new(commands.GetBuffer(pair.Source), 0,
                        commands.GetBuffer(pair.Destination), 0, 12)));
            edit.Commit();
        }

        var inputs = new (TextureSrv Texture, Sampler Sampler, Buffer Constants, ulong Offset,
            BufferSrv Buffer, uint[] Expected)[]
        {
            (data.FirstTextureView, data.Edge, data.FirstBuffer, 0, data.FirstBufferView, [25, 11, 11]),
            (data.SecondTextureView, data.Edge, data.FirstBuffer, 0, data.FirstBufferView, [75, 11, 11]),
            (data.SecondTextureView, data.Edge, data.FirstBuffer, 0, data.LaterBufferView, [75, 11, 22]),
            (data.SecondTextureView, data.Edge, data.FirstBuffer, 256, data.LaterBufferView, [75, 22, 22]),
            (data.SecondTextureView, data.Edge, data.SecondBuffer, 0, data.LaterBufferView, [75, 101, 22]),
            (data.SecondTextureView, data.Border, data.SecondBuffer, 0, data.LaterBufferView, [0, 101, 22]),
            (data.FirstTextureView, data.Edge, data.FirstBuffer, 0, data.FirstBufferView, [25, 11, 11]),
        };
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        QueueCompletion last = default;
        ulong signal = 0;
        try
        {
            foreach (var input in inputs)
            {
                for (int pair = 0; pair < 3; pair++)
                {
                    int before = records;
                    signal++;
                    // Keep both frames unfinished until they have been submitted. The second
                    // BeginFrame must use different frame storage, without relying on timing.
                    last = backend.Submit(queue, new([], [new(gate, signal)], [], [], []));
                    for (int frameIndex = 0; frameIndex < 2; frameIndex++)
                    {
                        using RenderGraphFrame frame = graph.BeginFrame();
                        frame.Import(input.Texture.Description.Texture,
                            [new(data.TextureRange, PipelineSync.ComputeShading, ResourceAccess.ShaderResource,
                                TextureLayout.ShaderResource, ResourceContentState.Defined, queue)]);
                        frame.Import(input.Constants,
                            [new(BufferRange.Whole, input.Constants.InitialSync,
                                input.Constants.InitialAccess, ResourceContentState.Defined)]);
                        Buffer source = input.Buffer.Description.Buffer;
                        frame.Import(source,
                            [new(BufferRange.Whole, source.InitialSync, source.InitialAccess, ResourceContentState.Defined)]);
                        frame.SetParameterInput(textureInput, ResourceBinding.SampledTexture(input.Texture));
                        frame.SetParameterInput(samplerInput, ResourceBinding.SampledWith(input.Sampler));
                        frame.SetParameterInput(constantsInput,
                            ResourceBinding.ConstantBuffer(input.Constants, new(input.Offset, 256)));
                        frame.SetParameterInput(bufferInput, ResourceBinding.ReadOnlyBuffer(input.Buffer));
                        Assert.Equal(1, frame.Execute(completions));
                        last = completions.First(static completion => completion != default);
                        Assert.False(backend.IsComplete(last));
                    }
                    Assert.True(nativeGate->Signal(signal) >= 0);
                    Assert.Equal(WaitStatus.Completed, backend.WaitCpu(last, TimeSpan.FromSeconds(10)));
                    backend.CollectCompleted(data.Device);
                    using MappedBuffer bytes = backend.Map(result, MapType.Read, BufferRange.Whole);
                    bytes.Invalidate(bytes.Range);
                    Assert.Equal(input.Expected, MemoryMarshal.Cast<byte, uint>(bytes.Bytes).ToArray());
                    if (pair == 2) Assert.Equal(before, records);
                }
            }
        }
        finally
        {
            _ = nativeGate->Signal(signal);
            if (last != default) _ = backend.WaitCpu(last, TimeSpan.FromSeconds(10));
            backend.CollectCompleted(data.Device);
        }
    }

    [Fact]
    public void IndexedReadBesideTheSameBindingStillRecordsEachImportedFrame()
    {
        using var data = new InputResources(shaderCompiler);
        IGraphicsBackend backend = data.NativeBackend;
        Queue queue = backend.GetQueue(data.Device, QueueType.Compute);
        using var graph = new SomeEngine.RenderGraph.RenderGraph(backend, data.Device, [queue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer result = backend.CreateBuffer(data.Device,
            new BufferDesc(12, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphParameterInput textureInput, samplerInput, constantsInput, bufferInput;
        int records = 0;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            textureInput = edit.CreateParameterInput(ResourceBindingType.TextureSrv);
            samplerInput = edit.CreateParameterInput(ResourceBindingType.Sampler);
            constantsInput = edit.CreateParameterInput(ResourceBindingType.ConstantBuffer);
            bufferInput = edit.CreateParameterInput(ResourceBindingType.BufferSrv);
            GraphBufferId values = edit.CreatePersistentBuffer(new BufferDesc(12,
                BufferUsages.ShaderWrite | BufferUsages.CopySource));
            GraphBufferUavId valuesView = edit.CreateBufferUav(values, structureStride: 4);
            GraphBufferId destination = edit.RegisterExternalBuffer(result,
                [new(BufferRange.Whole, result.InitialSync, result.InitialAccess, ResourceContentState.Undefined)]);
            edit.AddComputeFramePass("read both bound and indexed inputs", PassQueueSelection.Exact(queue),
                (byte)0, new PassOptions(RepeatCommands: true),
                (ref PassDefinition pass, ref byte _) =>
                {
                    pass.SetPipeline(data.Pipeline);
                    pass.SetParameterBlock(data.Layout, []);
                    pass.Read(textureInput, PipelineSync.ComputeShading);
                    pass.Bind(textureInput, PipelineSync.ComputeShading);
                    pass.Bind(samplerInput, PipelineSync.ComputeShading);
                    pass.Bind(constantsInput, PipelineSync.ComputeShading);
                    pass.Bind(bufferInput, PipelineSync.ComputeShading);
                    pass.Bind(valuesView, PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete);
                },
                (ref ComputePassCommandScope commands, in byte _) =>
                {
                    records++;
                    commands.Dispatch(new(1, 1, 1));
                });
            edit.AddCopyFramePass("read shader results", PassQueueSelection.Exact(queue),
                (values, destination), new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                static (ref PassDefinition pass, ref (GraphBufferId Source, GraphBufferId Destination) pair) =>
                {
                    pass.Read(pair.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(pair.Destination, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Source, GraphBufferId Destination) pair) =>
                    commands.CopyBuffer(new(commands.GetBuffer(pair.Source), 0,
                        commands.GetBuffer(pair.Destination), 0, 12)));
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int index = 0; index < 9; index++)
        {
            TextureSrv texture = index is >= 3 and < 6 ? data.SecondTextureView : data.FirstTextureView;
            int before = records;
            using (RenderGraphFrame frame = graph.BeginFrame())
            {
                frame.Import(texture.Description.Texture,
                    [new(data.TextureRange, PipelineSync.ComputeShading, ResourceAccess.ShaderResource,
                        TextureLayout.ShaderResource, ResourceContentState.Defined, queue)]);
                frame.Import(data.FirstBuffer,
                    [new(BufferRange.Whole, data.FirstBuffer.InitialSync,
                        data.FirstBuffer.InitialAccess, ResourceContentState.Defined)]);
                frame.SetParameterInput(textureInput, ResourceBinding.SampledTexture(texture));
                frame.SetParameterInput(samplerInput, ResourceBinding.SampledWith(data.Edge));
                frame.SetParameterInput(constantsInput, ResourceBinding.ConstantBuffer(data.FirstBuffer, new(0, 256)));
                frame.SetParameterInput(bufferInput, ResourceBinding.ReadOnlyBuffer(data.FirstBufferView));
                frame.Execute(completions);
            }
            foreach (QueueCompletion completion in completions)
                if (completion != default)
                    Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            Assert.Equal(before + 1, records);
            using MappedBuffer bytes = backend.Map(result, MapType.Read, BufferRange.Whole);
            bytes.Invalidate(bytes.Range);
            Assert.Equal(new uint[] { index is >= 3 and < 6 ? 75u : 25u, 11, 11 },
                MemoryMarshal.Cast<byte, uint>(bytes.Bytes).ToArray());
        }
    }

    private sealed class InputResources : IDisposable
    {
        internal IGraphicsBackend NativeBackend { get; }
        internal ValidationLayer Validation { get; }
        internal Device Device { get; }
        internal Pipeline Pipeline { get; }
        internal VariableLayoutReflection Layout { get; }
        internal Texture FirstTexture { get; }
        internal Texture SecondTexture { get; }
        internal TextureSrv FirstTextureView { get; }
        internal TextureSrv SecondTextureView { get; }
        internal Buffer FirstBuffer { get; }
        internal Buffer SecondBuffer { get; }
        internal BufferSrv FirstBufferView { get; }
        internal BufferSrv LaterBufferView { get; }
        internal Sampler Edge { get; }
        internal Sampler Border { get; }
        internal TextureSubresourceRange TextureRange => new(0, 1, 0, 1, TextureAspects.Color);

        internal InputResources(D3D12TestCompiler compiler)
        {
            D3D12TestShader shader = compiler.Compile("reimported_parameter_inputs", """
                Texture2D<float> InputTexture;
                SamplerState InputSampler;
                struct Values { uint Value; };
                ConstantBuffer<Values> InputValues;
                Buffer<uint> InputBuffer;
                RWStructuredBuffer<uint> Output;
                [shader("compute")]
                [numthreads(1, 1, 1)]
                void computeMain()
                {
                    Output[0] = uint(InputTexture.SampleLevel(InputSampler, float2(1.5), 0) * 100.0);
                    Output[1] = InputValues.Value;
                    Output[2] = InputBuffer[0];
                }
                """, [new("computeMain", SlangStage.Compute)], "sm_6_2");
            NativeBackend = D3D12GraphicsBackend.Create();
            // Native repetition is optional on the validation receiver. Enable its native
            // validation first, then exercise repetition through the public D3D12 receiver.
            Validation = new ValidationLayer(NativeBackend);
            Device = D3D12TestSupport.CreateWarpDevice(Validation);
            Pipeline = Validation.CreateComputePipeline(Device,
                new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0)));
            Layout = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
            FirstTexture = CreateTexture(0.25f);
            SecondTexture = CreateTexture(0.75f);
            FirstTextureView = Validation.CreateTextureSrv(Device,
                new(FirstTexture, TextureRange, Format.R32Float, TextureViewDimension.Texture2D));
            SecondTextureView = Validation.CreateTextureSrv(Device,
                new(SecondTexture, TextureRange, Format.R32Float, TextureViewDimension.Texture2D));
            FirstBuffer = CreateBuffer(11, 22);
            SecondBuffer = CreateBuffer(101, 202);
            FirstBufferView = Validation.CreateBufferSrv(Device, new(FirstBuffer, new(0, 4), Format.R32UInt));
            LaterBufferView = Validation.CreateBufferSrv(Device, new(FirstBuffer, new(256, 4), Format.R32UInt));
            Edge = Validation.CreateSampler(Device, new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge));
            Border = Validation.CreateSampler(Device, new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToBorder, AddressType.ClampToBorder, AddressType.ClampToBorder,
                BorderColor: System.Numerics.Vector4.Zero));
        }

        private Texture CreateTexture(float value)
        {
            Texture texture = Validation.CreateTexture(Device, new(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
                Format.R32Float, TextureUsages.Sampled | TextureUsages.CopyDestination));
            D3D12TestSupport.UploadSinglePixelR32Float(Validation, Device, texture, value);
            return texture;
        }

        private Buffer CreateBuffer(uint first, uint second)
        {
            Buffer buffer = Validation.CreateBuffer(Device,
                new(512, BufferUsages.Constant | BufferUsages.ShaderRead), MemoryType.Upload);
            using MappedBuffer bytes = Validation.Map(buffer, MapType.Write, BufferRange.Whole);
            bytes.Bytes.Clear();
            MemoryMarshal.Write(bytes.Bytes, in first);
            MemoryMarshal.Write(bytes.Bytes[256..], in second);
            bytes.Flush(bytes.Range);
            return buffer;
        }

        public void Dispose()
        {
            FirstTextureView.Dispose();
            SecondTextureView.Dispose();
            FirstBufferView.Dispose();
            LaterBufferView.Dispose();
            FirstTexture.Dispose();
            SecondTexture.Dispose();
            FirstBuffer.Dispose();
            SecondBuffer.Dispose();
            Edge.Dispose();
            Border.Dispose();
            Pipeline.Dispose();
            Device.Dispose();
            Validation.Dispose();
        }
    }
}
