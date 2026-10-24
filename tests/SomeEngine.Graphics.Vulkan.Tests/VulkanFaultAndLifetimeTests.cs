namespace SomeEngine.Graphics.Vulkan.Tests;

using SlangShaderSharp;
using Xunit;

[Xunit.Collection("Vulkan shader compilation")]
public sealed class VulkanFaultAndLifetimeTests(VulkanTestCompiler shaderCompiler)
{






    [Fact]
    public async Task Concurrent_submit_and_collect_preserve_unique_monotonic_completions()
    {
        const int threadCount = 4;
        const int submissionsPerThread = 16;
        using var backend = VulkanGraphicsBackend.Create();
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
            Assert.Equal(commands.Length, values.Distinct().Count());
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
        VulkanTestShader shader = shaderCompiler.Compile(
            source,
            ("computeMain", SlangStage.Compute));
        using var backend = VulkanGraphicsBackend.Create();
        IGraphicsBackend graphics = backend;
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = graphics.CreateDevice(new DeviceDesc(default, queues));
        PipelineCache cache = graphics.CreatePipelineCache(device, default);
        Task<Pipeline> creation = graphics.CreateComputePipelineAsync(
            device,
            new ComputePipelineDesc(shader.SlangCode, shader.Reflection, shader.Entries[0], shader.GetEntryCode(0)),
            cache);
        cache.Dispose();

        using Pipeline pipeline = await creation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PipelineType.Compute, pipeline.Type);
    }

    [Fact]
    public async Task Concurrent_backend_and_device_dispose_join_one_release()
    {
        var backend = VulkanGraphicsBackend.Create();
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
        Assert.Throws<ObjectDisposedException>(() => backend.CreateDevice(new DeviceDesc(default, queues)));
    }
}
