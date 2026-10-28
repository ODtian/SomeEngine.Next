namespace SomeEngine.RenderGraph.Tests;

public sealed class FrameUploadArgumentTests
{
    [Fact]
    public void RejectedNextFrameWritesPreserveQueuedBytesAcrossFrameReuse()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        var receiver = new ReadbackBytes();
        GraphBufferId values;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            values = edit.CreatePersistentBuffer(new BufferDesc(16,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            _ = edit.AddReadback(values, BufferRange.Whole, receiver, "Upload argument recovery");
            edit.Commit();
        }
        (ulong Offset, int Length)[] allInvalidRanges =
        [
            (8, -5), (8, int.MinValue), (8, -4), (8, -3), (8, -1), (8, 0),
            (0, 2147418113), (0, int.MaxValue),
            (ulong.MaxValue, 1), (ulong.MaxValue - 3, 4),
        ];
        // The first frame isolates -5 so the unfixed producer fails on corrupted bytes,
        // rather than an unrelated invalid-argument exception. Later frames reuse the slot.
        (ulong Offset, int Length)[][] invalidBatches = [[(8, -5)], allInvalidRanges, allInvalidRanges];
        int round = 0;
        foreach (var invalidRanges in invalidBatches)
        {
            byte first = (byte)(23 + round), second = (byte)(129 + round);
            Assert.True(graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.Zero));
            using (next)
            {
                next.GetWriteSpan(values, 0, 1).Fill(first);
                next.GetWriteSpan(values, 1, 3).Fill(first);
                next.GetWriteSpan(values, 4, 4).Fill(first);
                foreach (var (offset, length) in invalidRanges)
                    Assert.ThrowsAny<Exception>(() => { _ = next.GetWriteSpan(values, offset, length); });
                next.GetWriteSpan(values, 8, 5).Fill(second);
                next.GetWriteSpan(values, 13, 3).Fill(second);
                using RenderGraphFrame frame = graph.BeginFrame(next);
                Execute(frame, graph, support);
            }
            var expected = new byte[16];
            expected.AsSpan(0, 8).Fill(first);
            expected.AsSpan(8, 8).Fill(second);
            Assert.Equal(expected, Assert.Single(receiver.Results));
            receiver.Results.Clear();
            round++;
        }
    }

    [Fact]
    public void RejectedGroupedWritesPreserveQueuedBytesAcrossFrameReuse()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        var receiver = new ReadbackBytes();
        GraphBufferId values;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            values = edit.CreatePersistentBuffer(new BufferDesc(16,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            _ = edit.AddReadback(values, BufferRange.Whole, receiver, "Upload argument recovery");
            edit.Commit();
        }
        (ulong Offset, int Length)[] allInvalidRanges =
        [
            (8, -5), (8, int.MinValue), (8, -4), (8, -3), (8, -1), (8, 0),
            (0, 2147418113), (0, int.MaxValue),
            (ulong.MaxValue, 1), (ulong.MaxValue - 3, 4),
        ];
        // The first frame isolates -5 so the unfixed producer fails on corrupted bytes,
        // rather than an unrelated invalid-argument exception. Later frames reuse the slot.
        (ulong Offset, int Length)[][] invalidBatches = [[(8, -5)], allInvalidRanges, allInvalidRanges];
        int round = 0;
        foreach (var invalidRanges in invalidBatches)
        {
            byte first = (byte)(23 + round), second = (byte)(129 + round);
            Assert.True(graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.Zero));
            using (next)
            {
                using var writes = next.BeginBufferWrites();
                writes.GetWriteSpan(values, 0, 1).Fill(first);
                writes.GetWriteSpan(values, 1, 3).Fill(first);
                writes.GetWriteSpan(values, 4, 4).Fill(first);
                foreach (var (offset, length) in invalidRanges)
                    Assert.ThrowsAny<Exception>(() => { _ = writes.GetWriteSpan(values, offset, length); });
                writes.GetWriteSpan(values, 8, 5).Fill(second);
                writes.GetWriteSpan(values, 13, 3).Fill(second);
                writes.Commit();
                using RenderGraphFrame frame = graph.BeginFrame(next);
                Execute(frame, graph, support);
            }
            var expected = new byte[16];
            expected.AsSpan(0, 8).Fill(first);
            expected.AsSpan(8, 8).Fill(second);
            Assert.Equal(expected, Assert.Single(receiver.Results));
            receiver.Results.Clear();
            round++;
        }
    }

    [Fact]
    public void RejectedFrameWritesPreserveQueuedBytesAcrossFrameReuse()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        var receiver = new ReadbackBytes();
        GraphBufferId values;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            values = edit.CreatePersistentBuffer(new BufferDesc(16,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            _ = edit.AddReadback(values, BufferRange.Whole, receiver, "Upload argument recovery");
            edit.Commit();
        }
        (ulong Offset, int Length)[] allInvalidRanges =
        [
            (8, -5), (8, int.MinValue), (8, -4), (8, -3), (8, -1), (8, 0),
            (0, 2147418113), (0, int.MaxValue),
            (ulong.MaxValue, 1), (ulong.MaxValue - 3, 4),
        ];
        // The first frame isolates -5 so the unfixed producer fails on corrupted bytes,
        // rather than an unrelated invalid-argument exception. Later frames reuse the slot.
        (ulong Offset, int Length)[][] invalidBatches = [[(8, -5)], allInvalidRanges, allInvalidRanges];
        int round = 0;
        foreach (var invalidRanges in invalidBatches)
        {
            byte first = (byte)(23 + round), second = (byte)(129 + round);
            Assert.True(graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.Zero));
            using (next)
            {
                using RenderGraphFrame frame = graph.BeginFrame(next);
                frame.GetWriteSpan(values, 0, 1).Fill(first);
                frame.GetWriteSpan(values, 1, 3).Fill(first);
                frame.GetWriteSpan(values, 4, 4).Fill(first);
                foreach (var (offset, length) in invalidRanges)
                {
                    Exception? failure = null;
                    try { _ = frame.GetWriteSpan(values, offset, length); }
                    catch (Exception error) { failure = error; }
                    Assert.NotNull(failure);
                }
                frame.GetWriteSpan(values, 8, 5).Fill(second);
                frame.GetWriteSpan(values, 13, 3).Fill(second);
                Execute(frame, graph, support);
            }
            var expected = new byte[16];
            expected.AsSpan(0, 8).Fill(first);
            expected.AsSpan(8, 8).Fill(second);
            Assert.Equal(expected, Assert.Single(receiver.Results));
            receiver.Results.Clear();
            round++;
        }
    }

    private static void Execute(RenderGraphFrame frame, RenderGraph graph, WarpGraphTestSupport support)
    {
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        frame.Execute(completions);
        support.Wait(completions);
        graph.CollectCompleted();
    }

    private sealed class ReadbackBytes : IGraphReadbackReceiver
    {
        internal List<byte[]> Results { get; } = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Results.Add(bytes.ToArray());
    }
}
