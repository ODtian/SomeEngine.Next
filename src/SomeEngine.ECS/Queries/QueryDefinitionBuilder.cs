using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Queries;

public sealed class QueryDefinitionBuilder
{
    private readonly List<QueryTerm> _terms = new();

    public QueryDefinitionBuilder All<T>() where T : struct =>
        Add<T>(QueryTermKind.All, QueryAccess.None, QueryTermFilter.None);

    public QueryDefinitionBuilder None<T>() where T : struct =>
        Add<T>(QueryTermKind.None, QueryAccess.None, QueryTermFilter.None);

    public QueryDefinitionBuilder Any<T>() where T : struct =>
        Add<T>(QueryTermKind.Any, QueryAccess.None, QueryTermFilter.None);

    public QueryDefinitionBuilder Optional<T>(QueryAccess access = QueryAccess.None) where T : struct =>
        Add<T>(QueryTermKind.Optional, access, QueryTermFilter.None);

    public QueryDefinitionBuilder Read<T>() where T : struct =>
        Add<T>(QueryTermKind.All, QueryAccess.Read, QueryTermFilter.None);

    public QueryDefinitionBuilder Write<T>() where T : struct =>
        Add<T>(QueryTermKind.All, QueryAccess.Write, QueryTermFilter.None);

    public QueryDefinitionBuilder ReadWrite<T>() where T : struct =>
        Add<T>(QueryTermKind.All, QueryAccess.ReadWrite, QueryTermFilter.None);

    public QueryDefinitionBuilder Added<T>() where T : struct =>
        Add<T>(QueryTermKind.All, QueryAccess.None, QueryTermFilter.Added);

    public QueryDefinitionBuilder Changed<T>() where T : struct =>
        Add<T>(QueryTermKind.All, QueryAccess.None, QueryTermFilter.Changed);

    public QueryDefinitionBuilder ChunkChanged<T>() where T : struct =>
        Add<T>(QueryTermKind.All, QueryAccess.None, QueryTermFilter.ChunkChanged);

    public QueryDefinitionBuilder Removed<T>() where T : struct, IComponent =>
        Add<Removed<T>>(QueryTermKind.All, QueryAccess.Read, QueryTermFilter.None);

    public QueryDefinitionBuilder Enabled<T>() where T : struct, IEnableableComponent =>
        Add<T>(QueryTermKind.All, QueryAccess.None, QueryTermFilter.Enabled);

    public QueryDefinitionBuilder Disabled<T>() where T : struct, IEnableableComponent =>
        Add<T>(QueryTermKind.All, QueryAccess.None, QueryTermFilter.Disabled);

    public QueryDefinitionBuilder Shared<T>() where T : struct, ISharedComponent =>
        All<T>();

    public QueryDefinitionBuilder Buffer<T>(QueryAccess access = QueryAccess.None)
        where T : struct, IBufferElement
    {
        AddBacking<DynamicBufferHeader<T>>(QueryTermKind.All, access, QueryTermFilter.None);
        AddBacking<DynamicBufferInline<T>>(QueryTermKind.All, access, QueryTermFilter.None);
        return this;
    }

    public QueryDefinitionBuilder ReadBuffer<T>() where T : struct, IBufferElement =>
        Buffer<T>(QueryAccess.Read);

    public QueryDefinitionBuilder WriteBuffer<T>() where T : struct, IBufferElement =>
        Buffer<T>(QueryAccess.ReadWrite);

    public QueryDefinitionBuilder ChangedBuffer<T>() where T : struct, IBufferElement
    {
        Buffer<T>();
        AddBacking<DynamicBufferHeader<T>>(
            QueryTermKind.All,
            QueryAccess.None,
            QueryTermFilter.ChunkChanged);
        return this;
    }

    public QueryDefinition Build()
    {
        if (_terms.Count == 0)
            return QueryDefinition.Empty;

        var states = new Dictionary<(int componentId, QueryTermKind kind), TermState>();

        for (int i = 0; i < _terms.Count; i++)
        {
            var term = _terms[i];
            var key = (term.ComponentId, term.Kind);
            if (states.TryGetValue(key, out var existing))
            {
                existing.Access = QueryAccessExtensions.Merge(existing.Access, term.Access);
                existing.Filters |= term.Filters;
                states[key] = existing;
            }
            else
            {
                states.Add(key, new TermState(term.Access, term.Filters));
            }
        }

        ValidateConflicts(states);

        var normalized = new List<QueryTerm>(states.Count);
        foreach (var pair in states)
        {
            var term = new QueryTerm(pair.Key.componentId, pair.Key.kind, pair.Value.Access, pair.Value.Filters);
            QueryableTypeInfo.ForComponentId(term.ComponentId).Validate(term.Kind, term.Access, term.Filters);
            normalized.Add(term);
        }

        normalized.Sort(static (a, b) =>
        {
            int kind = a.Kind.CompareTo(b.Kind);
            if (kind != 0)
                return kind;

            int component = a.ComponentId.CompareTo(b.ComponentId);
            if (component != 0)
                return component;

            int access = a.Access.CompareTo(b.Access);
            return access != 0 ? access : a.Filters.CompareTo(b.Filters);
        });

        return new QueryDefinition(normalized.ToArray());
    }

    private QueryDefinitionBuilder Add<T>(
        QueryTermKind kind,
        QueryAccess access,
        QueryTermFilter filters)
        where T : struct
    {
        var info = QueryableTypeInfo.For<T>();
        info.Validate(kind, access, filters);
        _terms.Add(new QueryTerm(info.ComponentId, kind, access, filters));
        return this;
    }

    private void AddBacking<T>(
        QueryTermKind kind,
        QueryAccess access,
        QueryTermFilter filters)
        where T : struct
    {
        int componentId = ComponentMetadata<T>.Id;
        var info = QueryableTypeInfo.ForComponentId(componentId);
        info.Validate(kind, access, filters);
        _terms.Add(new QueryTerm(componentId, kind, access, filters));
    }

    private static void ValidateConflicts(
        Dictionary<(int componentId, QueryTermKind kind), TermState> states)
    {
        var componentKinds = new Dictionary<int, QueryTermKind>();
        foreach (var pair in states)
        {
            int componentId = pair.Key.componentId;
            QueryTermKind kind = pair.Key.kind;

            if (!componentKinds.TryGetValue(componentId, out var existing))
            {
                componentKinds.Add(componentId, kind);
                continue;
            }

            if ((existing == QueryTermKind.None && kind != QueryTermKind.None) ||
                (kind == QueryTermKind.None && existing != QueryTermKind.None))
            {
                throw new InvalidOperationException(
                    $"Component ID {componentId} cannot be both excluded and included in the same query.");
            }
        }

        foreach (var pair in states)
        {
            var filters = pair.Value.Filters;
            if ((filters & QueryTermFilter.Enabled) != 0 &&
                (filters & QueryTermFilter.Disabled) != 0)
            {
                throw new InvalidOperationException(
                    $"Component ID {pair.Key.componentId} cannot be both Enabled and Disabled in one query.");
            }
        }
    }

    private struct TermState
    {
        public TermState(QueryAccess access, QueryTermFilter filters)
        {
            Access = access;
            Filters = filters;
        }

        public QueryAccess Access;
        public QueryTermFilter Filters;
    }
}

