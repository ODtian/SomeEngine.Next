using System.Numerics;
using System.Runtime.CompilerServices;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;

namespace SomeEngine.Render.Tests;

public sealed class RenderInstanceLayoutTests
{
    [Fact]
    public void FreezeLinksOpaqueKeysDeterministically()
    {
        var firstBuilder = new RenderInstancePropertyLayoutBuilder();
        RenderInstanceProperty<Vector4> firstTint = firstBuilder.Register<Vector4>(
            "Test.Material",
            Key("someengine.material.tint"),
            Linear<Vector4>("test.float4.v1", 16, 16));
        RenderInstanceProperty<float> firstBounds = firstBuilder.Register<float>(
            "Test.Pipeline",
            Key("someengine.cluster.bounds_expansion"),
            Linear<float>("test.float.v1", 4, 4));
        RenderInstanceProperty<uint> firstRoot = firstBuilder.Register<uint>(
            "Test.Pipeline",
            Key("someengine.cluster.bvh_root"),
            Linear<uint>("test.uint.v1", 4, 4));

        var secondBuilder = new RenderInstancePropertyLayoutBuilder();
        RenderInstanceProperty<uint> secondRoot = secondBuilder.Register<uint>(
            "Test.Pipeline",
            Key("someengine.cluster.bvh_root"),
            Linear<uint>("test.uint.v1", 4, 4));
        RenderInstanceProperty<float> secondBounds = secondBuilder.Register<float>(
            "Test.Pipeline",
            Key("someengine.cluster.bounds_expansion"),
            Linear<float>("test.float.v1", 4, 4));
        RenderInstanceProperty<Vector4> secondTint = secondBuilder.Register<Vector4>(
            "Test.Material",
            Key("someengine.material.tint"),
            Linear<Vector4>("test.float4.v1", 16, 16));

        RenderInstancePropertyLayout first = firstBuilder.Freeze();
        RenderInstancePropertyLayout second = secondBuilder.Freeze();

        Assert.NotSame(first, second);
        Assert.Same(first, firstBuilder.Freeze());
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal(3, first.MetadataWordCount);
        Assert.Equal(
            new[]
            {
                "someengine.cluster.bounds_expansion",
                "someengine.cluster.bvh_root",
                "someengine.material.tint",
            },
            first.Properties.Select(static property => property.Key.Value));

        Assert.Equal(0, first.Resolve(firstBounds).Ordinal);
        Assert.Equal(1, first.Resolve(firstRoot).Ordinal);
        Assert.Equal(2, first.Resolve(firstTint).Ordinal);
        Assert.Equal(0, second.Resolve(secondBounds).Ordinal);
        Assert.Equal(1, second.Resolve(secondRoot).Ordinal);
        Assert.Equal(2, second.Resolve(secondTint).Ordinal);

        var properties = Assert.IsAssignableFrom<IList<RenderInstancePropertyDescriptor>>(first.Properties);
        Assert.True(properties.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => properties.Add(first.Properties[0]));
    }

    [Fact]
    public void ComposeDeduplicatesOnlyKeyAndEncoding()
    {
        RenderInstancePropertyEncoding roughnessEncoding = Linear<float>("test.roughness.f32.v1", 4, 8);
        var pipelineBuilder = new RenderInstancePropertyLayoutBuilder();
        RenderInstanceProperty<float> pipelineRoughness = pipelineBuilder.Register<float>(
            "Test.Pipeline",
            Key("someengine.surface.roughness"),
            roughnessEncoding);
        pipelineBuilder.Register<uint>(
            "Test.Pipeline",
            Key("someengine.cluster.bvh_root"),
            Linear<uint>("test.uint.v1", 4, 4));

        var materialBuilder = new RenderInstancePropertyLayoutBuilder();
        RenderInstanceProperty<float> materialRoughness = materialBuilder.Register<float>(
            "Test.Material",
            Key("someengine.surface.roughness"),
            roughnessEncoding);
        materialBuilder.Register<Vector4>(
            "Test.Material",
            Key("someengine.material.tint"),
            Linear<Vector4>("test.float4.v1", 16, 16));

        RenderInstancePropertyLayout pipeline = pipelineBuilder.Freeze();
        RenderInstancePropertyLayout material = materialBuilder.Freeze();
        RenderInstancePropertyLayout composed = RenderInstancePropertyLayout.Compose(pipeline, material);

        Assert.Equal(3, composed.Properties.Count);
        Assert.True(composed.Contains(Key("someengine.cluster.bvh_root")));
        Assert.True(composed.Contains(Key("someengine.material.tint")));
        RenderInstancePropertyDescriptor roughness = composed.Resolve(pipelineRoughness).Descriptor;
        Assert.Same(composed.Resolve(materialRoughness).Descriptor, roughness);
        Assert.Equal(new[] { "Test.Material", "Test.Pipeline" }, roughness.Contributors);
    }

    [Fact]
    public void ComposeRejectsOnlyEncodingConflictsForTheSameKey()
    {
        RenderInstancePropertyKey key = Key("someengine.surface.value");
        RenderInstancePropertyLayout pipeline = PropertyLayout<float>(
            "Test.Pipeline",
            key,
            Linear<float>("test.f32.v1", 4, 4));
        RenderInstancePropertyLayout differentStride = PropertyLayout<float>(
            "Test.Material",
            key,
            Linear<float>("test.f32.v1", 4, 8));
        InvalidOperationException conflict = Assert.Throws<InvalidOperationException>(
            () => RenderInstancePropertyLayout.Compose(pipeline, differentStride));
        Assert.Contains("encoding", conflict.Message, StringComparison.OrdinalIgnoreCase);

        RenderInstancePropertyLayout differentCodec = PropertyLayout<float>(
            "Test.Material",
            key,
            Linear<float>("test.packed_f32.v2", 4, 4));
        Assert.Throws<InvalidOperationException>(
            () => RenderInstancePropertyLayout.Compose(pipeline, differentCodec));

        var frozenBuilder = new RenderInstancePropertyLayoutBuilder();
        frozenBuilder.Register<uint>(
            "Test",
            Key("someengine.value"),
            Linear<uint>("test.uint.v1", 4, 4));
        frozenBuilder.Freeze();
        Assert.Throws<InvalidOperationException>(() => frozenBuilder.Register<uint>(
            "Test",
            Key("someengine.other"),
            Linear<uint>("test.uint.v1", 4, 4)));
    }

