using System.Runtime.InteropServices;
using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class RetainedHeapTests
{
    [Fact]
    public void UploadInputsAndReadbackOutputsCoexistAcrossDisjointGpuUses()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        Buffer[] outputs = new Buffer[2];
        for (uint iteration = 0; iteration < 4; iteration++)
        {
            using RenderGraphFrame frame = graph.BeginFrame();
            GraphPassId previous = default;
            for (int index = 0; index < 2; index++)
            {
                uint value = 0x12345678u + iteration * 2 + (uint)index;
                GraphBufferSlice source = frame.Upload<uint>(value, BufferUsages.CopySource);
                GraphBufferId destination = frame.CreateBuffer(new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
                GraphPassId pass = frame.AddCopyPass("copy retained host data", PassQueueSelection.Exact(support.GraphicsQueue),
                    (Source: source, Destination: destination, Index: index, Outputs: outputs),
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition definition, ref (GraphBufferSlice Source, GraphBufferId Destination, int Index, Buffer[] Outputs) data) =>
                    {
                        definition.Read(data.Source.Buffer, data.Source.Range, PipelineSync.Copy, ResourceAccess.CopySource);
                        definition.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                    },
                    static (ref CopyPassCommandScope commands, in (GraphBufferSlice Source, GraphBufferId Destination, int Index, Buffer[] Outputs) data) =>
                    {
                        Buffer destination = commands.GetBuffer(data.Destination);
                        data.Outputs[data.Index] = destination;
                        commands.CopyBuffer(new(commands.GetBuffer(data.Source.Buffer), data.Source.Range.Offset, destination, 0, 4));
                    });
                if (index != 0) frame.OrderAfter(pass, previous);
                previous = pass;
            }
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            int count = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, count));
            for (int index = 0; index < outputs.Length; index++)
            {
                using MappedBuffer mapped = support.Backend.Map(outputs[index], MapType.Read, BufferRange.Whole);
                Assert.Equal(0x12345678u + iteration * 2 + (uint)index, MemoryMarshal.Read<uint>(mapped.Bytes));
            }
        }
    }

    [Fact]
    public void AdjacentFreeRegionsAreCoalescedAndReusedWithoutGrowingTheHeap()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        GraphBufferId large, left, right, replacement = default;
        GraphPassId pass;
        GraphBufferAccessId leftWrite = default, rightWrite = default;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            large = edit.CreateBuffer(new BufferDesc(131072, BufferUsages.CopyDestination));
            left = edit.CreateBuffer(new BufferDesc(65536, BufferUsages.CopyDestination));
            right = edit.CreateBuffer(new BufferDesc(65536, BufferUsages.CopyDestination));
            pass = edit.AddCopyFramePass("fill three regions", PassQueueSelection.Exact(support.GraphicsQueue),
                new[] { large, left, right }, new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition definition, ref GraphBufferId[] buffers) =>
                {
                    definition.Write(buffers[0], BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                    leftWrite = definition.Write(buffers[1], BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                    rightWrite = definition.Write(buffers[2], BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in GraphBufferId[] buffers) =>
                {
                    foreach (GraphBufferId buffer in buffers)
                        commands.ClearBuffer(commands.GetBuffer(buffer), BufferRange.Whole, 0x12345678);
                });
            edit.Commit();
        }
        Execute(false);
        Assert.True(graph.TryGetTransientPlacement(large, out Heap? original, out _));
        Assert.True(graph.TryGetTransientPlacement(left, out Heap? leftHeap, out ulong leftOffset));
        Assert.True(graph.TryGetTransientPlacement(right, out Heap? rightHeap, out ulong rightOffset));
        Assert.Same(original, leftHeap);
        Assert.Same(original, rightHeap);
        Assert.Equal(65536UL, Math.Max(leftOffset, rightOffset) - Math.Min(leftOffset, rightOffset));
        ulong capacity = graph.HeapByteBudget.CommittedBytes;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.Remove(leftWrite); edit.Remove(rightWrite);
            edit.Remove(left); edit.Remove(right);
            replacement = edit.CreateBuffer(new BufferDesc(131072, BufferUsages.CopyDestination));
            PassDefinition definition = edit.GetPass(pass);
            definition.Write(replacement, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            edit.Commit();
        }
        Execute(true);
        Assert.True(graph.TryGetTransientPlacement(replacement, out Heap? replacementHeap, out ulong offset));
        Assert.Same(original, replacementHeap);
        Assert.Equal(Math.Min(leftOffset, rightOffset), offset);
        Assert.Equal(capacity, graph.HeapByteBudget.CommittedBytes);

        void Execute(bool changed)
        {
            using RenderGraphFrame frame = graph.BeginFrame();
            if (changed) frame.SetPassData(pass, new[] { large, replacement });
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            int count = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, count));
        }
    }

    [Fact]
    public void AliasesFollowQueueSubmissionOrderAndSeparateWhenTheirLifetimesOverlap()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue, support.CopyQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer readback = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(8, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferId left, right;
        GraphPassId leftRead, rightWrite;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId result = edit.RegisterExternalBuffer(readback,
                [new BufferBoundaryState(new(0, 8), readback.InitialSync, readback.InitialAccess, ResourceContentState.Undefined)]);
            left = edit.CreateTransientBuffer(new BufferDesc(4, BufferUsages.CopySource | BufferUsages.CopyDestination, "first alias"));
            right = edit.CreateTransientBuffer(new BufferDesc(4, BufferUsages.CopySource | BufferUsages.CopyDestination, "second alias"));
            edit.AddCopyFramePass("occupy graphics queue", PassQueueSelection.Exact(support.GraphicsQueue), (byte)0,
                new PassOptions(Culling: PassCullingMode.NeverCull, EstimatedExecutionCost: 1000),
                static (ref PassDefinition _, ref byte _) => { }, static (ref CopyPassCommandScope _, in byte _) => { });
            AddClear(edit, support.CopyQueue, left, 0x12345678);
            leftRead = AddRead(edit, support.GraphicsQueue, left, result, 0, 100);
            rightWrite = AddClear(edit, support.GraphicsQueue, right, 0x87654321);
            AddRead(edit, support.GraphicsQueue, right, result, 4, 1);
            edit.Commit();
        }
        Execute(); Execute();
        Assert.True(graph.TryGetTransientPlacement(left, out Heap? leftHeap, out ulong leftOffset));
        Assert.True(graph.TryGetTransientPlacement(right, out Heap? rightHeap, out ulong rightOffset));
        Assert.Same(leftHeap, rightHeap);
        Assert.Equal(leftOffset, rightOffset);
        Assert.True(leftHeap!.Info.Size < 1UL << 20);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.OrderAfter(leftRead, rightWrite);
            edit.Commit();
        }
        Execute(); Execute();
        Assert.True(graph.TryGetTransientPlacement(left, out leftHeap, out leftOffset));
        Assert.True(graph.TryGetTransientPlacement(right, out rightHeap, out rightOffset));
        Assert.True(!ReferenceEquals(leftHeap, rightHeap) || leftOffset != rightOffset);

        void Execute()
        {
            using RenderGraphFrame frame = graph.BeginFrame();
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            int count = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, count));
            using MappedBuffer mapped = support.Backend.Map(readback, MapType.Read, BufferRange.Whole);
            Assert.Equal(0x12345678u, MemoryMarshal.Read<uint>(mapped.Bytes));
            Assert.Equal(0x87654321u, MemoryMarshal.Read<uint>(mapped.Bytes[4..]));
        }
    }

    [Fact]
    public void RemovingTheLastAllocationReleasesItsHeapCapacity()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        GraphBufferId resource;
        GraphPassId pass;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            resource = edit.CreateTransientBuffer(new BufferDesc(4, BufferUsages.CopyDestination));
            pass = AddClear(edit, support.GraphicsQueue, resource, 1);
            edit.SetPassOptions(pass, new PassOptions(Culling: PassCullingMode.NeverCull));
            edit.Commit();
        }
        Execute();
        Assert.True(graph.HeapByteBudget.CommittedBytes > 0);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.Remove(pass); edit.Remove(resource); edit.Commit();
        }
        Execute();
        Assert.Equal(0UL, graph.HeapByteBudget.CommittedBytes);
        void Execute()
        {
            using RenderGraphFrame frame = graph.BeginFrame();
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            int count = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, count));
        }
    }

    [Fact]
    public void CrossQueueBufferReadersRemainIndependentAndOrderTheNextWriter()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        Assert.True(support.Device.Capabilities.SupportsConcurrentBufferReads);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer first = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(8, BufferUsages.CopyDestination), MemoryType.Readback);
        using Buffer second = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphPassId firstReader, secondReader, overwrite;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId firstResult = edit.RegisterExternalBuffer(first,
                [new(new(0, 8), first.InitialSync, first.InitialAccess, ResourceContentState.Undefined)]);
            GraphBufferId secondResult = edit.RegisterExternalBuffer(second,
                [new(new(0, 4), second.InitialSync, second.InitialAccess, ResourceContentState.Undefined)]);
            GraphBufferId source = edit.CreateBuffer(new BufferDesc(4,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            AddClear(edit, support.CopyQueue, source, 0x12345678);
            firstReader = AddRead(edit, support.GraphicsQueue, source, firstResult, 0, 1);
            secondReader = AddRead(edit, support.ComputeQueue, source, secondResult, 0, 1);
            overwrite = AddClear(edit, support.CopyQueue, source, 0x87654321);
            edit.OrderAfter(overwrite, firstReader);
            edit.OrderAfter(overwrite, secondReader);
            AddRead(edit, support.GraphicsQueue, source, firstResult, 4, 1);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int iteration = 0; iteration < 4; iteration++)
        {
            using RenderGraphFrame frame = graph.BeginFrame(new RenderGraphFrameOptions(Diagnostics: Inspect));
            int count = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, count));
            using MappedBuffer a = support.Backend.Map(first, MapType.Read, BufferRange.Whole);
            using MappedBuffer b = support.Backend.Map(second, MapType.Read, BufferRange.Whole);
            Assert.Equal(0x12345678u, MemoryMarshal.Read<uint>(a.Bytes));
            Assert.Equal(0x12345678u, MemoryMarshal.Read<uint>(b.Bytes));
            Assert.Equal(0x87654321u, MemoryMarshal.Read<uint>(a.Bytes[4..]));
        }
        void Inspect(in RenderGraphDiagnosticsView diagnostics)
        {
            foreach (RenderGraphDependencyDiagnostic dependency in diagnostics.Dependencies)
                Assert.False((dependency.Predecessor == firstReader && dependency.Consumer == secondReader) ||
                    (dependency.Predecessor == secondReader && dependency.Consumer == firstReader));
            foreach (RenderGraphBarrierDiagnostic barrier in diagnostics.Barriers)
                Assert.False(barrier.Kind is RenderGraphBarrierKind.QueueAcquire or RenderGraphBarrierKind.QueueRelease);
        }
    }

    private static GraphPassId AddClear(RenderGraphEdit edit, Queue queue, GraphBufferId buffer, uint value) =>
        edit.AddCopyFramePass("initialize alias", PassQueueSelection.Exact(queue), (Buffer: buffer, Value: value), default,
            static (ref PassDefinition pass, ref (GraphBufferId Buffer, uint Value) data) =>
                pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete),
            static (ref CopyPassCommandScope commands, in (GraphBufferId Buffer, uint Value) data) =>
                commands.ClearBuffer(commands.GetBuffer(data.Buffer), BufferRange.Whole, data.Value));

    private static GraphPassId AddRead(RenderGraphEdit edit, Queue queue, GraphBufferId source, GraphBufferId destination, ulong offset, uint cost) =>
        edit.AddCopyFramePass("read alias", PassQueueSelection.Exact(queue), (Source: source, Destination: destination, Offset: offset),
            new PassOptions(EstimatedExecutionCost: cost),
            static (ref PassDefinition pass, ref (GraphBufferId Source, GraphBufferId Destination, ulong Offset) data) =>
            {
                pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                pass.Write(data.Destination, new(data.Offset, 4), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            },
            static (ref CopyPassCommandScope commands, in (GraphBufferId Source, GraphBufferId Destination, ulong Offset) data) =>
                commands.CopyBuffer(new(commands.GetBuffer(data.Source), 0, commands.GetBuffer(data.Destination), data.Offset, 4)));
}
