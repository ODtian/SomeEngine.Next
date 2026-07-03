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

internal sealed class Iteration
{
    private int _depth;

    internal bool Active => _depth > 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Begin()
    {
        _depth++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void End()
    {
        _depth--;
    }

    internal void Throw()
    {
        if (_depth > 0)
            throw new InvalidOperationException(
                "Cannot perform structural changes during iteration. Use CommandBuffer.");
    }
}


