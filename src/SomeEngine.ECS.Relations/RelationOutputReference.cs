using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Relations;

/// <summary>Shares the caller's output storage through synchronous relation callbacks.</summary>
internal readonly ref struct RelationOutputReference<TOutput>
    where TOutput : allows ref struct
{
    private readonly ref byte _value;

    internal RelationOutputReference(ref TOutput output) =>
        _value = ref Unsafe.As<TOutput, byte>(ref output);

    // The byte reference preserves the original managed address and is restored to the same type.
    internal ref TOutput Value => ref Unsafe.As<byte, TOutput>(ref _value);
}
