using System.Numerics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;

namespace SomeEngine.Render.Tests;

public sealed class RenderInstanceUpdateTests
{
    [Fact]
    public void Staged_values_are_invisible_until_commit()
    {
        RenderInstancePropertyLayout layout = RenderInstanceTransformProperties.Layout;
        ResolvedRenderInstanceProperty<RenderTransform> current =
            layout.Resolve(RenderInstanceTransformProperties.CurrentTransform);
        using var buffer = new RenderInstanceBuffer(layout, capacity: 1);
        _ = buffer.Add();
        RenderTransform original = Transform(1.0f);
        RenderTransform replacement = Transform(2.0f);
        buffer.Set(current, 0, original);

        using RenderInstanceUpdate update = buffer.BeginUpdate();
        update.Write(current, 0, replacement);

        Assert.Equal(original, buffer.Get(current, 0));
        update.Commit();
        Assert.Equal(replacement, buffer.Get(current, 0));
    }

    [Fact]
    public void Disposing_without_commit_discards_staged_changes()
    {
        RenderInstancePropertyLayout layout = RenderInstanceTransformProperties.Layout;
        ResolvedRenderInstanceProperty<RenderTransform> current =
            layout.Resolve(RenderInstanceTransformProperties.CurrentTransform);
        using var buffer = new RenderInstanceBuffer(layout, capacity: 1);
        _ = buffer.Add();
        RenderTransform original = Transform(1.0f);
        buffer.Set(current, 0, original);

        using (RenderInstanceUpdate update = buffer.BeginUpdate())
            update.Write(current, 0, Transform(9.0f));

        Assert.Equal(original, buffer.Get(current, 0));
    }

    [Fact]
    public void One_commit_updates_count_and_multiple_properties_coherently()
    {
        RenderInstancePropertyLayout layout = RenderInstanceTransformProperties.Layout;
        ResolvedRenderInstanceProperty<RenderTransform> current =
            layout.Resolve(RenderInstanceTransformProperties.CurrentTransform);
        ResolvedRenderInstanceProperty<RenderPreviousTransform> previous =
            layout.Resolve(RenderInstanceTransformProperties.PreviousTransform);
        using var buffer = new RenderInstanceBuffer(layout);
        ulong revisionBefore = buffer.Revision;

        using RenderInstanceUpdate update = buffer.BeginUpdate();
        update.SetCount(3);
        update.WriteRange(current, 0, [Transform(1.0f), Transform(2.0f), Transform(3.0f)]);
        update.WriteRange(previous, 0,
        [
            new RenderPreviousTransform(Transform(0.0f)),
            new RenderPreviousTransform(Transform(1.0f)),
            new RenderPreviousTransform(Transform(2.0f)),
        ]);
        update.Commit();

        Assert.Equal(3, buffer.Count);
        Assert.Equal(revisionBefore + 1ul, buffer.Revision);
        Assert.Equal(Transform(3.0f), buffer.Get(current, 2));
        Assert.Equal(
            new RenderPreviousTransform(Transform(1.0f)),
            buffer.Get(previous, 1));
    }

    [Fact]
    public void Sparse_write_is_index_exact_and_publishes_property_change()
    {
        RenderInstancePropertyLayout layout = RenderInstanceTransformProperties.Layout;
        ResolvedRenderInstanceProperty<RenderTransform> current =
            layout.Resolve(RenderInstanceTransformProperties.CurrentTransform);
        using var buffer = new RenderInstanceBuffer(layout, capacity: 4);
        for (int index = 0; index < 4; index++)
            _ = buffer.Add();
        buffer.SetRange(
            current,
            0,
            [Transform(0.0f), Transform(0.0f), Transform(0.0f), Transform(0.0f)]);
        ulong previousRevision = buffer.Revision;

        using (RenderInstanceUpdate update = buffer.BeginUpdate())
        {
            update.WriteSparse(
                current,
                [3, 1],
                [Transform(30.0f), Transform(10.0f)]);
            update.Commit();
        }

        Assert.Equal(Transform(0.0f), buffer.Get(current, 0));
        Assert.Equal(Transform(10.0f), buffer.Get(current, 1));
        Assert.Equal(Transform(0.0f), buffer.Get(current, 2));
        Assert.Equal(Transform(30.0f), buffer.Get(current, 3));
        using RenderInstanceSourceSnapshot snapshot = buffer.Capture(previousRevision);
        RenderInstancePropertyChange changed = Assert.Single(snapshot.Changes.Properties);
        Assert.Equal(new RenderInstanceRange(1, 3), changed.Range);
    }

    [Fact]
    public void Invalid_transaction_is_rejected_before_mutating_buffer()
    {
        RenderInstancePropertyLayout layout = RenderInstanceTransformProperties.Layout;
        ResolvedRenderInstanceProperty<RenderTransform> current =
            layout.Resolve(RenderInstanceTransformProperties.CurrentTransform);
        using var buffer = new RenderInstanceBuffer(layout, capacity: 1);
        _ = buffer.Add();
        RenderTransform original = Transform(1.0f);
        buffer.Set(current, 0, original);

        using RenderInstanceUpdate update = buffer.BeginUpdate();
        update.SetCount(1);
        update.Write(current, 2, Transform(2.0f));

        Assert.Throws<ArgumentOutOfRangeException>(() => update.Commit());
        Assert.Equal(1, buffer.Count);
        Assert.Equal(original, buffer.Get(current, 0));
    }

    private static RenderTransform Transform(float x) =>
        new(Quaternion.Identity, new Vector3(x, 0.0f, 0.0f), 1.0f, Vector3.One);
}
