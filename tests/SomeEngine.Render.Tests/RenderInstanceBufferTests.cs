using System.Numerics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;

namespace SomeEngine.Render.Tests;

public sealed class RenderInstanceBufferTests
{




    [Fact]
    public void CountGrowthZeroesStorageAndLeavesDefaultsToPropertyContributors()
    {
        TestContract contract = CreateContract();
        using var buffer = new RenderInstanceBuffer(contract.Layout, capacity: 1);
        buffer.SetCount(4);

        Assert.True(buffer.Capacity >= 4);
        Assert.Equal(default, buffer.Get(contract.Current, 0));
        Assert.Equal(default, buffer.Get(contract.Previous, 3));
        Assert.Equal(Vector4.Zero, buffer.Get(contract.Tint, 2));
    }





    [Fact]
    public void TransactionRollbackAndConcurrentWritesNeverPublishPartialValues()
    {
        TestContract contract = CreateContract();
        using var buffer = new RenderInstanceBuffer(contract.Layout, capacity: 2);
        buffer.SetCount(2);

        ulong beforeRollback = buffer.Revision;
        using (RenderInstanceUpdate update = buffer.BeginUpdate())
            update.Write(contract.Current, 0, Transform(9.0f));
        Assert.Equal(beforeRollback, buffer.Revision);
        Assert.Equal(default, buffer.Get(contract.Current, 0));

        using RenderInstanceUpdate updateAfterExternalWrite = buffer.BeginUpdate();
        updateAfterExternalWrite.Write(contract.Current, 0, Transform(3.0f));
        buffer.Set(contract.Current, 1, Transform(4.0f));
        updateAfterExternalWrite.Commit();
        Assert.Equal(Transform(3.0f), buffer.Get(contract.Current, 0));
        Assert.Equal(Transform(4.0f), buffer.Get(contract.Current, 1));
    }



    private static TestContract CreateContract()
    {
        var builder = new RenderInstancePropertyLayoutBuilder();
        builder.Include(RenderInstanceTransformProperties.Layout);
        RenderInstanceProperty<Vector4> tint = builder.Register<Vector4>(
            "SomeEngine.Render.Tests.Material",
            new RenderInstancePropertyKey("test.material.tint"),
            new RenderInstancePropertyEncoding(
                "test.material.float4.v1",
                valueSize: 16,
                storageAlignment: 16,
                storageStride: 16,
                metadataWordCount: 1));
        RenderInstancePropertyLayout layout = builder.Freeze();
        return new TestContract(
            layout,
            layout.Resolve(RenderInstanceTransformProperties.CurrentTransform),
            layout.Resolve(RenderInstanceTransformProperties.PreviousTransform),
            layout.Resolve(tint));
    }

    private static RenderTransform Transform(float x) => new(
        Quaternion.Identity,
        new Vector3(x, 0.0f, 0.0f),
        1.0f,
        Vector3.One);

    private readonly record struct TestContract(
        RenderInstancePropertyLayout Layout,
        ResolvedRenderInstanceProperty<RenderTransform> Current,
        ResolvedRenderInstanceProperty<RenderPreviousTransform> Previous,
        ResolvedRenderInstanceProperty<Vector4> Tint);
}
