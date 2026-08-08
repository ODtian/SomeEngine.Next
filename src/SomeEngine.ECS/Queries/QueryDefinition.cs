using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Queries;

public sealed class QueryDefinition
{
    private readonly QueryTerm[] _terms;
    private readonly QueryAccessEntry[] _accesses;
    private readonly WorldJobStorageAccess[] _jobStorageAccesses;

    private QueryDefinition(QueryTerm[] ownedTerms)
    {
        ArgumentNullException.ThrowIfNull(ownedTerms);
        _terms = ownedTerms.Length == 0
            ? Array.Empty<QueryTerm>()
            : ownedTerms;
        Key = new QueryKey(_terms);

        var accesses = new List<QueryAccessEntry>();
        for (int i = 0; i < _terms.Length; i++)
        {
            QueryTerm term = _terms[i];
            if (term.Access != QueryAccess.None)
                accesses.Add(new QueryAccessEntry(term.ComponentId, term.Access, term.Kind));
        }

        _accesses = accesses.Count == 0
            ? Array.Empty<QueryAccessEntry>()
            : accesses.ToArray();
        (_jobStorageAccesses, HasRelationshipWrite, CanWrite) =
            CompileJobStorageAccesses(_terms);
    }

    public static QueryDefinition Empty { get; } = new(Array.Empty<QueryTerm>());

    public ReadOnlySpan<QueryTerm> Terms => _terms;

    public ReadOnlySpan<QueryAccessEntry> Accesses => _accesses;

    public QueryKey Key { get; }

    internal ReadOnlyMemory<WorldJobStorageAccess> JobStorageAccesses =>
        _jobStorageAccesses;

    internal bool HasRelationshipWrite { get; }

    internal bool CanWrite { get; }

    /// <summary>
    /// Normalizes query terms once for every construction path. Keeping merge, conflict, and
    /// ordering rules here prevents generated-filter composition from drifting away from the
    /// public builder.
    /// </summary>
    internal static QueryDefinition CreateNormalized(ReadOnlySpan<QueryTerm> terms) =>
        CreateNormalized(terms, ReadOnlySpan<QueryTerm>.Empty);

    internal static QueryDefinition Combine(
        QueryDefinition direct,
        QueryDefinition filter)
    {
        ArgumentNullException.ThrowIfNull(direct);
        ArgumentNullException.ThrowIfNull(filter);
        return CreateNormalized(direct.Terms, filter.Terms);
    }

    private static QueryDefinition CreateNormalized(
        ReadOnlySpan<QueryTerm> first,
        ReadOnlySpan<QueryTerm> second)
    {
        if (first.Length == 0 && second.Length == 0)
            return Empty;

        var states = new Dictionary<(int ComponentId, QueryTermKind Kind), TermState>();
        AddTerms(first, states);
        AddTerms(second, states);

        var componentKinds = new Dictionary<int, QueryTermKind>();
        foreach (var pair in states)
        {
            int componentId = pair.Key.ComponentId;
            QueryTermKind kind = pair.Key.Kind;
            if (componentKinds.TryGetValue(componentId, out QueryTermKind existing) &&
                ((existing == QueryTermKind.None && kind != QueryTermKind.None) ||
                 (kind == QueryTermKind.None && existing != QueryTermKind.None)))
            {
                throw new InvalidOperationException(
                    $"Component ID {componentId} cannot be both excluded and included in the same query.");
            }
            componentKinds[componentId] = kind;
        }

        var normalized = new List<QueryTerm>(states.Count);
        foreach (var pair in states)
        {
            QueryTermFilter filters = pair.Value.Filters;
            if ((filters & QueryTermFilter.Enabled) != 0 &&
                (filters & QueryTermFilter.Disabled) != 0)
            {
                throw new InvalidOperationException(
                    $"Component ID {pair.Key.ComponentId} cannot be both Enabled and Disabled in one query.");
            }

            var term = new QueryTerm(
                pair.Key.ComponentId,
                pair.Key.Kind,
                pair.Value.Access,
                filters);
            QueryableTypeInfo.ForComponentId(term.ComponentId)
                .Validate(term.Kind, term.Access, term.Filters);
            normalized.Add(term);
        }

        normalized.Sort(static (left, right) =>
        {
            int kind = left.Kind.CompareTo(right.Kind);
            if (kind != 0)
                return kind;
            int component = left.ComponentId.CompareTo(right.ComponentId);
            if (component != 0)
                return component;
            int access = left.Access.CompareTo(right.Access);
            return access != 0 ? access : left.Filters.CompareTo(right.Filters);
        });
        return new QueryDefinition(normalized.ToArray());
    }

