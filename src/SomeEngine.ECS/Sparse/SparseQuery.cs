using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

internal readonly record struct SparseCondition(SparseStore Store, byte Changes = 0, ulong Since = 0)
{
    internal bool Has(int id) => Changes == 0 ? Store.Has(id) : Store.HasChanged(id, Since, Changes == 2);
}

public sealed class SparseQuery
{
    private readonly World _world;
    private readonly Query _core;
    private SparseCondition[] _stores;
    private int[] _tables = [];
    private int _tableCount = -1, _queryVersion = -1;

    internal SparseQuery(World world, QueryDescription description, SparseStore first)
        => (_world, _core, _stores) = (world, world.Query(description), [new(first)]);

    public SparseQuery With<T>() where T : struct
    {
        _world.RequireStructure();
        SparseStore<T> store = _world.Sparse<T>();
        if (!_stores.Any(condition => ReferenceEquals(condition.Store, store)))
        {
            int count = _stores.Length;
            Array.Resize(ref _stores, count + 1);
            _stores[count] = new(store);
        }
        return this;
    }

    public SparseQuery WithChanged<T>(ulong since) where T : struct, ITrackedComponent
        => SetChanges<T>(since, 1);

    public SparseQuery WithAdded<T>(ulong since) where T : struct, IAddedComponent
        => SetChanges<T>(since, 2);

    private SparseQuery SetChanges<T>(ulong since, byte mode) where T : struct
    {
        With<T>();
        SparseStore<T> store = _world.Sparse<T>();
        for (int i = 0; i < _stores.Length; i++)
            if (ReferenceEquals(_stores[i].Store, store)) _stores[i] = new(store, mode, since);
        return this;
    }

    public SparseQueryScope Open()
    {
        _world.BeginRead();
        QueryMatch[] matches = _core.GetMatches(out int count);
        if (_tableCount != _world.ArchetypeCount || _queryVersion != _world.QueryVersion)
        {
            Array.Resize(ref _tables, _world.ArchetypeCount);
            Array.Clear(_tables);
            for (int i = 0; i < count; i++) _tables[matches[i].Archetype.Id] = i + 1;
            _tableCount = _world.ArchetypeCount;
            _queryVersion = _world.QueryVersion;
        }
        int candidates = 0;
        for (int i = 0; i < count; i++)
            foreach (Chunk chunk in matches[i].Archetype.AllChunks) candidates += chunk.Count;
        int driver = -1;
        for (int i = 0; i < _stores.Length; i++)
            if (_stores[i].Store.Count <= candidates) { candidates = _stores[i].Store.Count; driver = i; }
        return new(_world, _stores, driver, _tables, matches, count, _core.SinceVersion);
    }
}

public ref struct SparseQueryScope
{
    private readonly World _world;
    private readonly SparseCondition[] _stores;
    private readonly int _driver;
    private readonly int[] _tables;
    private readonly QueryMatch[] _matches;
    private readonly int _count;
    private readonly ulong _since;

    internal SparseQueryScope(World world, SparseCondition[] stores, int driver, int[] tables, QueryMatch[] matches, int count, ulong since)
        => (_world, _stores, _driver, _tables, _matches, _count, _since) = (world, stores, driver, tables, matches, count, since);

    public SparseEntityEnumerator GetEnumerator() => new(_world, _stores, _driver, _tables, _matches, _count, _since);
    public void Dispose() => _world.EndRead();
}

public struct SparseEntityEnumerator
{
    private readonly EntityRecord[] _records;
    private readonly SparseCondition[] _stores;
    private readonly int _driver;
    private readonly int[] _tables;
    private readonly QueryMatch[] _matches;
    private readonly ulong _since;
    private ChunkEnumerator _chunks;
    private QueryChunk _chunk;
    private int _row;
    private Entity _current;

    internal SparseEntityEnumerator(World world, SparseCondition[] stores, int driver, int[] tables, QueryMatch[] matches, int count, ulong since)
    {
        _records = world.Records;
        _stores = stores;
        _driver = driver;
        _tables = tables;
        _matches = matches;
        _since = since;
        _chunks = new(matches, count, world.HasMultipleChunks, since, write: false);
        _chunk = default;
        _row = 0;
        _current = default;
    }

    public Entity Current => _current;

    public bool MoveNext()
    {
        if (_driver >= 0)
        {
            SparseCondition condition = _stores[_driver];
            SparseStore driver = condition.Store;
            while (_row < driver.Count)
            {
                Entity entity = driver.Entities[_row++];
                if (condition.Changes != 0 && !condition.Has(entity.Id)) continue;
                ref EntityRecord record = ref _records[entity.Id];
                Chunk chunk = record.Chunk!;
                int match = _tables[chunk.Archetype.Id] - 1;
                if (match < 0 || (_matches[match].Filter is { } filter && !filter.Contains(chunk, record.Row, _since))) continue;
                if (!HasOthers(entity.Id)) continue;
                _current = entity;
                return true;
            }
            return false;
        }
        while (true)
        {
            if (_row == _chunk.Count)
            {
                if (!_chunks.MoveNext()) return false;
                _chunk = _chunks.Current;
                _row = 0;
            }
            Entity entity = _chunk.Entities[_row++];
            if (!HasOthers(entity.Id)) continue;
            _current = entity;
            return true;
        }
    }

    private readonly bool HasOthers(int id)
    {
        for (int i = 0; i < _stores.Length; i++)
            if (i != _driver && !_stores[i].Has(id)) return false;
        return true;
    }
}

public sealed partial class World
{
    public SparseQuery QuerySparse<T>(QueryDescription? core = null) where T : struct
        => new(this, core ?? new QueryDescription(), Sparse<T>());
}
