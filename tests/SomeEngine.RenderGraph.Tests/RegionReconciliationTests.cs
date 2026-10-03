using static SomeEngine.RenderGraph.Tests.FrameRegionTests;

namespace SomeEngine.RenderGraph.Tests;

public sealed class RegionReconciliationTests
{
    [Fact]
    public void NewKeysBuildChildRegionsAtTheEndOfTheirParent()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId parent;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            parent = edit.AddRegion("Keys");
            AddCopy(edit, data.Queue, edit.AddRegion("Read"), data.Values, data.Output);
            edit.Commit();
        }
        var original = new GraphRegionMap<string, uint>(parent);
        GraphRegionMap<string, uint> result;
        RenderGraphEdit update = data.Graph.BeginEdit();
        try
        {
            result = original.Reconcile(ref update,
                [new("first", 23), new("second", 47)],
                (ref RenderGraphEdit edit, GraphRegionId region, string key, uint value) =>
                    AddClear(edit, data.Queue, region, data.Values, value));
            update.Commit();
        }
        finally
        {
            update.Dispose();
        }
        data.Execute();
        Assert.True(result.TryGetRegion("first", out GraphRegionId first));
        Assert.True(result.TryGetRegion("second", out GraphRegionId second));
        Assert.NotEqual(first, second);
        Assert.Equal(new uint[] { 23, 47 }, result.Values.Order().ToArray());
        Assert.Empty(original.Values);
        Assert.Equal(new uint[] { 47, 47, 47, 47 }, data.Read());
    }

    [Fact]
    public void DisappearingKeysRemoveTheirChildRegionWork()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId parent;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            AddClear(edit, data.Queue, default, data.Values, 7);
            parent = edit.AddRegion("Keys");
            AddCopy(edit, data.Queue, edit.AddRegion("Read"), data.Values, data.Output);
            edit.Commit();
        }
        var regions = new GraphRegionMap<string, uint>(parent);
        RenderGraphEdit create = data.Graph.BeginEdit();
        try
        {
            regions = regions.Reconcile(ref create, [new("first", 23), new("second", 47)],
                (ref RenderGraphEdit edit, GraphRegionId region, string key, uint value) =>
                    AddClear(edit, data.Queue, region, data.Values, value));
            create.Commit();
        }
        finally
        {
            create.Dispose();
        }
        data.Execute();
        RenderGraphEdit remove = data.Graph.BeginEdit();
        try
        {
            regions = regions.Reconcile(ref remove, [],
                static (ref RenderGraphEdit edit, GraphRegionId region, string key, uint value) =>
                    throw new InvalidOperationException("An empty desired sequence has no regions to build."));
            remove.Commit();
        }
        finally
        {
            remove.Dispose();
        }
        data.Execute();
        Assert.Equal(new uint[] { 7, 7, 7, 7 }, data.Read());
    }

    [Fact]
    public void EqualValuesKeepRegionIdentitiesAndReuseAnalysis()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId parent;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            parent = edit.AddRegion("Keys");
            AddCopy(edit, data.Queue, edit.AddRegion("Read"), data.Values, data.Output);
            edit.Commit();
        }
        var original = new GraphRegionMap<string, uint>(parent);
        RenderGraphEdit create = data.Graph.BeginEdit();
        try
        {
            original = original.Reconcile(ref create, [new("write", 37), new("other", 99)],
                (ref RenderGraphEdit edit, GraphRegionId region, string key, uint value) =>
                    AddClear(edit, data.Queue, region, key == "write" ? data.Values : data.Saved, value));
            create.Commit();
        }
        finally
        {
            create.Dispose();
        }
        data.Execute();
        data.Execute();
        GraphRegionMap<string, uint> result;
        RenderGraphEdit update = data.Graph.BeginEdit();
        try
        {
            result = original.Reconcile(ref update, [new("other", 99), new("write", 37)],
                static (ref RenderGraphEdit edit, GraphRegionId region, string key, uint value) =>
                    throw new InvalidOperationException("Equal values keep their existing work."));
            update.Commit();
        }
        finally
        {
            update.Dispose();
        }
        RenderGraphStatistics statistics = data.Execute();
        Assert.True(original.TryGetRegion("write", out GraphRegionId before));
        Assert.True(result.TryGetRegion("write", out GraphRegionId after));
        Assert.Equal(before, after);
        Assert.True(statistics.AnalysisReused);
        Assert.Equal(0, statistics.AnalyzedPassCount);
        Assert.Equal(0, statistics.RebuiltRowCount);
        Assert.Equal(new uint[] { 37, 37, 37, 37 }, data.Read());
    }

    [Fact]
    public void ChangedValuesReplaceThePreviousChildRegionWork()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId parent;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            AddClear(edit, data.Queue, default, data.Values, 7);
            parent = edit.AddRegion("Keys");
            AddCopy(edit, data.Queue, edit.AddRegion("Read"), data.Values, data.Output);
            edit.Commit();
        }
        var original = new GraphRegionMap<string, uint>(parent);
        RenderGraphEdit create = data.Graph.BeginEdit();
        try
        {
            original = original.Reconcile(ref create, [new("write", 31)],
                (ref RenderGraphEdit edit, GraphRegionId region, string key, uint value) =>
                    AddClear(edit, data.Queue, region, data.Values, value));
            create.Commit();
        }
        finally
        {
            create.Dispose();
        }
        data.Execute();
        GraphRegionMap<string, uint> result;
        RenderGraphEdit replace = data.Graph.BeginEdit();
        try
        {
            result = original.Reconcile(ref replace, [new("write", 43)],
                (ref RenderGraphEdit edit, GraphRegionId region, string key, uint value) =>
                    AddClear(edit, data.Queue, region, data.Values, value));
            replace.Commit();
        }
        finally
        {
            replace.Dispose();
        }
        data.Execute();
        Assert.Equal(new uint[] { 43, 43, 43, 43 }, data.Read());
        Assert.True(original.TryGetRegion("write", out GraphRegionId before));
        Assert.True(result.TryGetRegion("write", out GraphRegionId after));
        Assert.NotEqual(before, after);
        using (RenderGraphFrame frame = data.Graph.BeginFrame())
        {
            frame.SetRegionEnabled(after, false);
            data.Execute(frame);
        }
        Assert.Equal(new uint[] { 7, 7, 7, 7 }, data.Read());
    }

    [Fact]
    public void ChangingOneKeyKeepsOtherRegionIdentitiesAndLimitsAnalysis()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId parent;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            parent = edit.AddRegion("Keys");
            AddCopy(edit, data.Queue, edit.AddRegion("Read"), data.Values, data.Output);
            edit.Commit();
        }
        var original = new GraphRegionMap<string, uint>(parent);
        RenderGraphEdit create = data.Graph.BeginEdit();
        try
        {
            original = original.Reconcile(ref create, [new("write", 37), new("other", 99)],
                (ref RenderGraphEdit edit, GraphRegionId region, string key, uint value) =>
                {
                    int count = key == "write" ? 1 : 8;
                    GraphBufferId buffer = key == "write" ? data.Values : data.Saved;
                    for (int index = 0; index < count; index++) AddClear(edit, data.Queue, region, buffer, value);
                });
            create.Commit();
        }
        finally
        {
            create.Dispose();
        }
        data.Execute();
        data.Execute();
        GraphRegionMap<string, uint> result;
        RenderGraphEdit replace = data.Graph.BeginEdit();
        try
        {
            result = original.Reconcile(ref replace, [new("write", 59), new("other", 99)],
                (ref RenderGraphEdit edit, GraphRegionId region, string key, uint value) =>
                    AddClear(edit, data.Queue, region, data.Values, value));
            replace.Commit();
        }
        finally
        {
            replace.Dispose();
        }
        RenderGraphStatistics statistics = data.Execute();
        Assert.True(original.TryGetRegion("other", out GraphRegionId before));
        Assert.True(result.TryGetRegion("other", out GraphRegionId after));
        Assert.Equal(before, after);
        Assert.InRange(statistics.AnalyzedPassCount, 1, 2);
        Assert.Equal(new uint[] { 59, 59, 59, 59 }, data.Read());
    }

    [Fact]
    public void DiscardingAnEditLeavesTheOriginalMapUsableForTheNextEdit()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId parent;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            parent = edit.AddRegion("Keys");
            AddCopy(edit, data.Queue, edit.AddRegion("Read"), data.Values, data.Output);
            edit.Commit();
        }
        var original = new GraphRegionMap<string, uint>(parent);
        RenderGraphEdit create = data.Graph.BeginEdit();
        try
        {
            original = original.Reconcile(ref create, [new("write", 31)],
                (ref RenderGraphEdit edit, GraphRegionId region, string key, uint value) =>
                    AddClear(edit, data.Queue, region, data.Values, value));
            create.Commit();
        }
        finally
        {
            create.Dispose();
        }
        Assert.True(original.TryGetRegion("write", out GraphRegionId before));
        RenderGraphEdit discarded = data.Graph.BeginEdit();
        try
        {
            _ = original.Reconcile(ref discarded, [new("write", 43), new("new", 47)],
                (ref RenderGraphEdit edit, GraphRegionId region, string key, uint value) =>
                    AddClear(edit, data.Queue, region, data.Values, value));
        }
        finally
        {
            discarded.Dispose();
        }
        data.Execute();
        Assert.Equal(new uint[] { 31, 31, 31, 31 }, data.Read());
        Assert.True(original.TryGetRegion("write", out GraphRegionId after));
        Assert.Equal(before, after);
        RenderGraphEdit retry = data.Graph.BeginEdit();
        try
        {
            original = original.Reconcile(ref retry, [new("write", 61)],
                (ref RenderGraphEdit edit, GraphRegionId region, string key, uint value) =>
                    AddClear(edit, data.Queue, region, data.Values, value));
            retry.Commit();
        }
        finally
        {
            retry.Dispose();
        }
        data.Execute();
        Assert.Equal(new uint[] { 61, 61, 61, 61 }, data.Read());
    }
}
