namespace SomeEngine.Graphics.Vulkan.Tests;

using SlangShaderSharp;
using Xunit;

public sealed class VulkanHotPathTests
{
    [Fact]
    public void RepeatedParameterBindingDispatchWritesExpectedBytes()
    {
        const string source = """
            float4 Tint;
            RWStructuredBuffer<float4> Output;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID) { Output[0] = Tint; }
            """;
        using VulkanTestShaderProgram shader = VulkanTestShaderProgram.Compile(
            source,
            ("computeMain", SlangStage.Compute));
        ShaderVariableLayout globals = shader.Reflection.GetGlobalParamsVarLayout()
            ?? ShaderVariableLayout.Null;
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.Entries[0]));
        using Buffer output = backend.CreateBuffer(
            device,
            new BufferDesc(16, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputView = backend.CreateBufferUav(
            device,
            new BufferUavDesc(output, BufferRange.Whole, StructureStride: 16));
        byte[] ordinary = new byte[16];
        ordinary.AsSpan().Fill(0x3F);
        ResourceBinding[] resources = [ResourceBinding.WritableBuffer(outputView)];
        using PersistentParameterBindings persistent =
            backend.CreatePersistentParameterBindings(
                device,
                pipeline,
                new ParameterBlockBindings(globals, resources, ordinary));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        var commands = new RecordedCommands[1];
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(16, BufferUsages.CopyDestination), MemoryType.Readback);

        bool firstDispatch = true;
        RecordAndSubmit();
        backend.CollectCompleted(device);

        RecordAndSubmit();
        backend.CollectCompleted(device);

        void RecordAndSubmit()
        {
            backend.Begin(context, new CommandRecordingDesc(4, 4, 4));
            backend.SetPipeline(context, pipeline);
            backend.SetPersistentParameterBindings(context, persistent);
            backend.SetTransientParameterBindings(
                context,
                new ParameterBlockBindings(globals, resources, ordinary));
            backend.Barrier(context, new BufferBarrier(output,
                firstDispatch ? PipelineSync.None : PipelineSync.Copy,
                PipelineSync.ComputeShading,
                firstDispatch ? ResourceAccess.NoAccess : ResourceAccess.CopySource,
                ResourceAccess.UnorderedAccess));
            firstDispatch = false;
            backend.Dispatch(context, new DispatchArguments(1, 1, 1));
            backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading,
                PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 16));
            RecordedCommands recorded = backend.End(context);
            commands[0] = recorded;
            QueueCompletion completion = backend.Submit(
                queue,
                new QueueSubmitDesc([], [], commands, [], []));
            recorded.Dispose();
            Assert.Equal(
                WaitStatus.Completed,
                backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
            using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
            result.Invalidate(result.Range);
            Assert.Equal(ordinary, result.Bytes.ToArray());
        }
    }
}
