using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Indexing;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Owners;

internal sealed class Clock
{
    private int _tick = 1;

    internal uint Tick => unchecked((uint)Volatile.Read(ref _tick));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint Acquire()
    {
        return unchecked((uint)(Interlocked.Increment(ref _tick) - 1));
    }

    internal void Write(uint tick)
    {
        Volatile.Write(ref _tick, unchecked((int)tick));
    }
}


