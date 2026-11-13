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
            GraphBufferId sourceId = edit.RegisterExternalBuffer(source);
            GraphBufferId destinationId = edit.RegisterExternalBuffer(destination);
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

        RenderGraphDiagnosticsData? data = null;
        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame(
                   new RenderGraphFrameOptions(Diagnostics: Capture)))
        {
            _ = frame.Execute(completions);
        }
        support.Wait(completions);
        Assert.NotNull(data);
        Assert.Contains(data!.Passes, pass => pass.Label == "Dead write" && !pass.Live);
        Assert.Contains(data.Passes, pass => pass.Label == "Live overwrite" && pass.Live);
        Assert.True(data.Statistics.TransientOccupiedBytes <= data.Statistics.TransientResourceBytes);
        Assert.True(data.Statistics.TransientHeapBytes >= data.Statistics.TransientOccupiedBytes);
        Assert.True(data.Statistics.GraphHeapBytes >= data.Statistics.TransientHeapBytes);

        void Capture(in RenderGraphDiagnosticsView view) =>
            data = CaptureDiagnostics(view);
    }

    [Fact]
    public void TransientRegionsDoNotAliasAcrossQueueBoundaries()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        RenderGraphDiagnosticsData crossQueue = Execute(
            support.CopyQueue,
            support.GraphicsQueue,
            "cross-queue first",
            "cross-queue second");
        Assert.DoesNotContain(
            crossQueue.Barriers,
            static barrier => barrier.Kind == RenderGraphBarrierKind.Aliasing);

        RenderGraphDiagnosticsData Execute(
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

            RenderGraphDiagnosticsData? data = null;
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            int submitted;
            using (RenderGraphFrame frame = graph.BeginFrame(
                       new RenderGraphFrameOptions(Diagnostics: Capture)))
            {
                submitted = frame.Execute(completions);
            }
            support.Wait(completions.AsSpan(0, submitted));
            Assert.NotNull(data);
            Assert.Equal(2, data!.Buffers.Length);
            return data;

            void Capture(in RenderGraphDiagnosticsView view) =>
                data = CaptureDiagnostics(view);
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

        GraphRegionId point;
        GraphBufferId sourceId;
        GraphBufferId destinationId;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            sourceId = edit.RegisterExternalBuffer(source);
            destinationId = edit.RegisterExternalBuffer(destination);
            point = edit.AddRegion("Dynamic work");
            edit.Commit();
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            RenderGraphFrame extension = frame;
            GraphBufferId transient = extension.CreateBuffer(new BufferDesc(
                ByteCount,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            _ = extension.AddCopyPass(
                "Extension upload",
                PassQueueSelection.OnQueue(support.CopyQueue),
                new CopyState(sourceId, transient, ByteCount),
                default,
                DeclareCopy,
                RecordCopy);
            _ = extension.AddCopyPass(
                "Extension readback",
                PassQueueSelection.OnQueue(support.GraphicsQueue),
                new CopyState(transient, destinationId, ByteCount),
                default,
                DeclareCopy,
                RecordCopy);
            _ = frame.Execute(completions);
        }
        support.Wait(completions);
    }
}
