using SomeEngine.RenderGraph.Diagnostics;
using static SomeEngine.RenderGraph.Tests.CopyGraphTestSupport;

namespace SomeEngine.RenderGraph.Tests;

[Collection("Render graph trace")]
public sealed class PassCullingAndAliasingTests
{
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