    private static void AddTerms(
        ReadOnlySpan<QueryTerm> terms,
        Dictionary<(int ComponentId, QueryTermKind Kind), TermState> states)
    {
        for (int i = 0; i < terms.Length; i++)
        {
            QueryTerm term = terms[i];
            var key = (term.ComponentId, term.Kind);
            if (states.TryGetValue(key, out TermState state))
            {
                state.Access = QueryAccessExtensions.Merge(state.Access, term.Access);
                state.Filters |= term.Filters;
                states[key] = state;
            }
            else
            {
                states.Add(key, new TermState(term.Access, term.Filters));
            }
        }
    }

    private struct TermState
    {
        internal TermState(QueryAccess access, QueryTermFilter filters)
        {
            Access = access;
            Filters = filters;
        }

        internal QueryAccess Access;
        internal QueryTermFilter Filters;
    }

    private static (
        WorldJobStorageAccess[] accesses,
        bool relationshipWrite,
        bool canWrite) CompileJobStorageAccesses(QueryTerm[] terms)
    {
        if (terms.Length == 0)
            return (Array.Empty<WorldJobStorageAccess>(), false, false);

        var storage = new List<WorldJobStorageAccess>(terms.Length);
        bool relationshipWrite = false;
        bool canWrite = false;
        for (int i = 0; i < terms.Length; i++)
        {
            QueryTerm term = terms[i];
            bool readsFilterData = term.Filters != QueryTermFilter.None;
            if (term.Access == QueryAccess.None && !readsFilterData)
                continue;

            bool write = term.Access.CanWrite();
            canWrite |= write;

            ref readonly ComponentInfo info = ref ComponentRegistry.Get(term.ComponentId);
            relationshipWrite |= write &&
                (info.IsRelationshipSource || info.IsRelationshipTarget);

            WorldStorageKind kind;
            int componentId;
            if (BufferRegistry.TryGetHeaderComponentId(term.ComponentId, out int headerId))
            {
                kind = WorldStorageKind.Buffer;
                componentId = headerId;
            }
            else if (info.Storage == StoragePath.Table)
            {
                kind = WorldStorageKind.Table;
                componentId = term.ComponentId;
            }
            else
            {
                continue;
            }

            WorldStorageAccess access = write
                ? WorldStorageAccess.Write
                : WorldStorageAccess.Read;
            int existing = FindStorage(storage, kind, componentId);
            if (existing < 0)
            {
                storage.Add(new WorldJobStorageAccess(kind, componentId, access));
            }
            else if (access == WorldStorageAccess.Write &&
                     storage[existing].Access != WorldStorageAccess.Write)
            {
                storage[existing] = new WorldJobStorageAccess(kind, componentId, access);
            }
        }

        return (
            storage.Count == 0 ? Array.Empty<WorldJobStorageAccess>() : storage.ToArray(),
            relationshipWrite,
            canWrite);
    }

    private static int FindStorage(
        List<WorldJobStorageAccess> storage,
        WorldStorageKind kind,
        int componentId)
    {
        for (int i = 0; i < storage.Count; i++)
        {
            WorldJobStorageAccess access = storage[i];
            if (access.Kind == kind && access.ComponentId == componentId)
                return i;
        }

        return -1;
    }

}

