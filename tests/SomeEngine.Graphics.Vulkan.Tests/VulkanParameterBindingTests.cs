using SlangShaderSharp;
using Xunit;

namespace SomeEngine.Graphics.Vulkan.Tests;

public sealed class VulkanParameterBindingTests
{
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
        var shader = VulkanTestShader.Compile(source, ("computeMain", SlangStage.Compute));
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
