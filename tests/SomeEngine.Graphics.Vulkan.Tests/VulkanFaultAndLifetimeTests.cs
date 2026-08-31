namespace SomeEngine.Graphics.Vulkan.Tests;

using SlangShaderSharp;
using Xunit;
using VkResult = Silk.NET.Vulkan.Result;

public sealed class VulkanFaultAndLifetimeTests
{
    [Fact]
    public void Submit_out_of_memory_restores_commands_and_allows_retry()
    {
        using var backend = new VulkanBackend();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        using RecordedCommands commands = backend.End(context);
        backend.FaultHooks.OverrideResult = static (point, result) =>
            point == VulkanCallPoint.QueueSubmit ? VkResult.ErrorOutOfHostMemory : result;

        GraphicsException failure = Assert.Throws<GraphicsException>(() =>
            backend.Submit(
                queue,
                new QueueSubmitDesc([], [], [commands], [], [])));
        Assert.Equal(GraphicsError.OutOfMemory, failure.Error);
        Assert.Equal(RecordedCommandsStatus.Executable, commands.Status);

        backend.FaultHooks.Reset();
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        Assert.Equal(RecordedCommandsStatus.Completed, commands.Status);
    }

    [Fact]
    public void Submit_device_loss_is_terminal_and_reuses_the_first_exception()
    {
        using var backend = new VulkanBackend();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        using RecordedCommands commands = backend.End(context);
        backend.FaultHooks.OverrideResult = static (point, result) =>
            point == VulkanCallPoint.QueueSubmit ? VkResult.ErrorDeviceLost : result;

        GraphicsException first = Assert.Throws<GraphicsException>(() => backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [commands], [], [])));
        GraphicsException subsequent = Assert.Throws<GraphicsException>(() =>
            backend.CreateBuffer(device, new BufferDesc(16, BufferUsages.CopySource)));

        Assert.Equal(GraphicsError.DeviceLost, first.Error);
        Assert.Same(first, subsequent);
        Assert.Equal(RecordedCommandsStatus.DeviceLost, commands.Status);
    }

    [Fact]
    public void Allocation_out_of_memory_does_not_poison_the_device()
    {
        using var backend = new VulkanBackend();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        backend.FaultHooks.OverrideResult = static (point, result) =>
            point == VulkanCallPoint.AllocateMemory ? VkResult.ErrorOutOfDeviceMemory : result;

        GraphicsException failure = Assert.Throws<GraphicsException>(() =>
            backend.CreateBuffer(device, new BufferDesc(16, BufferUsages.CopySource)));
        Assert.Equal(GraphicsError.OutOfMemory, failure.Error);

        backend.FaultHooks.Reset();
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(16, BufferUsages.CopySource));
        Assert.Equal(16UL, buffer.Info.Size);
    }

    [Fact]
    public void Wait_device_loss_is_terminal_and_reuses_the_first_exception()
    {
        using var backend = new VulkanBackend();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [commands], [], []));
        backend.FaultHooks.OverrideResult = static (point, result) =>
            point == VulkanCallPoint.WaitSemaphores ? VkResult.ErrorDeviceLost : result;

        GraphicsException first = Assert.Throws<GraphicsException>(() =>
            backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        GraphicsException subsequent = Assert.Throws<GraphicsException>(() =>
            backend.CreateBuffer(device, new BufferDesc(16, BufferUsages.CopySource)));

        Assert.Equal(GraphicsError.DeviceLost, first.Error);
        Assert.Same(first, subsequent);
        Assert.Equal(RecordedCommandsStatus.DeviceLost, commands.Status);
    }

    [Fact]
    public void Pipeline_creation_device_loss_is_terminal()
    {
        const string source = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID) {}
            """;
        using VulkanTestShaderProgram shader = VulkanTestShaderProgram.Compile(
            source,
            ("computeMain", SlangStage.Compute));
        using var backend = new VulkanBackend();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        backend.FaultHooks.OverrideResult = static (point, result) =>
            point == VulkanCallPoint.CreatePipeline ? VkResult.ErrorDeviceLost : result;

        GraphicsException first = Assert.Throws<GraphicsException>(() =>
            backend.CreateComputePipeline(
                device,
                new ComputePipelineDesc(shader.Program, shader.Entries[0])));
        GraphicsException subsequent = Assert.Throws<GraphicsException>(() =>
            backend.CreateBuffer(device, new BufferDesc(16, BufferUsages.CopySource)));

        Assert.Equal(GraphicsError.DeviceLost, first.Error);
        Assert.Same(first, subsequent);
    }

    [Fact]
    public async Task Concurrent_submit_and_collect_preserve_unique_monotonic_completions()
    {
        const int threadCount = 4;
        const int submissionsPerThread = 16;
        using var backend = new VulkanBackend();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        var contexts = new CommandContext[threadCount];
        var commands = new RecordedCommands[threadCount * submissionsPerThread];
        var completions = new QueueCompletion[commands.Length];
        for (int index = 0; index < contexts.Length; index++)
        {
            contexts[index] = backend.CreateCommandContext(
                device,
                new CommandContextDesc(QueueType.Graphics, 0, submissionsPerThread));
        }
        using var start = new ManualResetEventSlim();
        int submittersRemaining = threadCount;
        try
        {
            Task[] submitters = Enumerable.Range(0, threadCount)
                .Select(thread => Task.Run(() =>
                {
                    start.Wait();
                    int first = thread * submissionsPerThread;
                    for (int offset = 0; offset < submissionsPerThread; offset++)
                    {
                        backend.Begin(contexts[thread]);
                        RecordedCommands recorded = backend.End(contexts[thread]);
                        commands[first + offset] = recorded;
                        completions[first + offset] = backend.Submit(
                            queue,
                            new QueueSubmitDesc([], [], [recorded], [], []));
                    }
                    Interlocked.Decrement(ref submittersRemaining);
                }))
                .ToArray();
            Task[] collectors = Enumerable.Range(0, threadCount)
                .Select(_ => Task.Run(() =>
                {
                    start.Wait();
                    while (Volatile.Read(ref submittersRemaining) != 0)
                    {
                        backend.CollectCompleted(device);
                        Thread.Yield();
                    }
                    backend.CollectCompleted(device);
                }))
                .ToArray();

            start.Set();
            await Task.WhenAll(submitters.Concat(collectors)).WaitAsync(TimeSpan.FromSeconds(15));
            foreach (QueueCompletion completion in completions)
            {
                Assert.Equal(
                    WaitStatus.Completed,
                    backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
            }

            ulong[] values = completions.Select(static completion => completion.Value).ToArray();
            Array.Sort(values);
            Assert.Equal(
                Enumerable.Range(1, commands.Length).Select(static value => (ulong)value),
                values);
            Assert.All(completions, completion => Assert.True(backend.IsComplete(completion)));
        }
        finally
        {
            foreach (RecordedCommands? recorded in commands)
                recorded?.Dispose();
            foreach (CommandContext context in contexts)
                context.Dispose();
        }
    }

    [Fact]
    public async Task Async_pipeline_retains_a_disposed_cache_until_native_creation_finishes()
    {
        const string source = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID) {}
            """;
        using VulkanTestShaderProgram shader = VulkanTestShaderProgram.Compile(
            source,
            ("computeMain", SlangStage.Compute));
        using var backend = new VulkanBackend();
        IGraphicsBackend graphics = backend;
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = graphics.CreateDevice(new DeviceDesc(default, queues));
        PipelineCache cache = graphics.CreatePipelineCache(device, default);
        using var reached = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        backend.FaultHooks.BeforeCall = point =>
        {
            if (point != VulkanCallPoint.CreatePipeline)
                return;
            reached.Set();
            Assert.True(proceed.Wait(TimeSpan.FromSeconds(5)));
        };

        Task<Pipeline> creation = graphics.CreateComputePipelineAsync(
            device,
            new ComputePipelineDesc(shader.Program, shader.Entries[0]),
            cache);
        Assert.True(reached.Wait(TimeSpan.FromSeconds(5)));
        cache.Dispose();
        proceed.Set();

        using Pipeline pipeline = await creation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PipelineType.Compute, pipeline.Type);
    }

    [Fact]
    public async Task Concurrent_backend_and_device_dispose_join_one_release()
    {
        var backend = new VulkanBackend();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        using var start = new ManualResetEventSlim();
        Task[] disposals = Enumerable.Range(0, 8)
            .Select(index => Task.Run(() =>
            {
                start.Wait();
                if ((index & 1) == 0)
                    backend.Dispose();
                else
                    device.Dispose();
            }))
            .ToArray();

        start.Set();
        await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(backend.ReleaseFailure);
    }
}
