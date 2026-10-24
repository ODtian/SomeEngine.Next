using System.Runtime.CompilerServices;

namespace SomeEngine.ECS;

/// <summary>A typed field column borrowed from the current component data.</summary>
public readonly ref struct FieldReadView<TComponent, TValue, TAccess>
    where TComponent : struct, IComponent
    where TValue : unmanaged
    where TAccess : struct, IFieldAccess<TComponent, TValue>
{
    private readonly ReadOnlySpan<TComponent> _components;

    internal FieldReadView(ReadOnlySpan<TComponent> components) => _components = components;

    public int Count => _components.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue Read(int row) => TAccess.Read(in _components[row]);
}
