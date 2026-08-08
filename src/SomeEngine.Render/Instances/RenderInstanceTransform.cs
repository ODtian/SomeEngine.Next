using SomeEngine.Render.Components;

namespace SomeEngine.Render.Instances;

/// <summary>Built-in properties contributed by the Render core to every composed instance layout.</summary>
public static class RenderInstanceTransformProperties
{
    public static RenderInstancePropertyKey CurrentTransformKey { get; } =
        new("someengine.render.current_transform");

    public static RenderInstancePropertyKey PreviousTransformKey { get; } =
        new("someengine.render.previous_transform");

    private static RenderInstancePropertyEncoding TransformEncoding { get; } = new(
        "someengine.render.transform_qvvs48.v1",
        RenderTransform.SizeInBytes,
        storageAlignment: 16,
        storageStride: 48,
        metadataWordCount: 1);

    public static void Register(RenderInstancePropertyLayoutBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Register<RenderTransform>(
            "SomeEngine.Render",
            CurrentTransformKey,
            TransformEncoding);
        builder.Register<RenderPreviousTransform>(
            "SomeEngine.Render",
            PreviousTransformKey,
            TransformEncoding);
    }
}
