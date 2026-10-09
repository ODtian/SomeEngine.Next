namespace SomeEngine.RenderGraph.Tests;

public sealed class OverlappingFrameUploadTests
{
    [Fact]
    public void NextFrameWritesKeepTheirRegistrationOrderAcrossThreads()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        var receiver = new ReadbackBytes();
        GraphBufferId values;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            values = edit.CreatePersistentBuffer(new BufferDesc(8,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            _ = edit.AddReadback(values, BufferRange.Whole, receiver, "Uploaded values");
            edit.Commit();
        }

        Assert.True(graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.Zero));
        using (next)
        {
            next.GetWriteSpan(values, 0, 4).Fill(1);
            Exception? writeError = null;
            var writer = new Thread(() =>
            {
                try { next.GetWriteSpan(values, 0, 8).Fill(2); }
                catch (Exception error) { writeError = error; }
            });
            writer.Start();
            Assert.True(writer.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(writeError);
            next.GetWriteSpan(values, 4, 4).Fill(3);
            using RenderGraphFrame frame = graph.BeginFrame(next);
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            frame.Execute(completions);
            support.Wait(completions);
            graph.CollectCompleted();
        }

        Assert.Equal(new byte[] { 2, 2, 2, 2, 3, 3, 3, 3 }, Assert.Single(receiver.Results));
    }

    [Fact]
    public void CommittedGroupKeepsItsWriteOrderAcrossThreads()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        var receiver = new ReadbackBytes();
        GraphBufferId values;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            values = edit.CreatePersistentBuffer(new BufferDesc(8,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            _ = edit.AddReadback(values, BufferRange.Whole, receiver, "Uploaded values");
            edit.Commit();
        }

        Assert.True(graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.Zero));
        using (next)
        {
            using var writes = next.BeginBufferWrites();
            writes.GetWriteSpan(values, 0, 4).Fill(1);
            Exception? writeError = null;
            var writer = new Thread(() =>
            {
                try { writes.GetWriteSpan(values, 0, 8).Fill(2); }
                catch (Exception error) { writeError = error; }
            });
            writer.Start();
            Assert.True(writer.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(writeError);
            writes.GetWriteSpan(values, 4, 4).Fill(3);
            next.GetWriteSpan(values, 0, 8).Fill(4);
            writes.Commit();
            using RenderGraphFrame frame = graph.BeginFrame(next);
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            frame.Execute(completions);
            support.Wait(completions);
            graph.CollectCompleted();
        }

        Assert.Equal(new byte[] { 2, 2, 2, 2, 3, 3, 3, 3 }, Assert.Single(receiver.Results));
    }

    private sealed class ReadbackBytes : IGraphReadbackReceiver
    {
        internal List<byte[]> Results { get; } = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Results.Add(bytes.ToArray());
    }
}
