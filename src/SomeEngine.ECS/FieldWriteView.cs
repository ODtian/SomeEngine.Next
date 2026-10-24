using System.Runtime.CompilerServices;

namespace SomeEngine.ECS;

/// <summary>A writable typed field column borrowed for the current query callback.</summary>
public readonly ref struct FieldWriteView<TComponent, TValue, TAccess>
    where TComponent : struct, IComponent
    where TValue : unmanaged
    where TAccess : struct, IFieldAccess<TComponent, TValue>
{
    private readonly Span<TComponent> _components;

    internal FieldWriteView(Span<TComponent> components) => _components = components;

    public int Count => _components.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(int row, in TValue value) => TAccess.Write(ref _components[row], in value);
}
