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

internal sealed class Queries
{
    internal QueryRegistry Registry { get; } = new();
    private QueryHandle _stateKey;
    private QueryState? _state;
    private QueryHandle _accessKey;
    private ReadWriteMatches? _access;
    private int _accessWrite;
    private int _accessRead;

    internal QueryHandle Query(QueryDefinition definition, IReadOnlyList<Archetype> archetypes)
    {
        return Registry.GetOrCreate(definition, archetypes);
    }

    internal QueryDefinition Definition(QueryHandle query)
    {
        return Registry.Get(query).Definition;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal QueryState State(QueryHandle query)
    {
        if (_state is not null &&
            _stateKey.Index == query.Index &&
            _stateKey.Version == query.Version)
        {
            return _state;
        }

        var state = Registry.Get(query).State;
        _stateKey = query;
        _state = state;
        return state;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadWriteMatches Access<TWrite, TRead>(
        QueryHandle query,
        int writeComponentId,
        int readComponentId)
        where TWrite : struct, IComponent
        where TRead : struct, IComponent
    {
        if (_access is not null &&
            _accessKey.Index == query.Index &&
            _accessKey.Version == query.Version &&
            _accessWrite == writeComponentId &&
            _accessRead == readComponentId)
        {
            return _access;
        }

        var matches = State(query).AccessMatches<TWrite, TRead>(
            writeComponentId,
            readComponentId);
        _accessKey = query;
        _accessWrite = writeComponentId;
        _accessRead = readComponentId;
        _access = matches;
        return matches;
    }

    internal void OnArchetype(Archetype archetype)
    {
        Registry.OnNewArchetype(archetype);
        _access = null;
    }

    internal void Reset()
    {
        _state = null;
        _access = null;
    }
}


