using System.Runtime.CompilerServices;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Components;

public static class BufferComponents
{
    public static int Header<T>() where T : struct, IBufferElement
    {
        BufferRegistry.Register<T>();
        return ComponentMetadata<DynamicBufferHeader<T>>.Id;
    }

    public static int Inline<T>() where T : struct, IBufferElement
    {
        BufferRegistry.Register<T>();
        return ComponentMetadata<DynamicBufferInline<T>>.Id;
    }
}

internal static class DynamicBufferConstants
{
    public const int MaxInlineCapacity = 8;
}

internal static class DynamicBufferLayout<T>
    where T : struct, IBufferElement
{
    public static readonly int InlineCapacity = ResolveInlineCapacity();

    private static int ResolveInlineCapacity()
    {
        var attr = (BufferCapacityAttribute?)Attribute.GetCustomAttribute(
            typeof(T),
            typeof(BufferCapacityAttribute));
        int capacity = attr?.Capacity ?? DynamicBufferConstants.MaxInlineCapacity;
        if (capacity < 0)
            throw new InvalidOperationException(
                $"Internal buffer capacity for {typeof(T).Name} cannot be negative.");
        if (capacity > DynamicBufferConstants.MaxInlineCapacity)
            throw new InvalidOperationException(
                $"{typeof(T).Name} requests inline buffer capacity {capacity}, " +
                $"but the current generic inline storage supports at most {DynamicBufferConstants.MaxInlineCapacity}.");
        return capacity;
    }
}

internal interface IBufferStorageComponent;

internal struct DynamicBufferHeader<T> : global::SomeEngine.ECS.IComponent, IBufferStorageComponent
    where T : struct, IBufferElement
{
    private T[]? _overflow;

    internal int Count;
    internal int InlineCapacity;

    internal bool HasOverflow => _overflow is not null;

    internal int OverflowCapacity => _overflow?.Length ?? 0;

    internal ReadOnlySpan<T> OverflowReadSpan => _overflow;

    internal Span<T> OverflowWriteSpan => _overflow;

    internal object? OverflowBackingIdentity => _overflow;

    internal void SetOwnedOverflow(T[]? ownedOverflow, long ownerIdentity)
    {
        _overflow = ownedOverflow;
        OverflowOwnerIdentity = ownedOverflow is null ? 0 : ownerIdentity;
    }

    // An overflow array is mutable only through the Chunk whose unique ownership identity is
    // recorded here. Forking or structurally copying a header intentionally preserves this token:
    // the destination can read the same immutable backing, but its first content write must
    // detach the single row before mutation. The token is deliberately not a reference count;
    // conservative extra detaches are safe, while a stale token can never grant a new Chunk write
    // ownership.
    internal long OverflowOwnerIdentity;

    public static DynamicBufferHeader<T> Create()
    {
        return new DynamicBufferHeader<T>
        {
            InlineCapacity = DynamicBufferLayout<T>.InlineCapacity,
        };
    }
}

[InlineArray(DynamicBufferConstants.MaxInlineCapacity)]
internal struct DynamicBufferInline<T> : global::SomeEngine.ECS.IComponent, IBufferStorageComponent
    where T : struct, IBufferElement
{
    private T _element0;
}

