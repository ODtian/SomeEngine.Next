using SlangShaderSharp;
using Xunit;

namespace SomeEngine.Graphics.Vulkan.Tests;

[Collection("Vulkan shader compilation")]
public sealed class VulkanNullBufferTests(VulkanTestCompiler shaderCompiler)
{
    private const string ReadNullBuffers = """
        struct Values { uint Value; };
        ConstantBuffer<Values> UniformValues;
        StructuredBuffer<uint> StorageValues;
        RWStructuredBuffer<uint> Result;
        [shader("compute")]
        [numthreads(1, 1, 1)]
        void computeMain()
        {
            Result[0] = UniformValues.Value;
            Result[1] = StorageValues[0];
            Result[2] = 0xdecafbadu;
        }
        """;

    private const string NullStorageWriteShader = """
        RWStructuredBuffer<uint> NullOutput;
        RWStructuredBuffer<uint> Result;
        [shader("compute")]
        [numthreads(1, 1, 1)]
        void computeMain()
        {
            NullOutput[0] = 99u;
            Result[0] = 42u;
        }
        """;

    [Fact]
    public void PersistentNullStorageBufferWritesPreserveTheFollowingOutput()
    {
        VulkanTestShader shader = shaderCompiler.Compile(NullStorageWriteShader, ("computeMain", SlangStage.Compute));
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.SlangCode, shader.Reflection, shader.Entries[0], shader.GetEntryCode(0)));
        using Buffer output = backend.CreateBuffer(device,
            new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputView = backend.CreateBufferUav(device,
            new BufferUavDesc(output, BufferRange.Whole, StructureStride: sizeof(uint)));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
        ResourceBinding[] resources =
        [
            ResourceBinding.Null(ResourceBindingType.BufferUav),
            ResourceBinding.WritableBuffer(outputView),
        ];
        using PersistentParameterBindings bindings = backend.CreatePersistentParameterBindings(device, pipeline,
            new ParameterBlockBindings(globals, resources, []));
        using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context);
        backend.SetPipeline(context, pipeline);
        backend.SetPersistentParameterBindings(context, bindings);
        backend.Barrier(context, new BufferBarrier(output, output.InitialSync, PipelineSync.ComputeShading,
            output.InitialAccess, ResourceAccess.UnorderedAccess));
        backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 4));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        Assert.Equal(42u, BitConverter.ToUInt32(mapped.Bytes));
    }

    [Fact]
    public void TransientNullStorageBufferWritesPreserveTheFollowingOutput()
    {
        VulkanTestShader shader = shaderCompiler.Compile(NullStorageWriteShader, ("computeMain", SlangStage.Compute));
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.SlangCode, shader.Reflection, shader.Entries[0], shader.GetEntryCode(0)));
        using Buffer output = backend.CreateBuffer(device,
            new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputView = backend.CreateBufferUav(device,
            new BufferUavDesc(output, BufferRange.Whole, StructureStride: sizeof(uint)));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
        ResourceBinding[] resources =
        [
            ResourceBinding.Null(ResourceBindingType.BufferUav),
            ResourceBinding.WritableBuffer(outputView),
        ];
        using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context);
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context, new ParameterBlockBindings(globals, resources, []));
        backend.Barrier(context, new BufferBarrier(output, output.InitialSync, PipelineSync.ComputeShading,
            output.InitialAccess, ResourceAccess.UnorderedAccess));
        backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 4));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        Assert.Equal(42u, BitConverter.ToUInt32(mapped.Bytes));
    }

    [Fact]
    public void PersistentNullUniformAndStorageBuffersReadZero()
    {
        VulkanTestShader shader = shaderCompiler.Compile(ReadNullBuffers, ("computeMain", SlangStage.Compute));
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.SlangCode, shader.Reflection, shader.Entries[0], shader.GetEntryCode(0)));
        using Buffer output = backend.CreateBuffer(device,
            new BufferDesc(12, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputView = backend.CreateBufferUav(device,
            new BufferUavDesc(output, BufferRange.Whole, StructureStride: sizeof(uint)));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(12, BufferUsages.CopyDestination), MemoryType.Readback);
        ResourceBinding[] resources =
        [
            ResourceBinding.Null(ResourceBindingType.ConstantBuffer),
            ResourceBinding.Null(ResourceBindingType.BufferSrv),
            ResourceBinding.WritableBuffer(outputView),
        ];
        using PersistentParameterBindings bindings = backend.CreatePersistentParameterBindings(device, pipeline,
            new ParameterBlockBindings(globals, resources, []));
        using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context);
        backend.SetPipeline(context, pipeline);
        backend.SetPersistentParameterBindings(context, bindings);
        backend.Barrier(context, new BufferBarrier(output, output.InitialSync, PipelineSync.ComputeShading,
            output.InitialAccess, ResourceAccess.UnorderedAccess));
        backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 12));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        Assert.Equal(0u, BitConverter.ToUInt32(mapped.Bytes[..4]));
        Assert.Equal(0u, BitConverter.ToUInt32(mapped.Bytes.Slice(4, 4)));
        Assert.Equal(0xdecafbadu, BitConverter.ToUInt32(mapped.Bytes.Slice(8, 4)));
    }

    [Fact]
    public void TransientNullUniformAndStorageBuffersReadZero()
    {
        VulkanTestShader shader = shaderCompiler.Compile(ReadNullBuffers, ("computeMain", SlangStage.Compute));
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.SlangCode, shader.Reflection, shader.Entries[0], shader.GetEntryCode(0)));
        using Buffer output = backend.CreateBuffer(device,
            new BufferDesc(12, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputView = backend.CreateBufferUav(device,
            new BufferUavDesc(output, BufferRange.Whole, StructureStride: sizeof(uint)));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(12, BufferUsages.CopyDestination), MemoryType.Readback);
        ResourceBinding[] resources =
        [
            ResourceBinding.Null(ResourceBindingType.ConstantBuffer),
            ResourceBinding.Null(ResourceBindingType.BufferSrv),
            ResourceBinding.WritableBuffer(outputView),
        ];
        using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context);
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context, new ParameterBlockBindings(globals, resources, []));
        backend.Barrier(context, new BufferBarrier(output, output.InitialSync, PipelineSync.ComputeShading,
            output.InitialAccess, ResourceAccess.UnorderedAccess));
        backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 12));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        Assert.Equal(0u, BitConverter.ToUInt32(mapped.Bytes[..4]));
        Assert.Equal(0u, BitConverter.ToUInt32(mapped.Bytes.Slice(4, 4)));
        Assert.Equal(0xdecafbadu, BitConverter.ToUInt32(mapped.Bytes.Slice(8, 4)));
    }
}
