using SlangShaderSharp;
using Xunit;

namespace SomeEngine.Graphics.Vulkan.Tests;

[Collection("Vulkan shader compilation")]
public sealed class SlangDescriptorSpaceTests(VulkanTestCompiler shaderCompiler)
{
    [Fact]
    public void AComputeResourceUsesItsDeclaredDescriptorSet()
    {
        var shader = shaderCompiler.Compile("""
            [[vk::binding(3, 2)]] RWStructuredBuffer<uint> Output;
            [shader("compute")] [numthreads(1,1,1)]
            void WriteValue() { Output[0] = 17; }
            """, ("WriteValue", SlangStage.Compute));
        VariableLayoutReflection parameters = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.SlangCode, shader.Reflection, shader.Entries[0], shader.GetEntryCode(0)));
        using Buffer output = backend.CreateBuffer(device, new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav view = backend.CreateBufferUav(device, new BufferUavDesc(output, BufferRange.Whole, StructureStride: 4));
        using Buffer readback = backend.CreateBuffer(device, new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext commands = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(commands);
        backend.SetPipeline(commands, pipeline);
        backend.SetTransientParameterBindings(commands, new ParameterBlockBindings(parameters,
            [ResourceBinding.WritableBuffer(view)], []));
        backend.Barrier(commands, new BufferBarrier(output, output.InitialSync, PipelineSync.ComputeShading,
            output.InitialAccess, ResourceAccess.UnorderedAccess));
        backend.Dispatch(commands, new(1, 1, 1));
        backend.Barrier(commands, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(commands, new BufferCopy(output, 0, readback, 0, 4));
        using RecordedCommands recorded = backend.End(commands);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Graphics),
            new QueueSubmitDesc([], [], [recorded], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        using MappedBuffer mapping = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapping.Invalidate(mapping.Range);
        Assert.Equal(17u, BitConverter.ToUInt32(mapping.Bytes));
    }
}
