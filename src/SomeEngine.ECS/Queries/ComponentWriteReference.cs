using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Queries;

/// <summary>A writable component reference borrowed during a World query callback.</summary>
public readonly ref struct ComponentWriteReference<T> where T : struct
{
    private readonly ref T _component;

    internal ComponentWriteReference(ref T component) => _component = ref component;

    public ref T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _component;
    }
}
