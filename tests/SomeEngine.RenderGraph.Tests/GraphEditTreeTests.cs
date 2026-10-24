namespace SomeEngine.RenderGraph.Tests;

public sealed class GraphEditTreeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(130)]
    [InlineData(2048)]
    [InlineData(2113)]
    [InlineData(65601)]
    public void LargeEditsPreserveEarlierFramesAndDiscardedChanges(int count)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        var ids = new GraphBufferId[count];
        var initial = new Dictionary<GraphBufferId, ulong>();
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            for (int index = 0; index < count; index++)
            {
                ulong size = checked((ulong)(index + 1) * 4);
                ids[index] = edit.CreateTransientBuffer(new BufferDesc(size, BufferUsages.CopyDestination));
                initial.Add(ids[index], size);
            }
            edit.Commit();
        }

        Dictionary<GraphBufferId, ulong>? observed = null;
        var options = new RenderGraphFrameOptions(Diagnostics: (in RenderGraphDiagnosticsView data) =>
            observed = data.Buffers.ToArray().ToDictionary(value => value.Id, value => value.Size));
        using RenderGraphFrame earlier = graph.BeginFrame(options);
        ulong version = graph.StructureVersion;
        GraphBufferId discarded;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            for (int index = 0; index < count; index += 113) edit.Remove(ids[index]);
            for (int index = 1; index < count; index += 127)
                if (index % 113 != 0)
                    edit.SetBufferDescription(ids[index], new BufferDesc(4096, BufferUsages.CopyDestination));
            discarded = edit.CreateTransientBuffer(new BufferDesc(16384, BufferUsages.CopyDestination));
            for (int index = 0; index < 80; index++)
                edit.CreateTransientBuffer(new BufferDesc(2048, BufferUsages.CopyDestination));
        }
        Assert.Equal(version, graph.StructureVersion);

        var changed = new Dictionary<GraphBufferId, ulong>(initial);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            for (int index = 0; index < count; index += 113)
            {
                edit.Remove(ids[index]);
                changed.Remove(ids[index]);
            }
            for (int index = 1; index < count; index += 127)
            {
                if (!changed.ContainsKey(ids[index])) continue;
                edit.SetBufferDescription(ids[index], new BufferDesc(8192, BufferUsages.CopyDestination));
                changed[ids[index]] = 8192;
            }
            for (int index = 0; index < 80; index++)
            {
                GraphBufferId added = edit.CreateTransientBuffer(new BufferDesc(1024, BufferUsages.CopyDestination));
                Assert.NotEqual(discarded, added);
                changed.Add(added, 1024);
            }
            bool rejected = false;
            try { edit.Remove(discarded); }
            catch (ArgumentException) { rejected = true; }
            Assert.True(rejected);
            edit.Commit();
        }
        Assert.Equal(version + 1, graph.StructureVersion);
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        Assert.Equal(0, earlier.Execute(completions));
        Check(initial);
        earlier.Dispose();

        using RenderGraphFrame current = graph.BeginFrame(options);
        Assert.Equal(0, current.Execute(completions));
        Check(changed);

        void Check(Dictionary<GraphBufferId, ulong> expected)
        {
            Assert.NotNull(observed);
            Assert.Equal(expected.Count, observed.Count);
            foreach ((GraphBufferId id, ulong size) in expected)
            {
                Assert.True(observed.TryGetValue(id, out ulong actual));
                Assert.Equal(size, actual);
            }
        }
    }
}
