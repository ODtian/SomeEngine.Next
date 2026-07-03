using System.Runtime.CompilerServices;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Components;

public readonly struct BufferValues<T>
    where T : struct, IBufferElement
{
    private readonly ReadOnlyMemory<T> _items;

    public BufferValues(params T[] items)
    {
        _items = items;
    }

    public BufferValues(ReadOnlyMemory<T> items)
    {
        _items = items;
    }

    public ReadOnlySpan<T> AsSpan() => _items.Span;
}

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

internal struct DynamicBufferHeader<T> : IComponent
    where T : struct, IBufferElement
{
    public int Count;
    public int InlineCapacity;
    public T[]? Overflow;

    public static DynamicBufferHeader<T> Create()
    {
        return new DynamicBufferHeader<T>
        {
            InlineCapacity = DynamicBufferLayout<T>.InlineCapacity,
        };
    }
}

[InlineArray(DynamicBufferConstants.MaxInlineCapacity)]
internal struct BufferInlineStorage<T>
    where T : struct, IBufferElement
{
    private T _element0;
}

internal struct DynamicBufferInline<T> : IComponent
    where T : struct, IBufferElement
{
    public BufferInlineStorage<T> Elements;
}

