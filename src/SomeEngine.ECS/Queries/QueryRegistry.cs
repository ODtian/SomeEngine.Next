using SomeEngine.ECS.Archetypes;

namespace SomeEngine.ECS.Queries;

internal sealed class QueryRegistry
{
    private readonly Lock _registrationGate = new();
    private readonly Dictionary<QueryKey, QueryRecord> _recordsByKey;
    // The last element is the next reusable slot; cloning preserves this order explicitly.
    private readonly List<int> _freeIndices;
    // Slot identity is immutable within one published array. Only registration and final release
    // copy and publish this table; retaining an interned definition only updates its record count.
    private QueryRecord[] _publishedRecords = Array.Empty<QueryRecord>();

    internal QueryRegistry()
        : this(recordCapacity: 0)
    {
    }

    private QueryRegistry(int recordCapacity)
    {
        _recordsByKey = new Dictionary<QueryKey, QueryRecord>(recordCapacity);
        _freeIndices = new List<int>();
    }

    public QueryHandle GetOrCreate(QueryDefinition definition, ReadOnlySpan<Archetype> archetypes)
        => GetOrCreateRecord(definition, archetypes).Handle;

    internal QueryRecord GetOrCreateRecord(
        QueryDefinition definition,
        ReadOnlySpan<Archetype> archetypes) =>
        GetOrCreateRecord(definition, archetypes, generated: false);

    internal QueryHandle GetOrCreateGenerated(
        QueryDefinition definition,
        ReadOnlySpan<Archetype> archetypes) =>
        GetOrCreateRecord(definition, archetypes, generated: true).Handle;

    private QueryRecord GetOrCreateRecord(
        QueryDefinition definition,
        ReadOnlySpan<Archetype> archetypes,
        bool generated)
    {
        lock (_registrationGate)
        {
            if (_recordsByKey.TryGetValue(definition.Key, out var existing))
            {
                if (generated)
                {
                    if (!existing.HasGeneratedPin)
                        existing.PinGenerated();
                }
                else
                {
                    existing.Retain();
                }
                return existing;
            }

            QueryRecord[] current = Volatile.Read(ref _publishedRecords);
            bool reusesSlot = _freeIndices.Count > 0;
            int index = reusesSlot ? _freeIndices[^1] : current.Length;
            int version = reusesSlot ? current[index].Handle.Version : 1;
            var handle = new QueryHandle(index, version);
            var state = QueryState.Create(definition, archetypes);
            var record = new QueryRecord(
                handle,
                definition,
                state,
                hasGeneratedPin: generated);

            int nextLength = reusesSlot ? current.Length : checked(current.Length + 1);
            var next = new QueryRecord[nextLength];
            Array.Copy(current, next, current.Length);
            next[index] = record;

            _recordsByKey.Add(definition.Key, record);
            if (reusesSlot)
                _freeIndices.RemoveAt(_freeIndices.Count - 1);
            Volatile.Write(ref _publishedRecords, next);
            return record;
        }
    }

    public QueryRecord Get(QueryHandle handle)
    {
        QueryRecord[] records = Volatile.Read(ref _publishedRecords);
        if (!handle.IsValid ||
            (uint)handle.Index >= (uint)records.Length ||
            records[handle.Index].Handle.Version != handle.Version ||
            !records[handle.Index].IsActive)
        {
            throw new InvalidOperationException($"Invalid or stale query handle {handle}.");
        }

        return records[handle.Index];
    }

    public void OnNewArchetype(Archetype archetype)
    {
        QueryRecord[] records = Volatile.Read(ref _publishedRecords);
        for (int i = 0; i < records.Length; i++)
        {
            if (records[i].IsActive)
                records[i].State.TryAddArchetype(archetype);
        }
    }

