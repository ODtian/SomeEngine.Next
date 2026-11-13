using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

public sealed class BufferWriterTests
{
    [Fact]
    public void CompletedFramesExposeTheirSubmittedData()
    {
        using var support = new RenderTestDevice();
        using var graph = new RenderGraph.RenderGraph(support.Backend, support.Device,
            [support.Backend.GetQueue(support.Device, QueueType.Graphics)]);
        var result = new ReadbackBytes();
        GraphBufferId data;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            data = edit.CreatePersistentBuffer(new BufferDesc(4, BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.AddReadback(data, new(0, 4), result, "read completed frame");
            edit.Commit();
        }
        Assert.True(graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.Zero));
        ulong identity;
        using (next)
        {
            next.WriteBuffer(data, 0, new byte[] { 1, 2, 3, 4 });
            using RenderGraphFrame frame = graph.BeginFrame(next);
            identity = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
        }
        Assert.Equal(identity, graph.LastSubmittedFrame);
        Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
        Assert.True(graph.IsFrameComplete(identity));
        Assert.True(graph.LastCompletedFrame >= identity);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.Bytes);
    }

    [Fact]
    public void CommittedBufferWritersUploadTheirRanges()
    {
        using var support = new RenderTestDevice();
        Queue queue = support.Backend.GetQueue(support.Device, QueueType.Graphics);
        using var graph = new RenderGraph.RenderGraph(support.Backend, support.Device, [queue]);
        var result = new ReadbackBytes();
        GraphBufferId data;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            data = edit.CreatePersistentBuffer(new BufferDesc(64, BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.AddReadback(data, new(0, 64), result, "read buffer writes");
            edit.Commit();
        }

        byte[] expected = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        Assert.True(graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.Zero));
        using (next)
        {
            using (RenderGraphBufferWrites writes = next.BeginBufferWrites())
            {
                Parallel.For(0, 4, index =>
                {
                    RenderGraphBufferWrites.BufferWriter writer = writes.CreateWriter();
                    writer.WriteBuffer(data, (ulong)(index * 16), expected.AsSpan(index * 16, 16));
                });
                writes.Commit();
            }
            using (RenderGraphBufferWrites discarded = next.BeginBufferWrites())
                discarded.CreateWriter().GetWriteSpan(data, 0, 64).Fill(255);
            using RenderGraphFrame frame = graph.BeginFrame(next);
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            frame.Execute(completions);
            foreach (QueueCompletion completion in completions)
                Assert.Equal(WaitStatus.Completed, support.Backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        }

        graph.CollectCompleted();
        Assert.Equal(expected, result.Bytes);
    }

    private sealed class ReadbackBytes : IGraphReadbackReceiver
    {
        internal byte[] Bytes = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Bytes = bytes.ToArray();
    }
}
