using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit.Sdk;

namespace SomeEngine.RenderGraph.Tests;

public sealed class InitialWaitMembershipTests
{
    [Fact]
    public void ChangedInitialWaitResourcesPreserveQueryReuseAndIndependentSubmissions()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("D3D12 WARP requires Windows.");
        using IGraphicsBackend backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        using Device device = CreateDevice(backend);
        Queue sourceQueue = backend.GetQueue(device, QueueType.Graphics, 0);
        Queue targetQueue = backend.GetQueue(device, QueueType.Graphics, 1);
        Queue signalQueue = backend.GetQueue(device, QueueType.Copy);
        using ExternalTimeline gate = backend.CreateExternalTimeline(device, 0);
        using QueryPool pool = backend.CreateQueryPool(device,
            new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 1));
        using CommandContext sourceCommands = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        BufferDesc bufferDescription = new(16, BufferUsages.CopySource);
        using Buffer first = backend.CreateBuffer(device, bufferDescription, MemoryType.Upload);
        using Buffer second = backend.CreateBuffer(device, bufferDescription, MemoryType.Upload);
        foreach (Buffer buffer in new[] { first, second })
        {
            using MappedBuffer mapping = backend.Map(buffer, MapType.Write, BufferRange.Whole);
            mapping.Bytes.Clear();
            mapping.Flush(mapping.Range);
        }

        using var graph = new RenderGraph(backend, device, [targetQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        GraphBufferId firstBuffer, secondBuffer;
        GraphQueryPoolId queries;
        GraphPassId pass;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            firstBuffer = edit.DeclareExternalBuffer(bufferDescription, MemoryType.Upload);
            secondBuffer = edit.DeclareExternalBuffer(bufferDescription, MemoryType.Upload);
            queries = edit.RegisterQueryPool(pool);
            pass = edit.AddGeneralFramePass("query reset after external work",
                PassQueueSelection.Exact(targetQueue), default(byte),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref byte _) => { },
                static (ref GeneralPassCommandScope _, in byte _) => { });
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        QueueCompletion sourceCompletion = default;

        // Establish both external waits and the query reset on this frame's reusable pass.
        try
        {
            sourceCompletion = SubmitQueryReset(1);
            Assert.False(backend.IsComplete(sourceCompletion));
            using RenderGraphFrame frame = graph.BeginFrame();
            BindWaitBuffers(frame);
            PassDefinition access = frame.GetPass(pass);
            access.Read(firstBuffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            access.Read(secondBuffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            access.Write(queries, new QueryRange(0, 1), resultContents: ResourceContentState.Undefined);
            Assert.Equal(1, frame.Execute(completions));
            Assert.Same(targetQueue, completions[0].Queue);
        }
        finally
        {
            SignalAndDrain(1);
        }

        // Removing A must retain B's fresh completion wait for the foreign query reset.
        // This does not distinguish an extra A wait dominated by B's same completion.
        Array.Clear(completions);
        sourceCompletion = default;
        try
        {
            sourceCompletion = SubmitQueryReset(2);
            Assert.False(backend.IsComplete(sourceCompletion));
            using RenderGraphFrame frame = graph.BeginFrame();
            BindWaitBuffers(frame);
            PassDefinition access = frame.GetPass(pass);
            access.Read(secondBuffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            access.Write(queries, new QueryRange(0, 1), resultContents: ResourceContentState.Undefined);
            Assert.Equal(1, frame.Execute(completions));
            Assert.Same(targetQueue, completions[0].Queue);
        }
        finally
        {
            SignalAndDrain(2);
        }

        // With all accesses removed, target completion must precede releasing the source.
        Array.Clear(completions);
        sourceCompletion = default;
        try
        {
            sourceCompletion = SubmitQueryReset(3);
            Assert.False(backend.IsComplete(sourceCompletion));
            using RenderGraphFrame frame = graph.BeginFrame();
            BindWaitBuffers(frame);
            Assert.Equal(1, frame.Execute(completions));
            Assert.Same(targetQueue, completions[0].Queue);
            Assert.Equal(WaitStatus.Completed,
                backend.WaitCpu(completions[0], TimeSpan.FromSeconds(10)));
            Assert.False(backend.IsComplete(sourceCompletion));
        }
        finally
        {
            SignalAndDrain(3);
        }

        // Restore B and the query reset, requiring the current rather than an old completion.
        Array.Clear(completions);
        sourceCompletion = default;
        try
        {
            sourceCompletion = SubmitQueryReset(4);
            Assert.False(backend.IsComplete(sourceCompletion));
            using RenderGraphFrame frame = graph.BeginFrame();
            BindWaitBuffers(frame);
            PassDefinition access = frame.GetPass(pass);
            access.Read(secondBuffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            access.Write(queries, new QueryRange(0, 1), resultContents: ResourceContentState.Undefined);
            Assert.Equal(1, frame.Execute(completions));
            Assert.Same(targetQueue, completions[0].Queue);
        }
        finally
        {
            SignalAndDrain(4);
        }

        QueueCompletion SubmitQueryReset(ulong value)
        {
            backend.Begin(sourceCommands);
            backend.ResetQueries(sourceCommands, pool, 0, 1);
            using RecordedCommands commands = backend.End(sourceCommands);
            return backend.Submit(sourceQueue, new QueueSubmitDesc(
                [], [new TimelinePoint(gate, value)], [commands], [], []));
        }

        void BindWaitBuffers(RenderGraphFrame frame)
        {
            frame.BindExternalBuffer(firstBuffer, first,
                [new BufferBoundaryState(BufferRange.Whole, first.InitialSync, first.InitialAccess,
                    ResourceContentState.Defined, sourceQueue, sourceCompletion)]);
            frame.BindExternalBuffer(secondBuffer, second,
                [new BufferBoundaryState(BufferRange.Whole, second.InitialSync, second.InitialAccess,
                    ResourceContentState.Defined, sourceQueue, sourceCompletion)]);
        }

        void SignalAndDrain(ulong value)
        {
            // Signal on an independent queue before waiting, including after failed assertions.
            QueueCompletion signal = backend.Submit(signalQueue,
                new QueueSubmitDesc([], [], [], [], [new TimelineSignal(gate, value)]));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(signal, TimeSpan.FromSeconds(10)));
            if (sourceCompletion != default)
                Assert.Equal(WaitStatus.Completed,
                    backend.WaitCpu(sourceCompletion, TimeSpan.FromSeconds(10)));
            foreach (QueueCompletion completion in completions)
                if (completion != default)
                    Assert.Equal(WaitStatus.Completed,
                        backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public void RemovingOtherPassAccessesPreservesTheRestoredPassCompletionWait()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("D3D12 WARP requires Windows.");
        using IGraphicsBackend backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        using Device device = CreateDevice(backend);
        Queue sourceQueue = backend.GetQueue(device, QueueType.Graphics, 0);
        Queue targetQueue = backend.GetQueue(device, QueueType.Graphics, 1);
        Queue signalQueue = backend.GetQueue(device, QueueType.Copy);
        using ExternalTimeline gate = backend.CreateExternalTimeline(device, 0);
        QueryPoolDesc queryDescription = new(QueryType.Timestamp, QueueType.Graphics, 1);
        using QueryPool poolA = backend.CreateQueryPool(device, queryDescription);
        using QueryPool poolB = backend.CreateQueryPool(device, queryDescription);
        using QueryPool poolC = backend.CreateQueryPool(device, queryDescription);
        CommandContextDesc commandDescription = new(QueueType.Graphics, 0, 1);
        using CommandContext commandsA = backend.CreateCommandContext(device, commandDescription);
        using CommandContext commandsB = backend.CreateCommandContext(device, commandDescription);
        using CommandContext commandsC = backend.CreateCommandContext(device, commandDescription);
        BufferDesc bufferDescription = new(16, BufferUsages.CopySource);
        using Buffer bufferA = backend.CreateBuffer(device, bufferDescription, MemoryType.Upload);
        using Buffer bufferB = backend.CreateBuffer(device, bufferDescription, MemoryType.Upload);
        using Buffer bufferC = backend.CreateBuffer(device, bufferDescription, MemoryType.Upload);
        foreach (Buffer buffer in new[] { bufferA, bufferB, bufferC })
        {
            using MappedBuffer mapping = backend.Map(buffer, MapType.Write, BufferRange.Whole);
            mapping.Bytes.Clear();
            mapping.Flush(mapping.Range);
        }

        using var graph = new RenderGraph(backend, device, [targetQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        GraphBufferId resourceA, resourceB, resourceC;
        GraphQueryPoolId queriesA, queriesB, queriesC;
        GraphPassId passA, passB, passC;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            resourceA = edit.DeclareExternalBuffer(bufferDescription, MemoryType.Upload);
            resourceB = edit.DeclareExternalBuffer(bufferDescription, MemoryType.Upload);
            resourceC = edit.DeclareExternalBuffer(bufferDescription, MemoryType.Upload);
            queriesA = edit.RegisterQueryPool(poolA);
            queriesB = edit.RegisterQueryPool(poolB);
            queriesC = edit.RegisterQueryPool(poolC);
            passA = edit.AddGeneralFramePass("query reset A",
                PassQueueSelection.Exact(targetQueue), default(byte),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref byte _) => { },
                static (ref GeneralPassCommandScope _, in byte _) => { });
            passB = edit.AddGeneralFramePass("query reset B",
                PassQueueSelection.Exact(targetQueue), default(byte),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref byte _) => { },
                static (ref GeneralPassCommandScope _, in byte _) => { });
            passC = edit.AddGeneralFramePass("query reset C",
                PassQueueSelection.Exact(targetQueue), default(byte),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref byte _) => { },
                static (ref GeneralPassCommandScope _, in byte _) => { });
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        QueueCompletion sourceA = default, sourceB = default, sourceC = default;

        // Establish three passes with separate foreign completions.
        try
        {
            SubmitSourceResets(1);
            using RenderGraphFrame frame = graph.BeginFrame();
            BindWaitBuffers(frame);
            PassDefinition accessA = frame.GetPass(passA);
            accessA.Read(resourceA, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            accessA.Write(queriesA, new QueryRange(0, 1), resultContents: ResourceContentState.Undefined);
            PassDefinition accessB = frame.GetPass(passB);
            accessB.Read(resourceB, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            accessB.Write(queriesB, new QueryRange(0, 1), resultContents: ResourceContentState.Undefined);
            PassDefinition accessC = frame.GetPass(passC);
            accessC.Read(resourceC, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            accessC.Write(queriesC, new QueryRange(0, 1), resultContents: ResourceContentState.Undefined);
            Assert.Equal(1, frame.Execute(completions));
            Assert.Same(targetQueue, completions[0].Queue);
        }
        finally
        {
            SignalAndDrain(1);
        }

        // A remains a live pass, but only B and C retain resource accesses.
        Array.Clear(completions);
        sourceA = sourceB = sourceC = default;
        try
        {
            SubmitSourceResets(2);
            using RenderGraphFrame frame = graph.BeginFrame();
            BindWaitBuffers(frame);
            PassDefinition accessB = frame.GetPass(passB);
            accessB.Read(resourceB, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            accessB.Write(queriesB, new QueryRange(0, 1), resultContents: ResourceContentState.Undefined);
            PassDefinition accessC = frame.GetPass(passC);
            accessC.Read(resourceC, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            accessC.Write(queriesC, new QueryRange(0, 1), resultContents: ResourceContentState.Undefined);
            Assert.Equal(1, frame.Execute(completions));
            Assert.Same(targetQueue, completions[0].Queue);
        }
        finally
        {
            SignalAndDrain(2);
        }

        // Restore A after B and C have kept their accesses across frames.
        Array.Clear(completions);
        sourceA = sourceB = sourceC = default;
        try
        {
            SubmitSourceResets(3);
            using RenderGraphFrame frame = graph.BeginFrame();
            BindWaitBuffers(frame);
            PassDefinition accessA = frame.GetPass(passA);
            accessA.Read(resourceA, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            accessA.Write(queriesA, new QueryRange(0, 1), resultContents: ResourceContentState.Undefined);
            PassDefinition accessB = frame.GetPass(passB);
            accessB.Read(resourceB, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            accessB.Write(queriesB, new QueryRange(0, 1), resultContents: ResourceContentState.Undefined);
            PassDefinition accessC = frame.GetPass(passC);
            accessC.Read(resourceC, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            accessC.Write(queriesC, new QueryRange(0, 1), resultContents: ResourceContentState.Undefined);
            Assert.Equal(1, frame.Execute(completions));
            Assert.Same(targetQueue, completions[0].Queue);
        }
        finally
        {
            SignalAndDrain(3);
        }

        // Removing B must preserve restored A's later completion, which C cannot satisfy.
        Array.Clear(completions);
        sourceA = sourceB = sourceC = default;
        try
        {
            SubmitSourceResets(4);
            using RenderGraphFrame frame = graph.BeginFrame();
            BindWaitBuffers(frame);
            PassDefinition accessA = frame.GetPass(passA);
            accessA.Read(resourceA, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            accessA.Write(queriesA, new QueryRange(0, 1), resultContents: ResourceContentState.Undefined);
            PassDefinition accessC = frame.GetPass(passC);
            accessC.Read(resourceC, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            accessC.Write(queriesC, new QueryRange(0, 1), resultContents: ResourceContentState.Undefined);
            Assert.Equal(1, frame.Execute(completions));
            Assert.Same(targetQueue, completions[0].Queue);
        }
        finally
        {
            SignalAndDrain(4);
        }

        void SubmitSourceResets(ulong value)
        {
            // A must be later than B and C so their waits cannot hide a missing A wait.
            sourceB = SubmitQueryReset(commandsB, poolB, value);
            sourceC = SubmitQueryReset(commandsC, poolC, value);
            sourceA = SubmitQueryReset(commandsA, poolA, value);
            Assert.True(sourceB.Value < sourceC.Value);
            Assert.True(sourceC.Value < sourceA.Value);
            Assert.False(backend.IsComplete(sourceA));
        }

        QueueCompletion SubmitQueryReset(CommandContext context, QueryPool pool, ulong value)
        {
            backend.Begin(context);
            backend.ResetQueries(context, pool, 0, 1);
            using RecordedCommands commands = backend.End(context);
            return backend.Submit(sourceQueue, new QueueSubmitDesc(
                [], [new TimelinePoint(gate, value)], [commands], [], []));
        }

        void BindWaitBuffers(RenderGraphFrame frame)
        {
            frame.BindExternalBuffer(resourceA, bufferA,
                [new BufferBoundaryState(BufferRange.Whole, bufferA.InitialSync, bufferA.InitialAccess,
                    ResourceContentState.Defined, sourceQueue, sourceA)]);
            frame.BindExternalBuffer(resourceB, bufferB,
                [new BufferBoundaryState(BufferRange.Whole, bufferB.InitialSync, bufferB.InitialAccess,
                    ResourceContentState.Defined, sourceQueue, sourceB)]);
            frame.BindExternalBuffer(resourceC, bufferC,
                [new BufferBoundaryState(BufferRange.Whole, bufferC.InitialSync, bufferC.InitialAccess,
                    ResourceContentState.Defined, sourceQueue, sourceC)]);
        }

        void SignalAndDrain(ulong value)
        {
            // Release the source first, even if recording or submission failed partway through.
            QueueCompletion signal = backend.Submit(signalQueue,
                new QueueSubmitDesc([], [], [], [], [new TimelineSignal(gate, value)]));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(signal, TimeSpan.FromSeconds(10)));
            foreach (QueueCompletion completion in new[] { sourceA, sourceB, sourceC })
                if (completion != default)
                    Assert.Equal(WaitStatus.Completed,
                        backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            foreach (QueueCompletion completion in completions)
                if (completion != default)
                    Assert.Equal(WaitStatus.Completed,
                        backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        }
    }

    private static Device CreateDevice(IGraphicsBackend backend)
    {
        AdapterEnumerationOptions options = new(AdapterPreference.HighPerformance, IncludeSoftware: true);
        _ = backend.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        Assert.True(backend.TryEnumerateAdapters(options, adapters, out int confirmed));
        Assert.Equal(adapters.Length, confirmed);
        AdapterInfo warp = adapters.FirstOrDefault(static adapter => !adapter.HardwareAccelerated);
        if (warp.Name.Length == 0)
            throw SkipException.ForSkip("The D3D12 WARP adapter is unavailable.");
        return backend.CreateDevice(new DeviceDesc(warp.Id,
            [new DeviceQueueDesc(QueueType.Graphics, Count: 2), new DeviceQueueDesc(QueueType.Copy)],
            requiredFeatures: DeviceFeatures.ExternalTimelines,
            label: "Initial wait membership test"));
    }
}
