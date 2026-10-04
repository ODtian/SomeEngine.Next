using System.Runtime.CompilerServices;
using SomeEngine.Animation;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

public static partial class Scope
{
    internal static void Animate<T>(Entity entity, Channel<T> channel, Spec spec) where T : unmanaged
    {
        ScopeData current = Current;
        var copies = (current.Calculation ?? current).ComponentCopies;
        bool hasCopy = copies is not null && copies.TryGetValue((entity, channel.Component), out _);
        T value = hasCopy
            ? Unsafe.ReadUnaligned<T>(ref Unsafe.Add(ref copies![(entity, channel.Component)].Bytes, channel.Offset))
            : Unsafe.ReadUnaligned<T>(in current.World.ReadBytes(entity, channel.Component, channel.Offset, Unsafe.SizeOf<T>()));
        AnimatedFields.Declare(current.World, entity, channel, spec, value, hasValue: !hasCopy);
    }
}
