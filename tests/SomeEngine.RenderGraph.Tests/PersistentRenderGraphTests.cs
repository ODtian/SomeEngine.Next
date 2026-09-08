using SomeEngine.Graphics;
using SomeEngine.RenderGraph.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Diagnostics;
using SomeEngine.Trace;

namespace SomeEngine.RenderGraph.Tests;

public sealed class PersistentRenderGraphTests
{
    private const int ByteCount = 256;

    [Fact]
    public void EditCommitIsAtomicAndInvalidatesRemovedIds()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);

        GraphBufferId first;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            first = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.Commit();
        }
        ulong version = graph.StructureVersion;

        using (RenderGraphEdit abandoned = graph.BeginEdit())
            _ = abandoned.CreateTransientBuffer(new BufferDesc(64, BufferUsages.CopySource));
        Assert.Equal(version, graph.StructureVersion);

        GraphBufferId replacement;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.Remove(first);
            replacement = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.Commit();
        }
        Assert.NotEqual(first, replacement);
        Assert.Equal(version + 1, graph.StructureVersion);

        using RenderGraphEdit stale = graph.BeginEdit();
        bool rejected = false;
        try
        {
            stale.Remove(first);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        Assert.True(rejected, "The removed GraphBufferId generation must be rejected.");
    }

    [Fact]
    public void ResourceKindsUseDistinctIdentityDomains()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);

        GraphBufferId buffer;
        GraphTextureId texture;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            buffer = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopyDestination));
            texture = edit.CreateTransientTexture(new TextureDesc(
                TextureDimension.Texture2D,
                width: 1,
                height: 1,
                depth: 1,
                mipLevelCount: 1,
                arrayLayerCount: 1,
                sampleCount: 1,
                format: Format.R8G8B8A8UNorm,
                usages: TextureUsages.CopyDestination));
            edit.Commit();
        }

        Assert.Equal(buffer.Value.Owner, texture.Value.Owner);
        Assert.Equal(buffer.Value.Slot, texture.Value.Slot);
        Assert.Equal(buffer.Value.Generation, texture.Value.Generation);
        Assert.Equal(GraphIdentityDomain.Buffer, buffer.Value.Domain);
        Assert.Equal(GraphIdentityDomain.Texture, texture.Value.Domain);
        Assert.NotEqual(buffer.Value, texture.Value);

        using RenderGraphFrame frame = graph.BeginFrame();
        GraphBufferId frameBuffer = frame.CreateBuffer(new BufferDesc(
            64,
            BufferUsages.CopyDestination));
        GraphTextureId frameTexture = frame.CreateTexture(new TextureDesc(
            TextureDimension.Texture2D,
            width: 1,
            height: 1,
            depth: 1,
            mipLevelCount: 1,
            arrayLayerCount: 1,
            sampleCount: 1,
            format: Format.R8G8B8A8UNorm,
            usages: TextureUsages.CopyDestination));
        Assert.Equal(frameBuffer.Value.Owner, frameTexture.Value.Owner);
        Assert.Equal(frameBuffer.Value.Slot, frameTexture.Value.Slot);
        Assert.Equal(frameBuffer.Value.Generation, frameTexture.Value.Generation);
        Assert.Equal(GraphIdentityDomain.Buffer, frameBuffer.Value.Domain);
        Assert.Equal(GraphIdentityDomain.Texture, frameTexture.Value.Domain);
        Assert.NotEqual(frameBuffer.Value, frameTexture.Value);
    }

#if DEBUG
    [Fact]
    public void FrameBufferRangesRejectOutOfBoundsValuesInDevelopment()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);

        GraphBufferId buffer;
        GraphBufferCbvId view;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            buffer = edit.DeclareExternalBuffer(new BufferDesc(
                256,
                BufferUsages.Constant));
            view = edit.CreateBufferCbv(buffer, new BufferRange(0, 64));
            edit.Commit();
        }

        using RenderGraphFrame frame = graph.BeginFrame();
        frame.SetBufferRange(buffer, new BufferRange(64, 64));
        frame.SetBufferRange(view, new BufferRange(128, 64));
        bool bufferRejected = false;
        try { frame.SetBufferRange(buffer, new BufferRange(240, 32)); }
        catch (InvalidOperationException) { bufferRejected = true; }
        bool viewRejected = false;
        try { frame.SetBufferRange(view, new BufferRange(240, 32)); }
        catch (InvalidOperationException) { viewRejected = true; }
        Assert.True(bufferRejected);
        Assert.True(viewRejected);
    }
