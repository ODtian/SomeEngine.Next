using SomeEngine.Render.Components;

namespace SomeEngine.Render.Instances;

/// <summary>
/// Built-in spatial properties contributed by Render core. The tokens are independent of a final
/// pipeline layout and may be resolved against any compatible composition.
/// </summary>
public static class RenderInstanceTransformProperties
{
    public static RenderInstancePropertyKey CurrentTransformKey { get; } =
        new("someengine.render.current_transform");

    public static RenderInstancePropertyKey PreviousTransformKey { get; } =
        new("someengine.render.previous_transform");

    private static readonly TransformProperties s_properties = CreateTransformProperties();

    public static RenderInstanceProperty<RenderTransform> CurrentTransform =>
        s_properties.Current;

    public static RenderInstanceProperty<RenderPreviousTransform> PreviousTransform =>
        s_properties.Previous;

    public static RenderInstancePropertyLayout Layout => s_properties.Layout;

    public static void Register(RenderInstancePropertyLayoutBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Include(Layout);
    }

    private static TransformProperties CreateTransformProperties()
    {
        var transformEncoding = new RenderInstancePropertyEncoding(
            "someengine.render.transform_qvvs48.v1",
            RenderTransform.SizeInBytes,
            storageAlignment: 16,
            storageStride: 48,
            metadataWordCount: 1);
        var builder = new RenderInstancePropertyLayoutBuilder();
        RenderInstanceProperty<RenderTransform> current = builder.Register<RenderTransform>(
            "SomeEngine.Render",
            CurrentTransformKey,
            transformEncoding);
        RenderInstanceProperty<RenderPreviousTransform> previous =
            builder.Register<RenderPreviousTransform>(
                "SomeEngine.Render",
                PreviousTransformKey,
                transformEncoding);
        return new TransformProperties(builder.Freeze(), current, previous);
    }

    private readonly record struct TransformProperties(
        RenderInstancePropertyLayout Layout,
        RenderInstanceProperty<RenderTransform> Current,
        RenderInstanceProperty<RenderPreviousTransform> Previous);
}
