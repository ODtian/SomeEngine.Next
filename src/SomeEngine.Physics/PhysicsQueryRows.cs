using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Physics;

/// <summary>Borrowed original rows from a whole query or an admitted entity range.</summary>
internal readonly ref struct PhysicsQueryRows
{
    private readonly QueryCursor _query;
    private readonly EntityQuery _entities;
    private readonly EntityQuery _references;
    private readonly bool _restricted;
    private readonly bool _hasReferences;

    internal PhysicsQueryRows(QueryCursor query)
    { _query = query; _entities = _references = default; _restricted = _hasReferences = false; }
    internal PhysicsQueryRows(EntityQuery entities)
    { _query = default; _entities = entities; _references = default; _restricted = true; _hasReferences = false; }
    internal PhysicsQueryRows(EntityQuery entities, EntityQuery references)
    { _query = default; _entities = entities; _references = references; _restricted = _hasReferences = true; }
    internal RowsEnumerator Rows => _restricted ? new(_entities) : new(_query);
    internal bool TryGetRow(Entity entity, out QueryRow row)
    {
        if (!_restricted) return _query.TryGetRow(entity, out row);
        if (_entities.TryGetRow(entity, out row)) return true;
        return _hasReferences && _references.TryGetRow(entity, out row);
    }

    internal ref struct RowsEnumerator
    {
        private QueryRowEnumerator<NoSharedFilter> _query;
        private EntityQueryRows _entities;
        private readonly bool _restricted;
        internal RowsEnumerator(QueryCursor query) { _query = query.Rows; _entities = default; _restricted = false; }
        internal RowsEnumerator(EntityQuery entities) { _query = default; _entities = entities.Rows; _restricted = true; }
        public RowsEnumerator GetEnumerator() => this;
        public QueryRow Current => _restricted ? _entities.Current : _query.Current;
        public bool MoveNext() => _restricted ? _entities.MoveNext() : _query.MoveNext();
    }
}

internal static class XpbdConstraintRows<TConstraint, TProjection>
    where TConstraint : struct, SomeEngine.ECS.IComponent
    where TProjection : struct, IXpbdConstraint<TConstraint>
{
    internal static void Initialize(scoped PhysicsQueryRows query, in PhysicsStepContext context)
    {
        foreach (QueryRow row in query.Rows)
            TProjection.Initialize(in row.ReadRef<TConstraint>(), row, in context);
    }

    internal static void Project(scoped PhysicsQueryRows query, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        foreach (QueryRow row in query.Rows)
            TProjection.Project(in row.ReadRef<TConstraint>(), row, bodies, in context);
    }
}