#endif

    [Fact]
    public void PersistentGraphExecutesTwoCrossQueueFrames()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer upload = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using var graph = BuildCopyGraph(support, upload, readback);

        ExecuteAndVerify(support, graph, upload, readback, 17);
        ExecuteAndVerify(support, graph, upload, readback, 91);
        Assert.Equal(1UL, graph.StructureVersion);
    }

    [Fact]
    public void CrossQueueReaderHandoffPublishesOnlyTheFinalOwner()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue]);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId buffer = edit.CreatePersistentBuffer(new BufferDesc(
                64,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphPassId write = AddClear(edit, "write on copy", support.CopyQueue, buffer, 7);
            GraphPassId computeRead = AddRead(edit, "read on compute", support.ComputeQueue, buffer);
            GraphPassId graphicsRead = AddRead(edit, "read on graphics", support.GraphicsQueue, buffer);
            edit.OrderAfter(computeRead, write);
            edit.OrderAfter(graphicsRead, computeRead);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 2; frameIndex++)
        {
            Array.Clear(completions);
            int submitted;
            using (RenderGraphFrame frame = graph.BeginFrame())
                submitted = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, submitted));
        }
    }

    [Fact]
    public void MixedBufferAccessPublishesTheMergedPassState()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.ComputeQueue, support.CopyQueue]);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId buffer = edit.CreatePersistentBuffer(new BufferDesc(
                64,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite |
                    BufferUsages.CopyDestination));
            GraphPassId reset = AddClear(edit, "mixed-state reset", support.CopyQueue, buffer, 0);
            GraphPassId mixed = edit.AddComputePass<GraphBufferId, byte>(
                "mixed read and write",
                PassQueueSelection.Exact(support.ComputeQueue),
                buffer,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref GraphBufferId resource) =>
                {
                    _ = access.Write(
                        resource,
                        new BufferRange(0, 64),
                        PipelineSync.ComputeShading,
                        ResourceAccess.UnorderedAccess,
                        WriteCoverage.Partial);
                    _ = access.Read(
                        resource,
                        new BufferRange(0, 64),
                        PipelineSync.ComputeShading,
                        ResourceAccess.ShaderResource);
                },
                static (ref ComputePassCommandScope _, in GraphBufferId _, in byte _) => { });
            edit.OrderAfter(mixed, reset);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 2; frameIndex++)
        {
            Array.Clear(completions);
            int submitted;
            using (RenderGraphFrame frame = graph.BeginFrame())
                submitted = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, submitted));
        }
    }

    [Fact]
    public void PartialBufferReadPublishesWholeResourcePhysicalState()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId buffer = edit.CreatePersistentBuffer(new BufferDesc(
                64,
                BufferUsages.CopyDestination | BufferUsages.Indirect));
            GraphPassId reset = AddClear(
                edit,
                "partial-state reset",
                support.GraphicsQueue,
                buffer,
                0);
            GraphPassId partialRead = edit.AddComputePass<GraphBufferId, byte>(
                "partial indirect read",
                PassQueueSelection.Exact(support.GraphicsQueue),
                buffer,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref GraphBufferId resource) =>
                    _ = access.Read(
                        resource,
                        new BufferRange(0, 12),
                        PipelineSync.ExecuteIndirect,
                        ResourceAccess.IndirectArgument),
                static (ref ComputePassCommandScope _, in GraphBufferId _, in byte _) => { });
            edit.OrderAfter(partialRead, reset);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 2; frameIndex++)
        {
            Array.Clear(completions);
            int submitted;
            using (RenderGraphFrame frame = graph.BeginFrame())
                submitted = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, submitted));
        }
    }

    [Fact]
    public void PartialCrossQueueReadPublishesWholeResourceOwnership()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue]);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId buffer = edit.CreatePersistentBuffer(new BufferDesc(
                64,
                BufferUsages.CopyDestination | BufferUsages.ShaderWrite |
                    BufferUsages.Indirect));
            GraphPassId reset = AddClear(
                edit,
                "ownership reset",
                support.CopyQueue,
                buffer,
                0);
            GraphPassId write = edit.AddComputePass<GraphBufferId, byte>(
                "whole compute write",
                PassQueueSelection.Exact(support.ComputeQueue),
                buffer,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref GraphBufferId resource) =>
                    _ = access.Write(
                        resource,
                        new BufferRange(0, 64),
                        PipelineSync.ComputeShading,
                        ResourceAccess.UnorderedAccess,
                        WriteCoverage.Partial),
                static (ref ComputePassCommandScope _, in GraphBufferId _, in byte _) => { });
            GraphPassId partialRead = edit.AddComputePass<GraphBufferId, byte>(
                "partial cross-queue indirect read",
                PassQueueSelection.Exact(support.GraphicsQueue),
                buffer,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref GraphBufferId resource) =>
                    _ = access.Read(
                        resource,
                        new BufferRange(0, 12),
                        PipelineSync.ExecuteIndirect,
                        ResourceAccess.IndirectArgument),
                static (ref ComputePassCommandScope _, in GraphBufferId _, in byte _) => { });
            edit.OrderAfter(write, reset);
            edit.OrderAfter(partialRead, write);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 2; frameIndex++)
        {
            Array.Clear(completions);
            int submitted;
            using (RenderGraphFrame frame = graph.BeginFrame())
                submitted = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, submitted));
        }
    }

    [Fact]
    public void DynamicResourceIndexReuseDoesNotAliasAcrossQueueOwners()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];

        ExecuteUpload(support.GraphicsQueue, "graphics-owned dynamic upload");
        ExecuteUpload(support.CopyQueue, "copy-owned dynamic upload");

        void ExecuteUpload(Queue queue, string label)
        {
            Array.Clear(completions);
            int submitted;
            using (RenderGraphFrame frame = graph.BeginFrame())
            {
                GraphBufferSlice upload = frame.Upload(
                    new byte[64],
                    BufferUsages.CopySource,
                    label);
                _ = frame.AddCopyPass(
                    $"consume {label}",
                    PassQueueSelection.Exact(queue),
                    upload,
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition access, ref GraphBufferSlice resource) =>
                        _ = access.Read(
                            resource.Buffer,
                            resource.Range,
                            PipelineSync.Copy,
                            ResourceAccess.CopySource),
                    static (ref CopyPassCommandScope _, in GraphBufferSlice _) => { });
                submitted = frame.Execute(completions);
            }
            support.Wait(completions.AsSpan(0, submitted));
        }
    }

    [Fact]
    public void ExternalBufferFirstUseAcquiresFromItsPublishedQueue()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer buffer = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(64, BufferUsages.CopyDestination, "external first-use buffer"));
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId resource = edit.RegisterExternalBuffer(
                buffer,
                [Endpoint(buffer, support.GraphicsQueue, ResourceContentState.Undefined)]);
            _ = edit.AddCopyPass<GraphBufferId, byte>(
                "write acquired external buffer",
                PassQueueSelection.Exact(support.CopyQueue),
                resource,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref GraphBufferId target) =>
                    _ = access.Write(
                        target,
                        new BufferRange(0, 64),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete),
                static (ref CopyPassCommandScope _, in GraphBufferId _, in byte _) => { });
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        int submitted;
        using (RenderGraphFrame frame = graph.BeginFrame())
            submitted = frame.Execute(completions);
        support.Wait(completions.AsSpan(0, submitted));
    }

    [Fact]
    public void ExternalTextureFirstUseAcquiresFromItsPublishedQueue()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        TextureDesc description = new(
            TextureDimension.Texture2D,
            4,
            4,
            1,
            1,
            1,
            1,
            Format.R8G8B8A8UNorm,
            TextureUsages.CopyDestination,
            label: "external first-use texture");
        using Texture texture = support.Backend.CreateTexture(support.Device, description);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
            GraphTextureId resource = edit.RegisterExternalTexture(
                texture,
                [new TextureBoundaryState(
                    range,
                    texture.InitialSync,
                    texture.InitialAccess,
                    texture.InitialLayout,
                    ResourceContentState.Undefined,
                    support.GraphicsQueue)]);
            _ = edit.AddCopyPass<GraphTextureId, byte>(
                "write acquired external texture",
                PassQueueSelection.Exact(support.CopyQueue),
                resource,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition access, ref GraphTextureId target) =>
                    _ = access.Write(
                        target,
                        range,
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        TextureLayout.CopyDestination,
                        WriteCoverage.Complete),
                static (ref CopyPassCommandScope _, in GraphTextureId _, in byte _) => { });
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        int submitted;
        using (RenderGraphFrame frame = graph.BeginFrame())
            submitted = frame.Execute(completions);
        support.Wait(completions.AsSpan(0, submitted));
    }

    [Fact]
    public void IncomingAcquireCanFollowIndependentCommandsInTheSameRecording()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer local = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(64, BufferUsages.CopyDestination, "copy-local work"));
        using Buffer transferred = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(64, BufferUsages.CopyDestination, "later acquired work"));
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId localResource = edit.RegisterExternalBuffer(
                local,
                [Endpoint(local, support.CopyQueue, ResourceContentState.Undefined)]);
            GraphBufferId transferredResource = edit.RegisterExternalBuffer(
                transferred,
                [Endpoint(transferred, support.GraphicsQueue, ResourceContentState.Undefined)]);
            GraphPassId first = AddClear(
                edit,
                "copy-local pass before acquire",
                support.CopyQueue,
                localResource,
                1);
            GraphPassId second = AddClear(
                edit,
                "later pass with incoming acquire",
                support.CopyQueue,
                transferredResource,
                2);
            edit.OrderAfter(second, first);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        int submitted;
        using (RenderGraphFrame frame = graph.BeginFrame())
            submitted = frame.Execute(completions);
        support.Wait(completions.AsSpan(0, submitted));
    }

    [Theory]
    [InlineData(FrameSubmissionMode.Pipelined, false)]
    [InlineData(FrameSubmissionMode.Pipelined, true)]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit, false)]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit, true)]
    public void SubmissionMergingPreservesQueueDependenciesAndReadback(
        FrameSubmissionMode mode, bool crossesBack)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        Queue[] queues = [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue, support.GraphicsQueue];
        var outputs = new List<Buffer>();
        string path = Path.Combine(Path.GetTempPath(), $"graph-merging-{Guid.NewGuid():N}.setrace");
        try
        {
            using (RenderGraphEdit edit = graph.BeginEdit())
            {
                var passes = new GraphPassId[4];
                for (int index = 0; index < 4; index++)
                {
                    Buffer output = support.Backend.CreateBuffer(support.Device,
                        new BufferDesc(64, BufferUsages.CopyDestination), MemoryType.Readback);
                    outputs.Add(output);
                    GraphBufferId resource = edit.RegisterExternalBuffer(output,
                        [Endpoint(output, queues[index], ResourceContentState.Undefined)]);
                    passes[index] = AddClear(edit, $"merge output {index}", queues[index], resource, (uint)(index + 1));
                }
                edit.OrderAfter(passes[1], passes[0]);
                edit.OrderAfter(passes[3], passes[crossesBack ? 1 : 2]);
                edit.Commit();
            }
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            var options = new RenderGraphFrameOptions(mode,
                RenderGraphDebugOptions.DeclarationOrderScheduling | RenderGraphDebugOptions.DisableParallelRecording);
            using (EngineTrace.Start(path, new TraceOptions { Detail = true, BufferMegabytes = 4 }))
            {
                for (int frameIndex = 1; frameIndex <= 2; frameIndex++)
                {
                    using var trace = EngineTrace.BeginFrame(frameIndex);
                    using RenderGraphFrame frame = graph.BeginFrame(options);
                    int count = frame.Execute(completions);
                    support.Wait(completions.AsSpan(0, count));
                    for (int index = 0; index < outputs.Count; index++)
                    {
                        using MappedBuffer mapped = support.Backend.Map(outputs[index], MapType.Read, new(0, 64));
                        mapped.Invalidate(new(0, 64));
                        foreach (uint value in MemoryMarshal.Cast<byte, uint>(mapped.Bytes))
                            Assert.Equal((uint)(index + 1), value);
                    }
                }
            }
            TraceData data = TraceFile.Read(path);
            Assert.Equal(0, data.Misuse);
            Assert.All(data.Threads, thread => Assert.Equal(0, thread.Dropped));
            int submitSite = data.Sites.Single(site => site.Name == "ID3D12CommandQueue.ExecuteCommandLists").Id;
            for (int frameIndex = 1; frameIndex <= 2; frameIndex++)
                Assert.Equal(crossesBack ? 4 : 3, data.Events.Count(value =>
                    value.Event.Frame == frameIndex && value.Event.Kind == TraceEventKind.Begin &&
                    value.Event.Site == submitSite));
        }
        finally
        {
            foreach (Buffer output in outputs) output.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void SameQueueDependenciesInheritCrossQueueWavesAndCompletionsAreCompact()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer readback = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(64, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue]);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId gate = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphBufferId value = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphBufferId readbackId = edit.RegisterExternalBuffer(
                readback,
                [Endpoint(readback, support.CopyQueue, ResourceContentState.Undefined)]);
            _ = AddClear(edit, "wave gate", support.CopyQueue, gate, 1);
            GraphPassId delayed = edit.AddCopyPass<DependentClearState, byte>(
                "cross-queue delayed clear",
                PassQueueSelection.Exact(support.ComputeQueue),
                new DependentClearState(gate, value, 333),
                default,
                static (ref PassDefinition access, ref DependentClearState state) =>
                {
                    _ = access.Read(
                        state.Gate,
                        new BufferRange(0, 64),
                        PipelineSync.Copy,
                        ResourceAccess.CopySource);
                    _ = access.Write(
                        state.Buffer,
                        new BufferRange(0, 64),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands,
                    in DependentClearState state,
                    in byte _) => commands.ClearBuffer(
                        commands.GetBuffer(state.Buffer),
                        new BufferRange(0, 64),
                        state.Value));
            _ = AddClear(edit, "same-queue successor", support.ComputeQueue, value, 444);
            GraphPassId observe = AddCopy(
                edit,
                "observe wave order",
                support.CopyQueue,
                new CopyState(value, readbackId, 64));
            edit.OrderAfter(observe, delayed);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        int submitted;
        using (RenderGraphFrame frame = graph.BeginFrame())
            submitted = frame.Execute(completions);
        Assert.Equal(2, submitted);
        Assert.NotEqual(default, completions[0]);
        Assert.NotEqual(default, completions[1]);
        Assert.Equal(default, completions[2]);
        support.Wait(completions.AsSpan(0, submitted));

        using MappedBuffer mapping = support.Backend.Map(
            readback,
            MapType.Read,
            new BufferRange(0, 64));
        mapping.Invalidate(new BufferRange(0, 64));
        Assert.Equal(444u, BitConverter.ToUInt32(mapping.Bytes));
    }

    [Fact]
    public void DeclaredExternalSlotsBindPerFrameResources()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer source = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer destination = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);

        GraphBufferId sourceSlot;
        GraphBufferId destinationSlot;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            sourceSlot = edit.DeclareExternalBuffer(
                new BufferDesc(ByteCount, BufferUsages.CopySource),
                MemoryType.Upload);
            destinationSlot = edit.DeclareExternalBuffer(
                new BufferDesc(ByteCount, BufferUsages.CopyDestination),
                MemoryType.Readback);
            _ = edit.AddCopyPass<CopyState, byte>(
                "external slots",
                PassQueueSelection.Exact(support.GraphicsQueue),
                new CopyState(sourceSlot, destinationSlot, ByteCount),
                default,
                DeclareCopy,
                static (ref CopyPassCommandScope commands, in CopyState copy, in byte _) =>
                    RecordCopy(ref commands, copy));
            edit.Commit();
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.BindExternalBuffer(sourceSlot, source,
                [Endpoint(source, support.GraphicsQueue, ResourceContentState.Defined)]);
            frame.BindExternalBuffer(destinationSlot, destination,
                [Endpoint(destination, support.GraphicsQueue, ResourceContentState.Undefined)]);
            Assert.Equal(1, frame.Execute(completions));
        }
        support.Wait(completions);
    }

    [Fact]
    public void DynamicBufferAccessIdsDriveTheRecordedRange()
    {
        const ulong offset = 16;
        const ulong size = 32;
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer source = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer destination = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);

        GraphPassId pass;
        GraphBufferAccessId sourceAccess = default;
        GraphBufferAccessId destinationAccess = default;
        CopyState copy;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId sourceId = edit.RegisterExternalBuffer(source,
                [Endpoint(source, support.GraphicsQueue, ResourceContentState.Defined)]);
            GraphBufferId destinationId = edit.RegisterExternalBuffer(destination,
                [Endpoint(destination, support.GraphicsQueue, ResourceContentState.Undefined)]);
            copy = new CopyState(sourceId, destinationId, size);
            pass = edit.AddCopyPass<CopyState, DynamicCopyFrame>(
                "dynamic range copy",
                PassQueueSelection.Exact(support.GraphicsQueue),
                copy,
                default,
                (ref PassDefinition definition, ref CopyState state) =>
                {
                    sourceAccess = definition.Read(
                        state.Source,
                        new BufferRange(0, 1),
                        PipelineSync.Copy,
                        ResourceAccess.CopySource,
                        dynamicRange: true);
                    destinationAccess = definition.Write(
                        state.Destination,
                        new BufferRange(0, 1),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete,
                        dynamicRange: true);
                },
                static (ref CopyPassCommandScope commands,
                    in CopyState definition,
                    in DynamicCopyFrame frame) =>
                {
                    commands.CopyBuffer(new BufferCopy(
                        commands.GetBuffer(definition.Source),
                        frame.Offset,
                        commands.GetBuffer(definition.Destination),
                        frame.Offset,
                        frame.Size));
                });
            edit.Commit();
        }

        byte[] expected = new byte[checked((int)ByteCount)];
        for (int index = 0; index < expected.Length; index++)
            expected[index] = unchecked((byte)(index * 17 + 3));
        using (MappedBuffer mapped = support.Backend.Map(
            source,
            MapType.Write,
            new BufferRange(0, ByteCount)))
        {
            expected.CopyTo(mapped.Bytes);
            mapped.Flush(new BufferRange(0, ByteCount));
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetPassData(pass, new DynamicCopyFrame(offset, size));
            frame.SetBufferRange(sourceAccess, new BufferRange(offset, size));
            frame.SetBufferRange(destinationAccess, new BufferRange(offset, size));
            Assert.Equal(1, frame.Execute(completions));
        }
        support.Wait(completions);

        byte[] actual = new byte[checked((int)size)];
        using (MappedBuffer mapped = support.Backend.Map(
            destination,
            MapType.Read,
            new BufferRange(offset, size)))
        {
            mapped.Invalidate(new BufferRange(offset, size));
            mapped.Bytes.CopyTo(actual);
        }
        Assert.Equal(
            expected.AsSpan(checked((int)offset), checked((int)size)).ToArray(),
            actual);
    }

    [Fact]
    public void DynamicTextureAccessIdsDriveTheRecordedSubresource()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);
        GraphTextureAccessId textureAccess = default;
        GraphPassId pass;
        TextureWriteState state;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphTextureId texture = edit.CreatePersistentTexture(new TextureDesc(
                TextureDimension.Texture2D,
                width: 16,
                height: 16,
                depth: 1,
                mipLevelCount: 3,
                arrayLayerCount: 1,
                sampleCount: 1,
                format: Format.R8G8B8A8UNorm,
                usages: TextureUsages.ColorAttachment));
            state = new TextureWriteState(texture);
            pass = edit.AddCopyPass<TextureWriteState, DynamicTextureWriteFrame>(
                "dynamic texture range",
                PassQueueSelection.Exact(support.GraphicsQueue),
                state,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition definition, ref TextureWriteState definitionState) =>
                {
                    textureAccess = definition.Write(
                        definitionState.Texture,
                        new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                        PipelineSync.RenderTarget,
                        ResourceAccess.RenderTarget,
                        TextureLayout.RenderTarget,
                        WriteCoverage.Complete,
                        dynamicRange: true);
                },
                static (ref CopyPassCommandScope commands,
                    in TextureWriteState definitionState,
                    in DynamicTextureWriteFrame frame) =>
                {
                    Texture texture = commands.GetTexture(definitionState.Texture);
                    Assert.True(
                        texture.Info.Usages.HasFlag(TextureUsages.ColorAttachment),
                        $"Expected ColorAttachment usage, actual: {texture.Info.Usages}.");
                    commands.ClearTexture(
                        texture,
                        frame.Range,
                        new Vector4(0.25f, 0.5f, 0.75f, 1.0f));
                });
            edit.Commit();
        }

        TextureSubresourceRange range = new(1, 1, 0, 1, TextureAspects.Color);
        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetPassData(pass, new DynamicTextureWriteFrame(range));
            frame.SetTextureRange(textureAccess, range);
            Assert.Equal(1, frame.Execute(completions));
        }
        support.Wait(completions);
    }

    [Fact]
    public void CompleteOverwriteCullsDeadProducerAndReportsPlacedAliasing()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer source = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer destination = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId sourceId = edit.RegisterExternalBuffer(source,
                [Endpoint(source, support.CopyQueue, ResourceContentState.Defined)]);
            GraphBufferId destinationId = edit.RegisterExternalBuffer(destination,
                [Endpoint(destination, support.GraphicsQueue, ResourceContentState.Undefined)]);
            GraphBufferId scratch = edit.CreateTransientBuffer(new BufferDesc(
                ByteCount,
                BufferUsages.CopySource | BufferUsages.CopyDestination));

            _ = AddCopy(edit, "Dead write", support.CopyQueue,
                new CopyState(sourceId, scratch, ByteCount));
            _ = AddCopy(edit, "Live overwrite", support.CopyQueue,
                new CopyState(sourceId, scratch, ByteCount));
            _ = AddCopy(edit, "Observable readback", support.GraphicsQueue,
                new CopyState(scratch, destinationId, ByteCount));
            edit.Commit();
        }

        RenderGraphSnapshot? snapshot = null;
        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame(
                   new RenderGraphFrameOptions(Diagnostics: Capture)))
        {
            _ = frame.Execute(completions);
        }
        support.Wait(completions);
        Assert.NotNull(snapshot);
        Assert.Contains(snapshot!.Passes, pass => pass.Label == "Dead write" && !pass.Live);
        Assert.Contains(snapshot.Passes, pass => pass.Label == "Live overwrite" && pass.Live);
        Assert.True(snapshot.Statistics.TransientOccupiedBytes <= snapshot.Statistics.TransientResourceBytes);
        Assert.True(snapshot.Statistics.TransientHeapBytes >= snapshot.Statistics.TransientOccupiedBytes);
        Assert.True(snapshot.Statistics.GraphHeapBytes >= snapshot.Statistics.TransientHeapBytes);

        void Capture(in RenderGraphDiagnosticsView view) =>
            snapshot = CaptureSnapshot(view);
    }

    [Fact]
    public void TransientRegionsDoNotAliasAcrossQueueBoundaries()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        RenderGraphSnapshot crossQueue = Execute(
            support.CopyQueue,
            support.GraphicsQueue,
            "cross-queue first",
            "cross-queue second");
        Assert.DoesNotContain(
            crossQueue.Barriers,
            static barrier => barrier.Kind == RenderGraphBarrierKind.Aliasing);

        RenderGraphSnapshot Execute(
            Queue firstQueue,
            Queue secondQueue,
            string firstLabel,
            string secondLabel)
        {
            using var graph = new RenderGraph(
                support.Backend,
                support.Device,
                [support.GraphicsQueue, support.CopyQueue]);
            using (RenderGraphEdit edit = graph.BeginEdit())
            {
                GraphBufferId first = edit.CreateTransientBuffer(new BufferDesc(
                    65UL << 20,
                    BufferUsages.CopyDestination,
                    firstLabel));
                GraphBufferId second = edit.CreateTransientBuffer(new BufferDesc(
                    65UL << 20,
                    BufferUsages.CopyDestination,
                    secondLabel));
                GraphPassId firstPass = AddClear(edit, firstLabel, firstQueue, first, 1);
                GraphPassId secondPass = AddClear(edit, secondLabel, secondQueue, second, 2);
                edit.OrderAfter(secondPass, firstPass);
                edit.Commit();
            }

            RenderGraphSnapshot? snapshot = null;
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            int submitted;
            using (RenderGraphFrame frame = graph.BeginFrame(
                       new RenderGraphFrameOptions(Diagnostics: Capture)))
            {
                submitted = frame.Execute(completions);
            }
            support.Wait(completions.AsSpan(0, submitted));
            Assert.NotNull(snapshot);
            Assert.Equal(2, snapshot!.Buffers.Length);
            return snapshot;

            void Capture(in RenderGraphDiagnosticsView view) =>
                snapshot = CaptureSnapshot(view);
        }
    }

    [Fact]
    public void FrameExtensionParticipatesInCullingAndScheduling()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer source = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer destination = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(ByteCount, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);

        GraphExtensionPointId point;
        GraphBufferId sourceId;
        GraphBufferId destinationId;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            sourceId = edit.RegisterExternalBuffer(source,
                [Endpoint(source, support.CopyQueue, ResourceContentState.Defined)]);
            destinationId = edit.RegisterExternalBuffer(destination,
                [Endpoint(destination, support.GraphicsQueue, ResourceContentState.Undefined)]);
            point = edit.AddExtensionPoint("Dynamic work");
            edit.Commit();
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            using RenderGraphFrameExtension extension = frame.BeginExtension(point);
            GraphBufferId transient = extension.CreateBuffer(new BufferDesc(
                ByteCount,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            _ = extension.AddCopyPass(
                "Extension upload",
                PassQueueSelection.Exact(support.CopyQueue),
                new CopyState(sourceId, transient, ByteCount),
                default,
                DeclareCopy,
                RecordCopy);
            _ = extension.AddCopyPass(
                "Extension readback",
                PassQueueSelection.Exact(support.GraphicsQueue),
                new CopyState(transient, destinationId, ByteCount),
                default,
                DeclareCopy,
                RecordCopy);
            _ = frame.Execute(completions);
        }
        support.Wait(completions);
    }

    [Fact]
    public void FrameLocalIdentityDoesNotCollideWithGraphIdentity()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);
        using RenderGraphFrame frame = graph.BeginFrame();
        GraphBufferId buffer = frame.CreateBuffer(new BufferDesc(
            64,
            BufferUsages.CopySource | BufferUsages.CopyDestination));
        _ = frame.CreateBufferSrv(buffer, new BufferRange(0, 64));
    }

    [Fact]
    public void StaleFrameCopyCannotOperateOnReusedFrameSlot()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];

        RenderGraphFrame first = graph.BeginFrame();
        RenderGraphFrame stale = first;
        Assert.Equal(0, first.Execute(completions));

        RenderGraphFrame second = graph.BeginFrame();
        bool rejected = false;
        try
        {
            _ = stale.CreateBuffer(new BufferDesc(64, BufferUsages.CopySource));
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Assert.True(rejected, "A copied frame lease must not revive when its FrameSlot is reused.");

        stale.Dispose();
        _ = second.CreateBuffer(new BufferDesc(64, BufferUsages.CopySource));
        second.Dispose();
    }

    [Fact]
    public void StaleFrameExtensionCannotOperateOnReusedFrameSlot()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        GraphExtensionPointId point;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            point = edit.AddExtensionPoint("dynamic");
            edit.Commit();
        }
        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];

        RenderGraphFrame first = graph.BeginFrame();
        RenderGraphFrameExtension extension = first.BeginExtension(point);
        RenderGraphFrameExtension stale = extension;
        Assert.Equal(0, first.Execute(completions));

        RenderGraphFrame second = graph.BeginFrame();
        using RenderGraphFrameExtension current = second.BeginExtension(point);
        bool rejected = false;
        try
        {
            _ = stale.CreateBuffer(new BufferDesc(64, BufferUsages.CopySource));
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Assert.True(rejected, "A copied frame-extension lease must not revive with a new frame.");

        stale.Dispose();
        _ = current.CreateBuffer(new BufferDesc(64, BufferUsages.CopySource));
        second.Dispose();
    }

    [Fact]
    public void CopiedCommandContextLeaseReturnsPoolEntryOnlyOnce()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var pool = new CommandContextPool(support.Backend, support.Device);

        CommandContextPool.CommandContextLease original =
            pool.Acquire(support.GraphicsQueue, bundle: false, "lease copy test");
        CommandContextPool.CommandContextLease duplicate = original;
        CommandContext context = original.Context;
        original.Dispose();
        duplicate.Dispose();

        using CommandContextPool.CommandContextLease first =
            pool.Acquire(support.GraphicsQueue, bundle: false, "first reacquire");
        using CommandContextPool.CommandContextLease second =
            pool.Acquire(support.GraphicsQueue, bundle: false, "second reacquire");

        Assert.Same(context, first.Context);
        Assert.NotSame(first.Context, second.Context);
    }

    [Fact]
    public void ExplicitUndefinedResultRejectsSameFrameRead()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId buffer = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            _ = edit.AddCopyPass<int, byte>(
                "discard contents",
                PassQueueSelection.Exact(support.GraphicsQueue),
                0,
                default,
                (ref PassDefinition access, ref int state) =>
                    _ = access.Write(
                        buffer,
                        new BufferRange(0, 64),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete,
                        ResourceContentState.Undefined),
                static (ref CopyPassCommandScope _, in int _, in byte _) => { });
            _ = edit.AddCopyPass<int, byte>(
                "read discarded contents",
                PassQueueSelection.Exact(support.GraphicsQueue),
                0,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition access, ref int state) =>
                    _ = access.Read(
                        buffer,
                        new BufferRange(0, 64),
                        PipelineSync.Copy,
                        ResourceAccess.CopySource),
                static (ref CopyPassCommandScope _, in int _, in byte _) => { });
            edit.Commit();
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using RenderGraphFrame frame = graph.BeginFrame();
        InvalidOperationException? exception = null;
        try { _ = frame.Execute(completions); }
        catch (InvalidOperationException value) { exception = value; }
        Assert.NotNull(exception);
        Assert.Contains("RG4001:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("reads undefined Buffer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteOnlyDeclarationMismatchIsCheckedOnlyInDevelopment()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer destination = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(64, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId sourceId = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphBufferId destinationId = edit.RegisterExternalBuffer(destination,
                [Endpoint(destination, support.GraphicsQueue, ResourceContentState.Undefined)]);
            _ = edit.AddCopyPass<CopyState, byte>(
                "invalid read direction",
                PassQueueSelection.Exact(support.GraphicsQueue),
                new CopyState(sourceId, destinationId, 64),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref CopyState state) =>
                {
                    _ = access.Write(state.Source, new BufferRange(0, state.Size),
                        PipelineSync.Copy, ResourceAccess.CopyDestination,
                        WriteCoverage.Complete);
                    _ = access.Write(state.Destination, new BufferRange(0, state.Size),
                        PipelineSync.Copy, ResourceAccess.CopyDestination,
                        WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in CopyState state, in byte _) =>
                    commands.CopyBuffer(new BufferCopy(
                        commands.GetBuffer(state.Source), 0,
                        commands.GetBuffer(state.Destination), 0,
                        state.Size)));
            edit.Commit();
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using RenderGraphFrame frame = graph.BeginFrame();
        InvalidOperationException? exception = null;
        try { _ = frame.Execute(completions); }
        catch (InvalidOperationException value) { exception = value; }
#if DEBUG
        Assert.NotNull(exception);
        Assert.Contains("not covered", exception.Message, StringComparison.Ordinal);
#else
        Assert.Null(exception);
#endif
    }

    [Fact]
    public void PartialSubmissionTransfersAcceptedCompletionsToFrameSlot()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId buffer = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            _ = edit.AddCopyPass<int, byte>(
                "accepted first wave",
                PassQueueSelection.Exact(support.CopyQueue),
                0,
                default,
                (ref PassDefinition access, ref int state) =>
                    _ = access.Write(buffer, new BufferRange(0, 64),
                        PipelineSync.Copy, ResourceAccess.CopyDestination,
                        WriteCoverage.Complete),
                static (ref CopyPassCommandScope _, in int _, in byte _) => { });
            _ = edit.AddCopyPass<int, byte>(
                "failing second wave",
                PassQueueSelection.Exact(support.GraphicsQueue),
                0,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition access, ref int state) =>
                    _ = access.Read(buffer, new BufferRange(0, 64),
                        PipelineSync.Copy, ResourceAccess.CopySource),
                static (ref CopyPassCommandScope _, in int _, in byte _) =>
                    throw new InvalidOperationException("injected callback failure"));
            edit.Commit();
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using RenderGraphFrame frame = graph.BeginFrame();
        bool failed = false;
        try { _ = frame.Execute(completions); }
        catch (InvalidOperationException) { failed = true; }
        Assert.True(failed);
        Assert.True(graph.InFlightCompletionCount > 0,
            "A successfully submitted wave must remain attached to the FrameSlot after a later failure.");
    }

    [Fact]
    public void ExecuteCompactsCompletionsForNonContiguousUsedQueueSlots()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue]);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId graphicsBuffer = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopyDestination));
            GraphBufferId copyBuffer = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopyDestination));
            AddClear(edit, "graphics clear", support.GraphicsQueue, graphicsBuffer, 1);
            AddClear(edit, "copy clear", support.CopyQueue, copyBuffer, 2);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        int count;
        using (RenderGraphFrame frame = graph.BeginFrame())
            count = frame.Execute(completions);

        Assert.Equal(2, count);
        Assert.Same(support.GraphicsQueue, completions[0].Queue);
        Assert.Same(support.CopyQueue, completions[1].Queue);
        support.Wait(completions.AsSpan(0, count));
    }

    [Fact]
    public void PersistentPassDataRemainsUntilUpdated()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        int[] observed = [0];
        GraphPassId pass;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            pass = edit.AddCopyPass<ObservationState, int>(
                "retained pass data",
                PassQueueSelection.Exact(support.GraphicsQueue),
                new ObservationState(observed),
                new PassOptions(
                    Culling: PassCullingMode.NeverCull,
                    Recording: PassRecordingMode.CallingThread),
                static (ref PassDefinition _, ref ObservationState _) => { },
                static (ref CopyPassCommandScope _, in ObservationState state, in int frameValue) =>
                    state.Destination[0] = frameValue);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetPassData(pass, 17);
            Assert.Equal(1, frame.Execute(completions));
        }
        support.Wait(completions.AsSpan(0, 1));
        Assert.Equal(17, observed[0]);

        completions.AsSpan().Clear();
        using (RenderGraphFrame frame = graph.BeginFrame())
            Assert.Equal(1, frame.Execute(completions));
        support.Wait(completions.AsSpan(0, 1));
        Assert.Equal(17, observed[0]);
    }

    [Fact]
    public void FrameOnlyPassRetainsUpdatedData()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        int[] observed = [0];
        GraphPassId pass;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            pass = edit.AddCopyFramePass(
                "retained frame-only data",
                PassQueueSelection.Exact(support.GraphicsQueue),
                new ObservationState(observed),
                new PassOptions(
                    Culling: PassCullingMode.NeverCull,
                    Recording: PassRecordingMode.CallingThread),
                static (ref PassDefinition _, ref ObservationState _) => { },
                static (ref CopyPassCommandScope _, in ObservationState state) =>
                    state.Destination[0]++);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
            Assert.Equal(1, frame.Execute(completions));
        support.Wait(completions.AsSpan(0, 1));
        Assert.Equal(1, observed[0]);

        int[] overrideObserved = [10];
        completions.AsSpan().Clear();
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetPassData(pass, new ObservationState(overrideObserved));
            Assert.Equal(1, frame.Execute(completions));
        }
        support.Wait(completions.AsSpan(0, 1));
        Assert.Equal(1, observed[0]);
        Assert.Equal(11, overrideObserved[0]);

        completions.AsSpan().Clear();
        using (RenderGraphFrame frame = graph.BeginFrame())
            Assert.Equal(1, frame.Execute(completions));
        support.Wait(completions.AsSpan(0, 1));
        Assert.Equal(1, observed[0]);
        Assert.Equal(12, overrideObserved[0]);
    }

    [Fact]
    public void GeneralPassRequiresAGraphicsQueue()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);
        using RenderGraphEdit edit = graph.BeginEdit();
        _ = edit.AddGeneralPass<int, byte>(
            "General on Copy",
            PassQueueSelection.Exact(support.CopyQueue),
            0,
            default,
            static (ref PassDefinition _, ref int _) => { },
            static (ref GeneralPassCommandScope _, in int _, in byte _) => { });
        bool rejected = false;
        try
        {
            edit.Commit();
        }
        catch (NotSupportedException)
        {
            rejected = true;
        }
        Assert.True(rejected, "A general pass must not be accepted on a Copy Queue.");
    }

    [Fact]
    public void Explicit_order_can_place_a_frame_writer_before_a_retained_texture_reader()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        TextureDesc description = new(
            TextureDimension.Texture2D,
            4,
            4,
            1,
            1,
            1,
            1,
            Format.R8G8B8A8UNorm,
            TextureUsages.Sampled | TextureUsages.CopyDestination,
            label: "late frame upload texture");
        using Texture texture = support.Backend.CreateTexture(support.Device, description);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);

        GraphTextureId slot;
        GraphPassId consumer;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            slot = edit.DeclareExternalTexture(description);
            consumer = edit.AddGeneralFramePass(
                "retained texture consumer",
                PassQueueSelection.Exact(support.GraphicsQueue),
                slot,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref GraphTextureId state) =>
                    _ = access.Read(
                        state,
                        new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                        PipelineSync.PixelShading,
                        ResourceAccess.ShaderResource,
                        TextureLayout.ShaderResource),
                static (ref GeneralPassCommandScope _, in GraphTextureId _) => { });
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.BindExternalTexture(
                slot,
                texture,
                [new TextureBoundaryState(
                    new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                    texture.InitialSync,
                    texture.InitialAccess,
                    texture.InitialLayout,
                    ResourceContentState.Undefined)]);
            GraphPassId upload = frame.AddGeneralPass(
                "late frame upload",
                PassQueueSelection.Exact(support.GraphicsQueue),
                slot,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref GraphTextureId state) =>
                    _ = access.Write(
                        state,
                        new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        TextureLayout.CopyDestination,
                        WriteCoverage.Complete,
                        ResourceContentState.Defined),
                static (ref GeneralPassCommandScope _, in GraphTextureId _) => { });
            frame.OrderAfter(consumer, upload);
            Assert.Equal(1, frame.Execute(completions));
        }
        support.Wait(completions.AsSpan(0, 1));
    }

    [Fact]
    public void FrameGeneralPassRejectsCopyQueueDuringAuthoring()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);
        using RenderGraphFrame frame = graph.BeginFrame();

        bool rejected = false;
        try
        {
            _ = frame.AddGeneralPass(
                "General on Copy",
                PassQueueSelection.Exact(support.CopyQueue),
                0,
                default,
                static (ref PassDefinition _, ref int _) => { },
                static (ref GeneralPassCommandScope _, in int _) => { });
        }
        catch (NotSupportedException)
        {
            rejected = true;
        }
        Assert.True(rejected, "A frame-local general pass must reject a Copy Queue during authoring.");
    }

    private static RenderGraph BuildCopyGraph(
        WarpGraphTestSupport support,
        Buffer upload,
        Buffer readback)
    {
        var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);
        using RenderGraphEdit edit = graph.BeginEdit();
        GraphBufferId uploadId = edit.RegisterExternalBuffer(upload,
            [Endpoint(upload, support.CopyQueue, ResourceContentState.Defined)]);
        GraphBufferId readbackId = edit.RegisterExternalBuffer(readback,
            [Endpoint(readback, support.GraphicsQueue, ResourceContentState.Undefined)]);
        GraphBufferId transient = edit.CreateTransientBuffer(new BufferDesc(
            ByteCount,
            BufferUsages.CopySource | BufferUsages.CopyDestination));
        _ = AddCopy(edit, "Upload", support.CopyQueue,
            new CopyState(uploadId, transient, ByteCount));
        _ = AddCopy(edit, "Readback", support.GraphicsQueue,
            new CopyState(transient, readbackId, ByteCount));
        edit.Commit();
        return graph;
    }

    private static GraphPassId AddCopy(
        RenderGraphEdit edit,
        string label,
        Queue queue,
        in CopyState state) =>
        edit.AddCopyPass<CopyState, byte>(
            label,
            PassQueueSelection.Exact(queue),
            state,
            default,
            DeclareCopy,
            static (ref CopyPassCommandScope commands, in CopyState copy, in byte _) =>
                RecordCopy(ref commands, copy));

    private static GraphPassId AddClear(
        RenderGraphEdit edit,
        string label,
        Queue queue,
        GraphBufferId buffer,
        uint value)
    {
        return edit.AddCopyPass<ClearState, byte>(
            label,
            PassQueueSelection.Exact(queue),
            new ClearState(buffer, value),
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition access, ref ClearState state) =>
                _ = access.Write(
                    state.Buffer,
                    new BufferRange(0, 64),
                    PipelineSync.Copy,
                    ResourceAccess.CopyDestination,
                    WriteCoverage.Complete),
            static (ref CopyPassCommandScope commands, in ClearState state, in byte _) =>
                commands.ClearBuffer(
                    commands.GetBuffer(state.Buffer),
                    new BufferRange(0, 64),
                    state.Value));
    }

    private static GraphPassId AddRead(
        RenderGraphEdit edit,
        string label,
        Queue queue,
        GraphBufferId buffer) =>
        edit.AddCopyPass<GraphBufferId, byte>(
            label,
            PassQueueSelection.Exact(queue),
            buffer,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition access, ref GraphBufferId resource) =>
                _ = access.Read(
                    resource,
                    new BufferRange(0, 64),
                    PipelineSync.Copy,
                    ResourceAccess.CopySource),
            static (ref CopyPassCommandScope _, in GraphBufferId _, in byte _) => { });

    private static void DeclareCopy(ref PassDefinition access, ref CopyState state)
    {
        _ = access.Read(state.Source, new BufferRange(0, state.Size),
            PipelineSync.Copy, ResourceAccess.CopySource);
        _ = access.Write(state.Destination, new BufferRange(0, state.Size),
            PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
    }

    private static void RecordCopy(ref CopyPassCommandScope commands, in CopyState state)
    {
        Buffer source = commands.GetBuffer(state.Source);
        Buffer destination = commands.GetBuffer(state.Destination);
        commands.CopyBuffer(new BufferCopy(source, 0, destination, 0, state.Size));
    }

    private static void ExecuteAndVerify(
        WarpGraphTestSupport support,
        RenderGraph graph,
        Buffer upload,
        Buffer readback,
        byte seed)
    {
        byte[] expected = new byte[ByteCount];
        for (int index = 0; index < expected.Length; index++)
            expected[index] = unchecked((byte)(index * 31 + seed));
        BufferRange range = new(0, ByteCount);
        using (MappedBuffer mapping = support.Backend.Map(upload, MapType.Write, range))
        {
            expected.CopyTo(mapping.Bytes);
            mapping.Flush(range);
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
            _ = frame.Execute(completions);
        support.Wait(completions);

        byte[] actual = new byte[ByteCount];
        using (MappedBuffer mapping = support.Backend.Map(readback, MapType.Read, range))
        {
            mapping.Invalidate(range);
            mapping.Bytes.CopyTo(actual);
        }
        Assert.Equal(expected, actual);
    }

    private static BufferBoundaryState Endpoint(
        Buffer buffer,
        Queue queue,
        ResourceContentState contents) =>
        new(new BufferRange(0, buffer.Info.Size),
            buffer.InitialSync, buffer.InitialAccess, contents, queue);

    private static RenderGraphSnapshot CaptureSnapshot(in RenderGraphDiagnosticsView view)
    {
        return RenderGraphSnapshot.Capture(view);
    }

    private readonly record struct CopyState(
        GraphBufferId Source,
        GraphBufferId Destination,
        ulong Size);

    private readonly record struct ClearState(GraphBufferId Buffer, uint Value);

    private readonly record struct DependentClearState(
        GraphBufferId Gate,
        GraphBufferId Buffer,
        uint Value);

    private readonly record struct ObservationState(int[] Destination);

    private readonly record struct DynamicCopyFrame(ulong Offset, ulong Size);

    private readonly record struct TextureWriteState(GraphTextureId Texture);

    private readonly record struct DynamicTextureWriteFrame(TextureSubresourceRange Range);
}