    [Fact]
    public void StructuralEqualityContainsKeysEncodingsAndDenseMetadataOffsets()
    {
        RenderInstancePropertyLayout baseline = PropertyLayout<float>(
            "Test",
            Key("someengine.value"),
            Linear<float>("test.f32.v1", 4, 4));
        Assert.NotEqual(baseline, PropertyLayout<float>(
            "Test",
            Key("someengine.other"),
            Linear<float>("test.f32.v1", 4, 4)));
        Assert.NotEqual(baseline, PropertyLayout<float>(
            "Test",
            Key("someengine.value"),
            Linear<float>("test.f32.v2", 4, 4)));
        Assert.NotEqual(baseline, PropertyLayout<float>(
            "Test",
            Key("someengine.value"),
            Linear<float>("test.f32.v1", 4, 8)));

        Assert.Null(typeof(RenderInstancePropertyLayout).GetProperty("ContractId"));
        Assert.Null(typeof(RenderInstancePropertyEncoding).GetProperty("Fingerprint"));

        var twoWordBuilder = new RenderInstancePropertyLayoutBuilder();
        RenderInstanceProperty<uint> custom = twoWordBuilder.Register<uint>(
            "Test",
            Key("someengine.custom.inline_or_address"),
            new RenderInstancePropertyEncoding(
                "test.inline_or_address.v1",
                valueSize: 4,
                storageAlignment: 4,
                storageStride: 0,
                metadataWordCount: 2));
        RenderInstanceProperty<uint> linear = twoWordBuilder.Register<uint>(
            "Test",
            Key("someengine.value"),
            Linear<uint>("test.uint.v1", 4, 4));
        RenderInstancePropertyLayout layout = twoWordBuilder.Freeze();

        Assert.Equal(3, layout.MetadataWordCount);
        Assert.Equal(0, layout.Resolve(custom).Descriptor.MetadataWordOffset);
        Assert.Equal(2, layout.Resolve(linear).Descriptor.MetadataWordOffset);
        Assert.False(layout.Resolve(custom).Encoding.HasManagedStorage);
    }

    [Fact]
    public void RegistrationRequiresAnExplicitValidStrideAndMatchingManagedSize()
    {
        Assert.Throws<ArgumentException>(() => new RenderInstancePropertyKey("someengine..invalid"));
        Assert.Throws<ArgumentException>(() => new RenderInstancePropertyEncoding(
            "test.bad_stride.v1",
            valueSize: 12,
            storageAlignment: 16,
            storageStride: 12,
            metadataWordCount: 1));

        var builder = new RenderInstancePropertyLayoutBuilder();
        Assert.Throws<ArgumentException>(() => builder.Register<Vector3>(
            "Test",
            Key("someengine.direction"),
            new RenderInstancePropertyEncoding(
                "test.wrong_size.v1",
                valueSize: 16,
                storageAlignment: 16,
                storageStride: 16,
                metadataWordCount: 1)));

        RenderInstanceProperty<Vector3> direction = builder.Register<Vector3>(
            "Test",
            Key("someengine.direction"),
            Linear<Vector3>("test.float3.v1", 16, 16));
        RenderInstancePropertyDescriptor descriptor = builder.Freeze().Resolve(direction).Descriptor;
        Assert.Equal(12, descriptor.Encoding.ValueSize);
        Assert.Equal(16, descriptor.Encoding.StorageAlignment);
        Assert.Equal(16, descriptor.Encoding.StorageStride);
        Assert.Equal(1, descriptor.Encoding.MetadataWordCount);
    }

    [Fact]
    public void TransformModuleOwnsItsEncodingWithoutTeachingTheGenericTransportItsMeaning()
    {
        var builder = new RenderInstancePropertyLayoutBuilder();
        RenderInstanceTransformProperties.Register(builder);
        RenderInstancePropertyLayout layout = builder.Freeze();

        RenderInstancePropertyDescriptor current = layout.Resolve<RenderTransform>(
            RenderInstanceTransformProperties.CurrentTransformKey).Descriptor;
        Assert.Equal(RenderTransform.SizeInBytes, current.Encoding.ValueSize);
        Assert.Equal(16, current.Encoding.StorageAlignment);
        Assert.Equal(48, current.Encoding.StorageStride);
        Assert.Equal("someengine.render.transform_qvvs48.v1", current.Encoding.Codec);
    }

    private static RenderInstancePropertyKey Key(string value) => new(value);

    private static RenderInstancePropertyEncoding Linear<T>(
        string id,
        int alignment,
        int stride)
        where T : unmanaged =>
        new(id, Unsafe.SizeOf<T>(), alignment, stride, metadataWordCount: 1);

    private static RenderInstancePropertyLayout PropertyLayout<T>(
        string contributor,
        RenderInstancePropertyKey key,
        RenderInstancePropertyEncoding encoding)
        where T : unmanaged
    {
        var builder = new RenderInstancePropertyLayoutBuilder();
        builder.Register<T>(contributor, key, encoding);
        return builder.Freeze();
    }
}
