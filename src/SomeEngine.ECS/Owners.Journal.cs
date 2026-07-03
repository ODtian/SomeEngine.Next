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

internal sealed class Journal
{
    internal readonly List<SerializationChangeEvent> Events = new();
    private int _depth;

    internal bool Suppressed
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _depth != 0;
    }

    internal void Write(
        SerializationChangeKind kind,
        Entity entity,
        int componentId,
        Entity target,
        uint tick)
    {
        if (_depth != 0)
            return;

        if (Events.Capacity == 0)
            Events.Capacity = 64;

        Events.Add(new SerializationChangeEvent(kind, entity, componentId, target, tick));
    }

    internal SerializationScope Suppress()
    {
        _depth++;
        return new SerializationScope(this);
    }

    internal void Resume()
    {
        if (_depth <= 0)
            throw new InvalidOperationException("Serialization change journal suppression is not active.");

        _depth--;
    }

    internal void Clear()
    {
        Events.Clear();
    }
}

internal readonly struct SerializationScope : IDisposable
{
    private readonly Journal? _journal;

    internal SerializationScope(Journal journal)
    {
        _journal = journal;
    }

    public void Dispose()
    {
        _journal?.Resume();
    }
}


