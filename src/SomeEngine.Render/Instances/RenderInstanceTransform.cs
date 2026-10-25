using SomeEngine.ECS.Systems;
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

    private static readonly Contracts s_contracts = BuildContracts();

    public static RenderInstanceProperty<RenderTransform> CurrentTransform =>
        s_contracts.Current;

    public static RenderInstanceProperty<RenderPreviousTransform> PreviousTransform =>
        s_contracts.Previous;

    public static RenderInstancePropertyLayout Layout => s_contracts.Layout;

    public static void Register(RenderInstancePropertyLayoutBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Include(Layout);
    }

    private static Contracts BuildContracts()
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
        return new Contracts(builder.Freeze(), current, previous);
    }

    private readonly record struct Contracts(
        RenderInstancePropertyLayout Layout,
        RenderInstanceProperty<RenderTransform> Current,
        RenderInstanceProperty<RenderPreviousTransform> Previous);
}

/// <summary>
/// Writes RenderWorld's current and previous transform columns into the shared instance store.
/// It owns no entities, storage, temporal history, or resource lifetime.
/// </summary>
public readonly struct RenderInstanceTransformProducer : IRenderInstanceProducer
{
    private static readonly ResolvedRenderInstanceProperty<RenderTransform> Current =
        RenderInstanceTransformProperties.Layout.Resolve(RenderInstanceTransformProperties.CurrentTransform);
    private static readonly ResolvedRenderInstanceProperty<RenderPreviousTransform> Previous =
        RenderInstanceTransformProperties.Layout.Resolve(RenderInstanceTransformProperties.PreviousTransform);

    public RenderInstancePropertyLayout Properties => RenderInstanceTransformProperties.Layout;

    public RenderInstanceChanges GetChanges(ReadOnlyQueryPacket packet, uint lastSystemVersion) =>
        packet.ChangedSince<RenderTransform>(lastSystemVersion)
        || packet.ChangedSince<RenderPreviousTransform>(lastSystemVersion)
            ? RenderInstanceChanges.Values
            : RenderInstanceChanges.None;

    public void Bind(RenderInstanceWriteSlice destination) => Bind(destination, true, true);

    internal void Bind(RenderInstanceWriteSlice destination, bool current, bool previous)
    {
        if (current) destination.BindPerInstance(Current);
        if (previous) destination.BindPerInstance(Previous);
    }

    public void Write(RenderInstanceWriteSlice destination, ReadOnlyQueryPacket packet)
    {
        destination.Write(Current, packet.Read<RenderTransform>());
        destination.Write(Previous, packet.Read<RenderPreviousTransform>());
    }

    public void WriteChanged(RenderInstanceWriteSlice destination, ReadOnlyQueryPacket packet,
        uint lastSystemVersion) => WriteChanged(destination, packet, lastSystemVersion, false);

    internal void WriteChanged(RenderInstanceWriteSlice destination, ReadOnlyQueryPacket packet,
        uint lastSystemVersion, bool allTransformsChanged)
    {
        if (allTransformsChanged)
        {
            Write(destination, packet);
            return;
        }
        RenderInstanceColumnWrites.WriteChanged(destination, packet, Current, lastSystemVersion, useWholeColumnFastPath: true);
        RenderInstanceColumnWrites.WriteChanged(destination, packet, Previous, lastSystemVersion, useWholeColumnFastPath: true);
    }
}
