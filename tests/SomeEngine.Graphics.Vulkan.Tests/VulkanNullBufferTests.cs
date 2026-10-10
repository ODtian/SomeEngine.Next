using SlangShaderSharp;
using Xunit;

namespace SomeEngine.Graphics.Vulkan.Tests;

[Collection("Vulkan shader compilation")]
public sealed class VulkanNullBufferTests(VulkanTestCompiler shaderCompiler)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedNullBuffersReadZeroAndDiscardWrites(bool persistent)
    {
        const string source = """
            struct Parameters { uint Value; }
            ConstantBuffer<Parameters> Constants;
            StructuredBuffer<uint> Input;
            RWStructuredBuffer<uint> Discarded;
            RWStructuredBuffer<uint> Result;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain()
            {
                Discarded[0] = 99u;
                Result[0] = Constants.Value + Input[0] + 42u;
            }
            """;
        var shader = shaderCompiler.Compile(source, ("computeMain", SlangStage.Compute));
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.SlangCode, shader.Reflection, shader.Entries[0], shader.GetEntryCode(0)));
        using Buffer output = backend.CreateBuffer(device,
            new BufferDesc(sizeof(uint), BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputView = backend.CreateBufferUav(device,
            new BufferUavDesc(output, BufferRange.Whole, StructureStride: sizeof(uint)));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(sizeof(uint), BufferUsages.CopyDestination), MemoryType.Readback);
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        ResourceBinding[] resources =
        [
            ResourceBinding.Null(ResourceBindingType.ConstantBuffer),
            ResourceBinding.Null(ResourceBindingType.BufferSrv),
            ResourceBinding.Null(ResourceBindingType.BufferUav),
            ResourceBinding.WritableBuffer(outputView),
        ];
        using PersistentParameterBindings bindings = backend.CreatePersistentParameterBindings(device, pipeline,
            new ParameterBlockBindings(globals, resources, []));
        using CommandContext commands = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(commands);
        backend.Barrier(commands, new BufferBarrier(output, output.InitialSync, PipelineSync.ComputeShading,
            output.InitialAccess, ResourceAccess.UnorderedAccess));
        backend.SetPipeline(commands, pipeline);
        if (persistent) backend.SetPersistentParameterBindings(commands, bindings);
        else backend.SetTransientParameterBindings(commands, new ParameterBlockBindings(globals, resources, []));
        backend.Dispatch(commands, new DispatchArguments(1, 1, 1));
        backend.Barrier(commands, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(commands, new BufferCopy(output, 0, readback, 0, sizeof(uint)));
        using RecordedCommands recorded = backend.End(commands);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Graphics),
            new QueueSubmitDesc([], [], [recorded], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(result.Range);
        Assert.Equal(42u, BitConverter.ToUInt32(result.Bytes));
    }
}
