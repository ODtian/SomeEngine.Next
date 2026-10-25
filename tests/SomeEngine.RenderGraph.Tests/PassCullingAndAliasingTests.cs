using SomeEngine.Graphics;
using SomeEngine.RenderGraph.Diagnostics;
using static SomeEngine.RenderGraph.Tests.CopyGraphTestSupport;

namespace SomeEngine.RenderGraph.Tests;

[Collection("Render graph trace")]
public sealed class PassCullingAndAliasingTests
{
    [Fact]
    public void ComputeAliasDependencyTracksRetainedPredecessorAccessChanges()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        AliasScopePassState writer;
        AliasScopePassState reader;
        AliasScopePassState successor;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId source = edit.CreateTransientBuffer(new BufferDesc(
                64, BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "alias scope source"));
            GraphBufferId destination = edit.CreateTransientBuffer(new BufferDesc(
                64, BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "alias scope successor"));
            writer = new(source, false);
            reader = new(source, true);
            successor = new(destination, false);
            GraphPassId write = AddAliasScopePass(edit, support.GraphicsQueue, writer, "alias scope write");
            GraphPassId read = AddAliasScopePass(edit, support.GraphicsQueue, reader, "alias scope read");
            GraphPassId next = AddAliasScopePass(edit, support.GraphicsQueue, successor, "alias scope next");
            edit.OrderAfter(read, write);
            edit.OrderAfter(next, read);
            edit.SetBufferAccess(reader.Access, GraphAccessMode.Read, WriteCoverage.Partial,
                PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, BufferRange.Whole);
            edit.Commit();
        }

        ResourceAccess[] scopes =
        [
            ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess,
            ResourceAccess.ShaderResource, ResourceAccess.ShaderResource,
            ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess,
        ];
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        SomeEngine.Graphics.Buffer? initialSource = null, initialDestination = null;
        object? initialHeap = null;
        (ulong SourceOffset, ulong DestinationOffset, ulong SourceSize, ulong DestinationSize,
            int SourceFirst, int SourceLast, int DestinationFirst, int DestinationLast)? initialPlacement = null;
        int iteration = 0;
        int observations = 0;
        for (; iteration < scopes.Length; iteration++)
        {
            if (iteration > 0 && scopes[iteration] != scopes[iteration - 1])
            {
                using RenderGraphEdit edit = graph.BeginEdit();
                edit.SetBufferAccess(reader.Access, GraphAccessMode.Read, WriteCoverage.Partial,
                    PipelineSync.ComputeShading, scopes[iteration], BufferRange.Whole);
                edit.Commit();
            }
            completions.AsSpan().Clear();
            using (RenderGraphFrame frame = graph.BeginFrame(
                       new RenderGraphFrameOptions(Diagnostics: ObserveDependency)))
                Assert.Equal(1, frame.Execute(completions));
            support.Wait(completions);
            Assert.Equal(iteration + 1, observations);
            Assert.Same(initialSource, writer.Backing);
            Assert.Same(initialSource, reader.Backing);
            Assert.Same(initialDestination, successor.Backing);
        }

        void ObserveDependency(in RenderGraphDiagnosticsView _)
        {
            bool expected = scopes[iteration] == ResourceAccess.ShaderResource;
            var slots = (Array)AliasScopeMember(graph, "_frameSlots");
            object state = AliasScopeMember(slots.GetValue(0)!, "_state");
            object executor = AliasScopeMember(state, "_executor");
            var buffers = (System.Collections.IList)AliasScopeMember(executor, "_buffers");
            object source = Assert.Single(buffers.Cast<object>(), row =>
                ((BufferDesc)AliasScopeMember(row, "Description")).Label == "alias scope source");
            object destination = Assert.Single(buffers.Cast<object>(), row =>
                ((BufferDesc)AliasScopeMember(row, "Description")).Label == "alias scope successor");
            var sourceBuffer = Assert.IsAssignableFrom<SomeEngine.Graphics.Buffer>(AliasScopeMember(source, "Resource"));
            AliasingResource unmarked = new(sourceBuffer);
            Assert.False(unmarked.RequireBufferReadVisibility);
            Assert.NotEqual(unmarked, unmarked with { RequireBufferReadVisibility = true });
            var destinationBuffer = Assert.IsAssignableFrom<SomeEngine.Graphics.Buffer>(AliasScopeMember(destination, "Resource"));
            object sourcePlacement = AliasScopeMember(source, "Placement");
            object destinationPlacement = AliasScopeMember(destination, "Placement");
            object heap = AliasScopeMember(sourcePlacement, "Heap");
            Assert.Same(heap, AliasScopeMember(destinationPlacement, "Heap"));
            Assert.Same(sourceBuffer, AliasScopeMember(destinationPlacement, "AliasingPredecessor"));
            var placement = (
                (ulong)AliasScopeMember(sourcePlacement, "Offset"),
                (ulong)AliasScopeMember(destinationPlacement, "Offset"),
                (ulong)AliasScopeMember(sourcePlacement, "Size"),
                (ulong)AliasScopeMember(destinationPlacement, "Size"),
                (int)AliasScopeMember(source, "FirstPass"),
                (int)AliasScopeMember(source, "LastPass"),
                (int)AliasScopeMember(destination, "FirstPass"),
                (int)AliasScopeMember(destination, "LastPass"));
            Assert.True(placement.Item3 > 0 && placement.Item4 > 0);
            Assert.True(placement.Item1 <= placement.Item2
                ? placement.Item2 - placement.Item1 < placement.Item3
                : placement.Item1 - placement.Item2 < placement.Item4);
            if (initialSource is null)
            {
                initialSource = sourceBuffer;
                initialDestination = destinationBuffer;
                initialHeap = heap;
                initialPlacement = placement;
            }
            else
            {
                Assert.Same(initialSource, sourceBuffer);
                Assert.Same(initialDestination, destinationBuffer);
                Assert.Same(initialHeap, heap);
                Assert.Equal(initialPlacement!.Value, placement);
            }

            var resourceRows = (System.Collections.IDictionary)AliasScopeMember(executor, "_resourceRows");
            object sourceKey = resourceRows[sourceBuffer]!;
            object destinationKey = resourceRows[destinationBuffer]!;
            var synchronization = (System.Collections.IDictionary)AliasScopeMember(executor, "_synchronizationByResource");
            object owner = synchronization[destinationKey]!;
            Assert.Equal(sourceKey, AliasScopeMember(owner, "AliasScopeSource"));
            var passes = (System.Collections.IDictionary)AliasScopeMember(owner, "Passes");
            int requested = 0;
            int aliasPass = -1;
            foreach (System.Collections.DictionaryEntry entry in passes)
            {
                object pass = entry.Value!;
                var aliases = (System.Collections.IEnumerable)AliasScopeMember(pass, "AliasBefore");
                foreach (AliasingResource alias in aliases)
                {
                    if (alias.RequireBufferReadVisibility)
                    {
                        Assert.Same(sourceBuffer, alias.Resource);
                        Assert.Null(alias.TextureRange);
                        requested++;
                    }
                    if (ReferenceEquals(alias.Resource, sourceBuffer))
                    {
                        Assert.Equal(-1, aliasPass);
                        aliasPass = (int)entry.Key;
                    }
                }
                var afterAliases = (System.Collections.IEnumerable)AliasScopeMember(pass, "AliasAfter");
                Assert.All(afterAliases.Cast<AliasingResource>(), static alias =>
                    Assert.False(alias.RequireBufferReadVisibility));
            }
            Assert.True(aliasPass >= 0);
            Assert.Equal(expected ? 1 : 0, requested);
            var aggregate = (List<AliasingResource>[])AliasScopeMember(executor, "_aliasBeforeResources");
            AliasingResource outgoing = Assert.Single(aggregate[aliasPass],
                alias => ReferenceEquals(alias.Resource, sourceBuffer));
            Assert.Equal(expected, outgoing.RequireBufferReadVisibility);
            Assert.Equal(expected ? 1 : 0, aggregate.Sum(static aliases =>
                aliases?.Count(static alias => alias.RequireBufferReadVisibility) ?? 0));

            var batches = (Array)AliasScopeMember(executor, "_recordingBatchByPass");
            object batch = Assert.IsAssignableFrom<object>(batches.GetValue(aliasPass));
            bool commandsChanged = (bool)AliasScopeMember(batch, "CommandsChanged");
            if (iteration == 0 || scopes[iteration] != scopes[iteration - 1])
                Assert.True(commandsChanged);
            else
                Assert.False(commandsChanged);
            observations++;
        }
    }

    private static GraphPassId AddAliasScopePass(
        RenderGraphEdit edit, Queue queue, AliasScopePassState state, string label) =>
        edit.AddComputePass<AliasScopePassState, byte>(
            label, PassQueueSelection.Exact(queue), state,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            static (ref PassDefinition pass, ref AliasScopePassState value) =>
            {
                value.Access = value.Read
                    ? pass.Read(value.Buffer, BufferRange.Whole,
                        PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess)
                    : pass.Write(value.Buffer, BufferRange.Whole,
                        PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
            },
            static (ref ComputePassCommandScope commands, in AliasScopePassState value, in byte _) =>
                value.Backing = commands.GetBuffer(value.Buffer));

    private sealed class AliasScopePassState(GraphBufferId buffer, bool read)
    {
        internal GraphBufferId Buffer { get; } = buffer;
        internal bool Read { get; } = read;
        internal GraphBufferAccessId Access { get; set; }
        internal SomeEngine.Graphics.Buffer? Backing { get; set; }
    }

    private static object AliasScopeMember(object value, string name)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        Type type = value.GetType();
        object? result = type.GetField(name, flags)?.GetValue(value) ??
            type.GetProperty(name, flags)?.GetValue(value);
        Assert.NotNull(result);
        return result!;
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
    public void FrameExtensionParticipatesInCullingAndScheduling_DoesNotThrow()
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
}
