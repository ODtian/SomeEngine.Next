using SomeEngine.ECS.Systems;

namespace SomeEngine.Render.Instances;

/// <summary>
/// Declares and fills one bundle of instance properties. A producer receives only a write
/// capability for <see cref="Properties"/>; allocation, publication, retirement, and fields
/// declared by other producers remain owned by the instance-storage system. Producers are
/// immutable value descriptions: packet execution may copy them to several workers concurrently.
/// </summary>
public interface IRenderInstanceProducer
{
    RenderInstancePropertyLayout Properties { get; }

    /// <summary>Reports value invalidation for the ECS columns owned by this producer.</summary>
    RenderInstanceChanges GetChanges(
        ReadOnlyQueryPacket packet,
        uint lastSystemVersion);

    /// <summary>Binds this producer's shared/per-instance metadata once for the batch.</summary>
    void Bind(RenderInstanceWriteSlice destination);

    /// <summary>
    /// Writes the rows represented by <paramref name="chunk"/> into the matching destination
    /// range. The slice already carries that range; producers never calculate a storage row.
    /// </summary>
    void Write(RenderInstanceWriteSlice destination, ReadOnlyQueryPacket packet);
}

/// <summary>
/// Neutral producer used when a pipeline's own producer already covers the exact shader layout.
/// It declares and writes no properties; it is a value type and carries no retained state.
/// </summary>
public readonly struct EmptyRenderInstanceProducer : IRenderInstanceProducer
{
    private static RenderInstancePropertyLayout EmptyProperties { get; } =
        new RenderInstancePropertyLayoutBuilder().Freeze();

    public RenderInstancePropertyLayout Properties => EmptyProperties;

    public RenderInstanceChanges GetChanges(
        ReadOnlyQueryPacket packet,
        uint lastSystemVersion) => RenderInstanceChanges.None;

    public void Bind(RenderInstanceWriteSlice destination)
    {
    }

    public void Write(RenderInstanceWriteSlice destination, ReadOnlyQueryPacket packet)
    {
    }
}

/// <summary>
/// Generic one-component producer. One ECS component remains one column; composing several of
/// these producers with <see cref="RenderInstanceProducerBundle{TFirst,TSecond}"/> produces SoA
/// without reflecting or splitting the fields inside a component.
/// </summary>
public readonly struct RenderInstanceComponentProducer<T> : IRenderInstanceProducer
    where T : unmanaged
{
    private readonly ResolvedRenderInstanceProperty<T> _property;

    public RenderInstanceComponentProducer(
        string contributor,
        RenderInstancePropertyKey key,
        RenderInstancePropertyEncoding encoding)
    {
        var builder = new RenderInstancePropertyLayoutBuilder();
        RenderInstanceProperty<T> property = builder.Register<T>(
            contributor,
            key,
            encoding);
        Properties = builder.Freeze();
        _property = Properties.Resolve(property);
    }

    public RenderInstancePropertyLayout Properties { get; }

    public RenderInstanceChanges GetChanges(
        ReadOnlyQueryPacket packet,
        uint lastSystemVersion) =>
        packet.ChangedSince<T>(lastSystemVersion)
            ? RenderInstanceChanges.Values
            : RenderInstanceChanges.None;

    public void Bind(RenderInstanceWriteSlice destination) =>
        destination.BindPerInstance(_property);

    public void Write(RenderInstanceWriteSlice destination, ReadOnlyQueryPacket packet) =>
        destination.Write(_property, packet.Read<T>());
}

/// <summary>
/// Zero-business-logic composition of two producer bundles. More components are represented by
/// nesting bundles; each child receives only its own property subset while the composed layout
/// remains the exact shader ABI.
/// </summary>
public readonly struct RenderInstanceProducerBundle<TFirst, TSecond> : IRenderInstanceProducer
    where TFirst : struct, IRenderInstanceProducer
    where TSecond : struct, IRenderInstanceProducer
{
    private readonly TFirst _first;
    private readonly TSecond _second;

    public RenderInstanceProducerBundle(TFirst first, TSecond second)
        : this(Compose(first, second), first, second)
    {
    }

    /// <summary>
    /// Uses an already linked pipeline/material/pass layout. This is the steady-state path: the
    /// exact layout is validated without rebuilding it on every batch update.
    /// </summary>
    public RenderInstanceProducerBundle(
        RenderInstancePropertyLayout exactLayout,
        TFirst first,
        TSecond second)
    {
        ArgumentNullException.ThrowIfNull(exactLayout);
        ValidateExactLayout(exactLayout, first.Properties, second.Properties);
        _first = first;
        _second = second;
        Properties = exactLayout;
    }

    public RenderInstancePropertyLayout Properties { get; }

    public TFirst First => _first;

    public TSecond Second => _second;

    public RenderInstanceChanges GetChanges(
        ReadOnlyQueryPacket packet,
        uint lastSystemVersion) =>
        _first.GetChanges(packet, lastSystemVersion)
        | _second.GetChanges(packet, lastSystemVersion);

    public void Bind(RenderInstanceWriteSlice destination)
    {
        _first.Bind(destination.Restrict(_first.Properties));
        _second.Bind(destination.Restrict(_second.Properties));
    }

    public void Write(RenderInstanceWriteSlice destination, ReadOnlyQueryPacket packet)
    {
        _first.Write(destination.Restrict(_first.Properties), packet);
        _second.Write(destination.Restrict(_second.Properties), packet);
    }

    private static void ValidateExactLayout(
        RenderInstancePropertyLayout exact,
        RenderInstancePropertyLayout first,
        RenderInstancePropertyLayout second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        foreach (RenderInstancePropertyDescriptor property in first.Properties)
            _ = exact.RequireCompatible(property, nameof(exact));
        foreach (RenderInstancePropertyDescriptor property in second.Properties)
        {
            if (first.Contains(property.Key))
            {
                throw new ArgumentException(
                    $"More than one producer owns render-instance property '{property.Key}'.",
                    nameof(second));
            }
            _ = exact.RequireCompatible(property, nameof(exact));
        }
        if (exact.Properties.Count != first.Properties.Count + second.Properties.Count)
        {
            throw new ArgumentException(
                "The exact layout contains a property that neither producer owns.",
                nameof(exact));
        }
    }

    private static RenderInstancePropertyLayout Compose(TFirst first, TSecond second)
    {
        return RenderInstancePropertyLayout.Compose(first.Properties, second.Properties);
    }
}
