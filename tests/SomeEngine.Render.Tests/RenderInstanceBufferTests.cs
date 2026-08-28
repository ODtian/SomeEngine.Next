using System.Numerics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;

namespace SomeEngine.Render.Tests;

public sealed class RenderInstanceBufferTests
{
    [Fact]
    public void DenseBufferMovesEveryDeclaredPropertyDuringSwapBackRemoval()
    {
        TestContract contract = CreateContract();
        using var buffer = new RenderInstanceBuffer(contract.Layout, capacity: 2);
        buffer.SetCount(2);

        RenderTransform first = Transform(1.0f);
        RenderTransform second = Transform(2.0f);
        buffer.Set(contract.Current, 0, first);
        buffer.Set(contract.Current, 1, second);
        buffer.Set(contract.Previous, 0, new RenderPreviousTransform(first));
        buffer.Set(contract.Previous, 1, new RenderPreviousTransform(second));
        buffer.Set(contract.Tint, 0, new Vector4(1, 0, 0, 1));
        buffer.Set(contract.Tint, 1, new Vector4(0, 1, 0, 1));

        ulong beforeRemoval = buffer.Revision;
        RenderInstanceRemoval removal = buffer.RemoveAtSwapBack(0);

        Assert.True(removal.Moved);
        Assert.Equal(1, removal.MovedFromIndex);
        Assert.Equal(1, buffer.Count);
        Assert.Equal(second, buffer.Get(contract.Current, 0));
        Assert.Equal(new RenderPreviousTransform(second), buffer.Get(contract.Previous, 0));
        Assert.Equal(new Vector4(0, 1, 0, 1), buffer.Get(contract.Tint, 0));

        using RenderInstanceSourceSnapshot snapshot = buffer.Capture(beforeRemoval);
        Assert.True(snapshot.Changes.StructureChanged);
        Assert.True(snapshot.Changes.TryGetRange(
            contract.Current.Key,
            out RenderInstanceRange currentRange));
        Assert.Equal(RenderInstanceRange.Full(1), currentRange);
        Assert.True(snapshot.Changes.TryGetRange(
            contract.Tint.Key,
            out RenderInstanceRange tintRange));
        Assert.Equal(RenderInstanceRange.Full(1), tintRange);
    }

    [Fact]
    public void PropertyLocalWritesCoalesceWithoutInventingSemanticChannels()
    {
        TestContract contract = CreateContract();
        using var buffer = new RenderInstanceBuffer(contract.Layout, capacity: 8);
        buffer.SetCount(6);
        ulong baseline = buffer.Revision;

        buffer.Set(contract.Current, 4, Transform(4.0f));
        buffer.Set(contract.Current, 2, Transform(2.0f));
        using (RenderInstanceColumnWriteScope<RenderTransform> write =
            buffer.BeginWrite(contract.Current, 1, 2))
        {
            write.Values[0] = Transform(10.0f);
            write.Values[1] = Transform(11.0f);
        }
        using (RenderInstanceColumnWriteScope<RenderPreviousTransform> write =
            buffer.BeginWrite(contract.Previous, 1, 2))
        {
            write.Values[0] = new RenderPreviousTransform(Transform(10.0f));
            write.Values[1] = new RenderPreviousTransform(Transform(11.0f));
        }

        using (RenderInstanceSourceSnapshot snapshot = buffer.Capture(baseline))
        {
            Assert.False(snapshot.Changes.StructureChanged);
            Assert.True(snapshot.Changes.TryGetRange(
                contract.Current.Key,
                out RenderInstanceRange currentRange));
            Assert.Equal(new RenderInstanceRange(1, 4), currentRange);
            Assert.True(snapshot.Changes.TryGetRange(
                contract.Previous.Key,
                out RenderInstanceRange previousRange));
            Assert.Equal(new RenderInstanceRange(1, 2), previousRange);
            Assert.False(snapshot.Changes.TryGetRange(contract.Tint.Key, out _));
        }

        var values = new RenderTransform[4];
        buffer.Copy(contract.Current, 1, values);
        Assert.Equal(
            [10.0f, 11.0f, 0.0f, 4.0f],
            values.Select(static value => value.Position.X));
    }

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
    public void ProceduralInvalidationUsesCanonicalPropertyIdentity()
    {
        TestContract contract = CreateContract();
        bool writerCalled = false;
        var source = new RenderInstanceProceduralSource(
            contract.Layout,
            count: 16,
            (_, _) => writerCalled = true);

        using RenderInstanceSourceSnapshot first = source.Capture();
        Assert.False(writerCalled);
        Assert.True(first.Changes.StructureChanged);
        Assert.Equal(contract.Layout, first.Layout);

        ulong revision = source.Revision;
        source.Invalidate(contract.Tint, new RenderInstanceRange(4, 3));
        using RenderInstanceSourceSnapshot second = source.Capture(revision);
        Assert.False(second.Changes.StructureChanged);
        Assert.True(second.Changes.TryGetRange(
            contract.Tint.Key,
            out RenderInstanceRange range));
        Assert.Equal(new RenderInstanceRange(4, 3), range);
        Assert.False(second.Changes.TryGetRange(contract.Current.Key, out _));
    }

