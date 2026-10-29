using SlangShaderSharp;
using Xunit;

namespace SomeEngine.Graphics.Vulkan.Tests;

[Xunit.Collection("Vulkan shader compilation")]
public sealed class VulkanParameterBindingTests(VulkanTestCompiler shaderCompiler)
{
    [Fact]
    public void TransientDispatchValuesSurvivePipelineReplacement()
    {
        VulkanTestShader[] shaders =
        [
            shaderCompiler.Compile("""
                uint Values[64];
                Texture2D<float> Bias;
                SamplerState Sampling;
                RWStructuredBuffer<uint> Result;
                [shader("compute")][numthreads(1, 1, 1)]
                void computeMain() { Result[0] = Values[0] + uint(Bias.SampleLevel(Sampling, float2(0.5), 0)); }
                """, ("computeMain", SlangStage.Compute)),
            shaderCompiler.Compile("""
                uint Values[64];
                Texture2D<float> Bias;
                SamplerState Sampling;
                StructuredBuffer<uint> Addends;
                RWStructuredBuffer<uint> Result;
                [shader("compute")][numthreads(1, 1, 1)]
                void computeMain() { Result[0] = Values[0] + uint(Bias.SampleLevel(Sampling, float2(0.5), 0)) + Addends[0]; }
                """, ("computeMain", SlangStage.Compute)),
        ];
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
        using Buffer input = backend.CreateBuffer(device, new BufferDesc(4, BufferUsages.ShaderRead), MemoryType.Upload);
        using (MappedBuffer mapping = backend.Map(input, MapType.Write, BufferRange.Whole))
        {
            BitConverter.TryWriteBytes(mapping.Bytes, 3u);
            mapping.Flush(mapping.Range);
        }
        using BufferSrv inputView = backend.CreateBufferSrv(device, new BufferSrvDesc(input, BufferRange.Whole, StructureStride: 4));
        using Texture bias = backend.CreateTexture(device, new TextureDesc(
            TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, Format.R32Float,
            TextureUsages.Sampled | TextureUsages.CopyDestination));
        TextureSubresourceRange biasRange = new(0, 1, 0, 1, TextureAspects.Color);
        using TextureSrv biasView = backend.CreateTextureSrv(device, new TextureSrvDesc(
            bias, biasRange, Format.R32Float, TextureViewDimension.Texture2D));
        using Buffer biasUpload = backend.CreateBuffer(device, new BufferDesc(256, BufferUsages.CopySource), MemoryType.Upload);
        using (MappedBuffer mapping = backend.Map(biasUpload, MapType.Write, BufferRange.Whole))
        {
            BitConverter.TryWriteBytes(mapping.Bytes, 5f);
            mapping.Flush(new BufferRange(0, 4));
        }
        using Buffer firstOutput = backend.CreateBuffer(device, new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using Buffer secondOutput = backend.CreateBuffer(device, new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav firstView = backend.CreateBufferUav(device, new BufferUavDesc(firstOutput, BufferRange.Whole, StructureStride: 4));
        using BufferUav secondView = backend.CreateBufferUav(device, new BufferUavDesc(secondOutput, BufferRange.Whole, StructureStride: 4));
        using Buffer readback = backend.CreateBuffer(device, new BufferDesc(8, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext commands = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        for (int index = 0; index < shaders.Length; index++)
        {
            VulkanTestShader shader = shaders[index];
            VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
            TypeLayoutReflection data = globals.TypeLayout.ElementTypeLayout;
            nint samplerRange = Enumerable.Range(0, checked((int)data.BindingRangeCount))
                .Select(static range => (nint)range)
                .Single(range => (data.GetBindingRangeType(range) & SlangBindingType.BaseMask) == SlangBindingType.Sampler);
            SamplerDesc sampler = new(FilterType.Linear, FilterType.Linear, FilterType.Linear,
                AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge);
            using Pipeline pipeline = backend.CreateComputePipeline(device, new ComputePipelineDesc(
                shader.SlangCode, shader.Reflection, shader.Entries[0], shader.GetEntryCode(0),
                StaticSamplers: new[] { new StaticSamplerBinding(data.GetBindingRangeLeafVariable(samplerRange), sampler) }));
            byte[] ordinary = new byte[checked((int)data.GetSize(SlangParameterCategory.Uniform))];
            ResourceBinding[] firstResources = index == 0
                ? [ResourceBinding.SampledTexture(biasView), ResourceBinding.WritableBuffer(firstView)]
                : [ResourceBinding.SampledTexture(biasView), ResourceBinding.ReadOnlyBuffer(inputView), ResourceBinding.WritableBuffer(firstView)];
            ResourceBinding[] secondResources = index == 0
                ? [ResourceBinding.SampledTexture(biasView), ResourceBinding.WritableBuffer(secondView)]
                : [ResourceBinding.SampledTexture(biasView), ResourceBinding.ReadOnlyBuffer(inputView), ResourceBinding.WritableBuffer(secondView)];
            backend.Begin(commands);
            backend.SetPipeline(commands, pipeline);
            if (index == 0)
            {
                backend.Barrier(commands, new TextureBarrier(bias, biasRange,
                    PipelineSync.None, PipelineSync.Copy, ResourceAccess.NoAccess, ResourceAccess.CopyDestination,
                    TextureLayout.Undefined, TextureLayout.CopyDestination));
                backend.CopyBufferToTexture(commands, new BufferTextureCopy(
                    biasUpload, 0, 256, 1, bias, 0, 0, TextureAspects.Color, 0, 0, 0, 1, 1, 1));
                backend.Barrier(commands, new TextureBarrier(bias, biasRange,
                    PipelineSync.Copy, PipelineSync.ComputeShading, ResourceAccess.CopyDestination, ResourceAccess.ShaderResource,
                    TextureLayout.CopyDestination, TextureLayout.ShaderResource));
            }
            if (index == 1)
                backend.Barrier(commands, new BufferBarrier(input, input.InitialSync, PipelineSync.ComputeShading,
                    input.InitialAccess, ResourceAccess.ShaderResource));
            foreach (Buffer output in new[] { firstOutput, secondOutput })
                backend.Barrier(commands, new BufferBarrier(output,
                    index == 0 ? output.InitialSync : PipelineSync.Copy, PipelineSync.ComputeShading,
                    index == 0 ? output.InitialAccess : ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
            BitConverter.TryWriteBytes(ordinary, 17u);
            backend.SetTransientParameterBindings(commands, new ParameterBlockBindings(globals, firstResources, ordinary));
            backend.Dispatch(commands, new DispatchArguments(1, 1, 1));
            BitConverter.TryWriteBytes(ordinary, 29u);
            backend.SetTransientParameterBindings(commands, new ParameterBlockBindings(globals, secondResources, ordinary));
            backend.Dispatch(commands, new DispatchArguments(1, 1, 1));
            foreach (Buffer output in new[] { firstOutput, secondOutput })
                backend.Barrier(commands, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
                    ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            backend.CopyBuffer(commands, new BufferCopy(firstOutput, 0, readback, 0, 4));
            backend.CopyBuffer(commands, new BufferCopy(secondOutput, 0, readback, 4, 4));
            using RecordedCommands recorded = backend.End(commands);
            QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [recorded], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
            using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
            result.Invalidate(result.Range);
            Assert.Equal(index == 0 ? 22u : 25u, BitConverter.ToUInt32(result.Bytes));
            Assert.Equal(index == 0 ? 34u : 37u, BitConverter.ToUInt32(result.Bytes[4..]));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlobalAndEntryResourcesSurviveBindingInEitherOrder(bool persistent)
    {
        const string source = """
            StructuredBuffer<uint> Values;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uniform RWStructuredBuffer<uint> Result)
            {
                Result[0] = Values[0] + 7u;
            }
            """;
        var shader = shaderCompiler.Compile(source, ("computeMain", SlangStage.Compute));
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        VariableLayoutReflection entry = shader.Entries[0].VarLayout;
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
        using Pipeline pipeline = backend.CreateComputePipeline(device, new ComputePipelineDesc(shader.SlangCode, shader.Reflection, shader.Entries[0], shader.GetEntryCode(0)));
        using Buffer input = backend.CreateBuffer(device, new BufferDesc(4, BufferUsages.ShaderRead), MemoryType.Upload);
        using (MappedBuffer mapping = backend.Map(input, MapType.Write, BufferRange.Whole))
        {
            BitConverter.TryWriteBytes(mapping.Bytes, 35u);
            mapping.Flush(mapping.Range);
        }
        using BufferSrv inputView = backend.CreateBufferSrv(device, new BufferSrvDesc(input, BufferRange.Whole, StructureStride: 4));
        using Buffer output = backend.CreateBuffer(device, new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputView = backend.CreateBufferUav(device, new BufferUavDesc(output, BufferRange.Whole, StructureStride: 4));
        using Buffer readback = backend.CreateBuffer(device, new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
        ResourceBinding[] globalResources = [ResourceBinding.ReadOnlyBuffer(inputView)];
        ResourceBinding[] entryResources = [ResourceBinding.WritableBuffer(outputView)];
        using PersistentParameterBindings globalBindings = backend.CreatePersistentParameterBindings(device, pipeline,
            new ParameterBlockBindings(globals, globalResources, []));
        using PersistentParameterBindings entryBindings = backend.CreatePersistentParameterBindings(device, pipeline,
            new ParameterBlockBindings(entry, entryResources, []));
        using CommandContext commands = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        for (int index = 0; index < 2; index++)
        {
            backend.Begin(commands);
            backend.SetPipeline(commands, pipeline);
            Bind(index == 0);
            Bind(index != 0);
            if (index == 0)
                backend.Barrier(commands, new BufferBarrier(input, input.InitialSync, PipelineSync.ComputeShading,
                    input.InitialAccess, ResourceAccess.ShaderResource));
            backend.Barrier(commands, new BufferBarrier(output,
                index == 0 ? output.InitialSync : PipelineSync.Copy, PipelineSync.ComputeShading,
                index == 0 ? output.InitialAccess : ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
            backend.Dispatch(commands, new DispatchArguments(1, 1, 1));
            backend.Barrier(commands, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            backend.CopyBuffer(commands, new BufferCopy(output, 0, readback, 0, 4));
            using RecordedCommands recorded = backend.End(commands);
            QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [recorded], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
            using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
            result.Invalidate(result.Range);
            Assert.Equal(42u, BitConverter.ToUInt32(result.Bytes));
        }

        void Bind(bool global)
        {
            if (persistent) backend.SetPersistentParameterBindings(commands, global ? globalBindings : entryBindings);
            else backend.SetTransientParameterBindings(commands, new ParameterBlockBindings(
                global ? globals : entry, global ? globalResources : entryResources, []));
        }
    }
}
