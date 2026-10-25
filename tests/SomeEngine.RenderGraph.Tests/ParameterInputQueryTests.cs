using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class ParameterInputQueryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueriesObserveLaterMutationsAndPreserveReturnedArrays(bool commit)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        GraphParameterInput bound, accessed, sampler, unreferenced;
        GraphPassId firstBoundPass, secondBoundPass;
        using (RenderGraphEdit seed = graph.BeginEdit())
        {
            bound = seed.CreateParameterInput(ResourceBindingType.BufferSrv);
            accessed = seed.CreateParameterInput(ResourceBindingType.BufferSrv);
            sampler = seed.CreateParameterInput(ResourceBindingType.Sampler);
            unreferenced = seed.CreateParameterInput(ResourceBindingType.BufferSrv);
            firstBoundPass = AddInputPass(seed, bound, bind: true);
            secondBoundPass = AddInputPass(seed, bound, bind: true);
            _ = AddInputPass(seed, accessed, bind: false);
            _ = AddInputPass(seed, sampler, bind: true);
            seed.Commit();
        }
        ulong version = graph.StructureVersion;
        GraphParameterInput replacement;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphParameterInput[] earlierInputs = edit.GetParameterInputs();
            GraphParameterInput[] other = edit.GetParameterInputs();
            AssertInputs(earlierInputs, bound, accessed, sampler);
            AssertInputs(other, bound, accessed, sampler);
            Assert.NotSame(earlierInputs, other);
            other[0] = unreferenced;
            AssertInputs(edit.GetParameterInputs(), bound, accessed, sampler);

            replacement = edit.CreateParameterInput(ResourceBindingType.BufferSrv);
            edit.Remove(firstBoundPass);
            edit.Remove(secondBoundPass);
            _ = AddInputPass(edit, replacement, bind: true);
            AssertInputs(edit.GetParameterInputs(), accessed, sampler, replacement);
            AssertInputs(earlierInputs, bound, accessed, sampler);
            if (commit) edit.Commit();
            Assert.Equal(commit, edit.IsAccepted);
        }
        Assert.Equal(commit ? version + 1 : version, graph.StructureVersion);
        using RenderGraphEdit check = graph.BeginEdit();
        if (commit) AssertInputs(check.GetParameterInputs(), accessed, sampler, replacement);
        else AssertInputs(check.GetParameterInputs(), bound, accessed, sampler);
        check.Commit();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void QueryOnlyEditsPreserveVersionAndPermitAnotherEdit(bool populated, bool commit)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        GraphParameterInput[] expected = [];
        if (populated)
        {
            using RenderGraphEdit seed = graph.BeginEdit();
            GraphParameterInput input = seed.CreateParameterInput(ResourceBindingType.BufferSrv);
            _ = AddInputPass(seed, input, bind: true);
            expected = [input];
            seed.Commit();
        }
        ulong version = graph.StructureVersion;
        RenderGraphEdit edit = graph.BeginEdit();
        RenderGraphEdit copy = edit;
        try
        {
            AssertInputs(edit.GetParameterInputs(), expected);
            AssertInputs(edit.GetParameterInputs(), expected);
            if (commit) edit.Commit();
        }
        finally { edit.Dispose(); }
        edit.Dispose();
        Assert.Equal(commit, edit.IsAccepted);
        Assert.Equal(version, graph.StructureVersion);
        using RenderGraphEdit next = graph.BeginEdit();
        AssertQueryRejected(edit);
        AssertQueryRejected(copy);
        copy.Dispose();
        AssertInputs(next.GetParameterInputs(), expected);
        next.Commit();
        Assert.True(next.IsAccepted);
        Assert.Equal(version, graph.StructureVersion);
    }

    private static GraphPassId AddInputPass(RenderGraphEdit edit, GraphParameterInput input, bool bind) =>
        edit.AddCopyFramePass("parameter query fixture", PassQueueSelection.AnyOfType(QueueType.Graphics),
            (input, bind),
            new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
            static (ref PassDefinition pass, ref (GraphParameterInput Input, bool Bind) data) =>
            {
                if (data.Bind) pass.Bind(data.Input, PipelineSync.ComputeShading);
                else pass.Read(data.Input, PipelineSync.ComputeShading);
            },
            static (ref CopyPassCommandScope _, in (GraphParameterInput Input, bool Bind) _) => { });

    private static void AssertInputs(GraphParameterInput[] actual, params GraphParameterInput[] expected)
    {
        Assert.Equal(expected.Length, actual.Length);
        foreach (GraphParameterInput input in expected)
            Assert.Contains(actual, candidate => ReferenceEquals(candidate, input));
    }

    private static void AssertQueryRejected(RenderGraphEdit edit)
    {
        bool rejected = false;
        try { _ = edit.GetParameterInputs(); }
        catch (InvalidOperationException) { rejected = true; }
        Assert.True(rejected);
    }
}
