using System.Text;
using SomeEngine.Examples.Curves;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Assets.Consumer.Tests;

public sealed class CurveAssetBehaviorTests
{
    [Fact]
    public async Task OrdinaryCollectionEditsIsolateBranchesAndCopiedValuesCrossAwait()
    {
        using var asset = new CurveAsset();
        asset.GetBlock<CurveBlock>().Points = [new(0, 1), new(1, 2), new(2, 3)];
        using var copy = asset.Duplicate();
        ReadOnlyMemory<Point> range = asset.GetBlock<CurveBlock>().Points.Skip(1).Take(2).ToArray();
        IList<Point> points = copy.GetBlock<CurveBlock>().Points;
        for (int i = 0; i < points.Count; i++) points[i] = points[i] with { Y = points[i].Y * 2 };
        await Task.Yield();
        asset.Dispose();
        Assert.Equal(new Point[] { new(1, 2), new(2, 3) }, range.ToArray());
        Assert.Equal(new Point(2, 6), copy.GetBlock<CurveBlock>().Points[2]);
    }

    [Fact]
    public void CreatedCurvesKeepTheirPointsAfterAssetCloses()
    {
        var asset = new CurveAsset();
        asset.GetBlock<CurveBlock>().Points = [new(0, 0), new(1, 4)];
        Curve curve = asset.GetBlock<CurveBlock>().CreateCurve();
        CurveSamples samples = asset.GetBlock<CurveBlock>().CreateSamples(0, 1, 3);
        asset.GetBlock<CurveBlock>().Points[1] = new(1, 12);
        Assert.Equal(2, curve.Evaluate(0.5f));
        asset.GetBlock<CurveBlock>().Update(curve);
        asset.Dispose();
        Assert.Equal(6, curve.Evaluate(0.5f));
        Assert.Equal(new float[] { 0, 2, 4 }, samples.Values.ToArray());
    }

    [Fact]
    public void DifferentAssetsCreateTheSameUseTypeAndTextEditsIsolateBranches()
    {
        using var asset = new CurveAsset { Name = "linear" };
        asset.GetBlock<CurveBlock>().Points = [new(0, 0), new(1, 4)];
        using var branch = asset.Duplicate();
        var read = asset.GetBlock<CurveBlock>().Points.ToArray();
        TextSerializer.DeserializeInto(Encoding.UTF8.GetBytes("""{"Points":[{"X":0,"Y":8},{"X":1,"Y":8}]}"""), branch.GetBlock<CurveBlock>());
        Curve linear = asset.GetBlock<CurveBlock>().CreateCurve();
        Curve changed = branch.GetBlock<CurveBlock>().CreateCurve();
        using var constant = new ConstantCurveAsset();
        constant.GetBlock<ConstantCurveBlock>().Value = 8;
        Curve flat = constant.GetBlock<ConstantCurveBlock>().CreateCurve();
        Assert.Equal(2, linear.Evaluate(0.5f));
        Assert.Equal(flat.Evaluate(0.5f), changed.Evaluate(0.5f));
        Assert.Equal(new Point(1, 4), read.ToArray()[1]);
        Assert.Equal(asset.Id, branch.Id);
    }

    [Fact]
    public void PointReadsDoNotAllocatePerElement()
    {
        using var asset = new CurveAsset();
        asset.GetBlock<CurveBlock>().Points = Enumerable.Range(0, 4000).Select(i => new Point(i, i * 2)).ToArray();
        var curve = asset.GetBlock<CurveBlock>().CreateCurve();
        for (int i = 0; i < 100; i++) curve.Evaluate(i + 0.5f);
        long before = GC.GetAllocatedBytesForCurrentThread();
        float total = 0;
        for (int i = 0; i < 100; i++) total += curve.Evaluate(i + 0.5f);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(10000, total);
        Assert.Equal(0, allocated);
    }
}
