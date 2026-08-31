using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;

namespace SomeEngine.Render.Tests;

public sealed class RenderMeshInstanceSetTests
{
    [Fact]
    public void ProceduralSnapshotKeepsOneLogicalRevisionAndCoreTransformContract()
    {
        Mesh mesh = TestAssets.Mesh(11);
        Material material = TestAssets.Material(12);
        using var set = new RenderMeshInstanceSet(
            mesh,
            [material],
            instanceCount: 8,
            static (start, current, previous) =>
            {
                for (int row = 0; row < current.Length; row++)
                {
                    var value = new RenderTransform(
                        Quaternion.Identity,
                        new Vector3(start + row, 2, 3),
                        1,
                        Vector3.One);
                    current[row] = value;
                    previous[row] = new RenderPreviousTransform(value);
                }
            },
            boundsExpansion: 0.25f,
            updateMode: RenderMeshInstanceUpdateMode.EveryFrame);

        using RenderMeshInstanceSnapshot first = set.Capture();
        Assert.Equal(mesh, first.Mesh);
        Assert.Equal([material], first.Materials);
        Assert.Equal(8, first.Count);
        Assert.Equal(8, first.Capacity);
        Assert.Equal(0.25f, first.BoundsExpansion);
        Assert.Equal(RenderMeshInstanceUpdateMode.EveryFrame, first.UpdateMode);
        Assert.Equal(1ul, first.Revision);
        Assert.Equal(1ul, first.DataRevision);
        Assert.Equal(RenderInstanceTransformProperties.Layout, first.InstanceLayout);
        Assert.True(first.Changes.StructureChanged);

        set.SetData(
            2,
            static (_, current, previous) =>
            {
                current.Clear();
                previous.Clear();
            },
            RenderMeshInstanceUpdateMode.OnDemand);
        using RenderMeshInstanceSnapshot second = set.Capture();
        Assert.Equal(2ul, second.Revision);
        Assert.Equal(2, second.Count);
        Assert.Equal(RenderMeshInstanceUpdateMode.OnDemand, second.UpdateMode);

        // A captured prepare revision remains coherent after the resource changes.
        Assert.Equal(8, first.Count);
        Assert.Equal(1ul, first.DataRevision);
    }

    [Fact]
    public void BufferedSetUsesCallerDeclaredCanonicalProperties()
    {
        Mesh mesh = TestAssets.Mesh(21);
        Material material = TestAssets.Material(22);
        (RenderInstancePropertyLayout layout,
            ResolvedRenderInstanceProperty<RenderTransform> current,
            ResolvedRenderInstanceProperty<RenderPreviousTransform> previous,
            ResolvedRenderInstanceProperty<Vector4> tint) = CreateMaterialContract();

        using RenderMeshInstanceSet set = RenderMeshInstanceSet.CreateBuffered(
            mesh,
            [material],
            layout,
            capacity: 4,
            boundsExpansion: 0.5f);
        RenderInstanceBuffer buffer = Assert.IsType<RenderInstanceBuffer>(set.Buffer);
        buffer.SetCount(2);

        RenderTransform first = Transform(1);
        RenderTransform second = Transform(2);
        buffer.Set(current, 0, first);
        buffer.Set(current, 1, second);
        buffer.Set(previous, 0, new RenderPreviousTransform(first));
        buffer.Set(previous, 1, new RenderPreviousTransform(second));
        buffer.Set(tint, 0, new Vector4(1, 0, 0, 1));
        buffer.Set(tint, 1, new Vector4(0, 1, 0, 1));

        using (RenderMeshInstanceSnapshot snapshot = set.Capture())
        {
            Assert.Equal(2, snapshot.Count);
            Assert.Equal(4, snapshot.Capacity);
            Assert.Equal(layout, snapshot.InstanceLayout);
            Assert.True(snapshot.Changes.TryGetRange(tint.Key, out RenderInstanceRange range));
            Assert.Equal(RenderInstanceRange.Full(2), range);
        }
        Assert.Equal(second, buffer.Get(current, 1));
        Assert.Equal(new Vector4(0, 1, 0, 1), buffer.Get(tint, 1));
    }

    [Fact]
    public void SharedBindingsAreImmutableAndSourcesMustProvideSpatialContract()
    {
        Mesh mesh = TestAssets.Mesh(31);
        Material material = TestAssets.Material(32);
        using var set = new RenderMeshInstanceSet(
            mesh,
            [material],
            1,
            static (_, current, previous) =>
            {
                current.Clear();
                previous.Clear();
            });

        IList<Material> materials = Assert.IsAssignableFrom<IList<Material>>(
            set.Materials);
        Assert.True(materials.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => materials[0] = TestAssets.Material(33));

        var nonSpatialBuilder = new RenderInstancePropertyLayoutBuilder();
        _ = nonSpatialBuilder.Register<uint>(
            "SomeEngine.Render.Tests",
            new RenderInstancePropertyKey("test.instance.id"),
            new RenderInstancePropertyEncoding(
                "test.uint32.v1",
                valueSize: 4,
                storageAlignment: 4,
                storageStride: 4,
                metadataWordCount: 1));
        using var nonSpatial = new RenderInstanceBuffer(nonSpatialBuilder.Freeze(), 1);
        Assert.Throws<ArgumentException>(() => new RenderMeshInstanceSet(
            mesh,
            [material],
            nonSpatial));

        Assert.Throws<ArgumentNullException>(() => new RenderMeshInstanceSet(
            null!,
            [material],
            1,
            static (_, _, _) => { }));
        Assert.Throws<ArgumentException>(() => new RenderMeshInstanceSet(
            mesh,
            [],
            1,
            static (_, _, _) => { }));
        Assert.Throws<ArgumentException>(() => new RenderMeshInstanceSet(
            mesh,
            [null!],
            1,
            static (_, _, _) => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenderMeshInstanceSet(
            mesh,
            [material],
            -1,
            static (_, _, _) => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenderMeshInstanceSet(
            mesh,
            [material],
            1,
            static (_, _, _) => { },
            boundsExpansion: float.NaN));
    }

    private static (
        RenderInstancePropertyLayout Layout,
        ResolvedRenderInstanceProperty<RenderTransform> Current,
        ResolvedRenderInstanceProperty<RenderPreviousTransform> Previous,
        ResolvedRenderInstanceProperty<Vector4> Tint) CreateMaterialContract()
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
        return (
            layout,
            layout.Resolve(RenderInstanceTransformProperties.CurrentTransform),
            layout.Resolve(RenderInstanceTransformProperties.PreviousTransform),
            layout.Resolve(tint));
    }

    private static RenderTransform Transform(float x) => new(
        Quaternion.Identity,
        new Vector3(x, 0, 0),
        1,
        Vector3.One);
}
