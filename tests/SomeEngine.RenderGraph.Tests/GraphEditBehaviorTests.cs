using static SomeEngine.RenderGraph.Tests.CopyGraphTestSupport;

namespace SomeEngine.RenderGraph.Tests;

[Collection("Render graph trace")]
public sealed class GraphEditBehaviorTests
{
    [Fact]
    public void DiscardedLargeEditsKeepTheCommittedGraphAndRetireTheirIds()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        var recorded = new List<int>();
        var passes = new List<GraphPassId>();
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            for (int i = 0; i < 193; i++) passes.Add(Add(edit, i));
            edit.Commit();
        }

        GraphPassId discarded;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            for (int i = 0; i < passes.Count; i += 3) edit.Remove(passes[i]);
            discarded = Add(edit, 1000);
            for (int i = 1; i < 75; i++) Add(edit, 1000 + i);
        }
        Execute();
        Assert.Equal(Enumerable.Range(0, 193), recorded.Order());

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            for (int i = 0; i < passes.Count; i += 3) edit.Remove(passes[i]);
            for (int i = 0; i < 75; i++) Add(edit, 2000 + i);
            bool rejected = false;
            try { _ = edit.GetPass(discarded); }
            catch (ArgumentException) { rejected = true; }
            Assert.True(rejected);
            edit.Commit();
        }
        Execute();
        Assert.Equal(Enumerable.Range(0, 193).Where(i => i % 3 != 0).Concat(Enumerable.Range(2000, 75)), recorded.Order());

        GraphPassId Add(RenderGraphEdit edit, int value) => edit.AddCopyFramePass(
            $"record {value}", PassQueueSelection.AnyOfType(QueueType.Graphics), value,
            new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
            static (ref PassDefinition _, ref int _) => { },
            (ref CopyPassCommandScope _, in int number) => recorded.Add(number));

        void Execute()
        {
            recorded.Clear();
            using RenderGraphFrame frame = graph.BeginFrame();
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            int count = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, count));
        }
    }

    [Fact]
    public void ExecutionOrderSurvivesReorderingRemovingAndAddingPasses()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        var recorded = new List<int>();
        GraphPassId first, middle, last;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            first = Add(edit, 1);
            middle = Add(edit, 2);
            last = Add(edit, 3);
            edit.Commit();
        }
        Execute();
        Assert.Equal(new[] { 1, 2, 3 }, recorded);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.OrderAfter(first, last);
            edit.Commit();
        }
        Execute();
        Assert.Equal(3, recorded.Count);
        Assert.True(recorded.IndexOf(3) < recorded.IndexOf(1));
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.Remove(middle);
            edit.Commit();
        }
        Execute();
        Assert.Equal(new[] { 3, 1 }, recorded);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.RemoveOrder(first, last);
            GraphPassId added = Add(edit, 4);
            edit.OrderAfter(last, added);
            edit.Commit();
        }
        Execute();
        Assert.Equal(3, recorded.Count);
        Assert.Contains(1, recorded);
        Assert.True(recorded.IndexOf(4) < recorded.IndexOf(3));

        GraphPassId Add(RenderGraphEdit edit, int value) => edit.AddCopyFramePass(
            $"record {value}", PassQueueSelection.Exact(support.GraphicsQueue), value,
            new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
            static (ref PassDefinition _, ref int _) => { },
            (ref CopyPassCommandScope _, in int number) => recorded.Add(number));

        void Execute()
        {
            recorded.Clear();
            using RenderGraphFrame frame = graph.BeginFrame();
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            int count = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, count));
        }
    }

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

        GraphBufferId discarded;
        using (RenderGraphEdit abandoned = graph.BeginEdit())
            discarded = abandoned.CreateTransientBuffer(new BufferDesc(64, BufferUsages.CopySource));
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
        Assert.NotEqual(discarded, replacement);
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
    public void DiscardedIdsStayInvalidAndCommittedEditCopiesCannotWrite()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        GraphBufferId discarded;
        using (RenderGraphEdit edit = graph.BeginEdit())
            discarded = edit.CreateTransientBuffer(new BufferDesc(64, BufferUsages.CopyDestination));

        RenderGraphEdit current = graph.BeginEdit();
        RenderGraphEdit copy = current;
        GraphBufferId buffer = current.CreateTransientBuffer(new BufferDesc(64, BufferUsages.CopyDestination));
        current.Commit();
        Assert.NotEqual(discarded, buffer);
        bool rejected = false;
        try
        {
            copy.SetBufferDescription(buffer, new BufferDesc(128, BufferUsages.CopyDestination));
        }
        catch (InvalidOperationException) { rejected = true; }
        finally { copy.Dispose(); }
        Assert.True(rejected);

        using RenderGraphEdit next = graph.BeginEdit();
        rejected = false;
        try { next.SetBufferDescription(discarded, new BufferDesc(128, BufferUsages.CopyDestination)); }
        catch (ArgumentException) { rejected = true; }
        Assert.True(rejected);
        next.SetBufferDescription(buffer, new BufferDesc(256, BufferUsages.CopyDestination));
        next.Commit();
    }
}
