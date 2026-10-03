using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;

namespace SomeEngine.Animation;

public delegate ref TValue FieldRef<TComponent, TValue>(ref TComponent component);

/// <summary>A component field located by its component number and byte offset.</summary>
public readonly struct Channel<T> : IEquatable<Channel<T>> where T : unmanaged
{
    public readonly ushort Component;
    public readonly ushort Offset;

    private Channel(ushort component, ushort offset) => (Component, Offset) = (component, offset);

    /// <summary>Computes a field location once; callers can keep the result in a static readonly field.</summary>
    public static Channel<T> Of<TComponent>(FieldRef<TComponent, T> field)
        where TComponent : unmanaged, IComponent
    {
        TComponent component = default;
        ref T value = ref field(ref component);
        nint offset = Unsafe.ByteOffset(ref Unsafe.As<TComponent, byte>(ref component), ref Unsafe.As<T, byte>(ref value));
        DevelopmentCheck.Require(offset >= 0 && offset <= ushort.MaxValue && offset <= Unsafe.SizeOf<TComponent>() - Unsafe.SizeOf<T>(),
            "A channel field must fit inside its component and its ushort byte offset.");
        return new Channel<T>(ComponentId<TComponent>.Value, (ushort)offset);
    }

    public bool Equals(Channel<T> other) => Component == other.Component && Offset == other.Offset;
    public override bool Equals(object? obj) => obj is Channel<T> other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Component, Offset);
    public static bool operator ==(Channel<T> left, Channel<T> right) => left.Equals(right);
    public static bool operator !=(Channel<T> left, Channel<T> right) => !left.Equals(right);
}
