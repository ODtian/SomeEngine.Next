using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Systems;

/// <summary>Query declarations and actual participating entities for each independent work item.</summary>
public interface IEntityQueryWork
{
    int Count { get; }
    int QueryCount { get; }
    QueryHandle GetQuery(int workIndex, int queryIndex);
    ReadOnlySpan<Entity> GetEntities(int workIndex, int queryIndex);
}

public interface IEntityQueryJob
{
    void Execute(int index, scoped EntityQueryViews queries);
}

/// <summary>Views borrowed only for the currently admitted work item's query entities.</summary>
public readonly ref struct EntityQueryViews
{
    private readonly ReadOnlySpan<EntityQueryData> _queries;
    internal EntityQueryViews(ReadOnlySpan<EntityQueryData> queries) => _queries = queries;
    public int Count => _queries.Length;
    public EntityQuery Get(int index) => new(_queries[index]);
}

/// <summary>Original query rows restricted to this work item's actual entity set.</summary>
public readonly ref struct EntityQuery
{
    private readonly EntityQueryData _query;
    internal EntityQuery(EntityQueryData query) => _query = query;
    public ReadOnlySpan<Entity> Entities => _query.Entities.AsSpan(0, _query.Count);
    public uint CurrentSystemVersion => _query.Data.CurrentSystemVersion;
    public EntityQueryRows Rows => new(this);

    public bool TryGetRow(Entity entity, out QueryRow row)
    {
        if (_query.Members.Contains(entity))
            return _query.Data.World.TryGetEntityQueryRow(_query.Data.State, entity,
                _query.Data.LastSystemVersion, _query.Data.CurrentSystemVersion, out row);
        row = default;
        return false;
    }

    public bool TryReadRef<T>(Entity entity, out ComponentReference<T> component) where T : struct
    {
        component = _query.Members.Contains(entity)
            ? World.ReadEntityQueryRef<T>(in _query.Data, entity) : default;
        return component.HasValue;
    }
}

public ref struct EntityQueryRows
{
    private readonly EntityQuery _query;
    private int _index;
    private QueryRow _current;
    internal EntityQueryRows(EntityQuery query) { _query = query; _index = -1; _current = default; }
    public readonly EntityQueryRows GetEnumerator() => this;
    public readonly QueryRow Current => _current;
    public bool MoveNext()
    {
        while (++_index < _query.Entities.Length)
            if (_query.TryGetRow(_query.Entities[_index], out _current)) return true;
        return false;
    }
}

internal sealed class EntityQueryData
{
    internal QueryData Data;
    internal QueryDefinition Definition = QueryDefinition.Empty;
    internal Entity[] Entities = [];
    internal int Count;
    internal readonly HashSet<Entity> Members = [];

    internal void Set(World world, QueryHandle query, ReadOnlySpan<Entity> entities)
    {
        var current = world.ActiveStructureRoot.Queries.Get(query);
        Definition = current.Definition;
        Data = new(world, current.State, 0, 0);
        if (Entities.Length < entities.Length) Array.Resize(ref Entities, Math.Max(entities.Length, Entities.Length * 2));
        entities.CopyTo(Entities);
        Count = entities.Length;
        Members.Clear();
        foreach (Entity entity in entities) Members.Add(entity);
    }
}