    [Fact]
    public void TransactionPublishesMultiplePropertiesAsOneRevisionAndKeepsSparseIndices()
    {
        TestContract contract = CreateContract();
        using var buffer = new RenderInstanceBuffer(contract.Layout, capacity: 4);
        buffer.SetCount(4);
        ulong baseline = buffer.Revision;

        using (RenderInstanceUpdate update = buffer.BeginUpdate())
        {
            update.Set(contract.Current, 1, Transform(7.0f));
            update.Set(
                contract.Previous,
                1,
                new RenderPreviousTransform(Transform(6.0f)));
            update.WriteSparse(
                contract.Tint,
                [0, 3],
                [new Vector4(1, 0, 0, 1), new Vector4(0, 0, 1, 1)]);

            Assert.Equal(baseline, buffer.Revision);
            Assert.Equal(default, buffer.Get(contract.Current, 1));
            Assert.Equal(baseline + 1ul, update.Commit());
        }

        Assert.Equal(Transform(7.0f), buffer.Get(contract.Current, 1));
        Assert.Equal(
            new RenderPreviousTransform(Transform(6.0f)),
            buffer.Get(contract.Previous, 1));
        Assert.Equal(new Vector4(1, 0, 0, 1), buffer.Get(contract.Tint, 0));
        Assert.Equal(new Vector4(0, 0, 1, 1), buffer.Get(contract.Tint, 3));

        using RenderInstanceSourceSnapshot snapshot = buffer.Capture(baseline);
        Assert.False(snapshot.Changes.StructureChanged);
        Assert.True(snapshot.Changes.TryGetRange(
            contract.Current.Key,
            out RenderInstanceRange currentRange));
        Assert.Equal(new RenderInstanceRange(1, 1), currentRange);
        Assert.True(snapshot.Changes.TryGetRange(
            contract.Previous.Key,
            out RenderInstanceRange previousRange));
        Assert.Equal(new RenderInstanceRange(1, 1), previousRange);
        Assert.True(snapshot.Changes.TryGetSparseIndices(
            contract.Tint.Key,
            out ReadOnlyMemory<int> tintIndices));
        Assert.Equal([0, 3], tintIndices.ToArray());
        Assert.False(snapshot.Changes.TryGetRange(contract.Tint.Key, out _));
    }

    [Fact]
    public void TransactionRollbackAndOptimisticConflictNeverPublishPartialValues()
    {
        TestContract contract = CreateContract();
        using var buffer = new RenderInstanceBuffer(contract.Layout, capacity: 2);
        buffer.SetCount(2);

        ulong beforeRollback = buffer.Revision;
        using (RenderInstanceUpdate update = buffer.BeginUpdate())
            update.Set(contract.Current, 0, Transform(9.0f));
        Assert.Equal(beforeRollback, buffer.Revision);
        Assert.Equal(default, buffer.Get(contract.Current, 0));

        using RenderInstanceUpdate stale = buffer.BeginUpdate();
        stale.Set(contract.Current, 0, Transform(3.0f));
        buffer.Set(contract.Current, 1, Transform(4.0f));
        Assert.Throws<InvalidOperationException>(() => stale.Commit());
        Assert.Equal(default, buffer.Get(contract.Current, 0));
        Assert.Equal(Transform(4.0f), buffer.Get(contract.Current, 1));
    }

    [Fact]
    public void TransactionStructureMutationPublishesFullPropertyInvalidation()
    {
        TestContract contract = CreateContract();
        using var buffer = new RenderInstanceBuffer(contract.Layout, capacity: 2);
        buffer.SetCount(2);
        ulong baseline = buffer.Revision;

        using (RenderInstanceUpdate update = buffer.BeginUpdate())
        {
            int start = update.AddRange(2);
            Assert.Equal(2, start);
            update.Set(contract.Current, 2, Transform(2.0f));
            update.Set(contract.Current, 3, Transform(3.0f));
            _ = update.Commit();
        }

        Assert.Equal(4, buffer.Count);
        using RenderInstanceSourceSnapshot snapshot = buffer.Capture(baseline);
        Assert.True(snapshot.Changes.StructureChanged);
        foreach (RenderInstancePropertyDescriptor property in contract.Layout.Properties)
        {
            Assert.True(snapshot.Changes.TryGetRange(
                property.Key,
                out RenderInstanceRange range));
            Assert.Equal(RenderInstanceRange.Full(4), range);
        }
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