    internal void Release(QueryHandle handle)
    {
        lock (_registrationGate)
        {
            QueryRecord[] published = Volatile.Read(ref _publishedRecords);
            if (!handle.IsValid ||
                (uint)handle.Index >= (uint)published.Length ||
                published[handle.Index].Handle.Version != handle.Version ||
                !published[handle.Index].IsActive)
            {
                throw new InvalidOperationException($"Invalid or stale query handle {handle}.");
            }

            QueryRecord active = published[handle.Index];
            if (active.AcquisitionCount == 1 &&
                active.HasGeneratedPin)
            {
                throw new InvalidOperationException(
                    "The registry-owned generated query pin cannot be released as a caller acquisition.");
            }
            if (active.AcquisitionCount > 1)
            {
                active.Release();
                return;
            }

            int nextVersion = handle.Version == int.MaxValue ? 1 : handle.Version + 1;
            var released = QueryRecord.Released(new QueryHandle(handle.Index, nextVersion));
            var next = new QueryRecord[published.Length];
            Array.Copy(published, next, published.Length);
            next[handle.Index] = released;

            if (!_recordsByKey.TryGetValue(active.Definition.Key, out QueryRecord? registered) ||
                !ReferenceEquals(registered, active))
            {
                throw new InvalidOperationException("Active query definition was not registered.");
            }

            _freeIndices.EnsureCapacity(checked(_freeIndices.Count + 1));
            _ = active.Release();
            _recordsByKey.Remove(active.Definition.Key);
            _freeIndices.Add(handle.Index);
            Volatile.Write(ref _publishedRecords, next);
        }
    }

    internal QueryRegistry CloneExact(ReadOnlySpan<Archetype> archetypes)
    {
        QueryRecord[] records = Volatile.Read(ref _publishedRecords);
        var clone = new QueryRegistry(records.Length);
        var clonedRecords = new QueryRecord[records.Length];
        for (int i = 0; i < records.Length; i++)
        {
            var source = records[i];
            if (!source.IsActive)
            {
                clonedRecords[i] = QueryRecord.Released(source.Handle);
                continue;
            }

            var record = new QueryRecord(
                source.Handle,
                source.Definition,
                QueryState.Create(source.Definition, archetypes),
                source.AcquisitionCount,
                source.HasGeneratedPin);
            clonedRecords[i] = record;
            clone._recordsByKey.Add(record.Definition.Key, record);
        }
        CopyFreeIndicesTo(clone);
        clone._publishedRecords = clonedRecords;

        return clone;
    }

    /// <summary>
    /// Clones the compiled query registry by exact source-to-candidate archetype identity mapping.
    /// Work is proportional to existing matches rather than query-count times archetype-count.
    /// </summary>
    internal QueryRegistry CloneExact(DetachedTableMap tableMap, out int clonedMatchCount)
    {
        ArgumentNullException.ThrowIfNull(tableMap);

        QueryRecord[] records = Volatile.Read(ref _publishedRecords);
        var clone = new QueryRegistry(records.Length);
        var clonedRecords = new QueryRecord[records.Length];
        clonedMatchCount = 0;
        for (int i = 0; i < records.Length; i++)
        {
            QueryRecord source = records[i];
            if (!source.IsActive)
            {
                clonedRecords[i] = QueryRecord.Released(source.Handle);
                continue;
            }

            QueryState state = source.State.CloneExact(tableMap);
            var record = new QueryRecord(
                source.Handle,
                source.Definition,
                state,
                source.AcquisitionCount,
                source.HasGeneratedPin);
            clonedRecords[i] = record;
            clone._recordsByKey.Add(record.Definition.Key, record);
            clonedMatchCount = checked(clonedMatchCount + state.Matches.Length);
        }

        CopyFreeIndicesTo(clone);
        clone._publishedRecords = clonedRecords;
        return clone;
    }

    private void CopyFreeIndicesTo(QueryRegistry clone)
    {
        clone._freeIndices.EnsureCapacity(_freeIndices.Count);
        for (int i = 0; i < _freeIndices.Count; i++)
            clone._freeIndices.Add(_freeIndices[i]);
    }

}

