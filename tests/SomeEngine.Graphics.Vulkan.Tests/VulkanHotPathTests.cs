namespace SomeEngine.Graphics.Vulkan.Tests;

using SlangShaderSharp;
using Xunit;

public sealed class VulkanHotPathTests
{
    [Fact]
    public void Warm_pipeline_and_parameter_binding_frame_allocates_no_managed_memory()
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
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.Entries[0]));
        using Buffer output = backend.CreateBuffer(
            device,
            new BufferDesc(16, BufferUsages.ShaderWrite));
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

        RecordAndSubmit(measure: false, out _);
        backend.CollectCompleted(device);

        RecordAndSubmit(measure: true, out long allocated);
        Assert.Equal(0, allocated);
        backend.CollectCompleted(device);

        void RecordAndSubmit(bool measure, out long allocated)
        {
            long before = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
            backend.Begin(context, new CommandRecordingDesc(4, 4, 4));
            long afterBegin = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
            backend.SetPipeline(context, pipeline);
            long afterPipeline = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
            backend.SetPersistentParameterBindings(context, persistent);
            long afterPersistent = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
            backend.SetTransientParameterBindings(
                context,
                new ParameterBlockBindings(globals, resources, ordinary));
            long afterTransient = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
            backend.Dispatch(context, new DispatchArguments(1, 1, 1));
            long afterDispatch = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
            RecordedCommands recorded = backend.End(context);
            long afterEnd = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
            commands[0] = recorded;
            QueueCompletion completion = backend.Submit(
                queue,
                new QueueSubmitDesc([], [], commands, [], []));
            allocated = measure
                ? GC.GetAllocatedBytesForCurrentThread() - before
                : 0;
            recorded.Dispose();
            if (measure && allocated != 0)
            {
                Assert.Fail(
                    $"Begin={afterBegin - before}, " +
                    $"Pipeline={afterPipeline - afterBegin}, " +
                    $"Persistent={afterPersistent - afterPipeline}, " +
                    $"Transient={afterTransient - afterPersistent}, " +
                    $"Dispatch={afterDispatch - afterTransient}, " +
                    $"End={afterEnd - afterDispatch}, " +
                    $"Submit={allocated - (afterEnd - before)}");
            }
            Assert.Equal(
                WaitStatus.Completed,
                backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        }
    }
}
