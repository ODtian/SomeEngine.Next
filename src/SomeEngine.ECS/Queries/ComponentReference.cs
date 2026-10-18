using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Queries;

/// <summary>A read-only component reference borrowed during a World query callback.</summary>
public readonly ref struct ComponentReference<T> where T : struct
{
    private readonly ref readonly T _component;

    internal ComponentReference(ref readonly T component) => _component = ref component;

    public ref readonly T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _component;
    }
}
