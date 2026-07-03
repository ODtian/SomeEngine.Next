using SomeEngine.ECS.Archetypes;

namespace SomeEngine.ECS.Queries;

internal sealed class QueryRegistry
{
    private readonly List<QueryRecord> _records = new();
    private readonly Dictionary<QueryKey, QueryRecord> _recordsByKey = new();

    public QueryHandle GetOrCreate(QueryDefinition definition, IReadOnlyList<Archetype> archetypes)
    {
        if (_recordsByKey.TryGetValue(definition.Key, out var existing))
            return existing.Handle;

        var handle = new QueryHandle(_records.Count, version: 1);
        var state = QueryState.Create(definition, archetypes);
        var record = new QueryRecord(handle, definition, state);
        _records.Add(record);
        _recordsByKey.Add(definition.Key, record);
        return handle;
    }

    public QueryRecord Get(QueryHandle handle)
    {
        if (!handle.IsValid ||
            (uint)handle.Index >= (uint)_records.Count ||
            _records[handle.Index].Handle.Version != handle.Version)
        {
            throw new InvalidOperationException($"Invalid or stale query handle {handle}.");
        }

        return _records[handle.Index];
    }

    public void OnNewArchetype(Archetype archetype)
    {
        for (int i = 0; i < _records.Count; i++)
            _records[i].State.TryAddArchetype(archetype);
    }
}

